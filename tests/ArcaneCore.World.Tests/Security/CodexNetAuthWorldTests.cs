using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Independent cross-verification of the world-side half of the Codex net/auth findings, using the
/// exact attack sequences from the report.
///
/// Finding 2: "On port 8085, connect and withhold the six-byte packet header or finish one byte very
/// slowly." The world connection must be cut off by a deadline and the admission caps must hold.
///
/// Deadlines are injected as options small enough to keep the tests fast; every assertion budget is
/// at least 10x the configured deadline so load cannot make them flake.
/// </summary>
public sealed class CodexNetAuthWorldTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // --- finding 2: pre-auth read deadline ------------------------------------------------

    [Fact]
    public async Task WithheldPacketHeader_IsCutOffByThePreAuthDeadline()
    {
        await using var host = WorldTestHost.Start(sessionOptions: new WorldSessionOptions { PreAuthTimeout = Deadline });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream stream = tcp.GetStream();

        // The server speaks first (SMSG_AUTH_CHALLENGE); the attacker then sends nothing at all.
        Assert.True(await DrainUntilClosedAsync(stream, Budget), "a connection that never sends its header must be closed");
    }

    [Fact]
    public async Task OneByteOfTheHeader_ThenSilence_IsCutOffByThePreAuthDeadline()
    {
        await using var host = WorldTestHost.Start(sessionOptions: new WorldSessionOptions { PreAuthTimeout = Deadline });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync(new byte[] { 0x00 }); // "finish one byte very slowly"
        Assert.True(await DrainUntilClosedAsync(stream, Budget), "a trickled header must not keep the connection open");
    }

    [Fact]
    public async Task AuthenticatedSession_IsNotKilledByThePreAuthDeadline()
    {
        // 4 s deadline versus an authentication that takes milliseconds; the session then idles past it.
        await using var host = WorldTestHost.Start(sessionOptions: new WorldSessionOptions { PreAuthTimeout = TimeSpan.FromSeconds(4) });
        byte[] key = await host.AddAccountAsync("PATIENT");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PATIENT", key);

        await Task.Delay(TimeSpan.FromSeconds(5));

        await client.SendAsync(WorldOpcode.CmsgPing, [7, 0, 0, 0, 0, 0, 0, 0]);
        byte[] pong = await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(pong));
    }

    [Fact]
    public void PreAuthTimeout_DefaultsToTheRetailTenSecondsAndBindsFromConfiguration()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), new WorldSessionOptions().PreAuthTimeout);

        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:PreAuthTimeout"] = "00:00:03",
        }).Build();
        var bound = new WorldSessionOptions();
        cfg.GetSection(WorldOptions.SectionName).Bind(bound);
        Assert.Equal(TimeSpan.FromSeconds(3), bound.PreAuthTimeout);
    }

    // --- finding 2: admission caps through the real listener ---------------------------------

    [Fact]
    public async Task StalledConnections_CannotExceedTheCap_AndTheirSlotsComeBack()
    {
        await using var harness = await WorldServerHarness.StartAsync(maxConnections: 2, new WorldSessionOptions { PreAuthTimeout = Deadline });

        // Two attackers that connect and withhold the header occupy every slot.
        using var a = new TcpClient();
        using var b = new TcpClient();
        await a.ConnectAsync(IPAddress.Loopback, harness.Port);
        await b.ConnectAsync(IPAddress.Loopback, harness.Port);
        Assert.True(await ReceivesDataAsync(a.GetStream()), "the first connection is admitted (receives SMSG_AUTH_CHALLENGE)");
        Assert.True(await ReceivesDataAsync(b.GetStream()), "the second connection is admitted");

        // A third is refused before a session or DI scope exists: it never gets a challenge.
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, harness.Port);
        Assert.False(await ReceivesDataAsync(c.GetStream()), "the cap must refuse the third connection");

        // The pre-auth deadline cuts the stalled connections off, which frees their slots.
        Assert.True(await DrainUntilClosedAsync(a.GetStream(), Budget));
        Assert.True(await DrainUntilClosedAsync(b.GetStream(), Budget));

        DateTime until = DateTime.UtcNow + Budget;
        bool admitted = false;
        while (!admitted && DateTime.UtcNow < until)
        {
            using var d = new TcpClient();
            await d.ConnectAsync(IPAddress.Loopback, harness.Port);
            admitted = await ReceivesDataAsync(d.GetStream());
            if (!admitted)
            {
                await Task.Delay(50); // the lease is released just after the socket closes
            }
        }

        Assert.True(admitted, "slots must be returned once the stalled connections are cut off");
    }

    // --- plumbing ----------------------------------------------------------------------------

    /// <summary>Send CMSG_AUTH_SESSION for <paramref name="account"/> and return the SMSG_AUTH_RESPONSE code.</summary>
    internal static async Task<byte> SendAuthSessionAsync(NetworkStream client, string account, byte[] key)
    {
        byte[] seedPacket = await ReadFrameAsync(client);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedPacket.AsSpan(2));
        uint clientSeed = 0xBEEF;
        byte[] digest = Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], Le(clientSeed), Le(serverSeed), key);

        var w = new PacketWriter(64);
        w.WriteUInt32(ClientBuild.Vanilla1121);
        w.WriteUInt32(0);
        w.WriteCString(account);
        w.WriteUInt32(clientSeed);
        w.WriteBytes(digest);
        w.WriteUInt32(0); // empty addon block
        byte[] payload = w.AsMemory().ToArray();
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await client.WriteAsync(frame);

        byte[] reply = await ReadFrameAsync(client);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(reply));
        return reply[2];
    }

    /// <summary>One plain (not yet header-encrypted) server frame: opcode (u16) then payload.</summary>
    internal static async Task<byte[]> ReadFrameAsync(NetworkStream s)
    {
        using var cts = new CancellationTokenSource(Budget);
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, cts.Token);
        int len = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
        if (len < 0 || len > 256)
        {
            // A plain header never looks like this: the server switched to header encryption, which
            // it only does once the world authentication has succeeded.
            throw new Xunit.Sdk.XunitException("the server accepted the authentication (reply header is encrypted)");
        }

        byte[] body = new byte[2 + len];
        header.AsSpan(2, 2).CopyTo(body);
        await s.ReadExactlyAsync(body.AsMemory(2), cts.Token);
        return body;
    }

    /// <summary>Read and discard until the server closes the connection; false if it never does within the budget.</summary>
    internal static async Task<bool> DrainUntilClosedAsync(NetworkStream stream, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        byte[] buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }

            return true;
        }
        catch (IOException)
        {
            return true; // reset counts as closed
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True if at least one byte arrives (an admitted session sends its auth challenge at once); false on close.</summary>
    internal static async Task<bool> ReceivesDataAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Budget);
        byte[] one = new byte[1];
        try
        {
            return await stream.ReadAsync(one, cts.Token) > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static byte[] Le(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    /// <summary>The real <see cref="WorldServer"/> listener with a connection cap, minus the world thread.</summary>
    private sealed class WorldServerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;
        private readonly WorldServer _server;

        private WorldServerHarness(ServiceProvider provider, WorldRuntime world, WorldServer server, int port)
        {
            _provider = provider;
            _world = world;
            _server = server;
            Port = port;
        }

        public int Port { get; }

        public static async Task<WorldServerHarness> StartAsync(int maxConnections, WorldSessionOptions sessionOptions)
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
                Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port, MaxConnections = maxConnections }),
                Options.Create(sessionOptions), new OpcodeTable(), world, new SessionRegistry(),
                NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance);
            await server.StartAsync(CancellationToken.None);
            return new WorldServerHarness(provider, world, server, port);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.StopAsync(CancellationToken.None).WaitAsync(Budget);
            _server.Dispose();
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
