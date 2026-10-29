using ArcaneCore.Data.World.Rules;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules.Creatures;

/// <summary>The EventAI converter (inventory 12): ids, phase masks, timer caps, texts, options and the report of what it could not convert.</summary>
public sealed class EventAiRuleConverterTests
{
    private const int Cap = EventAiRuleConverter.MaxTimerMs;
    private const int TextBase = CreatureRuleIds.ConvertedTextIdBase;

    private static CreatureAiEvent Row(uint id, byte eventType = 4, uint creature = 100, uint phaseInverse = 0, CreatureAiAction? action = null, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, uint flags = 0, byte chance = 100, uint guid = 0)
        => new()
        {
            Id = id,
            CreatureId = creature,
            CreatureGuid = guid,
            EventType = eventType,
            InversePhaseMask = phaseInverse,
            Chance = chance,
            Flags = flags,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Action1 = action ?? new CreatureAiAction(20, 1, 0, 0),
        };

    private static EventAiConversion Convert(params CreatureAiEvent[] rows)
        => EventAiRuleConverter.Convert(new CreatureAiContent(rows, []));

    private static RuleRecord OnlyRule(EventAiConversion conversion) => Assert.Single(conversion.Content.Rules);

    [Fact]
    public void RuleIds_AreTheBasePlusTheSourceId_AndAConversionIsDeterministic()
    {
        CreatureAiEvent[] rows = [Row(7), Row(1_500_000, eventType: 7), Row(3, guid: 55)];

        EventAiConversion first = Convert(rows);
        EventAiConversion second = Convert([.. rows.Reverse()]);

        Assert.Equal([1 << 30 | 3, (1 << 30) + 7, (1 << 30) + 1_500_000], first.Content.Rules.Select(r => r.Id));
        Assert.Equal(first.Content.Rules, second.Content.Rules);
        Assert.Equal(first.Content.Actions, second.Content.Actions);
        Assert.Equal(first.Content.Texts, second.Content.Texts);
        Assert.All(first.Content.Rules, r => Assert.Equal(CreatureRuleIds.Host, r.Host));
        Assert.Equal((1 << 30) + 7, EventAiRuleConverter.RuleIdOf(7));
    }

