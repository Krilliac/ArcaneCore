using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Watchdog;
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
}
