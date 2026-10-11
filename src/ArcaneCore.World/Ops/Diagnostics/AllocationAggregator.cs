using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>Where a sampled allocation landed: the small object heap, the large object heap or the pinned object heap (the runtime's <c>AllocationKind</c> 0, 1, 2).</summary>
public enum AllocationHeap
{
    Soh = 0,
    Loh = 1,
    Poh = 2,
}

/// <summary>One (type, heap) row of an allocation sample.</summary>
public readonly record struct AllocationRow(string Type, AllocationHeap Heap, long Bytes, long Events);

/// <summary>A copy of an aggregator at one instant. <see cref="OtherBytes"/>/<see cref="OtherEvents"/> are indexed by <see cref="AllocationHeap"/>.</summary>
public sealed record AllocationSnapshot(
    IReadOnlyList<AllocationRow> Rows,
    IReadOnlyList<long> OtherBytes,
    IReadOnlyList<long> OtherEvents,
    int DistinctTypes,
    long TotalEvents,
    long MalformedEvents);

/// <summary>
/// Sums sampled allocation bytes per (type, heap), bounded to <see cref="MaxTypes"/> distinct rows: a row that would be the
/// <c>MaxTypes + 1</c>-th is added to its heap's "other" bucket instead, so a generic-heavy or dynamic-assembly workload cannot grow the
/// dictionary without limit. Thread-safe: the runtime delivers events on its own threads, so every call takes one short lock (the
/// sampling rate is about one event per 100 KB allocated, which is a few thousand events a second at most). <see cref="Add"/> allocates
/// only when a new row is created.
/// </summary>
public sealed class AllocationAggregator(int maxTypes = AllocationAggregator.DefaultMaxTypes)
{
    /// <summary>The default cap on distinct (type, heap) rows.</summary>
    public const int DefaultMaxTypes = 2000;

    /// <summary>The name an event without a type name is filed under.</summary>
    public const string UnknownType = "(unknown)";

    /// <summary>The label the report gives the "other" bucket.</summary>
    public const string OtherLabel = "(other types, beyond the row cap)";

    private readonly record struct Key(string Type, int Heap);

    private struct Counts
    {
        public long Bytes;
        public long Events;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, Counts> _rows = new();
    private readonly long[] _otherBytes = new long[3];
    private readonly long[] _otherEvents = new long[3];
    private long _totalEvents;
    private long _malformed;

    /// <summary>The cap on distinct rows (at least 1).</summary>
    public int MaxTypes { get; } = Math.Max(1, maxTypes);

    /// <summary>Events seen so far, including those that landed in an "other" bucket.</summary>
    public long TotalEvents
    {
        get
        {
            lock (_gate)
            {
                return _totalEvents;
            }
        }
    }

    /// <summary>
    /// Record one sampled allocation of <paramref name="bytes"/> by <paramref name="typeName"/> on heap <paramref name="heap"/>
    /// (the runtime's <c>AllocationKind</c>). An unknown heap or a negative size counts as a malformed event and nothing else.
    /// </summary>
    public void Add(string? typeName, int heap, long bytes)
    {
        if (heap is < 0 or > 2 || bytes < 0)
        {
            AddMalformed();
            return;
        }

        string type = string.IsNullOrEmpty(typeName) ? UnknownType : typeName;
        lock (_gate)
        {
            _totalEvents++;
            ref Counts existing = ref CollectionsMarshal.GetValueRefOrNullRef(_rows, new Key(type, heap));
            if (!Unsafe.IsNullRef(ref existing))
            {
                existing.Bytes += bytes;
                existing.Events++;
                return;
            }

            if (_rows.Count >= MaxTypes)
            {
                _otherBytes[heap] += bytes;
                _otherEvents[heap]++;
                return;
            }

            _rows.Add(new Key(type, heap), new Counts { Bytes = bytes, Events = 1 });
        }
    }

    /// <summary>An event whose payload could not be read (a field the runtime did not send).</summary>
    public void AddMalformed()
    {
        lock (_gate)
        {
            _malformed++;
        }
    }

    /// <summary>Copy the current totals; the aggregator keeps counting.</summary>
    public AllocationSnapshot Snapshot()
    {
        lock (_gate)
        {
            var rows = new List<AllocationRow>(_rows.Count);
            foreach ((Key key, Counts counts) in _rows)
            {
                rows.Add(new AllocationRow(key.Type, (AllocationHeap)key.Heap, counts.Bytes, counts.Events));
            }

            return new AllocationSnapshot(rows, (long[])_otherBytes.Clone(), (long[])_otherEvents.Clone(), _rows.Count, _totalEvents, _malformed);
        }
    }
}

/// <summary>The text of an allocation sample (<c>.debug alloc report</c>).</summary>
public static class AllocationReportFormat
{
    /// <summary>The sampling rate the runtime documents for GCAllocationTick, stated in every report.</summary>
    public const string SamplingNote = "sampled: the runtime raises about one allocation-tick event per 100 KB allocated and the event is charged with that amount, so rows are estimates and small types are under-represented";

    /// <summary>Longest type name printed; a longer one is cut (the generic arguments are the first thing lost).</summary>
    public const int MaxTypeNameLength = 90;

    private const double MiB = 1024d * 1024d;

    /// <summary>
    /// Lines for <paramref name="snapshot"/> taken after <paramref name="elapsed"/> of sampling: a header, then for each of SOH, LOH and POH
    /// its total and the <paramref name="top"/> biggest types with sampled MiB, sampled MiB per second and the share of that heap.
    /// </summary>
    public static IReadOnlyList<string> Format(AllocationSnapshot snapshot, TimeSpan elapsed, bool running, int top)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"Allocation sample ({(running ? "running" : "stopped")}): {elapsed.TotalSeconds:F1} s, {snapshot.TotalEvents} events, {snapshot.DistinctTypes} distinct (type, heap) rows{(snapshot.MalformedEvents > 0 ? $", {snapshot.MalformedEvents} unreadable events" : string.Empty)}"),
            "Numbers are " + SamplingNote + ".",
        };

        foreach (AllocationHeap heap in new[] { AllocationHeap.Soh, AllocationHeap.Loh, AllocationHeap.Poh })
        {
            List<AllocationRow> rows = [.. snapshot.Rows.Where(r => r.Heap == heap).OrderByDescending(r => r.Bytes).ThenBy(r => r.Type, StringComparer.Ordinal)];
            long otherBytes = snapshot.OtherBytes[(int)heap];
            long total = rows.Sum(r => r.Bytes) + otherBytes;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{heap.ToString().ToUpperInvariant()}: {total / MiB:F1} MiB sampled, {total / MiB / seconds:F2} MiB/s{(total == 0 ? " (nothing sampled)" : string.Empty)}"));
            int shown = Math.Max(1, top);
            foreach (AllocationRow row in rows.Take(shown))
            {
                lines.Add(Row(row.Type, row.Bytes, row.Events, total, seconds));
            }

            if (rows.Count > shown)
            {
                long hidden = rows.Skip(shown).Sum(r => r.Bytes);
                lines.Add(Row($"({rows.Count - shown} more types)", hidden, rows.Skip(shown).Sum(r => r.Events), total, seconds));
            }

            if (otherBytes > 0)
            {
                lines.Add(Row(AllocationAggregator.OtherLabel, otherBytes, snapshot.OtherEvents[(int)heap], total, seconds));
            }
        }

        return lines;
    }

    private static string Row(string type, long bytes, long events, long total, double seconds)
    {
        string name = type.Length <= MaxTypeNameLength ? type : type[..MaxTypeNameLength] + "...";
        double share = total <= 0 ? 0 : bytes * 100d / total;
        return string.Create(CultureInfo.InvariantCulture, $"  {name}: {bytes / MiB:F2} MiB, {bytes / MiB / seconds:F2} MiB/s, {share:F1}% ({events} events)");
    }
}
