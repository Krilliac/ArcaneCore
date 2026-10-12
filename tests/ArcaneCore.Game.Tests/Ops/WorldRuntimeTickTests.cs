using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>Tick measurement and the vmangos-style slow-update log (Perf.log).</summary>
public sealed class WorldRuntimeTickTests
{
    private static WorldRuntime Create(CapturingLogger log, Action<WorldRuntimeOptions>? tune = null)
    {
        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 };
        tune?.Invoke(options);
        return new WorldRuntime(options, new RecordingSaveQueue(), log);
    }

    private static void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the world thread");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void Run_RecordsEveryTick_AndTheHeartbeatAdvances()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 5);

        TickStatsSnapshot first = world.Stats.Snapshot();
        WaitFor(() => world.Stats.Snapshot().TotalTicks > first.TotalTicks + 3);
        TickStatsSnapshot second = world.Stats.Snapshot();
        Assert.True(second.LastTickTimestamp > first.LastTickTimestamp);
        Assert.True(second.Samples > 0);
    }

    [Fact]
    public void Run_RecordsLowLoadFrameCadenceAndEffectiveRate()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().FrameSamples >= 3);

        TickStatsSnapshot snapshot = world.Stats.Snapshot();
        Assert.True(snapshot.MeanFrameIntervalMicros > 0);
        Assert.NotNull(snapshot.EffectiveTicksPerSecond);
        Assert.True(snapshot.EffectiveTicksPerSecond > 0);
    }

    [Fact]
    public void SlowCommand_IsCountedAsAnOverrun_AndMeasuredInTheMaximum()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 3);
        long before = world.Stats.Snapshot().Overruns;

        world.Post(() => Thread.Sleep(80));
        WaitFor(() => world.Stats.Snapshot().Overruns > before);

        Assert.True(world.Stats.Snapshot().MaxMicros >= 80_000 - 5_000);
    }

    [Fact]
    public void SlowCommand_ContributesToFrameCadenceWithoutReplacingWorkDuration()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().FrameSamples >= 2);
        TickStatsSnapshot before = world.Stats.Snapshot();
        using var commandFinished = new ManualResetEventSlim();
        long ticksAtCommand = 0;
        world.Post(() =>
        {
            ticksAtCommand = world.Stats.Snapshot().TotalTicks;
            Thread.Sleep(80);
            commandFinished.Set();
        });
        Assert.True(commandFinished.Wait(TimeSpan.FromSeconds(10)));
        WaitFor(() => world.Stats.Snapshot().TotalTicks > ticksAtCommand + 1);

        TickStatsSnapshot snapshot = world.Stats.Snapshot();
        Assert.True(snapshot.MeanFrameIntervalMicros > 0);
        Assert.True(snapshot.EffectiveTicksPerSecond > 0);
        Assert.True(snapshot.FrameOverruns > before.FrameOverruns);
        Assert.True(snapshot.MaxMicros >= 80_000 - 5_000);
    }

    [Fact]
    public void SlowWorldUpdate_LogsWithThePerfEventId()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowWorldUpdate = 30);
        world.Start();
        world.Post(() => Thread.Sleep(70));
        WaitFor(() => log.Entries.Any(e => e.EventName == PerformanceLogOptions.EventName));

        CapturingLogger.Entry entry = log.Entries.First(e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Contains("Slow world update", entry.Message);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void SlowWorldUpdate_DefaultsToTheRetailFrameInterval()
        => Assert.Equal(SlowWorldUpdateMeasure.FrameInterval, new PerformanceLogOptions().SlowWorldUpdateMeasure);

    // vmangos WorldRunnable.cpp:73-74: the frame interval includes the sleep, so a 60 ms tick
    // length trips a 40 ms threshold with no slow work at all, and the line reads "N ms" only.
    [Fact]
    public void SlowWorldUpdate_FrameInterval_IncludesTheSleep_AndUsesTheRetailText()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o =>
        {
            o.TickIntervalMs = 60;
            o.Perf.SlowWorldUpdate = 40;
        });
        world.Start();
        WaitFor(() => log.Entries.Any(e => e.EventName == PerformanceLogOptions.EventName));

        CapturingLogger.Entry entry = log.Entries.First(e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Matches(@"^Slow world update: \d+ms$", entry.Message);
    }

    [Fact]
    public void SlowWorldUpdate_TickDuration_IgnoresTheSleep_ButCatchesASlowTick()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o =>
        {
            o.TickIntervalMs = 60;
            o.Perf.SlowWorldUpdate = 40;
            o.Perf.SlowWorldUpdateMeasure = SlowWorldUpdateMeasure.TickDuration;
        });
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 4);
        Assert.DoesNotContain(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);

        world.Post(() => Thread.Sleep(70));
        WaitFor(() => log.Entries.Any(e => e.EventName == PerformanceLogOptions.EventName));
        Assert.Contains("interval", log.Entries.First(e => e.EventName == PerformanceLogOptions.EventName).Message);
    }

    [Fact]
    public void SlowWorldUpdate_ThresholdZero_LogsNothing()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowWorldUpdate = 0);
        world.Start();
        long before = 0;
        world.Post(() => Thread.Sleep(70));
        WaitFor(() => world.Stats.Snapshot().Overruns > before);
        Thread.Sleep(30);
        Assert.DoesNotContain(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
    }

    [Fact]
    public void SlowMapUpdate_NamesTheMapAndInstance()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowMapUpdate = 20);
        Map map = world.GetMap(1, 7);
        map.AddUpdater(new SleepingUpdater(50));

        world.RunTick(50);

        CapturingLogger.Entry entry = Assert.Single(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Contains("Slow map update", entry.Message);
        Assert.Contains("map 1", entry.Message);
        Assert.Contains("instance 7", entry.Message);
    }

    [Fact]
    public void SlowMapUpdate_IncludesPhaseCounters_WhenDiagnosticsAreEnabled()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowMapUpdate = 1);
        Map map = world.GetMap(1, 8);
        map.AddUpdater(new SleepingUpdater(20));

        world.RunTick(50);

        CapturingLogger.Entry entry = Assert.Single(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Contains("simulation", entry.Message);
        Assert.Contains("visibility", entry.Message);
        Assert.Contains("values", entry.Message);
        Assert.Contains("flush", entry.Message);
        Assert.Contains("cleanup", entry.Message);
        Assert.Contains("players", entry.Message);
        Assert.Contains("visibility candidates", entry.Message);
        Assert.Contains("moved", entry.Message);
        Assert.Matches(@"players \d+, moved \d+, changed \d+, new \d+", entry.Message);
    }

    [Fact]
    public void SlowMapUpdate_DisabledDoesNotEmitDiagnostics()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowMapUpdate = 0);
        world.GetMap(1).AddUpdater(new ThrowingUpdater());

        world.RunTick(50);

        Assert.DoesNotContain(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
    }

    [Fact]
    public void RunTick_CalledDirectly_StillWorksWithoutStats()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        var ran = false;
        world.Post(() => ran = true);
        world.RunTick(50);
        Assert.True(ran);
        Assert.Equal(0, world.Stats.Snapshot().TotalTicks); // only the world loop records
    }

    [Fact]
    public void RunTick_BoundsCommandsButPreservesFifoAndSelfPosting()
    {
        using WorldRuntime world = Create(new CapturingLogger(), o =>
        {
            o.MaxCommandsPerTick = 2;
            o.CommandTimeBudgetMs = 0;
        });
        var order = new List<int>();
        world.Post(() => { order.Add(1); world.Post(() => order.Add(3)); });
        world.Post(() => order.Add(2));

        world.RunTick(50);
        Assert.Equal([1, 2], order);
        Assert.Equal(1, world.PendingCommandCount);

        world.RunTick(50);
        Assert.Equal([1, 2, 3], order);
        Assert.Equal(0, world.PendingCommandCount);
    }

    [Fact]
    public void Stop_DrainsCommandsAfterTheWorldThreadExits()
    {
        using WorldRuntime world = Create(new CapturingLogger(), o =>
        {
            o.TickIntervalMs = 10_000;
            o.MaxCommandsPerTick = 1;
            o.CommandTimeBudgetMs = 0;
        });
        using var entered = new ManualResetEventSlim();
        bool ran = false;
        int worldThread = 0;
        int shutdownThread = 0;
        world.Post(() =>
        {
            worldThread = Environment.CurrentManagedThreadId;
            world.Post(() =>
            {
                shutdownThread = Environment.CurrentManagedThreadId;
                ran = true;
            });
            entered.Set();
        });
        world.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        world.Stop();
        Assert.True(ran);
        Assert.NotEqual(worldThread, shutdownThread);
        Assert.Equal(0, world.PendingCommandCount);
    }

    /// <summary>A GC counter source the test advances by hand, so a "collection" happens inside a chosen tick.</summary>
    private sealed class FakeGc
    {
        private long _pauseTicks;
        private int _gen0, _gen1, _gen2;

        public GcSnapshot Read() => new(Volatile.Read(ref _pauseTicks), Volatile.Read(ref _gen0), Volatile.Read(ref _gen1), Volatile.Read(ref _gen2));

        public void Collect(TimeSpan pause, int gen0, int gen1, int gen2)
        {
            Interlocked.Add(ref _pauseTicks, pause.Ticks);
            Interlocked.Add(ref _gen0, gen0);
            Interlocked.Add(ref _gen1, gen1);
            Interlocked.Add(ref _gen2, gen2);
        }
    }

    // RED proof: with the capture removed from WorldRuntime.Run, LastTickPhases carries no GC and this line has no "gc" field.
    [Fact]
    public void SlowWorldUpdate_TickDuration_NamesTheGcPauseAndCollectionsThatHappenedInsideTheTick()
    {
        var log = new CapturingLogger();
        var gc = new FakeGc();
        using WorldRuntime world = Create(log, o =>
        {
            o.TickIntervalMs = 60;
            o.Perf.SlowWorldUpdate = 40;
            o.Perf.SlowWorldUpdateMeasure = SlowWorldUpdateMeasure.TickDuration;
        });
        world.GcSource = gc.Read;
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 4);
        Assert.DoesNotContain(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);

        world.Post(() =>
        {
            gc.Collect(TimeSpan.FromMilliseconds(2345.5), 3, 2, 1); // a blocking gen2 inside this tick
            Thread.Sleep(70);
        });
        WaitFor(() => log.Entries.Any(e => e.EventName == PerformanceLogOptions.EventName));

        string message = log.Entries.First(e => e.EventName == PerformanceLogOptions.EventName).Message;
        Assert.Contains("gc 2345.5 ms (gen0 +3, gen1 +2, gen2 +1)", message);

        // The slow-tick record the watchdog's TickHang line reads carries the same split.
        SlowTickInfo worst = world.Stats.SlowTicks(TimeSpan.FromMinutes(1))[0];
        Assert.True(worst.Phases.GcCaptured);
        Assert.Equal(TimeSpan.FromMilliseconds(2345.5).Ticks, worst.Phases.Gc.PauseTicks);
        Assert.Contains("gc 2345.5 ms (gen0 +3, gen1 +2, gen2 +1)", worst.Phases.Format());
    }

    [Fact]
    public void ATickWithoutCollections_ReportsZeroGc_AndEarlierCollectionsAreNotCharged()
    {
        var gc = new FakeGc();
        gc.Collect(TimeSpan.FromSeconds(9), 40, 10, 5); // before the world starts: not part of any tick
        using WorldRuntime world = Create(new CapturingLogger());
        world.GcSource = gc.Read;
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 3);
        world.Post(() => Thread.Sleep(30)); // an over-interval tick that records a slow-tick entry
        WaitFor(() => world.Stats.SlowTicks(TimeSpan.FromMinutes(1)).Count > 0);

        TickPhases phases = world.Stats.SlowTicks(TimeSpan.FromMinutes(1))[0].Phases;
        Assert.True(phases.GcCaptured);
        Assert.Equal(new GcDelta(0, 0, 0, 0), phases.Gc);
        Assert.EndsWith("gc 0.0 ms (gen0 +0, gen1 +0, gen2 +0)", phases.Format());
    }

    [Fact]
    public void TickPhasesFormat_OmitsGcWhenNoneWasCaptured_AndThePauseIsNeverNegative()
    {
        var phases = new TickPhases(1000, 2000, 3000);
        Assert.DoesNotContain("gc ", phases.Format());
        Assert.EndsWith("gc 1.5 ms (gen0 +1, gen1 +0, gen2 +0)", (phases with { GcCaptured = true, Gc = new GcDelta(15_000, 1, 0, 0) }).Format());

        var before = new GcSnapshot(100, 5, 5, 5);
        Assert.Equal(new GcDelta(0, 0, 0, 0), new GcSnapshot(50, 4, 4, 4).Since(before));
    }

    [Fact]
    public void ReadingTheGcCounters_AllocatesNothing()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        Func<GcSnapshot> source = world.GcSource;
        GcSnapshot warm = source();
        _ = source().Since(warm); // JIT both paths first
        // A gen0 GC triggered by another test thread can add ~one allocation quantum (7-8 KB) to this thread's counter inside a window; a real
        // allocation shows in every window, so the claim is that the cheapest window allocates nothing.
        long pause = 0, allocated = long.MaxValue;
        for (int window = 0; window < 5 && allocated != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                GcSnapshot a = source();
                GcSnapshot b = source();
                pause += b.Since(a).PauseTicks;
            }

            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(pause >= 0);
        Assert.Equal(0, allocated);
    }

    private sealed class SleepingUpdater(int ms) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => Thread.Sleep(ms);

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class ThrowingUpdater : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => throw new InvalidOperationException("diagnostic test failure");
        public void OnPlayerRemoved(Map map, Player player) { }
    }

    private sealed class CapturingLogger : ILogger<WorldRuntime>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new Entry(logLevel, eventId.Name, formatter(state, exception)));

        public sealed record Entry(LogLevel Level, string? EventName, string Message);
    }
}
