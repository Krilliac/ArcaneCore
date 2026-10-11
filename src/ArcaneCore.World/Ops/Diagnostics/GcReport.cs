using System.Globalization;
using System.Runtime;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>Size and fragmentation of one heap generation around a collection (0 = gen0, 1, 2, 3 = LOH, 4 = POH).</summary>
internal sealed record GcGenerationValues(string Name, long SizeBefore, long SizeAfter, long FragmentationBefore, long FragmentationAfter);

/// <summary>What <c>GC.GetGCMemoryInfo(kind)</c> says about the last collection of one kind. <see cref="Index"/> 0: none has happened yet.</summary>
internal sealed record GcKindValues(
    string Kind,
    long Index,
    int Generation,
    IReadOnlyList<double> PausesMs,
    double PauseTimePercent,
    long PromotedBytes,
    bool Compacted,
    bool Concurrent,
    long HeapBytes,
    long FragmentedBytes,
    IReadOnlyList<GcGenerationValues> Generations);

/// <summary>Everything <c>.debug gc</c> prints, as plain values so the formatter is testable without a collection.</summary>
internal sealed record GcReportValues(
    bool ServerGc,
    bool ConcurrentGc,
    string Datas,
    string LatencyMode,
    string LohCompaction,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    long TotalAvailableBytes,
    long TotalCommittedBytes,
    double TotalPauseMs,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    IReadOnlyList<GcKindValues> Kinds);

/// <summary>
/// The in-process GC report behind <c>.debug gc</c> (docs/ops/diagnostics.md): the last ephemeral, full blocking and background
/// collection with their pauses and per-generation sizes, the memory load, the collection counts and the GC mode. Every read is a
/// runtime call that takes no lock the world thread needs and costs microseconds; the report is built on the calling thread.
/// </summary>
internal static class GcReport
{
    /// <summary>The runtimeconfig knob for DATAS (dynamic adaptation to application sizes).</summary>
    internal const string DatasKnob = "System.GC.DynamicAdaptationMode";

    private static readonly string[] GenerationNames = ["gen0", "gen1", "gen2", "LOH", "POH"];

