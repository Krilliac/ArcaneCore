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
    public void Alerted_ReportsAnOverrun_AndACompletedHang_RightAfterTheirLogLines()
    {
        TickMonitor monitor = Create(o => o.HangMs = 1000);
        var alerts = new List<TickAlert>();
        monitor.Alerted = alerts.Add;

        Tick(monitor, 99, 50);
        Tick(monitor, 1, 150); // one slow frame
        Tick(monitor, 1, 50);
        monitor.Check(_clock.NowMicros);
        TickAlert overrun = Assert.Single(alerts);
        Assert.Equal(TickAlertKind.Overrun, overrun.Kind);
        Assert.Contains("1 frame(s) over the 100 ms budget", overrun.Detail);

        Tick(monitor, 2, 3000); // a frame of 3 s, over the 1 s hang threshold
        monitor.Check(_clock.NowMicros);
        Assert.Contains(alerts, a => a.Kind == TickAlertKind.Hang && a.Detail.Contains("hang threshold 1000 ms", StringComparison.Ordinal));
    }

    [Fact]
    public void Alerted_ThrowingSubscriber_DoesNotStopTheCheck()
    {
        TickMonitor monitor = Create();
        monitor.Alerted = _ => throw new InvalidOperationException("subscriber failed");
        Tick(monitor, 99, 50);
        Tick(monitor, 1, 150);
        Tick(monitor, 1, 50);

        monitor.Check(_clock.NowMicros);

        Assert.Single(_log.Of(WatchdogEvents.TickOverrun)); // the line was written before the subscriber ran
    }

    [Fact]
    public void Alerted_AHangStillInProgress_IsReportedAsHung_AndTheCompletedHangAsStalled()
    {
        TickMonitor monitor = Create(o => o.HangMs = 2000);
        var alerts = new List<TickAlert>();
        monitor.Alerted = alerts.Add;
        Tick(monitor, 5, 50);
        monitor.OnTick(_clock.NowMicros); // this tick never ends
        _clock.AdvanceMs(1_500);
        monitor.Check(_clock.NowMicros);
        Assert.Empty(alerts); // under the threshold

        _clock.AdvanceMs(1_000); // 2.5 s into the tick, no OnTick since
        monitor.Check(_clock.NowMicros);
        TickAlert running = Assert.Single(alerts);
        Assert.Equal(TickAlertKind.Hang, running.Kind);
        Assert.True(running.InProgress);
        Assert.Contains("is hung", running.Text);
        Assert.Contains("2500 ms", running.Text);
        Assert.Contains("hang threshold 2000 ms", running.Detail);
        Assert.Single(_log.Of(WatchdogEvents.TickHang));

        // The tick ends: the same hang is reported again, as a completed one, and says so.
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);
        TickAlert completed = Assert.Single(alerts, a => a.Kind == TickAlertKind.Hang && !a.InProgress);
        Assert.Contains("stalled", completed.Text);
        Assert.DoesNotContain("is hung", completed.Text);
    }

    [Fact]
    public void Alerted_AHangInProgress_WithAThrowingSubscriber_StillWritesTheCriticalLine_AndKeepsChecking()
    {
        TickMonitor monitor = Create(o => o.HangMs = 2000);
        int calls = 0;
        monitor.Alerted = _ =>
        {
            calls++;
            throw new InvalidOperationException("subscriber failed");
        };
        Tick(monitor, 3, 50);
        monitor.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(2_500);

        monitor.Check(_clock.NowMicros); // must not throw

        LogLine line = Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Contains("running for 2500 ms", line.Message);
        Assert.Equal(1, calls);

        // The watchdog thread is intact: the completed hang is still reported afterwards.
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.TickHang));
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
    public void Hang_WithHangDetail_AppendsTheDetailToBothHangLines()
    {
        TickMonitor monitor = Create(o => o.HangMs = 2000);
        var asked = new List<bool>();
        monitor.HangDetail = inProgress =>
        {
            asked.Add(inProgress);
            return inProgress ? "phase=Features, running=SyntheticSlowFeature for 2500 ms" : "slowest tick body 9000 ms: maps 3 ms";
        };
        Tick(monitor, 5, 50);
        monitor.OnTick(_clock.NowMicros); // this tick never ends
        _clock.AdvanceMs(2_500);
        monitor.Check(_clock.NowMicros);

        LogLine live = Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.EndsWith("); phase=Features, running=SyntheticSlowFeature for 2500 ms", live.Message);
        Assert.Equal([true], asked);

        monitor.OnTick(_clock.NowMicros); // the tick ends, the next starts
        monitor.Check(_clock.NowMicros);
        LogLine done = _log.Of(WatchdogEvents.TickHang).Last();
        Assert.Equal(2, _log.CountOf(WatchdogEvents.TickHang));
        Assert.EndsWith("since start; slowest tick body 9000 ms: maps 3 ms", done.Message);
        // The completed hang also counts as an overrun, so the same Check logs the overrun warning, which asks after the fact too.
        LogLine overrun = Assert.Single(_log.Of(WatchdogEvents.TickOverrun));
        Assert.EndsWith("; slowest tick body 9000 ms: maps 3 ms", overrun.Message);
        Assert.Equal([true, false, false], asked);
    }

    [Fact]
    public void Hang_WithoutHangDetail_OrAnEmptyOne_LeavesTheLinesUnchanged()
    {
        TickMonitor plain = Create(o => o.HangMs = 2000);
        Tick(plain, 5, 50);
        plain.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(2_500);
        plain.Check(_clock.NowMicros);
        Assert.EndsWith("interval 50 ms)", Assert.Single(_log.Of(WatchdogEvents.TickHang)).Message);

        var log = new RecordingLogger<TickMonitor>();
        var empty = new TickMonitor(new TickMonitorOptions { HangMs = 2000 }, _clock, WatchdogTestSupport.NewRegistry(), log);
        empty.StartTicking(50);
        empty.HangDetail = _ => string.Empty;
        empty.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(2_500);
        empty.Check(_clock.NowMicros);
        Assert.EndsWith("interval 50 ms)", Assert.Single(log.Of(WatchdogEvents.TickHang)).Message);
    }

    [Fact]
    public void Hang_WithAThrowingHangDetail_StillLogsTheLine_NamingTheFailure()
    {
        TickMonitor monitor = Create(o => o.HangMs = 2000);
        monitor.HangDetail = _ => throw new InvalidOperationException("boom");
        Tick(monitor, 3, 50);
        monitor.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(2_500);
        monitor.Check(_clock.NowMicros);
        LogLine line = Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Contains("running for 2500 ms", line.Message);
        Assert.EndsWith("; hang detail unavailable (InvalidOperationException)", line.Message);
        Assert.True(monitor.HangDetail is not null); // the watchdog keeps checking afterwards
    }

    [Fact]
    public void Overrun_WithHangDetail_AppendsTheSlowestFrameBreakdown()
    {
        TickMonitor monitor = Create();
        bool? askedInProgress = null;
        monitor.HangDetail = inProgress =>
        {
            askedInProgress = inProgress;
            return "slowest tick body 130 ms: commands 0 ms (0 run), maps 2 ms";
        };
        Tick(monitor, 2, 50);
        Tick(monitor, 1, 130);
        Tick(monitor, 1, 50);
        monitor.Check(_clock.NowMicros);
        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.TickOverrun));
        Assert.EndsWith("; slowest tick body 130 ms: commands 0 ms (0 run), maps 2 ms", warning.Message);
        Assert.False(askedInProgress); // an overrun is after the fact
    }

    [Fact]
    public void HangDetail_IsNotCalledWhenNoLineIsLogged()
    {
        TickMonitor monitor = Create();
        int calls = 0;
        monitor.HangDetail = _ => { calls++; return "x"; };
        Tick(monitor, 200, 50);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(0, calls);
        Assert.Empty(_log.Lines);
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
