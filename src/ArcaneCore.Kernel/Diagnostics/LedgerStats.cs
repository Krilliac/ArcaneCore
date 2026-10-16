using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>One subject (or message) of a ledger entry with how often it was reported.</summary>
public readonly record struct SubjectCount(string Subject, long Count);

/// <summary>
/// When and how fast a ledger (or one of its entries) fired: first and last seen, the events of the last 15 minutes and the last hour
/// (the current, partly elapsed minute counts, so a window holds between N-1 and N minutes), and the p50/p99 of the gaps between consecutive events
/// (exact nearest rank over the newest <see cref="GapRing.Capacity"/> gaps; null until the second event).
/// </summary>
public readonly record struct LedgerTiming(DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, long Last15Min, long Last1Hour, TimeSpan? InterArrivalP50 = null, TimeSpan? InterArrivalP99 = null)
{
    /// <summary>Events in the last 15 minutes scaled to an hour.</summary>
    public double RatePerHour15Min => Last15Min * 4d;

    /// <summary>Events in the last hour.</summary>
    public double RatePerHour1Hour => Last1Hour;

    /// <summary>
    /// <c>rate 12.0/h last 15 min, 8.0/h last 1 h; gap p50 5 s p99 3.0 min</c> (the gap part only once there are two events). Culture invariant.
    /// </summary>
    public string Describe()
    {
        string rate = string.Create(CultureInfo.InvariantCulture, $"rate {RatePerHour15Min:F1}/h last 15 min, {RatePerHour1Hour:F1}/h last 1 h");
        return InterArrivalP50 is { } p50 && InterArrivalP99 is { } p99
            ? $"{rate}; gap p50 {FormatSpan(p50)} p99 {FormatSpan(p99)}"
            : rate;
    }

    /// <summary>A duration in the unit that reads best: <c>250 ms</c>, <c>12 s</c>, <c>5.2 min</c>, <c>3.1 h</c>.</summary>
    public static string FormatSpan(TimeSpan span)
    {
        double ms = Math.Max(0, span.TotalMilliseconds);
        return ms switch
        {
            < 1000 => string.Create(CultureInfo.InvariantCulture, $"{ms:F0} ms"),
            < 90_000 => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:F0} s"),
            < 5_400_000 => string.Create(CultureInfo.InvariantCulture, $"{ms / 60_000:F1} min"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:F1} h"),
        };
    }
}

/// <summary>
/// Events per minute for the last hour in 60 fixed one-minute slots (720 bytes). <see cref="Add"/> is lock-free except once a minute (a slot rotation);
/// <see cref="CountSince"/> may be called from any thread. A slot is reused 60 minutes later; a clock that moves backwards just lands events in older slots.
/// </summary>
internal sealed class RateWindow
{
    public const int Minutes = 60;

    private readonly long[] _minute = new long[Minutes];
    private readonly int[] _count = new int[Minutes];
    private readonly object _rotateGate = new();

    public void Add(DateTimeOffset now)
    {
        long minute = now.UtcTicks / TimeSpan.TicksPerMinute;
        int slot = (int)(minute % Minutes);
        if (Volatile.Read(ref _minute[slot]) != minute)
        {
            lock (_rotateGate)
            {
                if (_minute[slot] != minute)
                {
                    Volatile.Write(ref _count[slot], 0);
                    Volatile.Write(ref _minute[slot], minute);
                }
            }
        }

        Interlocked.Increment(ref _count[slot]);
    }

    /// <summary>Events in the newest <paramref name="minutes"/> minutes ending at <paramref name="now"/>, the current minute included.</summary>
    public long CountSince(DateTimeOffset now, int minutes)
    {
        long current = now.UtcTicks / TimeSpan.TicksPerMinute;
        long oldest = current - Math.Clamp(minutes, 1, Minutes) + 1;
        long sum = 0;
        for (int i = 0; i < Minutes; i++)
        {
            long minute = Volatile.Read(ref _minute[i]);
            if (minute >= oldest && minute <= current)
            {
                sum += Volatile.Read(ref _count[i]);
            }
        }

        return sum;
    }

    public void Clear()
    {
        lock (_rotateGate)
        {
            Array.Clear(_minute);
            Array.Clear(_count);
        }
    }
}

/// <summary>The newest <see cref="Capacity"/> gaps between consecutive events, in milliseconds (512 bytes, allocated at the second event). Not thread safe: the owner locks.</summary>
internal sealed class GapRing
{
    public const int Capacity = 128;

    private readonly uint[] _ms = new uint[Capacity];
    private int _next;
    private int _count;

