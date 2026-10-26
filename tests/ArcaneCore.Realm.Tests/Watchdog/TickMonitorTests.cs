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
    private readonly CounterRegistry _counters = WatchdogTestKit.NewRegistry();

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
    public void Overrun_KeepsTheOldTextAndAppendsTheFullFrameLatencyLine()
    {
        TickMonitor monitor = Create();
        Tick(monitor, 99, 50);
        Tick(monitor, 1, 150);
        Tick(monitor, 1, 50);
        monitor.Check(_clock.NowMicros);

        string message = Assert.Single(_log.Of(WatchdogEvents.TickOverrun)).Message;
        // The existing placeholders, in their existing order, are untouched ...
        Assert.StartsWith("world tick overran its 100 ms budget 1 time(s): frame p50 50 ms, p99 50 ms, slowest 150 ms over 100 frames", message, StringComparison.Ordinal);
        // ... and the new detail follows them: p75, p95, p99.9, min, mean and spread over the last minute.
        Assert.Contains("; frame latency last 1 min: p50 ", message, StringComparison.Ordinal);
        Assert.Contains("p99.9 150.0 max 150.0 (n=100, min 50.0, mean 51.0 +/-9.9)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("last 15 min", message, StringComparison.Ordinal); // the warning carries the minute only; the summary has both windows
    }

    [Fact]
    public void Summary_KeepsTheOldTextAndAppendsBothLatencyWindows_AndPublishesTheGauges()
    {
        TickMonitor monitor = Create(o => o.SummaryIntervalSeconds = 5);
        monitor.Check(_clock.NowMicros); // arms the first summary
        Tick(monitor, 100, 50);
        _clock.AdvanceMs(10);
        monitor.Check(_clock.NowMicros);

        string message = Assert.Single(_log.Of(WatchdogEvents.TickSummary)).Message;
        Assert.StartsWith("world tick frames: p50 50 ms, p90 50 ms, p99 50 ms, slowest 50 ms over 99 frames; 0 overrun(s) of 100 ms, 0 hang(s), ", message, StringComparison.Ordinal);
        Assert.Contains("; frame latency last 1 min: p50 50.0 ms p75 50.0 p90 50.0 p95 50.0 p99 50.0 p99.9 50.0 max 50.0 (n=99, min 50.0, mean 50.0 +/-0.0); last 15 min: p50 50.0 ms", message, StringComparison.Ordinal);

        // The new percentiles are gauges too, next to the existing frame_p99_us and frame_max_us.
        foreach (string gauge in new[] { "p50", "p75", "p90", "p95", "p999", "mean" })
        {
            Assert.Equal(50_000, _counters.Find($"watchdog.tick.frame_{gauge}_us")!.Value);
        }

        Assert.Equal(0, _counters.Find("watchdog.tick.frame_stddev_us")!.Value);
        Assert.Equal(50_000, _counters.Find("watchdog.tick.frame_p99_us")!.Value);
        Assert.Equal(50_000, _counters.Find("watchdog.tick.frame_max_us")!.Value);
    }

    [Fact]
    public void AttachedBodyAndPhaseHistograms_AppearInTheSummaryAndTheGauges()
    {
        TickMonitor monitor = Create(o => o.SummaryIntervalSeconds = 5);
        var body = new ArcaneCore.Kernel.Diagnostics.LatencyHistogram();
        var maps = new ArcaneCore.Kernel.Diagnostics.LatencyHistogram();
        var commands = new ArcaneCore.Kernel.Diagnostics.LatencyHistogram();
        for (int i = 0; i < 50; i++)
        {
            body.Record(12_000);
            maps.Record(8_000);
        }

        var frame = new ArcaneCore.Kernel.Diagnostics.LatencyHistogram();
        for (int i = 0; i < 50; i++)
        {
            frame.Record(20_000);
        }

        monitor.AttachLatency(frame, body, [new("commands", commands), new("maps", maps)]);
        monitor.Check(_clock.NowMicros);
        Tick(monitor, 100, 50);
        _clock.AdvanceMs(10);
        monitor.Check(_clock.NowMicros);

        string message = Assert.Single(_log.Of(WatchdogEvents.TickSummary)).Message;
        // The attached histogram is the single source of the frame interval: the monitor did not record its own frames next to it.
        Assert.Same(frame, monitor.FrameLatency);
        Assert.Equal(50, frame.Count);
        Assert.Contains("frame latency last 1 min: p50 20.0 ms", message, StringComparison.Ordinal);
        Assert.Contains("; tick body last 1 min: p50 12.0 ms", message, StringComparison.Ordinal);
        Assert.Contains("; tick phases p50/p99 ms last 1 min: maps 8.0/8.0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("commands ", message[message.IndexOf("tick phases", StringComparison.Ordinal)..], StringComparison.Ordinal); // a phase with no samples is not listed
        Assert.Equal(12_000, _counters.Find("watchdog.tick.body_p99_us")!.Value);
        Assert.Equal(12_000, _counters.Find("watchdog.tick.body_max_us")!.Value);
        Assert.Equal(8_000, _counters.Find("watchdog.tick.phase_maps_p99_us")!.Value);
        Assert.Equal(0, _counters.Find("watchdog.tick.phase_commands_p99_us")!.Value);
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
        Assert.Contains("; slowest tick body 9000 ms: maps 3 ms; frame latency last 1 min: ", overrun.Message);
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
        var empty = new TickMonitor(new TickMonitorOptions { HangMs = 2000 }, _clock, WatchdogTestKit.NewRegistry(), log);
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
        Assert.Contains("; slowest tick body 130 ms: commands 0 ms (0 run), maps 2 ms; frame latency last 1 min: ", warning.Message);
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
        long bytes = WatchdogTestKit.AllocatedBy(() =>
        {
            monitor.OnTick(_clock.NowMicros);
            _clock.AdvanceMs(50);
        }, 100_000);
        Assert.Equal(0, bytes);
        Assert.Equal(0, WatchdogTestKit.AllocatedBy(() => monitor.OnTick(), 100_000));
    }

    [Fact]
    public void Check_WithNothingToReport_DoesNotAllocateBeyondTheFirstCall()
    {
        TickMonitor monitor = Create(o => o.SummaryIntervalSeconds = 0);
        Tick(monitor, 10, 50);
        // AllocatedBy warms up, then takes the least of three runs: a one-off runtime allocation on this thread (tiered JIT / OSR
        // type loading, a few KB once) lands in one run only, a per-call allocation in Check shows in every run.
        Assert.Equal(0, WatchdogTestKit.AllocatedBy(() => monitor.Check(_clock.NowMicros), 1000));
    }

    private sealed class FakeTickHangActuator : ITickHangActuator
    {
        public List<string> Calls { get; } = [];

        public void Stop(string reason) => Calls.Add("stop:" + reason);

        public void FailFast(string reason) => Calls.Add("failfast:" + reason);
    }

    private sealed class FakeHostLoadProbe : IHostLoadProbe
    {
        public HostLoadReading Current { get; set; }

        public HostLoadReading Read() => Current;
    }

    [Theory]
    [InlineData(TickHangAction.Stop, "stop:")]
    [InlineData(TickHangAction.FailFast, "failfast:")]
    public void ActionHang_FiresActuatorOncePerHungTick_AndResetsForNextTick(TickHangAction action, string expectedPrefix)
    {
        var actuator = new FakeTickHangActuator();
        var options = new TickMonitorOptions
        {
            HangMs = 2000,
            ActionHangMs = 10_000,
            Action = action,
        };
        var monitor = new TickMonitor(options, _clock, _counters, _log, actuator);
        monitor.HangDetail = _ => "phase=Maps, running=map 0 instance 0 for 12000 ms";
        monitor.StartTicking(50);

        Tick(monitor, 5, 50);
        monitor.OnTick(_clock.NowMicros);

        // At 3s: HangMs (2s) triggered, but below ActionHangMs (10s).
        _clock.AdvanceMs(3_000);
        monitor.Check(_clock.NowMicros);
        Assert.Empty(actuator.Calls);
        Assert.Empty(_log.Of(WatchdogEvents.TickAction));

        // At 12s: ActionHangMs (10s) exceeded -> fires once.
        _clock.AdvanceMs(9_000);
        monitor.Check(_clock.NowMicros);
        string call = Assert.Single(actuator.Calls);
        Assert.StartsWith(expectedPrefix, call, StringComparison.Ordinal);
        Assert.Contains("12000 ms", call, StringComparison.Ordinal);
        Assert.Contains("phase=Maps", call, StringComparison.Ordinal);
        LogLine actionLine = Assert.Single(_log.Of(WatchdogEvents.TickAction));
        Assert.Equal(LogLevel.Critical, actionLine.Level);
        Assert.Equal(1, monitor.ActionsFired);
        Assert.Equal(1, _counters.Find("watchdog.tick.actions")!.Value);

        // Still on the same hung tick at 20s: does not fire a second time.
        _clock.AdvanceMs(8_000);
        monitor.Check(_clock.NowMicros);
        Assert.Single(actuator.Calls);
        Assert.Equal(1, monitor.ActionsFired);

        // If the tick ever recovers and a subsequent tick hangs past ActionHangMs, it can fire again.
        monitor.OnTick(_clock.NowMicros);
        _clock.AdvanceMs(11_000);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(2, actuator.Calls.Count);
        Assert.Equal(2, monitor.ActionsFired);
    }

    [Fact]
    public void ActionHang_SuppressedWhenHostLoadBlamesHostStarvedOrGcPauses()
    {
        var actuator = new FakeTickHangActuator();
        var probe = new FakeHostLoadProbe
        {
            Current = new HostLoadReading(0, true, 0, 0, 0, 0, 0, 0, 12, 0, 24),
        };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, _clock, probe);
        sampler.Check(_clock.NowMicros);

        var options = new TickMonitorOptions
        {
            HangMs = 2000,
            ActionHangMs = 5000,
            Action = TickHangAction.FailFast,
        };
        var monitor = new TickMonitor(options, _clock, _counters, _log, actuator) { HostLoad = sampler };
        monitor.StartTicking(50);
        Tick(monitor, 2, 50);
        monitor.OnTick(_clock.NowMicros);

        // Advance 6s while machine is 100% busy with other processes (HostStarved).
        _clock.AdvanceMs(6_000);
        probe.Current = new HostLoadReading(1 * 10_000_000L, true, 144 * 10_000_000L, 144 * 10_000_000L, 0, 0, 0, 0, 12, 0, 24);
        monitor.Check(_clock.NowMicros);

        Assert.Empty(actuator.Calls);
        Assert.Equal(0, monitor.ActionsFired);
    }

    [Fact]
    public void ActionHang_SuppressedWhenHostLoadBlamesGcPaging()
    {
        var actuator = new FakeTickHangActuator();
        var probe = new FakeHostLoadProbe { Current = new HostLoadReading(0, true, 0, 0, 0, 0, 0, 0, 12, 0, 24) };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, _clock, probe);
        sampler.Check(_clock.NowMicros);

        var options = new TickMonitorOptions { HangMs = 2000, ActionHangMs = 5000, Action = TickHangAction.FailFast };
        var monitor = new TickMonitor(options, _clock, _counters, _log, actuator) { HostLoad = sampler };
        monitor.StartTicking(50);
        Tick(monitor, 2, 50);
        monitor.OnTick(_clock.NowMicros);

        // 6 s stuck in one GC pause (5.5 s) with the process idle and the machine at 95% memory load against a 90% threshold.
        const long Gib = 1024L * 1024 * 1024;
        _clock.AdvanceMs(6_000);
        probe.Current = new HostLoadReading(0, true, 1 * 10_000_000L, 144 * 10_000_000L, 0, 0, 1, 5_500 * 10_000L, 12, 0, 24,
            MemoryLoadBytes: 95 * Gib / 100 * 32, HighMemoryLoadThresholdBytes: 90 * Gib / 100 * 32, TotalAvailableMemoryBytes: 32 * Gib);
        Assert.Equal(HostLoadVerdict.GcPaging, sampler.Report(6_000_000)!.Value.Verdict);
        monitor.Check(_clock.NowMicros);

        Assert.Empty(actuator.Calls);
        Assert.Equal(0, monitor.ActionsFired);
    }

    private sealed class ThrowingHostLoadProbe : IHostLoadProbe
    {
        public HostLoadReading Current { get; set; }

        public bool Throw { get; set; }

        public HostLoadReading Read() => Throw ? throw new InvalidOperationException("probe failed") : Current;
    }

    private sealed class ToolRunner : IHangStackRunner
    {
        public Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
            => Task.FromResult((0, "Thread (0x1):\n  ArcaneCore.Game.Maps.WorldRuntime.RunTick\n"));
    }

    // A process using 0.2 cores on a 24 cpu machine at 45% load whose memory load (97%) is at the GC high-memory threshold, sampled once a second.
    private static HostLoadReading IdleUnderMemoryPressure(int seconds)
    {
        const long Gib = 1024L * 1024 * 1024;
        return new HostLoadReading(seconds * 2_000_000L, true, seconds * 108_000_000L, seconds * 240_000_000L, 0, 0, 0, 0, 12, 0, 24,
            MemoryLoadBytes: (long)(32 * Gib * 0.97), HighMemoryLoadThresholdBytes: (long)(32 * Gib * 0.97), TotalAvailableMemoryBytes: 32 * Gib);
    }

    [Fact]
    public void ActionHang_MemoryStarvedVerdict_OnlyDefersTheAction_AFarLongerHangStillFires()
    {
        var actuator = new FakeTickHangActuator();
        var probe = new FakeHostLoadProbe { Current = IdleUnderMemoryPressure(0) };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, _clock, probe);
        sampler.Check(_clock.NowMicros);

        var options = new TickMonitorOptions { HangMs = 2000, ActionHangMs = 5000, Action = TickHangAction.FailFast };
        var monitor = new TickMonitor(options, _clock, _counters, _log, actuator) { HostLoad = sampler };
        monitor.StartTicking(50);
        Tick(monitor, 2, 50);
        monitor.OnTick(_clock.NowMicros);

        for (int second = 1; second <= 7; second++)
        {
            _clock.AdvanceMs(1_000);
            probe.Current = IdleUnderMemoryPressure(second);
            sampler.Check(_clock.NowMicros);
        }

        Assert.Equal(HostLoadVerdict.MemoryStarved, sampler.Report(7_000_000)!.Value.Verdict);
        monitor.Check(_clock.NowMicros); // 7 s: past ActionHangMs (5 s), within 3x of it
        Assert.Empty(actuator.Calls);

        for (int second = 8; second <= 16; second++)
        {
            _clock.AdvanceMs(1_000);
            probe.Current = IdleUnderMemoryPressure(second);
            sampler.Check(_clock.NowMicros);
        }

        Assert.Equal(HostLoadVerdict.MemoryStarved, sampler.Report(16_000_000)!.Value.Verdict);
        monitor.Check(_clock.NowMicros); // 16 s: past 3 x ActionHangMs (15 s), still memory-starved
        Assert.Single(actuator.Calls);
        Assert.Equal(1, monitor.ActionsFired);
    }

    [Fact]
    public async Task AFaultingGcPauseProbe_IsSaidInTheCapture_NotShownAsAZeroPause()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-tickmonitor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = new ThrowingHostLoadProbe { Current = new HostLoadReading(0, true, 0, 0, 0, 0, 0, 0, 12, 0, 24) };
            var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, _clock, probe);
            sampler.Check(_clock.NowMicros);

            var options = new TickMonitorOptions { HangMs = 2000, StackCaptureTool = "C:/tools/dotnet-stack.exe", StackCaptureDirectory = directory, StackCaptureIntervalSeconds = 300 };
            var monitor = new TickMonitor(options, _clock, _counters, _log) { HostLoad = sampler };
            monitor.StackCapture = new HangStackCapture(options, _log, new ToolRunner(), fileExists: _ => true, processId: 4242);
            monitor.StartTicking(50);
            Tick(monitor, 2, 50);
            monitor.OnTick(_clock.NowMicros);

            _clock.AdvanceMs(3_000);
            probe.Throw = true;
            monitor.Check(_clock.NowMicros);
            await monitor.StackCapture.Last;

            string text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory)));
            Assert.Contains("GC pause unknown", text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void OnTick_ReturnsTheClosedFramesLength_AndZeroForTheFirstTick()
    {
        TickMonitor monitor = Create();

        Assert.Equal(0, monitor.OnTick(_clock.NowMicros));
        _clock.AdvanceMs(70);
        Assert.Equal(70_000, monitor.OnTick(_clock.NowMicros));
        _clock.AdvanceMs(5);
        Assert.Equal(5_000, monitor.OnTick(_clock.NowMicros));
    }

    [Fact]
    public void Hang_WithCompletedHangDetail_CallsItOncePerCompletedHangLine_AndCarriesItsText()
    {
        TickMonitor monitor = Create(o => o.HangMs = 1000);
        var closed = new List<long>();
        int calls = 0;
        // The window-wide detail still names an earlier 1.9 s body; it must not be what the completed-hang line carries.
        monitor.HangDetail = inProgress => inProgress ? "running" : "slowest tick body 1938 ms: features SyntheticSlowFeature";
        monitor.HangClosed = closed.Add;
        monitor.CompletedHangDetail = () =>
        {
            calls++;
            Assert.Equal(2, closed.Count); // both frames were recorded before the hang was reported
            return new CompletedHangText("hung frame 4000 ms: tick body 0 ms", 2);
        };

        Tick(monitor, 5, 50);
        Tick(monitor, 1, 1_900); // an earlier 1.9 s hang
        Tick(monitor, 1, 4_000); // the body-less 4 s hang
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);

        Assert.Equal([1_900_000L, 4_000_000L], closed);
        Assert.Equal(1, calls);
        LogLine done = _log.Of(WatchdogEvents.TickHang).Last();
        Assert.EndsWith("; hung frame 4000 ms: tick body 0 ms", done.Message);
        Assert.DoesNotContain("1938", done.Message);

        // Nothing new: no second line, no second call.
        monitor.Check(_clock.NowMicros);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AHangThatClosesBetweenTheCountReadAndTheDetail_IsReportedOnceWithItsOwnRecord()
    {
        TickMonitor monitor = Create(o => o.HangMs = 1000);
        var pending = new List<long>();
        var described = new List<int>();
        monitor.HangClosed = pending.Add;
        bool injected = false;
        monitor.CompletedHangDetail = () =>
        {
            if (!injected)
            {
                // Check has already read "1 hang completed"; a second frame closes now, before the host is asked.
                injected = true;
                Tick(monitor, 1, 2_000);
                monitor.OnTick(_clock.NowMicros);
            }

            int consumed = pending.Count;
            long worst = pending.Max();
            pending.Clear();
            described.Add(consumed);
            return new CompletedHangText($"worst {worst / 1000} ms", consumed);
        };

        Tick(monitor, 3, 50);
        Tick(monitor, 1, 1_500); // hang 1 closes at the next tick
        monitor.OnTick(_clock.NowMicros);
        monitor.Check(_clock.NowMicros);

        LogLine line = Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.Contains("2 new hang(s), 2 since start", line.Message);
        Assert.EndsWith("; worst 2000 ms", line.Message.Split("; host")[0]);
        Assert.Equal([2], described);
        Assert.Equal(2, monitor.HangsCompleted);

        // Nothing is left for the next check: no second line, no "no record" report.
        monitor.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.TickHang));
        Assert.Equal(2, _counters.Find("watchdog.tick.hangs")!.Value);
    }

    [Fact]
    public void Hang_WithoutCompletedHangDetail_FallsBackToHangDetail_AndAThrowingOneIsNamed()
    {
        TickMonitor fallback = Create(o => o.HangMs = 1000);
        fallback.HangDetail = inProgress => inProgress ? "running" : "slowest tick body 9000 ms: maps 3 ms";
        Tick(fallback, 3, 50);
        Tick(fallback, 1, 1_500);
        fallback.OnTick(_clock.NowMicros);
        fallback.Check(_clock.NowMicros);
        Assert.Contains("; slowest tick body 9000 ms: maps 3 ms", _log.Of(WatchdogEvents.TickHang).Last().Message);

        TickMonitor throwing = Create(o => o.HangMs = 1000);
        throwing.CompletedHangDetail = () => throw new InvalidOperationException("boom");
        throwing.HangClosed = _ => throw new InvalidOperationException("recorder boom");
        Tick(throwing, 3, 50);
        Tick(throwing, 1, 1_500);
        throwing.OnTick(_clock.NowMicros); // the throwing recorder must not stop the hang being counted
        Assert.Equal(1, throwing.HangsCompleted);
        throwing.Check(_clock.NowMicros);
        Assert.Contains("hang detail unavailable (InvalidOperationException)", _log.Of(WatchdogEvents.TickHang).Last().Message);
    }
}
