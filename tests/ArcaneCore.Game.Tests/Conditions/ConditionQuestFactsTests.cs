using Xunit;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using Microsoft.Extensions.Logging.Abstractions;
using static ArcaneCore.Game.Tests.Conditions.ConditionTestSupport;

namespace ArcaneCore.Game.Tests.Conditions;

/// <summary>
/// The quest conditions over the real quest service's log (cmangos Player::IsCurrentQuest,
/// GetQuestRewardStatus, CanTakeQuest; Conditions.cpp:220-227, 295-298, 309-313).
/// </summary>
public sealed class ConditionQuestFactsTests
{
    private const uint Taken = 10;
    private const uint Done = 11;
    private const uint Rewarded = 12;
    private const uint Fresh = 13;
    private const uint TooHigh = 14;
    private const uint RepeatableRewarded = 15;

    private static QuestTemplate Task(uint id, byte minLevel = 1, uint special = 0, uint condition = 0) => new()
    {
        Entry = id, Method = 2, MinLevel = minLevel, QuestLevel = 1, Title = $"Quest {id}", SpecialFlags = (byte)special, RequiredCondition = condition,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
    };

    private static CharacterQuestStatus Log(uint id, QuestStatus status, bool rewarded = false)
        => new(1, id, (byte)status, rewarded, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static (QuestNpcServices Services, Player Player) Build(bool load)
    {
        Player player = CreatePlayer(guid: 1);
        QuestTemplate[] templates = [Task(Taken), Task(Done), Task(Rewarded), Task(Fresh), Task(TooHigh, minLevel: 10), Task(RepeatableRewarded, special: 1)];
        var services = new QuestNpcServices(new QuestStore(new QuestContent(templates, [], [])), NpcStore.Empty,
            new QuestNpcDependencies(), new QuestNpcOptions(), new NullSink(), () => 100, NullLogger.Instance);
        PlayerNpcState state = services.Track(player);
        if (load)
        {
            services.CompleteLoad(state, new CharacterQuestData(
            [
                Log(Taken, QuestStatus.Incomplete),
                Log(Done, QuestStatus.Complete),
                Log(Rewarded, QuestStatus.Complete, rewarded: true),
                Log(RepeatableRewarded, QuestStatus.Complete, rewarded: true),
            ], []));
        }

        return (services, player);
    }

    [Fact]
    public void TheQuestLogDecidesTheQuestConditions()
    {
        (QuestNpcServices services, Player player) = Build(load: true);
        ConditionEvaluator e = Evaluator(new ConditionContext { Quests = () => services },
            Row(1, ConditionType.QuestTaken, Taken, 0), Row(2, ConditionType.QuestTaken, Taken, 1), Row(3, ConditionType.QuestTaken, Taken, 2),
            Row(4, ConditionType.QuestTaken, Done, 2), Row(5, ConditionType.QuestTaken, Done, 1), Row(6, ConditionType.QuestTaken, Rewarded, 0),
            Row(7, ConditionType.QuestRewarded, Rewarded), Row(8, ConditionType.QuestRewarded, Done),
            Row(9, ConditionType.QuestRewarded, RepeatableRewarded), Row(10, ConditionType.QuestRewarded, 9999),
            Row(11, ConditionType.QuestNone, Fresh), Row(12, ConditionType.QuestNone, Taken), Row(13, ConditionType.QuestNone, Rewarded),
            Row(14, ConditionType.QuestAvailable, Fresh), Row(15, ConditionType.QuestAvailable, TooHigh), Row(16, ConditionType.QuestAvailable, Taken),
            Row(17, ConditionType.QuestAvailable, 9999));
        bool[] expected =
        [
            true, true, false, true, false, false,    // 1-6: taken / done / rewarded-is-no-longer-current
            true, false, false, false,                // 7-10: rewarded; a repeatable quest never counts as rewarded; unknown quest
            true, false, false,                       // 11-13: quest-none
            true, false, false, false,                // 14-17: can take
        ];
        for (uint id = 1; id <= 17; id++)
        {
            Assert.True(expected[id - 1] == e.IsSatisfied(id, player, null), $"condition {id}");
        }
    }

    [Fact]
    public void BeforeTheQuestLogIsLoaded_TheQuestConditionsFailClosed_EvenReversed()
    {
        (QuestNpcServices services, Player player) = Build(load: false);
        ConditionEvaluator e = Evaluator(new ConditionContext { Quests = () => services },
            Row(1, ConditionType.QuestNone, Fresh),
            Row(2, ConditionType.QuestNone, Fresh, flags: ConditionFlags.ReverseResult),
            Row(3, ConditionType.QuestAvailable, Fresh));
        Assert.False(e.IsSatisfied(1, player, null));
        Assert.False(e.IsSatisfied(2, player, null));
        Assert.False(e.IsSatisfied(3, player, null));
        Assert.Equal(3, e.Unavailable.Values.Sum());
    }

    [Fact]
    public void AQuestTemplateRequiredConditionGatesTheQuest()
    {
        // QuestMenu.CanTakeQuest: RequiredCondition is evaluated with the player and no NPC (cmangos
        // CONDITION_FROM_QUEST). Here the condition is "not a warrior", so this warrior cannot take it.
        Player player = CreatePlayer(guid: 1);
        QuestTemplate gated = Task(20, condition: 50);
        var evaluator = new ConditionEvaluator(
            ConditionTable.Build([Row(50, ConditionType.RaceClass, 0, 1 << 1)]), new ConditionContext());   // paladins only
        var services = new QuestNpcServices(new QuestStore(new QuestContent([gated, Task(21)], [], [])), NpcStore.Empty,
            new QuestNpcDependencies(Conditions: evaluator), new QuestNpcOptions(), new NullSink(), () => 100, NullLogger.Instance);
        services.CompleteLoad(services.Track(player), new CharacterQuestData([], []));
        Assert.False(services.CanTakeQuest(player, 20));
        Assert.True(services.CanTakeQuest(player, 21));

        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Paladin);
        Assert.True(services.CanTakeQuest(player, 20));
    }

    private sealed class NullSink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
    }
}
