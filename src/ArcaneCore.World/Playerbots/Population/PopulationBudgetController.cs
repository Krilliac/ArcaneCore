namespace ArcaneCore.World.Playerbots.Population;

/// <summary>What a pass of the control loop does to the population.</summary>
internal enum PopulationAction
{
    /// <summary>Change nothing.</summary>
    Hold,

    /// <summary>Start or create <see cref="PopulationDecision.Count"/> bots.</summary>
    Add,

    /// <summary>Stop <see cref="PopulationDecision.Count"/> bots the population owns.</summary>
    Park,
}

/// <summary>
/// The control loop's answer. <see cref="State"/> is a stable code (settling, over-target, over-budget, over-budget-confirming,
/// over-budget-hold, over-budget-no-owned, hysteresis, under-budget, under-budget-confirming, at-target); <see cref="Reason"/> says it
/// in words for status.
/// </summary>
internal readonly record struct PopulationDecision(PopulationAction Action, int Count, string State, string Reason);

/// <summary>
/// The tick-budget control loop as a pure function (an ArcaneCore operator choice, not a game rule; the idea of a bounded population
/// with periodic add and remove is vmangos <c>RandomBot.MinBots/MaxBots/Refresh</c>, PlayerBotMgr.cpp:54-56, 368-391). It has no state.
/// The hysteresis is threefold: the band between <c>ResumeBelowPct</c> of the budget (adding allowed below it) and the budget itself
/// (parking above it); the caller resets the tick window after every change, so a decision never sees ticks from before the last
/// one; and a park or an add for the tick budget needs <c>ConfirmWindows</c> consecutive fresh windows on the same side of the
/// band (<see cref="PopulationBudgetWindows"/> counts them), so one slow window cannot park and one quiet window cannot add.
/// </summary>
internal static class PopulationBudgetController
{
    /// <summary>The number of bots to keep running: TargetOnline, never above the running cap <c>MaxBots</c>.</summary>
    public static int EffectiveTarget(PlayerbotPopulationOptions options, int maxBots)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Math.Min(options.TargetOnline, Math.Max(0, maxBots));
    }

    /// <param name="samples">Ticks measured since the last change.</param>
    /// <param name="p95Ms">The tick p95 over the window, milliseconds.</param>
    /// <param name="runningAll">Every managed bot running, whoever created it.</param>
    /// <param name="ownedRunning">The running bots the population owns (the only ones it may park).</param>
    /// <param name="effectiveTarget">The lower of TargetOnline and MaxBots.</param>
    /// <param name="windows">
    /// Consecutive fresh windows, this one included, whose p95 fell on the same side of the band as this one
    /// (<see cref="PopulationBudgetWindows"/>). A park for the budget needs <c>ConfirmWindows</c> of them over the budget, an add as
    /// many under the resume line; fewer hold in the matching <c>-confirming</c> state.
    /// </param>
    public static PopulationDecision Decide(int samples, double p95Ms, int runningAll, int ownedRunning, int effectiveTarget,
        PlayerbotPopulationOptions options, int windows)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (samples < options.WindowTicks)
            return new(PopulationAction.Hold, 0, "settling", $"waiting for {options.WindowTicks} fresh ticks ({samples} so far)");

        if (runningAll > effectiveTarget && ownedRunning > 0)
        {
            int surplus = runningAll - effectiveTarget;
            return new(PopulationAction.Park, Math.Min(Math.Min(surplus, options.MaxParksPerInterval), ownedRunning), "over-target",
                $"{runningAll} bots running, target {effectiveTarget}");
        }

        if (p95Ms > options.TickP95BudgetMs)
        {
            string over = $"tick p95 {p95Ms:0.0} ms is over the {options.TickP95BudgetMs} ms budget";
            if (!options.ParkWhenOverBudget) return new(PopulationAction.Hold, 0, "over-budget-hold", over + "; parking is off");
            if (ownedRunning == 0) return new(PopulationAction.Hold, 0, "over-budget-no-owned", over + "; the population owns no running bot to park");
            if (windows < options.ConfirmWindows)
                return new(PopulationAction.Hold, 0, "over-budget-confirming", $"{over} in {windows} of the {options.ConfirmWindows} windows a park needs");
            return new(PopulationAction.Park, Math.Min(options.MaxParksPerInterval, ownedRunning), "over-budget", over);
        }

        double resumeBelow = options.TickP95BudgetMs * options.ResumeBelowPct / 100.0;
        if (p95Ms >= resumeBelow)
            return new(PopulationAction.Hold, 0, "hysteresis", $"tick p95 {p95Ms:0.0} ms is not below {resumeBelow:0.0} ms ({options.ResumeBelowPct}% of the budget)");

        if (runningAll < effectiveTarget && windows < options.ConfirmWindows)
            return new(PopulationAction.Hold, 0, "under-budget-confirming",
                $"tick p95 {p95Ms:0.0} ms is under {resumeBelow:0.0} ms in {windows} of the {options.ConfirmWindows} windows an add needs");

        if (runningAll < effectiveTarget)
            return new(PopulationAction.Add, Math.Min(effectiveTarget - runningAll, options.MaxStartsPerInterval), "under-budget",
                $"tick p95 {p95Ms:0.0} ms is under {resumeBelow:0.0} ms and {runningAll} of {effectiveTarget} bots run");

        return new(PopulationAction.Hold, 0, "at-target", $"{runningAll} of {effectiveTarget} bots run");
    }
}