    [Fact]
    public void GuidRows_AreOwnedByTheGuid_AndRunAfterEntryRows()
    {
        EventAiConversion conversion = Convert(Row(1), Row(2, guid: 900));

        RuleRecord entry = conversion.Content.Rules[0];
        RuleRecord guid = conversion.Content.Rules[1];
        Assert.Equal((CreatureRuleIds.OwnerEntry, 100, (short)0), (entry.OwnerKind, entry.OwnerId, entry.Priority));
        Assert.Equal((CreatureRuleIds.OwnerGuid, 900, (short)1), (guid.OwnerKind, guid.OwnerId, guid.Priority));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0b100u, ~0b100)]
    [InlineData(0x80000000u, 0x7FFFFFFF)]
    [InlineData(0xFFFFFFFEu, 1)]
    public void PhaseMask_IsTheInverseMaskInverted_As32Bits(uint inverse, int expected)
    {
        EventAiConversion conversion = Convert(Row(1, phaseInverse: inverse));

        RuleRecord rule = OnlyRule(conversion);
        Assert.Equal(expected, rule.PhaseMask);
        Assert.True(rule.Enabled);
        Assert.Empty(conversion.Issues);
    }

    [Fact]
    public void PhaseMask_AllBitsInverse_WritesTheRowDisabled_AndReportsIt()
    {
        EventAiConversion conversion = Convert(Row(41, phaseInverse: uint.MaxValue));

        RuleRecord rule = OnlyRule(conversion);
        Assert.False(rule.Enabled);
        Assert.Equal(0, rule.PhaseMask);
        EventAiConversionIssue issue = Assert.Single(conversion.Issues);
        Assert.Equal((41u, EventAiConversionOutcome.Disabled), (issue.SourceId, issue.Outcome));
        Assert.Contains("32 phases", issue.Reason, StringComparison.Ordinal);
        Assert.Equal(0, conversion.Converted);
    }

    [Fact]
    public void Timers_OverTheCap_AreReported_AndNeverClamped()
    {
        EventAiConversion conversion = Convert(
            Row(1, eventType: 0, p1: 1000, p2: 2000, p3: Cap, p4: Cap),
            Row(2, eventType: 0, p1: 1000, p2: 2000, p3: Cap, p4: Cap + 1),
            Row(3, eventType: 0, p1: Cap + 1, p2: 2000, p3: 100, p4: 100),
            Row(4, eventType: 8, p3: -5, p4: 10));

        RuleRecord kept = OnlyRule(conversion);
        Assert.Equal(((1 << 30) + 1, Cap, Cap), (kept.Id, kept.CooldownMinMs, kept.CooldownMaxMs));
        Assert.Equal([2u, 3u, 4u], conversion.Issues.Select(i => i.SourceId));
        Assert.All(conversion.Issues, i => Assert.Equal(EventAiConversionOutcome.Unconvertible, i.Outcome));
        Assert.Contains("24 h cap", conversion.Issues[0].Reason, StringComparison.Ordinal);
        Assert.Contains("24 h cap", conversion.Issues[1].Reason, StringComparison.Ordinal);
        Assert.Contains("negative", conversion.Issues[2].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatMaximumBelowTheMinimum_BecomesAOneShotRow()
    {
        EventAiConversion conversion = Convert(Row(1, eventType: 8, p3: 5000, p4: 1000));

        RuleRecord rule = OnlyRule(conversion);
        Assert.Equal((0, 0), (rule.CooldownMinMs, rule.CooldownMaxMs));
        RuleActionRecord marker = conversion.Content.Actions.First(a => a.ActionType == CreatureRuleIds.ActionRow);
        Assert.True(((CreatureRuleIds.RowFlags)marker.Param1).HasFlag(CreatureRuleIds.RowFlags.OneShot));
    }

    [Fact]
    public void EventParameters_RepeatRangeAndOptions_AreMapped()
    {
        EventAiConversion conversion = Convert(
            Row(1, eventType: 2, p1: 80, p2: 20, p3: 3000, p4: 6000, flags: 0x01 | 0x20 | 0x400 | 0x100 | 0x200));

        RuleRecord rule = OnlyRule(conversion);
        Assert.Equal(CreatureRuleIds.EventHealthPct, rule.EventType);
        Assert.Equal((80, 20, 0, 0), (rule.EventParam1, rule.EventParam2, rule.EventParam3, rule.EventParam4));
        Assert.Equal((3000, 6000), (rule.CooldownMinMs, rule.CooldownMaxMs));
        Assert.Equal(CreatureRuleIds.FlagRepeatable, rule.Flags);
        RuleActionRecord marker = conversion.Content.Actions.Single(a => a.ActionType == CreatureRuleIds.ActionRow);
        Assert.Equal(0, marker.Seq);
        var flags = (CreatureRuleIds.RowFlags)marker.Param1;
        Assert.Equal(
            CreatureRuleIds.RowFlags.Repeatable | CreatureRuleIds.RowFlags.RandomAction | CreatureRuleIds.RowFlags.CombatAction
            | CreatureRuleIds.RowFlags.RangedModeOnly | CreatureRuleIds.RowFlags.MeleeModeOnly,
            flags);
        Assert.Equal(1, marker.Param2);
    }

    [Fact]
    public void ChanceZeroAndDebugOnlyRows_AreWrittenDisabled_AndChanceIsCapped()
    {
        EventAiConversion conversion = Convert(Row(1, chance: 0), Row(2, flags: 0x80), Row(3, chance: 200));

        Assert.Equal([false, false, true], conversion.Content.Rules.Select(r => r.Enabled));
        Assert.Equal([1, 100, 100], conversion.Content.Rules.Select(r => (int)r.ChancePct));
        Assert.Equal(1, conversion.Converted);
        Assert.Equal(2, conversion.Disabled);
        Assert.Equal(0, conversion.Unconvertible);
    }

    [Fact]
    public void ActionsAreNumberedWithoutGaps_AfterTheMarker()
    {
        CreatureAiEvent row = Row(1) with
        {
            Action1 = default,
            Action2 = new CreatureAiAction(20, 0, 0, 0),
            Action3 = new CreatureAiAction(21, 1, 0, 0),
        };

        EventAiConversion conversion = Convert(row);

        Assert.Equal([0, 1, 2], conversion.Content.Actions.Select(a => (int)a.Seq));
        Assert.Equal([CreatureRuleIds.ActionRow, CreatureRuleIds.ActionAutoAttack, CreatureRuleIds.ActionCombatMovement], conversion.Content.Actions.Select(a => a.ActionType));
        Assert.Equal(2, conversion.Content.Actions[0].Param2);
    }

    [Fact]
    public void UnsupportedTypes_AreReportedByIdAndReason_AndNothingIsWritten()
    {
        EventAiConversion conversion = Convert(
            Row(1, eventType: 19),
            Row(2, action: new CreatureAiAction(16, 1, 2, 3)),
            Row(3, action: new CreatureAiAction(11, 1000, 13, 0)),
            Row(4, eventType: 11, p1: 7),
            Row(5, flags: 0x400) with { Action1 = default, Action2 = new CreatureAiAction(20, 1, 0, 0) },
            Row(6, creature: 0));

        Assert.Empty(conversion.Content.Rules);
        Assert.Empty(conversion.Content.Actions);
        Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u], conversion.Issues.Select(i => i.SourceId));
        Assert.All(conversion.Issues, i => Assert.Equal(EventAiConversionOutcome.Unconvertible, i.Outcome));
        Assert.Contains("event type 19", conversion.Issues[0].Reason, StringComparison.Ordinal);
        Assert.Contains("action type 16", conversion.Issues[1].Reason, StringComparison.Ordinal);
        Assert.Contains("target type 13", conversion.Issues[2].Reason, StringComparison.Ordinal);
        Assert.Contains("spawned condition 7", conversion.Issues[3].Reason, StringComparison.Ordinal);
        Assert.Contains("empty first action", conversion.Issues[4].Reason, StringComparison.Ordinal);
        Assert.Equal(6, conversion.Unconvertible);
    }

    [Fact]
    public void ASourceIdAboveTheBand_IsReported()
    {
        EventAiConversion conversion = Convert(Row(CreatureRuleIds.MaxConvertedSourceId), Row(CreatureRuleIds.MaxConvertedSourceId + 1));

        Assert.Equal((1 << 30) + (int)CreatureRuleIds.MaxConvertedSourceId, OnlyRule(conversion).Id);
        Assert.Equal(CreatureRuleIds.MaxConvertedSourceId + 1, Assert.Single(conversion.Issues).SourceId);
    }

    [Fact]
    public void Texts_AreMappedToTheTwoIdBands_AndOnlyTheReferencedOnesAreWritten()
    {
        var broadcast = new BroadcastText(77, "Hello there", "", 1, 7, 1234, [11, 0, 0], [0, 0, 0]);
        var content = new CreatureAiContent(
            [
                Row(1, action: new CreatureAiAction(1, 77, -5, 0)),
                Row(2, action: new CreatureAiAction(54, -6, 1, 0)),
                Row(3, action: new CreatureAiAction(1, -9, 0, 0)),
            ],
            [
                new CreatureAiText(-5, "Second line", 0, 0, 0),
                new CreatureAiText(-6, "Third line", 1, 0, 5) { Sound = 99, BroadcastTextId = 12 },
                new CreatureAiText(-8, "Never referenced", 0, 0, 0),
            ],
            new BroadcastTextCatalog([broadcast]));

        EventAiConversion conversion = EventAiRuleConverter.Convert(content);

        Assert.Equal([TextBase + 77, TextBase + 500_005, TextBase + 500_006], conversion.Content.Texts.Select(t => t.Id));
        RuleTextRecord line = conversion.Content.Texts[0];
        Assert.Equal(("Hello there", 1, 7, 1234, 11, 77), (line.ContentDefault, (int)line.Type, (int)line.Language, line.Sound, line.Emote, line.BroadcastTextId));
        RuleTextRecord ai = conversion.Content.Texts[2];
        Assert.Equal(("Third line", 99, 5, 12), (ai.ContentDefault, ai.Sound, ai.Emote, ai.BroadcastTextId));

        RuleActionRecord say = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(1) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionSay, TextBase + 77, TextBase + 500_005, 0, CreatureRuleIds.TargetTextAudience), (say.ActionType, say.Param1, say.Param2, say.Param3, say.TargetType));
        RuleActionRecord sayNew = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(2) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionSay, TextBase + 500_006, CreatureRuleIds.TargetVictim, 0), (sayNew.ActionType, sayNew.Param1, sayNew.TargetType, sayNew.TargetParam1));

        EventAiConversionIssue missing = Assert.Single(conversion.Issues);
        Assert.Equal(3u, missing.SourceId);
        Assert.Contains("text -9 does not exist", missing.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextTemplate_IsReported_AndAThirdTextWithoutASecondIsIgnored()
    {
        var content = new CreatureAiContent(
            [
                Row(1, action: new CreatureAiAction(54, -1, 0, 4)),
                Row(2, action: new CreatureAiAction(1, -1, 0, -2)),
            ],
            [new CreatureAiText(-1, "A line", 0, 0, 0)]);

        EventAiConversion conversion = EventAiRuleConverter.Convert(content);

        EventAiConversionIssue template = Assert.Single(conversion.Issues);
        Assert.Equal(1u, template.SourceId);
        Assert.Contains("template", template.Reason, StringComparison.Ordinal);
        RuleActionRecord say = conversion.Content.Actions.Single(a => a.Seq == 1);
        Assert.Equal((TextBase + 500_001, 0, 0), (say.Param1, say.Param2, say.Param3));
    }

    [Fact]
    public void SummonId_CarriesThePositionAsFloatBits_AndTheLifetime()
    {
        var content = new CreatureAiContent(
            [Row(1, action: new CreatureAiAction(32, 4000, 0, 8))],
            [],
            summons: [new CreatureAiSummon(8, 1.5f, -2.25f, 30f, 0.5f, 60_000)]);

        EventAiConversion conversion = EventAiRuleConverter.Convert(content);

        RuleActionRecord summon = conversion.Content.Actions.Single(a => a.Seq == 1);
        Assert.Equal(CreatureRuleIds.ActionSummonAt, summon.ActionType);
        Assert.Equal((4000, 60_000), (summon.Param1, summon.Param2));
        Assert.Equal(
            [1.5f, -2.25f, 30f, 0.5f],
            new[] { summon.Param3, summon.Param4, summon.Param5, summon.Param6 }.Select(BitConverter.Int32BitsToSingle));
        Assert.Equal((CreatureRuleIds.TargetSelf, 1), (summon.TargetType, summon.TargetParam1));
    }

    [Fact]
    public void ActionTargets_AreMapped_AndOptionalOnesAreMarked()
    {
        EventAiConversion conversion = Convert(
            Row(1, action: new CreatureAiAction(11, 1000, 1, 0x20)),
            Row(2, action: new CreatureAiAction(13, 50, 0, 1)),
            Row(3, action: new CreatureAiAction(59, 6, 0, 0)),
            Row(4, action: new CreatureAiAction(59, 6, 1, 0)));

        RuleActionRecord cast = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(1) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionCast, 1000, 0x20, CreatureRuleIds.TargetVictim, 0), (cast.ActionType, cast.Param1, cast.Param2, cast.TargetType, cast.TargetParam1));
        RuleActionRecord threat = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(2) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionThreatSingle, 50, 1, CreatureRuleIds.TargetSelf), (threat.ActionType, threat.Param1, threat.Param2, threat.TargetType));
        RuleActionRecord facing = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(3) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionSetFacing, 0, CreatureRuleIds.TargetInvoker), (facing.ActionType, facing.Param1, facing.TargetType));
        RuleActionRecord reset = conversion.Content.Actions.Single(a => a.OwnerId == EventAiRuleConverter.RuleIdOf(4) && a.Seq == 1);
        Assert.Equal((CreatureRuleIds.ActionSetFacing, 1, CreatureRuleIds.TargetSelf), (reset.ActionType, reset.Param1, reset.TargetType));
    }

    [Fact]
    public void SetPhase_BecomesTheSharedAction_WithThePhaseClamped()
    {
        EventAiConversion conversion = Convert(Row(1, action: new CreatureAiAction(22, 99, 0, 0)), Row(2, action: new CreatureAiAction(22, -3, 0, 0)));

        Assert.Equal([31, 0], conversion.Content.Actions.Where(a => a.Seq == 1).Select(a => a.Param1));
        Assert.All(conversion.Content.Actions.Where(a => a.Seq == 1), a => Assert.Equal(CreatureRuleIds.SharedActionSetPhase, a.ActionType));
    }

    [Fact]
    public void ARichContent_BuildsIntoACatalogWithoutARejectedRow()
    {
        var content = new CreatureAiContent(
            [
                Row(1, eventType: 0, p1: 1000, p2: 2000, p3: 5000, p4: 9000, action: new CreatureAiAction(11, 133, 1, 0), flags: 0x01),
                Row(2, eventType: 2, p1: 50, p2: 0, p3: 0, p4: 0, phaseInverse: 0b10, action: new CreatureAiAction(1, -1, -2, -3), flags: 0x20) with { Action2 = new CreatureAiAction(22, 1, 0, 0) },
                Row(3, eventType: 6, action: new CreatureAiAction(12, 4000, 0, 30_000)),
                Row(4, eventType: 17, p1: 4000, p3: 1000, p4: 2000, action: new CreatureAiAction(32, 4001, 0, 2)),
                Row(5, eventType: 10, p1: 1, p2: 30, p3: 4000, p4: 4000, guid: 9, action: new CreatureAiAction(5, 1, 0, 0)),
                Row(6, eventType: 22, p1: 3, p3: 500, p4: 500, action: new CreatureAiAction(54, -1, 7, 0)),
            ],
            [
                new CreatureAiText(-1, "One", 0, 0, 0),
                new CreatureAiText(-2, "Two", 1, 0, 0),
                new CreatureAiText(-3, "Three", 0, 0, 0),
            ],
            summons: [new CreatureAiSummon(2, 1f, 2f, 3f, 0f, 20_000)]);

        EventAiConversion conversion = EventAiRuleConverter.Convert(content);
        RuleCatalog catalog = RuleCatalog.Build(conversion.Content, RuleVocabulary.Default);

        Assert.Empty(conversion.Issues);
        Assert.Empty(catalog.Rejected.Select(r => $"{r.Table} {r.Id}: {r.Reason}"));
        Assert.Equal(6, catalog.RuleCount);
        Assert.Equal(3, catalog.Texts.Count);
    }
}
