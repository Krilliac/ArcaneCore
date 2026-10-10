using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Commands;

internal sealed record ServerInfoDiagnosticsValues(
    int TargetTickMs,
    double TargetTicksPerSecond,
    double? ObservedTicksPerSecond,
    int FrameSamples,
    double? MeanFrameIntervalMicros,
    long? MeanTickMicros,
    long? P99TickMicros,
    long? MaxTickMicros,
    long? WorkOverruns,
    long? FrameOverruns,
    int PendingCommands,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    double? AllocatedBytesPerTick,
    int? ActiveBots)
{
    public long? P95TickMicros { get; init; }

    /// <summary>Median tick work (the watchdog's TickSummary body p50).</summary>
    public long? P50TickMicros { get; init; }

    /// <summary>Frame interval (between two tick starts) percentiles; null without frame samples.</summary>
    public long? FrameP50Micros { get; init; }
    public long? FrameP90Micros { get; init; }

    /// <summary>How late a tick may start before it counts as late (<c>World:TickLateToleranceMs</c>).</summary>
    public int LateToleranceMs { get; init; }

    /// <summary>World-loop schedule: ticks started after their due time, and due starts given up after a stall.</summary>
    public long LateTicks { get; init; }
    public long SkippedTicks { get; init; }

    /// <summary>Mean/max per tick phase (null without samples).</summary>
    public TickPhaseSummary? Commands { get; init; }
    public TickPhaseSummary? Maps { get; init; }
    public TickPhaseSummary? Features { get; init; }

    /// <summary>The slowest world features (smoothed mean per tick).</summary>
    public IReadOnlyList<(string Name, double MeanMicros)> SlowestFeatures { get; init; } = [];

    /// <summary>Mean bytes allocated per tick by phase (null without samples), and the largest allocating features (smoothed).</summary>
    public (double Commands, double Maps, double Features)? PhaseAllocations { get; init; }
    public IReadOnlyList<(string Name, double MeanBytes)> TopAllocatingFeatures { get; init; } = [];

    /// <summary>The garbage collector: server or workstation, concurrent, collections per generation, total pause.</summary>
    public string? Gc { get; init; }
}

internal static class ServerInfoDiagnostics
{
    internal static ServerInfoDiagnosticsValues Capture(WorldRuntime world, IServiceProvider services)
    {
        TickStatsSnapshot stats = world.Stats.Snapshot();
        int targetMs = Math.Max(1, world.Options.TickIntervalMs);
        int? bots = services.GetService<ManagedPlayerbotFeature>()?.Snapshot()
            .Count(status => status.State == ManagedPlayerbotState.Running);
        return new(targetMs, 1000d / targetMs,
            Finite(stats.FrameSamples > 0 ? stats.EffectiveTicksPerSecond : null),
            stats.FrameSamples,
            Finite(stats.FrameSamples > 0 ? stats.MeanFrameIntervalMicros : null),
            stats.Samples > 0 ? (long?)Math.Round(stats.MeanMicros) : null,
            stats.Samples > 0 ? stats.P99Micros : null,
            stats.Samples > 0 ? stats.MaxMicros : null,
            stats.Samples > 0 ? stats.Overruns : null,
            stats.FrameSamples > 0 ? stats.FrameOverruns : null,
            world.PendingCommandCount,
            // Environment.WorkingSet asks for this process's counters alone; Process.WorkingSet64 snapshots every process on the
            // machine first (8 ms a call on an idle desktop, 20 to 112 ms under the live stress test, where each `.server info`
            // was logged as a slow CMSG_MESSAGECHAT on the world thread).
            Environment.WorkingSet,
            GC.GetTotalMemory(false),
            stats.Samples > 0 ? stats.MeanAllocatedBytes : null,
            bots)
        {
            P95TickMicros = stats.Samples > 0 ? stats.P95Micros : null,
            P50TickMicros = stats.Samples > 0 ? stats.P50Micros : null,
            FrameP50Micros = stats.FrameSamples > 0 ? stats.FrameP50Micros : null,
            FrameP90Micros = stats.FrameSamples > 0 ? stats.FrameP90Micros : null,
            LateToleranceMs = world.Scheduler.LateToleranceMs,
            LateTicks = world.Scheduler.LateTicks,
            SkippedTicks = world.Scheduler.SkippedTicks,
            Commands = stats.Samples > 0 ? stats.Commands : null,
            Maps = stats.Samples > 0 ? stats.Maps : null,
            Features = stats.Samples > 0 ? stats.Features : null,
            SlowestFeatures = stats.FeatureMeans.Take(3).ToArray(),
            PhaseAllocations = stats.Samples > 0 ? (stats.CommandsMeanBytes, stats.MapsMeanBytes, stats.FeaturesMeanBytes) : null,
            TopAllocatingFeatures = stats.FeatureAllocations.Take(3).ToArray(),
            Gc = GcLine(),
        };
    }

