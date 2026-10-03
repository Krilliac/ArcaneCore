using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;
using ItemsResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Quest acceptance refusals, source items and abandonment (vmangos QuestHandler.cpp
/// HandleQuestgiverAcceptQuestOpcode, Player.cpp CanTakeQuest / CanAddQuest /
/// CanGiveQuestSourceItemIfNeed / RemoveQuestAtSlot / TakeOrReplaceQuestStartItems).
/// </summary>
public sealed class QuestRefusalTests
{
    private static uint InvalidReason(QuestFlowKit kit)
    {
        var drained = kit.Drain();
        (WorldOpcode Opcode, byte[] Payload) invalid = Assert.Single(drained, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid);
        Assert.Equal(4, invalid.Payload.Length);
        Assert.Equal(WorldOpcode.SmsgGossipComplete, drained[^1].Opcode);   // CloseGossip follows every refusal
        return BitConverter.ToUInt32(invalid.Payload);
    }

    private static void AssertNoInvalid(QuestFlowKit kit)
    {
        var drained = kit.Drain();
        Assert.DoesNotContain(drained, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid);
        Assert.Equal(WorldOpcode.SmsgGossipComplete, drained[^1].Opcode);
    }

    [Fact]
    public void InvalidAndFailedPackets_AreByteExact()
    {
        // gtker/wow_messages smsg_questgiver_quest_invalid (u32) and smsg_questgiver_questfailed (u32 quest, u32 reason).
        Assert.Equal([13, 0, 0, 0], QuestPackets.QuestInvalid(QuestInvalidReason.AlreadyOn).ToArray());
        Assert.Equal([0x2A, 0x23, 0, 0, 4, 0, 0, 0], QuestPackets.QuestFailed(9002, QuestInvalidReason.InventoryFull).ToArray());
    }

    [Fact]
    public void ReasonNumbers_AreTheVanillaOnes()
    {
        // vmangos QuestDef.h:47-57 / wow_messages QuestFailedReason (1.12).
        Assert.Equal(0u, (uint)QuestInvalidReason.DontHaveReq);
        Assert.Equal(1u, (uint)QuestInvalidReason.LowLevel);
        Assert.Equal(4u, (uint)QuestInvalidReason.InventoryFull);
        Assert.Equal(6u, (uint)QuestInvalidReason.WrongRace);
        Assert.Equal(12u, (uint)QuestInvalidReason.OnlyOneTimed);
        Assert.Equal(13u, (uint)QuestInvalidReason.AlreadyOn);
        Assert.Equal(17u, (uint)QuestInvalidReason.DuplicateItem);
        Assert.Equal(20u, (uint)QuestInvalidReason.MissingItems);
        Assert.Equal(22u, (uint)QuestInvalidReason.NotEnoughMoney);
    }

    [Fact]
    public void AcceptingAQuestYouAreOn_SendsAlreadyOn_AndLeavesTheJournalAlone()
    {
        using var kit = new QuestFlowKit([Task(10)], [Row(10, QuestStatus.Incomplete)], starters: [10]);
        Assert.False(kit.Accept(10));
        Assert.Equal((uint)QuestInvalidReason.AlreadyOn, InvalidReason(kit));
        Assert.Empty(kit.Sink.Rows);
    }

    [Fact]
    public void AcceptingARewardedQuest_IsAlsoAlreadyOn()
    {
        // vmangos keeps a rewarded quest at COMPLETE, and SatisfyQuestStatus refuses any status but NONE (Player.cpp:13532-13545).
        using var kit = new QuestFlowKit([Task(10)], [Row(10, QuestStatus.Complete, rewarded: true)], starters: [10]);
        Assert.False(kit.Accept(10));
        Assert.Equal((uint)QuestInvalidReason.AlreadyOn, InvalidReason(kit));
    }

    [Fact]
    public void AWrongRaceQuest_SendsWrongRace()
    {
        using var kit = new QuestFlowKit([Task(10, races: 2)], starters: [10]);   // orc only
        Assert.False(kit.Accept(10));
        Assert.Equal((uint)QuestInvalidReason.WrongRace, InvalidReason(kit));
    }

    [Theory]
    [InlineData("level")]
    [InlineData("class")]
    [InlineData("previous")]
    [InlineData("exclusive")]
    public void OtherRequirementFailures_SendTheDefaultReason(string kind)
    {
        // Level, class, quest line and exclusive group all answer INVALIDREASON_DONT_HAVE_REQ (Player.cpp:13319-13330, 13473-13489, 13345-13435, 13560-13592).
        QuestTemplate[] quests = kind switch
        {
            "level" => [Task(10, minLevel: 20)],
            "class" => [Task(10, classes: 1 << 7)],
            "previous" => [Task(10, previous: 11), Task(11)],
            _ => [Task(10, exclusiveGroup: 5), Task(11, exclusiveGroup: 5)],
        };
        CharacterQuestStatus[] rows = kind == "exclusive" ? [Row(11, QuestStatus.Incomplete)] : [];
        using var kit = new QuestFlowKit(quests, rows, starters: [10]);
        Assert.False(kit.Accept(10));
        Assert.Equal((uint)QuestInvalidReason.DontHaveReq, InvalidReason(kit));
    }

    [Fact]
    public void ASecondTimedQuest_SendsOnlyOneTimed()
    {
        using var kit = new QuestFlowKit([Task(10, limitTime: 600, special: (byte)QuestSpecialFlags.Timed), Task(11, limitTime: 600, special: (byte)QuestSpecialFlags.Timed)],
            [Row(11, QuestStatus.Incomplete, timer: 100 + 300)], starters: [10]);
        Assert.False(kit.Accept(10));
        Assert.Equal((uint)QuestInvalidReason.OnlyOneTimed, InvalidReason(kit));
    }

    [Fact]
    public void TheFirstFailingCheckDecidesTheReason_InVmangosOrder()
    {
        // CanTakeQuest order (Player.cpp:12570-12575): status, exclusive group, class, race, level ... so a quest
        // that is wrong-race and too-high-level answers WRONG_RACE, and one already taken answers ALREADY_ON.
        using (var kit = new QuestFlowKit([Task(10, minLevel: 20, races: 2)], starters: [10]))
        {
            kit.Accept(10);
            Assert.Equal((uint)QuestInvalidReason.WrongRace, InvalidReason(kit));
        }

        using (var kit = new QuestFlowKit([Task(10, races: 2)], [Row(10, QuestStatus.Incomplete)], starters: [10]))
        {
            kit.Accept(10);
            Assert.Equal((uint)QuestInvalidReason.AlreadyOn, InvalidReason(kit));
        }

        // class (DONT_HAVE_REQ) comes before race (WRONG_RACE)
        using (var kit = new QuestFlowKit([Task(10, races: 2, classes: 1 << 7)], starters: [10]))
        {
            kit.Accept(10);
            Assert.Equal((uint)QuestInvalidReason.DontHaveReq, InvalidReason(kit));
        }
    }

    [Fact]
    public void SilentRefusals_SendNoInvalidPacket()
    {
        // Player.cpp:12567-12568 (MaxLevel) and :12576 (IsActive) return false without a message.
        using (var kit = new QuestFlowKit([Task(10, maxLevel: 3)], starters: [10], level: 5))
        {
            Assert.False(kit.Accept(10));
            AssertNoInvalid(kit);
        }

        using (var kit = new QuestFlowKit([Task(10, method: 2 | QuestConstants.MethodDisabled)], starters: [10]))
        {
            Assert.False(kit.Accept(10));
            AssertNoInvalid(kit);
        }
    }

    [Fact]
    public void AFullQuestLog_SendsQuestlogFull_NotInvalid()
    {
        QuestTemplate[] quests = [.. Enumerable.Range(0, QuestConstants.MaxQuestLogSize + 1).Select(i => Task(100 + (uint)i))];
        CharacterQuestStatus[] rows = [.. quests.Take(QuestConstants.MaxQuestLogSize).Select(q => Row(q.Entry, QuestStatus.Incomplete))];
        using var kit = new QuestFlowKit(quests, rows, starters: [100 + (uint)QuestConstants.MaxQuestLogSize]);
        Assert.False(kit.Accept(100 + (uint)QuestConstants.MaxQuestLogSize));
        var sent = kit.Drain();
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgQuestlogFull);
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid);
    }

    [Fact]
    public void AnAvailableQuest_StillAccepts_WithoutAnyRefusalPacket()
    {
        using var kit = new QuestFlowKit([Task(10)], starters: [10]);
        Assert.True(kit.Accept(10));
        Assert.DoesNotContain(kit.Drain(), p => p.Opcode is WorldOpcode.SmsgQuestgiverQuestInvalid or WorldOpcode.SmsgQuestgiverQuestFailed);
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(10));
    }

    // ---- source item (CanGiveQuestSourceItemIfNeed, Player.cpp:13643-13676) ----------------------

    [Fact]
    public void ASourceItemTheBankAlreadyHolds_IsNotGivenAgain()
    {
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.ToughJerky, srcCount: 3)], starters: [10]);
        kit.Player.Inventory.Load([new InventoryItemData(0, InventorySlots.BankItemStart,
            new ItemInstanceData { Guid = 100, Entry = ItemTestData.ToughJerky, Count = 3 })]);
        kit.Player.Inventory.GuidAllocator!.Seed(100);
        kit.Drain();

        Assert.True(kit.Accept(10));

        Assert.Equal(3u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky, inBankAlso: true));   // not 6
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void APartlyOwnedSourceItem_IsToppedUpToTheCount()
    {
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.ToughJerky, srcCount: 3)], starters: [10]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 1);
        kit.Drain();

        Assert.True(kit.Accept(10));

        Assert.Equal(3u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void AFullBackpack_RefusesWithQuestFailedInventoryFull()
    {
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.ToughJerky, srcCount: 3)], starters: [10]);
        for (int i = 0; i < InventorySlots.ItemEnd - InventorySlots.ItemStart; i++)
        {
            ItemTestData.Give(kit.Player.Inventory, ItemTestData.IndestructibleRock);
        }

        kit.Drain();
        Assert.False(kit.Accept(10));
        var sent = kit.Drain();
        (WorldOpcode Opcode, byte[] Payload) failed = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestFailed);
        Assert.Equal([10, 0, 0, 0, 4, 0, 0, 0], failed.Payload);
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(10));
        Assert.Empty(kit.Sink.Rows);
    }

    [Fact]
    public void AUniqueSourceItemWeAlreadyCarry_RefusesWithQuestFailedDuplicate()
    {
        // has 1 of a unique item, needs 2: the missing 1 cannot be carried (EQUIP_ERR_CANT_CARRY_MORE_OF_THIS -> 17).
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.UniqueKey, srcCount: 2)], starters: [10]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.UniqueKey, 1);
        kit.Drain();

        Assert.False(kit.Accept(10));
        (WorldOpcode Opcode, byte[] Payload) failed = Assert.Single(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestFailed);
        Assert.Equal([10, 0, 0, 0, 17, 0, 0, 0], failed.Payload);
    }

    // ---- abandoning (RemoveQuestAtSlot, Player.cpp:13033-13066) ---------------------------------

    [Fact]
    public void AbandoningDestroysQuestBoundRequiredItems_BankIncluded()
    {
        // Destroying them on abandon was added with 1.12.1 (Player.cpp:13046-13062).
        using var kit = new QuestFlowKit([Task(10, reqItem: ItemTestData.QuestPelt, reqItemCount: 4)],
            [Row(10, QuestStatus.Incomplete)], starters: [10]);
        kit.Player.Inventory.Load([new InventoryItemData(0, InventorySlots.BankItemStart,
            new ItemInstanceData { Guid = 100, Entry = ItemTestData.QuestPelt, Count = 3 })]);
        kit.Player.Inventory.GuidAllocator!.Seed(100);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.QuestPelt, 2);
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestPelt, inBankAlso: true));
        Assert.Equal(2u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestPelt));

        int slot = kit.State.Quests.FindSlot(10);
        Assert.True(slot < QuestConstants.MaxQuestLogSize);
        Assert.True(kit.Services.AbandonQuest(kit.Player, (byte)slot));

        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestPelt, inBankAlso: true));
        Assert.Equal(QuestStatus.None, kit.State.Quests.GetStatus(10));
    }

    [Fact]
    public void AbandoningKeepsOrdinaryRequiredItems()
    {
        using var kit = new QuestFlowKit([Task(10, reqItem: ItemTestData.ToughJerky, reqItemCount: 4)],
            [Row(10, QuestStatus.Incomplete)], starters: [10]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 4);

        Assert.True(kit.Services.AbandonQuest(kit.Player, (byte)kit.State.Quests.FindSlot(10)));

        Assert.Equal(4u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void AbandoningDestroysTheSourceItemCount_EvenWhenTheQuestAlsoRequiresIt()
    {
        // TakeOrReplaceQuestStartItems (Player.cpp:13696-13760): destroys SrcItemCount when owned (bank included),
        // with no "also required" exception.
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.ToughJerky, srcCount: 2, reqItem: ItemTestData.ToughJerky, reqItemCount: 5)],
            [Row(10, QuestStatus.Incomplete)], starters: [10]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 6);

        Assert.True(kit.Services.AbandonQuest(kit.Player, (byte)kit.State.Quests.FindSlot(10)));

        Assert.Equal(4u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void AbandoningLeavesAStartItemThatIsTheQuestsOwnStartItem()
    {
        // giveQuestStartItem && questId == item.StartQuest: "Just leave it" (Player.cpp:13716-13721).
        using var kit = new QuestFlowKit([Task(ItemTestData.QuestNoteQuest, srcItem: ItemTestData.QuestNote, srcCount: 1)],
            [Row(ItemTestData.QuestNoteQuest, QuestStatus.Incomplete)], starters: [ItemTestData.QuestNoteQuest]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.QuestNote, 1);

        Assert.True(kit.Services.AbandonQuest(kit.Player, (byte)kit.State.Quests.FindSlot(ItemTestData.QuestNoteQuest)));

        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestNote));
    }

    [Fact]
    public void Abandoning_ReplacesTheSourceItemWithTheQuestsStartingItem()
    {
        // Two items start quest 7020; GetQuestStartingItemID returns the one that loaded first (lowest entry,
        // ObjectMgr.cpp:4224-4225) and TakeOrReplaceQuestStartItems hands that back (Player.cpp:13744-13748).
        Assert.Equal(ItemTestData.QuestLetter, ItemTestData.Store.QuestStartingItem(ItemTestData.QuestLetterQuest));
        Assert.Equal(0u, ItemTestData.Store.QuestStartingItem(99999));

        using var kit = new QuestFlowKit([Task(ItemTestData.QuestLetterQuest, srcItem: ItemTestData.ToughJerky, srcCount: 2)],
            [Row(ItemTestData.QuestLetterQuest, QuestStatus.Incomplete)], starters: [ItemTestData.QuestLetterQuest]);
        ItemTestData.Give(kit.Player.Inventory, ItemTestData.ToughJerky, 2);

        Assert.True(kit.Services.AbandonQuest(kit.Player, (byte)kit.State.Quests.FindSlot(ItemTestData.QuestLetterQuest)));

        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(2u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestLetter));
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(ItemTestData.QuestLetterCopy));
    }

    [Fact]
    public void AbandoningIsRefusedWhileTheSourceItemCannotBeUnequipped()
    {
        // CanUnequipItems (Player.cpp:8326-8397): a worn copy that cannot come off in combat blocks the abandon.
        using var kit = new QuestFlowKit([Task(10, srcItem: ItemTestData.RecruitsShirt, srcCount: 1)],
            [Row(10, QuestStatus.Incomplete)], starters: [10]);
        Item shirt = ItemTestData.Give(kit.Player.Inventory, ItemTestData.RecruitsShirt);
        kit.Player.Inventory.AutoEquipItem(shirt.BagSlot, shirt.Slot);
        Assert.Contains(kit.Player.Inventory.Equipped, e => e.Item.Entry == ItemTestData.RecruitsShirt);
        kit.Player.UnitFlags |= UnitFlags.InCombat;
        kit.Drain();

        Assert.False(kit.Services.AbandonQuest(kit.Player, (byte)kit.State.Quests.FindSlot(10)));

        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(10));
        Assert.Contains(kit.Player.Inventory.Equipped, e => e.Item.Entry == ItemTestData.RecruitsShirt);
        Assert.Equal([ItemsResult.NotInCombat], ItemTestData.EquipErrors(kit.Session));
    }

    // ---- cancel and swap ---------------------------------------------------------------------

    [Fact]
    public void QuestgiverCancel_ClosesTheGossipWindow()
    {
        using var kit = new QuestFlowKit([Task(10)], starters: [10]);
        kit.Services.QuestgiverCancel(kit.Player);
        Assert.Equal(WorldOpcode.SmsgGossipComplete, Assert.Single(kit.Drain()).Opcode);
    }

    [Fact]
    public void SwappingQuestSlots_ExchangesAllThreeFields_AndIgnoresBadRequests()
    {
        using var kit = new QuestFlowKit([Task(10), Task(11)], [Row(10, QuestStatus.Incomplete), Row(11, QuestStatus.Incomplete)], starters: [10]);
        uint first = kit.State.Quests.SlotQuestId(0);
        uint second = kit.State.Quests.SlotQuestId(1);
        Assert.NotEqual(first, second);
        Assert.NotEqual(0u, first);
        Assert.NotEqual(0u, second);

        kit.Services.SwapQuestSlots(kit.Player, 0, 0);                                       // same slot
        kit.Services.SwapQuestSlots(kit.Player, 0, (byte)QuestConstants.MaxQuestLogSize);    // out of range
        kit.Services.SwapQuestSlots(kit.Player, 200, 1);
        Assert.Equal(first, kit.State.Quests.SlotQuestId(0));

        kit.State.Quests.SetSlotTimer(0, 77);
        kit.Services.SwapQuestSlots(kit.Player, 0, 1);

        Assert.Equal(second, kit.State.Quests.SlotQuestId(0));
        Assert.Equal(first, kit.State.Quests.SlotQuestId(1));
        Assert.Equal(77u, kit.Player.GetUInt32(UpdateFields.PlayerQuestLog11 + QuestConstants.FieldsPerSlot + 2));   // the timer travelled with the quest
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(first));
    }
}
