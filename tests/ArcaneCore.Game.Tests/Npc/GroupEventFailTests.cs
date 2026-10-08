using ArcaneCore.Game.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// vmangos Player::GroupEventFailHappens (Player.cpp:13888-13903), what a failed escort calls: the quest fails while it is incomplete; a
/// quest whose objectives are already complete is kept (FailQuest alone would fail that one too).
/// </summary>
public sealed class GroupEventFailTests
{
    private const uint Incomplete = 1393;
    private const uint Complete = 1394;

    [Fact]
    public void GroupEventFail_FailsAnIncompleteQuest_AndKeepsACompleteOne()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Incomplete), QuestFlowKit.Task(Complete)],
            [QuestFlowKit.Row(Incomplete, QuestStatus.Incomplete), QuestFlowKit.Row(Complete, QuestStatus.Complete, kills: 2)]);

        kit.Services.GroupEventFailHappens(kit.Player, Incomplete);
        kit.Services.GroupEventFailHappens(kit.Player, Complete);

        Assert.Equal(QuestStatus.Failed, kit.State.Quests.GetStatus(Incomplete));
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(Complete));
    }
}
