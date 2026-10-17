using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bots' think cadence at 750 bots (live logs 2026-10-15, Debug build: a think every 9.5 to 16 s per bot, every tick ended on the 8 ms
/// budget with about four thinks): the two think lanes, the adaptive budget, the options, the metrics and the starvation anomaly.
/// The rotation tests run a simulated clock: a think costs a fixed number of milliseconds, a tick is 50 ms.
/// </summary>
public sealed class PlayerbotThinkLanesTests
{
    private const int TickMs = 50;

    /// <summary>Result of a simulated run: the longest wait (ms) between two thinks per bot, and the thinks per tick.</summary>
    private sealed record Run(double[] LongestGapMs, int[] Thinks);

    /// <summary>
    /// <paramref name="bots"/> bots, the ones <paramref name="inCombat"/> names always in the first lane (due every
    /// <paramref name="combatIntervalMs"/>), a think costing <paramref name="thinkCostMs"/> of a <paramref name="budgetMs"/> budget,
    /// the first lane holding <paramref name="share"/> of it, over <paramref name="ticks"/> ticks.
    /// </summary>
    private static Run Simulate(int bots, Func<int, bool> inCombat, double thinkCostMs, double budgetMs, double share, int combatIntervalMs, int ticks, bool lanes = true)
    {
        var rotation = new PlayerbotThinkRotation();
        var last = new double[bots];
        Array.Fill(last, -1);
        var longest = new double[bots];
        var thinks = new int[bots];
        for (int tick = 0; tick < ticks; tick++)
        {
            double now = tick * (double)TickMs;
            double spent = 0;
            Func<int, bool> visit = index =>
            {
                spent += thinkCostMs;
                if (last[index] >= 0) longest[index] = Math.Max(longest[index], now - last[index]);
                last[index] = now;
                thinks[index]++;
                return true;
            };
            if (lanes)
                rotation.TickLanes(bots, budgetMs, share,
                    index => inCombat(index) && (last[index] < 0 || now - last[index] >= combatIntervalMs)
                        ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                    (index, _) => visit(index), () => spent);
            else
                rotation.TickWithin(bots, budgetMs, visit, () => spent);
        }

        return new Run(longest, thinks);
    }

    [Fact]
    public void BothLanes_KeepTheirFairnessBound_UnderLoad()
    {
        // 300 bots, every third one fighting; a 20 ms budget at 1 ms per think: 14 thinks for the fights, 6 kept for everyone.
        const int bots = 300;
        Run run = Simulate(bots, i => i % 3 == 0, thinkCostMs: 1, budgetMs: 20, share: 0.7, combatIntervalMs: 50, ticks: 400);

        int fighters = bots / 3;
        int fightBound = (fighters + 13) / 14;              // ceil(P / Kp) ticks for a due bot of the first lane
        int mainBound = (bots + 5) / 6;                      // ceil(N / Kmin) ticks for every bot, Kmin = the reserved 6 thinks
        double fightWorst = Enumerable.Range(0, bots).Where(i => i % 3 == 0).Max(i => run.LongestGapMs[i]);
        double quietWorst = Enumerable.Range(0, bots).Where(i => i % 3 != 0).Max(i => run.LongestGapMs[i]);
        Assert.True(fightWorst <= fightBound * TickMs, $"a fighter waited {fightWorst} ms (bound {fightBound * TickMs} ms)");
        Assert.True(quietWorst <= mainBound * TickMs, $"a quiet bot waited {quietWorst} ms (bound {mainBound * TickMs} ms)");
        Assert.All(run.Thinks, count => Assert.True(count > 0, "a bot never thought"));
    }

    [Fact]
    public void CombatBots_ThinkAtTheirInterval_UnderLoad_WhereOneLaneLeavesThemTheWholeRotationWait()
    {
        // 750 bots, 50 of them fighting, 0.5 ms a think and a 12 ms budget: 24 thinks a tick. One lane: a bot waits 750 / 24 = 32 ticks (1.6 s).
        const int bots = 750;
        bool Fighting(int i) => i % 15 == 0;
        Run lanes = Simulate(bots, Fighting, thinkCostMs: 0.5, budgetMs: 12, share: 0.7, combatIntervalMs: 300, ticks: 500);
        Run single = Simulate(bots, Fighting, thinkCostMs: 0.5, budgetMs: 12, share: 0.7, combatIntervalMs: 300, ticks: 500, lanes: false);

        double worstFight = Enumerable.Range(0, bots).Where(Fighting).Max(i => lanes.LongestGapMs[i]);
        double singleFight = Enumerable.Range(0, bots).Where(Fighting).Max(i => single.LongestGapMs[i]);
        Assert.True(worstFight <= 400, $"a fighting bot waited {worstFight} ms (interval 300 ms plus a tick or two)");
        Assert.True(singleFight >= 1500, $"the single lane should leave a fighter waiting a whole rotation, got {singleFight} ms");
    }