    private static string GcLine()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} {(System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.Batch ? "non-concurrent" : "concurrent")} gen0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} pauseTotal={GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms lastPause={info.PauseDurations[0].TotalMilliseconds:F1} ms ({info.Generation}{(info.Concurrent ? " background" : string.Empty)})");
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    internal static IReadOnlyList<string> Format(ServerInfoDiagnosticsValues value)
    {
        string Rate(double? number) => number is { } value && double.IsFinite(value)
            ? value.ToString("F2", CultureInfo.InvariantCulture) + " ticks/s" : "unavailable";
        string Millis(double? micros) => micros is { } value && double.IsFinite(value) && value >= 0
            ? (value / 1000d).ToString("F2", CultureInfo.InvariantCulture) + " ms" : "unavailable";
        string Count(long? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        string Bytes(long number) => number < 1024 ? number.ToString(CultureInfo.InvariantCulture) + " B"
            : number < 1024 * 1024 ? (number / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " KiB"
            : (number / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) + " MiB";
        string Allocated(double? number) => number is { } value && double.IsFinite(value) && value >= 0
            ? Bytes((long)value) : "unavailable";
        string Active(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        string Phase(TickPhaseSummary? phase) => phase is { } p ? $"{Millis(p.MeanMicros)}/{Millis(p.MaxMicros)}" : "unavailable";
        static string Name(string name) => name.Length <= 40 ? name : name[..40];
        return
        [
            string.Create(CultureInfo.InvariantCulture,
                $"Tick target: {value.TargetTickMs} ms ({Rate(value.TargetTicksPerSecond)}); observed: {Rate(value.ObservedTicksPerSecond)}"),
            $"Tick frames: samples={value.FrameSamples.ToString(CultureInfo.InvariantCulture)} mean={Millis(value.MeanFrameIntervalMicros)} p50={Millis(value.FrameP50Micros)} p90={Millis(value.FrameP90Micros)} frameOverruns={Count(value.FrameOverruns)}",
            $"Tick work: mean={Millis(value.MeanTickMicros)} p50={Millis(value.P50TickMicros)} p95={Millis(value.P95TickMicros)} p99={Millis(value.P99TickMicros)} max={Millis(value.MaxTickMicros)} workOverruns={Count(value.WorkOverruns)}",
            $"Commands: pending={value.PendingCommands.ToString(CultureInfo.InvariantCulture)}; process: workingSet={Bytes(value.WorkingSetBytes)} managedHeap={Bytes(value.ManagedHeapBytes)} allocatedPerTick={Allocated(value.AllocatedBytesPerTick)}",
            $"Managed bots: {Active(value.ActiveBots)}",
            $"Tick schedule: late={value.LateTicks.ToString(CultureInfo.InvariantCulture)} (>{value.LateToleranceMs.ToString(CultureInfo.InvariantCulture)} ms) skipped={value.SkippedTicks.ToString(CultureInfo.InvariantCulture)} (drift-compensated; a stall skips missed starts instead of bursting)",
            $"Tick phases mean/max: commands={Phase(value.Commands)} maps={Phase(value.Maps)} features={Phase(value.Features)}",
            "Slowest features: " + (value.SlowestFeatures.Count == 0 ? "unavailable"
                : string.Join(", ", value.SlowestFeatures.Select(feature => $"{Name(feature.Name)} {Millis(feature.MeanMicros)}"))),
            "Tick allocation mean: " + (value.PhaseAllocations is { } a
                ? $"commands={Allocated(a.Commands)} maps={Allocated(a.Maps)} features={Allocated(a.Features)}"
                    + (value.TopAllocatingFeatures.Count == 0 ? string.Empty
                        : "; top: " + string.Join(", ", value.TopAllocatingFeatures.Select(feature => $"{Name(feature.Name)} {Allocated(feature.MeanBytes)}")))
                : "unavailable"),
            "GC: " + (value.Gc ?? "unavailable"),
        ];
    }
}
