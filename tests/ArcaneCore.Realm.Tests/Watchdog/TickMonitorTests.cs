using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class TickMonitorTests
{
    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<TickMonitor> _log = new();
    private readonly CounterRegistry _counters = WatchdogTestSupport.NewRegistry();

    private TickMonitor Create(Action<TickMonitorOptions>? configure = null, int tickMs = 50)
    {
        var options = new TickMonitorOptions();
        configure?.Invoke(options);
        var monitor = new TickMonitor(options, _clock, _counters, _log);
        monitor.StartTicking(tickMs);
        return monitor;
    }

    /// <summary>Run <paramref name="frames"/> ticks, each starting <paramref name="frameMs"/> after the previous one.</summary>
    private void Tick(TickMonitor monitor, int frames, long frameMs)
    {
        for (int i = 0; i < frames; i++)
        {
            monitor.OnTick(_clock.NowMicros);
            _clock.AdvanceMs(frameMs);
        }
    }

    [Fact]
    public void Budget_DefaultsToTwiceTheTickInterval_AndHangToTheOption()
    {
        TickMonitor monitor = Create(tickMs: 50);
        Assert.Equal(100_000, monitor.BudgetMicros);
        Assert.Equal(2_000_000, monitor.HangMicros);
        Assert.Equal(60_000, Create(o => o.BudgetMs = 60).BudgetMicros);
        Assert.Equal(long.MaxValue, Create(o => o.HangMs = 0).HangMicros);
    }

    [Fact]
    public void FramesWithinBudget_ProduceNoLogLine_AndTheMonitorIsAlive()
    {
        TickMonitor monitor = Create();
        Tick(monitor, 200, 50);
        monitor.Check(_clock.NowMicros);
        Assert.Empty(_log.Lines);
        Assert.True(monitor.IsAlive(_clock.NowMicros, out string? reason));
        Assert.Null(reason);
        Assert.Equal(0, monitor.Overruns);
        Assert.Equal(199, monitor.Ring.Count); // the first tick has no previous start
    }

    [Fact]
    public void Overrun_LogsOneRateLimitedWarning_WithPercentilesAndTheSlowestFrame()
    {
        TickMonitor monitor = Create(o => o.WarnIntervalSeconds = 30);
        Tick(monitor, 99, 50);
        Tick(monitor, 1, 150); // one slow frame
        Tick(monitor, 1, 50);
        monitor.Check(_clock.NowMicros);

        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.TickOverrun));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("100 ms budget 1 time(s)", warning.Message);
        Assert.Contains("p50 50 ms", warning.Message);
        Assert.Contains("p99 50 ms", warning.Message);
        Assert.Contains("slowest 150 ms", warning.Message);
        Assert.Equal(1, monitor.Overruns);
        Assert.Equal(1, _counters.Find("watchdog.tick.overruns")!.Value);

        // More overruns inside the warning interval are counted, not logged, and come out with the next warning.
        Tick(monitor, 20, 50);
        Tick(monitor, 3, 200);
        Tick(monitor, 1, 50);
        monitor.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.TickOverrun));
        Tick(monitor, 620, 50); // 31 s of healthy frames: past the warning interval
        monitor.Check(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.TickOverrun));
        Assert.Contains("3 time(s) in the last", _log.Of(WatchdogEvents.TickOverrun).Last().Message);
        Assert.Equal(4, _counters.Find("watchdog.tick.overruns")!.Value);
    }

    [Fact]
    public void Overrun_WithBodyStats_AppendsTheTickBodyPercentiles()
    {
        TickMonitor monitor = Create();
        monitor.BodyStats = () => new TickBodySummary(10, 12_000, 48_000, 130_000, 1);
        Tick(monitor, 2, 50);
        Tick(monitor, 1, 130);
        Tick(monitor, 1, 50); // the 130 ms frame is recorded when the next tick starts
        monitor.Check(_clock.NowMicros);
        Assert.Contains("tick body p50 12 ms, p99 48 ms, max 130 ms, 1 over the interval", Assert.Single(_log.Of(WatchdogEvents.TickOverrun)).Message);
    }

    [Fact]
    public void HangInProgress_IsCriticalOnce_WithholdsLiveness_AndClearsWhenTheNextTickStarts()
    {
        TickMonitor monitor = Create(o => o.HangMs = 2000);
        Tick(monitor, 5, 50);
        monitor.OnTick(_clock.NowMicros); // this tick never ends
        _clock.AdvanceMs(1_500);
        monitor.Check(_clock.NowMicros);
        Assert.Empty(_log.Of(WatchdogEvents.TickHang));
        Assert.True(monitor.IsAlive(_clock.NowMicros, out _));

        _clock.AdvanceMs(1_000); // 2.5 s into the tick
        monitor.Check(_clock.NowMicros);
        LogLine critical = Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.Equal(LogLevel.Critical, critical.Level);
        Assert.Contains("running for 2500 ms", critical.Message);
        Assert.False(monitor.IsAlive(_clock.NowMicros, out string? reason));
        Assert.Contains("2500 ms", reason);

        _clock.AdvanceMs(5_000); // still hung, inside the warn interval: no second line
        monitor.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.TickHang));

        _clock.AdvanceMs(30_000); // past the warn interval: one more "still running"
        monitor.Check(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.TickHang));

        // The tick ends and the next starts: the completed hang is reported once and liveness is back.
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);
        Assert.True(monitor.IsAlive(_clock.NowMicros, out _));
        Assert.Equal(1, monitor.HangsCompleted);
        Assert.Equal(3, _log.CountOf(WatchdogEvents.TickHang));
        Assert.Contains("exceeded the hang threshold 2000 ms: slowest recent frame 37500 ms, 1 new hang(s), 1 since start", _log.Of(WatchdogEvents.TickHang).Last().Message);
        Assert.Equal(1, _counters.Find("watchdog.tick.hangs")!.Value);

        // Nothing more on the next check.
        _clock.AdvanceMs(50);
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(3, _log.CountOf(WatchdogEvents.TickHang));
    }

    [Fact]
    public void HangDetection_Off_NeverReportsOrWithholds()
    {
        TickMonitor monitor = Create(o => o.HangMs = 0);
        Tick(monitor, 2, 50);
        monitor.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(600_000);
        monitor.Check(_clock.NowMicros);
        Assert.Empty(_log.Of(WatchdogEvents.TickHang));
        Assert.True(monitor.IsAlive(_clock.NowMicros, out _));
    }

    [Fact]
    public void Liveness_BeforeTheFirstTick_AfterStopTicking_AndWhenNotGating_IsAlive()
    {
        TickMonitor fresh = Create();
        Assert.True(fresh.IsAlive(_clock.NowMicros, out _)); // the world has not started ticking

        TickMonitor stopped = Create();
        stopped.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(10_000);
        Assert.False(stopped.IsAlive(_clock.NowMicros, out _));
        stopped.StopTicking();
        Assert.True(stopped.IsAlive(_clock.NowMicros, out _));

        TickMonitor notGating = Create(o => o.GatesHeartbeat = false);
        notGating.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(10_000);
        Assert.True(notGating.IsAlive(_clock.NowMicros, out _));

        TickMonitor disabled = Create(o => o.Enabled = false);
        disabled.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(10_000);
        disabled.Check(_clock.NowMicros);
        Assert.True(disabled.IsAlive(_clock.NowMicros, out _));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Summary_IsLoggedEveryInterval_WithTheDistribution()
    {
        TickMonitor monitor = Create(o => o.SummaryIntervalSeconds = 60);
        monitor.Check(_clock.NowMicros); // arms the first summary
        Tick(monitor, 100, 50);
        _clock.AdvanceMs(60_000);
        monitor.Check(_clock.NowMicros);
        LogLine summary = Assert.Single(_log.Of(WatchdogEvents.TickSummary));
        Assert.Equal(LogLevel.Information, summary.Level);
        Assert.Contains("p50 50 ms", summary.Message);
        Assert.Contains("over 99 frames", summary.Message);
        Assert.Contains("0 overrun(s) of 100 ms", summary.Message);
        _clock.AdvanceMs(30_000);
        monitor.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.TickSummary));
    }

    [Fact]
    public void BeforeStartTicking_NothingCountsAsAnOverrunOrHang()
    {
        var monitor = new TickMonitor(new TickMonitorOptions(), _clock, _counters, _log);
        monitor.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(100_000);
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(0, monitor.Overruns);
        Assert.Equal(0, monitor.HangsCompleted);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void OnTick_DoesNotAllocate()
    {
        TickMonitor monitor = Create();
        long bytes = WatchdogTestSupport.AllocatedBy(() =>
        {
            monitor.OnTick(_clock.NowMicros);
            _clock.AdvanceMs(50);
        }, 100_000);
        Assert.Equal(0, bytes);
        Assert.Equal(0, WatchdogTestSupport.AllocatedBy(monitor.OnTick, 100_000));
    }

    [Fact]
    public void Check_WithNothingToReport_DoesNotAllocateBeyondTheFirstCall()
    {
        TickMonitor monitor = Create(o => o.SummaryIntervalSeconds = 0);
        Tick(monitor, 10, 50);
        monitor.Check(_clock.NowMicros);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            monitor.Check(_clock.NowMicros);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
