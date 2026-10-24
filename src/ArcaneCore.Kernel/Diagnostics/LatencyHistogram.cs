using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// A point-in-time summary of a <see cref="LatencyHistogram"/> window. Values are in the unit that was recorded (the server records
/// microseconds); <see cref="Count"/> == 0 means "no measurement": every other field is 0 and must not be read as a value.
/// Percentiles are bucket upper bounds clamped to [<see cref="Min"/>, <see cref="Max"/>]: never below the exact nearest-rank value and at most
/// <see cref="LatencyHistogram.RelativeError"/> above it (exact below 32). <see cref="Min"/>, <see cref="Max"/>, <see cref="Mean"/> and <see cref="StdDev"/> are exact.
/// </summary>
public readonly record struct LatencySnapshot(long Count, long Min, long Max, double Mean, double StdDev, long P50, long P75, long P90, long P95, long P99, long P999)
{
    /// <summary>No samples.</summary>
    public static LatencySnapshot Empty => default;

    /// <summary>True when nothing was recorded in the window.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// The one text form of a latency distribution, for microsecond values shown in milliseconds:
    /// <c>p50 4.1 ms p75 5.9 p90 7.9 p95 9.2 p99 18.0 p99.9 41.3 max 52.0 (n=12000, min 0.8, mean 4.6 +/-2.1)</c>.
    /// Culture invariant; <c>no samples</c> when empty.
    /// </summary>
    public string Format()
    {
        if (IsEmpty)
        {
            return "no samples";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"p50 {Millis(P50)} ms p75 {Millis(P75)} p90 {Millis(P90)} p95 {Millis(P95)} p99 {Millis(P99)} p99.9 {Millis(P999)} max {Millis(Max)} (n={Count}, min {Millis(Min)}, mean {Millis(Mean)} +/-{Millis(StdDev)})");
    }

    /// <inheritdoc/>
    public override string ToString() => Format();

    /// <summary>Microseconds as milliseconds: one decimal, three below 0.1 ms so a fast path does not read as 0.0.</summary>
    public static string Millis(double micros)
    {
        double ms = micros / 1000d;
        return ms.ToString(ms < 0.1 && ms > 0 ? "F3" : "F1", CultureInfo.InvariantCulture);
    }
}

/// <summary>The three views of one histogram that the logs and commands show: the last minute, the last 15 minutes and everything since start.</summary>
public readonly record struct LatencyWindows(LatencySnapshot Last1Min, LatencySnapshot Last15Min, LatencySnapshot Total)
{
    /// <summary>The 1 min, 15 min and total lines on one line each, joined with <c>; </c>: <c>last 1 min: ...; last 15 min: ...; since start: ...</c>.</summary>
    public string Format() => $"last 1 min: {Last1Min.Format()}; last 15 min: {Last15Min.Format()}; since start: {Total.Format()}";
}

/// <summary>
/// A fixed-memory, allocation-free, HDR-style latency histogram (the shared latency primitive of the diagnostics).
/// <para>
/// Layout: values 0..31 have a bucket each; above that every power-of-two range is split into 32 linear sub-buckets, up to <see cref="MaxValue"/>
/// (2^40, about 12.7 days of microseconds; larger values are clamped to it).
/// A percentile is therefore exact below 32 and otherwise at most <see cref="RelativeError"/> (1/32, 3.1 %) above the exact nearest-rank value, never below.
/// Count, minimum, maximum, mean and standard deviation are exact.
/// </para>
/// <para>
/// Windows: the samples are also kept in <c>slices</c> ring slices of <c>sliceLength</c> each (default 45 x 20 s = 15 minutes), so
/// <see cref="Snapshot(TimeSpan)"/> answers "the last 1 min" and "the last 15 min" besides the totals since start. A window of W covers the
/// ceil(W / sliceLength) newest slices including the current, partly filled one, so it spans between W - sliceLength and W of recent time. A slice's
/// bucket array is allocated the first time it is used. A bucket array is <see cref="BucketCount"/> x 8 = 9,216 bytes, so the default configuration (45 slices
/// plus the since-start array) costs up to 46 x 9 KB = about 424 KB when busy and about 20 KB when idle. A caller that keeps many histograms uses the compact
/// configuration (<see cref="CompactSlices"/> slices of <see cref="CompactSliceLength"/>, no since-start array): at most 3 x 9 KB = 27 KB each, whose smallest window is one
/// 5 minute slice.
/// </para>
/// <para>
/// Range: a value above <see cref="MaxValue"/> counts in the last bucket and is recorded as <see cref="MaxValue"/> everywhere (the maximum, the mean and every percentile),
/// so for such values the reported figures are clamped and the "never below the exact value" guarantee does not hold; this also keeps the sums far from overflow.
/// </para>
/// <para>
/// Thread safety: any number of threads may call <see cref="Record(long)"/> and snapshot concurrently, lock-free on the hot path (about a dozen
/// interlocked operations, no allocation, no lock; a slice rotation, once per sliceLength, takes one short lock). A snapshot is not atomic: a sample
/// being recorded during it may be counted in the buckets but not yet in the sum (or the other way round), a difference of at most the samples in
/// flight. A writer that is stalled for a whole ring while recording finds its slice already holding newer samples and then counts only in the totals (the slice is never erased for it).
/// <see cref="Reset"/> must not run concurrently with recording if exact totals matter.
/// </para>
/// </summary>
public sealed class LatencyHistogram
{
    /// <summary>Linear sub-buckets per power of two = 2^this.</summary>
    public const int SubBucketBits = 5;

