using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

public sealed class MemoryMonitorTests
{
    private const long Mib = 1024 * 1024;

    private readonly FakeClock _clock = new();
    private readonly RecordingLogger<MemoryMonitor> _log = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeActuator _actuator = new();

    private sealed class FakeProbe : IMemoryProbe
    {
        public MemorySample Sample { get; set; } = new(100 * Mib, 40 * Mib, 10 * Mib, 1 * Mib, 5 * Mib, 2000 * Mib, 8000 * Mib, 7200 * Mib, 3, 1.5, 300 * Mib);

        public MemorySample Read() => Sample;
    }

    private sealed class FakeActuator : IMemoryPressureActuator
    {
        public int Collects { get; private set; }

        public int Stops { get; private set; }

        public void Collect() => Collects++;

        public void Stop() => Stops++;
    }

    private MemoryMonitor Create(Action<MemoryMonitorOptions>? configure = null)
    {
        var options = new MemoryMonitorOptions { SampleIntervalSeconds = 10 };
        configure?.Invoke(options);
        return new MemoryMonitor(options, _clock, WatchdogTestSupport.NewRegistry(), _probe, _actuator, _log);
    }

    private void Grow(long gen2Mib, long lohMib, long heapMib)
        => _probe.Sample = _probe.Sample with { Gen2Bytes = gen2Mib * Mib, LohBytes = lohMib * Mib, HeapBytes = heapMib * Mib };

    [Fact]
    public void Check_SamplesOnTheInterval_AndTracksHighWaterMarks()
    {
        MemoryMonitor monitor = Create();
        monitor.Check(_clock.NowMicros);
        Assert.Equal(100 * Mib, monitor.HeapHighWaterBytes);
        Grow(60, 10, 150);
        _clock.AdvanceMs(5_000);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(100 * Mib, monitor.HeapHighWaterBytes); // not sampled yet
        _clock.AdvanceMs(5_000);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(150 * Mib, monitor.HeapHighWaterBytes);
        Assert.Equal(60 * Mib, monitor.Gen2HighWaterBytes);
        Grow(30, 10, 80);
        _clock.AdvanceMs(10_000);
        monitor.Check(_clock.NowMicros);
        Assert.Equal(60 * Mib, monitor.Gen2HighWaterBytes); // marks never fall
        Assert.Equal(80 * Mib, monitor.LastSample.HeapBytes);
    }

    [Fact]
    public void GrowthLine_AppearsWhenAMarkRisesByTheThreshold_NotForSmallerSteps()
    {
        MemoryMonitor monitor = Create(o => o.GrowthLogBytes = 16 * Mib);
        monitor.Sample(_clock.NowMicros);
        LogLine first = Assert.Single(_log.Of(WatchdogEvents.MemoryGrowth)); // the first sample is 40 MiB above 0
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.Contains("gen2 40 MiB, LOH 10 MiB, heap 100 MiB", first.Message);

        Grow(50, 10, 110); // +10 MiB gen2: below the threshold
        monitor.Sample(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.MemoryGrowth));

