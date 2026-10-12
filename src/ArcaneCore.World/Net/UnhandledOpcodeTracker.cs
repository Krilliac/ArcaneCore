using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>One opcode (by its reference name, or <see cref="UnhandledOpcodeTracker.UnknownLabel"/>) and how many times a client sent it with no handler registered.</summary>
public readonly record struct UnhandledOpcodeCount(string Opcode, long Count);

/// <summary>
/// Counts the client opcodes that reach <see cref="WorldSession"/> with no registered handler, which the session drops (the connection
/// stays open). A handler that was never written or never registered is otherwise invisible: the client's feature just does nothing.
/// <para>
/// Bounded by the opcode space: one counter per named <see cref="WorldOpcode"/> plus one shared bucket (<see cref="UnknownLabel"/>) for
/// values with no reference name (the 826 gap, anything past 827), so a client cannot grow it by sending arbitrary values.
/// Exposed as the observable counter <see cref="MetricName"/> (tags <c>protocol=world</c>, <c>opcode=&lt;name&gt;</c>, non-zero slots
/// only; docs/ops/metrics.md) and read by <c>WiringAudit</c> / <c>.server wiring</c>.
/// </para>
/// <para>
/// Thread affinity: any thread. <see cref="Record"/> is one <see cref="Interlocked.Increment(ref long)"/>; no allocation, no lock. The
/// caller that receives a count of 1 owns the one-time warning for that slot.
/// </para>
/// </summary>
public sealed class UnhandledOpcodeTracker
{
    /// <summary>The label of the shared bucket for opcode values that have no reference name.</summary>
    public const string UnknownLabel = OpcodeTable.UnknownLabel;

    /// <summary>The metric name (tags <c>protocol</c> and <c>opcode</c>).</summary>
    public const string MetricName = "arcanecore.net.opcode.unhandled";

    // Index = opcode value; null = a value with no reference name (it counts in the shared bucket at index Labels.Length).
    private static readonly string?[] Labels = BuildLabels();

    /// <summary>The process-wide instance: the one the daemon registers, the one a session uses when its container has none, and the one the metric reads.</summary>
    public static UnhandledOpcodeTracker Shared { get; } = new();

    // The instrument reads Shared only; the daemon registers Shared, so there is exactly one series per opcode. It exists once this type
    // is first touched (static initialisation), which the daemon's registration of Shared does at start-up.
    private static readonly ObservableCounter<long> Metric = ArcaneMeters.Net.CreateObservableCounter(
        MetricName, ObserveShared, "{packet}", "Client packets that had no registered handler and were dropped, by protocol and opcode.");

    private readonly long[] _counts = new long[Labels.Length + 1];

    /// <summary>Total unhandled packets counted.</summary>
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

    /// <summary>
    /// Count one unhandled packet. Returns the new count of its slot, so a result of 1 means this is the first time the opcode (or, for
    /// <see cref="UnknownLabel"/>, any unnamed value) was seen and the caller should log it.
    /// </summary>
    public long Record(WorldOpcode opcode) => Interlocked.Increment(ref _counts[SlotOf(opcode)]);

    /// <summary>The label <see cref="Record"/> files <paramref name="opcode"/> under: its reference name, or <see cref="UnknownLabel"/>.</summary>
    public static string LabelOf(WorldOpcode opcode)
    {
        int slot = SlotOf(opcode);
        return slot < Labels.Length ? Labels[slot] ?? UnknownLabel : UnknownLabel;
    }

    /// <summary>The non-zero counters, most frequent first (ties by name). A snapshot; counts may advance while it is read.</summary>
    public IReadOnlyList<UnhandledOpcodeCount> Snapshot()
    {
        var list = new List<UnhandledOpcodeCount>();
        for (int slot = 0; slot < _counts.Length; slot++)
        {
            long count = Volatile.Read(ref _counts[slot]);
            if (count != 0)
            {
                list.Add(new UnhandledOpcodeCount(Labels.ElementAtOrDefault(slot) ?? UnknownLabel, count));
            }
        }

        return [.. list.OrderByDescending(c => c.Count).ThenBy(c => c.Opcode, StringComparer.Ordinal)];
    }

    private static int SlotOf(WorldOpcode opcode)
    {
        int value = (ushort)opcode;
        return value < Labels.Length && Labels[value] is not null ? value : Labels.Length;
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
        foreach (UnhandledOpcodeCount entry in Shared.Snapshot())
        {
            yield return new Measurement<long>(entry.Count, new KeyValuePair<string, object?>("protocol", Ops.Metrics.WorldPacketMetrics.Protocol), new KeyValuePair<string, object?>("opcode", entry.Opcode));
        }
    }
}