    private const int SubCount = 1 << SubBucketBits;

    /// <summary>The largest value that has a bucket of its own: 2^40 - 1.</summary>
    public const long MaxValue = (1L << 40) - 1;

    /// <summary>Buckets per histogram (one array of this many counters per slice).</summary>
    public const int BucketCount = (40 - SubBucketBits + 1) << SubBucketBits;

    /// <summary>The most a reported percentile may exceed the exact nearest-rank value, as a fraction of it (values of 32 and above).</summary>
    public const double RelativeError = 1.0 / SubCount;

    /// <summary>Default number of slices.</summary>
    public const int DefaultSlices = 45;

    /// <summary>Default length of one slice.</summary>
    public static readonly TimeSpan DefaultSliceLength = TimeSpan.FromSeconds(20);

    /// <summary>Slices of the compact configuration (3 x 5 minutes = 15 minutes).</summary>
    public const int CompactSlices = 3;

    /// <summary>Slice length of the compact configuration.</summary>
    public static readonly TimeSpan CompactSliceLength = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly long _sliceMicros;
    private readonly Slice[] _slices;
    private readonly object _rotateGate = new();
    private readonly long[]? _totalBuckets;
    private readonly Totals _total = new();

    /// <summary>A histogram on the system clock with the default 45 x 20 s windows (15 minutes).</summary>
    public LatencyHistogram()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// A histogram on <paramref name="time"/> (tests drive a manual clock) with <paramref name="slices"/> ring slices of <paramref name="sliceLength"/>.
    /// With <paramref name="keepTotals"/> false there is no since-start bucket array: <see cref="Snapshot()"/> then answers the whole retained span
    /// (<see cref="Span"/>) and <see cref="Count"/> still counts since start.
    /// </summary>
    public LatencyHistogram(TimeProvider time, TimeSpan? sliceLength = null, int slices = DefaultSlices, bool keepTotals = true)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        TimeSpan length = sliceLength ?? DefaultSliceLength;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        _time = time;
        _totalBuckets = keepTotals ? new long[BucketCount] : null;
        _sliceMicros = Math.Max(1, length.Ticks / (TimeSpan.TicksPerMillisecond / 1000));
        _slices = new Slice[slices];
        for (int i = 0; i < _slices.Length; i++)
        {
            _slices[i] = new Slice();
        }
    }

    /// <summary>Samples recorded since start (or <see cref="Reset"/>).</summary>
    public long Count => Volatile.Read(ref _total.Count);

    /// <summary>The longest window <see cref="Snapshot(TimeSpan)"/> can answer.</summary>
    public TimeSpan Span => TimeSpan.FromTicks(_sliceMicros * _slices.Length * (TimeSpan.TicksPerMillisecond / 1000));

    /// <summary>Record one value (negative values count as 0, values above <see cref="MaxValue"/> in the last bucket) at the current time. Any thread.</summary>
    public void Record(long value) => Record(value, NowMicros());

    /// <summary>Record one value at <paramref name="nowMicros"/>, a time on the same axis the snapshots will use (<see cref="Snapshot(TimeSpan, long)"/>). Any thread.</summary>
    public void Record(long value, long nowMicros)
    {
        if (value < 0)
        {
            value = 0;
        }

        long epoch = nowMicros / _sliceMicros;
        Slice? slice = _slices[(int)(epoch % _slices.Length)];
        if (Volatile.Read(ref slice.Epoch) != epoch)
        {
            slice = Rotate(epoch);
        }

        int bucket = BucketOf(value);
        long clamped = Math.Min(value, MaxValue);

        // A null slice: this writer stalled past a whole ring and its slice now holds newer samples; the sample counts in the totals only.
        if (slice is not null)
        {
            Interlocked.Increment(ref slice.Buckets![bucket]);
            slice.Stats.Add(clamped);
        }

        if (_totalBuckets is not null)
        {
            Interlocked.Increment(ref _totalBuckets[bucket]);
        }

        _total.Add(clamped);
    }

    /// <summary>Everything recorded since start. Any thread; allocation-free.</summary>
    public LatencySnapshot Snapshot()
    {
        if (_totalBuckets is null)
        {
            return Snapshot(Span);
        }

        Span<long> merged = stackalloc long[BucketCount];
        for (int i = 0; i < merged.Length; i++)
        {
            merged[i] = Volatile.Read(ref _totalBuckets[i]);
        }

        return Build(merged, _total.Read());
    }

    /// <summary>The newest ceil(<paramref name="window"/> / slice length) slices (at most <see cref="Span"/>) at the current time. Any thread; allocation-free.</summary>
    public LatencySnapshot Snapshot(TimeSpan window) => Snapshot(window, NowMicros());

    /// <summary><see cref="Snapshot(TimeSpan)"/> at <paramref name="nowMicros"/>, the axis <see cref="Record(long, long)"/> used.</summary>
    public LatencySnapshot Snapshot(TimeSpan window, long nowMicros)
    {
        long windowMicros = Math.Max(1, window.Ticks / (TimeSpan.TicksPerMillisecond / 1000));
        long wanted = Math.Clamp((windowMicros + _sliceMicros - 1) / _sliceMicros, 1, _slices.Length);
        long current = nowMicros / _sliceMicros;
        long oldest = current - wanted + 1;
        Span<long> merged = stackalloc long[BucketCount];
        StatsRead stats = StatsRead.Zero;
        foreach (Slice slice in _slices)
        {
            long epoch = Volatile.Read(ref slice.Epoch);
            if (epoch < oldest || epoch > current || slice.Buckets is not { } buckets)
            {
                continue;
            }

            for (int i = 0; i < merged.Length; i++)
            {
                merged[i] += Volatile.Read(ref buckets[i]);
            }

            stats.Merge(slice.Stats.Read());
        }

        return Build(merged, stats);
    }

    /// <summary>The last minute, the last 15 minutes (clamped to <see cref="Span"/>) and the totals, at the current time.</summary>
    public LatencyWindows Windows()
    {
        long now = NowMicros();
        return new LatencyWindows(Snapshot(TimeSpan.FromMinutes(1), now), Snapshot(TimeSpan.FromMinutes(15), now), Snapshot());
    }

    /// <summary>Forget every sample (tests, the operator reset). Not safe against concurrent recording if exact totals matter.</summary>
    public void Reset()
    {
        lock (_rotateGate)
        {
            foreach (Slice slice in _slices)
            {
                Volatile.Write(ref slice.Epoch, long.MinValue);
                if (slice.Buckets is { } buckets)
                {
                    Array.Clear(buckets);
                }

                slice.Stats.Clear();
            }

            if (_totalBuckets is not null)
            {
                Array.Clear(_totalBuckets);
            }

            _total.Clear();
        }
    }

    /// <summary>The bucket a value counts in.</summary>
    internal static int BucketOf(long value)
    {
        if (value < SubCount)
        {
            return (int)Math.Max(0, value);
        }

        if (value > MaxValue)
        {
            return BucketCount - 1;
        }

        int msb = 63 - BitOperations.LeadingZeroCount((ulong)value);
        int shift = msb - SubBucketBits;
        return ((msb - SubBucketBits + 1) << SubBucketBits) + (int)((value >> shift) - SubCount);
    }

    /// <summary>The largest value that counts in <paramref name="bucket"/>.</summary>
    internal static long UpperBoundOf(int bucket)
    {
        if (bucket < SubCount)
        {
            return bucket;
        }

        int octave = bucket >> SubBucketBits;
        int shift = octave - 1;
        long lower = (long)(SubCount + (bucket & (SubCount - 1))) << shift;
        return lower + (1L << shift) - 1;
    }

    private long NowMicros() => (long)(_time.GetTimestamp() * (1_000_000d / _time.TimestampFrequency));

    private Slice? Rotate(long epoch)
    {
        Slice slice = _slices[(int)(epoch % _slices.Length)];
        lock (_rotateGate)
        {
            long current = Volatile.Read(ref slice.Epoch);
            if (current > epoch)
            {
                // A writer that stalled for a whole ring: the slice already belongs to a newer interval and must not be erased for an old sample.
                return null;
            }

            if (current != epoch)
            {
                if (slice.Buckets is null)
                {
                    slice.Buckets = new long[BucketCount];
                }
                else
                {
                    Array.Clear(slice.Buckets);
                }

                slice.Stats.Clear();
                Volatile.Write(ref slice.Epoch, epoch);
            }
        }

        return slice;
    }

    // A static array: a collection-expression span of doubles is not a constant blob and would allocate on every snapshot.
    private static readonly double[] PercentileTargets = [50, 75, 90, 95, 99, 99.9];

    private static LatencySnapshot Build(ReadOnlySpan<long> buckets, in StatsRead stats)
    {
        long n = 0;
        foreach (long count in buckets)
        {
            n += count;
        }

        if (n == 0 || stats.Count == 0)
        {
            return LatencySnapshot.Empty;
        }

        Span<long> rank = stackalloc long[6];
        double[] percentiles = PercentileTargets;
        for (int i = 0; i < rank.Length; i++)
        {
            rank[i] = Math.Clamp((long)Math.Ceiling(percentiles[i] / 100d * n - 1e-9), 1, n);
        }

        Span<long> value = stackalloc long[6];
        int next = 0;
        long cumulative = 0;
        for (int bucket = 0; bucket < buckets.Length && next < rank.Length; bucket++)
        {
            cumulative += buckets[bucket];
            if (buckets[bucket] == 0)
            {
                continue;
            }

            long upper = Math.Max(stats.Min, Math.Min(UpperBoundOf(bucket), stats.Max));
            while (next < rank.Length && cumulative >= rank[next])
            {
                value[next++] = upper;
            }
        }

        while (next < rank.Length)
        {
            value[next++] = stats.Max;
        }

        double mean = (double)stats.Sum / stats.Count;
        double variance = Math.Max(0, (stats.SumSquares / stats.Count) - (mean * mean));
        return new LatencySnapshot(stats.Count, stats.Min, stats.Max, mean, Math.Sqrt(variance), value[0], value[1], value[2], value[3], value[4], value[5]);
    }

    private sealed class Slice
    {
        public long Epoch = long.MinValue;
        public long[]? Buckets;
        public readonly Totals Stats = new();
    }

    /// <summary>The non-bucket statistics of one slice or of the totals; <see cref="Zero"/> is the identity of <see cref="Merge"/>.</summary>
    private struct StatsRead
    {
        public long Count;
        public long Sum;
        public double SumSquares;
        public long Min;
        public long Max;

        public static StatsRead Zero => new() { Min = long.MaxValue };

        public void Merge(in StatsRead other)
        {
            if (other.Count == 0)
            {
                return;
            }

            Count += other.Count;
            Sum += other.Sum;
            SumSquares += other.SumSquares;
            Min = Math.Min(Min, other.Min);
            Max = Math.Max(Max, other.Max);
        }
    }

    private sealed class Totals
    {
        public long Count;
        public long Sum;
        private long _sumSquaresBits;
        private long _min = long.MaxValue;
        private long _max;

        public void Add(long exact)
        {
            Interlocked.Add(ref Sum, exact);
            double square = (double)exact * exact;
            long seen = Volatile.Read(ref _sumSquaresBits);
            while (true)
            {
                long updated = BitConverter.DoubleToInt64Bits(BitConverter.Int64BitsToDouble(seen) + square);
                long actual = Interlocked.CompareExchange(ref _sumSquaresBits, updated, seen);
                if (actual == seen)
                {
                    break;
                }

                seen = actual;
            }

            long min = Volatile.Read(ref _min);
            while (exact < min)
            {
                long actual = Interlocked.CompareExchange(ref _min, exact, min);
                if (actual == min)
                {
                    break;
                }

                min = actual;
            }

            long max = Volatile.Read(ref _max);
            while (exact > max)
            {
                long actual = Interlocked.CompareExchange(ref _max, exact, max);
                if (actual == max)
                {
                    break;
                }

                max = actual;
            }

            // Count last: a reader that sees a sample counted also sees its minimum and maximum.
            Interlocked.Increment(ref Count);
        }

        public StatsRead Read() => new()
        {
            Count = Volatile.Read(ref Count),
            Sum = Volatile.Read(ref Sum),
            SumSquares = BitConverter.Int64BitsToDouble(Volatile.Read(ref _sumSquaresBits)),
            Min = Volatile.Read(ref _min),
            Max = Volatile.Read(ref _max),
        };

        public void Clear()
        {
            Volatile.Write(ref Count, 0);
            Volatile.Write(ref Sum, 0);
            Volatile.Write(ref _sumSquaresBits, 0);
            Volatile.Write(ref _min, long.MaxValue);
            Volatile.Write(ref _max, 0);
        }
    }
}

