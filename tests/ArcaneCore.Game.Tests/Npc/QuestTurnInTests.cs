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

    private static QuestFlowTestKit AutoKit(QuestTemplate quest, Action<QuestNpcOptions>? configure = null, Class cls = Class.Warrior)
        => new([quest], starters: [quest.Entry], enders: [quest.Entry], cls: cls, configure: configure);

    private static void Settle(QuestFlowTestKit kit, QuestRewardPlan plan)
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
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0));
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
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable));
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
        QuestTemplate quest = QuestFlowTestKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable,
            reqItem: ItemTestData.ToughJerky, reqItemCount: 2);
        using QuestFlowTestKit kit = AutoKit(quest);
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
    public void AutocompleteDeliver_IgnoresItemsOfferedInAnOpenTrade_AndLeavesTheOfferedStackAlone()
    {
        QuestTemplate quest = QuestFlowTestKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable,
            reqItem: ItemTestData.ToughJerky, reqItemCount: 2);
        using QuestFlowTestKit kit = AutoKit(quest);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        uint before = kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky);
        HashSet<ObjectGuid> offered = [.. kit.Player.Inventory.AllItems.Where(i => i.Entry == ItemTestData.ToughJerky).Select(i => i.Guid)];
        kit.Session.Clear();

        // Every jerky is in the trade window, so the delivery is short and nothing is taken.
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _, offered));
        Assert.Contains("missing required delivery item", kit.Services.DiagnoseTurnInRefusal(kit.Player, kit.Creature.Guid, Id, 0, offered));

        Assert.Equal(before, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));

        // Without a trade the same turn-in works.
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void DeliveryCounts_GoThroughAnInjectedItemService_WhichIsToldWhichStacksAreOffered()
    {
        QuestTemplate quest = QuestFlowTestKit.Task(Id, method: 0, special: (byte)(QuestSpecialFlags.Repeatable | QuestSpecialFlags.Deliver),
            reqItem: ItemTestData.ToughJerky, reqItemCount: 2);
        var items = new CountingItemService();
        using QuestFlowTestKit kit = new([quest], starters: [quest.Entry], enders: [quest.Entry], items: items);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        HashSet<ObjectGuid> offered = [.. kit.Player.Inventory.AllItems.Where(i => i.Entry == ItemTestData.ToughJerky).Select(i => i.Guid)];

        Assert.True(kit.Services.MeetsTurnInRequirements(kit.Player, kit.Services.Quests.Get(Id)!));
        Assert.Null(items.LastExclude);

        Assert.False(kit.Services.MeetsTurnInRequirements(kit.Player, kit.Services.Quests.Get(Id)!, offered));
        Assert.Same(offered, items.LastExclude);
        Assert.Contains("missing required delivery item", kit.Services.DiagnoseTurnInRefusal(kit.Player, kit.Creature.Guid, Id, 0, offered));
        Assert.Same(offered, items.LastExclude);
    }

    /// <summary>The player's own count, remembering the exclusion it was last asked with.</summary>
    private sealed class CountingItemService : IItemService
    {
        public IReadOnlySet<ObjectGuid>? LastExclude { get; private set; }

        public ItemInfo? GetItem(uint itemId) => null;

        public uint GetItemCount(Player player, uint itemId, bool inBankAlso, IReadOnlySet<ObjectGuid>? exclude = null)
        {
            LastExclude = exclude;
            return player.Inventory.GetItemCount(itemId, inBankAlso, exclude: exclude is { Count: > 0 } kept ? item => kept.Contains(item.Guid) : null);
        }

        public ArcaneCore.Game.Items.InventoryResult CanStoreNewItem(Player player, uint itemId, uint count) => ArcaneCore.Game.Items.InventoryResult.Ok;

        public bool StoreNewItem(Player player, uint itemId, uint count) => false;

        public void DestroyItemCount(Player player, uint itemId, uint count)
        {
        }

        public ItemSale SellToVendor(Player player, ObjectGuid vendor, ObjectGuid item, byte count) => ItemSale.Ignored;

        public void SendEquipError(Player player, ArcaneCore.Game.Items.InventoryResult result, uint itemId)
        {
        }
    }

    [Fact]
    public void AutocompleteQuest_FailingTheClassCheck_IsNotRewardable()
    {
        // CanTakeQuest(skipStatusCheck) still applies the class gate (Player.cpp:12684-12698): packet editing cannot help.
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0, classes: 1u << ((byte)Class.Mage - 1)));
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
        Assert.Empty(kit.Session.Sent);
    }

    [Fact]
    public void AutocompleteQuest_FailingTheClassCheck_IsRewardableByAGameMaster()
    {
        // Player.cpp:12688-12692: a game master bypasses the CanTakeQuest gate for an autocomplete quest.
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0, classes: 1u << ((byte)Class.Mage - 1)));
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
        using QuestFlowTestKit kit = AutoKit(quest);
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
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0), o => o.RewardMode = QuestRewardMode.AllowlistOnly);
        Assert.False(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, Id, 0, out _));
    }

    [Fact]
    public void RequestReward_ForAnAutocompleteQuest_OffersTheReward_WithoutAnyJournalEntry()
    {
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0));
        kit.Services.RequestReward(kit.Player, kit.Creature.Guid, Id);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, Assert.Single(kit.Session.Sent).Opcode);
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(Id));
    }

    [Fact]
    public void CompleteQuestRequest_WithoutRequestText_GoesStraightToOfferReward()
    {
        QuestTemplate quest = QuestFlowTestKit.Task(Id, method: 0);
        var noText = new QuestTemplate { Entry = quest.Entry, Method = 0, QuestLevel = 1, Title = "Quiet" };
        using QuestFlowTestKit kit = AutoKit(noText);
        kit.Services.CompleteQuest(kit.Player, kit.Creature.Guid, Id);
        Assert.Equal(WorldOpcode.SmsgQuestgiverOfferReward, Assert.Single(kit.Session.Sent).Opcode);
    }

    [Fact]
    public void CompleteQuestRequest_ForADeliveryQuest_SendsRequestItemsWithTheCompletableFlag()
    {
        QuestTemplate quest = QuestFlowTestKit.Task(Id, method: 0, special: (byte)QuestSpecialFlags.Repeatable,
            reqItem: ItemTestData.ToughJerky, reqItemCount: 1);
        using QuestFlowTestKit kit = AutoKit(quest);
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
        using QuestFlowTestKit kit = AutoKit(QuestFlowTestKit.Task(Id, method: 0));
        Assert.Equal(QuestAdapter.Autocomplete, kit.Services.ProvidedAdapters & QuestAdapter.Autocomplete);
        Assert.True(kit.Services.Supported(kit.Services.Quests.Get(Id)!));
    }

    [Fact]
    public void CanFitQuestRewards_RespectsBagSpaceAndRequiredItemRemovals()
    {
        var quest = new QuestTemplate
        {
            Entry = Id,
            Method = 2,
            QuestLevel = 1,
            Title = "Reward fit",
            ReqItemId1 = ItemTestData.ToughJerky,
            ReqItemCount1 = 1,
            RewItemId1 = ItemTestData.WornShortsword,
            RewItemCount1 = 1,
            RewChoiceItemId1 = ItemTestData.WornWoodenShield,
            RewChoiceItemCount1 = 1,
        };
        using QuestFlowTestKit kit = AutoKit(quest);
        Quest definition = kit.Services.Quests.Get(Id)!;

        // Fill all 16 backpack slots with non-stacking swords so 0 free slots remain.
        for (int i = 0; i < 16; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.WornShortsword);
        }

        // With 0 free slots and no removable delivery item in the bags, 2 reward items cannot fit.
        Assert.False(QuestNpcServices.CanFitQuestRewards(kit.Player, definition, 0));

        // Even with 1 delivery item in the bags and 15 swords (0 free slots), 2 non-stacking rewards (RewItemId1 + RewChoiceItemId1) need 2 slots, while removal only frees 1.
        Assert.Equal(1u, kit.Player.Inventory.DestroyItemCount(ItemTestData.WornShortsword, 1));
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky);
        Assert.False(QuestNpcServices.CanFitQuestRewards(kit.Player, definition, 0));

        // A quest that grants only 1 reward item fits at 0 free slots when turning it in removes the 1 required delivery item.
        var singleRewardTemplate = new QuestTemplate
        {
            Entry = Id + 1,
            Method = 2,
            QuestLevel = 1,
            Title = "Single reward fit",
            ReqItemId1 = ItemTestData.ToughJerky,
            ReqItemCount1 = 1,
            RewItemId1 = ItemTestData.WornShortsword,
            RewItemCount1 = 1,
        };
        var singleQuest = new Quest(singleRewardTemplate);
        Assert.True(QuestNpcServices.CanFitQuestRewards(kit.Player, singleQuest, 0));
        HashSet<ObjectGuid> offered = [.. kit.Player.Inventory.AllItems.Where(i => i.Entry == ItemTestData.ToughJerky).Select(i => i.Guid)];
        Assert.False(QuestNpcServices.CanFitQuestRewards(kit.Player, singleQuest, 0, offered: offered));
    }
}
