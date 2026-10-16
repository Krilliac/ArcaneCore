using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
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

    private static LatencyWindows Windows(long micros, int count = 100)
    {
        var histogram = new LatencyHistogram();
        for (int i = 0; i < count; i++) histogram.Record(micros + i);
        return histogram.Windows();
    }

    [Fact]
    public void FormatAppendsTheFullPercentileLines_AfterTheExistingTenLines()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, 20, 64, 50_000, 3_000, 9_000, 12_000, 0, 0, 0, 1, 2, 10, 0)
        {
            Gc = "workstation concurrent gen0/1/2=5/2/1 pauseTotal=40 ms lastPause=1.5 ms (0)",
            TickBodyLatency = Windows(4_000),
            FrameLatency = Windows(50_000),
            PhaseLatency = [("commands", new LatencyHistogram().Snapshot()), ("maps", Windows(2_000).Last1Min)],
            OtherLatencies = [("db.character_save", Windows(8_000).Last15Min), ("packet.handler.CMSG_X", Windows(1_000).Last15Min)],
        };

        string[] lines = [.. ServerInfoDiagnostics.Format(values)];

        Assert.Equal(16, lines.Length);
        Assert.StartsWith("GC: ", lines[9]); // the first ten lines keep their positions
        Assert.Matches(@"^Tick body latency, last 1 min: p50 4\.\d ms p75 4\.\d p90 4\.\d p95 4\.\d p99 4\.\d p99\.9 4\.\d max 4\.1 \(n=100, min 4\.0, mean 4\.\d \+/-0\.\d+\)$", lines[10]);
        Assert.Equal("Tick body latency, last 15 min: " + values.TickBodyLatency!.Value.Last15Min.Format(), lines[11]);
        Assert.Equal("Tick frame latency, last 1 min: " + values.FrameLatency!.Value.Last1Min.Format(), lines[12]);
        Assert.Equal("Tick frame latency, last 15 min: " + values.FrameLatency!.Value.Last15Min.Format(), lines[13]);
        LatencySnapshot maps = values.PhaseLatency[1].Last1Min;
        Assert.Equal($"Tick phases p50/p99 ms, last 1 min: maps {LatencySnapshot.Millis(maps.P50)}/{LatencySnapshot.Millis(maps.P99)}", lines[14]); // a phase without samples is left out
        LatencySnapshot save = values.OtherLatencies[0].Last15Min;
        LatencySnapshot packet = values.OtherLatencies[1].Last15Min;
        Assert.Equal(
            "Other latencies, last 15 min (p50/p99/max ms, n): "
            + $"db.character_save {LatencySnapshot.Millis(save.P50)}/{LatencySnapshot.Millis(save.P99)}/{LatencySnapshot.Millis(save.Max)} (n=100), "
            + $"packet.handler.CMSG_X {LatencySnapshot.Millis(packet.P50)}/{LatencySnapshot.Millis(packet.P99)}/{LatencySnapshot.Millis(packet.Max)} (n=100)",
            lines[15]);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
    }

    [Fact]
    public void FormatWithoutLatencyData_SaysSo()
    {
        ServerInfoDiagnosticsValues values = new(50, 20, null, 0, null, null, null, null, null, null, 0, 1, 2, null, null);

        string[] lines = [.. ServerInfoDiagnostics.Format(values)];

        Assert.Equal("Tick body latency, last 1 min: unavailable", lines[10]);
        Assert.Equal("Tick frame latency, last 15 min: unavailable", lines[13]);
        Assert.Equal("Tick phases p50/p99 ms, last 1 min: unavailable", lines[14]);
        Assert.Equal("Other latencies, last 15 min (p50/p99/max ms, n): none recorded", lines[15]);
        ServerInfoDiagnosticsValues empty = values with { TickBodyLatency = new LatencyHistogram().Windows() };
        Assert.Equal("Tick body latency, last 1 min: no samples", ServerInfoDiagnostics.Format(empty)[10]);
    }

    [Fact]
    public void OtherLatenciesLine_IsBounded_AndCountsWhatItLeavesOut()
    {
        LatencySnapshot one = Windows(8_000).Last15Min;
        ServerInfoDiagnosticsValues values = new(50, 20, null, 0, null, null, null, null, null, null, 0, 1, 2, null, null)
        {
            OtherLatencies = [.. Enumerable.Range(0, 40).Select(i => ($"packet.handler.CMSG_LONG_OPCODE_NAME_{i}", one))],
        };

        string line = ServerInfoDiagnostics.Format(values)[15];

        Assert.InRange(line.Length, 1, 240);
        Assert.Matches(@" and \d+ more$", line);
    }

    [Fact]
    public async Task Capture_FillsTheLatencyFields_AndEveryLineStaysWithinTheBound()
    {
        LatencyRegistry.Shared.Record(LatencyNames.CharacterSave, 5_000);
        LatencyRegistry.Shared.Record(LatencyNames.PacketHandlerPrefix + "CMSG_TEST_ONLY", 9_000);
        await using WorldTestHost host = WorldTestHost.Start();
        string[] lines = await host.OnWorldAsync(() => ServerInfoDiagnostics.Format(ServerInfoDiagnostics.Capture(host.World, host.WorldServices)).ToArray());

        Assert.True(lines.Length >= 16);
        Assert.Contains(lines, l => l.StartsWith("Tick body latency, last 15 min: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Other latencies, last 15 min", StringComparison.Ordinal) && l.Contains(LatencyNames.CharacterSave, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
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