/// <summary>
/// The process-wide, name-keyed set of latency histograms the periodic summary prints (<c>db.character_save</c>, <c>nav.path_query</c>, <c>login.to_world</c>,
/// <c>packet.handler.CMSG_...</c>). Bounded: at most <see cref="MaxNames"/> names, of which at most <see cref="MaxDefaultNames"/> use the default layout; a request beyond either
/// cap returns the single <see cref="OverflowName"/> histogram, so a caller that builds names from data can never grow memory without bound. Memory: a name under
/// <see cref="LatencyNames.PacketHandlerPrefix"/> (up to 261 opcodes) and the overflow histogram use the compact configuration (at most 27 KB each, typically a few KB for an
/// opcode that is rarely handled), every other name the default one (up to 424 KB each; the server registers fewer than ten). The hard ceiling is 16 x 424 KB + 368 x 27 KB,
/// about 17 MB; the typical total is 1 to 3 MB. Any thread; <see cref="Get"/> is a dictionary lookup on the hot path.
/// </summary>
public sealed class LatencyRegistry
{
    /// <summary>The most distinct histograms the registry holds.</summary>
    public const int MaxNames = 384;

    /// <summary>The most histograms with the default (424 KB) layout; further names that are not packet handlers go to the overflow histogram.</summary>
    public const int MaxDefaultNames = 16;

