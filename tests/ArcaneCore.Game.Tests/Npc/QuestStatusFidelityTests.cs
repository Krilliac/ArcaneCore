using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// SMSG_QUESTGIVER_STATUS marks (vmangos HandleQuestgiverStatusQueryOpcode / GetDialogStatus, QuestHandler.cpp:36-77, 484-574;
/// Player::GetQuestLevelForPlayer, Player.h:1114).
/// </summary>
public sealed class QuestStatusFidelityTests
{
    private const uint Id = 990001;

    private static DialogStatus StatusOf(QuestFlowKit kit)
    {
        kit.Session.Clear();
        kit.Services.QuestgiverStatusQuery(kit.Player, kit.Creature.Guid);
        (WorldOpcode opcode, byte[] payload) = Assert.Single(kit.Drain());
        Assert.Equal(WorldOpcode.SmsgQuestgiverStatus, opcode);
        return (DialogStatus)BitConverter.ToUInt32(payload, 8);
    }

    private static QuestTemplate Level(int questLevel) => new()
    {
        Entry = Id, Method = 2, MinLevel = 1, QuestLevel = questLevel, Title = "Levels", Details = "d", Objectives = "o",
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1,
    };

    [Fact]
    public void QuestLevelZero_UsesThePlayersLevel_SoItIsNotGreyedOut()
    {
        // A level 14 player with Quests.LowLevelHideDiff 4: a quest of level 0 counts as level 14, not as a level 0 quest.
        using var zero = new QuestFlowKit([Level(0)], starters: [Id], level: 14);
        Assert.Equal(DialogStatus.Available, StatusOf(zero));
        using var low = new QuestFlowKit([Level(2)], starters: [Id], level: 14);
        Assert.Equal(DialogStatus.Chat, StatusOf(low)); // genuinely too low: the grey mark
        using var near = new QuestFlowKit([Level(10)], starters: [Id], level: 14);
        Assert.Equal(DialogStatus.Available, StatusOf(near));
    }

    [Fact]
    public void NegativeQuestLevel_StillUsesThePlayersLevel()
    {
        using var kit = new QuestFlowKit([Level(-1)], starters: [Id], level: 14);
        Assert.Equal(DialogStatus.Available, StatusOf(kit));
    }

    [Fact]
    public void TheStatusQuery_DoesNotRequireTheQuestGiverNpcFlag()
    {
        using var kit = new QuestFlowKit([Level(10)], starters: [Id], level: 10, npcFlags: 0);
        Assert.Equal(DialogStatus.Available, StatusOf(kit));
    }

    private static QuestTemplate Auto(bool repeatable) => new()
    {
        Entry = Id, Method = 0, MinLevel = 1, QuestLevel = 10, Title = "Auto", Details = "d", Objectives = "o",
        SpecialFlags = repeatable ? (byte)QuestSpecialFlags.Repeatable : (byte)0,
    };

    [Fact]
    public void ACompletedAutocompleteRepeatableQuest_ShowsTheRepeatableRewardMark_NotReward2()
    {
        // Not in the log: takeable autocomplete, repeatable.
        using var open = new QuestFlowKit([Auto(repeatable: true)], starters: [Id], enders: [Id], level: 10);
        Assert.Equal(DialogStatus.RewardRep, StatusOf(open));
        // In the log as complete and unrewarded: retail still says RewardRep for an autocomplete repeatable quest.
        using var complete = new QuestFlowKit([Auto(repeatable: true)], [Row(Id, QuestStatus.Complete)], starters: [Id], enders: [Id], level: 10);
        Assert.Equal(DialogStatus.RewardRep, StatusOf(complete));
        // An ordinary autocomplete quest that is complete keeps the yellow mark.
        using var ordinary = new QuestFlowKit([Auto(repeatable: false)], [Row(Id, QuestStatus.Complete)], starters: [Id], enders: [Id], level: 10);
        Assert.Equal(DialogStatus.Reward2, StatusOf(ordinary));
    }

    [Fact]
    public void AnIncompleteQuest_ShowsTheIncompleteMark_AndAFinishedOrdinaryQuestTheYellowOne()
    {
        using var incomplete = new QuestFlowKit([Level(10)], [Row(Id, QuestStatus.Incomplete)], enders: [Id], level: 10);
        Assert.Equal(DialogStatus.Incomplete, StatusOf(incomplete));
        using var done = new QuestFlowKit([Level(10)], [Row(Id, QuestStatus.Complete, kills: 1)], enders: [Id], level: 10);
        Assert.Equal(DialogStatus.Reward2, StatusOf(done));
    }
}
