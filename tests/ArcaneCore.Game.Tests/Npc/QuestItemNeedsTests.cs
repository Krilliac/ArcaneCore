using Xunit;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>vmangos Player::HasQuestForItem (Player.cpp:14267-14320): which quest items a player still wants.</summary>
public sealed class QuestItemNeedsTests
{
    private const uint Delivery = 30;
    private const uint Sourced = 31;
    private const uint Pelt = ItemTestData.QuestPelt;
    private const uint Jerky = ItemTestData.ToughJerky;   // stack 20
    private const uint Key = ItemTestData.UniqueKey;      // max count 1

    private static QuestFlowKit Kit(QuestTemplate quest, params CharacterQuestStatus[] rows)
        => new([quest], rows, starters: [quest.Entry], enders: [quest.Entry]);

    [Fact]
    public void ARequiredItem_IsWantedWhileTheQuestCounterIsBelowTheRequirement()
    {
        using QuestFlowKit kit = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3), Row(Delivery, QuestStatus.Incomplete, items: 2));
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: false));
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));   // not required by any quest

        using QuestFlowKit full = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3), Row(Delivery, QuestStatus.Incomplete, items: 3));
        Assert.False(full.Services.HasQuestForItem(full.Player, Pelt, inRaidGroup: false));
    }

    [Fact]
    public void TheCounterDecides_NotWhatTheBagsHold()
    {
        // The player carries three pelts but the quest's own counter still says two: vmangos asks the counter (m_itemcount).
        using QuestFlowKit kit = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3), Row(Delivery, QuestStatus.Incomplete, items: 2));
        ItemTestData.Give(kit.Player.Inventory, Pelt, 3);
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: false));
    }

    [Fact]
    public void OnlyAnIncompleteQuestInTheLogWantsItems()
    {
        using QuestFlowKit complete = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3), Row(Delivery, QuestStatus.Complete, items: 1));
        Assert.False(complete.Services.HasQuestForItem(complete.Player, Pelt, inRaidGroup: false));

        using QuestFlowKit none = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3));
        Assert.False(none.Services.HasQuestForItem(none.Player, Pelt, inRaidGroup: false));
    }

    [Fact]
    public void ARaidGroupMemberDoesNotWantItemsOfAQuestThatIsNotAllowedInRaids()
    {
        QuestTemplate ordinary = Task(Delivery, reqItem: Pelt, reqItemCount: 3);
        using QuestFlowKit kit = Kit(ordinary, Row(Delivery, QuestStatus.Incomplete));
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: false));
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: true));

        // QUEST_TYPE_RAID (62) and the RAID flag 0x40 are allowed (QuestDef.cpp:227-233).
        using QuestFlowKit raidType = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3, type: 62), Row(Delivery, QuestStatus.Incomplete));
        Assert.True(raidType.Services.HasQuestForItem(raidType.Player, Pelt, inRaidGroup: true));
        using QuestFlowKit raidFlag = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3, flags: 0x40), Row(Delivery, QuestStatus.Incomplete));
        Assert.True(raidFlag.Services.HasQuestForItem(raidFlag.Player, Pelt, inRaidGroup: true));
    }

    [Fact]
    public void QuestsIgnoreRaid_LetsEveryQuestWantItemsInARaid()
    {
        var quest = Task(Delivery, reqItem: Pelt, reqItemCount: 3);
        using var kit = new QuestFlowKit([quest], [Row(Delivery, QuestStatus.Incomplete)], starters: [Delivery], configure: o => o.IgnoreRaid = true);
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: true));
        Assert.True(kit.Services.IsAllowedInRaid(kit.Services.Quests.Get(Delivery)!));
    }

    [Fact]
    public void ASourceItemOfAUniqueItem_IsWantedUntilTheMaxCountIsHeld()
    {
        // pProto->MaxCount && GetItemCount(item, bank) < MaxCount (Player.cpp:14303-14305).
        using QuestFlowKit kit = Kit(Task(Sourced, reqSource: Key), Row(Sourced, QuestStatus.Incomplete));
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Key, inRaidGroup: false));
        ItemTestData.Give(kit.Player.Inventory, Key, 1);
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Key, inRaidGroup: false));
    }

    [Fact]
    public void ASourceItemWithACustomCount_IsWantedUntilThatMany_BankIncluded()
    {
        using QuestFlowKit kit = Kit(Task(Sourced, reqSource: Jerky, reqSourceCount: 3), Row(Sourced, QuestStatus.Incomplete));
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));
        ItemTestData.Give(kit.Player.Inventory, Jerky, 2);
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));
        kit.Player.Inventory.Load([new InventoryItemData(0, InventorySlots.BankItemStart, new ItemInstanceData { Guid = 100, Entry = Jerky, Count = 3 })]);
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));   // three, in the bank, count
    }

    [Fact]
    public void ASourceItemWithoutACount_IsWantedUntilOneFullStackIsHeld()
    {
        // ReqSourceCount 0 -> GetItemCount < pProto->Stackable (Player.cpp:14315-14317); Tough Jerky stacks to 20.
        using QuestFlowKit kit = Kit(Task(Sourced, reqSource: Jerky), Row(Sourced, QuestStatus.Incomplete));
        ItemTestData.Give(kit.Player.Inventory, Jerky, 19);
        Assert.True(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));
        ItemTestData.Give(kit.Player.Inventory, Jerky, 1);
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Jerky, inRaidGroup: false));
    }

    [Fact]
    public void WithoutALoadedJournal_NothingIsWanted()
    {
        using QuestFlowKit kit = Kit(Task(Delivery, reqItem: Pelt, reqItemCount: 3), Row(Delivery, QuestStatus.Incomplete));
        kit.Services.Untrack(kit.Player);
        Assert.False(kit.Services.HasQuestForItem(kit.Player, Pelt, inRaidGroup: false));
    }
}
