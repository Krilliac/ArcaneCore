using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;
using ItemsResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>Vendors: buy, sell, buyback, limited stock and repair (vmangos ItemHandler.cpp / Player.cpp).</summary>
public sealed class NpcVendorServiceTests
{
    private static NpcContent Vendor(params VendorItem[] items) => NpcContent.Empty with { VendorItems = items };

    private static VendorItem Row(uint item, uint maxCount = 0, uint incrTime = 0, uint slot = 0)
        => new() { Entry = NpcServiceKit.Entry, Item = item, MaxCount = maxCount, IncrTime = incrTime, Slot = slot };

    [Fact]
    public void ListInventory_SendsTheVisibleRowsWithPricesAndStock()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread, slot: 1), Row(Sword, maxCount: 2, incrTime: 60, slot: 2)));
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        var r = new PacketReader(kit.Single(WorldOpcode.SmsgListInventory));
        Assert.Equal(kit.Npc.Guid.Value, r.ReadUInt64());
        Assert.Equal(2, r.ReadByte());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(Bread, r.ReadUInt32());
        Assert.Equal(11u, r.ReadUInt32());
        Assert.Equal(0xFFFFFFFFu, r.ReadUInt32());
        Assert.Equal(25u, r.ReadUInt32());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(5u, r.ReadUInt32());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(Sword, r.ReadUInt32());
        Assert.Equal(12u, r.ReadUInt32());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(1000u, r.ReadUInt32());
        Assert.Equal(50u, r.ReadUInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void BuyItem_ChargesStoresTheBuyCountAndAnswersWithTheClientSlot()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)));
        kit.Player.Money = 100;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Bread, 2);
        Assert.Equal(50u, kit.Player.Money);
        Assert.Equal(10u, kit.Player.Inventory.GetItemCount(Bread));
        var r = new PacketReader(kit.Drain().Single(p => p.Opcode == WorldOpcode.SmsgBuyItem).Payload);
        Assert.Equal(kit.Npc.Guid.Value, r.ReadUInt64());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(0xFFFFFFFFu, r.ReadUInt32());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.True(kit.Sink.CharacterChanges > 0);
    }

    [Fact]
    public void BuyItem_WithoutEnoughMoney_FailsAndChangesNothing()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Sword)));
        kit.Player.Money = 999;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Sword, 1);
        var r = new PacketReader(kit.Single(WorldOpcode.SmsgBuyFailed));
        Assert.Equal(kit.Npc.Guid.Value, r.ReadUInt64());
        Assert.Equal(Sword, r.ReadUInt32());
        Assert.Equal((byte)BuyResult.NotEnoughMoney, r.ReadByte());
        Assert.Equal(999u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Sword));
    }

    [Fact]
    public void BuyItem_OutOfRange_HostileDeadOrUnlisted_IsRefused()
    {
        using (var far = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)), npcDistance: 10))
        {
            far.Player.Money = 1000;
            far.Services.BuyItem(far.Player, far.Npc.Guid, Bread, 1);
            Assert.Equal((byte)BuyResult.DistanceTooFar, far.Single(WorldOpcode.SmsgBuyFailed)[12]);
            Assert.Equal(1000u, far.Player.Money);
        }

        using (var hostile = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)), hostile: true))
        {
            hostile.Player.Money = 1000;
            hostile.Services.BuyItem(hostile.Player, hostile.Npc.Guid, Bread, 1);
            Assert.Equal(1000u, hostile.Player.Money);
            Assert.False(hostile.Sent(WorldOpcode.SmsgBuyItem));
        }

        using (var dead = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread))))
        {
            dead.Player.Money = 1000;
            dead.Player.Health = 0;
            dead.Player.Combat.DeathState = Combat.DeathState.Dead;
            dead.Services.BuyItem(dead.Player, dead.Npc.Guid, Bread, 1);
            dead.Services.ListInventory(dead.Player, dead.Npc.Guid);
            Assert.Empty(dead.Drain());
            Assert.Equal(1000u, dead.Player.Money);
        }

        using var unlisted = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)));
        unlisted.Player.Money = 1000;
        unlisted.Services.BuyItem(unlisted.Player, unlisted.Npc.Guid, Lantern, 1);
        Assert.Equal((byte)BuyResult.CantFindItem, unlisted.Single(WorldOpcode.SmsgBuyFailed)[12]);
        unlisted.Services.BuyItem(unlisted.Player, unlisted.Npc.Guid, 4242, 1);
        Assert.Equal((byte)BuyResult.CantFindItem, unlisted.Single(WorldOpcode.SmsgBuyFailed)[12]);
    }

    [Fact]
    public void BuyItem_WithFullBags_SendsTheEquipErrorAndKeepsTheMoney()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Lantern)));
        for (int i = 0; i < 16; i++)
        {
            kit.Give(Lantern);
        }

        kit.Player.Money = 1000;
        kit.Session.Clear();
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Equal([ItemsResult.InventoryFull], ItemTestData.EquipErrors(kit.Session));
    }

    [Fact]
    public void LimitedStock_SellsOutThenRestocksByBuyCountAfterTheTimer()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Lantern, maxCount: 2, incrTime: 60)));
        kit.Player.Money = 10_000;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Equal(0u, new PacketReader(kit.Drain().Last(p => p.Opcode == WorldOpcode.SmsgBuyItem).Payload[12..]).ReadUInt32());

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Equal((byte)BuyResult.ItemAlreadySold, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);

        kit.Now += 59;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Equal((byte)BuyResult.ItemAlreadySold, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);

        kit.Now += 1; // one increment: one more lantern
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] list = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(1u, BitConverter.ToUInt32(list, 9 + 12));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.True(kit.Sent(WorldOpcode.SmsgBuyItem) || kit.Player.Inventory.GetItemCount(Lantern) == 3);
        Assert.Equal(3u, kit.Player.Inventory.GetItemCount(Lantern));

        kit.Now += 3600; // fully restocked, capped at max count
        kit.Drain();
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        Assert.Equal(2u, BitConverter.ToUInt32(kit.Single(WorldOpcode.SmsgListInventory), 9 + 12));
    }

    [Fact]
    public void SellItem_PaysTheSellPriceAndKeepsTheItemInTheFirstBuybackSlot()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        kit.Session.Clear();
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Equal(250u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Sword));
        Assert.Same(sword, kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
        Assert.Equal(250u, kit.Player.GetUInt32(UpdateFields.PlayerFieldBuybackPrice1));
        Assert.Equal(PlayerInventory.BuybackLifetimeSeconds, kit.Player.GetUInt32(UpdateFields.PlayerFieldBuybackTimestamp1));
        Assert.Equal(sword.Guid.Value, kit.Player.GetUInt64(UpdateFields.PlayerFieldInvSlotHead + (InventorySlots.BuybackStart * 2)));
        Assert.Null(kit.Player.Inventory.GetItemByGuid(sword.Guid));
        Assert.DoesNotContain(kit.Player.Inventory.CreateSnapshot().Items, row => row.Item.Guid == sword.Guid.Low);
        Assert.False(kit.Sent(WorldOpcode.SmsgDestroyObject));
    }

    [Fact]
    public void SellItem_SpentExpendableCharges_ScaleThePrice()
    {
        // vmangos HandleSellItemOpcode (ItemHandler.cpp:84-96): negative template charges make the price relative to
        // the charges left; the buyback slot keeps the scaled price.
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item wand = kit.Give(Wand);
        wand.SetInt32(UpdateFields.ItemFieldSpellCharges, -4);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, wand.Guid, 0);
        Assert.Equal(80u, kit.Player.Money);
        Assert.Equal(80u, kit.Player.Inventory.GetBuybackPrice(InventorySlots.BuybackStart));
    }

    [Fact]
    public void SellItem_LostDurability_SubtractsTheUndiscountedRepairCost()
    {
        // ItemHandler.cpp:98-138: uint32(lost × DurabilityCosts multiplier × DurabilityQuality factor) comes off the price.
        using var kit = new NpcServiceKit(NpcFlags.Vendor, repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 40; // 10 lost × 3 × 1.0 = 30
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Equal(220u, kit.Player.Money);
        Assert.Equal(220u, kit.Player.Inventory.GetBuybackPrice(InventorySlots.BuybackStart));
    }

    [Fact]
    public void SellItem_RepairCostAboveThePrice_SellsForOneCopper()
    {
        uint[] multipliers = new uint[RepairCostTable.MultiplierCount];
        multipliers[7] = 100;
        using var kit = new NpcServiceKit(NpcFlags.Vendor, repair: new RepairCostTable([(10u, multipliers)], [(6u, 1.0f)]));
        Item sword = kit.Give(Sword);
        sword.Durability = 40; // 10 lost × 100 = 1000 > 250: "starter items can cost more to repair than vendorprice"
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Equal(1u, kit.Player.Money);
    }

    [Fact]
    public void SellItem_DamagedItemWithoutARepairCostRow_IsRefused()
    {
        // ItemHandler.cpp:106-121: no DurabilityCosts/DurabilityQuality row for a damaged item answers SELL_ERR_CANT_SELL_ITEM.
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        sword.Durability = 49;
        kit.Session.Clear();
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Equal((byte)SellResult.CantSellItem, kit.Single(WorldOpcode.SmsgSellItem)[16]);
        Assert.Equal(0u, kit.Player.Money);
        Assert.Same(sword, kit.Player.Inventory.GetItemByGuid(sword.Guid));
    }

    [Fact]
    public void SellItem_NearTheMoneyCap_BuybackCostsWhatWasActuallyPaid()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        kit.Player.Money = QuestNpcServices.MaxMoneyAmount - 10;
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Equal(QuestNpcServices.MaxMoneyAmount, kit.Player.Money);
        Assert.Equal(10u, kit.Player.Inventory.GetBuybackPrice(InventorySlots.BuybackStart));

        kit.Player.Money = 100;
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        Assert.Equal(90u, kit.Player.Money);
        Assert.Same(sword, kit.Player.Inventory.GetItemByGuid(sword.Guid));
    }

    [Fact]
    public void SellItem_PartOfAStack_SplitsIt()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item bread = kit.Give(Bread, 10);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, bread.Guid, 4);
        Assert.Equal(24u, kit.Player.Money);
        Assert.Equal(6u, bread.Count);
        Item sold = kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart)!;
        Assert.NotEqual(bread.Guid, sold.Guid);
        Assert.Equal(4u, sold.Count);
        Assert.Equal(24u, kit.Player.Inventory.GetBuybackPrice(InventorySlots.BuybackStart));
    }

    [Fact]
    public void SellItem_Refusals_AnswerWithTheSellError()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item junk = kit.Give(Junk, 2);
        Item bread = kit.Give(Bread, 3);
        kit.Session.Clear();

        kit.Services.SellItem(kit.Player, kit.Npc.Guid, junk.Guid, 0);
        Assert.Equal((byte)SellResult.CantSellItem, kit.Single(WorldOpcode.SmsgSellItem)[16]);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, bread.Guid, 4);
        Assert.Equal((byte)SellResult.CantSellItem, kit.Single(WorldOpcode.SmsgSellItem)[16]);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, ObjectGuid.Item(999_999), 1);
        Assert.Equal((byte)SellResult.CantFindItem, kit.Single(WorldOpcode.SmsgSellItem)[16]);

        kit.Npc = kit.Npc with { X = kit.Player.X + 20 };
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, bread.Guid, 1);
        byte[] far = kit.Single(WorldOpcode.SmsgSellItem);
        Assert.Equal(0ul, BitConverter.ToUInt64(far, 0));
        Assert.Equal((byte)SellResult.CantFindVendor, far[16]);
        Assert.Equal(0u, kit.Player.Money);
        Assert.Equal(3u, bread.Count);
    }

    [Fact]
    public void BuybackItem_ChargesTheSlotPriceAndReturnsTheSameItem()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        kit.Player.Money = 300;
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        Assert.Equal(50u, kit.Player.Money);
        Assert.Same(sword, kit.Player.Inventory.GetItemByGuid(sword.Guid));
        Assert.Null(kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
        Assert.Equal(0u, kit.Player.GetUInt32(UpdateFields.PlayerFieldBuybackPrice1));
        Assert.Contains(kit.Player.Inventory.CreateSnapshot().Items, row => row.Item.Guid == sword.Guid.Low);
    }

    [Fact]
    public void BuybackItem_WithoutMoneyOrItemOrRange_IsRefused()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        kit.Player.Money = 249;
        kit.Session.Clear();
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        byte[] poor = kit.Single(WorldOpcode.SmsgBuyFailed);
        Assert.Equal(Sword, BitConverter.ToUInt32(poor, 8));
        Assert.Equal((byte)BuyResult.NotEnoughMoney, poor[12]);
        Assert.Same(sword, kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));

        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart + 1);
        Assert.Equal((byte)BuyResult.CantFindItem, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, 5); // not a buyback slot
        Assert.Equal((byte)BuyResult.CantFindItem, kit.Single(WorldOpcode.SmsgBuyFailed)[12]);

        kit.Npc = kit.Npc with { X = kit.Player.X + 20 };
        kit.Player.Money = 1000;
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        Assert.Equal((byte)SellResult.CantFindVendor, kit.Single(WorldOpcode.SmsgSellItem)[16]);
        Assert.Equal(1000u, kit.Player.Money);
    }

    [Fact]
    public void Buyback_WhenAllTwelveSlotsAreFull_ReplacesTheOldest()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor);
        var sold = new List<Item>();
        for (int i = 0; i < 12; i++)
        {
            kit.Now += 10;
            Item lantern = kit.Give(Lantern);
            kit.Services.SellItem(kit.Player, kit.Npc.Guid, lantern.Guid, 0);
            sold.Add(lantern);
        }

        Assert.All(Enumerable.Range(0, 12), i => Assert.Same(sold[i], kit.Player.Inventory.GetBuybackItem((byte)(InventorySlots.BuybackStart + i))));
        kit.Session.Clear();
        kit.Now += 10;
        Item thirteenth = kit.Give(Lantern);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, thirteenth.Guid, 0);
        Assert.Same(thirteenth, kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
        Assert.True(kit.Sent(WorldOpcode.SmsgDestroyObject)); // the oldest item is deleted
        Assert.Equal(13 * 75u, kit.Player.Money);
    }

    private static RepairCostTable Repair()
    {
        uint[] multipliers = new uint[RepairCostTable.MultiplierCount];
        multipliers[7] = 3;       // weapon subclass 7 (one-handed swords)
        multipliers[1 + 21] = 2;  // armor subclass 1 (cloth)
        return new RepairCostTable([(10u, multipliers)], [(6u, 1.0f), (4u, 0.5f)]);
    }

    [Fact]
    public void RepairItem_One_ChargesLostTimesMultiplierTimesQuality()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor | NpcFlags.Repair, repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 40; // 10 lost × 3 × quality (2+1)×2=6 → 1.0
        kit.Player.Money = 100;
        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, sword.Guid);
        Assert.Equal(50u, sword.Durability);
        Assert.Equal(70u, kit.Player.Money);
    }

    [Fact]
    public void LimitedStock_IsIndependentForTwoSpawnsOfTheSameVendorEntry()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Lantern, maxCount: 1, incrTime: 60)));
        kit.Player.Money = 1000;
        ObjectGuid first = kit.Npc.Guid;
        kit.Services.BuyItem(kit.Player, first, Lantern, 1);
        kit.Drain();

        kit.Npc = kit.Npc with { Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 78), SpawnId = 78 };
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        Assert.Equal(1u, BitConverter.ToUInt32(kit.Single(WorldOpcode.SmsgListInventory), 9 + 12));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);
        Assert.Equal(2u, kit.Player.Inventory.GetItemCount(Lantern));
    }

    [Fact]
    public void RepairItem_DiscountRoundsHalfCopperUp()
    {
        using var kit = new NpcServiceKit(NpcFlags.Repair,
            extra: new QuestNpcDependencies(Reputation: new FixedReputation(0.75f)), repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 40; // base cost 30; discounted cost 22.5 rounds to 23
        kit.Player.Money = 23;

        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, sword.Guid);

        Assert.Equal(50u, sword.Durability);
        Assert.Equal(0u, kit.Player.Money);
    }

    [Fact]
    public void RepairItem_All_RepairsWhatIsAffordableInOrder()
    {
        using var kit = new NpcServiceKit(NpcFlags.Repair, repair: Repair());
        Item helm = kit.Give(Helm);
        kit.Player.Inventory.AutoEquipItem(InventorySlots.Bag0, helm.Slot);
        Assert.Same(helm, kit.Player.Inventory.GetItem(InventorySlots.Bag0, 0));
        helm.Durability = 0;            // 40 × 2 × 0.5 = 40
        Item sword = kit.Give(Sword);
        sword.Durability = 0;           // 50 × 3 × 1.0 = 150
        kit.Player.Money = 100;
        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, ObjectGuid.Empty);
        Assert.Equal(40u, helm.Durability);
        Assert.Equal(0u, sword.Durability);
        Assert.Equal(60u, kit.Player.Money);
    }

    [Fact]
    public void RepairItem_WithoutPricesFlagOrRange_RepairsNothing()
    {
        using (var noPrices = new NpcServiceKit(NpcFlags.Repair))
        {
            Item sword = noPrices.Give(Sword);
            sword.Durability = 1;
            noPrices.Player.Money = 1000;
            noPrices.Services.RepairItem(noPrices.Player, noPrices.Npc.Guid, ObjectGuid.Empty);
            Assert.Equal(1u, sword.Durability);
            Assert.Equal(1000u, noPrices.Player.Money);
        }

        using (var notArmorer = new NpcServiceKit(NpcFlags.Vendor, repair: Repair()))
        {
            Item sword = notArmorer.Give(Sword);
            sword.Durability = 1;
            notArmorer.Player.Money = 1000;
            notArmorer.Services.RepairItem(notArmorer.Player, notArmorer.Npc.Guid, sword.Guid);
            Assert.Equal(1u, sword.Durability);
        }

        using var far = new NpcServiceKit(NpcFlags.Repair, repair: Repair(), npcDistance: 12);
        Item farSword = far.Give(Sword);
        farSword.Durability = 1;
        far.Player.Money = 1000;
        far.Services.RepairItem(far.Player, far.Npc.Guid, farSword.Guid);
        Assert.Equal(1u, farSword.Durability);
        Assert.Equal(1000u, far.Player.Money);
    }

    [Fact]
    public void RepairItem_One_SnapshotContainsChargedMoneyAndRepairedDurability()
    {
        using var kit = new NpcServiceKit(NpcFlags.Repair, repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 40;
        kit.Player.Money = 100;
        Assert.Equal(40u, Assert.Single(kit.Player.CreateSnapshot(0).Inventory!.Items).Item.Durability);
        List<CharacterState> snapshots = CaptureRepairSnapshots(kit);

        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, sword.Guid);

        CharacterState saved = Assert.Single(snapshots);
        Assert.Equal(70u, saved.Money);
        Assert.Equal(50u, Assert.Single(saved.Inventory!.Items).Item.Durability);
    }

    [Theory]
    [InlineData(1000u, 810u, 50u)]
    [InlineData(100u, 60u, 0u)]
    public void RepairItem_All_SnapshotIncludesEveryAffordableRepair(uint money, uint expectedMoney, uint swordDurability)
    {
        using var kit = new NpcServiceKit(NpcFlags.Repair, repair: Repair());
        Item helm = kit.Give(Helm);
        kit.Player.Inventory.AutoEquipItem(InventorySlots.Bag0, helm.Slot);
        helm.Durability = 0;
        Item sword = kit.Give(Sword);
        sword.Durability = 0;
        kit.Player.Money = money;
        Assert.Equal(2, kit.Player.CreateSnapshot(0).Inventory!.Items.Count);
        List<CharacterState> snapshots = CaptureRepairSnapshots(kit);

        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, ObjectGuid.Empty);

        CharacterState saved = Assert.Single(snapshots);
        Assert.Equal(expectedMoney, saved.Money);
        Assert.Equal(40u, Assert.Single(saved.Inventory!.Items, row => row.Item.Guid == helm.Guid.Low).Item.Durability);
        Assert.Equal(swordDurability, Assert.Single(saved.Inventory!.Items, row => row.Item.Guid == sword.Guid.Low).Item.Durability);
        Assert.Equal(expectedMoney, kit.Player.Money);
        Assert.Equal(swordDurability, sword.Durability);
    }

    [Fact]
    public void RepairItem_Unaffordable_EmitsNoSnapshot()
    {
        using var kit = new NpcServiceKit(NpcFlags.Repair, repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 40;
        kit.Player.Money = 29;
        List<CharacterState> snapshots = CaptureRepairSnapshots(kit);

        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, sword.Guid);

        Assert.Empty(snapshots);
        Assert.Equal(29u, kit.Player.Money);
        Assert.Equal(40u, sword.Durability);
    }

    private static List<CharacterState> CaptureRepairSnapshots(NpcServiceKit kit)
    {
        var snapshots = new List<CharacterState>();
        kit.Sink.OnCharacterChanged = player => snapshots.Add(player.CreateSnapshot(kit.World.NowMs));
        return snapshots;
    }

    // vmangos Player::BuyItemFromVendor (Player.cpp:18442-18445): uint32(price * GetReputationPriceDiscount + 0.5f), i.e. round half up in
    // single precision, NOT floor. Bread costs 25: 25 x 0.9 = 22.5 -> 23, 25 x 0.95 = 23.75 -> 24, 25 x 0.85 = 21.25 -> 21.
    [Theory]
    [InlineData(1.0f, 25u)]
    [InlineData(0.9f, 23u)]
    [InlineData(0.95f, 24u)]
    [InlineData(0.85f, 21u)]
    public void Discounts_RoundHalfUpAsVmangosDoes(float discount, uint expectedPrice)
    {
        var reputation = new FixedReputation(discount);
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)), new QuestNpcDependencies(Reputation: reputation));
        kit.Player.Money = 100;
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Bread, 1);
        Assert.Equal(100u - expectedPrice, kit.Player.Money);
    }

    [Fact]
    public void ListInventory_AtHonored_ShowsTheHalfUpRoundedPrice()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(Row(Bread)), new QuestNpcDependencies(Reputation: new FixedReputation(0.9f)));
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        var r = new PacketReader(kit.Single(WorldOpcode.SmsgListInventory));
        r.ReadUInt64();
        r.ReadByte();
        r.ReadBytes(4 + 4 + 4 + 4); // slot, item, display, max count
        Assert.Equal(23u, r.ReadUInt32()); // ItemHandler.cpp:763: uint32(25 × 0.9f + 0.5f)
    }

    [Fact]
    public void RepairItem_AtHonored_RoundsHalfUp()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor | NpcFlags.Repair, null, new QuestNpcDependencies(Reputation: new FixedReputation(0.9f)), repair: Repair());
        Item sword = kit.Give(Sword);
        sword.Durability = 43; // 7 lost × 3 × 1.0 = 21; 21 × 0.9f = 18.9 -> 19 (Player.cpp:4955), the old floor charged 18
        kit.Player.Money = 100;
        kit.Services.RepairItem(kit.Player, kit.Npc.Guid, sword.Guid);
        Assert.Equal(81u, kit.Player.Money);
    }
    internal sealed class FixedReputation(float discount, byte rank = 4) : IPlayerReputation
    {
        public int GetReputation(Player player, uint factionId) => 0;

        public byte GetReputationRank(Player player, uint factionId) => rank;

        public float GetPriceDiscount(Player player, NpcInfo npc) => discount;
    }
}
