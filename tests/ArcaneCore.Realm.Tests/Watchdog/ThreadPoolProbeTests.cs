using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class ThreadPoolProbeTests
{
    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<ThreadPoolProbe> _log = new();

    private ThreadPoolProbe Create(Action<IThreadPoolWorkItem>? queue, Action<ThreadPoolProbeOptions>? configure = null)
    {
        var options = new ThreadPoolProbeOptions { ProbeIntervalSeconds = 5, WarnDelayMs = 200, CriticalDelayMs = 2000, WarnIntervalSeconds = 30 };
        configure?.Invoke(options);
        return new ThreadPoolProbe(options, _clock, WatchdogTestSupport.NewRegistry(), _log, queue);
    }

    [Fact]
    public async Task OnTheRealPool_TheProbeRuns_AndAPromptArrivalIsNotAStarvation()
    {
        ThreadPoolProbe probe = Create(queue: null);
        probe.Check(_clock.NowMicros); // first probe goes out
        Assert.True(probe.Outstanding || probe.LastDelayMicros >= 0);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.Outstanding && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.False(probe.Outstanding);
        _clock.AdvanceMs(10); // the fake clock did not move while the pool ran it: a 10 ms delay
        probe.Check(_clock.NowMicros);
        Assert.Equal(0, probe.Starvations);
        Assert.Empty(_log.Lines);
        Assert.True(probe.LastDelayMicros >= 0);
    }

    [Fact]
    public void AProbeThatNeverRuns_IsCriticalOnce_AndNoSecondProbeIsQueuedMeanwhile()
    {
        int queued = 0;
        ThreadPoolProbe probe = Create(_ => queued++);
        probe.Check(_clock.NowMicros);
        Assert.Equal(1, queued);
        _clock.AdvanceMs(1_999);
        probe.Check(_clock.NowMicros);
        Assert.Empty(_log.Lines);
        _clock.AdvanceMs(2);
        probe.Check(_clock.NowMicros);
        LogLine critical = Assert.Single(_log.Of(WatchdogEvents.ThreadPoolStarvation));
        Assert.Equal(LogLevel.Critical, critical.Level);
        Assert.Contains("has not run for 2001 ms", critical.Message);
        Assert.Equal(1, probe.Starvations);
        _clock.AdvanceMs(60_000);
        probe.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.ThreadPoolStarvation));
        Assert.Equal(1, queued); // one outstanding probe at a time
        Assert.True(probe.Outstanding);
    }

    [Fact]
    public void ALateArrival_IsAWarning_RateLimited_AndAVeryLateOneIsCriticalWithoutDoubleCounting()
    {
        IThreadPoolWorkItem? pending = null;
        ThreadPoolProbe probe = Create(item => pending = item);

        // Warning: arrives after 300 ms.
        probe.Check(_clock.NowMicros);
        _clock.AdvanceMs(300);
        pending!.Execute();
        probe.Check(_clock.NowMicros);
        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.ThreadPoolStarvation));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("waited 300 ms for a thread (warning delay 200 ms)", warning.Message);
        Assert.Equal(300_000, probe.LastDelayMicros);
        Assert.Equal(1, probe.Starvations);

        // Second late probe inside the warn interval: counted, not logged.
        _clock.AdvanceMs(5_000);
        probe.Check(_clock.NowMicros); // queues the next probe
        _clock.AdvanceMs(500);
        pending.Execute();
        probe.Check(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.ThreadPoolStarvation));
        Assert.Equal(2, probe.Starvations);

        // Very late: the "has not run" critical fires while waiting, then the arrival is critical too but counted once.
        _clock.AdvanceMs(5_000);
        probe.Check(_clock.NowMicros);
        _clock.AdvanceMs(2_500);
        probe.Check(_clock.NowMicros);
        Assert.Equal(3, probe.Starvations);
        pending.Execute();
        probe.Check(_clock.NowMicros);
        Assert.Equal(3, probe.Starvations);
        Assert.Equal(2, _log.Of(WatchdogEvents.ThreadPoolStarvation).Count(l => l.Level == LogLevel.Critical));
        Assert.Contains("waited 2500 ms for a thread (critical delay 2000 ms)", _log.Lines.Last().Message);
    }

    [Fact]
    public void Disabled_QueuesNothing()
    {
        int queued = 0;
        ThreadPoolProbe probe = Create(_ => queued++, o => o.Enabled = false);
        probe.Check(_clock.NowMicros);
        _clock.AdvanceMs(60_000);
        probe.Check(_clock.NowMicros);
        Assert.Equal(0, queued);
    }

    [Fact]
    public void Execute_DoesNotAllocate()
    {
        ThreadPoolProbe probe = Create(_ => { });
        probe.Probe(_clock.NowMicros);
        Assert.Equal(0, WatchdogTestSupport.AllocatedBy(probe.Execute, 100_000));
    }
}
