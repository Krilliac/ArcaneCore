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

    /// <summary>The formula of <see cref="Budget"/>, pure. NaN inputs give the floor; a ceiling under the floor is the floor.</summary>
    internal static double Compute(double tickIntervalMs, double nonBotMs, double spentMs, double floorMs, double ceilingMs, double share)
    {
        double hi = Math.Max(floorMs, ceilingMs);
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
