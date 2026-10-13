namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What the bots' think cadence looked like over a report window (<see cref="PlayerbotCadenceStats.Report"/>).
/// </summary>
/// <param name="Bots">Bots running at the end of the window.</param>
/// <param name="Thinks">Thinks made in the window.</param>
/// <param name="Ticks">World ticks that thought for bots.</param>
/// <param name="GapP50Ms">Median time between two thinks of one bot (to the histogram's bucket, <see cref="PlayerbotCadenceStats.BucketMs"/>).</param>
/// <param name="GapP95Ms">95th percentile of that time.</param>
/// <param name="GapMaxMs">Longest time between two thinks of one bot (exact).</param>
/// <param name="ThinksPerTick">Thinks per tick, averaged over the ticks.</param>
/// <param name="TimeLimitedPct">Percent of the ticks that stopped because <see cref="PlayerbotThinkRotation.TickBudgetMs"/> went by.</param>
/// <param name="ActionLimitedPct">Percent of the ticks that stopped because the shared action budget (<c>MaxActionsPerTick</c>) was spent.</param>
internal readonly record struct PlayerbotCadenceReport(int Bots, long Thinks, int Ticks, uint GapP50Ms, uint GapP95Ms, uint GapMaxMs,
    double ThinksPerTick, double TimeLimitedPct, double ActionLimitedPct);

/// <summary>
/// The bots' think cadence, measured: the time between the thinks of one bot (a fixed histogram), the thinks per tick, and how often a
/// tick ended on the time budget or on the action budget. One <see cref="Report"/> per <see cref="ReportIntervalMs"/>, then the window
/// starts again. Nothing in it names a bot. World thread only. Allocation-free after construction: the histogram is one array made once.
/// </summary>
internal sealed class PlayerbotCadenceStats
{
    /// <summary>The window between two reports: 60 s of world time.</summary>
    internal const uint ReportIntervalMs = 60_000;

    /// <summary>Width of one histogram bucket (ms): the resolution of the percentiles.</summary>
    internal const uint BucketMs = 100;

    /// <summary>Buckets up to 30 s; the last one holds every longer gap.</summary>
    private const int Buckets = 301;

    private readonly int[] _gaps = new int[Buckets];
    private long _gapCount;
    private uint _gapMax;
    private long _thinks;
    private int _ticks;
    private int _timeLimited;
    private int _actionLimited;
    private uint _windowMs;

    /// <summary>One bot's think came <paramref name="sinceLastMs"/> ms after its previous one.</summary>
    internal void RecordGap(uint sinceLastMs)
    {
        if (sinceLastMs > int.MaxValue) return;
        _gaps[(int)Math.Min(sinceLastMs / BucketMs, Buckets - 1)]++;
        _gapCount++;
        if (sinceLastMs > _gapMax) _gapMax = sinceLastMs;
    }

    /// <summary>One tick thought for <paramref name="thinks"/> bots, and stopped on a budget or ran through the rotation.</summary>
    internal void EndTick(int thinks, bool timeLimited, bool actionLimited)
    {
        if (thinks <= 0 && !timeLimited && !actionLimited) return;
        _ticks++;
        _thinks += Math.Max(0, thinks);
        if (timeLimited) _timeLimited++;
        if (actionLimited) _actionLimited++;
    }

    /// <summary>
    /// The window moves on by <paramref name="elapsedMs"/> ms. At the end of it, true with the report, and the counts start again.
    /// </summary>
    internal bool TryReport(uint elapsedMs, int bots, out PlayerbotCadenceReport report)
    {
        _windowMs += elapsedMs;
        report = default;
        if (_windowMs < ReportIntervalMs) return false;
        _windowMs = 0;
        report = new PlayerbotCadenceReport(bots, _thinks, _ticks, Percentile(0.50), Percentile(0.95), _gapMax,
            _ticks == 0 ? 0 : (double)_thinks / _ticks,
            _ticks == 0 ? 0 : 100.0 * _timeLimited / _ticks,
            _ticks == 0 ? 0 : 100.0 * _actionLimited / _ticks);
        Array.Clear(_gaps);
        _gapCount = 0;
        _gapMax = 0;
        _thinks = 0;
        _ticks = 0;
        _timeLimited = 0;
        _actionLimited = 0;
        return true;
    }

    /// <summary>The upper edge (ms) of the bucket holding the <paramref name="fraction"/> quantile of the gaps, never above the longest gap.</summary>
    private uint Percentile(double fraction)
    {
        if (_gapCount == 0) return 0;
        long rank = Math.Max(1, (long)Math.Ceiling(_gapCount * fraction));
        long seen = 0;
        for (int bucket = 0; bucket < Buckets; bucket++)
        {
            seen += _gaps[bucket];
            if (seen >= rank) return Math.Min(_gapMax, (uint)(bucket + 1) * BucketMs);
        }

        return _gapMax;
    }
}
