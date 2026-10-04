namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// A lock-free, allocation-free ring of the most recent samples (tick frame times in
/// microseconds). Ownership: exactly one writer thread (the world thread) calls
/// <see cref="Record"/>; any number of readers call <see cref="Snapshot"/>. The writer publishes
/// a slot with a release store of the count; a reader takes the count, copies, and re-reads the
/// count to drop every slot the writer may have overwritten meanwhile, so a snapshot never
/// contains a torn or mixed-generation sample. <see cref="Record"/> is two stores and never
/// blocks, allocates or spins; <see cref="Snapshot"/> writes into a caller-supplied buffer.
/// </summary>
public sealed class TickRing
{
    public const int MinCapacity = 16;

    public const int MaxCapacity = 1 << 20;

    private readonly long[] _slots;
    private readonly int _mask;
    private long _count;

    /// <summary><paramref name="capacity"/> is clamped to [16, 1048576] and rounded up to a power of two.</summary>
    public TickRing(int capacity)
    {
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(capacity, MinCapacity, MaxCapacity));
        _slots = new long[size];
        _mask = size - 1;
    }

    /// <summary>Slots in the ring; a snapshot returns at most one fewer samples (the slot the writer may be filling is never read).</summary>
    public int Capacity => _slots.Length;

    /// <summary>Samples recorded since creation (not the samples retained).</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Append one sample (writer thread only).</summary>
    public void Record(long value)
    {
        long count = _count;
        _slots[count & _mask] = value;
        Volatile.Write(ref _count, count + 1);
    }

    /// <summary>
    /// Copy the most recent samples, oldest first, into <paramref name="destination"/> (any
    /// thread). Returns how many were copied; fewer than requested when the ring holds fewer, or
    /// when the writer overwrote the oldest ones during the copy.
    /// </summary>
    public int Snapshot(Span<long> destination)
    {
        // At most Capacity - 1 samples are ever safe to read: the slot of the oldest one is the slot the writer's
        // next, not yet published, store lands on.
        long before = Volatile.Read(ref _count);
        int wanted = (int)Math.Min(before, Math.Min(_slots.Length - 1, destination.Length));
        if (wanted == 0)
        {
            return 0;
        }

        long first = before - wanted;
        for (int i = 0; i < wanted; i++)
        {
            destination[i] = Volatile.Read(ref _slots[(first + i) & _mask]);
        }

        // Every index up to and including (after - Capacity) may have been overwritten while copying: the writer
        // stores the slot of index `after` before it publishes count = after + 1, and that store lands on the slot
        // of index (after - Capacity). Drop them all.
        long after = Volatile.Read(ref _count);
        int skip = (int)Math.Clamp(after - _slots.Length - first + 1, 0, wanted);
        if (skip > 0)
        {
            destination.Slice(skip, wanted - skip).CopyTo(destination);
        }

        return wanted - skip;
    }
}

/// <summary>Nearest-rank statistics over a sample buffer. <see cref="Samples"/> == 0 means "no measurement": the other fields are 0 and must not be read as values.</summary>
public readonly record struct RingStats(int Samples, long P50, long P90, long P99, long Max, long Mean)
{
    public static RingStats Empty => default;

    /// <summary>Sort <paramref name="samples"/> in place and compute the statistics (allocation-free).</summary>
    public static RingStats Compute(Span<long> samples)
    {
        if (samples.IsEmpty)
        {
            return Empty;
        }

        samples.Sort();
        long sum = 0;
        foreach (long sample in samples)
        {
            sum += sample;
        }

        return new RingStats(samples.Length, NearestRank(samples, 50), NearestRank(samples, 90), NearestRank(samples, 99), samples[^1], sum / samples.Length);
    }

    /// <summary>The value at 1-based rank ceil(p/100 * n) of an ascending span; empty input throws rather than answering 0.</summary>
    public static long NearestRank(ReadOnlySpan<long> sortedAscending, double percentile)
    {
        if (sortedAscending.IsEmpty)
        {
            throw new ArgumentException("percentile of an empty sample is undefined", nameof(sortedAscending));
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Length - 1e-9);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }
}
