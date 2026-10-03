using Xunit;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// The next quest of a chain is offered right after a turn-in
/// (vmangos HandleQuestgiverChooseRewardOpcode, QuestHandler.cpp:275-277, and Player::GetNextQuest, Player.cpp:12514-12541).
/// </summary>
public sealed class QuestChainOfferTests
{
    private const uint First = 20;
    private const uint Second = 21;

    private static (WorldOpcode Opcode, byte[] Payload)[] TurnIn(QuestFlowKit kit)
    {
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, First, 0, out QuestRewardPlan? plan));
        kit.Drain();
        kit.Services.ApplyReward(plan!);
        return [.. kit.Drain()];
    }

    private static QuestFlowKit Kit(uint[] starters, uint next = Second)
    {
        QuestTemplate first = Task(First, nextInChain: next);
        QuestTemplate second = Task(next);
        return new QuestFlowKit([first, second], [Row(First, QuestStatus.Complete, kills: 2)], starters: starters, enders: [First], rewardable: [First]);
    }

    [Fact]
    public void WhenTheGiverAlsoStartsTheNextQuest_ItsDetailsFollowTheCompletion()
    {
        using QuestFlowKit kit = Kit(starters: [Second]);
        var sent = TurnIn(kit);

        int complete = Array.FindIndex(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete);
        int details = Array.FindIndex(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
        Assert.True(complete >= 0, "quest complete packet");
        Assert.True(details > complete, "details after the completion");
        var reader = new PacketReader(sent[details].Payload);
        Assert.Equal(kit.Creature.Guid.Value, reader.ReadUInt64());
        Assert.Equal(Second, reader.ReadUInt32());
    }

    [Fact]
    public void WhenAnotherGiverStartsTheNextQuest_NothingIsOffered()
    {
        // GetNextQuest only returns a quest this very giver starts (creature quest relations).
        using QuestFlowKit kit = Kit(starters: []);
        Assert.DoesNotContain(TurnIn(kit), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
    }

    [Fact]
    public void AQuestWithoutANextInChain_OffersNothing()
    {
        using QuestFlowKit kit = Kit(starters: [Second], next: 0);
        Assert.DoesNotContain(TurnIn(kit), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
    }

    [Fact]
    public void TheOfferIsMadeEvenWhenTheNextQuestCouldNotBeTakenYet()
    {
        // vmangos sends the details without a CanTakeQuest check; accepting is where the refusal happens.
        QuestTemplate first = Task(First, nextInChain: Second);
        QuestTemplate second = Task(Second, minLevel: 40);
        using var kit = new QuestFlowKit([first, second], [Row(First, QuestStatus.Complete, kills: 2)], starters: [Second], enders: [First], rewardable: [First]);

        Assert.Contains(TurnIn(kit), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
    }
}
