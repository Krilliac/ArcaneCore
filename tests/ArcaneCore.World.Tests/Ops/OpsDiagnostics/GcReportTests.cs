using ArcaneCore.World.Ops.Diagnostics;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.OpsDiagnostics;

/// <summary><c>.debug gc</c>: the formatter over synthetic values, DATAS detection, and one read of the real runtime.</summary>
public sealed class GcReportTests
{
    private const long Mib = 1024 * 1024;
    private const long Gib = 1024 * Mib;

    private static GcKindValues Kind(string name, long index) => new(name, index, 2, [2345.6], 41.27, 120 * Mib, true, false, 5632 * Mib, 1792 * Mib,
    [
        new GcGenerationValues("gen0", 10 * Mib, 0, 1 * Mib, 0),
        new GcGenerationValues("gen1", 20 * Mib, 5 * Mib, 2 * Mib, 1 * Mib),
        new GcGenerationValues("gen2", 2000 * Mib, 1900 * Mib, 100 * Mib, 80 * Mib),
        new GcGenerationValues("LOH", 3 * Gib, 3 * Gib, 1800 * Mib, 1792 * Mib),
        new GcGenerationValues("POH", 0, 0, 0, 0),
    ]);

    private static GcReportValues Values() => new(true, true, "on (default for server GC)", "Interactive", "Default", 20 * Gib, 28 * Gib, 31 * Gib, 6 * Gib,
        3456.78, 1000, 100, 7, [Kind("Ephemeral", 900), Kind("FullBlocking", 7), new GcKindValues("Background", 0, 0, [], 0, 0, false, false, 0, 0, [])]);

    [Fact]
    public void Format_PrintsEveryRequestedField_InInvariantCulture()
    {
        string[] lines = [.. GcReport.Format(Values())];
        string all = string.Join("\n", lines);

        Assert.Contains("GC mode: server, concurrent, DATAS on (default for server GC), latency mode Interactive, LOH compaction Default", all);
        Assert.Contains("collections gen0/1/2 = 1000/100/7", all);
        Assert.Contains("total pause 3456.8 ms", all);
        Assert.Contains("load 20.00 GiB of 31.00 GiB available (high-memory-load threshold 28.00 GiB), committed 6.00 GiB", all);
        Assert.Contains("FullBlocking GC #7: gen 2, pause 2345.6 ms, 41.3% of time in GC, promoted 120.00 MiB, compacted yes, concurrent no, heap 5.50 GiB, fragmented 1.75 GiB", all);
        Assert.Contains("FullBlocking LOH: size 3.00 GiB -> 3.00 GiB, fragmentation 1.76 GiB -> 1.75 GiB", all);
        Assert.Contains("Ephemeral gen0: size 10.00 MiB -> 0 B, fragmentation 1.00 MiB -> 0 B", all);
        Assert.Contains("FullBlocking POH:", all);
        Assert.Contains("Background GC: none has happened yet", all);
        Assert.DoesNotContain("Background gen", all);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240)); // one chat message each
    }

    [Fact]
    public void Format_ShowsBothPausesOfABackgroundCollection_AndNonConcurrentWorkstationMode()
    {
        GcReportValues values = Values() with
        {
            ServerGc = false,
            ConcurrentGc = false,
            Datas = "n/a (workstation GC)",
            Kinds = [new GcKindValues("Background", 3, 2, [1.5, 0.5], 0.5, 0, false, true, 1, 0, [])],
        };
        string all = string.Join("\n", GcReport.Format(values));
        Assert.Contains("GC mode: workstation, non-concurrent, DATAS n/a (workstation GC)", all);
        Assert.Contains("pause 1.5 ms, 0.5 ms", all);
        Assert.Contains("concurrent yes", all);
    }

    [Theory]
    [InlineData(true, null, null, "on (default for server GC)")]
    [InlineData(true, null, "1", "on (configured)")]
    [InlineData(true, null, "0", "off (configured)")]
    [InlineData(true, "0", "1", "off (configured)")] // the environment wins over runtimeconfig
    [InlineData(true, "", "0", "off (configured)")]
    [InlineData(true, "7", null, "unknown (7)")]
    [InlineData(false, "1", "1", "n/a (workstation GC)")]
    public void DescribeDatas_ReadsTheEnvironmentThenTheRuntimeConfigKnob(bool server, string? environment, string? configured, string expected)
        => Assert.Equal(expected, GcReport.DescribeDatas(server, environment, configured));

    [Fact]
    public void Bytes_PicksTheUnit()
    {
        Assert.Equal("0 B", GcReport.Bytes(0));
        Assert.Equal("1023 B", GcReport.Bytes(1023));
        Assert.Equal("1.50 KiB", GcReport.Bytes(1536));
        Assert.Equal("2.00 MiB", GcReport.Bytes(2 * Mib));
        Assert.Equal("5.50 GiB", GcReport.Bytes(5 * Gib + Gib / 2));
    }

    [Fact]
    public void Capture_ReadsTheRealRuntime_AndAForcedFullCollectionShowsInFullBlocking()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GcReportValues values = GcReport.Capture();

        Assert.Equal(System.Runtime.GCSettings.IsServerGC, values.ServerGc);
        Assert.True(values.TotalAvailableBytes > 0);
        Assert.True(values.Gen2Collections >= 1);
        Assert.True(values.TotalPauseMs > 0);
        GcKindValues full = Assert.Single(values.Kinds, k => k.Kind == "FullBlocking");
        Assert.True(full.Index > 0);
        Assert.Equal(2, full.Generation);
        Assert.NotEmpty(full.PausesMs);
        Assert.True(full.Generations.Count >= 5);
        string[] lines = [.. GcReport.Format(values)];
        Assert.Contains(lines, l => l.StartsWith("FullBlocking GC #", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 240));
    }
}
