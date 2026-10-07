using System.Diagnostics;
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
    int? ActiveBots);

internal static class ServerInfoDiagnostics
{
    internal static ServerInfoDiagnosticsValues Capture(WorldRuntime world, IServiceProvider services)
    {
        TickStatsSnapshot stats = world.Stats.Snapshot();
        int targetMs = Math.Max(1, world.Options.TickIntervalMs);
        int? bots = services.GetService<ManagedPlayerbotFeature>()?.Snapshot()
            .Count(status => status.State == ManagedPlayerbotState.Running);
        using Process process = Process.GetCurrentProcess();
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
            process.WorkingSet64,
            GC.GetTotalMemory(false),
            stats.Samples > 0 ? stats.MeanAllocatedBytes : null,
            bots);
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
        return
        [
            string.Create(CultureInfo.InvariantCulture,
                $"Tick target: {value.TargetTickMs} ms ({Rate(value.TargetTicksPerSecond)}); observed: {Rate(value.ObservedTicksPerSecond)}"),
            $"Tick frames: samples={value.FrameSamples.ToString(CultureInfo.InvariantCulture)} mean={Millis(value.MeanFrameIntervalMicros)} frameOverruns={Count(value.FrameOverruns)}",
            $"Tick work: mean={Millis(value.MeanTickMicros)} p99={Millis(value.P99TickMicros)} max={Millis(value.MaxTickMicros)} workOverruns={Count(value.WorkOverruns)}",
            $"Commands: pending={value.PendingCommands.ToString(CultureInfo.InvariantCulture)}; process: workingSet={Bytes(value.WorkingSetBytes)} managedHeap={Bytes(value.ManagedHeapBytes)} allocatedPerTick={Allocated(value.AllocatedBytesPerTick)}",
            $"Managed bots: {Active(value.ActiveBots)}",
        ];
    }
}
