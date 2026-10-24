using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;

namespace ArcaneCore.World.Net;

/// <summary>One stripped developer opcode (by its reference name) and how many times a client sent it.</summary>
public readonly record struct DevOpcodeCount(string Opcode, long Count);

/// <summary>
/// Counts the stripped developer, GM, cheat and debug opcodes (<see cref="ArcaneCore.World.Handlers.StrippedDevOpcodes"/>) that sessions dropped, per opcode and per
/// traffic kind. They are deliberately not in <see cref="UnhandledOpcodeTracker"/>, so that one shows only real gaps. Exposed as the observable
/// counter <see cref="MetricName"/> (tags <c>protocol=world</c>, <c>opcode</c>, <c>traffic</c>; non-zero slots only; docs/ops/metrics.md) and read by
/// <c>WiringAudit</c> / <c>.server wiring</c>. Also owns the two <see cref="LogGate"/>s that keep a flooding client to one log line per interval.
/// <para>
/// Bounded by the opcode space: one counter per named <see cref="WorldOpcode"/> and traffic kind. Thread affinity: any thread; <see cref="Record"/> is
/// one <see cref="Interlocked.Increment(ref long)"/>, no allocation, no lock.
/// </para>
/// </summary>
public sealed class DevOpcodeTracker
{
    /// <summary>The metric name (tags <c>protocol</c>, <c>opcode</c> and <c>traffic</c>).</summary>
    public const string MetricName = "arcanecore.net.opcode.dev_dropped";

    private static readonly int Kinds = Enum.GetValues<PacketTraffic>().Length;

    // Index = opcode value; null = a value with no reference name (never counted: only registered stripped opcodes are recorded).
    private static readonly string?[] Labels = BuildLabels();

    /// <summary>The process-wide instance: the one the daemon registers, the one a session uses when its container has none, and the one the metric reads.</summary>
    public static DevOpcodeTracker Shared { get; } = new();

    private static readonly ObservableCounter<long> Metric = ArcaneMeters.Net.CreateObservableCounter(
        MetricName, ObserveShared, "{packet}", "Stripped developer, GM and cheat opcodes a client sent that were dropped unread, by protocol, opcode and traffic kind.");

    private readonly long[] _counts = new long[Labels.Length * Kinds];

    /// <summary>At most one log line per interval for a non-staff session's dev opcodes (process wide).</summary>
    internal LogGate PlayerGate { get; } = new(TimeSpan.FromSeconds(10));

    /// <summary>At most one log line per interval for staff audit lines (the audit tail still holds every one).</summary>
    internal LogGate StaffGate { get; } = new(TimeSpan.FromSeconds(10));

    /// <summary>Total dev opcodes counted.</summary>
    public long Total
    {
        get
        {
            long total = 0;
            for (int slot = 0; slot < _counts.Length; slot++)
            {
                total += Volatile.Read(ref _counts[slot]);
            }

            return total;
        }
    }

    /// <summary>Count one dropped dev opcode; false (and nothing counted) when <paramref name="opcode"/> has no reference name.</summary>
    public bool Record(WorldOpcode opcode, PacketTraffic traffic)
    {
        int value = (ushort)opcode;
        if (value >= Labels.Length || Labels[value] is null)
        {
            return false;
        }

        Interlocked.Increment(ref _counts[(value * Kinds) + (int)traffic]);
        return true;
    }

    /// <summary>The non-zero counters summed over traffic kinds, most frequent first (ties by name). A snapshot; counts may advance while it is read.</summary>
    public IReadOnlyList<DevOpcodeCount> Snapshot()
    {
        var list = new List<DevOpcodeCount>();
        for (int value = 0; value < Labels.Length; value++)
        {
            long count = 0;
            for (int kind = 0; kind < Kinds; kind++)
            {
                count += Volatile.Read(ref _counts[(value * Kinds) + kind]);
            }

            if (count != 0)
            {
                list.Add(new DevOpcodeCount(Labels[value]!, count));
            }
        }

        return [.. list.OrderByDescending(c => c.Count).ThenBy(c => c.Opcode, StringComparer.Ordinal)];
    }

    private static string?[] BuildLabels()
    {
        WorldOpcode[] known = Enum.GetValues<WorldOpcode>();
        var labels = new string?[known.Max(o => (int)o) + 1];
        foreach (WorldOpcode opcode in known)
        {
            labels[(int)opcode] = WorldOpcodeNames.GetName(opcode);
        }

        return labels;
    }

    private static IEnumerable<Measurement<long>> ObserveShared()
    {
        DevOpcodeTracker shared = Shared;
        for (int value = 0; value < Labels.Length; value++)
        {
            for (int kind = 0; kind < Kinds; kind++)
            {
                long count = Volatile.Read(ref shared._counts[(value * Kinds) + kind]);
                if (count != 0)
                {
                    yield return new Measurement<long>(count,
                        new KeyValuePair<string, object?>("protocol", Ops.Metrics.WorldPacketMetrics.Protocol),
                        new KeyValuePair<string, object?>("opcode", Labels[value]),
                        new KeyValuePair<string, object?>("traffic", ((PacketTraffic)kind).ToString().ToLowerInvariant()));
                }
            }
        }
    }
}
