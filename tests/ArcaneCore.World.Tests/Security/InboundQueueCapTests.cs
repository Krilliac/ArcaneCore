using System.Buffers.Binary;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Codex finding 1 (net-auth): the per-session queue of world-handler packets was unbounded. An
/// authenticated client could flood valid 12-byte CMSG_MOVE_TIME_SKIPPED frames faster than the
/// world thread drains them (MaxWorldPacketsPerTick per tick) and grow the retained arrays without
/// limit. vmangos (WorldSession.cpp:307-332) and mangos-classic (WorldSession.cpp:256-285) have no
/// bound either, so the cap is hardening whose defaults sit far above any retail client.
///
/// The world thread is parked on a gate so nothing drains; that makes the queue depth a function of
/// what the client sent, not of scheduling, so none of these tests depends on a wall-clock window.
/// </summary>
public sealed class InboundQueueCapTests
{
    private static async Task<(WorldTestClient Client, WorldSession Session)> EnterAsync(WorldTestHost host, string name)
    {
        WorldTestClient client = await host.EnterWorldAsync(name, name.ToLowerInvariant() + "x");
        Account account = (await host.Accounts.FindByUsernameAsync(name))!;
        WorldSession session = host.Registry.Find(account.Id) ?? throw new InvalidOperationException("session not registered");
        Assert.Equal(SessionState.InWorld, session.State);
        return (client, session);
    }

