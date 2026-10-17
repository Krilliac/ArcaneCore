using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// Where the population manager reads the world's tick time from. The production source is <see cref="WorldTickWindow"/>; tests supply
/// a fake with a chosen p95 sequence (a direct <c>RunTick</c> does not raise <c>WorldRuntime.TickCompleted</c>).
/// </summary>
internal interface IPopulationTickSource
{
    /// <summary>The tick durations recorded since the last <see cref="Reset"/>, at most <see cref="WorldTickWindow.Capacity"/>.</summary>
    int Samples { get; }

    /// <summary>Every tick recorded since the source was created; <see cref="Reset"/> does not lower it. Two readings differ by the ticks that arrived between them.</summary>
    long TotalSamples { get; }

    /// <summary>The 95th percentile (nearest rank) of the last <paramref name="windowTicks"/> recorded ticks, in milliseconds (0 without samples).</summary>
    double P95Ms(int windowTicks);

    /// <summary>Forget every sample, so the next decision waits for ticks measured after the change that caused the reset.</summary>
    void Reset();
}

/// <summary>
/// A ring of the last <see cref="Capacity"/> tick body durations, fed from <c>WorldRuntime.TickCompleted</c>
/// (src/ArcaneCore.Game/Maps/WorldRuntime.cs, raised on the world thread after <c>Stats.Record</c>). <c>WorldRuntime.Stats</c> keeps a
/// 4096-tick ring too, but its snapshot copies and sorts all of it (about 3.4 minutes at 50 ms, too slow to steer a 10 s loop and
/// allocating); this window can be reset after every change and sorts only the ticks asked for.
/// <para>Thread affinity: <see cref="Add"/>, <see cref="P95Ms"/>, <see cref="Reset"/> and <see cref="Samples"/> are world-thread only (no lock). <see cref="Add"/> does not allocate.</para>
/// </summary>
internal sealed class WorldTickWindow : IPopulationTickSource
{
    /// <summary>The ring size (the largest WindowTicks the options accept).</summary>
    public const int Capacity = 4096;

    private readonly long[] _ring = new long[Capacity];
    private readonly long[] _scratch = new long[Capacity];
    private int _next;
    private int _count;
    private long _total;

    public int Samples => _count;

    public long TotalSamples => _total;

    /// <summary>Record one tick body duration in microseconds (negative values are recorded as 0).</summary>
    public void Add(long micros)
    {
        _ring[_next] = micros < 0 ? 0 : micros;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        _total++;
    }

    public double P95Ms(int windowTicks)
    {
        int n = Math.Min(_count, Math.Clamp(windowTicks, 1, Capacity));
        if (n == 0) return 0;
        for (int i = 0; i < n; i++) _scratch[i] = _ring[(_next - 1 - i + Capacity) % Capacity];
        Span<long> window = _scratch.AsSpan(0, n);
        window.Sort();
        return TickStats.NearestRank(window, 95) / 1000.0;
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }
}
