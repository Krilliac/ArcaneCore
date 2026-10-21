using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>
/// The slow-update summary (<see cref="PerfLogAggregator"/>): one line per interval instead of one per slow frame or map update, with a few
/// detail lines for the outliers. The aggregator tests run on a manual clock; the runtime tests drive <see cref="WorldRuntime"/> through
/// its manual game clock (the aggregator's interval runs on it) or through <see cref="WorldRuntime.RunTick(uint)"/>.
/// </summary>
public sealed class PerfLogAggregatorTests
{
    private const string Summary = "Slow update summary";

    private sealed class CapturingLog : ILogger<WorldRuntime>
    {
        public ConcurrentQueue<(LogLevel Level, string? EventName, string Message)> Entries { get; } = new();

        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, eventId.Name, formatter(state, exception)));
    }

    private sealed class ManualClock
    {
        public long Micros;

        public long Now() => Micros;

        public void Seconds(double seconds) => Micros = (long)(seconds * 1_000_000);
    }

    private sealed class Alpha : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class Beta : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class Gamma : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class Delta : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class Sleeper(int ms) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => Thread.Sleep(ms);

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private static PerformanceLogOptions Options(Action<PerformanceLogOptions>? tune = null)
    {
        var options = new PerformanceLogOptions { SlowWorldUpdate = 100, SlowMapUpdate = 100 };
        tune?.Invoke(options);
        return options;
    }

    private static MapUpdateDiagnostics Diagnostics(params (IMapUpdater Updater, long Micros)[] updaters)
    {
        var diagnostics = new MapUpdateDiagnostics();
        foreach ((IMapUpdater updater, long micros) in updaters)
        {
            diagnostics.Updaters.Add((updater, micros));
        }

        diagnostics.Complete();
        return diagnostics;
    }

    private static WorldRuntime CreateWorld(CapturingLog log, Action<PerformanceLogOptions> tune)
    {
        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 };
        tune(options.Perf);
        return new WorldRuntime(options, new RecordingSaveQueue(), log);
    }

    [Fact]
    public void ThousandSlowFrames_InOneInterval_AreOneSummaryAtTheBoundary_WithExactDistribution()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        int detailLines = 0;
        for (int i = 0; i < 1000; i++)
        {
            clock.Seconds(i * 59.0 / 1000);
            aggregator.CountFrame();
            long ms = 100 + i % 100; // 100..199 ms, ten of each, all under the 250 ms detail bar
            if (aggregator.RecordWorldFrame(ms * 1000))
            {
                detailLines++;
            }

            Assert.False(aggregator.FlushIfDue());
        }

        Assert.Equal(0, detailLines);
        Assert.Empty(log.Entries);

        clock.Seconds(60);
        Assert.True(aggregator.FlushIfDue());

        string line = Assert.Single(log.Messages);
        Assert.StartsWith(Summary, line, StringComparison.Ordinal);
        Assert.Contains("world frames over 100 ms: 1000 of 1000 (p50 149 ms, p95 194 ms, max 199 ms)", line);
        Assert.Equal(PerformanceLogOptions.EventName, Assert.Single(log.Entries).EventName);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);

        // The next interval starts empty: nothing slow, nothing written.
        clock.Seconds(121);
        Assert.True(aggregator.FlushIfDue());
        Assert.Single(log.Entries);
    }

    [Fact]
    public void FiftyDetailEligibleUpdates_OnOneMap_GiveOneDetailLine_AndTheSummaryCountsTheRest()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        int detailLines = 0;
        for (int i = 0; i < 50; i++)
        {
            clock.Seconds(i);
            if (aggregator.RecordMapUpdate(1, 7, 250_000, null, default))
            {
                detailLines++;
            }
        }

        Assert.Equal(1, detailLines);
        clock.Seconds(60);
        Assert.True(aggregator.FlushIfDue());

        string line = Assert.Single(log.Messages);
        Assert.Contains("map 1 instance 7: 50 (p50 250 ms, p95 250 ms, max 250 ms", line);
        Assert.Contains("detail lines suppressed: 49", line);
    }

    [Fact]
    public void TwoMaps_EachGetTheirOwnDetailLine()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        bool a1 = aggregator.RecordMapUpdate(0, 0, 300_000, null, default);
        bool b1 = aggregator.RecordMapUpdate(1, 5, 300_000, null, default);
        bool a2 = aggregator.RecordMapUpdate(0, 0, 300_000, null, default);
        bool b2 = aggregator.RecordMapUpdate(1, 5, 300_000, null, default);

        Assert.True(a1);
        Assert.True(b1);
        Assert.False(a2);
        Assert.False(b2);
    }

    [Fact]
    public void DetailLinesPerMapPerInterval_RaisesTheCap_AndZeroSilencesTheDetail()
    {
        var clock = new ManualClock();
        var three = new PerfLogAggregator(Options(o => o.DetailLinesPerMapPerInterval = 3), new CapturingLog(), clock.Now);
        int written = 0;
        for (int i = 0; i < 10; i++)
        {
            written += three.RecordMapUpdate(2, 0, 300_000, null, default) ? 1 : 0;
        }

        Assert.Equal(3, written);

        var none = new PerfLogAggregator(Options(o => o.DetailLinesPerMapPerInterval = 0), new CapturingLog(), clock.Now);
        Assert.False(none.RecordMapUpdate(2, 0, 300_000, null, default));
        Assert.False(none.RecordWorldFrame(900_000));
    }

    [Fact]
    public void UpdatesBelowTheDetailThreshold_NeverAskForALine()
    {
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), new CapturingLog(), clock.Now);

        Assert.False(aggregator.RecordMapUpdate(1, 1, 150_000, null, default)); // over the 100 ms slow bar, under the 200 ms detail bar
        Assert.False(aggregator.RecordWorldFrame(240_000)); // under the 250 ms detail bar
        Assert.True(aggregator.RecordWorldFrame(251_000));
    }

    [Fact]
    public void AQuietInterval_WritesNoSummary()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        for (int i = 0; i < 1000; i++)
        {
            aggregator.CountFrame();
        }

        clock.Seconds(60);
        Assert.True(aggregator.FlushIfDue()); // the interval ended
        clock.Seconds(120);
        Assert.True(aggregator.FlushIfDue());
        aggregator.FlushNow();

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void TopContributors_AreRankedBySummedMilliseconds_AcrossUpdates()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        aggregator.RecordMapUpdate(3, 0, 400_000, Diagnostics((new Alpha(), 100_000), (new Beta(), 60_000), (new Gamma(), 150_000), (new Delta(), 5_000)), default);
        aggregator.RecordMapUpdate(3, 0, 400_000, Diagnostics((new Alpha(), 200_000), (new Beta(), 40_000), (new Gamma(), 50_000), (new Delta(), 5_000)), default);
        clock.Seconds(60);
        aggregator.FlushIfDue();

        string line = Assert.Single(log.Messages);
        Assert.Contains("top Alpha 300 ms, Gamma 200 ms, Beta 100 ms)", line);
        Assert.DoesNotContain("Delta", line);
    }

    [Fact]
    public void MapsAreListedBusiestFirst_WithTheirOwnDistribution()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        aggregator.RecordMapUpdate(5, 1, 120_000, null, default);
        aggregator.RecordMapUpdate(9, 2, 130_000, null, default);
        aggregator.RecordMapUpdate(9, 2, 190_000, null, default);
        clock.Seconds(60);
        aggregator.FlushIfDue();

        string line = Assert.Single(log.Messages);
        Assert.Contains("map updates over 100 ms: 3 in 2 maps: map 9 instance 2: 2 (p50 130 ms, p95 190 ms, max 190 ms", line);
        Assert.True(line.IndexOf("map 9 instance 2", StringComparison.Ordinal) < line.IndexOf("map 5 instance 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Verdicts_AreCounted_FromTheCollectorShareAndTheHostProbe()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        int probes = 0;
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now, _ =>
        {
            probes++;
            return HostLoadVerdict.Quiet;
        });

        aggregator.RecordMapUpdate(1, 0, 200_000, null, new GcDelta(PauseTicks: 1_000_000, 1, 0, 0)); // 100 ms of a 200 ms update in the collector
        aggregator.RecordMapUpdate(1, 0, 200_000, null, default);
        aggregator.RecordMapUpdate(1, 0, 200_000, null, default);
        clock.Seconds(60);
        aggregator.FlushIfDue();

        Assert.Contains("verdicts: host-starved 0, quiet 2, gc 1", Assert.Single(log.Messages));
        Assert.Equal(1, probes); // the second reused the first's answer: the host is probed at most once a second
    }

    [Fact]
    public void RecordingASample_AllocatesNothing_OnceWarm()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now, static _ => HostLoadVerdict.Quiet);
        MapUpdateDiagnostics diagnostics = Diagnostics((new Alpha(), 100_000), (new Beta(), 60_000));
        var gc = new GcDelta(0, 0, 0, 0);

        for (int i = 0; i < 20; i++)
        {
            aggregator.CountFrame();
            aggregator.RecordWorldFrame(120_000);
            aggregator.RecordMapUpdate(1, 0, 250_000, diagnostics, gc);
            aggregator.FlushIfDue();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            clock.Micros = i; // the interval does not end
            aggregator.CountFrame();
            aggregator.RecordWorldFrame(300_000); // over the detail bar: the limiter path
            aggregator.RecordMapUpdate(1, 0, 250_000, diagnostics, gc);
            aggregator.FlushIfDue();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TheNextInterval_IsCountedFromZero()
    {
        var log = new CapturingLog();
        var clock = new ManualClock();
        var aggregator = new PerfLogAggregator(Options(), log, clock.Now);

        aggregator.RecordMapUpdate(1, 1, 150_000, null, default);
        clock.Seconds(60);
        aggregator.FlushIfDue();
        clock.Seconds(100);
        aggregator.RecordMapUpdate(1, 1, 150_000, null, default);
        clock.Seconds(120);
        aggregator.FlushIfDue();

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Messages, m => Assert.Contains("map 1 instance 1: 1 (", m));
    }

    // ---- through WorldRuntime ----

    [Fact]
    public async Task World_ManySlowFrames_OverAMinuteOfGameTime_ProduceOneSummaryAndNoPerFrameLines()
    {
        var log = new CapturingLog();
        using WorldRuntime world = CreateWorld(log, p =>
        {
            p.SlowWorldUpdate = 30; // the manual clock's 50 ms steps are all over it
            p.SlowWorldUpdateDetail = 250;
        });
        world.UseManualClock(50);
        world.Start();

        await world.AdvanceClockAsync(59_000).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("Slow world update", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith(Summary, StringComparison.Ordinal));

        await world.AdvanceClockAsync(1_000).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("Slow world update", StringComparison.Ordinal));
        string line = Assert.Single(log.Messages, m => m.StartsWith(Summary, StringComparison.Ordinal));
        Assert.Contains("world frames over 30 ms: 1200 of ", line);
        Assert.Contains("(p50 50 ms, p95 50 ms, max 50 ms)", line);
    }

    [Fact]
    public async Task World_WithSummaryIntervalZero_KeepsTheRetailPerFrameLine()
    {
        var log = new CapturingLog();
        using WorldRuntime world = CreateWorld(log, p =>
        {
            p.SlowWorldUpdate = 30;
            p.SummaryIntervalSeconds = 0;
        });
        world.UseManualClock(50);
        world.Start();

        await world.AdvanceClockAsync(5_000).WaitAsync(TimeSpan.FromSeconds(60));

        string[] lines = [.. log.Messages.Where(m => m.StartsWith("Slow world update", StringComparison.Ordinal))];
        Assert.Equal(100, lines.Length);
        Assert.All(lines, l => Assert.Equal("Slow world update: 50ms", l));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith(Summary, StringComparison.Ordinal));
    }

    [Fact]
    public void World_TwoSlowMaps_EachWriteOneDetailLine_AndTheRepeatsAreSuppressed()
    {
        var log = new CapturingLog();
        using WorldRuntime world = CreateWorld(log, p =>
        {
            p.SlowMapUpdate = 1;
            p.SlowMapUpdateDetail = 2;
        });
        world.GetMap(1, 7).AddUpdater(new Sleeper(8));
        world.GetMap(2, 0).AddUpdater(new Sleeper(8));

        for (int i = 0; i < 4; i++)
        {
            world.RunTick(50);
        }

        string[] lines = [.. log.Messages.Where(m => m.StartsWith("Slow map update", StringComparison.Ordinal))];
        Assert.Equal(2, lines.Length);
        Assert.Single(lines, l => l.Contains("map 1 instance 7", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("map 2 instance 0", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith(Summary, StringComparison.Ordinal)); // the interval has not ended
    }

    [Fact]
    public void World_SlowMapUpdatesBelowTheDetailBar_WriteNoPerEventLine()
    {
        var log = new CapturingLog();
        using WorldRuntime world = CreateWorld(log, p =>
        {
            p.SlowMapUpdate = 1;
            p.SlowMapUpdateDetail = 10_000;
        });
        world.GetMap(1, 3).AddUpdater(new Sleeper(8));

        for (int i = 0; i < 3; i++)
        {
            world.RunTick(50);
        }

        Assert.DoesNotContain(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
    }

    [Fact]
    public void World_WithSummaryIntervalZero_LogsEverySlowMapUpdate()
    {
        var log = new CapturingLog();
        using WorldRuntime world = CreateWorld(log, p =>
        {
            p.SlowMapUpdate = 1;
            p.SummaryIntervalSeconds = 0;
        });
        world.GetMap(1, 3).AddUpdater(new Sleeper(8));

        for (int i = 0; i < 3; i++)
        {
            world.RunTick(50);
        }

        string[] lines = [.. log.Messages.Where(m => m.StartsWith("Slow map update", StringComparison.Ordinal))];
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^Slow map update: map 1 instance 3 took \d+ ms \(simulation ", l));
    }
}
