using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

/// <summary>The host-load sampler and its use in the TickMonitor hang line (docs/ops/observability.md).</summary>
public sealed class HostLoadSamplerTests
{
    private const long Second = 1_000_000;

    private sealed class FakeProbe : IHostLoadProbe
    {
        public HostLoadReading Current { get; set; }

        public HostLoadReading Read() => Current;
    }

    private static HostLoadReading Reading(long procSeconds, long busySeconds, long totalSeconds, long pauseMs = 0, int g0 = 0, int g1 = 0, int g2 = 0)
        => new(procSeconds * 10_000_000L, true, busySeconds * 10_000_000L, totalSeconds * 10_000_000L, g0, g1, g2, pauseMs * 10_000L, 12, 3, 24);

    [Fact]
    public void BusyMachineWithLittleProcessCpu_IsHostStarved()
    {
        // 4 s window on 24 cpus: machine 96 cpu-seconds of 96 busy, process 4 cpu-seconds (4%).
        HostLoadReport r = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(4, 96, 96));
        Assert.Equal(HostLoadVerdict.HostStarved, r.Verdict);
        Assert.InRange(r.ProcessCpuPercent, 3.9, 4.2);
        Assert.InRange(r.MachineCpuPercent, 99, 100);
        Assert.InRange(r.OtherCpuPercent, 90, 100);
        Assert.Contains("verdict host-starved", r.Format());
    }

    [Fact]
    public void QuietMachineWithSlowTick_IsHostQuiet_AndGcShareWins()
    {
        HostLoadReport quiet = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(4, 9, 96));
        Assert.Equal(HostLoadVerdict.Quiet, quiet.Verdict);

        HostLoadReport gc = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(4, 9, 96, pauseMs: 2500, g0: 3, g1: 1, g2: 1));
        Assert.Equal(HostLoadVerdict.GcPauses, gc.Verdict);
        Assert.Equal(2_500_000, gc.GcPauseMicros);
        Assert.Contains("gc +3/+1/+1", gc.Format());
        Assert.Contains("pause 2500 ms", gc.Format());
    }

    [Fact]
    public void BusyProcess_IsProcessBusy_AndUnknownMachineIsSaid()
    {
        HostLoadReport r = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(80, 85, 96));
        Assert.Equal(HostLoadVerdict.ProcessBusy, r.Verdict);

        HostLoadReading none = default;
        HostLoadReport unknown = HostLoadSampler.Compute(0, none with { Cpus = 4 }, 4 * Second, none with { Cpus = 4 });
        Assert.Contains("machine cpu unknown", unknown.Format());
    }

    [Fact]
    public void Report_DifferencesAgainstTheNewestSampleOldEnough_AndTheHangLineCarriesIt()
    {
        var clock = new FakeClock();
        var probe = new FakeProbe { Current = Reading(0, 0, 0) };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, clock, probe);
        sampler.Check(clock.NowMicros);
        clock.AdvanceMs(1000);
        probe.Current = Reading(1, 24, 24);
        sampler.Check(clock.NowMicros);
        clock.AdvanceMs(3000);
        probe.Current = Reading(5, 120, 120);

        HostLoadReport report = sampler.Report(3500 * 1000)!.Value;
        Assert.Equal(4, report.WindowMicros / Second); // baseline is the sample 4 s ago, the newest one that is old enough is 4 s back
        Assert.Equal(HostLoadVerdict.HostStarved, report.Verdict);

        var log = new RecordingLogger<TickMonitor>();
        var monitor = new TickMonitor(new TickMonitorOptions { HangMs = 2000 }, clock, WatchdogTestKit.NewRegistry(), log) { HostLoad = sampler };
        monitor.StartTicking(50);
        monitor.OnTick(clock.NowMicros);
        clock.AdvanceMs(3000);
        probe.Current = Reading(6, 200, 200);
        monitor.Check(clock.NowMicros);
        LogLine hang = Assert.Single(log.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Critical);
        Assert.Contains("host: process cpu", hang.Message);
        Assert.Contains("verdict host-starved", hang.Message);
    }

    [Fact]
    public void WatchdogThreadStoppedWithoutGcOrCpu_IsProcessStalled_NotHostQuiet()
    {
        // Live 2026-10-17 00:07 (issues #744/#755): an 8.2 s tick; the next slow-map line read "process cpu 0% / machine cpu 2% over 6.2 s;
        // gc pause 0 ms; verdict host-quiet (the time went in the code)". 465 players at 0% cpu is a stopped process, not slow code: the
        // watchdog's own once-a-second sample was 6.2 s old. The line must say the process was stalled, never blame the code.
        var clock = new FakeClock();
        var probe = new FakeProbe { Current = Reading(0, 0, 0) };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, clock, probe);
        for (int i = 0; i < 3; i++)
        {
            sampler.Check(clock.NowMicros);
            clock.AdvanceMs(1000);
        }

        sampler.Check(clock.NowMicros); // 3 s, then nothing for 6.2 s
        clock.AdvanceMs(6200);
        probe.Current = Reading(0, 4, 221, pauseMs: 0);

        HostLoadReport stalled = sampler.Report(60 * 1000)!.Value;
        Assert.NotEqual(HostLoadVerdict.Quiet, stalled.Verdict);
        Assert.Equal(HostLoadVerdict.ProcessStalled, stalled.Verdict);
        Assert.Equal(5200, stalled.StallMicros / 1000); // 6.2 s without a sample, less the 1 s the sampler waits anyway
        Assert.Contains("verdict process-stalled", stalled.Format());
        Assert.DoesNotContain("host-quiet", stalled.Format());

        // The same gap covered by GC pauses is the collector's: the GC verdict stays.
        probe.Current = Reading(0, 4, 221, pauseMs: 5900);
        Assert.Equal(HostLoadVerdict.GcPauses, sampler.Report(60 * 1000)!.Value.Verdict);
    }

    [Fact]
    public void RegularSamples_AreNoStall()
    {
        var clock = new FakeClock();
        var probe = new FakeProbe { Current = Reading(0, 0, 0) };
        var sampler = new HostLoadSampler(new HostLoadOptions(), 1000, clock, probe);
        for (int i = 0; i < 6; i++)
        {
            sampler.Check(clock.NowMicros);
            clock.AdvanceMs(1000);
        }

        probe.Current = Reading(4, 9, 144); // the process ran a core: not an idle (process-blocked) window
        HostLoadReport quiet = sampler.Report(4 * Second)!.Value;
        Assert.Equal(0, quiet.StallMicros);
        Assert.Equal(HostLoadVerdict.Quiet, quiet.Verdict);
    }

    [Fact]
    public void Disabled_ReportsNothing()
    {
        var clock = new FakeClock();
        var sampler = new HostLoadSampler(new HostLoadOptions { Enabled = false }, 1000, clock, new FakeProbe());
        sampler.Check(clock.NowMicros);
        Assert.Equal(0, sampler.Count);
        Assert.Equal(string.Empty, sampler.Describe(Second));
    }

    [Fact]
    public void SystemProbe_ReadsThisProcess()
    {
        HostLoadReading r = new SystemHostLoadProbe().Read();
        Assert.True(r.ProcessCpuTicks > 0);
        Assert.True(r.PoolThreads > 0);
        Assert.True(r.Cpus > 0);
    }

    private const long GiB = 1024L * 1024 * 1024;

    // A 4.2 s window on 24 cpus (100.8 cpu-seconds) with a 4158 ms GC pause, on a 32 GiB machine whose GC high-memory threshold is 90%.
    private static HostLoadReport Pause(long processCpuSeconds, double memoryLoad, long highThresholdBytes = 29 * GiB)
    {
        HostLoadReading start = Reading(0, 0, 0);
        HostLoadReading end = Reading(processCpuSeconds, 9, 100, pauseMs: 4158, g2: 1) with
        {
            MemoryLoadBytes = (long)(32 * GiB * memoryLoad / 100),
            HighMemoryLoadThresholdBytes = highThresholdBytes,
            TotalAvailableMemoryBytes = 32 * GiB,
        };
        return HostLoadSampler.Compute(0, start, 4_200_000, end);
    }

    [Fact]
    public void GcPauseCoveringTheStall_WithAnIdleProcessAndAShortMachine_IsGcPaging()
    {
        HostLoadReport r = Pause(processCpuSeconds: 1, memoryLoad: 95);

        Assert.Equal(HostLoadVerdict.GcPaging, r.Verdict);
        Assert.InRange(r.MemoryLoadPercent, 94.9, 95.1);
        string text = r.Format();
        Assert.Contains("verdict gc-paging", text);
        Assert.Contains("free host memory; the GC is compacting paged-out heap", text);
        Assert.Contains("machine memory 95%", text);
    }

    [Fact]
    public void GcPause_WithABusyProcessOrAnUnpressuredMachine_StaysGcPauses()
    {
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(processCpuSeconds: 60, memoryLoad: 50).Verdict);
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(processCpuSeconds: 1, memoryLoad: 50).Verdict);
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(processCpuSeconds: 60, memoryLoad: 95).Verdict);
    }

    [Fact]
    public void GcPaging_WithoutARuntimeThreshold_UsesNinetyPercent()
    {
        Assert.Equal(HostLoadVerdict.GcPaging, Pause(1, 95, highThresholdBytes: 0).Verdict);
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(1, 85, highThresholdBytes: 0).Verdict);
    }

    [Fact]
    public void GcPause_WithNoMemoryReading_StaysGcPauses_AndPrintsNoMemory()
    {
        HostLoadReport r = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4_200_000, Reading(1, 9, 100, pauseMs: 4158));
        Assert.Equal(HostLoadVerdict.GcPauses, r.Verdict);
        Assert.DoesNotContain("machine memory", r.Format());
    }

    [Fact]
    public void GcPause_WithOneBusyCoreOnABigMachine_IsNotPaging()
    {
        // 4.2 cpu-seconds in a 4.2 s window on 24 cpus is 4% of the machine but a whole core: a cpu-bound compaction, not a wait for paged-out memory.
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(processCpuSeconds: 4, memoryLoad: 95).Verdict);
        Assert.Equal(HostLoadVerdict.GcPauses, Pause(processCpuSeconds: 3, memoryLoad: 95).Verdict); // 0.7 cores
        Assert.Equal(HostLoadVerdict.GcPaging, Pause(processCpuSeconds: 2, memoryLoad: 95).Verdict); // 0.48 cores: under half a core
    }

    [Fact]
    public void IdleProcess_UnderMemoryPressure_IsMemoryStarved_NotHostQuiet()
    {
        // 4 s window on 24 cpus, 0.24 cores used by the process, machine memory 97% against a 97% threshold, small GC pause, machine cpu 68%.
        HostLoadReading end = Reading(1, 65, 96, pauseMs: 400) with
        {
            MemoryLoadBytes = (long)(32 * GiB * 0.97),
            HighMemoryLoadThresholdBytes = (long)(32 * GiB * 0.97),
            TotalAvailableMemoryBytes = 32 * GiB,
        };
        HostLoadReport r = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, end);

        Assert.Equal(HostLoadVerdict.MemoryStarved, r.Verdict);
        Assert.Contains("verdict memory-starved", r.Format());
        Assert.DoesNotContain("host-quiet", r.Format());
    }

    [Fact]
    public void IdleProcess_WithoutMemoryPressure_IsProcessBlocked_AndABusyCoreStaysQuiet()
    {
        HostLoadReport idle = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(1, 9, 96));
        Assert.Equal(HostLoadVerdict.ProcessBlocked, idle.Verdict);
        Assert.Contains("verdict process-blocked", idle.Format());

        HostLoadReport oneCore = HostLoadSampler.Compute(0, Reading(0, 0, 0), 4 * Second, Reading(4, 9, 96));
        Assert.Equal(HostLoadVerdict.Quiet, oneCore.Verdict);
    }
}