    /// <summary>CMSG_MOVE_TIME_SKIPPED: own GUID (u64) and the skipped milliseconds (u32) = 12 bytes.</summary>
    private static byte[] TimeSkipped(ulong guid, uint lag = 1)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(body, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), lag);
        return body;
    }

    private static async Task FloodAsync(WorldTestClient client, int count, uint lag = 1)
    {
        byte[] body = TimeSkipped(0, lag);
        try
        {
            for (int i = 0; i < count; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgMoveTimeSkipped, body);
            }
        }
        catch (IOException)
        {
            // the server already cut the connection
        }
    }

    [Fact]
    public async Task Flood_BeyondThePacketBound_DisconnectsAndTheQueueNeverExceedsTheBound()
    {
        const int limit = 50;
        var log = new CapturingLogger();
        await using var host = WorldTestHost.Start(
            sessionOptions: new WorldSessionOptions { MaxQueuedWorldPackets = limit, MaxQueuedWorldBytes = 0 },
            sessionLogger: log);
        (WorldTestClient client, WorldSession session) = await EnterAsync(host, "Flooder");
        await using (client)
        {
            using var gate = new WorldThreadGate(host);
            await gate.EngageAsync();

            int highWater = 0;
            using var stopWatching = new CancellationTokenSource();
            Task watcher = Task.Run(async () =>
            {
                while (!stopWatching.IsCancellationRequested)
                {
                    highWater = Math.Max(highWater, session.QueuedWorldPackets);
                    await Task.Yield();
                }
            });

            await FloodAsync(client, 2000, lag: 0xDEADBEEF);

            Assert.True(await client.IsClosedByServerAsync(), "a flood past the bound must disconnect the client");
            await stopWatching.CancelAsync();
            await watcher;

            Assert.True(highWater <= limit, $"queue reached {highWater} packets, bound is {limit}");
            Assert.Equal(SessionState.Closed, session.State);
            await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 0 && session.QueuedWorldBytes == 0,
                "a disconnected session retains no queued packets");
        }

        string warning = Assert.Single(log.Messages, m => m.Contains("queue", StringComparison.OrdinalIgnoreCase)
            && m.Contains("disconnecting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("DEADBEEF", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(0xDEADBEEF.ToString(), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flood_BeyondTheByteBound_Disconnects()
    {
        const int limitBytes = 200; // 16 packets of 12 bytes fit, the 17th does not
        await using var host = WorldTestHost.Start(
            sessionOptions: new WorldSessionOptions { MaxQueuedWorldPackets = 0, MaxQueuedWorldBytes = limitBytes });
        (WorldTestClient client, WorldSession session) = await EnterAsync(host, "Heavy");
        await using (client)
        {
            using var gate = new WorldThreadGate(host);
            await gate.EngageAsync();

            long highWater = 0;
            using var stopWatching = new CancellationTokenSource();
            Task watcher = Task.Run(async () =>
            {
                while (!stopWatching.IsCancellationRequested)
                {
                    highWater = Math.Max(highWater, session.QueuedWorldBytes);
                    await Task.Yield();
                }
            });

            await FloodAsync(client, 500);

            Assert.True(await client.IsClosedByServerAsync());
            await stopWatching.CancelAsync();
            await watcher;
            Assert.True(highWater <= limitBytes, $"queue retained {highWater} bytes, bound is {limitBytes}");
            await WorldTestHost.WaitForAsync(() => session.QueuedWorldBytes == 0, "bytes return to zero");
        }
    }

    [Fact]
    public async Task TrafficUnderTheBound_IsKeptAndAccountingReturnsToZeroAfterTheDrain()
    {
        await using var host = WorldTestHost.Start(
            sessionOptions: new WorldSessionOptions { MaxQueuedWorldPackets = 100, MaxQueuedWorldBytes = 100 * 12 });
        (WorldTestClient client, WorldSession session) = await EnterAsync(host, "Polite");
        await using (client)
        {
            using var gate = new WorldThreadGate(host);
            await gate.EngageAsync();

            // exactly at the bound: all 100 are queued, none is dropped, the client stays connected
            await FloodAsync(client, 100);
            await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 100, "100 packets queued");
            Assert.Equal(100 * 12, session.QueuedWorldBytes);
            Assert.NotEqual(SessionState.Closed, session.State);

            gate.Release();

            await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 0 && session.QueuedWorldBytes == 0,
                "the world thread drains the queue and the accounting returns to zero");
            Assert.Equal(SessionState.InWorld, session.State);

            // the connection is still usable
            await client.SendAsync(WorldOpcode.CmsgPing, [1, 0, 0, 0, 0, 0, 0, 0]);
            Assert.Equal(4, (await client.ReadUntilAsync(WorldOpcode.SmsgPong)).Length);
        }
    }

    [Fact]
    public async Task DefaultBounds_DoNotTouchAHeavyButLegitimateBurst()
    {
        await using var host = WorldTestHost.Start(); // default options
        (WorldTestClient client, WorldSession session) = await EnterAsync(host, "Burst");
        await using (client)
        {
            using var gate = new WorldThreadGate(host);
            await gate.EngageAsync();

            // 3000 packets queued while the world thread is stalled: 20x what a retail client sends
            // in a second, well under the default bound.
            await FloodAsync(client, 3000);
            await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 3000, "3000 packets queued");
            Assert.NotEqual(SessionState.Closed, session.State);

            gate.Release();
            await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 0 && session.QueuedWorldBytes == 0,
                "drained after the stall");
            Assert.Equal(SessionState.InWorld, session.State);
        }
    }

    [Fact]
    public async Task ClientDisconnect_ReleasesQueuedPackets()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, WorldSession session) = await EnterAsync(host, "Quitter");
        using var gate = new WorldThreadGate(host);
        await gate.EngageAsync();

        await FloodAsync(client, 40);
        await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 40, "40 packets queued");
        await client.DisposeAsync();

        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "session closed");
        await WorldTestHost.WaitForAsync(() => session.QueuedWorldPackets == 0 && session.QueuedWorldBytes == 0,
            "a closed session retains no queued packets");
    }

    [Fact]
    public void Defaults_AreGenerousAndBindFromTheWorldSection()
    {
        var defaults = new WorldSessionOptions();
        Assert.Equal(8192, defaults.MaxQueuedWorldPackets);
        Assert.Equal(8L * 1024 * 1024, defaults.MaxQueuedWorldBytes);
        // far above what the world thread leaves queued even when it falls many ticks behind
        Assert.True(defaults.MaxQueuedWorldPackets >= 50 * defaults.MaxWorldPacketsPerTick);

        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:MaxQueuedWorldPackets"] = "123",
            ["World:MaxQueuedWorldBytes"] = "4567",
        }).Build();
        var bound = new WorldSessionOptions();
        cfg.GetSection(WorldOptions.SectionName).Bind(bound);
        Assert.Equal(123, bound.MaxQueuedWorldPackets);
        Assert.Equal(4567, bound.MaxQueuedWorldBytes);
    }

    /// <summary>Parks the world thread so queued packets cannot drain until released.</summary>
    private sealed class WorldThreadGate(WorldTestHost host) : IDisposable
    {
        private readonly ManualResetEventSlim _open = new(false);
        private Task? _blocked;

        public async Task EngageAsync()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blocked = host.World.InvokeAsync(() =>
            {
                entered.SetResult();
                _open.Wait();
                return true;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Release() => _open.Set();

        public void Dispose()
        {
            _open.Set();
            _blocked?.Wait(TimeSpan.FromSeconds(10));
            _open.Dispose();
        }
    }

    private sealed class CapturingLogger : ILogger
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
