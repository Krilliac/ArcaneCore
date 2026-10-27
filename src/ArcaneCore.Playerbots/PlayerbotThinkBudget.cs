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
    /// <paramref name="bots"/> bots within the starvation bound (<see cref="CoverageMs"/>: the measured cost of a main-lane visit at the
    /// configured <paramref name="tickIntervalMs"/>, plus the time the combat lane uses). Sized from the nominal tick rather than
    /// <see cref="RealTickMs"/> so an overrunning tick does not raise the bots' floor and lock the world into slow ticks; the
    /// <see cref="CoverageFraction"/> margin and <see cref="PlayerbotThinkStarvation.PriorityScale"/> absorb slow stretches.
    /// </summary>
    internal double Budget(double tickIntervalMs, double spentMs, double floorMs, double ceilingMs, double share, int bots, double boundMs)
        => Compute(tickIntervalMs, _nonBotMs, spentMs, floorMs, ceilingMs, share,
            CoverageMs(bots, tickIntervalMs, boundMs, _visitMs, _priorityMs));

    /// <summary>Weight of a new main-lane visit in the smoothed visit cost.</summary>
    internal const double VisitAlpha = 0.02;

    /// <summary>The most one visit counts for in the smoothed visit cost (ms): a search that hit its node cap (30 to 50 ms, or seconds on a bad tile) is not what the next hundred visits will cost.</summary>
    internal const double MaxVisitSampleMs = 8;

    /// <summary>The longest tick (ms) the smoothed real tick length may reach: a stall longer than that is not the tick rate.</summary>
    internal const double MaxRealTickMs = 1000;

    /// <summary>The fraction of the starvation bound the rotation should need for one full walk over the bots, the rest being headroom for tick-to-tick variation and the slow thinks.</summary>
    internal const double CoverageFraction = 0.5;

    private double _visitMs;
    private bool _visitPrimed;
    private double _priorityMs;
    private double _realTickMs;
    private bool _tickPrimed;

    /// <summary>The smoothed wall time (ms) of one main-lane visit (tests, metrics).</summary>
    internal double VisitMs => _visitMs;

    /// <summary>The smoothed wall time (ms) the combat lane used per tick (tests, metrics).</summary>
    internal double PriorityMs => _priorityMs;

    /// <summary>One main-lane visit took <paramref name="ms"/>. A single visit counts for at most <see cref="MaxVisitSampleMs"/>.</summary>
    internal void ObserveVisit(double ms)
    {
        if (!double.IsFinite(ms)) return;
        ms = Math.Clamp(ms, 0, MaxVisitSampleMs);
        if (!_visitPrimed)
        {
            _visitMs = ms;
            _visitPrimed = true;
            return;
        }

        _visitMs += VisitAlpha * (ms - _visitMs);
    }

    /// <summary>One tick in which the combat lane used <paramref name="ms"/> of think time (0 when it had no due bot), smoothed.</summary>
    internal void ObservePriorityTick(double ms)
    {
        if (!double.IsFinite(ms)) return;
        _priorityMs += SmoothingAlpha * (Math.Max(0, ms) - _priorityMs);
    }

    /// <summary>One tick came <paramref name="intervalMs"/> of wall time after the one before (the world's elapsed time handed to the feature), smoothed.</summary>
    internal void ObserveTickInterval(double intervalMs)
    {
        if (!double.IsFinite(intervalMs) || intervalMs <= 0) return;
        intervalMs = Math.Min(intervalMs, MaxRealTickMs);
        if (!_tickPrimed)
        {
            _realTickMs = intervalMs;
            _tickPrimed = true;
            return;
        }

        _realTickMs += SmoothingAlpha * (intervalMs - _realTickMs);
    }

    /// <summary>
    /// The tick length (ms) the starvation bound is walked in: the smoothed real one, never under the configured <paramref name="nominalMs"/>
    /// (a tick that came early does not shorten the bound's wall time) and never over <see cref="MaxRealTickMs"/>; the nominal one before the first sample.
    /// The bound is wall-clock time, and live ticks run 105 to 140 ms on slow stretches, so a floor sized for 50 ms ticks walks the bots too slowly there.
    /// </summary>
    internal double RealTickMs(double nominalMs)
        => _tickPrimed ? Math.Clamp(_realTickMs, nominalMs, Math.Max(nominalMs, MaxRealTickMs)) : nominalMs;

    /// <summary>
    /// The think time per tick (ms) the rotation needs to walk every one of <paramref name="bots"/> bots within <see cref="CoverageFraction"/> of
    /// <paramref name="boundMs"/> (0 = off, nothing needed): the main lane's <c>bots * tickMs * visitMs / (CoverageFraction * boundMs)</c>
    /// (a tick walks <c>budget / visitMs</c> bots, so a walk takes <c>bots * visitMs / budget</c> ticks, and the walk must fit in
    /// <c>CoverageFraction * boundMs / tickMs</c> of them) plus <paramref name="priorityMs"/>, the time the combat lane takes of the budget first.
    /// The combat lane is measured rather than guessed from its share, so the floor does not depend on the starvation feedback
    /// (<see cref="PlayerbotThinkStarvation.PriorityScale"/>) to give time back. 1000 bots, 50 ms ticks, a 10 s bound, 1.6 ms a visit: 16 ms plus the
    /// combat lane; the fixed 8 ms floor of the first release (sized at 300 bots) walked them in 8 to 13 s. Pure.
    /// </summary>
    internal static double CoverageMs(int bots, double tickMs, double boundMs, double visitMs, double priorityMs = 0)
    {
        if (bots <= 0 || !(boundMs > 0) || !(tickMs > 0) || !(visitMs > 0)) return 0;
        double need = bots * tickMs * visitMs / (CoverageFraction * boundMs) + (double.IsFinite(priorityMs) ? Math.Max(0, priorityMs) : 0);
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
/// An estimate of the time the main think lane needs for a full pass over the bots: a smoothed value of the gaps between two main-lane thinks of
/// one bot (every bot is visited once a pass, so a typical gap is the pass). It is not a mean: it moves up quickly (weight 1/<see cref="RiseWindow"/>)
/// and down slowly (1/<see cref="Window"/>), so it sits above the mean when the gaps vary, which is the safe side for both users. It sizes the
/// motion lease cap (<see cref="PlayerbotMotion.LeaseCapMs"/>) from the raw gaps, and, as a second instance fed the gaps less their world-stall
/// time (<see cref="PlayerbotThinkStarvation.WithoutStall"/>), the fairness bound of the think-starved report; neither may feed back into
/// the think budget. A gap over <see cref="MaxSampleMs"/> counts as <see cref="MaxSampleMs"/> (it is capped, not dropped), so the estimate
/// still follows a pass that really is longer (up to 5000 bots); the report bound at 1.5 x that cap, 180 s, is the intended absolute backstop.
/// World thread only.
/// </summary>
internal sealed class PlayerbotMainPass
{
    /// <summary>Weight of one sample is 1/this when the estimate falls: a pass of a thousand bots is a thousand samples.</summary>
    internal const int Window = 64;

    /// <summary>Weight of one sample is 1/this when the estimate rises (bots joining, a slower world): the lease and the bound catch up within a few thinks, not a pass.</summary>
    internal const int RiseWindow = 8;

    /// <summary>The longest gap one sample counts for (ms); a longer gap is taken as this.</summary>
    internal const uint MaxSampleMs = PlayerbotMotion.HardMaxGapLeaseMs;

    private uint _ms;

    /// <summary>Test seam: while set, <see cref="Note"/> changes nothing, so a test can hold the estimate below the gaps it sees.</summary>
    internal bool Pinned { get; set; }

    /// <summary>The estimated full-pass time in ms; 0 until the first sample.</summary>
    internal uint Ms => _ms;

    /// <summary>Sets the estimate (a hot reload handing it over, tests), capped at <see cref="MaxSampleMs"/>.</summary>
    internal void Seed(uint ms) => _ms = Math.Min(ms, MaxSampleMs);

    /// <summary>One bot's main-lane think came <paramref name="gapMs"/> ms after its previous one (0 is no gap). The first sample is taken as is.</summary>
    internal void Note(uint gapMs)
    {
        if (gapMs == 0 || Pinned) return;
        gapMs = Math.Min(gapMs, MaxSampleMs);
        int window = gapMs > _ms ? RiseWindow : Window;
        _ms = _ms == 0 ? gapMs : (uint)(((long)_ms * (window - 1) + gapMs + window / 2) / window);
    }
}

/// <summary>
/// The "a bot went too long without a think" check (<see cref="PlayerbotOptions.ThinkStarvedGapMs"/>), one episode per bot: the report is
/// made once when the silence passes the bound and not again until the bot has thought and gone quiet again. Pure; the feature owns the flag.
/// </summary>
internal static class PlayerbotThinkStarvation
{
    /// <summary>
    /// Scales the starvation bound proportionally once the running population exceeds the 1 000-bot design point of the default 30 ms
    /// per-tick think ceiling; up to 1 000 bots the configured bound stands unchanged.
    /// </summary>
    internal static uint EffectiveBoundMs(uint boundMs, int bots)
        => boundMs == 0 || bots <= 1000 ? boundMs : (uint)Math.Min(int.MaxValue, Math.Ceiling(boundMs * (bots / 1000.0)));

    /// <summary>The report bound while no full pass has been measured yet: this many times the bound, so a cold start reports nothing a pass would explain.</summary>
    internal const int ColdStartFactor = 3;

    /// <summary>The report bound is at least this many full passes: a bot is behind the rotation, not merely slow with everyone else.</summary>
    internal const double PassFactor = 1.5;

    /// <summary>
    /// The silence (ms) past which a bot is reported as starved: <paramref name="effectiveBoundMs"/> (<see cref="EffectiveBoundMs"/>; 0 = off),
    /// raised to 1.5 x the measured full pass (<paramref name="passMs"/>) so the report fires when one bot falls behind the rotation and
    /// not when every bot is equally slow. Unmeasured (0), <see cref="ColdStartFactor"/> x the bound. Pure. The think budget and the first
    /// lane's yield keep the unraised bound: raising that one would shrink the budget and slow the pass further.
    /// </summary>
    internal static uint ReportBoundMs(uint effectiveBoundMs, uint passMs)
    {
        if (effectiveBoundMs == 0) return 0;
        if (passMs == 0) return (uint)Math.Min(int.MaxValue, (long)effectiveBoundMs * ColdStartFactor);
        return Math.Max(effectiveBoundMs, (uint)Math.Min(int.MaxValue, Math.Ceiling(passMs * PassFactor)));
    }

    /// <summary>Whether to report now: the bot has been silent for more than <paramref name="boundMs"/> (0 = off) and this episode is not reported yet. Marks it reported.</summary>
    internal static bool ShouldReport(uint nowMs, uint lastThinkMs, uint boundMs, ref bool reported)
    {
        if (boundMs == 0 || reported) return false;
        uint silent = unchecked(nowMs - lastThinkMs);
        if (silent <= boundMs || silent > int.MaxValue) return false; // a clock that went backwards is not a gap
        reported = true;
        return true;
    }

    /// <summary>A gap between two bot ticks longer than this (ms) is a world stall, not the tick rate; the excess is not a rotation gap.</summary>
    internal const uint StallTickMs = (uint)PlayerbotThinkBudget.MaxRealTickMs;

    /// <summary>
    /// The length (ms) past which a gap between two bot ticks, or one think, is a world stall: <see cref="StallTickMs"/>, or twice the real tick
    /// length (<paramref name="realTickMs"/>, <see cref="PlayerbotThinkBudget.RealTickMs"/>) when that is longer. A world whose ticks all run over a
    /// second is slow, not stalled every tick: its silence and its pass are then judged alike, with no stall taken out of either. Pure.
    /// </summary>
    internal static uint StallThresholdMs(double realTickMs)
        => double.IsFinite(realTickMs) && realTickMs * 2 > StallTickMs ? (uint)Math.Min(int.MaxValue, Math.Ceiling(realTickMs * 2)) : StallTickMs;

    /// <summary>
    /// The stall (ms) in one gap between two bot ticks: its excess over <see cref="StallThresholdMs"/> of <paramref name="realTickMs"/> (0: the
    /// fixed <see cref="StallTickMs"/>); 0 for a clock that went backwards. Pure.
    /// </summary>
    internal static uint StallMs(uint tickGapMs, double realTickMs = 0)
    {
        uint threshold = StallThresholdMs(realTickMs);
        return tickGapMs > threshold && tickGapMs <= int.MaxValue ? tickGapMs - threshold : 0;
    }

    /// <summary>A gap (ms) less <paramref name="stalledMs"/>, the world-stall time inside it; never below 0.</summary>
    internal static uint WithoutStall(uint gapMs, ulong stalledMs) => stalledMs >= gapMs ? 0 : gapMs - (uint)stalledMs;

    /// <summary><see cref="ShouldReport(uint, uint, uint, ref bool)"/> on the silence less <paramref name="stalledMs"/>, the world-stall time since the bot's last think (never below 0).</summary>
    internal static bool ShouldReport(uint nowMs, uint lastThinkMs, uint boundMs, ref bool reported, ulong stalledMs)
    {
        uint silent = unchecked(nowMs - lastThinkMs);
        if (silent > int.MaxValue) return false; // a clock that went backwards is not a gap
        return ShouldReport(WithoutStall(silent, stalledMs), 0, boundMs, ref reported);
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
