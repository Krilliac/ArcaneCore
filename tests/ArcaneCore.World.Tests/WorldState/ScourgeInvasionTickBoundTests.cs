using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The live 2026-10-11 TickHang ("phase=WorldTick, running=ScourgeInvasionFeature for 2151 ms"; slowest bodies 3.9-13.3 s,
/// with no Scourge event running) was the five-second refresh blocking the world thread on the realm store. These tests pin
/// that no store call runs on the world thread, at most one store job is in flight, and a slow store delays only the refresh.
/// </summary>
public sealed class ScourgeInvasionTickBoundTests(ITestOutputHelper output)
{
    /// <summary>A realm store whose every call takes <see cref="Latency"/> and records the calling thread.</summary>
    private sealed class SlowStore : IScourgeInvasionStateStore
    {
        public ScourgeInvasionSnapshot State { get; set; } = ScourgeInvasionSnapshot.Disabled;
        public TimeSpan Latency { get; set; }
        public ManualResetEventSlim Gate { get; } = new(true);
        public int WorldThreadId { get; set; } = -1;
        public int CallsOnWorldThread;
        public int Calls;
        public int InFlight;
        public int MaxInFlight;

        private async Task<T> Call<T>(Func<T> body)
        {
            Interlocked.Increment(ref Calls);
            if (Environment.CurrentManagedThreadId == WorldThreadId) Interlocked.Increment(ref CallsOnWorldThread);
            int now = Interlocked.Increment(ref InFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxInFlight)) && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            try
            {
                Gate.Wait();
                if (Latency > TimeSpan.Zero) Thread.Sleep(Latency); // a contended SQLite busy wait blocks its thread
                await Task.Yield();
                return body();
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        }

        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Call(() => State);
        public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Call(() =>
        {
            State = new ScourgeInvasionSnapshot(ScourgeInvasionState.Enabled, 0, 0,
                ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, z.Necropolises, 0)).ToArray());
            return true;
        });
        public Task StopAsync(CancellationToken cancellationToken = default) => Call(() => State = ScourgeInvasionSnapshot.Disabled);
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default) => Call(() => false);
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default) => Call(() => false);
    }

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(17, 0, 525600, 1, 0, 0, "Scourge Invasion"),
                 .. Enumerable.Range(90, 10).Select(id => new GameEventRecord((uint)id, 0, 525600, 1, 0, 0, $"Scourge {id}"))],
                [], [], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig(SlowStore store, bool inline)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddScoped<IScourgeInvasionStateStore>(sp => sp.GetRequiredService<SlowStore>());
            services.AddScoped<IGameEventDataStore, Events>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new ScourgeInvasionFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<ScourgeInvasionFeature>.Instance));
            _provider = services.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Events = _provider.GetRequiredService<GameEventFeature>();
            Events.Attach(World);
            Invasion = _provider.GetRequiredService<ScourgeInvasionFeature>();
            // inline = the pre-fix behaviour: every store call on the world thread inside the tick.
            if (inline) Invasion.StoreOffload = work => work();
            Invasion.Attach(World);
            store.WorldThreadId = Environment.CurrentManagedThreadId;
        }

        public WorldRuntime World { get; }
        public GameEventFeature Events { get; }
        public ScourgeInvasionFeature Invasion { get; }

        /// <summary>Runs one tick and returns its wall time in milliseconds.</summary>
        public double Tick(uint diffMs)
        {
            long start = Stopwatch.GetTimestamp();
            World.RunTick(diffMs);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        /// <summary>Ticks (50 ms of game time each) until <paramref name="done"/> holds, up to a wall-clock limit.</summary>
        public void TickUntil(Func<bool> done, TimeSpan limit)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(clock.Elapsed < limit, "the store job never completed");
                World.RunTick(50);
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }
    }

    [Fact]
    public void DisabledInvasionRefreshNeverCallsTheStoreOnTheWorldThread()
    {
        var store = new SlowStore();
        using var rig = new Rig(store, inline: false);
        int attachCalls = store.Calls; // Attach's one startup load
        for (int i = 0; i < 40; i++) rig.Tick(5_000); // 40 refresh periods
        rig.TickUntil(() => rig.Invasion.StoreWork is { Busy: false }, TimeSpan.FromSeconds(10));

        Assert.Equal(1, attachCalls); // startup is the only synchronous load (before the world loop runs)
        Assert.Equal(0, store.CallsOnWorldThread);
        Assert.True(store.Calls > attachCalls, "the refresh must still poll the store");
        Assert.Equal(1, store.MaxInFlight);
    }

    [Fact]
    public void SlowStoreDelaysOnlyTheRefreshAndOneJobRunsAtATime()
    {
        var store = new SlowStore();
        using var rig = new Rig(store, inline: false);
        int worldThreadCallsAtStart = store.CallsOnWorldThread;
        store.Gate.Reset(); // the store is now stuck (a held SQLite write lock, a slow disk)

        double worst = 0;
        for (int i = 0; i < 20; i++) worst = Math.Max(worst, rig.Tick(5_000));
        Assert.True(worst < 250, $"a world tick waited {worst:F0} ms on a blocked store");
        Assert.Equal(1L, rig.Invasion.StoreWork!.Started); // later refreshes wait for the stuck job, they do not pile up
        Assert.True(rig.Invasion.StoreWork.Busy);

        Assert.True(rig.Events.Service!.StartEvent(17)); // queued behind the stuck job, not run on the world thread
        Assert.Equal(ScourgeInvasionState.Disabled, rig.Invasion.Snapshot.State);

        store.Gate.Set();
        rig.TickUntil(() => rig.Invasion.Snapshot.State == ScourgeInvasionState.Enabled, TimeSpan.FromSeconds(10));
        Assert.Equal(ScourgeInvasionState.Enabled, store.State.State);
        Assert.Equal(worldThreadCallsAtStart, store.CallsOnWorldThread);
        Assert.Equal(1, store.MaxInFlight);
        rig.Tick(5_000);
        rig.TickUntil(() => !rig.Invasion.StoreWork.Busy, TimeSpan.FromSeconds(10));
        Assert.All(ScourgeInvasionCatalog.Zones, zone => Assert.True(rig.Events.IsActiveEvent(zone.EventId)));
    }

    [Fact]
    public async Task StopAsyncWritesWhatTheWorldThreadStillHadQueued()
    {
        var store = new SlowStore();
        using var rig = new Rig(store, inline: false);
        store.Gate.Reset();
        rig.Tick(5_000); // a refresh job, stuck in the store
        Assert.True(rig.Events.Service!.StartEvent(17)); // queued behind it
        rig.World.Stop(); // the world loop is done: no more applies
        store.Gate.Set();
        await rig.Invasion.StopAsync();
        Assert.Equal(ScourgeInvasionState.Enabled, store.State.State);
    }

    /// <summary>
    /// Reproducible before/after: a store with 200 ms per call (a contended SQLite file) and a disabled invasion, 30 refresh
    /// periods. "inline" runs every store call inside the tick as the feature did before; the default runs them off the world
    /// thread. Reports the slowest tick of each.
    /// </summary>
    [Fact]
    public void MeasureTickCostWithASlowStore()
    {
        double Measure(bool inline)
        {
            var store = new SlowStore();
            using var rig = new Rig(store, inline);
            store.Latency = TimeSpan.FromMilliseconds(200);
            double worst = 0;
            for (int i = 0; i < 30; i++)
            {
                worst = Math.Max(worst, rig.Tick(5_000));
                Thread.Sleep(10);
            }
            rig.TickUntil(() => rig.Invasion.StoreWork is { Busy: false }, TimeSpan.FromSeconds(30));
            return worst;
        }

        double before = Measure(inline: true);
        double after = Measure(inline: false);
        output.WriteLine($"slowest tick with a 200 ms store: on-world-thread {before:F1} ms, off-world-thread {after:F1} ms");
        Assert.True(before >= 200, $"the inline baseline should pay the store latency, took {before:F1} ms");
        // The off-thread number is reported, not asserted: on a loaded machine (the full suite in parallel) a tick can be
        // descheduled for longer than the store call. The bound itself is DisabledInvasionRefreshNeverCallsTheStoreOnTheWorldThread.
    }
}