    [Fact]
    public void QuietBots_AreNotStarved_ByACrowdOfFights()
    {
        // 600 of 750 bots fighting and always due, 1 ms a think, 10 ms budget with 7 ms for the fights: the quiet 150 still get 3 thinks a tick.
        const int bots = 750;
        Run run = Simulate(bots, i => i % 5 != 0, thinkCostMs: 1, budgetMs: 10, share: 0.7, combatIntervalMs: 50, ticks: 600);

        int mainBound = (bots + 2) / 3; // the reserved 3 thinks a tick walk the main cursor over all 750 in at most 250 ticks
        double quietWorst = Enumerable.Range(0, bots).Where(i => i % 5 == 0).Max(i => run.LongestGapMs[i]);
        Assert.True(quietWorst <= mainBound * TickMs, $"a quiet bot waited {quietWorst} ms (bound {mainBound * TickMs} ms)");
        Assert.All(Enumerable.Range(0, bots).Where(i => i % 5 == 0), i => Assert.True(run.Thinks[i] > 2, $"quiet bot {i} thought {run.Thinks[i]} times"));
    }

    /// <summary>
    /// The live crowd of 2026-10-17: 1000 bots, 230 of them always in the first lane (a fight, or dead), 59 ms ticks, the 8 ms floor budget, a
    /// fight's think 0.16 ms and an ordinary bot's 0.75 ms. With <paramref name="feedback"/> the first lane's time share follows the longest
    /// silence of last tick (<see cref="PlayerbotThinkStarvation.PriorityScale"/>), as the feature does; without, the old fixed split.
    /// </summary>
    private static (double QuietWorstMs, double QuietWorstAfterWarmupMs, double FightWorstMs, double FightMeanGapMs) SimulateStarvedCrowd(bool feedback)
    {
        const int bots = 1000;
        const double tickMs = 59;
        const uint bound = 10_000;
        bool Fighter(int i) => i % 100 < 23;
        var rotation = new PlayerbotThinkRotation();
        var last = new double[bots];
        var quietWorst = 0.0;
        var quietAfterWarmup = 0.0;
        var fightWorst = 0.0;
        double fightGapSum = 0;
        long fightGaps = 0;
        uint oldest = 0;
        for (int tick = 0; tick < 6000; tick++)
        {
            double now = tick * tickMs;
            double spent = 0;
            rotation.TickLanes(bots, 8, 0.7,
                index => Fighter(index) && now - last[index] >= 300 ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                (index, _) =>
                {
                    spent += Fighter(index) ? 0.16 : 0.75;
                    double gap = now - last[index];
                    if (Fighter(index))
                    {
                        fightWorst = Math.Max(fightWorst, gap);
                        if (tick > 1700) { fightGapSum += gap; fightGaps++; }
                    }
                    else
                    {
                        quietWorst = Math.Max(quietWorst, gap);
                        if (tick > 1700) quietAfterWarmup = Math.Max(quietAfterWarmup, gap);
                    }

                    last[index] = now;
                    return true;
                },
                () => spent,
                feedback ? PlayerbotThinkStarvation.PriorityScale(oldest, bound) : 1);
            double max = 0;
            for (int i = 0; i < bots; i++) max = Math.Max(max, now + tickMs - last[i]);
            oldest = (uint)max;
        }

        return (quietWorst, quietAfterWarmup, fightWorst, fightGaps == 0 ? 0 : fightGapSum / fightGaps);
    }

    [Fact]
    public void AThousandBotsWithAFifthFighting_EveryBotThinksWithinTheStarvationBound_OnceTheFirstLaneYields()
    {
        var fixedSplit = SimulateStarvedCrowd(feedback: false);
        var yielding = SimulateStarvedCrowd(feedback: true);

        // The control (the shipped behaviour): the main lane thinks for 4 bots a tick and walks its 1000 in over 10 s (live p95 11 to 16 s).
        Assert.True(fixedSplit.QuietWorstAfterWarmupMs > 10_000, $"the fixed split should starve the quiet bots, got {fixedSplit.QuietWorstAfterWarmupMs} ms");
        // The fix: nobody waits past the 10 s bound, and the fights are still thought for about as often as the old lane allowed within a second.
        Assert.True(yielding.QuietWorstAfterWarmupMs <= 10_000, $"a bot waited {yielding.QuietWorstAfterWarmupMs} ms (bound 10000 ms)");
        Assert.True(yielding.FightMeanGapMs <= 1_000, $"a fighter waited {yielding.FightMeanGapMs} ms on average");
    }

    [Theory]
    [InlineData(0u, 10_000u, 1.0)]
    [InlineData(5_000u, 10_000u, 1.0)]
    [InlineData(7_000u, 10_000u, 0.6)]
    [InlineData(9_000u, 10_000u, PlayerbotThinkStarvation.MinPriorityScale)]
    [InlineData(60_000u, 10_000u, PlayerbotThinkStarvation.MinPriorityScale)]
    [InlineData(60_000u, 0u, 1.0)]
    [InlineData(uint.MaxValue, 10_000u, 1.0)] // a clock that went backwards is no silence
    public void ThePriorityScale_FallsFromOneToItsFloor_AsTheOldestSilenceNearsTheBound(uint oldestSilentMs, uint boundMs, double expected)
        => Assert.Equal(expected, PlayerbotThinkStarvation.PriorityScale(oldestSilentMs, boundMs), 9);

