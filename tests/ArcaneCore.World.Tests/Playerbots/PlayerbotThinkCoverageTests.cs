using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The "bot-cadence/think-starved" cluster (live 2026-10-17: about 28,500 reports an hour at 1000 bots, "no think for 10026 ms (bound 10000 ms;
/// 1000 bots, think budget 9.2 ms per tick)"). The rotation was fair; the 8 ms floor of the think budget was sized at 300 bots and could not
/// walk 1000 bots within the bound. The budget floor now follows the bot count, the measured cost of a visit, the measured tick length and the
/// time the combat lane uses (<see cref="PlayerbotThinkBudget.CoverageMs"/>).
/// <para>
/// Simulated clock, no starvation feedback (<see cref="PlayerbotThinkStarvation.PriorityScale"/> stays 1: the floor must hold the bound by itself).
/// Live shape, from the cadence lines of 18:46-18:48: about 5.5 bots in 6 are not fighting; the fighters' visits are about 0.1 ms. An ordinary visit
/// is 1.3 ms in the first scenario (the figure derived from those lines at 50 ms ticks: 4.4 to 5.8 main-lane visits in 7 to 8 ms), and the second
/// runs the same 1.3 ms at 100 ms ticks, the length live ticks reach on the slow stretches (105 to 140 ms "Slow world update" lines), where the
/// fixed floor walks half as many bots a second.
/// </para>
/// </summary>
public sealed class PlayerbotThinkCoverageTests
{
    private const double BoundMs = 10_000;
    private const double NominalTickMs = 50;

    /// <summary>
    /// 1000 bots, the first <c>bots / 5.5</c> of them in a fight (due every 300 ms, a visit costs 0.1 ms), the others costing
    /// <paramref name="visitMs"/> a visit; the world leaves the bots only the floor; real ticks are <paramref name="realTickMs"/> long.
    /// Returns the longest silence of any bot after the first minute of simulated time, and the budget of the last tick.
    /// </summary>
    private static (double Longest, double Budget) Simulate(double visitMs, double realTickMs, bool coverage, int bots = 1000)
    {
        var rotation = new PlayerbotThinkRotation();
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 100, botMs: 0); // a loaded world: the slack formula alone gives the floor
        int fighters = (int)(bots / 5.5);
        var last = new double[bots];
        double longest = 0;
        double budgetMs = 0;
        int ticks = (int)(240_000 / realTickMs);
        for (int tick = 0; tick < ticks; tick++)
        {
            double now = tick * realTickMs;
            double spent = 0;
            double combatMs = 0;
            budget.ObserveTickInterval(realTickMs);
            budgetMs = coverage
                ? budget.Budget(NominalTickMs, 0, 8, 30, 0.6, bots, BoundMs)
                : PlayerbotThinkBudget.Compute(NominalTickMs, 100, 0, 8, 30, 0.6);
            rotation.TickLanes(bots, budgetMs, 0.7,
                index => index < fighters && now - last[index] >= 300 ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                (index, lane) =>
                {
                    double cost = index < fighters ? 0.1 : visitMs;
                    spent += cost;
                    if (now >= 60_000) longest = Math.Max(longest, now - last[index]);
                    last[index] = now;
                    if (lane == PlayerbotThinkRotation.ThinkLane.Normal) budget.ObserveVisit(cost);
                    else combatMs += cost;
                    return true;
                },
                () => spent);
            budget.ObservePriorityTick(combatMs);
        }

        return (longest, budgetMs);
    }

    [Fact]
    public void At1000Bots_TheFixedFloor_CannotWalkThemWithinTheBound()
    {
        // The cause, not a rotation fault: 8 ms at 1.3 ms a visit is six visits a tick, and 1000 bots take well over 10 s.
        (double fifty, _) = Simulate(1.3, 50, coverage: false);
        (double hundred, _) = Simulate(1.3, 100, coverage: false);
        // At 50 ms ticks and 1.3 ms a visit the fixed floor only just holds (the live 1.5 to 1.8 ms visits and the combat lane tipped it over); at 100 ms it does not.
        Assert.True(fifty > 0.9 * BoundMs, $"50 ms ticks: longest silence {fifty} ms");
        Assert.True(hundred > BoundMs, $"100 ms ticks: longest silence {hundred} ms");
    }

    [Theory]
    [InlineData(50, PlayerbotThinkBudget.CoverageFraction * BoundMs * 1.1)]
    [InlineData(100, BoundMs)]
    public void At1000Bots_TheCoverageFloor_WalksThemWithinTheBound_WithoutInflatingOnSlowTicks(double realTickMs, double maxSilenceMs)
    {
        // Nominal 50 ms ticks walk the bots in CoverageFraction (0.5) of the bound; 100 ms ticks use that headroom to stay within BoundMs
        // without doubling the per-tick think budget floor.
        (double longest, double budgetMs) = Simulate(1.3, realTickMs, coverage: true);
        Assert.True(longest <= maxSilenceMs, $"longest silence {longest} ms at budget {budgetMs} ms (bound {maxSilenceMs} ms)");
        Assert.InRange(budgetMs, 13, 20);
    }

    [Fact]
    public void ASlowerRealTick_DoesNotRaiseTheFloor()
    {
        var fast = new PlayerbotThinkBudget();
        var slow = new PlayerbotThinkBudget();
        foreach (PlayerbotThinkBudget budget in new[] { fast, slow })
        {
            budget.Observe(tickMs: 100, botMs: 0);
            for (int i = 0; i < 50; i++) budget.ObserveVisit(1.0);
        }

        for (int i = 0; i < 100; i++)
        {
            fast.ObserveTickInterval(50);
            slow.ObserveTickInterval(100);
        }

        double fastFloor = fast.Budget(NominalTickMs, 0, 8, 100, 0.6, 1000, BoundMs);
        double slowFloor = slow.Budget(NominalTickMs, 0, 8, 100, 0.6, 1000, BoundMs);
        Assert.Equal(10, fastFloor, 3); // 1000 x 50 x 1.0 / (0.5 x 10000)
        Assert.Equal(10, slowFloor, 3); // a slow tick must not raise the floor and feed back into slower ticks
        // Before any sample the configured interval stands; a tick shorter than it never shortens the bound's wall time; a stall is capped.
        Assert.Equal(NominalTickMs, new PlayerbotThinkBudget().RealTickMs(NominalTickMs));
        var early = new PlayerbotThinkBudget();
        early.ObserveTickInterval(10);
        Assert.Equal(NominalTickMs, early.RealTickMs(NominalTickMs));
        var stalled = new PlayerbotThinkBudget();
        stalled.ObserveTickInterval(60_000);
        Assert.Equal(PlayerbotThinkBudget.MaxRealTickMs, stalled.RealTickMs(NominalTickMs));
    }

    [Fact]
    public void MeasuredSlowTick_DoesNotFeedbackIntoTheCoverageFloor()
    {
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 60, botMs: 12);
        for (int i = 0; i < 50; i++) budget.ObserveVisit(0.21);
        budget.ObservePriorityTick(0.2);

        budget.ObserveTickInterval(NominalTickMs);
        double atNominalTick = budget.Budget(NominalTickMs, spentMs: 0, floorMs: 8, ceilingMs: 30, share: 0.6, bots: 1000, boundMs: BoundMs);

        for (int i = 0; i < 50; i++) budget.ObserveTickInterval(120);
        double afterSlowTicks = budget.Budget(NominalTickMs, spentMs: 0, floorMs: 8, ceilingMs: 30, share: 0.6, bots: 1000, boundMs: BoundMs);

        Assert.Equal(atNominalTick, afterSlowTicks, 6);
        Assert.True(afterSlowTicks < 8.0 + 1e-6);
    }

    [Fact]
    public void LiveShape_1350Bots_85msTick_021msVisit_SizesCoverageToTheTargetTickRate()
    {
        var budget = new PlayerbotThinkBudget();
        for (int i = 0; i < 100; i++)
        {
            budget.Observe(tickMs: 85, botMs: 28);
            budget.ObserveTickInterval(85);
            budget.ObserveVisit(0.21);
            budget.ObservePriorityTick(0.21);
        }

        // 1350 bots scales the 2 s window (2000 ms) by EffectiveBoundMs -> 2700 ms; or at 0.21 ms visit with 2000 ms bound:
        // 1350 * 50 * 0.21 / (0.5 * 2000) + 0.21 = 14.175 + 0.21 = 14.385 ms.
        double budgetMs = budget.Budget(NominalTickMs, spentMs: 0, floorMs: 8, ceilingMs: 30, share: 0.6, bots: 1350, boundMs: 2000);
        Assert.InRange(budgetMs, 14.0, 15.0);
        double visitsPerSecond = (budgetMs - budget.PriorityMs) / budget.VisitMs * (1000.0 / NominalTickMs);
        Assert.True(visitsPerSecond * 2.0 >= 1350, $"visitsPerSecond={visitsPerSecond:F1}");
    }

    [Fact]
    public void TransientTickSpike_DoesNotLockBudgetAtCeiling_WhenTickDurationIncludesBotTime()
    {
        var budget = new PlayerbotThinkBudget();
        for (int i = 0; i < 100; i++)
        {
            budget.ObserveVisit(1.35);
            budget.ObservePriorityTick(3.0);
        }

        for (int tick = 0; tick < 5; tick++)
        {
            budget.Observe(tickMs: 110, botMs: 25);
            budget.ObserveTickInterval(110);
        }

        double currentBudget = 30.0;
        for (int tick = 0; tick < 40; tick++)
        {
            double totalTickMs = 48.0 + currentBudget;
            budget.Observe(tickMs: totalTickMs, botMs: currentBudget);
            budget.ObserveTickInterval(totalTickMs);
            currentBudget = budget.Budget(NominalTickMs, spentMs: 4.5, floorMs: 8, ceilingMs: 30, share: 0.6, bots: 1000, boundMs: BoundMs);
        }

        Assert.True(currentBudget < 20.0, $"Expected budget < 20.0 ms after transient spike, got {currentBudget:F2} ms");
    }

    [Fact]
    public void At300Bots_TheCoverageFloor_StaysTheOldEightMilliseconds()
    {
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 100, botMs: 0); // a loaded world: the slack formula alone gives the floor
        budget.ObserveTickInterval(NominalTickMs);
        for (int i = 0; i < 50; i++) budget.ObserveVisit(1.3);
        Assert.Equal(8, budget.Budget(NominalTickMs, 0, 8, 30, 0.6, 300, BoundMs)); // 300 x 50 x 1.3 / 5000 = 3.9 ms
    }

    [Fact]
    public void Coverage_FollowsTheBotCount_TheVisitCost_TheTick_AndTheBound_AndStopsAtTheCeiling()
    {
        double thousand = PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, BoundMs, 1.3);
        Assert.Equal(13, thousand, 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(2000, NominalTickMs, BoundMs, 1.3), 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, BoundMs, 2.6), 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(1000, 2 * NominalTickMs, BoundMs, 1.3), 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, BoundMs / 2, 1.3), 6);
        // The combat lane's measured time comes on top of the main lane's.
        Assert.Equal(thousand + 3, PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, BoundMs, 1.3, priorityMs: 3), 6);
        // Off, empty or garbage asks for nothing.
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, 0, 1.3));
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(0, NominalTickMs, BoundMs, 1.3));
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(1000, NominalTickMs, BoundMs, double.NaN));
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(1000, double.NaN, BoundMs, 1.3));
        // The ceiling still rules: a crowd the ceiling cannot cover gets the ceiling, not more.
        Assert.Equal(30, PlayerbotThinkBudget.Compute(NominalTickMs, 100, 0, 8, 30, 0.6, coverageMs: 400));
        Assert.Equal(12, PlayerbotThinkBudget.Compute(NominalTickMs, 100, 0, 8, 30, 0.6, coverageMs: 12));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(NominalTickMs, 100, 0, 8, 30, 0.6, coverageMs: 3));
    }

    [Fact]
    public void OneSlowVisit_DoesNotSwingTheCoverage()
    {
        var budget = new PlayerbotThinkBudget();
        for (int i = 0; i < 200; i++) budget.ObserveVisit(1.3);
        budget.ObserveVisit(4_000); // a search that hit its node cap on a bad tile
        Assert.InRange(budget.VisitMs, 1.3, 1.3 + PlayerbotThinkBudget.VisitAlpha * PlayerbotThinkBudget.MaxVisitSampleMs + 1e-9);
        budget.ObserveVisit(double.NaN);
        Assert.True(double.IsFinite(budget.VisitMs));
    }
}
