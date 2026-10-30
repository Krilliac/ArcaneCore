using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using Xunit;
using static ArcaneCore.Game.Tests.Rules.RuleTestKit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// The rule loader and validator (design 5.1): valid rows compile into per-host, per-owner, per-event arrays sorted by priority; every bad
/// row is returned as (table, id, reason), never thrown, and a rule with one bad action is rejected whole.
/// </summary>
public sealed class RuleCatalogTests
{
    [Fact]
    public void AValidRule_Loads_WithItsActionsInSeqOrder_AndBindsByOwner()
    {
        RuleRecord global = Rule(1);
        RuleRecord entry = Rule(2) with { OwnerKind = (byte)RuleOwnerKind.Entry, OwnerId = 77, Priority = -5 };
        RuleRecord other = Rule(3) with { OwnerKind = (byte)RuleOwnerKind.Entry, OwnerId = 78 };
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [global, entry, other],
            Actions = [RecordOf(1, 12, seq: 2), RecordOf(1, 11, seq: 1), RecordOf(2, 20), RecordOf(3, 30)],
        });

        Assert.Empty(catalog.Rejected);
        Assert.Equal(3, catalog.RuleCount);
        Assert.Equal([1, 2], catalog.FindRule(1)!.Actions.Select(a => (int)a.Seq));
        Assert.Equal([11, 12], catalog.FindRule(1)!.Actions.Select(a => a.Param(1)));

        RuleBinding binding = catalog.Bind(Kind, new RuleOwner(RuleOwnerKind.Entry, 77));
        Assert.Equal([2, 1], binding.ForEvent(Ping).ToArray().Select(b => b.Rule.Id));
        Assert.Same(binding, catalog.Bind(Kind, new RuleOwner(RuleOwnerKind.Entry, 77)));
        Assert.Equal([1], catalog.Bind(Kind).ForEvent(Ping).ToArray().Select(b => b.Rule.Id));
        Assert.True(catalog.Bind(Kind).ForEvent(ValueBelow).IsEmpty);
        Assert.Contains("3 rules (0 disabled)", catalog.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleAndTextIds_AreAcceptedInsideTheNamedSubBands_AndRejectedPastThem()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(RuleIdBands.CustomStart), Rule(RuleIdBands.EventAi), Rule(RuleIdBands.DbScripts + 1), Rule(RuleIdBands.End - 1), Rule(1_500_000_000)],
            Actions = [RecordOf(RuleIdBands.CustomStart, 1), RecordOf(RuleIdBands.EventAi, 1), RecordOf(RuleIdBands.DbScripts + 1, 1), RecordOf(RuleIdBands.End - 1, 1)],
            Texts = [new RuleTextRecord { Id = RuleIdBands.SmartAi, ContentDefault = "ok" }, new RuleTextRecord { Id = 1_500_000_000, ContentDefault = "too high" }],
        });

        Assert.Equal(4, catalog.RuleCount);
        Assert.Equal([1_500_000_000], catalog.Rejected.Where(r => r.Table == "rule").Select(r => r.Id));
        RuleRejection text = Assert.Single(catalog.Rejected, r => r.Table == "rule_text");
        Assert.Equal(1_500_000_000, text.Id);
        Assert.Contains("past the named converter sub-bands", text.Reason, StringComparison.Ordinal);
        Assert.Contains(RuleIdBands.SmartAi, catalog.Texts.Keys);
    }

    public static TheoryData<string, RuleRecord, string, string> BadRules() => new()
    {
        { "unknown host", Rule(1) with { Host = 9 }, "rule", "host 9 is not registered" },
        { "owner kind the host refuses", Rule(1) with { OwnerKind = (byte)RuleOwnerKind.Profile, OwnerId = 4 }, "rule", "owner_kind 4" },
        { "global with an owner id", Rule(1) with { OwnerId = 5 }, "rule", "a global rule needs owner_id 0" },
        { "owner without id", Rule(1) with { OwnerKind = (byte)RuleOwnerKind.Entry }, "rule", "owner_id 0 must be 1 or more" },
        { "unknown event", Rule(1) with { EventType = 99 }, "rule", "event_type 99 is not an event of host test" },
        { "unused event parameter", Rule(1) with { EventParam2 = 3 }, "rule", "event_param2 is 3, but test.ping takes 0 parameters" },
        { "event parameter out of range", Rule(1) with { EventType = ValueBelow, EventParam1 = 101 }, "rule", "event_param1 (threshold) = 101 must be 0..100" },
        { "missing filter", Rule(1) with { FilterId = 4 }, "rule", "filter_id 4 does not exist" },
        { "chance 0", Rule(1) with { ChancePct = 0 }, "rule", "chance_pct 0 must be 1..100" },
        { "chance above 100", Rule(1) with { ChancePct = 101 }, "rule", "chance_pct 101" },
        { "missing condition", Rule(1) with { ConditionId = 999 }, "rule", "condition_id 999 is not a loaded conditions row" },
        { "cooldown max below min", Rule(1) with { CooldownMinMs = 500, CooldownMaxMs = 100 }, "rule", "cooldown 500..100 ms" },
        { "negative cooldown", Rule(1) with { CooldownMinMs = -1, CooldownMaxMs = 100 }, "rule", "cooldown -1..100 ms" },
        { "cooldown range up to int.MaxValue", Rule(1) with { CooldownMinMs = 0, CooldownMaxMs = int.MaxValue }, "rule", "cooldown 0..2147483647 ms must be at most 86400000 ms" },
        { "fixed cooldown of int.MaxValue", Rule(1) with { CooldownMinMs = int.MaxValue, CooldownMaxMs = int.MaxValue }, "rule", "cooldown 2147483647..2147483647 ms must be at most 86400000 ms" },
        { "cooldown one past the cap", Rule(1) with { CooldownMinMs = 0, CooldownMaxMs = RuleCatalog.MaxCooldownMs + 1 }, "rule", "must be at most 86400000 ms" },
        { "unknown flag", Rule(1) with { Flags = 0x101 }, "rule", "flags 0x101 has unknown bits" },
        { "combat and out of combat", Rule(1) with { Flags = (int)(RuleFlags.Repeatable | RuleFlags.CombatOnly | RuleFlags.OutOfCombatOnly) }, "rule", "never fire" },
    };

    [Theory]
    [MemberData(nameof(BadRules))]
    public void ABadRuleRow_IsRejectedWithItsReason_NotThrown(string because, RuleRecord rule, string table, string reason)
    {
        RuleCatalog catalog = Build(Simple(rule, Rule(2)));

        RuleRejection rejection = Assert.Single(catalog.Rejected);
        Assert.True(rejection.Table == table && rejection.Id == 1 && rejection.Reason.Contains(reason, StringComparison.Ordinal),
            $"{because}: got {rejection}");
        Assert.Null(catalog.FindRule(1));
        Assert.NotNull(catalog.FindRule(2));
    }

    public static TheoryData<string, RuleActionRecord, string> BadActions() => new()
    {
        { "unknown action", Action(1, 77), "seq 0: action_type 77 is not an action of host test" },
        { "unused action parameter", RecordOf(1, 5) with { Param3 = 1 }, "param3 is 1, but test.record takes 1 parameter" },
        { "phase out of range", Action(1, SetPhase, 32), "param1 (phase) = 32 must be a phase 0..31" },
        { "unknown target", RecordOf(1, 5) with { TargetType = 0 }, "target_type 0 is not a target of host test" },
        { "unused target parameter", RecordOf(1, 5) with { TargetParam1 = 2 }, "target_param1 is 2, but self takes 0 parameters" },
        { "missing text", Action(1, Say, 8), "param1 (text_id) = 8 is not a loaded rule_text id" },
        { "missing list", Action(1, StartList, 8), "param1 (list_id) = 8 is not a rule_list id" },
        { "spell id 0", Action(1, Cast, 0), "param1 (spell_id) = 0 is not a known spell" },
        { "negative delay", RecordOf(1, 5, delayMs: -1), "delay_ms -1 must not be negative" },
        { "delay of int.MaxValue", RecordOf(1, 5, delayMs: int.MaxValue), "delay_ms 2147483647 must be at most 86400000" },
        { "delay one past the cap", RecordOf(1, 5, delayMs: RuleCatalog.MaxDelayMs + 1), "delay_ms 86400001 must be at most 86400000" },
        { "action flags", RecordOf(1, 5) with { Flags = 1 }, "flags 1 are not defined for actions" },
    };

    [Theory]
    [MemberData(nameof(BadActions))]
    public void ARuleWithOneBadAction_IsRejectedWhole(string because, RuleActionRecord bad, string reason)
    {
        RuleCatalog catalog = Build(new RuleContent { Rules = [Rule(1)], Actions = [RecordOf(1, 1, seq: 5), bad] });

        RuleRejection rejection = Assert.Single(catalog.Rejected);
        Assert.True(rejection.Table == "rule_action" && rejection.Id == 1 && rejection.Reason.Contains(reason, StringComparison.Ordinal),
            $"{because}: got {rejection}");
        Assert.Equal(Kind, rejection.Host);
        Assert.Equal(RuleOwner.Global, rejection.Owner);
        Assert.Equal(0, catalog.RuleCount);
    }

    [Fact]
    public void ARuleWithoutActions_OrphanActions_AndARepeatedSeq_AreReported()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1), Rule(2)],
            Actions = [RecordOf(2, 1, seq: 1), RecordOf(2, 2, seq: 1), RecordOf(9, 1)],
        });

        Assert.Equal(
            ["rule 1: the rule has no rule_action rows (it would fire and do nothing)", "rule_action 2: seq 1: duplicate seq (the first row is kept)", "rule_action 9: seq 0: rule 9 does not exist"],
            catalog.Rejected.Select(r => r.ToString()));
        Assert.Equal([1], catalog.FindRule(2)!.Actions.Select(a => a.Param(1)));
    }

    [Fact]
    public void ADisabledRule_IsValidatedButNotLoaded()
    {
        RuleCatalog catalog = Build(Simple(Rule(1) with { Enabled = false }, Rule(2) with { Enabled = false, ChancePct = 0 }));

        Assert.Equal(0, catalog.RuleCount);
        Assert.Equal(1, catalog.DisabledCount);
        Assert.Equal(2, Assert.Single(catalog.Rejected).Id);
    }

    [Fact]
    public void Filters_Texts_AndGoals_AreValidated()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Filters =
            [
                new RuleSubjectFilterRecord { Id = 1, ClassMask = 0x1, SharePct = 30 },
                new RuleSubjectFilterRecord { Id = 2, LevelMin = 40, LevelMax = 20 },
                new RuleSubjectFilterRecord { Id = 3, SharePct = 0 },
                new RuleSubjectFilterRecord { Id = 4, ClassMask = 0x20 },
                new RuleSubjectFilterRecord { Id = 5, Team = 3 },
                new RuleSubjectFilterRecord { Id = 6, SubjectFlags = 0x100 },
            ],
            Texts = [new RuleTextRecord { Id = 1, ContentDefault = "Hello" }, new RuleTextRecord { Id = 2, ContentDefault = " " }],
            Goals =
            [
                new PlayerbotGoalRecord { GoalId = 1, Name = "quest" },
                new PlayerbotGoalRecord { GoalId = 500, Name = "nope" },
                new PlayerbotGoalRecord { GoalId = RuleCatalog.CustomGoalIdStart, Name = "custom" },
                new PlayerbotGoalRecord { GoalId = 1001, Name = "" },
            ],
        });

        Assert.Equal([1], catalog.Filters.Keys);
        Assert.Equal([1], catalog.Texts.Keys);
        Assert.Equal([(short)1, RuleCatalog.CustomGoalIdStart], catalog.Goals.Keys.Order());
        Assert.Equal(
            [
                "rule_text 2: content_default is empty",
                "rule_subject_filter 2: level_min 40 is above level_max 20",
                "rule_subject_filter 3: share_pct 0 must be 1..100 (0 would select no subject)",
                "rule_subject_filter 4: class_mask 32 has bits outside the playable classes (0x5DF)",
                "rule_subject_filter 5: team 3 must be 0 (any), 1 (Alliance) or 2 (Horde)",
                "rule_subject_filter 6: subject_flags 256 has unknown bits (known: 0x1F)",
                "playerbot_goal 500: goal_id 500 is not a PlayerbotGoalKind value or a ladder rung (101..116); custom goals start at 1000",
                "playerbot_goal 1001: name is empty",
            ],
            catalog.Rejected.Select(r => r.ToString()));
    }

    [Fact]
    public void References_ResolveAtLoad_AndConditionsAreRecorded()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1) with { ConditionId = Condition }],
            Actions = [Action(1, Say, 1, seq: 0), Action(1, StartList, 4, seq: 1)],
            Texts = [new RuleTextRecord { Id = 1, ContentDefault = "Hello" }],
            Lists = [new RuleListRecord { Id = 4 }],
            ListActions = [RecordOf(4, 40)],
        });

        Assert.Empty(catalog.Rejected);
        CompiledAction[] actions = catalog.FindRule(1)!.Actions;
        Assert.Equal("Hello", Assert.IsType<RuleText>(actions[0].Reference).Content);
        Assert.Same(catalog.FindList(4), actions[1].Reference);
        Assert.Equal([(uint)Condition], catalog.ReferencedConditions);
    }

    [Fact]
    public void ASpellCheck_FromTheHost_RejectsAnUnknownSpell()
    {
        RuleContent content = new() { Rules = [Rule(1)], Actions = [Action(1, Cast, 133)] };

        Assert.Empty(Build(content).Rejected);
        RuleCatalog checkedCatalog = Build(content, new RuleBuildOptions { Conditions = RuleTestKit.Conditions, SpellExists = id => id == 116 });
        Assert.Contains("param1 (spell_id) = 133 is not a known spell", Assert.Single(checkedCatalog.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_ThatLoopWithoutDelay_ThatNoRuleStarts_OrThatStartARejectedList_AreRejected()
    {
        RuleCatalog catalog = Build(new RuleContent
        {
            Rules = [Rule(1), Rule(2), Rule(3), Rule(4)],
            Actions = [Action(1, StartList, 10), Action(2, StartList, 20), Action(3, StartList, 30), Action(4, StartList, 50)],
            Lists = [new RuleListRecord { Id = 10 }, new RuleListRecord { Id = 11 }, new RuleListRecord { Id = 20 }, new RuleListRecord { Id = 30 },
                new RuleListRecord { Id = 40 }, new RuleListRecord { Id = 50 }, new RuleListRecord { Id = 51 }],
            ListActions =
            [
                // 10 -> 11 -> 10 with no delay: an endless loop within one firing.
                RecordOf(10, 1), Action(10, StartList, 11, seq: 1), Action(11, StartList, 10),
                // 20 repeats itself every 5 s: allowed.
                RecordOf(20, 2), Action(20, StartList, 20, seq: 1, delayMs: 5000),
                // 30 starts 31, which does not exist.
                Action(30, StartList, 31),
                // 40 is never started.
                RecordOf(40, 4),
                // 50 starts 51, whose action is bad.
                Action(50, StartList, 51), Action(51, 77),
            ],
        });

        Assert.NotNull(catalog.FindRule(2));
        Assert.Same(catalog.FindList(20), catalog.FindList(20)!.Actions[1].Reference);
        Assert.Equal([2], catalog.Rules.Select(r => r.Id));
        string[] rejected = [.. catalog.Rejected.Select(r => r.ToString())];
        Assert.Contains(rejected, r => r.StartsWith("rule_list 11: for host test: seq 0: action start_rule_list: rule_list 10: it starts itself again with no delay", StringComparison.Ordinal));
        Assert.Contains(rejected, r => r.StartsWith("rule_list 10: for host test: seq 1: action start_rule_list: rule_list 11:", StringComparison.Ordinal));
        Assert.Contains("rule_list 40: no loaded rule starts this list (a list that is never started does nothing)", rejected);
        Assert.Contains(rejected, r => r.StartsWith("rule_list 51: for host test: seq 0: action_type 77", StringComparison.Ordinal));
        Assert.Contains(rejected, r => r.StartsWith("rule_action 1: seq 0: action start_rule_list: rule_list 10:", StringComparison.Ordinal));
        Assert.Contains(rejected, r => r.StartsWith("rule_action 3: seq 0: action start_rule_list: rule_list 30: seq 0: action start_rule_list: param1 (list_id) = 31", StringComparison.Ordinal));
        Assert.Contains(rejected, r => r.StartsWith("rule_action 4:", StringComparison.Ordinal));
    }
}