    public void Add(TimeSpan gap)
    {
        _ms[_next] = (uint)Math.Clamp(gap.TotalMilliseconds, 0, uint.MaxValue);
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    /// <summary>Nearest-rank p50 and p99 of the kept gaps, or nulls without any.</summary>
    public (TimeSpan? P50, TimeSpan? P99) Percentiles()
    {
        if (_count == 0)
        {
            return (null, null);
        }

        Span<uint> sorted = stackalloc uint[Capacity];
        _ms.AsSpan(0, _count).CopyTo(sorted);
        sorted = sorted[.._count];
        sorted.Sort();
        return (TimeSpan.FromMilliseconds(Rank(sorted, 50)), TimeSpan.FromMilliseconds(Rank(sorted, 99)));
    }

    public void Clear()
    {
        Array.Clear(_ms);
        _next = 0;
        _count = 0;
    }

    private static uint Rank(ReadOnlySpan<uint> sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling((percentile / 100d * sorted.Length) - 1e-9), 1, sorted.Length) - 1];
}

/// <summary>
/// Counts per subject in bounded memory by the Space-Saving algorithm: up to <c>capacity</c> subjects are tracked exactly; when a new one arrives at capacity it
/// replaces the tracked subject with the lowest count and starts at that count plus one, so a heavy subject is never lost, a subject that returns after eviction may be
/// over-counted by at most the count it replaced, and <see cref="Count"/> saturates at the capacity. Not thread safe: the owner locks.
/// </summary>
internal sealed class SubjectTally(int capacity)
{
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly List<string> _victims = [];
    private long _victimCount;
    private const int VictimBatch = 64;

    /// <summary>Subjects tracked now (at most the capacity).</summary>
    public int Count => _counts.Count;

    public void Add(string subject)
    {
        ref long count = ref CollectionsMarshal.GetValueRefOrNullRef(_counts, subject);
        if (!Unsafe.IsNullRef(ref count))
        {
            count++;
            return;
        }

        if (_counts.Count < capacity)
        {
            _counts[subject] = 1;
            return;
        }

        string? lowest = null;
        long lowestCount = long.MaxValue;
        while (_victims.Count > 0)
        {
            // Counts only grow, so a candidate still at the minimum count is still a minimum of the whole tally.
            string candidate = _victims[^1];
            _victims.RemoveAt(_victims.Count - 1);
            if (_counts.TryGetValue(candidate, out long candidateCount) && candidateCount == _victimCount)
            {
                lowest = candidate;
                lowestCount = candidateCount;
                break;
            }
        }

        if (lowest is null)
        {
            // One scan collects every subject tied at the minimum (up to VictimBatch), so the next evictions need no scan.
            foreach (KeyValuePair<string, long> pair in _counts)
            {
                if (pair.Value < lowestCount)
                {
                    lowestCount = pair.Value;
                }
            }

            _victims.Clear();
            _victimCount = lowestCount;
            foreach (KeyValuePair<string, long> pair in _counts)
            {
                if (pair.Value == lowestCount && _victims.Count < VictimBatch)
                {
                    _victims.Add(pair.Key);
                }
            }

            lowest = _victims[^1];
            _victims.RemoveAt(_victims.Count - 1);
        }

        _counts.Remove(lowest);
        _counts[subject] = lowestCount + 1;
    }

    /// <summary>The <paramref name="n"/> highest counts, highest first, ties by name: a partial selection, O(tracked x n), no sort of the whole tally.</summary>
    public IReadOnlyList<SubjectCount> Top(int n)
    {
        var top = new List<SubjectCount>(Math.Min(n, _counts.Count) + 1);
        foreach (KeyValuePair<string, long> pair in _counts)
        {
            var entry = new SubjectCount(pair.Key, pair.Value);
            if (top.Count == n && !Before(entry, top[^1]))
            {
                continue;
            }

            int at = top.Count;
            while (at > 0 && Before(entry, top[at - 1]))
            {
                at--;
            }

            top.Insert(at, entry);
            if (top.Count > n)
            {
                top.RemoveAt(top.Count - 1);
            }
        }

        return top;
    }

    private static bool Before(SubjectCount a, SubjectCount b)
        => a.Count != b.Count ? a.Count > b.Count : string.CompareOrdinal(a.Subject, b.Subject) < 0;

    public void Clear()
    {
        _counts.Clear();
        _victims.Clear();
    }
}

/// <summary>The newest distinct strings, most recent first, deduplicated (a repeat moves to the front). Not thread safe: the owner locks.</summary>
internal sealed class RecentDistinct(int capacity)
{
    private readonly string[] _items = new string[capacity];
    private int _count;

    public void Add(string item)
    {
        int at = Array.IndexOf(_items, item, 0, _count);
        if (at < 0)
        {
            at = Math.Min(_count, capacity - 1);
            _count = Math.Min(_count + 1, capacity);
        }

        for (int i = at; i > 0; i--)
        {
            _items[i] = _items[i - 1];
        }

        _items[0] = item;
    }

    public IReadOnlyList<string> ToList() => [.. _items.Take(_count)];

    public void Clear()
    {
        Array.Clear(_items);
        _count = 0;
    }
}
