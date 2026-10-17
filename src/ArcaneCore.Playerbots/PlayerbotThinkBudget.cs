namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The wall time one world tick may spend on the bots' thinks. The fixed 8 ms of the first release left, at 750 bots in a Debug build,
/// about four thinks per tick and a think every 9.5 to 16 s per bot (live logs 2026-10-15), while a 50 ms tick usually has far more
/// slack. The budget now follows the slack: <c>clamp(share * (tick interval - non-bot tick time) - time the bots already used this tick,
/// floor, ceiling)</c>. The floor (default the old 8 ms) is the guarantee that a loaded server still thinks; the ceiling keeps the bots
/// from taking a whole tick however idle the rest of it is; a floor equal to the ceiling is a fixed budget. World thread only.
/// </summary>
internal sealed class PlayerbotThinkBudget
{
    /// <summary>Weight of a new sample in the smoothed non-bot tick time.</summary>
    internal const double SmoothingAlpha = 0.2;

    private double _nonBotMs;
    private bool _primed;

    /// <summary>The smoothed time (ms) of a world tick that is not the bots' (tests, metrics).</summary>
    internal double NonBotMs => _nonBotMs;

    /// <summary>
    /// One finished tick: <paramref name="tickMs"/> in all, of which <paramref name="botMs"/> was the bot feature's. Smoothed so a single
    /// autosave or load spike does not swing the next budgets. A tick that reports less in all than the bots' own time counts as zero.
    /// </summary>
    internal void Observe(double tickMs, double botMs)
    {
        double nonBot = double.IsFinite(tickMs) && double.IsFinite(botMs) ? Math.Max(0, tickMs - botMs) : 0;
        if (!_primed)
        {
            _nonBotMs = nonBot;
            _primed = true;
            return;
        }

        _nonBotMs += SmoothingAlpha * (nonBot - _nonBotMs);
    }

    /// <summary>The think budget (ms) for a tick of <paramref name="tickIntervalMs"/> in which the bots already used <paramref name="spentMs"/>.</summary>
    internal double Budget(double tickIntervalMs, double spentMs, double floorMs, double ceilingMs, double share)
        => Compute(tickIntervalMs, _nonBotMs, spentMs, floorMs, ceilingMs, share);

    /// <summary>
    /// <see cref="Budget(double, double, double, double, double)"/> with the floor raised to what the rotation needs to walk
    /// <paramref name="bots"/> bots within the starvation bound (<see cref="CoverageMs"/>, from the measured cost of a main-lane visit).
    /// </summary>
    internal double Budget(double tickIntervalMs, double spentMs, double floorMs, double ceilingMs, double share, int bots, double boundMs, double combatShare)
        => Compute(tickIntervalMs, _nonBotMs, spentMs, floorMs, ceilingMs, share, CoverageMs(bots, tickIntervalMs, boundMs, _visitMs, combatShare));

    /// <summary>Weight of a new main-lane visit in the smoothed visit cost.</summary>
    internal const double VisitAlpha = 0.02;

    /// <summary>The fraction of the starvation bound the rotation should need for one full walk over the bots, the rest being headroom for tick-to-tick variation and the slow thinks.</summary>
    internal const double CoverageFraction = 0.5;

    private double _visitMs;
    private bool _visitPrimed;

    /// <summary>The smoothed wall time (ms) of one main-lane visit (tests, metrics).</summary>
    internal double VisitMs => _visitMs;

    /// <summary>
    /// One main-lane visit took <paramref name="ms"/>. A single visit counts for at most <see cref="PlayerbotThinkRotation.TickBudgetMs"/>:
    /// a search that hit its node cap (30 to 50 ms, or seconds on a bad tile) is not what the next hundred visits will cost.
    /// </summary>
    internal void ObserveVisit(double ms)
    {
        if (!double.IsFinite(ms)) return;
        ms = Math.Clamp(ms, 0, PlayerbotThinkRotation.TickBudgetMs);
        if (!_visitPrimed)
        {
            _visitMs = ms;
            _visitPrimed = true;
            return;
        }

        _visitMs += VisitAlpha * (ms - _visitMs);
    }

