using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Bot;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules.Bot;

/// <summary>
/// The bot host (spec P1): the shipped catalogue and ladder rules load clean, a pass ranks them in today's order with the policy between
/// the urgent errand and the quests, custom rows add, reorder, suppress and gate goals, and a stable share of one class gets a custom goal.
/// </summary>
public sealed class BotGoalLadderTests
{
    private static readonly PlayerbotLadderRung[] TodaysOrder =
    [
        PlayerbotLadderRung.HelpErrand, PlayerbotLadderRung.Equipment, PlayerbotLadderRung.ClassUpkeep, PlayerbotLadderRung.ReturnHome,
        PlayerbotLadderRung.UrgentTown, PlayerbotLadderRung.Policy, PlayerbotLadderRung.Quests, PlayerbotLadderRung.Town,
        PlayerbotLadderRung.SicknessStandDown, PlayerbotLadderRung.Crafting, PlayerbotLadderRung.Economy, PlayerbotLadderRung.Bank,
        PlayerbotLadderRung.Opportunities, PlayerbotLadderRung.Trainers, PlayerbotLadderRung.Riding, PlayerbotLadderRung.QuestDestinations,
    ];

    private const int CustomRuleId = RuleCatalog.CustomIdStart + 1;
    private const short CustomGoalId = RuleCatalog.CustomGoalIdStart;

    private static RuleCatalog Build(RuleContent content) => RuleCatalog.Build(content, RuleVocabulary.Default, new RuleBuildOptions());

    private static RuleCatalog Shipped() => Build(PlayerbotLadderContent.Content);

    private static BotGoalLadder Ladder(IRuleConditionGate? gate = null, int seed = 1)
        => new(new RuleEvaluator(new Random(seed)) { Conditions = gate, FaultSite = "rules.bot.test" });

    /// <summary>The shipped content plus a custom goal that runs the trainer driver, and whatever rows the test adds.</summary>
    private static RuleContent WithCustom(IEnumerable<RuleRecord> rules, IEnumerable<RuleActionRecord> actions, IEnumerable<RuleSubjectFilterRecord>? filters = null)
        => PlayerbotLadderContent.Content with
        {
            Rules = [.. PlayerbotLadderContent.Rules, .. rules],
            Actions = [.. PlayerbotLadderContent.Actions, .. actions],
            Filters = [.. filters ?? []],
            Goals = [.. PlayerbotLadderContent.Goals, new PlayerbotGoalRecord
            {
                GoalId = CustomGoalId, Name = "custom: trainer first", BasePriority = 5000, Driver = (short)PlayerbotLadderRung.Trainers,
            }],
        };

    private static RuleRecord CustomRule(int filterId = 0, byte chance = 100, int conditionId = 0, short eventType = BotRuleIds.GoalEvaluate, int eventParam1 = 0) => new()
    {
        Id = CustomRuleId, Host = (byte)RuleHostKind.Bot, EventType = eventType, EventParam1 = eventParam1, FilterId = filterId, ChancePct = chance,
        ConditionId = conditionId, Flags = (int)RuleFlags.Repeatable, Comment = "custom",
    };

    private static RuleActionRecord Propose(int ruleId, int goal, int weight, byte seq = 0) => new()
    {
        OwnerId = ruleId, Seq = seq, ActionType = BotRuleIds.ProposeGoal, Param1 = goal, Param2 = weight, TargetType = BotRuleIds.SelfTarget,
    };

    [Fact]
    public void TheShippedContent_LoadsWithoutRejections_AsTheSixteenRungLadder()
    {
        RuleCatalog catalog = Shipped();

        Assert.Empty(catalog.Rejected);
        Assert.Equal(16, catalog.RuleCount);
        Assert.Equal(34, catalog.Goals.Count);
        Assert.Equal(TodaysOrder, PlayerbotLadderContent.Rungs);
    }

