using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Ops.Watchdog;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.HotCode;
using ArcaneCore.World.Tests.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The world-side feed of the Kernel tick monitor (docs/ops/watchdog.md) on a real world thread.</summary>
public sealed class TickWatchdogFeatureTests
{
    private sealed class ManualClock : WatchdogClock
    {
        private long _micros = 1_000_000;

        public override long NowMicros => Volatile.Read(ref _micros);

        public void AdvanceMs(long ms) => Interlocked.Add(ref _micros, ms * 1000);
    }

    private static WatchdogOptions Options(Action<TickMonitorOptions>? configure = null)
    {
        var options = new WatchdogOptions();
        configure?.Invoke(options.TickMonitor);
        return options;
    }

    private static void Register(IServiceCollection services, WatchdogOptions options, WatchdogClock? clock = null)
    {
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(new CounterRegistry());
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddTickWatchdog();
    }

    [Fact]
    public void FeatureIsDiscovered_AndAttachesNothingWithoutTheWatchdog()
    {
        Assert.Contains(typeof(TickWatchdogFeature), WorldFeatures.FeatureTypes);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var feature = new TickWatchdogFeature(provider, NullLogger<TickWatchdogFeature>.Instance);
        Assert.Null(feature.Monitor);
    }

    [Fact]
    public async Task OnTheRealWorldThread_EveryTickIsRecorded_FrameTimesMatchTheInterval_AndTheHeartbeatIsAlive()
    {
        // A real world thread on a loaded machine can be descheduled for seconds (the full suite saw two 2 s "hangs" at the default
        // threshold). The hang threshold is therefore a bound no healthy run reaches, so HangsCompleted == 0 still proves that ordinary
        // frames are not counted as hangs; the hang path itself is covered on the fake clock below.
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.HangMs = 60_000)));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        var feature = host.WorldServices.GetRequiredService<TickWatchdogFeature>();
        Assert.Same(monitor, feature.Monitor);
        Assert.Equal(10_000, monitor.BudgetMicros); // twice the 5 ms test tick
        Assert.NotNull(monitor.BodyStats);

        await WorldTestHost.WaitForAsync(() => monitor.Ring.Count >= 50, "fifty frames");
        RingStats stats = monitor.Stats(out long[] buffer);
        System.Buffers.ArrayPool<long>.Shared.Return(buffer);
        Assert.True(stats.Samples >= 50);
        // The 5 ms test tick is shorter than a Windows wait's ~15.6 ms granularity, so the drift-compensated loop (WorldTickScheduler, like
        // vmangos WorldRunnable's "no sleep after an overrun") alternates an oversleeping wait (a ~15.6 ms frame) with an immediate tick
        // (a ~0.1 ms frame). The mean is the tick rate (~7.8 ms on the coarse timer, 5 ms on a fine one) and is never below the interval:
        // the loop does not tick early. Load only lengthens frames, and one descheduled frame of seconds moves the mean of fifty past any
        // fixed ceiling, so the ceiling is on the median instead: whichever side of the even split it lands on, a few stalled frames
        // cannot move it, and only a loop that no longer ticks at its interval puts it past 60 ms.
        Assert.True(stats.Mean >= 4_000, $"mean frame {stats.Mean} us is shorter than the 5 ms interval allows");
        Assert.InRange(stats.P50, 0, 60_000);
        Assert.True(monitor.IsAlive(WatchdogClock.System.NowMicros, out _));
        Assert.Equal(0, monitor.HangsCompleted);

        // The body statistics come from the runtime's own recorder.
        TickBodySummary body = monitor.BodyStats!();
        Assert.True(body.Samples > 0);
        Assert.True(body.MaxMicros >= body.P50Micros);
    }

    [Fact]
    public async Task ATickThatBlocksPastTheHangThreshold_IsReportedAndWithholdsLiveness_ThenRecovers()
    {
        var clock = new ManualClock();
        WatchdogOptions options = Options(o =>
        {
            o.HangMs = 100;
            o.BudgetMs = 50;
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, options, clock));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        await WorldTestHost.WaitForAsync(() => monitor.Ring.Count >= 5, "frames");

        // Block the world thread: the fake clock jumps 300 ms while the tick is in progress.
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        host.World.Post(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        // The blocked tick started at (or before) the current fake time; move the clock past the hang threshold.
        clock.AdvanceMs(300);
        monitor.Check(clock.NowMicros);
        Assert.False(monitor.IsAlive(clock.NowMicros, out string? reason));
        Assert.Contains("hang threshold 100 ms", reason);

        release.Set();
        await WorldTestHost.WaitForAsync(() => monitor.HangsCompleted >= 1, "the completed hang");
        monitor.Check(clock.NowMicros);
        Assert.True(monitor.IsAlive(clock.NowMicros, out _));
        Assert.True(monitor.Overruns >= 1);
    }

    [Fact]
    public async Task AHang_IsReportedToBotQaOnce_ByPhaseAndWork_NotByDuration_AndItsCompletionIsNotCountedTwice()
    {
        var clock = new ManualClock();
        WatchdogOptions options = Options(o =>
        {
            o.HangMs = 100;
            o.BudgetMs = 50;
        });
        var bus = new PlayerbotQaSignalBus(64);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s =>
        {
            Register(s, options, clock);
            s.AddSingleton<IPlayerbotQaSink>(new PlayerbotQaSink(bus));
        });
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        await WorldTestHost.WaitForAsync(() => monitor.Ring.Count >= 5, "frames");

        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        host.World.Post(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        clock.AdvanceMs(300);
        monitor.Check(clock.NowMicros);

        var first = new List<QaSignal>();
        bus.Drain(first, 16);
        QaSignal running = Assert.Single(first);
        Assert.Equal(QaSignalKind.TickHang, running.Kind);
        Assert.Equal(nameof(TickPhase.Commands), running.Stage);
        Assert.False(string.IsNullOrEmpty(running.Reason));
        Assert.Null(running.BotKey);
        Assert.StartsWith("v1|tickhang|phase=Commands|item=", PlayerbotQaFingerprint.Key(running), StringComparison.Ordinal);

        // The same hang, a second later and longer, is the same finding; and once it completes, the completion is not a second occurrence of it.
        clock.AdvanceMs(60_000);
        monitor.Check(clock.NowMicros);
        var again = new List<QaSignal>();
        bus.Drain(again, 16);
        Assert.Empty(again);

        release.Set();
        await WorldTestHost.WaitForAsync(() => monitor.HangsCompleted >= 1, "the completed hang");
        monitor.Check(clock.NowMicros);
        var done = new List<QaSignal>();
        bus.Drain(done, 16);
        Assert.Empty(done);

        var aggregator = new PlayerbotQaAggregator(100, 5);
        Assert.NotNull(aggregator.Ingest(running, new FakeFacts(), QaData.T0));
        QaFindingView view = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaSignalKind.TickHang, view.Kind);
        Assert.True(view.BotIndependent);
    }

    [Fact]
    public async Task StoppingTheHost_StopsTicking_SoLivenessNoLongerDependsOnTheWorldThread()
    {
        var clock = new ManualClock();
        WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.HangMs = 100), clock));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        await WorldTestHost.WaitForAsync(() => monitor.Ring.Count >= 2, "frames");
        await host.DisposeAsync();
        clock.AdvanceMs(10_000);
        Assert.True(monitor.IsAlive(clock.NowMicros, out _));
    }

    [Fact]
    public async Task Disabled_AttachesNoHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.Enabled = false)));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        await Task.Delay(100);
        Assert.Equal(0, monitor.Ring.Count);
        Assert.Null(host.WorldServices.GetRequiredService<TickWatchdogFeature>().Monitor);
    }

    /// <summary>A world feature that stalls the tick; the watchdog must name it by its class.</summary>
    private sealed class SyntheticSlowFeature(int sleepMs)
    {
        public void OnUpdate(uint diffMs) => Thread.Sleep(sleepMs);
    }

    /// <summary>A world feature that holds the tick until released.</summary>
    private sealed class BlockingFeature(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        public void OnUpdate(uint diffMs)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task ASyntheticSlowFeature_AppearsByNameInTheHangLog_WithThePhaseSplitAndSaveQueue()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.HangMs = 60_000)));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        Assert.NotNull(monitor.HangDetail);

        var slow = new SyntheticSlowFeature(80);
        host.World.Updated += slow.OnUpdate;
        await WorldTestHost.WaitForAsync(() => host.World.Stats.SlowTicks(TimeSpan.FromSeconds(60)).Count > 0, "a recorded slow tick");
        host.World.Updated -= slow.OnUpdate;

        string detail = monitor.HangDetail!(false);
        Assert.Contains("slowest tick body", detail);
        Assert.Contains("top features: ", detail);
        Assert.Contains("SyntheticSlowFeature", detail);
        Assert.Contains("commands ", detail);
        Assert.Contains("world-tick handlers ", detail);
        Assert.Contains("maps ", detail);
        Assert.Contains("autosave ", detail);
        Assert.Contains("save queue depth ", detail);
        Assert.Contains("oldest pending ", detail);

        // The slowest recorded body is at least the 80 ms the feature slept.
        SlowTickInfo worst = host.World.Stats.SlowTicks(TimeSpan.FromSeconds(60))[0];
        Assert.True(worst.DurationMicros >= 70_000, $"slow tick {worst.DurationMicros} us");
        Assert.Equal("SyntheticSlowFeature", worst.TopFeatures[0].Name);
        Assert.True(worst.TopFeatures[0].Micros >= 70_000);
    }

    [Fact]
    public async Task AHangInProgress_NamesThePhaseAndTheRunningFeature()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.HangMs = 60_000)));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blocking = new BlockingFeature(entered, release);
        host.World.Updated += blocking.OnUpdate;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            TickPosition position = host.World.CurrentPosition;
            Assert.Equal(TickPhase.Features, position.Phase);
            Assert.Equal("BlockingFeature", position.DescribeWork());

            string detail = monitor.HangDetail!(true);
            Assert.Contains("phase=Features", detail);
            Assert.Contains("running=BlockingFeature", detail);
            Assert.Contains(" ms; save queue depth ", detail);
        }
        finally
        {
            host.World.Updated -= blocking.OnUpdate;
            release.Set();
        }

        // Released: the world thread goes back to waiting between ticks.
        await WorldTestHost.WaitForAsync(() => host.World.CurrentPosition.Phase == TickPhase.Idle, "the world thread idle between ticks");
        Assert.Equal("(nothing)", host.World.CurrentPosition.DescribeWork());
    }

    [Fact]
    public async Task AQuietWorld_ReportsThatNoTickBodyWasRecorded()
    {
        // A slow tick is a tick body over the interval: the test host's 5 ms interval lets one startup tick on a loaded machine count
        // (seen on the CI node), so this world ticks once a minute, which no tick body here comes near.
        await using WorldTestHost host = WorldTestHost.Start(
            configure: o => o.TickIntervalMs = 60_000, configureServices: s => Register(s, Options(o => o.HangMs = 60_000)));
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        Assert.Empty(host.World.Stats.SlowTicks(TimeSpan.FromSeconds(60)));
        Assert.Contains("the stall was outside the tick body", monitor.HangDetail!(false));
    }

    /// <summary>Forwards to the real announcer and remembers what the feature asked for (the delivery alone does not carry severity or key).</summary>
    private sealed class RecordingAnnouncer(IOpsAnnouncer inner) : IOpsAnnouncer
    {
        public sealed record Call(AnnounceCategory Category, AnnounceSeverity Severity, string Text, bool GmOnly, string? DedupKey);

        private readonly List<Call> _calls = [];

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Announce(AnnounceCategory category, AnnounceSeverity severity, string shortText, string? gmDetail = null, IReadOnlyList<AnnounceField>? fields = null,
            string? shortTextWithNames = null, bool gmOnly = false, string? dedupKey = null, bool staffHandled = false)
        {
            lock (_calls)
            {
                _calls.Add(new Call(category, severity, shortText, gmOnly, dedupKey));
            }

            inner.Announce(category, severity, shortText, gmDetail, fields, shortTextWithNames, gmOnly, dedupKey, staffHandled);
        }
    }

    [Fact]
    public async Task WithAnAnnouncer_AHangInProgress_ItsCompletion_AndAnOverrun_ReachStaffOnly_EachWithItsOwnDedupKey()
    {
        var clock = new ManualClock();
        var sink = new RecordingSink();
        // The announcer's clock never moves, so the 300 s Faults dedup window stays open for the whole test: a repeated key is suppressed.
        var announcer = new RecordingAnnouncer(new OpsAnnouncer(new AnnounceOptions(), sink, NullLogger.Instance, () => 5_000_000));
        WatchdogOptions options = Options(o =>
        {
            o.HangMs = 100;
            o.BudgetMs = 50;
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s =>
        {
            Register(s, options, clock);
            s.AddSingleton<IOpsAnnouncer>(announcer);
        });
        TickMonitor monitor = host.WorldServices.GetRequiredService<TickMonitor>();
        Assert.NotNull(monitor.Alerted);
        await WorldTestHost.WaitForAsync(() => monitor.Ring.Count >= 5, "frames");

        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        host.World.Post(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        clock.AdvanceMs(300);
        monitor.Check(clock.NowMicros); // the tick is still running past the hang threshold

        RecordingAnnouncer.Call running = Assert.Single(announcer.Calls);
        Assert.Equal(AnnounceCategory.Faults, running.Category);
        Assert.Equal(AnnounceSeverity.Critical, running.Severity);
        Assert.True(running.GmOnly);
        Assert.Contains("is hung", running.Text);
        Assert.Equal("tick-Hang-running", running.DedupKey);

        release.Set();
        await WorldTestHost.WaitForAsync(() => monitor.HangsCompleted >= 1, "the completed hang");
        monitor.Check(clock.NowMicros); // the hang completed (and, being over the budget, is an overrun too)

        // The completed hang is not swallowed by the "is hung" line of the same window, and the overrun is a Warning of its own.
        Assert.Equal(3, announcer.Calls.Count);
        RecordingAnnouncer.Call done = Assert.Single(announcer.Calls, c => c.DedupKey == "tick-Hang-done");
        Assert.Equal(AnnounceSeverity.Critical, done.Severity);
        Assert.True(done.GmOnly);
        Assert.Contains("stalled", done.Text);
        RecordingAnnouncer.Call overrun = Assert.Single(announcer.Calls, c => c.DedupKey == "tick-Overrun");
        Assert.Equal(AnnounceSeverity.Warning, overrun.Severity);
        Assert.True(overrun.GmOnly);

        // Delivered to staff with the category tag, and to nobody else.
        Assert.Equal(3, sink.Sent.Count);
        Assert.All(sink.Sent, delivery =>
        {
            Assert.Contains("[Faults]", delivery.StaffChat);
            Assert.Null(delivery.WorldChat);
        });
        Assert.Contains("is hung", sink.Sent[0].StaffChat);
    }

    [Fact]
    public async Task WithoutAnAnnouncer_TheMonitorHasNoAlertSubscriber()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => Register(s, Options(o => o.HangMs = 60_000)));
        Assert.Null(host.WorldServices.GetRequiredService<TickMonitor>().Alerted);
    }
}
