using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Ops.Lifecycle;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>
/// <see cref="RealmLivenessService"/>: what the world writes into its realm-list row, and when (docs/ops/realm-liveness.md). The clock is
/// hand driven, so the 10 s beat and the 60 s threshold cost no real time. The logon daemon decides staleness from the heartbeat age
/// (RealmListProjectionTests in ArcaneCore.Realm.Tests); here "stale" is a heartbeat older than <see cref="StaleSeconds"/> at the clock's now.
/// </summary>
public sealed class RealmLivenessTests
{
    private const double StaleSeconds = 60;

    [Fact]
    public async Task Startup_GoesStartingThenOnline_OnlyAfterListenerAndFirstTick()
    {
        await using Rig rig = new(row => row.Flags = RealmFlags.NewPlayers | RealmFlags.Offline);

        await rig.Service.StartAsync(CancellationToken.None);
        await rig.Service.StartWrite;
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc);

        // Neither condition yet: the process beats, the realm stays offline.
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc); // a starting world beats on process liveness

        // The listener is bound (ApplicationStarted) but the world loop has not ticked.
        rig.Lifetime.Started.Cancel();
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        // The loop ticks: now both hold.
        rig.Ticks = 1;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.Equal(RealmFlags.NewPlayers, rig.Row.Flags);
        Assert.Equal(0f, rig.Row.Population);
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc);
    }

    [Fact]
    public async Task Startup_TicksBeforeTheListener_StayStarting()
    {
        await using Rig rig = new(row => row.Flags = RealmFlags.Offline);
        await rig.Service.StartAsync(CancellationToken.None);
        await rig.Service.StartWrite;

        rig.Ticks = 40; // the loop has been running a while, the listener is not bound yet
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        rig.Lifetime.Started.Cancel();
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task HungLoop_ProcessUp_GoesStaleThenOffline()
    {
        await using Rig rig = await Rig.OnlineAsync();
        DateTime lastBeat = rig.Row.HeartbeatUtc!.Value;

        // The process is up and the timer keeps firing, but the world loop stopped: no beat is written.
        for (int second = 1; second <= 70; second++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            await rig.WakeAsync();
        }

        Assert.Equal(lastBeat, rig.Row.HeartbeatUtc);
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State); // the world does not judge itself; the logon daemon reads the age
        Assert.True((rig.Clock.UtcNow - rig.Row.HeartbeatUtc!.Value).TotalSeconds > StaleSeconds);
        Assert.Contains(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("heartbeat", StringComparison.Ordinal) && l.Text.Contains("withheld", StringComparison.Ordinal));

        // One tick and the next pass beats again: the realm comes back by itself.
        rig.Ticks++;
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WakeAsync();
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc);
        Assert.True((rig.Clock.UtcNow - rig.Row.HeartbeatUtc!.Value).TotalSeconds < StaleSeconds);
    }

    [Fact]
    public async Task SlowTicks_StayOnline()
    {
        await using Rig rig = await Rig.OnlineAsync();
        double worstAge = 0;

        // A world that manages one tick every nine seconds is slow, not dead.
        for (int second = 1; second <= 300; second++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            if (second % 9 == 0)
            {
                rig.Ticks++;
            }

            await rig.WakeAsync();
            worstAge = Math.Max(worstAge, (rig.Clock.UtcNow - rig.Row.HeartbeatUtc!.Value).TotalSeconds);
        }

        Assert.True(worstAge < StaleSeconds / 2, $"worst heartbeat age {worstAge} s");
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
    }

    [Fact]
    public async Task BriefStall_UnderThreshold_StaysOnline()
    {
        await using Rig rig = await Rig.OnlineAsync();
        double worstAge = 0;

        for (int second = 1; second <= 200; second++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            if (second is <= 30 or > 70)
            {
                rig.Ticks++; // ticking, except for a 40 s stall in the middle
            }

            await rig.WakeAsync();
            worstAge = Math.Max(worstAge, (rig.Clock.UtcNow - rig.Row.HeartbeatUtc!.Value).TotalSeconds);
        }

        Assert.True(worstAge < StaleSeconds, $"worst heartbeat age {worstAge} s");
        Assert.True((rig.Clock.UtcNow - rig.Row.HeartbeatUtc!.Value).TotalSeconds <= 10);
    }

    [Fact]
    public async Task HeartbeatWrite_NeverRunsOnWorldThread()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a" });
        var queue = new CharacterSaveQueue(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0, TickIntervalMs = 5 }, queue, NullLogger<WorldRuntime>.Instance);
        var clock = new HandClock();
        var onWorldThread = new List<string>();
        var offWorldThread = new List<string>();
        store.OnCall = call => (world.IsMainWorldThread ? onWorldThread : offWorldThread).Add(call);

        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var service = new RealmLivenessService(services, NullLogger<RealmLivenessService>.Instance, new RealmStatusOptions { Id = 1 }, clock, () => world.TicksStarted);
        try
        {
            world.Start();
            await WaitAsync(() => world.TicksStarted > 0, "the world loop starts ticking");
            await service.StartAsync(CancellationToken.None);
            await service.StartWrite;
            service.MarkListenerBound();

            // The service's own timer drives the writer: advance the hand clock and let the pool thread do the work.
            for (int i = 0; i < 400 && !store.Calls.Contains("beat"); i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }

            Assert.Contains("online", store.Calls);
            Assert.True(store.Calls.Count(c => c == "beat") >= 1, "at least one heartbeat was written");
            Assert.Empty(onWorldThread);
            Assert.NotEmpty(offWorldThread);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            world.Stop();
        }
    }

    [Fact]
    public async Task RestartCountdown_SetsRestarting_CancelRestoresOnline()
    {
        await using Rig rig = await Rig.OnlineAsync();
        DateTime lastBeat = rig.Row.HeartbeatUtc!.Value;
        int writes = rig.Store.Writes;

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Lifecycle.Request(300, ShutdownMask.Restart, exitCode: 2);
        Assert.Equal(writes, rig.Store.Writes); // the world thread's call only records the state: no I/O there

        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Restarting, rig.Row.State);
        Assert.Equal(lastBeat, rig.Row.HeartbeatUtc); // a state change is not proof of life

        rig.Lifecycle.Cancel();
        Assert.Equal(RealmRuntimeState.Restarting, rig.Row.State);
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
    }

    [Fact]
    public async Task ShutdownCountdown_StaysOnline()
    {
        await using Rig rig = await Rig.OnlineAsync();

        rig.Lifecycle.Request(300, ShutdownMask.None, exitCode: 0);
        await rig.WakeAsync();

        // The row says ShuttingDown, which the logon daemon sends as the stored flags: normal, selectable.
        Assert.Equal(RealmRuntimeState.ShuttingDown, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Invalid));

        // A restart replaces a shutdown (and back) without passing through Online.
        rig.Lifecycle.Request(300, ShutdownMask.Restart, exitCode: 2);
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Restarting, rig.Row.State);

        rig.Lifecycle.Cancel();
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
    }

    [Fact]
    public async Task CleanStop_SetsStoppedAndOffline()
    {
        await using Rig rig = await Rig.OnlineAsync();
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        await rig.Service.StopAsync(CancellationToken.None);

        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
        Assert.Equal("stopped", rig.Store.Calls[^1]);

        // The writer is gone: neither a pass nor a countdown writes anything more, and a second stop is a no-op.
        int writes = rig.Store.Writes;
        rig.Ticks++;
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        await rig.WakeAsync();
        rig.Lifecycle.Request(60, ShutdownMask.Restart, exitCode: 2);
        await rig.WakeAsync();
        await rig.Service.StopAsync(CancellationToken.None);
        Assert.Equal(writes, rig.Store.Writes);
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
    }

    [Fact]
    public async Task FailedBeatWrite_IsLoggedAndLoopContinues()
    {
        await using Rig rig = await Rig.OnlineAsync();
        DateTime lastBeat = rig.Row.HeartbeatUtc!.Value;
        rig.Store.FailNext["beat"] = 1;

        rig.Ticks++;
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WakeAsync(); // does not throw
        Assert.Equal(lastBeat, rig.Row.HeartbeatUtc);
        Assert.Contains(rig.Log.Lines, l => l.Level >= LogLevel.Warning && l.Text.Contains("heartbeat", StringComparison.Ordinal) && l.Text.Contains("failed", StringComparison.Ordinal));

        // A failed write is retried on the next wake (one second), not after a full interval.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WakeAsync();
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc);

        // The world kept ticking; the next interval's write goes through.
        rig.Ticks++;
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await rig.WakeAsync();
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc);
    }

    [Fact]
    public async Task FailedStartingWrite_RetriesOnTheNextWake_ThenGoesOnline()
    {
        var rig = new Rig(row => row.Flags = RealmFlags.Offline);
        try
        {
            rig.Store.FailNext["starting"] = 1;
            await rig.Service.StartAsync(CancellationToken.None);
            await rig.Service.StartWrite; // the queued first pass fails and does not throw
            Assert.Equal(RealmRuntimeState.Unknown, rig.Row.State);
            Assert.Contains(rig.Log.Lines, l => l.Level >= LogLevel.Warning && l.Text.Contains("starting", StringComparison.Ordinal) && l.Text.Contains("failed", StringComparison.Ordinal));

            // Listener and first tick are already there, but Online is not written before Starting has landed.
            rig.Ticks = 1;
            rig.Lifetime.Started.Cancel();
            await rig.WakeAsync();
            Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
            Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

            await rig.WakeAsync();
            Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
            Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
            Assert.Equal(["starting", "starting", "online"], rig.Store.Calls);
        }
        finally
        {
            await rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stop_WhileTheStartingWriteIsInFlight_WaitsForItAndLeavesTheStoppedRow()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.NewPlayers });
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        store.OnCall = call =>
        {
            if (call == "starting")
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var service = new RealmLivenessService(services, NullLogger<RealmLivenessService>.Instance, new RealmStatusOptions { Id = 1 }, new HandClock(), () => 1)
        {
            StartLoop = false,
        };

        await service.StartAsync(CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the Starting write reached the store");

        Task stop = service.StopAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(stop.IsCompleted, "stop does not finish while the Starting write is still in the store");

        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // The Starting write landed first, then the stopped write: the row is not left at Starting or Online.
        Assert.True(service.StartWrite.IsCompleted, "no write task outlives StopAsync");
        Assert.Equal(["starting", "stopped"], store.Calls);
        Assert.Equal(RealmRuntimeState.Stopped, store.Row.State);
        Assert.True(store.Row.Flags.HasFlag(RealmFlags.Offline));
        service.Dispose();
    }

    [Fact]
    public async Task IdZero_WritesNothing()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.Offline });
        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var service = new RealmLivenessService(services, NullLogger<RealmLivenessService>.Instance, new RealmStatusOptions { Id = 0 }, new HandClock(), () => 1);

        await service.StartAsync(CancellationToken.None);
        await service.StartWrite;
        await service.StopAsync(CancellationToken.None);

        Assert.False(service.Enabled);
        Assert.Equal(0, store.Writes);
        Assert.Equal(RealmFlags.Offline, store.Row.Flags);
    }

    [Fact]
    public async Task StartAsync_DoesNotWaitForTheAuthDatabase()
    {
        // An unreachable auth database must not hold the world back by the connection timeout: the Starting write is queued to the writer.
        var store = new RealmRowStore(new RealmEntry { Id = 1, Name = "R", Address = "a", Flags = RealmFlags.NewPlayers });
        using var release = new ManualResetEventSlim(false);
        store.OnCall = _ => release.Wait(TimeSpan.FromSeconds(30));
        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var service = new RealmLivenessService(services, NullLogger<RealmLivenessService>.Instance, new RealmStatusOptions { Id = 1 }, new HandClock(), () => 1)
        {
            StartLoop = false,
        };

        // The write is blocked inside the store; StartAsync must still return (the bound only matters on failure).
        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.Enabled);
        Assert.False(service.StartWrite.IsCompleted);
        Assert.Equal(RealmRuntimeState.Unknown, store.Row.State);

        release.Set();
        await service.StartWrite;
        Assert.Equal(RealmRuntimeState.Starting, store.Row.State);
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
    }

    [Fact]
    public async Task MissingRealmRow_WarnsOnce_AndDoesNotFail()
    {
        var store = new RealmRowStore(new RealmEntry { Id = 9, Name = "Other", Address = "a" });
        var log = new LineLogger();
        var clock = new HandClock();
        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var service = new RealmLivenessService(services, log, new RealmStatusOptions { Id = 1 }, clock, () => 1);

        await service.StartAsync(CancellationToken.None);
        await service.StartWrite;
        service.MarkListenerBound();
        await service.WakeAsync(CancellationToken.None);
        await service.WakeAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Single(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("has no row", StringComparison.Ordinal));
        Assert.Equal(RealmFlags.None, store.Row.Flags); // another realm's row is never touched
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Started { get; } = new();

        public CancellationToken ApplicationStarted => Started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    /// <summary>A service, its row, a hand clock and a settable tick counter.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private long _ticks;

        public Rig(Action<RealmEntry>? seed = null)
        {
            Row = new RealmEntry { Id = 1, Name = "Realm", Address = "127.0.0.1:8085", Population = 0.5f };
            seed?.Invoke(Row);
            Store = new RealmRowStore(Row);
            Lifetime = new Lifetime();
            _services = new ServiceCollection()
                .AddSingleton<IRealmStatusStore>(Store)
                .AddSingleton<IHostApplicationLifetime>(Lifetime)
                .AddSingleton(sp => new ServerLifecycleFeature(sp, NullLogger<ServerLifecycleFeature>.Instance))
                .BuildServiceProvider();
            Lifecycle = _services.GetRequiredService<ServerLifecycleFeature>();
            Service = new RealmLivenessService(_services, Log, new RealmStatusOptions { Id = 1 }, Clock, () => Volatile.Read(ref _ticks)) { StartLoop = false };
        }

        public RealmEntry Row { get; }

        public RealmRowStore Store { get; }

        public HandClock Clock { get; } = new();

        public LineLogger Log { get; } = new();

        public Lifetime Lifetime { get; }

        public ServerLifecycleFeature Lifecycle { get; }

        public RealmLivenessService Service { get; }

        public long Ticks
        {
            get => Volatile.Read(ref _ticks);
            set => Volatile.Write(ref _ticks, value);
        }

        public Task WakeAsync() => Service.WakeAsync(CancellationToken.None);

        /// <summary>Started, listener bound, first tick seen: Online with the first beat at the clock's now.</summary>
        public static async Task<Rig> OnlineAsync()
        {
            var rig = new Rig(row => row.Flags = RealmFlags.NewPlayers | RealmFlags.Offline);
            await rig.Service.StartAsync(CancellationToken.None);
            await rig.Service.StartWrite;
            rig.Ticks = 1;
            rig.Lifetime.Started.Cancel();
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            await rig.WakeAsync();
            Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            await _services.DisposeAsync();
        }
    }
}