        Grow(56, 10, 120); // +16 MiB since the last line
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.MemoryGrowth));

        Grow(56, 30, 140); // LOH grew by 20 MiB
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(3, _log.CountOf(WatchdogEvents.MemoryGrowth));
        Assert.Contains("LOH 30 MiB", _log.Of(WatchdogEvents.MemoryGrowth).Last().Message);

        MemoryMonitor silent = Create(o => o.GrowthLogBytes = 0);
        Grow(500, 500, 1000);
        silent.Sample(_clock.NowMicros);
        Assert.Equal(3, _log.CountOf(WatchdogEvents.MemoryGrowth));
    }

    [Fact]
    public void HeapWarning_IsRateLimited_AndCarriesTheSuppressedCount()
    {
        MemoryMonitor monitor = Create(o =>
        {
            o.WarnHeapBytes = 120 * Mib;
            o.WarnIntervalSeconds = 60;
            o.GrowthLogBytes = 0;
        });
        monitor.Sample(_clock.NowMicros);
        Assert.Empty(_log.Of(WatchdogEvents.MemoryPressure));
        Grow(40, 10, 130);
        for (int i = 0; i < 5; i++)
        {
            monitor.Sample(_clock.NowMicros);
            _clock.AdvanceMs(10_000);
        }

        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.MemoryPressure));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("managed heap is 130 MiB (warning threshold 120 MiB", warning.Message);
        _clock.AdvanceMs(60_000);
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(2, _log.CountOf(WatchdogEvents.MemoryPressure));
        Assert.Contains("4 suppressed", _log.Of(WatchdogEvents.MemoryPressure).Last().Message);
    }

    [Fact]
    public void LoadWarning_FiresAtThePercentage_AndIsOffAtZero()
    {
        MemoryMonitor monitor = Create(o =>
        {
            o.WarnLoadPercent = 85;
            o.GrowthLogBytes = 0;
        });
        monitor.Sample(_clock.NowMicros); // 2000 of 8000 MiB = 25%
        Assert.Empty(_log.Of(WatchdogEvents.MemoryPressure));
        _probe.Sample = _probe.Sample with { MemoryLoadBytes = 6900 * Mib };
        monitor.Sample(_clock.NowMicros);
        LogLine warning = Assert.Single(_log.Of(WatchdogEvents.MemoryPressure));
        Assert.Contains("memory load is 86% of 8000 MiB (warning at 85%; the GC's high-load threshold is 7200 MiB", warning.Message);

        MemoryMonitor off = Create(o =>
        {
            o.WarnLoadPercent = 0;
            o.GrowthLogBytes = 0;
        });
        off.Sample(_clock.NowMicros);
        Assert.Single(_log.Of(WatchdogEvents.MemoryPressure));
    }

    [Fact]
    public void Action_Collect_FiresAtTheThreshold_WithACooldown_AndLogsCritical()
    {
        MemoryMonitor monitor = Create(o =>
        {
            o.Action = MemoryPressureAction.Collect;
            o.ActionHeapBytes = 150 * Mib;
            o.ActionCooldownSeconds = 300;
            o.GrowthLogBytes = 0;
        });
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(0, _actuator.Collects);
        Grow(40, 10, 160);
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(1, _actuator.Collects);
        Assert.Equal(1, monitor.ActionsFired);
        LogLine critical = _log.Of(WatchdogEvents.MemoryAction).First();
        Assert.Equal(LogLevel.Critical, critical.Level);
        Assert.Contains("160 MiB reached the action threshold 150 MiB: Collect", critical.Message);
        Assert.Contains(_log.Of(WatchdogEvents.MemoryAction), l => l.Message.Contains("forced compacting gen2 collection", StringComparison.Ordinal));

        _clock.AdvanceMs(299_000);
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(1, _actuator.Collects);
        _clock.AdvanceMs(1_000);
        monitor.Sample(_clock.NowMicros);
        Assert.Equal(2, _actuator.Collects);
    }

    [Fact]
    public void Action_Stop_FiresOnce_AndLogAndNoneNeverTouchTheActuator()
    {
        MemoryMonitor stop = Create(o =>
        {
            o.Action = MemoryPressureAction.Stop;
            o.ActionHeapBytes = 50 * Mib;
            o.ActionCooldownSeconds = 1;
        });
        stop.Sample(_clock.NowMicros);
        _clock.AdvanceMs(5_000);
        stop.Sample(_clock.NowMicros);
        Assert.Equal(1, _actuator.Stops);
        Assert.Equal(1, stop.ActionsFired);

        MemoryMonitor log = Create(o =>
        {
            o.Action = MemoryPressureAction.Log;
            o.ActionHeapBytes = 50 * Mib;
        });
        log.Sample(_clock.NowMicros);
        Assert.Equal(1, _actuator.Collects + _actuator.Stops);
        Assert.Contains(_log.Of(WatchdogEvents.MemoryAction), l => l.Message.Contains(": Log", StringComparison.Ordinal));

        MemoryMonitor none = Create(o =>
        {
            o.Action = MemoryPressureAction.None;
            o.ActionHeapBytes = 50 * Mib;
        });
        none.Sample(_clock.NowMicros);
        Assert.Equal(0, none.ActionsFired);

        // A threshold of 0 disables the action whatever the mode says.
        MemoryMonitor zero = Create(o =>
        {
            o.Action = MemoryPressureAction.Stop;
            o.ActionHeapBytes = 0;
        });
        zero.Sample(_clock.NowMicros);
        Assert.Equal(1, _actuator.Stops);
    }

    [Fact]
    public void Disabled_NeverSamples()
    {
        MemoryMonitor monitor = Create(o => o.Enabled = false);
        monitor.Start();
        monitor.Check(_clock.NowMicros);
        _clock.AdvanceMs(100_000);
        monitor.Check(_clock.NowMicros);
        monitor.Stop();
        Assert.Equal(0, monitor.HeapHighWaterBytes);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void RealProbe_ReadsTheHeap_AndFullGcNotificationStartup_EitherRegistersOrLogsTheRefusal()
    {
        MemorySample sample = new GcMemoryProbe().Read();
        Assert.True(sample.HeapBytes > 0);
        Assert.True(sample.WorkingSetBytes > 0);
        Assert.True(sample.Gen2Collections >= 0);

        var options = new MemoryMonitorOptions { FullGcNotifications = true };
        var monitor = new MemoryMonitor(options, _clock, WatchdogTestSupport.NewRegistry(), new GcMemoryProbe(), _actuator, _log);
        monitor.Start();
        try
        {
            // When the runtime accepted the registration, the notification thread may already have logged an approach as well.
            LogLine line = _log.Of(WatchdogEvents.FullGc).First();
            if (monitor.FullGcNotificationsActive)
            {
                Assert.Contains("registered", line.Message);
            }
            else
            {
                Assert.Contains("not available", line.Message);
            }
        }
        finally
        {
            monitor.Stop();
        }
    }
}