    [Fact]
    public void TheShippedWeights_Are100Apart_WithThePolicyBetweenTheUrgentErrandAndTheQuests()
    {
        Assert.Equal(1150, PlayerbotLadderContent.WeightOf(PlayerbotLadderRung.Policy));
        int[] withoutPolicy = [.. TodaysOrder.Where(r => r != PlayerbotLadderRung.Policy).Select(PlayerbotLadderContent.WeightOf)];
        Assert.All(withoutPolicy.Zip(withoutPolicy.Skip(1)), pair => Assert.Equal(100, pair.First - pair.Second));
        Assert.True(PlayerbotLadderContent.WeightOf(PlayerbotLadderRung.UrgentTown) > 1150);
        Assert.True(1150 > PlayerbotLadderContent.WeightOf(PlayerbotLadderRung.Quests));
    }

    [Fact]
    public void APass_RanksTheShippedRungsInTodaysOrder_ForEveryBot()
    {
        RuleCatalog catalog = Shipped();
        BotGoalLadder ladder = Ladder();
        for (ulong guid = 1; guid <= 40; guid++)
        {
            var bot = new TestBot(guid, (byte)(1 + guid % 9), (byte)(1 + guid % 60), (byte)(1 + guid % 2));

            Assert.Equal(16, ladder.Choose(bot, catalog, 0));
            Assert.Equal(TodaysOrder, bot.Candidates.ToArray().Select(c => c.Rung));
        }
    }

    [Fact]
    public void APass_AllocatesNothing_OnceWarm()
    {
        RuleCatalog catalog = Shipped();
        BotGoalLadder ladder = Ladder();
        var bot = new TestBot(7);
        for (int i = 0; i < 200; i++) ladder.Choose(bot, catalog, i);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) ladder.Choose(bot, catalog, i);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ACustomRow_MayOutweighTheUrgentErrand_AndReordersTheLadder()
    {
        RuleCatalog catalog = Build(WithCustom([CustomRule()], [Propose(CustomRuleId, CustomGoalId, 1250)]));
        var bot = new TestBot(1);

        Ladder().Choose(bot, catalog, 0);

        Assert.Empty(catalog.Rejected);
        // The custom goal (weight 1250) runs the trainer driver: it ranks between the return-home rung (1300) and the urgent errand (1200), and the shipped trainer proposal is not tried a second time.
        Assert.Equal(PlayerbotLadderRung.ReturnHome, bot.Candidates[3].Rung);
        Assert.Equal(CustomGoalId, bot.Candidates[4].GoalId);
        Assert.Equal(PlayerbotLadderRung.Trainers, bot.Candidates[4].Rung);
        Assert.Equal(PlayerbotLadderRung.UrgentTown, bot.Candidates[5].Rung);
        Assert.Equal(1, bot.Candidates.ToArray().Count(c => c.Rung == PlayerbotLadderRung.Trainers));
        Assert.Equal(16, bot.CandidateCount);
    }

    [Fact]
    public void EqualWeights_BreakTheirTieByBasePriority()
    {
        RuleCatalog catalog = Build(WithCustom([CustomRule()], [Propose(CustomRuleId, CustomGoalId, PlayerbotLadderContent.WeightOf(PlayerbotLadderRung.Quests))]));
        var bot = new TestBot(1);

        Ladder().Choose(bot, catalog, 0);

        // The custom goal's base_priority (5000) beats the quests rung's (1100) at the same weight, and both sit right after the policy.
        Assert.Equal(PlayerbotLadderRung.Policy, bot.Candidates[5].Rung);
        Assert.Equal(CustomGoalId, bot.Candidates[6].GoalId);
        Assert.Equal(PlayerbotLadderRung.Quests, bot.Candidates[7].Rung);
    }

