using System.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>An immutable view of <see cref="TickStats"/>. Percentiles are 0 when <see cref="Samples"/> is 0: check it first.</summary>
public sealed record TickStatsSnapshot(
    long TotalTicks,
    int Samples,
    long Overruns,
    long P50Micros,
    long P90Micros,
    long P99Micros,
    long P999Micros,
    long MaxMicros,
    double MeanMicros,
    double MeanAllocatedBytes,
    long MaxAllocatedBytes,
    long LastTickTimestamp)
{
    public int FrameSamples { get; init; }
    public double MeanFrameIntervalMicros { get; init; }
    public double? EffectiveTicksPerSecond { get; init; }
    public long FrameOverruns { get; init; }
}

/// <summary>
/// Fixed-size ring of recent world-tick durations and allocated bytes. One writer (the world
/// thread) records; any thread may take a snapshot, so monitoring never waits on the world
/// thread beyond a few instructions of a lock held only to copy the ring. Recording does not
/// allocate.
/// </summary>
public sealed class TickStats
{
    private readonly object _gate = new();
    private readonly long[] _durations;
    private readonly long[] _allocated;
    private readonly long[] _frameIntervals;
    private long _total;
    private long _overruns;
    private long _frameOverruns;
    private long _lastTimestamp;

    public TickStats(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _durations = new long[capacity];
        _allocated = new long[capacity];
        _frameIntervals = new long[capacity];
    }

    /// <summary>
    /// Record one completed tick. An overrun is a tick strictly longer than its interval.
    /// <paramref name="intervalMicros"/> is the configured tick length.
    /// </summary>
    public void Record(long durationMicros, long allocatedBytes, long intervalMicros, long frameIntervalMicros = 0)
    {
        long stamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            int slot = (int)(_total % _durations.Length);
            _durations[slot] = durationMicros;
            _allocated[slot] = allocatedBytes;
            _frameIntervals[slot] = frameIntervalMicros;
            _total++;
            if (durationMicros > intervalMicros)
            {
                _overruns++;
            }
            if (frameIntervalMicros > intervalMicros)
            {
                _frameOverruns++;
            }

            _lastTimestamp = stamp;
        }
    }

    /// <summary>Copy the ring and compute nearest-rank percentiles.</summary>
    public TickStatsSnapshot Snapshot()
    {
        long[] durations;
        long total, overruns, frameOverruns, stamp;
        double meanAlloc = 0;
        long maxAlloc = 0;
        double meanFrame = 0;
        int frameSamples = 0;
        lock (_gate)
        {
            int count = (int)Math.Min(_total, _durations.Length);
            durations = new long[count];
            Array.Copy(_durations, durations, count);
            long allocSum = 0;
            for (int i = 0; i < count; i++)
            {
                allocSum += _allocated[i];
                maxAlloc = Math.Max(maxAlloc, _allocated[i]);
            }

            meanAlloc = count == 0 ? 0 : (double)allocSum / count;
            total = _total;
            overruns = _overruns;
            frameOverruns = _frameOverruns;
            stamp = _lastTimestamp;
            long frameSum = 0;
            for (int i = 0; i < count; i++)
            {
                if (_frameIntervals[i] > 0)
                {
                    frameSum += _frameIntervals[i];
                    frameSamples++;
                }
            }
            meanFrame = frameSamples == 0 ? 0 : (double)frameSum / frameSamples;
        }

        if (durations.Length == 0)
        {
            return new TickStatsSnapshot(total, 0, overruns, 0, 0, 0, 0, 0, 0, 0, 0, stamp)
            {
                FrameOverruns = frameOverruns,
            };
        }

        Array.Sort(durations);
        return new TickStatsSnapshot(
            total,
            durations.Length,
            overruns,
            NearestRank(durations, 50),
            NearestRank(durations, 90),
            NearestRank(durations, 99),
            NearestRank(durations, 99.9),
            durations[^1],
            durations.Average(),
            meanAlloc,
            maxAlloc,
            stamp)
        {
            FrameSamples = frameSamples,
            MeanFrameIntervalMicros = meanFrame,
            EffectiveTicksPerSecond = meanFrame > 0 ? 1_000_000d / meanFrame : null,
            FrameOverruns = frameOverruns,
        };
    }

    /// <summary>
    /// Nearest-rank percentile of ascending-sorted values: the value at 1-based rank
    /// ceil(p/100 * n). Empty input throws rather than returning a reassuring 0.
    /// </summary>
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
