using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The "bot-cadence/think-starved" cluster (live 2026-10-17: about 28,500 reports an hour at 1000 bots, "no think for 10026 ms (bound 10000 ms;
/// 1000 bots, think budget 9.2 ms per tick)"). The rotation was fair; the 8 ms floor of the think budget was sized at 300 bots and could not
/// walk 1000 bots within the bound (live cadence lines: 20 to 30 visits a tick, four fifths of them the combat lane's cheap ones, so the
/// other bots got about 110 visits a second: a walk of 7 to 12 s). The budget floor now follows the bot count (<see cref="PlayerbotThinkBudget.CoverageMs"/>).
/// Simulated clock: 50 ms ticks, a think costs a fixed number of milliseconds.
/// </summary>
public sealed class PlayerbotThinkCoverageTests
{
    private const int TickMs = 50;
    private const double BoundMs = 10_000;

    /// <summary>
    /// The live shape: <paramref name="bots"/> bots, every fifth of them (the first <c>bots / 5.5</c>) in a fight (due every 300 ms, a visit costs
    /// <paramref name="fightCostMs"/>), the others costing <paramref name="thinkCostMs"/> a visit; the world leaves the bots only the floor.
    /// Returns the longest silence of any bot after the first minute.
    /// </summary>
    private static double LongestSilenceMs(int bots, double thinkCostMs, double fightCostMs, bool coverage, int ticks = 3600)
    {
        var rotation = new PlayerbotThinkRotation();
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 100, botMs: 0);
        int fighters = (int)(bots / 5.5);
        var last = new double[bots];
        double longest = 0;
        double oldest = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            double now = tick * (double)TickMs;
            double spent = 0;
            double floor = 8;
            double budgetMs = coverage
                ? budget.Budget(TickMs, 0, floor, 30, 0.6, bots, BoundMs, 0.7) // a loaded world (non-bot time 100 ms): the floor, raised by the coverage
                : PlayerbotThinkBudget.Compute(TickMs, 100, 0, floor, 30, 0.6);
            rotation.TickLanes(bots, budgetMs, 0.7,
                index => index < fighters && now - last[index] >= 300 ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                (index, lane) =>
                {
                    double cost = index < fighters ? fightCostMs : thinkCostMs;
                    spent += cost;
                    if (tick >= 1200) longest = Math.Max(longest, now - last[index]);
                    last[index] = now;
                    if (lane == PlayerbotThinkRotation.ThinkLane.Normal) budget.ObserveVisit(cost);
                    return true;
                },
                () => spent,
                PlayerbotThinkStarvation.PriorityScale((uint)oldest, (uint)BoundMs));
            oldest = 0;
            for (int i = 0; i < bots; i++) oldest = Math.Max(oldest, now - last[i]);
        }

        return longest;
    }

    [Fact]
    public void At1000Bots_TheFixedFloor_CannotWalkThemWithinTheBound()
    {
        // The cause, not a rotation fault: 8 ms at 1.3 ms a visit is six visits a tick, and 1000 bots take well over 10 s.
        double longest = LongestSilenceMs(1000, thinkCostMs: 1.8, fightCostMs: 0.1, coverage: false);
        Assert.True(longest > BoundMs, $"longest silence {longest} ms");
    }

    [Fact]
    public void At1000Bots_TheCoverageFloor_WalksThemWithinTheBound()
    {
        double longest = LongestSilenceMs(1000, thinkCostMs: 1.8, fightCostMs: 0.1, coverage: true);
        Assert.True(longest <= BoundMs, $"longest silence {longest} ms");
    }

    [Fact]
    public void At300Bots_TheCoverageFloor_StaysTheOldEightMilliseconds()
    {
        // Sized for 300 bots, the floor needs nothing more there: the budget a small server gets does not change.
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 100, botMs: 0); // a loaded world: the slack formula alone gives the floor
        for (int i = 0; i < 50; i++) budget.ObserveVisit(1.3);
        Assert.Equal(8, budget.Budget(TickMs, 0, 8, 30, 0.6, 300, BoundMs, 0.7));
    }

    [Fact]
    public void Coverage_FollowsTheBotCount_TheVisitCost_AndTheBound_AndStopsAtTheCeiling()
    {
        double thousand = PlayerbotThinkBudget.CoverageMs(1000, TickMs, BoundMs, 1.3, 0.7);
        Assert.InRange(thousand, 14, 16);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(2000, TickMs, BoundMs, 1.3, 0.7), 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(1000, TickMs, BoundMs, 2.6, 0.7), 6);
        Assert.Equal(thousand * 2, PlayerbotThinkBudget.CoverageMs(1000, TickMs, BoundMs / 2, 1.3, 0.7), 6);
        // Off, empty or garbage asks for nothing.
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(1000, TickMs, 0, 1.3, 0.7));
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(0, TickMs, BoundMs, 1.3, 0.7));
        Assert.Equal(0, PlayerbotThinkBudget.CoverageMs(1000, TickMs, BoundMs, double.NaN, 0.7));
        // The ceiling still rules: a crowd the ceiling cannot cover gets the ceiling, not more.
        Assert.Equal(30, PlayerbotThinkBudget.Compute(TickMs, 100, 0, 8, 30, 0.6, coverageMs: 400));
        Assert.Equal(12, PlayerbotThinkBudget.Compute(TickMs, 100, 0, 8, 30, 0.6, coverageMs: 12));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(TickMs, 100, 0, 8, 30, 0.6, coverageMs: 3));
    }

    [Fact]
    public void OneSlowVisit_DoesNotSwingTheCoverage()
    {
        var budget = new PlayerbotThinkBudget();
        for (int i = 0; i < 200; i++) budget.ObserveVisit(1.3);
        budget.ObserveVisit(4_000); // a search that hit its node cap on a bad tile
        Assert.InRange(budget.VisitMs, 1.3, 1.3 + PlayerbotThinkBudget.VisitAlpha * PlayerbotThinkRotation.TickBudgetMs + 1e-9);
        budget.ObserveVisit(double.NaN);
        Assert.True(double.IsFinite(budget.VisitMs));
    }
}