    [Fact]
    public void SuppressGoal_RemovesARung_WhateverTheOrder_AndADisabledGoalProposesNothing()
    {
        short questsGoal = PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.Quests);
        RuleContent content = WithCustom(
            [CustomRule() with { Priority = 100 }],
            [new RuleActionRecord { OwnerId = CustomRuleId, ActionType = BotRuleIds.SuppressGoal, Param1 = questsGoal, TargetType = BotRuleIds.SelfTarget }]);
        content = content with
        {
            Goals = [.. content.Goals.Select(g => g.GoalId == PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.Bank) ? g with { Enabled = false } : g)],
        };
        RuleCatalog catalog = Build(content);
        var bot = new TestBot(1);

        Ladder().Choose(bot, catalog, 0);

        Assert.Empty(catalog.Rejected);
        Assert.DoesNotContain(bot.Candidates.ToArray(), c => c.Rung is PlayerbotLadderRung.Quests or PlayerbotLadderRung.Bank);
        Assert.Equal(14, bot.CandidateCount);
    }

    [Fact]
    public void ARuleCannotProposeAGoalWithoutADriver_OrAnUnknownGoal()
    {
        RuleContent content = WithCustom([CustomRule(), CustomRule() with { Id = CustomRuleId + 1 }],
            [Propose(CustomRuleId, (short)PlayerbotGoalKind.Combat, 10), Propose(CustomRuleId + 1, 4242, 10)]);

        RuleCatalog catalog = Build(content);

        Assert.Equal(2, catalog.Rejected.Count(r => r.Table == RuleDataTables.Action));
        Assert.Contains(catalog.Rejected, r => r.Reason.Contains("without a driver", StringComparison.Ordinal));
        Assert.Contains(catalog.Rejected, r => r.Reason.Contains("not a loaded playerbot_goal", StringComparison.Ordinal));
    }

    [Fact]
    public void BotRules_CannotUseSayOrCast_BecauseBotSubjectsCannotSpeakOrCastThroughRules()
    {
        RuleContent content = WithCustom([CustomRule()], [new RuleActionRecord { OwnerId = CustomRuleId, ActionType = 4, Param1 = 133, TargetType = 1 }]);

        RuleCatalog catalog = Build(content);

        Assert.Contains(catalog.Rejected, r => r.Id == CustomRuleId);
    }

    [Fact]
    public void GoldBelowAndFreeBagSlotsBelow_FireWithThePass_OnlyWhenARuleListens()
    {
        short riding = PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.Riding);
        RuleContent content = WithCustom(
            [CustomRule(eventType: BotRuleIds.GoldBelow, eventParam1: 10_000), CustomRule(eventType: BotRuleIds.FreeBagSlotsBelow, eventParam1: 3) with { Id = CustomRuleId + 1 }],
            [Propose(CustomRuleId, CustomGoalId, 5000), Propose(CustomRuleId + 1, riding, 5001)]);
        RuleCatalog catalog = Build(content);
        Assert.Empty(catalog.Rejected);
        BotGoalLadder ladder = Ladder();
        var poorAndFull = new TestBot(1) { MoneyCopper = 500, Slots = 1 };
        var richAndEmpty = new TestBot(2) { MoneyCopper = 99_999, Slots = 10 };

        ladder.Choose(poorAndFull, catalog, 0);
        ladder.Choose(richAndEmpty, catalog, 0);

        // The poor bot gets both: the riding rung at 5001, then the custom trainer goal at 5000 (the shipped trainer and riding proposals are deduplicated).
        Assert.Equal([riding, CustomGoalId], poorAndFull.Candidates[..2].ToArray().Select(c => c.GoalId));
        Assert.Equal(PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.HelpErrand), richAndEmpty.Candidates[0].GoalId);

        int moneyReads = poorAndFull.MoneyReads;
        ladder.Choose(poorAndFull, Shipped(), 1);
        Assert.Equal(moneyReads, poorAndFull.MoneyReads); // no gold rule is loaded any more: the money is not even read
    }

    [Fact]
    public void ADelayedAction_OnTheBotHost_IsRejectedAtLoad_AndSoIsAListWithADelay()
    {
        RuleContent delayed = WithCustom([CustomRule()], [Propose(CustomRuleId, CustomGoalId, 5000) with { DelayMs = 500 }]);
        RuleRejection rule = Assert.Single(Build(delayed).Rejected);
        Assert.Equal(RuleDataTables.Action, rule.Table);
        Assert.Contains("never resumes a delayed action", rule.Reason, StringComparison.Ordinal);

        const int StartList = 2;
        RuleContent viaList = WithCustom([CustomRule()], [new RuleActionRecord { OwnerId = CustomRuleId, ActionType = StartList, Param1 = 9, TargetType = BotRuleIds.SelfTarget }]) with
        {
            Lists = [new RuleListRecord { Id = 9 }],
            ListActions = [Propose(9, CustomGoalId, 5000) with { DelayMs = 100 }],
        };
        RuleCatalog catalog = Build(viaList);
        Assert.Contains(catalog.Rejected, r => r.Table == RuleDataTables.List && r.Id == 9 && r.Reason.Contains("never resumes", StringComparison.Ordinal));
        Assert.Contains(catalog.Rejected, r => r.Table == RuleDataTables.Action && r.Id == CustomRuleId);

        RuleContent immediate = WithCustom([CustomRule()], [Propose(CustomRuleId, CustomGoalId, 5000)]);
        Assert.Empty(Build(immediate).Rejected);
    }

    [Fact]
    public void ADelayThatIsOnlyReachableThroughANestedList_IsRejectedToo()
    {
        const int StartList = 2;
        RuleActionRecord Start(int owner, int list) => new() { OwnerId = owner, ActionType = StartList, Param1 = list, TargetType = BotRuleIds.SelfTarget };
        RuleContent content = WithCustom([CustomRule()], [Start(CustomRuleId, 9)]) with
        {
            Lists = [new RuleListRecord { Id = 9 }, new RuleListRecord { Id = 10 }],
            ListActions = [Start(9, 10), Propose(10, CustomGoalId, 5000) with { DelayMs = 250 }],
        };

        RuleCatalog catalog = Build(content);

        Assert.Null(catalog.FindRule(CustomRuleId));
        Assert.Contains(catalog.Rejected, r => r.Table == RuleDataTables.List && r.Id == 10 && r.Reason.Contains("never resumes", StringComparison.Ordinal));
        Assert.Contains(catalog.Rejected, r => r.Table == RuleDataTables.Action && r.Id == CustomRuleId);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(-1)]
    public void AGoalWithADriverThatIsNotARung_IsRejectedAtLoad(int driver)
    {
        RuleContent content = new() { Goals = [new PlayerbotGoalRecord { GoalId = CustomGoalId, Name = "bad driver", Driver = (short)driver }] };

        RuleRejection rejection = Assert.Single(Build(content).Rejected);
        Assert.Contains("is not a ladder rung", rejection.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(117)]
    [InlineData(150)]
    [InlineData(199)]
    public void LadderGoalIds_AreOnlyTheDefinedRungs(int goalId)
    {
        RuleContent content = new() { Goals = [new PlayerbotGoalRecord { GoalId = (short)goalId, Name = "outside the rungs", Driver = 1 }] };

        Assert.Contains("is not a PlayerbotGoalKind value or a ladder rung", Assert.Single(Build(content).Rejected).Reason, StringComparison.Ordinal);
        Assert.Empty(Build(new RuleContent { Goals = [new PlayerbotGoalRecord { GoalId = RuleCatalog.RungGoalIdEnd, Name = "last rung", Driver = 16 }] }).Rejected);
    }

    [Fact]
    public void ACondition_GatesItsRule_WithTheBotAsSource_AndFailsClosedWhenUndecidable()
    {
        RuleContent content = WithCustom([CustomRule(conditionId: 77)], [Propose(CustomRuleId, CustomGoalId, 5000)]);
        ConditionTable table = ConditionTable.Build([new ConditionRecord(77, (int)ConditionType.ActiveGameEvent, 1, 0, 0, 0, 0)]);
        var bot = new TestBot(1);
        Assert.Contains(Build(content).Rejected, r => r.Reason.Contains("conditions row", StringComparison.Ordinal));

        RuleCatalog catalog = RuleCatalog.Build(content, RuleVocabulary.Default, new RuleBuildOptions { Conditions = table });
        Assert.Empty(catalog.Rejected);

        var open = new GateProbe(true);
        Ladder(open).Choose(bot, catalog, 0);
        Assert.Equal(CustomGoalId, bot.Candidates[0].GoalId);
        Assert.Equal(77u, open.Last);

        Ladder(new GateProbe(false)).Choose(bot, catalog, 1);
        Assert.Equal(16, bot.CandidateCount);
        Assert.DoesNotContain(bot.Candidates.ToArray(), c => c.GoalId == CustomGoalId);

        Ladder(new GateProbe(null)).Choose(bot, catalog, 2);
        Assert.Equal(16, bot.CandidateCount);
        Assert.DoesNotContain(bot.Candidates.ToArray(), c => c.GoalId == CustomGoalId);
    }

    [Fact]
    public void BotConditionGate_NeedsABotInTheWorld()
    {
        var gate = new BotConditionGate((_, _) => true);

        Assert.Null(gate.Evaluate(1, new TestBot(1), null));
        Assert.Null(gate.Evaluate(1, new TestSubject(2), null));
    }

    [Fact]
    public void ACustomRow_ForA30PercentShareOfOneClass_IsObservedByThatShare_AndStaysStable()
    {
        const int Warrior = 1;
        var filter = new RuleSubjectFilterRecord { Id = 1, ClassMask = 1 << (Warrior - 1), LevelMin = 20, LevelMax = 40, SharePct = 30, ShareSeed = 77 };
        RuleContent content = WithCustom([CustomRule(filterId: 1)], [Propose(CustomRuleId, CustomGoalId, 1250)], [filter]);
        RuleCatalog catalog = Build(content);
        Assert.Empty(catalog.Rejected);
        BotGoalLadder ladder = Ladder();

        int warriorsInBand = 0, selected = 0, outsiders = 0;
        var selectedGuids = new HashSet<ulong>();
        for (ulong guid = 1; guid <= 2000; guid++)
        {
            byte cls = (byte)(1 + guid % 9);
            byte level = (byte)(10 + guid % 40);
            var bot = new TestBot(guid, cls, level);
            ladder.Choose(bot, catalog, 0);
            bool has = bot.Candidates.ToArray().Any(c => c.GoalId == CustomGoalId);
            bool inBand = cls == Warrior && level is >= 20 and <= 40;
            if (inBand) warriorsInBand++;
            if (has && inBand) { selected++; selectedGuids.Add(guid); }
            if (has && !inBand) outsiders++;
        }

        Assert.Equal(0, outsiders);
        Assert.InRange(warriorsInBand, 50, 400);
        double share = (double)selected / warriorsInBand;
        Assert.InRange(share, 0.18, 0.42);

        // The same bots are selected on a later pass, and after the catalog is rebuilt from the same rows.
        RuleCatalog again = Build(content);
        for (ulong guid = 1; guid <= 2000; guid++)
        {
            var bot = new TestBot(guid, (byte)(1 + guid % 9), (byte)(10 + guid % 40));
            ladder.Choose(bot, again, 99_999);
            Assert.Equal(selectedGuids.Contains(guid), bot.Candidates.ToArray().Any(c => c.GoalId == CustomGoalId));
        }
    }

    private sealed class GateProbe(bool? answer) : IRuleConditionGate
    {
        public uint Last { get; private set; }

        public bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker)
        {
            Last = conditionId;
            return answer;
        }
    }

    internal sealed class TestBot(ulong guid, byte cls = 1, byte level = 30, byte team = 2) : BotRuleSubject
    {
        public override ulong Guid { get; } = guid;

        public override bool IsInCombat => false;

        public override RuleSubjectFacts Facts { get; } = new(cls, 1, level, team, 0, 0, 0, RuleSubjectFlags.Player | RuleSubjectFlags.Bot);

        public long MoneyCopper { get; set; }

        public int Slots { get; set; }

        public int MoneyReads { get; private set; }

        public override long Money
        {
            get
            {
                MoneyReads++;
                return MoneyCopper;
            }
        }

        public override int FreeBagSlots => Slots;
    }
}