    /// <summary>The name of the histogram that takes everything beyond <see cref="MaxNames"/>.</summary>
    public const string OverflowName = "(overflow)";

    private readonly ConcurrentDictionary<string, LatencyHistogram> _histograms = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly LatencyHistogram _overflow;
    private readonly object _addGate = new();
    private int _defaultNames;

    /// <summary>The process-wide registry.</summary>
    public static LatencyRegistry Shared { get; } = new();

    /// <summary>A registry on the system clock.</summary>
    public LatencyRegistry()
        : this(TimeProvider.System)
    {
    }

    /// <summary>A registry whose histograms read <paramref name="time"/> (tests drive a manual clock).</summary>
    public LatencyRegistry(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _overflow = new LatencyHistogram(time, LatencyHistogram.CompactSliceLength, LatencyHistogram.CompactSlices, keepTotals: false);
    }

    /// <summary>The histogram called <paramref name="name"/>, created on first use.</summary>
    public LatencyHistogram Get(string name)
    {
        if (_histograms.TryGetValue(name, out LatencyHistogram? existing))
        {
            return existing;
        }

        lock (_addGate)
        {
            if (_histograms.TryGetValue(name, out existing))
            {
                return existing;
            }

            if (_histograms.Count >= MaxNames)
            {
                return _overflow;
            }

            bool compact = name.StartsWith(LatencyNames.PacketHandlerPrefix, StringComparison.Ordinal);
            if (!compact && _defaultNames >= MaxDefaultNames)
            {
                return _overflow;
            }

            LatencyHistogram created = compact
                ? new LatencyHistogram(_time, LatencyHistogram.CompactSliceLength, LatencyHistogram.CompactSlices, keepTotals: false)
                : new LatencyHistogram(_time);
            if (!compact)
            {
                _defaultNames++;
            }

            _histograms[name] = created;
            return created;
        }
    }