    [Fact]
    public void WithRoomForEveryone_EveryBotThinksEveryTick_AsBefore()
    {
        Run run = Simulate(40, i => i % 2 == 0, thinkCostMs: 0.1, budgetMs: 20, share: 0.7, combatIntervalMs: 300, ticks: 50);
        Assert.All(run.Thinks, count => Assert.Equal(50, count));
        Assert.All(run.LongestGapMs, gap => Assert.Equal(TickMs, gap));
    }

    [Fact]
    public void WithNoTimeLeft_TheFirstLaneThinksNobody_AndTheMainLaneStillThinksOneBot()
    {
        var rotation = new PlayerbotThinkRotation();
        var seen = new List<int>();
        rotation.TickLanes(5, 0, 0.7, _ => PlayerbotThinkRotation.ThinkLane.Priority, (i, _) => { seen.Add(i); return true; }, () => 100);
        Assert.Equal([0], seen); // the first lane starts nothing after its time; the main lane's first visit is its guarantee
        Assert.Equal(0, rotation.LastPriorityVisits);
        Assert.True(rotation.LastTickTimeLimited);
    }

    /// <summary>Result of <see cref="SimulateActions"/>: the gaps and thinks, the fewest main-lane thinks and first-lane visits in any tick.</summary>
    private sealed record ActionRun(Run Run, int MinMainPerTick, int MinPriorityPerTick);

    /// <summary>
    /// Runs <paramref name="ticks"/> ticks of <paramref name="maxActions"/> actions where a fighter's think costs <paramref name="fightCost"/> and a quiet
    /// bot's costs <paramref name="quietCost"/> (each think takes up to its cost with <c>TryTake</c>, as the feature's thinks do). With
    /// <paramref name="split"/> the feature's two budgets (the combat lane's own, the main lane's reserve plus what the combat lane left, made at its
    /// first visit); without, one shared budget (the old soft reserve: the control).
    /// </summary>
    private static ActionRun SimulateActions(int bots, Func<int, bool> inCombat, int fightCost, int quietCost, int maxActions, double share, int combatIntervalMs, int ticks, bool split = true)
    {
        var rotation = new PlayerbotThinkRotation();
        var last = new double[bots];
        Array.Fill(last, -1);
        var longest = new double[bots];
        var thinks = new int[bots];
        int reserve = PlayerbotThinkRotation.ActionReserve(maxActions, share);
        int combatActions = PlayerbotThinkRotation.PriorityActions(maxActions, share);
        int minMain = int.MaxValue;
        int minPriority = int.MaxValue;
        for (int tick = 0; tick < ticks; tick++)
        {
            double now = tick * (double)TickMs;
            var combat = new ManagedActionBudget(split ? combatActions : maxActions);
            ManagedActionBudget? main = null;
            int mainThinks = 0;
            rotation.TickLanes(bots, budgetMs: 1000, share,
                index => inCombat(index) && (last[index] < 0 || now - last[index] >= combatIntervalMs)
                    ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                (index, lane) =>
                {
                    ManagedActionBudget budget = !split || lane == PlayerbotThinkRotation.ThinkLane.Priority
                        ? combat
                        : main ??= new ManagedActionBudget(reserve + combat.Remaining);
                    if (budget.Remaining <= 0) return false;
                    for (int k = inCombat(index) ? fightCost : quietCost; k > 0 && budget.TryTake(); k--) { }
                    if (last[index] >= 0) longest[index] = Math.Max(longest[index], now - last[index]);
                    last[index] = now;
                    thinks[index]++;
                    if (lane == PlayerbotThinkRotation.ThinkLane.Normal) mainThinks++;
                    return true;
                },
                () => 0);
            minMain = Math.Min(minMain, mainThinks);
            minPriority = Math.Min(minPriority, rotation.LastPriorityVisits);
        }

        return new ActionRun(new Run(longest, thinks), minMain, minPriority);
    }

    private const int Crowd = 750;
    private static bool CrowdFighter(int i) => i % 15 < 4; // 200 of 750, always due

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void TheMainLane_ThinksAtLeastOneBotEveryTick_AtASmallActionBudget(int maxActions, int fightCost)
    {
        // The review's case: one fighter's think costs the whole shared budget, which left the main lane refused every tick.
        ActionRun result = SimulateActions(Crowd, CrowdFighter, fightCost, 1, maxActions, 0.7, combatIntervalMs: 50, ticks: 2000);

        var quiet = Enumerable.Range(0, Crowd).Where(i => !CrowdFighter(i)).ToArray();
        Assert.All(quiet, i => Assert.True(result.Run.Thinks[i] > 0, $"quiet bot {i} never thought"));
        Assert.Equal(1, result.MinMainPerTick);
        double quietWorst = quiet.Max(i => result.Run.LongestGapMs[i]);
        double fightWorst = Enumerable.Range(0, Crowd).Where(CrowdFighter).Max(i => result.Run.LongestGapMs[i]);
        Assert.True(quietWorst <= 37_500, $"a quiet bot waited {quietWorst} ms");
        Assert.True(fightWorst <= 10_000, $"a fighter waited {fightWorst} ms");
    }

