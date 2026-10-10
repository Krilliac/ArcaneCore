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
/// The control loop's answer. <see cref="State"/> is a stable code (settling, over-target, over-budget, over-budget-hold,
/// over-budget-no-owned, hysteresis, under-budget, at-target); <see cref="Reason"/> says it in words for status.
/// </summary>
internal readonly record struct PopulationDecision(PopulationAction Action, int Count, string State, string Reason);

/// <summary>
/// The tick-budget control loop as a pure function (an ArcaneCore operator choice, not a game rule; the idea of a bounded population
/// with periodic add and remove is vmangos <c>RandomBot.MinBots/MaxBots/Refresh</c>, PlayerBotMgr.cpp:54-56, 368-391). It has no state:
/// the hysteresis is the band between <c>ResumeBelowPct</c> of the budget (adding allowed below it) and the budget itself (parking
/// above it), and the caller resets the tick window after every change, so a decision never sees ticks from before the last one.
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
    public static PopulationDecision Decide(int samples, double p95Ms, int runningAll, int ownedRunning, int effectiveTarget,
        PlayerbotPopulationOptions options)
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
            return new(PopulationAction.Park, Math.Min(options.MaxParksPerInterval, ownedRunning), "over-budget", over);
        }

        double resumeBelow = options.TickP95BudgetMs * options.ResumeBelowPct / 100.0;
        if (p95Ms >= resumeBelow)
            return new(PopulationAction.Hold, 0, "hysteresis", $"tick p95 {p95Ms:0.0} ms is not below {resumeBelow:0.0} ms ({options.ResumeBelowPct}% of the budget)");

        if (runningAll < effectiveTarget)
            return new(PopulationAction.Add, Math.Min(effectiveTarget - runningAll, options.MaxStartsPerInterval), "under-budget",
                $"tick p95 {p95Ms:0.0} ms is under {resumeBelow:0.0} ms and {runningAll} of {effectiveTarget} bots run");

        return new(PopulationAction.Hold, 0, "at-target", $"{runningAll} of {effectiveTarget} bots run");
    }
}
