using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Retail turn-in: the autocomplete quest (Method 0) is rewarded from the ender's list without being in the journal
/// (vmangos Player::CanRewardQuest, Player.cpp:12682-12739), refusals say why and re-send the offer window
/// (QuestHandler.cpp:262-271), and CMSG_QUESTGIVER_COMPLETE_QUEST goes through the request-items redirect
/// (GossipDef.cpp:484-497).
/// </summary>
public sealed class QuestTurnInTests
{
    private const uint Id = 930001;

    private static QuestFlowKit AutoKit(QuestTemplate quest, Action<QuestNpcOptions>? configure = null, Class cls = Class.Warrior)
        => new([quest], starters: [quest.Entry], enders: [quest.Entry], cls: cls, configure: configure);

    private static void Settle(QuestFlowKit kit, QuestRewardPlan plan)
    {
        Guid operation = Guid.NewGuid();
        Assert.True(kit.Player.BeginQuestSettlement(operation));
        using (kit.Player.BeginQuestSettlementPublication(operation))
        {
            kit.Services.ApplyReward(plan);
        }

        Assert.True(kit.Player.EndQuestSettlement(operation));
    }

    [Fact]
    public void AutocompleteQuest_NeverInTheLog_IsRewardedWithAVirtualRow_AndCannotBeRewardedTwice()
    {
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0));
        Assert.Equal(QuestConstants.MaxQuestLogSize, kit.State.Quests.FindSlot(Id));
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan));
        Assert.True(plan.InsertIfMissing);
        Assert.Equal((byte)QuestStatus.Complete, plan.ExpectedQuest.Status);
        Assert.False(plan.ExpectedQuest.Rewarded);
        Assert.Equal(((byte)QuestStatus.Complete, true), (plan.RewardedQuest.Status, plan.RewardedQuest.Rewarded));

        Settle(kit, plan);
        QuestStatusData data = kit.State.Quests.Get(Id)!;
        Assert.Equal((QuestStatus.Complete, true), (data.Status, data.Rewarded));
        Assert.Equal(QuestConstants.MaxQuestLogSize, kit.State.Quests.FindSlot(Id));
        Assert.Contains(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestComplete);

        // The reward row was committed by the settlement, never queued as an ordinary delta.
        Assert.Empty(kit.Sink.Rows);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void RepeatableAutocomplete_ReturnsToStatusNone_AndCanBeTurnedInAgain()
    {
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable));
        for (int pass = 0; pass < 2; pass++)
        {
            Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan), $"pass {pass}");
            Assert.Equal((byte)QuestStatus.None, plan.RewardedQuest.Status);
            Settle(kit, plan);
            Assert.Equal((QuestStatus.None, true), (kit.State.Quests.Get(Id)!.Status, kit.State.Quests.Get(Id)!.Rewarded));
        }
    }

    [Fact]
    public void AutocompleteDeliver_NeedsTheItemsInTheBags_AndConsumesThem()
    {
        QuestTemplate quest = QuestFlowKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable,
            reqItem: ItemTestData.ToughJerky, reqItemCount: 2);
        using QuestFlowKit kit = AutoKit(quest);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        kit.Session.Clear();
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
        // vmangos Player.cpp:12708-12716: a missing delivery item is ITEM_NOT_FOUND naming the item, then the offer returns.
        Assert.Equal([ArcaneCore.Game.Items.InventoryResult.ItemNotFound], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, kit.Session.Sent.Last().Opcode);

        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out QuestRewardPlan? plan));
        Settle(kit, plan);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void AutocompleteQuest_FailingTheClassCheck_IsNotRewardable()
    {
        // CanTakeQuest(skipStatusCheck) still applies the class gate (Player.cpp:12684-12698): packet editing cannot help.
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0, classes: 1u << ((byte)Class.Mage - 1)));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
        Assert.Empty(kit.Session.Sent);
    }

    [Fact]
    public void AutocompleteQuest_FailingTheClassCheck_IsRewardableByAGameMaster()
    {
        // Player.cpp:12688-12692: a game master bypasses the CanTakeQuest gate for an autocomplete quest.
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0, classes: 1u << ((byte)Class.Mage - 1)));
        kit.Player.Flags |= PlayerFlags.Gm;
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void NotEnoughMoney_SendsInvalid22_AndResendsTheOfferWindow()
    {
        var quest = new QuestTemplate
        {
            Entry = Id, Method = 0, QuestLevel = 1, Title = "Pay up", RequestItemsText = "Pay.", RewOrReqMoney = -100_000,
        };
        using QuestFlowKit kit = AutoKit(quest);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
        var sent = kit.Session.Sent.ToArray();
        Assert.Equal(QuestPackets.QuestInvalid(QuestInvalidReason.NotEnoughMoney).AsSpan().ToArray(),
            Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid).Payload);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, sent[^1].Opcode);
        Assert.Equal(50u, kit.Player.Money);
    }

    [Fact]
    public void AllowlistOnlyMode_StillGatesAutocompleteQuests()
    {
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0), o => o.RewardMode = QuestRewardMode.AllowlistOnly);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void RequestReward_ForAnAutocompleteQuest_OffersTheReward_WithoutAnyJournalEntry()
    {
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0));
        kit.Services.RequestReward(kit.Player, kit.Creature.Guid, Id);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, Assert.Single(kit.Session.Sent).Opcode);
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(Id));
    }

    [Fact]
    public void CompleteQuestRequest_WithoutRequestText_GoesStraightToOfferReward()
    {
        QuestTemplate quest = QuestFlowKit.Task(Id, method: 0);
        var noText = new QuestTemplate { Entry = quest.Entry, Method = 0, QuestLevel = 1, Title = "Quiet" };
        using QuestFlowKit kit = AutoKit(noText);
        kit.Services.CompleteQuest(kit.Player, kit.Creature.Guid, Id);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, Assert.Single(kit.Session.Sent).Opcode);
    }

    [Fact]
    public void CompleteQuestRequest_ForADeliveryQuest_SendsRequestItemsWithTheCompletableFlag()
    {
        QuestTemplate quest = QuestFlowKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable,
            reqItem: ItemTestData.ToughJerky, reqItemCount: 1);
        using QuestFlowKit kit = AutoKit(quest);
        kit.Services.CompleteQuest(kit.Player, kit.Creature.Guid, Id);
        var incomplete = Assert.Single(kit.Session.Sent);
        Assert.Equal(WorldOpcode.SmsgQuestgiverRequestItems, incomplete.Opcode);

        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        kit.Session.Clear();
        kit.Services.CompleteQuest(kit.Player, kit.Creature.Guid, Id);
        var complete = Assert.Single(kit.Session.Sent);
        Assert.Equal(WorldOpcode.SmsgQuestgiverRequestItems, complete.Opcode);
        Quest definition = kit.Services.Quests.Get(Id)!;
        Func<uint, uint> display = id => kit.Player.Inventory.Templates.Find(id)?.DisplayId ?? 0;
        Assert.Equal(QuestPackets.RequestItems(kit.Creature.Guid, definition, true, display, closeOnCancel: false).AsSpan().ToArray(), complete.Payload);
        Assert.NotEqual(incomplete.Payload, complete.Payload);
    }

    [Fact]
    public void AutocompleteAdapter_IsDiscovered_SoMethodZeroQuestsAreNotWithheld()
    {
        using QuestFlowKit kit = AutoKit(QuestFlowKit.Task(Id, method: 0));
        Assert.Equal(QuestAdapter.Autocomplete, kit.Services.ProvidedAdapters & QuestAdapter.Autocomplete);
        Assert.True(kit.Services.Supported(kit.Services.Quests.Get(Id)!));
    }
}