    /// <summary>Record the time since <paramref name="startTimestamp"/> (a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading) under <paramref name="name"/>, in microseconds.</summary>
    public void RecordSince(string name, long startTimestamp) => Get(name).Record(System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000));

    /// <summary>Record <paramref name="micros"/> under <paramref name="name"/> (<c>Get(name).Record(micros)</c>).</summary>
    public void Record(string name, long micros) => Get(name).Record(micros);

    /// <summary>Every histogram that has samples, with its name (the overflow one included when used), in name order.</summary>
    public IReadOnlyList<KeyValuePair<string, LatencyHistogram>> Histograms()
    {
        var list = new List<KeyValuePair<string, LatencyHistogram>>(_histograms.Count + 1);
        foreach (KeyValuePair<string, LatencyHistogram> pair in _histograms)
        {
            if (pair.Value.Count > 0)
            {
                list.Add(pair);
            }
        }

        if (_overflow.Count > 0)
        {
            list.Add(new KeyValuePair<string, LatencyHistogram>(OverflowName, _overflow));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    /// <summary>
    /// The <paramref name="count"/> histograms whose name starts with <paramref name="prefix"/> that have the highest p99 over the last <paramref name="window"/> (histograms with no
    /// sample in the window are left out), slowest first, each with its snapshot of that window. The prefix <c>packet.handler.</c> ranks the opcodes. One window merge per
    /// candidate, on the caller's thread; meant for a periodic pass or an operator command, not a hot path.
    /// </summary>
    public IReadOnlyList<(string Name, LatencyHistogram Histogram, LatencySnapshot Recent)> TopByP99(string prefix, int count, TimeSpan window)
    {
        var ranked = new List<(string Name, LatencyHistogram Histogram, LatencySnapshot Recent)>();
        foreach (KeyValuePair<string, LatencyHistogram> pair in _histograms)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.Count > 0)
            {
                LatencySnapshot recent = pair.Value.Snapshot(window);
                if (!recent.IsEmpty)
                {
                    ranked.Add((pair.Key, pair.Value, recent));
                }
            }
        }

        ranked.Sort(static (a, b) =>
        {
            int byP99 = b.Recent.P99.CompareTo(a.Recent.P99);
            if (byP99 != 0)
            {
                return byP99;
            }

            int byMax = b.Recent.Max.CompareTo(a.Recent.Max);
            return byMax != 0 ? byMax : string.CompareOrdinal(a.Name, b.Name);
        });
        if (ranked.Count > count)
        {
            ranked.RemoveRange(count, ranked.Count - count);
        }

        return ranked;
    }

    /// <summary>Forget every sample (tests). The histograms and their names stay, so a caller that cached one keeps recording into the registry.</summary>
    public void Reset()
    {
        lock (_addGate)
        {
            foreach (LatencyHistogram histogram in _histograms.Values)
            {
                histogram.Reset();
            }

            _overflow.Reset();
        }
    }
}

/// <summary>The names of the latency histograms the server feeds into <see cref="LatencyRegistry.Shared"/> (all in microseconds).</summary>
public static class LatencyNames
{
    /// <summary>One successful character write by the save queue.</summary>
    public const string CharacterSave = "db.character_save";

    /// <summary>One SQLite WAL checkpoint; the database file name follows.</summary>
    public const string CheckpointPrefix = "db.checkpoint.";

    /// <summary>One navigation path query (<c>NavMeshPathfinder.FindPath</c>).</summary>
    public const string PathQuery = "nav.path_query";

    /// <summary>A bot path job, from its submission to the answer being applied on the world thread (wall time).</summary>
    public const string BotPathJobWait = "nav.bot_path_job_wait";

    /// <summary>From the client's CMSG_PLAYER_LOGIN to the player being in the world.</summary>
    public const string LoginToWorld = "login.to_world";

    /// <summary>One in-world packet handler run; the opcode name follows.</summary>
    public const string PacketHandlerPrefix = "packet.handler.";
}
