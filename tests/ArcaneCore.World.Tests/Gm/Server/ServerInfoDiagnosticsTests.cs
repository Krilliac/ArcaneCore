using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Server;

public sealed class ServerInfoDiagnosticsTests
{
    [Fact]
    public void FormatPopulatedValuesUsesInvariantCultureAndBoundedLines()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, 19.75, 128, 50_632.5,
            1_234, 2_345, 3_456, 8, 7, 3, 12_345_678, 8_765_432, 321.5, 2);
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("20.00 ticks/s", lines[0]);
        Assert.Contains("19.75 ticks/s", lines[0]);
        Assert.DoesNotContain(',', lines[0]);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
        Assert.Contains("managedHeap=8.36 MiB", lines[3]);
        Assert.Contains("workOverruns=8", lines[2]);
        Assert.Contains("frameOverruns=7", lines[1]);
    }

    [Fact]
    public void FormatEmptySamplesSaysUnavailableWithoutInventingRate()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, null, 0, null, null, null, null, null, null,
            0, 1, 2, null, null);
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("observed: unavailable", lines[0]);
        Assert.Contains("mean=unavailable", lines[1]);
        Assert.Contains("mean=unavailable", lines[2]);
        Assert.Contains("Managed bots: unavailable", lines[4]);
    }

    [Fact]
    public void FormatterDoesNotDependOnCurrentCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            // Remain valid when the test host uses globalization-invariant mode.
            var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = commaCulture;
            ServerInfoDiagnosticsValues values = new(50, 20, 19.5, 1, 50_250,
                1_234, 2_345, 3_456, 1, 0, 1, 2, 3, 3.5, 0);
            Assert.Contains("19.50 ticks/s", ServerInfoDiagnostics.Format(values)[0]);
            Assert.Contains("50.25 ms", ServerInfoDiagnostics.Format(values)[1]);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void FormatNonFiniteObservedValuesAsUnavailable()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, double.NaN, 1, double.PositiveInfinity,
            1, 2, 3, 4, 5, 0, 1, 2, double.NaN, null);
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("observed: unavailable", lines[0]);
        Assert.Contains("mean=unavailable", lines[1]);
        Assert.Contains("allocatedPerTick=unavailable", lines[3]);
    }

    [Fact]
    public void FormatShowsP95ScheduleHealthPhasesAndSlowestFeatures()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, 19.9, 64, 50_100, 1_000, 4_000, 9_000, 2, 1, 0, 1, 2, 10, 4)
        {
            P95TickMicros = 3_250,
            LateTicks = 12,
            SkippedTicks = 7,
            Commands = new TickPhaseSummary(120, 900),
            Maps = new TickPhaseSummary(700, 8_000),
            Features = new TickPhaseSummary(180, 1_500),
            SlowestFeatures = [("ManagedPlayerbotFeature", 150), ("ChatFeature", 20)],
        };
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("p95=3.25 ms", lines[2]);
        Assert.Contains("p99=4.00 ms", lines[2]);
        Assert.Contains("late=12 (>0 ms) skipped=7", lines[5]);
        Assert.Contains("commands=0.12 ms/0.90 ms maps=0.70 ms/8.00 ms features=0.18 ms/1.50 ms", lines[6]);
        Assert.Equal("Slowest features: ManagedPlayerbotFeature 0.15 ms, ChatFeature 0.02 ms", lines[7]);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
    }

    [Fact]
    public void FormatWithoutPhaseSamplesSaysUnavailable()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, null, 0, null, null, null, null, null, null, 0, 1, 2, null, null);
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("p95=unavailable", lines[2]);
        Assert.Contains("commands=unavailable", lines[6]);
        Assert.Equal("Slowest features: unavailable", lines[7]);
    }

    [Fact]
    public void FormatShowsTheMedianTickAndTheFrameIntervalPercentiles()
    {
        // The live stress test (2026-10-08) had only the watchdog's TickSummary line for a median and none for the frame spread.
        ServerInfoDiagnosticsValues values = new(50, 20, 20, 64, 50_000, 3_000, 9_000, 12_000, 0, 0, 0, 1, 2, 10, 0)
        {
            P50TickMicros = 2_750,
            P95TickMicros = 8_000,
            FrameP50Micros = 50_004,
            FrameP90Micros = 50_331,
            LateToleranceMs = 2,
            LateTicks = 3,
        };
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("mean=50.00 ms p50=50.00 ms p90=50.33 ms frameOverruns=0", lines[1]);
        Assert.Contains("mean=3.00 ms p50=2.75 ms p95=8.00 ms", lines[2]);
        Assert.Contains("late=3 (>2 ms) skipped=0", lines[5]);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
    }

    [Fact]
    public void FormatShowsTheAllocationPerPhaseTheLargestAllocatorsAndTheCollector()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, 20, 64, 50_000, 3_000, 9_000, 12_000, 0, 0, 0, 1, 2, 10, 0)
        {
            PhaseAllocations = (512, 900_000, 3 * 1024),
            TopAllocatingFeatures = [("ManagedPlayerbotFeature", 2048), ("ChatFeature", 100)],
            Gc = "workstation concurrent gen0/1/2=5/2/1 pauseTotal=40 ms lastPause=1.5 ms (0)",
        };
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Equal("Tick allocation mean: commands=512 B maps=878.91 KiB features=3.00 KiB; top: ManagedPlayerbotFeature 2.00 KiB, ChatFeature 100 B", lines[8]);
        Assert.Equal("GC: workstation concurrent gen0/1/2=5/2/1 pauseTotal=40 ms lastPause=1.5 ms (0)", lines[9]);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));

        string[] empty = [.. ServerInfoDiagnostics.Format(new(50, 20, null, 0, null, null, null, null, null, null, 0, 1, 2, null, null))];
        Assert.Equal("Tick allocation mean: unavailable", empty[8]);
        Assert.Equal("GC: unavailable", empty[9]);
    }

    [Fact]
    public void FormatWithoutFrameSamplesSaysUnavailableForThePercentiles()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, null, 0, null, null, null, null, null, null, 0, 1, 2, null, null);
        string[] lines = [.. ServerInfoDiagnostics.Format(values)];
        Assert.Contains("p50=unavailable p90=unavailable", lines[1]);
        Assert.Contains("mean=unavailable p50=unavailable", lines[2]);
    }

    /// <summary>
    /// `.server info` runs on the world thread. Its working set came from Process.WorkingSet64, which on Windows snapshots every
    /// process on the machine (8 ms a call on an idle desktop); under the live stress test (2026-10-08) each `.server info` of the
    /// operator and the observer was logged as a slow CMSG_MESSAGECHAT, 20 to 112 ms. The capture now costs well under a
    /// millisecond (the bound leaves room for a loaded test machine).
    /// </summary>
    [Fact]
    public async Task CapturingTheDiagnostics_DoesNotHoldTheWorldThread()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        double meanMs = await host.OnWorldAsync(() =>
        {
            ServerInfoDiagnostics.Capture(host.World, host.WorldServices); // warm
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Assert.True(ServerInfoDiagnostics.Capture(host.World, host.WorldServices).WorkingSetBytes > 0);
            return watch.Elapsed.TotalMilliseconds / 20;
        });
        Assert.True(meanMs < 3, $"one capture took {meanMs:F2} ms on the world thread");
    }
}