/// <summary>
/// Counts the consecutive fresh windows whose p95 fell on the same side of the hysteresis band (over the budget, under the resume
/// line, or between them), for <see cref="PopulationBudgetController.Decide"/>. A pass with fewer than <c>WindowTicks</c> fresh ticks
/// (settling) is no evidence and leaves the count alone; <see cref="Reset"/> follows every start, creation and park, together with the tick window.
/// <para>Thread affinity: the population pass (one at a time).</para>
/// </summary>
internal sealed class PopulationBudgetWindows
{
    private int _side;
    private int _count;
    private bool _counted;
    private long _countedAt;

    /// <summary>The count so far (0 after a reset, until a full window is seen).</summary>
    public int Count => _count;

    /// <summary>
    /// Record this pass's window and return the count, this window included (unchanged while settling). A window is counted only when
    /// at least <c>WindowTicks</c> ticks arrived since the last counted one (<paramref name="totalSamples"/>, the source's running
    /// total), so two windows never share a tick: the p95 reads the last <c>WindowTicks</c> ticks, and with a slow tick the passes come
    /// faster than a window fills, where one burst would otherwise be counted by several passes and confirm a park. A pass whose
    /// window is not fresh counts nothing and returns the count only if its p95 is on the counted side (else 0, and the count starts again), so the count of one
    /// side never confirms a decision made on the other.
    /// </summary>
    public int Observe(int samples, long totalSamples, double p95Ms, PlayerbotPopulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (samples < options.WindowTicks) return _count;
        int side = p95Ms > options.TickP95BudgetMs ? 1 : p95Ms < options.TickP95BudgetMs * options.ResumeBelowPct / 100.0 ? -1 : 0;
        if (_counted && totalSamples - _countedAt < options.WindowTicks)
        {
            if (_count > 0 && side == _side) return _count;
            _count = 0; // a reading on the other side (or between the lines) starts the count again, fresh or not
            return 0;
        }

        _count = _count > 0 && side == _side ? _count + 1 : 1;
        _side = side;
        _counted = true;
        _countedAt = totalSamples;
        return _count;
    }

    /// <summary>Forget the windows seen (after a start, creation or park: the load they measured is gone; a relocation changes no count of bots, so it keeps them).</summary>
    public void Reset()
    {
        _count = 0;
        _side = 0;
        _counted = false;
    }
}