    [Theory]
    [InlineData(16, 3, 67)]
    [InlineData(30, 2, 100)]
    [InlineData(48, 1, 200)]
    public void TheMainLane_ThinksAtLeastOneBotEveryTick_WhenAFightersThinkCostsMostOfTheBudget(int fightCost, int minPriorityPerTick, int boundTicks)
    {
        Assert.Equal(15, PlayerbotThinkRotation.ActionReserve(48, 0.7));
        Assert.Equal(33, PlayerbotThinkRotation.PriorityActions(48, 0.7));
        ActionRun result = SimulateActions(Crowd, CrowdFighter, fightCost, 1, 48, 0.7, combatIntervalMs: 50, ticks: 2000);

        var quiet = Enumerable.Range(0, Crowd).Where(i => !CrowdFighter(i)).ToArray();
        Assert.All(quiet, i => Assert.True(result.Run.Thinks[i] > 0, $"quiet bot {i} never thought"));
        Assert.Equal(1, result.MinMainPerTick);
        Assert.Equal(minPriorityPerTick, result.MinPriorityPerTick);
        double quietWorst = quiet.Max(i => result.Run.LongestGapMs[i]) / TickMs;
        double fightWorst = Enumerable.Range(0, Crowd).Where(CrowdFighter).Max(i => result.Run.LongestGapMs[i]) / TickMs;
        Assert.True(quietWorst <= boundTicks, $"a quiet bot waited {quietWorst} ticks (bound {boundTicks})");
        Assert.True(fightWorst <= boundTicks, $"a fighter waited {fightWorst} ticks (bound {boundTicks})");
    }

    [Fact]
    public void WithOneSharedActionBudget_TheFightersStarveEveryQuietBot_TheControlOfTheSplit()
    {
        // RED control: the old soft reserve is one shared budget. Three fights at 16 actions drain the 48 every tick, so the main lane is refused.
        ActionRun shared = SimulateActions(Crowd, CrowdFighter, 16, 1, 48, 0.7, combatIntervalMs: 50, ticks: 2000, split: false);
        int starved = Enumerable.Range(0, Crowd).Count(i => !CrowdFighter(i) && shared.Run.Thinks[i] == 0);
        Assert.Equal(550, starved);
        Assert.Equal(0, shared.MinMainPerTick);
    }

    [Fact]
    public void TheCombatLane_CannotSpendTheMainLanesActions_SoTheQuietBotsStillThink()
    {
        // 750 bots, 200 fighters at 3 actions a think always due, 48 actions a tick: 33 are the fights' (11 thinks), 15 are the main lane's.
        ActionRun result = SimulateActions(Crowd, CrowdFighter, 3, 1, 48, 0.7, combatIntervalMs: 50, ticks: 2000);

        var quiet = Enumerable.Range(0, Crowd).Where(i => !CrowdFighter(i)).ToArray();
        Assert.All(quiet, i => Assert.True(result.Run.Thinks[i] > 0, $"quiet bot {i} never thought"));
        double quietWorst = quiet.Max(i => result.Run.LongestGapMs[i]) / TickMs;
        double fightWorst = Enumerable.Range(0, Crowd).Where(CrowdFighter).Max(i => result.Run.LongestGapMs[i]) / TickMs;
        Assert.True(quietWorst <= 150, $"a quiet bot waited {quietWorst} ticks");
        Assert.True(fightWorst <= 19, $"a fighter waited {fightWorst} ticks"); // ceil(200 / 11)
    }

    [Fact]
    public void TheMainLaneCursor_AlwaysMovesOn_WhateverTheFirstLaneSpends()
    {
        var rotation = new PlayerbotThinkRotation();
        int reserve = PlayerbotThinkRotation.ActionReserve(2, 0.7);
        int combatActions = PlayerbotThinkRotation.PriorityActions(2, 0.7);
        Assert.Equal((1, 1), (reserve, combatActions));
        string[] expectedSeen = ["0P,1M", "1P,2M", "0P,3M", "1P,4M", "0P,5M", "1P,2M"];
        int[] expectedCursor = [2, 3, 4, 5, 1, 3];
        for (int tick = 0; tick < 6; tick++)
        {
            var combat = new ManagedActionBudget(combatActions);
            ManagedActionBudget? main = null;
            var seen = new List<string>();
            // Bots 0 and 1 always fight; a think costs 2 actions, more than either budget holds.
            int thinks = rotation.TickLanes(6, 1000, 0.7, i => i < 2 ? PlayerbotThinkRotation.ThinkLane.Priority : PlayerbotThinkRotation.ThinkLane.Normal,
                (i, lane) =>
                {
                    ManagedActionBudget budget = lane == PlayerbotThinkRotation.ThinkLane.Priority ? combat : main ??= new ManagedActionBudget(reserve + combat.Remaining);
                    if (budget.Remaining <= 0) return false;
                    for (int k = 0; k < 2 && budget.TryTake(); k++) { }
                    seen.Add(i + (lane == PlayerbotThinkRotation.ThinkLane.Priority ? "P" : "M"));
                    return true;
                }, () => 0);
            Assert.Equal(expectedSeen[tick], string.Join(",", seen));
            Assert.Equal(expectedCursor[tick], rotation.Cursor);
            Assert.Equal(2, thinks);
            Assert.Equal(1, rotation.LastPriorityVisits);
            Assert.False(rotation.LastTickTimeLimited);
        }
    }