    /// <summary>
    /// The think time per tick (ms) the rotation needs to walk every one of <paramref name="bots"/> bots within <see cref="CoverageFraction"/> of
    /// <paramref name="boundMs"/> (0 = off, nothing needed). A tick of <paramref name="tickIntervalMs"/> walks <c>budget / visitMs</c> bots, so the
    /// walk takes <c>bots * visitMs / budget</c> ticks; solving for a walk of <c>CoverageFraction * boundMs / tickIntervalMs</c> ticks gives
    /// <c>bots * tickIntervalMs * visitMs / (CoverageFraction * boundMs)</c>, divided by what the combat lane leaves the main lane at its least
    /// (1 - share * <see cref="PlayerbotThinkStarvation.MinPriorityScale"/>). 1000 bots, 50 ms ticks, a 10 s bound and 1.3 ms a visit: about 15 ms,
    /// where the fixed 8 ms floor of the first release (sized at 300 bots) walked them in 8 to 13 s. Pure.
    /// </summary>
    internal static double CoverageMs(int bots, double tickIntervalMs, double boundMs, double visitMs, double combatShare)
    {
        if (bots <= 0 || !(boundMs > 0) || !(tickIntervalMs > 0) || !(visitMs > 0)) return 0;
        double mainLeft = 1 - Math.Clamp(double.IsFinite(combatShare) ? combatShare : 0, 0, 0.9) * PlayerbotThinkStarvation.MinPriorityScale;
        double need = bots * tickIntervalMs * visitMs / (CoverageFraction * boundMs) / mainLeft;
        return double.IsFinite(need) ? need : 0;
    }

    /// <summary>
    /// The formula of <see cref="Budget(double, double, double, double, double)"/>, pure. NaN inputs give the floor; a ceiling under the floor is the floor.
    /// <paramref name="coverageMs"/> (<see cref="CoverageMs"/>) raises the floor, never past the ceiling.
    /// </summary>
    internal static double Compute(double tickIntervalMs, double nonBotMs, double spentMs, double floorMs, double ceilingMs, double share, double coverageMs = 0)
    {
        double hi = Math.Max(floorMs, ceilingMs);
        if (double.IsFinite(coverageMs) && coverageMs > floorMs) floorMs = Math.Min(coverageMs, hi);
        double wanted = share * (tickIntervalMs - nonBotMs) - spentMs;
        return double.IsFinite(wanted) ? Math.Clamp(wanted, floorMs, hi) : floorMs;
    }
}

/// <summary>
/// The "a bot went too long without a think" check (<see cref="PlayerbotOptions.ThinkStarvedGapMs"/>), one episode per bot: the report is
/// made once when the silence passes the bound and not again until the bot has thought and gone quiet again. Pure; the feature owns the flag.
/// </summary>
internal static class PlayerbotThinkStarvation
{
    /// <summary>Whether to report now: the bot has been silent for more than <paramref name="boundMs"/> (0 = off) and this episode is not reported yet. Marks it reported.</summary>
    internal static bool ShouldReport(uint nowMs, uint lastThinkMs, uint boundMs, ref bool reported)
    {
        if (boundMs == 0 || reported) return false;
        uint silent = unchecked(nowMs - lastThinkMs);
        if (silent <= boundMs || silent > int.MaxValue) return false; // a clock that went backwards is not a gap
        reported = true;
        return true;
    }

    /// <summary>The least share of its time the first lane keeps however late the main lane runs (fights still get thought for).</summary>
    internal const double MinPriorityScale = 0.2;

    /// <summary>
    /// How much of its time share the first think lane may use, from the longest silence of any bot (<paramref name="oldestSilentMs"/>, last
    /// tick's) against the starvation <paramref name="boundMs"/> (0 = off, always 1): 1 up to half the bound, falling in a straight line to
    /// <see cref="MinPriorityScale"/> at nine tenths of it. A fixed split (70% of the time to the fights) leaves the main lane too few
    /// thinks for a crowd of fights (<see cref="PlayerbotThinkRotation.TickLanes"/>); this gives the time back just when a bot is about to
    /// go over the bound, and takes it again once the main lane caught up. Pure.
    /// </summary>
    internal static double PriorityScale(uint oldestSilentMs, uint boundMs)
    {
        if (boundMs == 0 || oldestSilentMs > int.MaxValue) return 1;
        double soft = boundMs * 0.5;
        double hard = boundMs * 0.9;
        if (oldestSilentMs <= soft) return 1;
        if (oldestSilentMs >= hard) return MinPriorityScale;
        return 1 - (1 - MinPriorityScale) * (oldestSilentMs - soft) / (hard - soft);
    }
}
