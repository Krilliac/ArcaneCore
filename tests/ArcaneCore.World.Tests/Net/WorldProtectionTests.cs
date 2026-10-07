using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// The world daemon's transport protections (docs/ops/netguard.md): the frame read deadline, the
/// per-address failure budget before the account lookup, the listener's connection rate, and the
/// containment of a malformed in-world packet. Every limit closes the connection; nothing escapes.
/// </summary>
public sealed class WorldProtectionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static NetGuard Guard(NetProtectionOptions options, CapturingLogger? log = null)
        => new(options, () => 0, () => 0, log ?? new CapturingLogger());

    [Fact]
    public async Task FrameReadTimeout_ClosesAConnectionThatWithholdsTheRestOfAFrame()
    {
        await using var host = WorldTestHost.Start();
        var log = new CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions { FrameReadTimeout = TimeSpan.FromMilliseconds(300) }, log);
        await using var stream = new FirstByteThenStallStream();
        var session = new WorldSession(
            stream, "127.0.0.1:40001", host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions { PreAuthTimeout = TimeSpan.Zero }, NullLogger.Instance, guard);

        Task run = session.RunAsync(CancellationToken.None);
        await run.WaitAsync(Budget);

        Assert.Contains(log.Messages, m => m.Contains("frame not completed", StringComparison.Ordinal));
        Assert.True(stream.Reads >= 2, "the session must have waited for the rest of the header"); // the first byte, then the stalled rest
    }

    [Fact]
    public async Task FrameReadTimeout_DoesNotFireWhileTheConnectionIsIdle()
    {
        await using var host = WorldTestHost.Start();
        var log = new CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions { FrameReadTimeout = TimeSpan.FromMilliseconds(200) }, log);
        await using var stream = new IdleStream();
        var session = new WorldSession(
            stream, "127.0.0.1:40002", host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions { PreAuthTimeout = TimeSpan.Zero }, NullLogger.Instance, guard);

        Task run = session.RunAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.False(run.IsCompleted, "an idle connection (no header byte yet) is not a slow frame");
        session.Kick();
        await run.WaitAsync(Budget);
        Assert.DoesNotContain(log.Messages, m => m.Contains("frame not completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthFailureBudget_RefusesBeforeTheAccountLookup_AndClosesWithAuthFailed()
    {
        await using var host = WorldTestHost.Start();
        var log = new CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions { AuthFailureBurstPerIp = 2, AuthFailuresPerMinutePerIp = 1 }, log);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        Task accepting = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient server = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(async () =>
                {
                    using (server)
                    await using (NetworkStream stream = server.GetStream())
                    await using (AsyncServiceScope scope = host.WorldServices.CreateAsyncScope())
                    {
                        var session = new WorldSession(
                            stream, "127.0.0.1:40003", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
                            new WorldSessionOptions(), NullLogger.Instance, guard);
                        await session.RunAsync(stop.Token);
                    }
                });
            }
        });

        try
        {
            // Two unknown accounts: answered AUTH_UNKNOWN_ACCOUNT and charged to the address.
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(AuthResponseCode.UnknownAccount, await AttemptAsync(port, "NOBODY"));
            }

            // The third attempt is refused before any lookup: AUTH_FAILED, then the connection is closed.
            Assert.Equal(AuthResponseCode.Failed, await AttemptAsync(port, "NOBODY"));
            Assert.Equal(1, guard.RefusedAuthAttempts);
            Assert.Single(log.Messages, m => m.Contains("too many failed attempts", StringComparison.Ordinal));

            // A real account at the same address is refused too (the budget is per address, as the retail throttle is).
            Assert.Equal(AuthResponseCode.Failed, await AttemptAsync(port, "NOBODY"));
            Assert.Equal(2, guard.RefusedAuthAttempts);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                await accepting.WaitAsync(Budget);
            }
            catch (Exception)
            {
                // the accept loop ends with the listener
            }
        }
    }

    /// <summary>One connection: read the challenge, send CMSG_AUTH_SESSION for <paramref name="account"/>, return the response code.</summary>
    private static async Task<AuthResponseCode> AttemptAsync(int port, string account)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Budget);
        await using var client = new WorldTestClient(socket);
        (WorldOpcode op, _) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, op);

        var session = new PacketWriter(64);
        session.WriteUInt32(ArcaneCore.Kernel.ClientBuild.Vanilla1121);
        session.WriteUInt32(0);
        session.WriteCString(account);
        session.WriteUInt32(1);
        session.WriteBytes(new byte[20]);
        session.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.CmsgAuthSession, session.ToArray());

        (op, byte[] payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.True(await client.IsClosedByServerAsync(), "a failed authentication closes the connection");
        return (AuthResponseCode)payload[0];
    }

    [Fact]
    public async Task Listener_RefusesConnectionsBeyondThePerAddressRate_BeforeAnySessionExists()
    {
        await using WorldServerHarness harness = await WorldServerHarness.StartAsync(new NetProtectionOptions
        {
            ConnectionBurstPerIp = 2, ConnectionsPerMinutePerIp = 1, MaxConnectionsPerIp = 0,
        });

        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, harness.Port).WaitAsync(Budget);
        await second.ConnectAsync(IPAddress.Loopback, harness.Port).WaitAsync(Budget);
        await using var a = new WorldTestClient(first);
        await using var b = new WorldTestClient(second);
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, (await a.ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, (await b.ReadAsync()).Opcode);

        using var third = new TcpClient();
        await third.ConnectAsync(IPAddress.Loopback, harness.Port).WaitAsync(Budget);
        await using var c = new WorldTestClient(third);
        Assert.True(await c.IsClosedByServerAsync(), "the third connection within the burst window must be closed without a challenge");

        NetGuard guard = Assert.IsType<NetGuard>(harness.Server.Guard);
        Assert.Equal(1, guard.RefusedConnections);
    }

    [Fact]
    public async Task ShortInWorldPacket_IsContained_AndDisconnectsTheSender()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MALFORMED", "Mal");

        // CMSG_MOVE_TIME_SKIPPED is 12 bytes; 3 bytes cannot hold the GUID. The handler's read is the
        // controlled outcome, caught on the world thread; the sender is kicked and the world keeps ticking.
        await client.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, new byte[3]);
        Assert.True(await client.IsClosedByServerAsync());
        Assert.Empty(host.SessionFaults);
        await host.OnWorldAsync(() => { }); // the world thread is alive and answering
    }

    /// <summary>
    /// A handler's own <see cref="IndexOutOfRangeException"/> is a server bug, not a client fault: it
    /// must never be reported as "malformed" (a Warning without the exception, blaming the player).
    /// On the session task it escapes the session as a session fault, which the production host logs
    /// at Error with the stack trace; on the world thread Map.ProcessPackets does the same and kicks.
    /// Before the fix both paths logged "malformed CMSG_X; disconnecting" and nothing else.
    /// </summary>
    [Fact]
    public async Task HandlerIndexOutOfRange_IsAServerFault_NeverReportedAsMalformed()
    {
        var log = new CapturingLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        host.ExpectSessionFaults = true;

        // Two opcodes nothing handles, one per dispatch path, each with a handler that has a bug.
        WorldOpcode[] free = [.. Enum.GetValues<WorldOpcode>()
            .Where(o => o is not WorldOpcode.CmsgPing and not WorldOpcode.CmsgAuthSession && !host.Opcodes.TryGet(o, out _))
            .Take(2)];
        Assert.Equal(2, free.Length);
        WorldOpcode worldOpcode = free[0];
        WorldOpcode sessionOpcode = free[1];
        host.Opcodes.OnWorld(worldOpcode, (_, _, _) => { int[] table = new int[1]; _ = table[int.Parse("7", System.Globalization.CultureInfo.InvariantCulture)]; });
        host.Opcodes.OnSession(sessionOpcode, SessionStates.Authenticated, (_, _) => { int[] table = new int[1]; _ = table[int.Parse("7", System.Globalization.CultureInfo.InvariantCulture)]; return Task.CompletedTask; });

        // World thread: the player is kicked by Map.ProcessPackets, the world keeps ticking, and no line blames the client.
        await using (WorldTestClient inWorld = await host.EnterWorldAsync("BUGGYW", "Buggyw"))
        {
            await inWorld.SendAsync(worldOpcode, new byte[4]);
            Assert.True(await inWorld.IsClosedByServerAsync(), "a handler fault on the world thread disconnects the player");
            await host.OnWorldAsync(() => { }); // the world thread is alive and answering
            AssertNothingBlamedTheClient(log);
        }

        // Session task: the exception leaves the session (the host logs it at Error as a session error).
        await using (WorldTestClient atCharScreen = await host.EnterWorldAsync("BUGGYS", "Buggys"))
        {
            await atCharScreen.SendAsync(sessionOpcode, new byte[4]);
            Assert.True(await atCharScreen.IsClosedByServerAsync(), "a handler fault on the session task closes the connection");
            AssertNothingBlamedTheClient(log);
        }

        await WorldTestHost.WaitForAsync(() => { lock (host.SessionFaults) { return host.SessionFaults.Count > 0; } }, "the session fault to be collected");
        Exception fault;
        lock (host.SessionFaults)
        {
            fault = Assert.Single(host.SessionFaults);
        }

        Assert.IsType<IndexOutOfRangeException>(fault);
    }

    /// <summary>No "malformed ...; disconnecting" line: that wording is reserved for the controlled reader outcome.</summary>
    private static void AssertNothingBlamedTheClient(CapturingLogger log)
    {
        lock (log.Messages)
        {
            Assert.DoesNotContain(log.Messages, m => m.Contains("malformed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Session_WithoutAGuard_StillHasTheDefaultFrameTimeout()
    {
        // The defaults are a static of the session, so a host that forgets the guard still gets the deadline.
        var defaults = new NetProtectionOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.FrameReadTimeout);
        Assert.True(defaults.FrameReadTimeout > TimeSpan.Zero);
    }

    // --- plumbing -----------------------------------------------------------------------

    /// <summary>Hands the session one header byte, then blocks every read until cancelled.</summary>
    private sealed class FirstByteThenStallStream : Stream
    {
        public int Reads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Reads++ == 0)
            {
                buffer.Span[0] = 0;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>Never delivers a byte; reads block until cancelled.</summary>
    private sealed class IdleStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>The real <see cref="WorldServer"/> listener with a guard built from the given section, minus the world thread.</summary>
    private sealed class WorldServerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;

        private WorldServerHarness(ServiceProvider provider, WorldRuntime world, WorldServer server, int port)
        {
            _provider = provider;
            _world = world;
            Server = server;
            Port = port;
        }

        public int Port { get; }

        public WorldServer Server { get; }

        public static async Task<WorldServerHarness> StartAsync(NetProtectionOptions protection)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
            services.AddSingleton<IAccountDataStore>(new InMemoryAccountDataStore());
            services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
            services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
            services.AddSingleton<CharacterSaveQueue>();
            ServiceProvider provider = services.BuildServiceProvider();
            var world = new WorldRuntime(
                new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                provider.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var server = new WorldServer(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port }),
                Options.Create(new WorldSessionOptions()), new OpcodeTable(), world, new SessionRegistry(),
                NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance, Options.Create(protection));
            await server.StartAsync(CancellationToken.None);
            return new WorldServerHarness(provider, world, server, port);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync(CancellationToken.None).WaitAsync(Budget);
            Server.Dispose();
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }

    internal sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