    [Theory]
    [InlineData(1, 0.7, 1, 0)]
    [InlineData(2, 0.7, 1, 1)]
    [InlineData(3, 0.7, 1, 2)]
    [InlineData(4, 0.7, 2, 2)]
    [InlineData(48, 0.7, 15, 33)]
    [InlineData(64, 0.7, 20, 44)]
    [InlineData(64, 0.9, 7, 57)]
    [InlineData(64, 0.1, 58, 6)]
    // 1 - 0.7 is 0.30000000000000004 in doubles: a ceil of the reserve gave the combat lane 6 of 10, not the documented floor(7).
    [InlineData(10, 0.7, 3, 7)]
    [InlineData(20, 0.7, 6, 14)]
    [InlineData(60, 0.7, 18, 42)]
    public void TheActionSplit_KeepsAtLeastOneActionForTheMainLane(int maxActions, double share, int reserve, int combat)
    {
        Assert.Equal(reserve, PlayerbotThinkRotation.ActionReserve(maxActions, share));
        Assert.Equal(combat, PlayerbotThinkRotation.PriorityActions(maxActions, share));
        Assert.Equal(maxActions, reserve + combat);
    }

    [Fact]
    public void AdaptiveBudget_RespectsItsFloorAndCeiling_AndFollowsTheSlack()
    {
        // Slack 50 - 10 = 40 ms, 60%: 24 ms.
        Assert.Equal(24, PlayerbotThinkBudget.Compute(50, 10, 0, 8, 30, 0.6), 6);
        // An idle world is held to the ceiling.
        Assert.Equal(30, PlayerbotThinkBudget.Compute(50, 0, 0, 8, 30, 0.95));
        // A tick the world already filled gets the floor, never zero or negative.
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 60, 0, 8, 30, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 10, 40, 8, 30, 0.6)); // the bots already used the share in motion and intake
        // The bots' own earlier work this tick comes off the budget.
        Assert.Equal(14, PlayerbotThinkBudget.Compute(50, 10, 10, 8, 30, 0.6), 6);
        // Equal floor and ceiling is a fixed budget; a ceiling under the floor is the floor; garbage is the floor.
        Assert.Equal(12, PlayerbotThinkBudget.Compute(50, 0, 0, 12, 12, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(50, 0, 0, 8, 3, 0.6));
        Assert.Equal(8, PlayerbotThinkBudget.Compute(double.NaN, 0, 0, 8, 30, 0.6));
    }

    [Fact]
    public void TheNonBotTime_IsSmoothed_SoOneSpikeDoesNotSwingTheBudget()
    {
        var budget = new PlayerbotThinkBudget();
        budget.Observe(tickMs: 20, botMs: 10); // 10 ms that are not the bots'
        Assert.Equal(10, budget.NonBotMs, 6);
        budget.Observe(tickMs: 110, botMs: 10); // an autosave spike: 100 ms
        Assert.Equal(10 + PlayerbotThinkBudget.SmoothingAlpha * 90, budget.NonBotMs, 6);
        double afterSpike = budget.Budget(50, 0, 8, 30, 0.6);
        Assert.InRange(afterSpike, 8, 30);
        budget.Observe(tickMs: 5, botMs: 10); // a tick that reports less than the bots' own: zero, not negative
        Assert.True(budget.NonBotMs >= 0);
    }

    [Fact]
    public void TheDefaults_AreValid_AndLeaveTheOldBudgetAsTheFloor()
    {
        var options = new PlayerbotOptions();
        options.Validate();
        Assert.Equal(PlayerbotThinkRotation.TickBudgetMs, options.ThinkBudgetMinMs);
        Assert.True(options.ThinkBudgetMaxMs > options.ThinkBudgetMinMs);
        Assert.Equal(300, options.CombatThinkIntervalMs);
        Assert.True(options.CombatThinkIntervalMs < 500, "a fight is thought for more often than the 500 ms decision interval");
        Assert.Equal(PlayerbotRecovery.StuckMs, (uint)options.ThinkStarvedGapMs);
    }

    [Theory]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), 0.5)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), 201)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMinMs), double.NaN)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), 7)]   // under the default floor of 8
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), 201)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetMaxMs), double.PositiveInfinity)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetTickShare), 0.01)]
    [InlineData(nameof(PlayerbotOptions.ThinkBudgetTickShare), 1.0)]
    [InlineData(nameof(PlayerbotOptions.CombatThinkIntervalMs), 49)]
    [InlineData(nameof(PlayerbotOptions.CombatThinkIntervalMs), 60_001)]
    [InlineData(nameof(PlayerbotOptions.CombatLaneShare), 0.05)]
    [InlineData(nameof(PlayerbotOptions.CombatLaneShare), 0.95)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), 999)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), -1)]
    [InlineData(nameof(PlayerbotOptions.ThinkStarvedGapMs), 3_600_001)]
    public void OutOfRangeThinkOptions_AreRefused(string name, double value)
    {
        var options = new PlayerbotOptions();
        typeof(PlayerbotOptions).GetProperty(name)!.SetValue(options, Convert.ChangeType(value, typeof(PlayerbotOptions).GetProperty(name)!.PropertyType));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThinkOptions_BindFromConfiguration_AndAFixedBudgetAndAnOffSwitchAreValid()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlayerbotOptions.SectionName + ":ThinkBudgetMinMs"] = "12",
            [PlayerbotOptions.SectionName + ":ThinkBudgetMaxMs"] = "12",
            [PlayerbotOptions.SectionName + ":ThinkBudgetTickShare"] = "0.5",
            [PlayerbotOptions.SectionName + ":CombatThinkIntervalMs"] = "400",
            [PlayerbotOptions.SectionName + ":CombatLaneShare"] = "0.5",
            [PlayerbotOptions.SectionName + ":ThinkStarvedGapMs"] = "0",
        }).Build();
        PlayerbotOptions options = PlayerbotOptions.Bind(configuration);
        options.Validate();
        Assert.Equal(12, options.ThinkBudgetMinMs);
        Assert.Equal(12, options.ThinkBudgetMaxMs);
        Assert.Equal(0.5, options.ThinkBudgetTickShare);
        Assert.Equal(400, options.CombatThinkIntervalMs);
        Assert.Equal(0.5, options.CombatLaneShare);
        Assert.Equal(0, options.ThinkStarvedGapMs);
    }

    [Fact]
    public void TheStarvationCheck_FiresOncePerEpisode_AndStaysSilentWhenHealthy()
    {
        bool reported = false;
        // Healthy: every check within the bound.
        for (uint now = 1000; now < 9000; now += 50)
            Assert.False(PlayerbotThinkStarvation.ShouldReport(now, lastThinkMs: now - 400, boundMs: 10_000, ref reported));
        Assert.False(reported);
        // Exactly at the bound is not over it.
        Assert.False(PlayerbotThinkStarvation.ShouldReport(11_000, 1000, 10_000, ref reported));
        // Over it: once, however many ticks follow.
        Assert.True(PlayerbotThinkStarvation.ShouldReport(11_050, 1000, 10_000, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(11_100, 1000, 10_000, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(20_000, 1000, 10_000, ref reported));
        // The next think clears the flag (the feature does it); a new long gap is a new episode.
        reported = false;
        Assert.True(PlayerbotThinkStarvation.ShouldReport(40_000, 25_000, 10_000, ref reported));
        // Off, and a clock that went backwards.
        reported = false;
        Assert.False(PlayerbotThinkStarvation.ShouldReport(40_000, 1, boundMs: 0, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(100, 200, 1000, ref reported));
    }

    private static async Task<(double Budget, int IntervalMs)> FeatureBudgetAsync(Action<PlayerbotOptions> configure, double atLeast = 0)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: configure);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        // The feature starts at the old 8 ms; the first ticks replace it with the option-driven budget.
        DateTime deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => feature.LastThinkBudgetMs) == PlayerbotThinkRotation.TickBudgetMs) await Task.Delay(20);
        // The first sample (a slow start-up tick) is smoothed away over a few dozen ticks.
        double budget = await host.World.InvokeAsync(() => feature.LastThinkBudgetMs);
        while (budget < atLeast && DateTime.UtcNow < deadline.AddSeconds(15))
        {
            await Task.Delay(100);
            budget = await host.World.InvokeAsync(() => feature.LastThinkBudgetMs);
        }

        int interval = host.World.Options.TickIntervalMs;
        await feature.ShutdownBeforeWorldStopAsync();
        return (budget, interval);
    }

    private static async Task<(double Budget, (double TickIntervalMs, double NonBotMs, double SpentMs) Inputs)> FeatureBudgetAndInputsAsync(Action<PlayerbotOptions> configure)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: configure);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        DateTime deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => feature.LastThinkBudgetMs) == PlayerbotThinkRotation.TickBudgetMs) await Task.Delay(20);
        var captured = await host.World.InvokeAsync(() => (feature.LastThinkBudgetMs, feature.LastThinkBudgetInputs));
        await feature.ShutdownBeforeWorldStopAsync();
        return captured;
    }

    [Fact]
    public async Task TheFeatureBudget_FollowsTheOptionsAndTheTickSlack()
    {
        // A fixed budget: min = max.
        Assert.Equal(12, (await FeatureBudgetAsync(o => { o.ThinkBudgetMinMs = 12; o.ThinkBudgetMaxMs = 12; })).Budget);
        // A tiny share of the tick, floor 1: at most max(1, 0.05 x interval) (the old 8 ms constant or the ceiling would show here).
        (double small, int interval) = await FeatureBudgetAsync(o => { o.ThinkBudgetMinMs = 1; o.ThinkBudgetMaxMs = 30; o.ThinkBudgetTickShare = 0.05; });
        Assert.InRange(small, 1, Math.Max(1, 0.05 * interval));
        // A big share: whatever the test world's slack, the budget is the formula of the inputs the feature recorded for that tick (captured
        // in one world-thread call, so the smoothed non-bot time cannot move between the two reads), inside the options' range.
        (double budget, (double TickIntervalMs, double NonBotMs, double SpentMs) inputs) = await FeatureBudgetAndInputsAsync(
            o => { o.ThinkBudgetMinMs = 1; o.ThinkBudgetMaxMs = 30; o.ThinkBudgetTickShare = 0.95; });
        Assert.Equal(PlayerbotThinkBudget.Compute(inputs.TickIntervalMs, inputs.NonBotMs, inputs.SpentMs, 1, 30, 0.95), budget, 9);
        Assert.InRange(budget, 1, 30);
    }

    [Fact]
    public void TheCadenceReport_CarriesTheLaneSplitTheBudgetAndTheP99_AndTheCommandPrintsThem()
    {
        var stats = new PlayerbotCadenceStats();
        for (int i = 0; i < 90; i++) stats.RecordGap(500);             // quiet bots every 0.5 s
        for (int i = 0; i < 10; i++) stats.RecordGap(300, combat: true); // fighters every 0.3 s
        stats.RecordGap(9_000);
        stats.EndTick(thinks: 30, timeLimited: true, actionLimited: false, budgetMs: 20);
        stats.EndTick(thinks: 30, timeLimited: false, actionLimited: false, budgetMs: 10);
        Assert.Null(stats.Last);
        Assert.True(stats.TryReport(PlayerbotCadenceStats.ReportIntervalMs, bots: 750, out PlayerbotCadenceReport report));

        Assert.Equal(10, report.CombatThinks);
        Assert.Equal(400u, report.CombatGapP95Ms);   // bucket edge of 300 ms
        Assert.Equal(15.0, report.AvgBudgetMs);
        Assert.Equal(50.0, report.TimeLimitedPct);
        Assert.True(report.GapP99Ms >= report.GapP95Ms);
        Assert.Equal(report, stats.Last);

        string[] lines = PlayerbotExtraCommands.CadenceLines(report, 18.5).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains("750 bots", lines[0], StringComparison.Ordinal);
        Assert.Contains("p99", lines[0], StringComparison.Ordinal);
        Assert.Contains("Combat/dead lane: 10 thinks", lines[1], StringComparison.Ordinal);
        Assert.Contains("time budget 50.0%", lines[2], StringComparison.Ordinal);
        Assert.Contains("no full minute", PlayerbotExtraCommands.CadenceLines(null, 8).Single(), StringComparison.Ordinal);
    }

    private static readonly string Starved = PlayerbotAnomalies.Key("cadence", "think-starved");

    /// <summary>A scripted controller whose think burns <paramref name="ms"/> of wall time.</summary>
    private sealed class Burn(double ms) : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        }

        public void Detached(Guid botId) { }
    }

    /// <summary>Three scripted bots named <c>prefix</c> + a/b/c, a <paramref name="boundMs"/> starvation bound and <paramref name="budgetMs"/> as the fixed think budget.</summary>
    private static async Task<(WorldTestHost Host, ManagedPlayerbotFeature Feature, AnomalyCapture Capture, Dictionary<string, int> Thinks)> StartAsync(
        double budgetMs, int boundMs, double burnMs, string prefix, Action<PlayerbotOptions>? configure = null)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: o => { o.ThinkStarvedGapMs = boundMs; configure?.Invoke(o); });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        feature.ThinkBudgetMs = budgetMs;
        var thinks = new Dictionary<string, int>();
        var names = new Dictionary<Guid, string>();
        feature.ThinkObserver = (id, _) =>
        {
            if (names.TryGetValue(id, out string? name)) thinks[name] = thinks.GetValueOrDefault(name) + 1; // world thread only
        };
        await feature.StartupAsync(default);
        AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Starved && n.Subject.Contains($"bot {prefix}", StringComparison.Ordinal));
        for (int i = 0; i < 3; i++)
        {
            string name = $"{prefix}{(char)('a' + i)}";
            Guid id = Assert.IsType<Guid>((await feature.CreateAsync(name, 1, 1)).BotId);
            names[id] = name;
            Assert.True((await feature.StartScriptedAsync(id.ToString(), new Burn(burnMs))).Success);
        }

        return (host, feature, capture, thinks);
    }

    [Theory]
    [InlineData(5, 3, 5)] // reserve ceil(5 x 0.3) = 2, combat 3; idle bots leave the combat lane's 3 to the main lane
    [InlineData(1, 0, 1)] // the first lane is idle at 1 action
    public async Task TheFeature_GivesEachLaneItsOwnActions(int maxActions, int combat, int main)
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 0, burnMs: 0, maxActions == 1 ? "Laneone" : "Lanefive",
            o => { o.MaxActionsPerTick = maxActions; o.CombatLaneShare = 0.7; });
        try
        {
            (int Combat, int Main) lanes = (0, 0);
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && (lanes = await host.World.InvokeAsync(() => feature.LastLaneActions)) == (0, 0)) await Task.Delay(20);
            Assert.Equal((combat, main), lanes);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AHealthyServer_ReportsNoStarvedBot()
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 1000, burnMs: 0, "Okbot");
        try
        {
            await Task.Delay(3000); // three bots thought for every tick: no gap near the 1 s bound
            Assert.False(capture.Any(Starved), capture.Describe());
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AStarvedBot_IsReportedOncePerEpisode_NotOncePerTick()
    {
        // A zero budget thinks one bot per tick and each think takes 1.2 s, so each of the three bots waits about 3.6 s between thinks:
        // over the 1 s bound every cycle. The two that sit out a tick are silent for the next tick's whole run: one report each, not one per tick.
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 1000, burnMs: 1200, "Hitbot");
        try
        {
            // Three full cycles of thinks (about 11 s), then compare in one world-thread call so nothing moves meanwhile.
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => thinks.Values.Sum()) < 9) await Task.Delay(100);
            (Dictionary<string, int> thinksNow, Dictionary<string, int> reports) = await host.World.InvokeAsync(() => (
                new Dictionary<string, int>(thinks),
                capture.Notices.GroupBy(n => n.Subject.Split(' ')[1]).ToDictionary(g => g.Key, g => g.Count())));
            Assert.True(thinksNow.Values.Sum() >= 9, "the bots did not think three cycles");
            Assert.Equal(3, reports.Count); // every bot was starved and reported
            // One report per silence: a bot's reports never exceed its thinks (+1 for the silence in progress). Reported every tick, the two bots
            // that sit out a tick would each report once per tick of the cycle, about twice per think.
            foreach ((string name, int count) in reports)
                Assert.True(count <= thinksNow.GetValueOrDefault(name) + 1,
                    $"{name}: {count} reports for {thinksNow.GetValueOrDefault(name)} thinks (more than one per episode)");
            Assert.Contains("bound 1000 ms", capture.Notices.First().Detail, StringComparison.Ordinal);
            Assert.Equal(AnomalySeverity.Warning, capture.Notices.First().Severity);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ABotSilentPastHalfTheBound_MakesTheFeatureShrinkTheFirstLanesShare()
    {
        // A zero budget thinks one bot per tick and each think takes 1.2 s: a bot waits about 3.6 s between thinks, past half of the 4 s bound.
        // ReportStarvedBots records that silence and Update hands PriorityScale of it to TickLanes: the rotation must have run with a scale under 1.
        var (host, feature, capture, _) = await StartAsync(budgetMs: 0, boundMs: 4000, burnMs: 1200, "Scalebot");
        try
        {
            double scale = 1;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && (scale = await host.World.InvokeAsync(() => feature.LastPriorityScale)) >= 1) await Task.Delay(50);
            Assert.True(scale < 1, $"a bot sat silent past half the bound but the first lane kept its whole share (scale {scale})");
            Assert.True(scale >= PlayerbotThinkStarvation.MinPriorityScale, $"scale {scale} is under the floor");
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheFeature_MeasuresTheMainLanesVisitCost_ForTheBudgetsCoverageFloor()
    {
        // Each scripted bot's think burns 3 ms: the feature must time its main-lane visits and hand them to the budget (ObserveVisit).
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 0, burnMs: 3, "Visitbot");
        try
        {
            double visitMs = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && (visitMs = await host.World.InvokeAsync(() => feature.ThinkVisitMs)) < 1) await Task.Delay(50);
            Assert.InRange(visitMs, 1, PlayerbotThinkBudget.MaxVisitSampleMs);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithTheBoundOff_TheFeatureNeverShrinksTheFirstLanesShare()
    {
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 0, burnMs: 1200, "Noscalebot");
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => thinks.Values.Sum()) < 6) await Task.Delay(100);
            (int total, double scale) = await host.World.InvokeAsync(() => (thinks.Values.Sum(), feature.LastPriorityScale));
            Assert.True(total >= 6, "the bots did not think two cycles");
            Assert.Equal(1.0, scale);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }
}
