using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Bots;
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
    public void TheMainPass_IsASmoothedEstimateBiasedHigh_AndCapsLongGapsInsteadOfDroppingThem()
    {
        var pass = new PlayerbotMainPass();
        Assert.Equal(0u, pass.Ms);
        pass.Note(0);
        Assert.Equal(0u, pass.Ms);
        pass.Note(8000);
        Assert.Equal(8000u, pass.Ms);
        for (int i = 0; i < 1000; i++) pass.Note(12_000);
        Assert.InRange(pass.Ms, 11_900u, 12_000u);

        // Biased high: alternating 1 s and 3 s gaps (mean 2 s) rise quickly and fall slowly, so the estimate sits above the mean.
        var alternating = new PlayerbotMainPass();
        for (int i = 0; i < 2000; i++) alternating.Note(i % 2 == 0 ? 1000u : 3000u);
        Assert.True(alternating.Ms > 2000, $"estimate {alternating.Ms} ms is not above the 2000 ms mean");

        // A gap over the cap counts as the cap: a pass that really is longer (5000 bots) is still followed, not frozen.
        var slow = new PlayerbotMainPass();
        slow.Note(100_000);
        for (int i = 0; i < 200; i++) slow.Note(150_000);
        Assert.InRange(slow.Ms, 119_900u, PlayerbotMainPass.MaxSampleMs);
        Assert.InRange(PlayerbotThinkStarvation.ReportBoundMs(10_000, slow.Ms), 179_800u, 180_000u); // the intended absolute backstop
        slow.Note(uint.MaxValue);
        Assert.InRange(slow.Ms, 119_900u, PlayerbotMainPass.MaxSampleMs);
        slow.Seed(uint.MaxValue);
        Assert.Equal(PlayerbotMainPass.MaxSampleMs, slow.Ms);
    }

    [Fact]
    public async Task TheMainPass_IsHandedOverByAHotReload()
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 0, burnMs: 0, "Handbot");
        try
        {
            uint written = await host.World.InvokeAsync(() =>
            {
                feature.SeedMainPassForTests(20_000, pin: true);
                var handover = new ArcaneCore.World.Features.FeatureHandover();
                feature.Detach(handover);
                return handover.TryGet("playerbots.mainPassMs", out uint ms) ? ms : 0u;
            });
            Assert.Equal(20_000u, written);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARestoredHandover_SeedsTheMainPassOfTheNewFeature()
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 0, burnMs: 0, "Restbot");
        try
        {
            uint restored = await host.World.InvokeAsync(() =>
            {
                feature.SeedMainPassForTests(0, pin: true);
                var handover = new ArcaneCore.World.Features.FeatureHandover();
                handover.Set("playerbots.mainPassMs", 20_000u);
                feature.RestoreMainPassForTests(handover);
                return feature.MainPassMs;
            });
            Assert.Equal(20_000u, restored);
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The host's feature is detached (its subscriptions and registry claims released, as a reload does), then a second instance takes the
    /// handover: <see cref="ManagedPlayerbotFeature.Detach"/> never clears the first instance's pass, so only a restore through
    /// AttachFrom -> RestoreHandover -> RestoreMainPass can bring the value to the fresh instance.
    /// </summary>
    private static async Task<(ManagedPlayerbotFeature Fresh, uint Before, uint After, ArcaneCore.World.Features.FeatureSubscriptions Subscriptions)> ReloadAsync(
        WorldTestHost host, ManagedPlayerbotFeature feature, uint seed)
    {
        var features = host.WorldServices.GetRequiredService<ArcaneCore.World.Features.FeatureHost>();
        Assert.True((await features.DetachAsync(nameof(ManagedPlayerbotFeature))).Ok);
        var fresh = new ManagedPlayerbotFeature(host.WorldServices, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagedPlayerbotFeature>.Instance);
        var subscriptions = new ArcaneCore.World.Features.FeatureSubscriptions(nameof(ManagedPlayerbotFeature));
        (uint before, uint after) = await host.World.InvokeAsync(() =>
        {
            feature.SeedMainPassForTests(seed, pin: true);
            var handover = new ArcaneCore.World.Features.FeatureHandover();
            feature.Detach(handover);
            uint untouched = fresh.MainPassMs;
            fresh.AttachFrom(host.World, handover, subscriptions);
            return (untouched, fresh.MainPassMs);
        });
        return (fresh, before, after, subscriptions);
    }

    [Fact]
    public async Task ADetachedPass_ReachesAFreshFeatureThroughAttachFrom()
    {
        var (host, feature, capture, _) = await StartAsync(budgetMs: double.PositiveInfinity, boundMs: 0, burnMs: 0, "Reloadbot");
        ManagedPlayerbotFeature? fresh = null;
        ArcaneCore.World.Features.FeatureSubscriptions? subscriptions = null;
        try
        {
            uint before, after;
            (fresh, before, after, subscriptions) = await ReloadAsync(host, feature, 20_000);
            Assert.Equal(0u, before);
            Assert.Equal(20_000u, after);
        }
        finally
        {
            capture.Dispose();
            if (fresh is not null)
            {
                await fresh.QuiesceAsync(CancellationToken.None); // AttachFrom started the chat, client, policy and navigation services; only the quiesce stops them
                await host.World.InvokeAsync(() => { fresh.Detach(new ArcaneCore.World.Features.FeatureHandover()); subscriptions!.Dispose(); return true; });
            }
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AFeatureWithNoPassYet_DoesNotWipeThePassABotsMotionKnows()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 8000);
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 0);
                Assert.Equal(8000u, PlayerbotMotion.SchedulerPassMsOf(session.Player!));
                PlayerbotMotion.NoteSchedulerPass(session.Player!, 9000);
                Assert.Equal(9000u, PlayerbotMotion.SchedulerPassMsOf(session.Player!));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AStarvedBot_IsReportedOncePerEpisode_WhenItFallsBehindThePass()
    {
        // The pass estimate is pinned at 100 ms while each of the three bots waits about 2.1 s between thinks (budget 0: one bot per tick,
        // 700 ms a think): every bot is far behind the pass the feature believes in, so the report must fire, once per silence, as a Warning.
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 1000, burnMs: 700, "Behindbot");
        try
        {
            await host.World.InvokeAsync(() => { feature.SeedMainPassForTests(100, pin: true); return true; });
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => thinks.Values.Sum()) < 12) await Task.Delay(100);
            (Dictionary<string, int> thinksNow, List<AnomalyNotice> pinned) = await host.World.InvokeAsync(() => (
                new Dictionary<string, int>(thinks),
                capture.Notices.Where(n => n.Detail.Contains("full pass 100 ms", StringComparison.Ordinal)).ToList()));
            Assert.True(thinksNow.Values.Sum() >= 12, "the bots did not think four cycles");
            Assert.NotEmpty(pinned);
            Assert.All(pinned, n => Assert.Equal(AnomalySeverity.Warning, n.Severity));
            Assert.Contains("bound 1000 ms, configured 1000 ms, full pass 100 ms", pinned[0].Detail, StringComparison.Ordinal);
            foreach (IGrouping<string, AnomalyNotice> bot in pinned.GroupBy(n => n.Subject.Split(' ')[1]))
                Assert.True(bot.Count() <= thinksNow.GetValueOrDefault(bot.Key) + 1,
                    $"{bot.Key}: {bot.Count()} reports for {thinksNow.GetValueOrDefault(bot.Key)} thinks (more than one per episode)");
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheFeature_MeasuresTheMainLanePass_AndHandsItToTheBotsMotion()
    {
        // A zero budget thinks one bot per tick and each think takes 100 ms: a bot waits about 300 ms between thinks.
        var (host, feature, capture, _) = await StartAsync(budgetMs: 0, boundMs: 0, burnMs: 100, "Passbot");
        try
        {
            Guid first = Guid.Empty;
            Action<Guid, PlayerbotThinkProbe>? inner = feature.ThinkObserver;
            feature.ThinkObserver = (id, probe) =>
            {
                if (first == Guid.Empty) first = id; // world thread only
                inner?.Invoke(id, probe);
            };
            (uint pass, uint seen) = (0, 0);
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && (pass == 0 || seen == 0))
            {
                await Task.Delay(50);
                (pass, seen) = await host.World.InvokeAsync(() => (feature.MainPassMs,
                    first != Guid.Empty && feature.FindSession(first)?.Player is { } player ? PlayerbotMotion.SchedulerPassMsOf(player) : 0u));
            }

            Assert.InRange(pass, 100u, 5000u);
            Assert.True(seen > 0, "the bot's motion never saw the scheduler's pass");
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
    public void AWorldStall_IsNotARotationGap_TheSilenceIsJudgedWithoutIt()
    {
        // A world thread that stood still about 16.5 s (a gap of 16 500 ms between two bot ticks) leaves every bot about 19.7 s silent against
        // a 12 254 ms bound (8169 ms pass). The excess over StallTickMs is the stall. The figures are illustrative.
        Assert.Equal(0u, PlayerbotThinkStarvation.StallMs(50));
        Assert.Equal(0u, PlayerbotThinkStarvation.StallMs(PlayerbotThinkStarvation.StallTickMs));
        Assert.Equal(15_500u, PlayerbotThinkStarvation.StallMs(16_500));
        Assert.Equal(0u, PlayerbotThinkStarvation.StallMs(uint.MaxValue)); // a clock that went backwards
        uint bound = PlayerbotThinkStarvation.ReportBoundMs(10_000, 8169);
        Assert.Equal(12_254u, bound);
        bool reported = false;
        // 19745 ms silent, 15500 of them the world stood still: 4245 ms behind the rotation, not starved.
        Assert.False(PlayerbotThinkStarvation.ShouldReport(119_745, 100_000, bound, ref reported, stalledMs: 15_500));
        Assert.False(reported);
        // The same silence with no stall is a starved bot, as before.
        Assert.True(PlayerbotThinkStarvation.ShouldReport(119_745, 100_000, bound, ref reported, stalledMs: 0));
        // A real straggler is still reported through a stall: 30 s silent, 15.5 s stalled, 14.5 s behind.
        reported = false;
        Assert.True(PlayerbotThinkStarvation.ShouldReport(130_000, 100_000, bound, ref reported, stalledMs: 15_500));
        // A stall larger than the silence (credited after the think) is no silence, not a wrap.
        reported = false;
        Assert.False(PlayerbotThinkStarvation.ShouldReport(101_000, 100_000, bound, ref reported, stalledMs: 5_000));
    }

    [Fact]
    public async Task AWorldStall_ReportsNoStarvedBot()
    {
        // A zero budget thinks one bot per tick, in rotation (about 150 ms between the thinks of one bot): none is behind it. A 3 s command
        // blocks the world thread (in the commands phase, before the features), so the two bots the first tick after it does not think for
        // are 5 s silent, past the 3 s bound, though the rotation is as fair as before. Without the stall they are about 1 s silent, so a loaded
        // runner has 2 s of margin.
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 3000, burnMs: 0, "Stallbot");
        try
        {
            // A measured pass (not the 3 x cold-start bound) and every bot primed.
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && !await host.World.InvokeAsync(() => feature.MainPassMs > 0 && thinks.Count == 3 && thinks.Values.All(n => n >= 5)))
                await Task.Delay(50);
            (int before, int thinksBefore, uint bound) = await host.World.InvokeAsync(() => (capture.Notices.Count, thinks.Values.Sum(),
                PlayerbotThinkStarvation.ReportBoundMs(3000, feature.MainPassMs)));
            Assert.True(thinksBefore >= 15, "the bots did not warm up");
            Assert.Equal(3000u, bound); // a short pass leaves the configured bound
            await host.World.InvokeAsync(() => { Thread.Sleep(5000); return true; });
            while (DateTime.UtcNow < deadline.AddSeconds(30) && await host.World.InvokeAsync(() => thinks.Values.Sum()) < thinksBefore + 9) await Task.Delay(50);
            int thinksAfter = await host.World.InvokeAsync(() => thinks.Values.Sum());
            Assert.True(thinksAfter >= thinksBefore + 9, $"the bots thought {thinksAfter - thinksBefore} times after the stall, not three more cycles");
            (int after, string described) = await host.World.InvokeAsync(() => (capture.Notices.Count, capture.Describe()));
            Assert.True(after == before, $"{after - before} bots were reported starved by a world stall: {described}");
        }
        finally
        {
            capture.Dispose();
            await feature.ShutdownBeforeWorldStopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void TheStarvedBound_IsFairness_NotAbsoluteSlowness()
    {
        // Off stays off; unmeasured is a cold start (3 x the bound).
        Assert.Equal(0u, PlayerbotThinkStarvation.ReportBoundMs(0, 8000));
        Assert.Equal(30_000u, PlayerbotThinkStarvation.ReportBoundMs(10_000, 0));
        // An 8 s pass keeps the 10 s bound under 12 s; a 20 s pass moves it to 30 s; a short pass leaves the configured bound alone.
        Assert.Equal(12_000u, PlayerbotThinkStarvation.ReportBoundMs(10_000, 8000));
        Assert.Equal(30_000u, PlayerbotThinkStarvation.ReportBoundMs(10_000, 20_000));
        Assert.Equal(10_000u, PlayerbotThinkStarvation.ReportBoundMs(10_000, 3000));
        Assert.Equal(int.MaxValue, (long)PlayerbotThinkStarvation.ReportBoundMs(uint.MaxValue / 2, uint.MaxValue));

        // Every bot equally slow (a 20 s pass, each silent up to a pass): none is reported. One skipped past 1.5 x the pass: reported, once.
        uint bound = PlayerbotThinkStarvation.ReportBoundMs(10_000, 20_000);
        bool reported = false;
        Assert.False(PlayerbotThinkStarvation.ShouldReport(120_000, 100_000, bound, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(130_000, 100_000, bound, ref reported));
        Assert.True(PlayerbotThinkStarvation.ShouldReport(130_001, 100_000, bound, ref reported));
        Assert.False(PlayerbotThinkStarvation.ShouldReport(140_000, 100_000, bound, ref reported));
    }

    [Fact]
    public async Task AUniformlySlowRotation_ReportsNoStarvedBot_OnceThePassIsMeasured()
    {
        // A zero budget thinks one bot per tick and each think takes 1.2 s, so each of the three bots waits about 3.6 s between thinks:
        // over the 2 s bound every cycle, but every bot equally. That is slowness, not a bot behind the rotation (cadence/think-starved).
        // The bots start one after the other, so the pass is measured while the rotation grows: reports in that warm-up are not judged.
        var (host, feature, capture, thinks) = await StartAsync(budgetMs: 0, boundMs: 2000, burnMs: 1200, "Slowbot");
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => feature.MainPassMs) < 3000) await Task.Delay(100);
            (uint pass, int reportsAtWarm, int thinksAtWarm) = await host.World.InvokeAsync(() => (feature.MainPassMs, capture.Notices.Count, thinks.Values.Sum()));
            Assert.True(pass >= 3000, $"the pass was measured as {pass} ms");
            while (DateTime.UtcNow < deadline && await host.World.InvokeAsync(() => thinks.Values.Sum()) < thinksAtWarm + 9) await Task.Delay(100);
            (int thinksNow, int reportsNow, string described) = await host.World.InvokeAsync(() => (thinks.Values.Sum(), capture.Notices.Count, capture.Describe()));
            Assert.True(thinksNow >= thinksAtWarm + 9, "the bots did not think three more cycles");
            Assert.True(reportsNow == reportsAtWarm, $"{reportsNow - reportsAtWarm} bots were reported starved though every bot waited the same: {described}");
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

    /// <summary>A scripted controller whose detach (the drop path, off the world thread) parks until released.</summary>
    private sealed class ParkOnDetach : IPlayerbotController
    {
        public readonly TaskCompletionSource Parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);

        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId)
        {
            Parked.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A bot dropped and another started after a tick captured the active set, with a world-thread Snapshot() inside that tick (a think
    /// observer here). The tick's publish must compare against the version it captured the set at, not the shared cache mark the inner
    /// Snapshot() just moved: with the equal counts the stale set took the in-place path and wrote the dropped bot's row over the new
    /// bot's (a ghost row for the dropped one, no running row for the new one).
    /// </summary>
    [Fact]
    public async Task ASnapshotInsideATick_AfterAStartAndADrop_DoesNotLetTheTicksStaleSetOverwriteIt()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        feature.ThinkBudgetMs = double.PositiveInfinity;
        await feature.StartupAsync(default);
        try
        {
            Guid droppedId = Assert.IsType<Guid>((await feature.CreateAsync("Stalea", 1, 1)).BotId);
            Guid keptId = Assert.IsType<Guid>((await feature.CreateAsync("Staleb", 1, 1)).BotId);
            Guid newId = Assert.IsType<Guid>((await feature.CreateAsync("Stalec", 1, 1)).BotId);
            var parkedDrop = new ParkOnDetach();
            Assert.True((await feature.StartScriptedAsync(droppedId.ToString(), parkedDrop)).Success);
            Assert.True((await feature.StartScriptedAsync(keptId.ToString(), new Burn(0))).Success);

            // The new bot's start parks at the write that marks it Running: past the world-thread login step, so only the add to the active set is left.
            var startParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owners.BeforeUpdate = async (bot, _) =>
            {
                if (bot.BotId != newId || bot.State != ManagedPlayerbotState.Running) return;
                startParked.TrySetResult();
                await startRelease.Task.WaitAsync(TimeSpan.FromSeconds(30));
            };
            Task<PlayerbotOperationResult> start = feature.StartScriptedAsync(newId.ToString(), new Burn(0));
            await startParked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Task drop = feature.DropFromActiveForTestsAsync(droppedId);
            await parkedDrop.Parked.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int fired = 0;
            feature.ThinkObserver = (_, _) =>
            {
                // World thread, inside the tick, after it captured the active set (the dropped bot in it, the new one not).
                if (Interlocked.Exchange(ref fired, 1) != 0) return;
                try
                {
                    startRelease.TrySetResult();
                    Assert.True(start.Wait(TimeSpan.FromSeconds(20)), "the start did not complete");
                    parkedDrop.Release.Set();
                    Assert.True(drop.Wait(TimeSpan.FromSeconds(20)), "the drop did not complete");
                    Assert.Equal(2, feature.Snapshot().Count(s => s.State == ManagedPlayerbotState.Running)); // the republish a command handler would trigger
                    done.TrySetResult(true);
                }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True((await start).Success);
            await host.World.InvokeAsync(() => true); // the tick that ran the observer has published by now

            IReadOnlyList<PlayerbotStatus> rows = feature.Snapshot(); // off the world thread: the copy the tick published
            Assert.Equal(3, rows.Count);
            Assert.Equal(rows.Count, rows.Select(r => r.BotId).Distinct().Count());
            Assert.Equal(ManagedPlayerbotState.Running, rows.Single(r => r.BotId == newId).State);
            Assert.Equal(ManagedPlayerbotState.Running, rows.Single(r => r.BotId == keptId).State);
            Assert.NotEqual(ManagedPlayerbotState.Running, rows.Single(r => r.BotId == droppedId).State);
        }
        finally
        {
            feature.ThinkObserver = null;
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }
}