    /// <summary>Read the runtime now.</summary>
    internal static GcReportValues Capture()
    {
        GCMemoryInfo any = GC.GetGCMemoryInfo(GCKind.Any);
        return new GcReportValues(
            GCSettings.IsServerGC,
            GCSettings.LatencyMode != GCLatencyMode.Batch,
            DescribeDatas(GCSettings.IsServerGC, Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode"), AppContext.GetData(DatasKnob)),
            GCSettings.LatencyMode.ToString(),
            GCSettings.LargeObjectHeapCompactionMode.ToString(),
            any.MemoryLoadBytes,
            any.HighMemoryLoadThresholdBytes,
            any.TotalAvailableMemoryBytes,
            any.TotalCommittedBytes,
            GC.GetTotalPauseDuration().TotalMilliseconds,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            [
                Kind("Ephemeral", GC.GetGCMemoryInfo(GCKind.Ephemeral)),
                Kind("FullBlocking", GC.GetGCMemoryInfo(GCKind.FullBlocking)),
                Kind("Background", GC.GetGCMemoryInfo(GCKind.Background)),
            ]);
    }

    private static GcKindValues Kind(string name, GCMemoryInfo info)
    {
        var pauses = new List<double>(info.PauseDurations.Length);
        foreach (TimeSpan pause in info.PauseDurations)
        {
            pauses.Add(pause.TotalMilliseconds);
        }

        var generations = new List<GcGenerationValues>(info.GenerationInfo.Length);
        for (int i = 0; i < info.GenerationInfo.Length; i++)
        {
            GCGenerationInfo generation = info.GenerationInfo[i];
            generations.Add(new GcGenerationValues(i < GenerationNames.Length ? GenerationNames[i] : "gen" + i.ToString(CultureInfo.InvariantCulture),
                generation.SizeBeforeBytes, generation.SizeAfterBytes, generation.FragmentationBeforeBytes, generation.FragmentationAfterBytes));
        }

        return new GcKindValues(name, info.Index, info.Generation, pauses, info.PauseTimePercentage, info.PromotedBytes,
            info.Compacted, info.Concurrent, info.HeapSizeBytes, info.FragmentedBytes, generations);
    }

    /// <summary>
    /// DATAS state from the environment (<c>DOTNET_GCDynamicAdaptationMode</c>, which wins) and the runtimeconfig knob
    /// <see cref="DatasKnob"/>. Unset: server GC on .NET 9 and later has it on by default; workstation GC has no DATAS.
    /// </summary>
    internal static string DescribeDatas(bool serverGc, string? environmentValue, object? configuredValue)
    {
        if (!serverGc)
        {
            return "n/a (workstation GC)";
        }

        string? text = !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue.Trim() : configuredValue?.ToString()?.Trim();
        return text switch
        {
            null or "" => "on (default for server GC)",
            "1" => "on (configured)",
            "0" => "off (configured)",
            _ => "unknown (" + text + ")",
        };
    }

    /// <summary>The report as chat/console lines, each short enough for one chat message.</summary>
    internal static IReadOnlyList<string> Format(GcReportValues values)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"GC mode: {(values.ServerGc ? "server" : "workstation")}, {(values.ConcurrentGc ? "concurrent" : "non-concurrent")}, DATAS {values.Datas}, latency mode {values.LatencyMode}, LOH compaction {values.LohCompaction}"),
            string.Create(CultureInfo.InvariantCulture,
                $"GC totals: collections gen0/1/2 = {values.Gen0Collections}/{values.Gen1Collections}/{values.Gen2Collections} (cumulative: a gen2 counts in all three), total pause {values.TotalPauseMs:F1} ms"),
            $"Memory: load {Bytes(values.MemoryLoadBytes)} of {Bytes(values.TotalAvailableBytes)} available (high-memory-load threshold {Bytes(values.HighMemoryLoadThresholdBytes)}), committed {Bytes(values.TotalCommittedBytes)}",
        };

        foreach (GcKindValues kind in values.Kinds)
        {
            if (kind.Index == 0)
            {
                lines.Add($"{kind.Kind} GC: none has happened yet");
                continue;
            }

            string pauses = kind.PausesMs.Count == 0 ? "none"
                : string.Join(", ", kind.PausesMs.Select(p => p.ToString("F1", CultureInfo.InvariantCulture) + " ms"));
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{kind.Kind} GC #{kind.Index}: gen {kind.Generation}, pause {pauses}, {kind.PauseTimePercent:F1}% of time in GC, promoted {Bytes(kind.PromotedBytes)}, compacted {YesNo(kind.Compacted)}, concurrent {YesNo(kind.Concurrent)}, heap {Bytes(kind.HeapBytes)}, fragmented {Bytes(kind.FragmentedBytes)}"));
            foreach (GcGenerationValues generation in kind.Generations)
            {
                lines.Add($"  {kind.Kind} {generation.Name}: size {Bytes(generation.SizeBefore)} -> {Bytes(generation.SizeAfter)}, fragmentation {Bytes(generation.FragmentationBefore)} -> {Bytes(generation.FragmentationAfter)}");
            }
        }

        return lines;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Bytes as B, KiB, MiB or GiB with two decimals (invariant culture).</summary>
    internal static string Bytes(long bytes)
    {
        double value = bytes;
        return Math.Abs(value) switch
        {
            < 1024d => bytes.ToString(CultureInfo.InvariantCulture) + " B",
            < 1024d * 1024 => (value / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " KiB",
            < 1024d * 1024 * 1024 => (value / (1024d * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " MiB",
            _ => (value / (1024d * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + " GiB",
        };
    }
}
