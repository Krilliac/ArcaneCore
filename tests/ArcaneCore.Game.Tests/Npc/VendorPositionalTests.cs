using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;
using ItemsResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// CMSG_BUY_ITEM_IN_SLOT: the item goes to the bag and slot the client named
/// (vmangos HandleBuyItemInSlotOpcode, ItemHandler.cpp:659-686, and the placement half of
/// Player::BuyItemFromVendor, Player.cpp:18453-18496).
/// </summary>
public sealed class VendorPositionalTests
{
    private static NpcContent Vendor(params uint[] items)
        => NpcContent.Empty with
        {
            VendorItems = items.Select((item, slot) => new VendorItem { Entry = NpcServiceKit.Entry, Item = item, Slot = (uint)slot }).ToArray(),
        };

    private static NpcServiceKit Kit(params uint[] items)
    {
        var kit = new NpcServiceKit(NpcFlags.Vendor, Vendor(items));
        kit.Player.Money = 10_000;
        kit.Session.Clear();
        return kit;
    }

    private static void BuyAt(NpcServiceKit kit, uint item, ObjectGuid bag, byte slot, byte count = 1)
        => kit.Services.BuyItemInSlot(kit.Player, kit.Npc.Guid, item, bag, slot, count);

    [Fact]
    public void IntoABackpackSlot_PutsTheItemExactlyThere()
    {
        using NpcServiceKit kit = Kit(Lantern);
        byte slot = InventorySlots.ItemStart + 7;
        BuyAt(kit, Lantern, kit.Player.Guid, slot);

        Item item = Assert.IsType<Item>(kit.Player.Inventory.GetItem(InventorySlots.Bag0, slot));
        Assert.Equal(Lantern, item.Entry);
        Assert.Null(kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));   // not "the first free slot"
        Assert.Equal(10_000u - 300, kit.Player.Money);
        Assert.True(kit.Sent(WorldOpcode.SmsgBuyItem));
        Assert.True(kit.Sent(WorldOpcode.SmsgItemPushResult));
    }

    [Fact]
    public void IntoASlotOfAWornBag_PutsTheItemInThatBag()
    {
        using NpcServiceKit kit = Kit(Lantern);
        Item pouch = kit.Give(Pouch);
        kit.Player.Inventory.AutoEquipItem(pouch.BagSlot, pouch.Slot);
        Assert.Same(pouch, kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.BagStart));
        kit.Session.Clear();

        BuyAt(kit, Lantern, pouch.Guid, 3);

        Assert.Equal(Lantern, kit.Player.Inventory.GetItem(InventorySlots.BagStart, 3)?.Entry);
        Assert.Null(kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart));
        Assert.Equal(10_000u - 300, kit.Player.Money);
    }

    [Fact]
    public void AnUnknownBagGuid_IsIgnoredAsACheat_WithoutAnyReply()
    {
        // ItemHandler.cpp:681-683: bag not found, return before BuyItemFromVendor (no error packet either).
        using NpcServiceKit kit = Kit(Lantern);
        BuyAt(kit, Lantern, ObjectGuid.WithEntry(HighGuid.Item, 0, 4242), InventorySlots.ItemStart);

        Assert.Empty(kit.Drain());
        Assert.Equal(10_000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Lantern));
    }

    [Fact]
    public void IntoAnOccupiedSlot_AnUnmergeableItemIsRefused_AndNothingIsCharged()
    {
        // _CanStoreItem_InSpecificSlot: a non-empty slot must merge (Item::CanBeMergedPartlyWith: other entry or
        // full stack is EQUIP_ERR_ITEM_CANT_STACK, Item.cpp:1187-1195).
        using NpcServiceKit kit = Kit(Lantern);
        Item blocker = kit.Give(Junk);
        BuyAt(kit, Lantern, kit.Player.Guid, blocker.Slot);

        Assert.Equal([ItemsResult.ItemCantStack], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(10_000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Lantern));
    }

    [Fact]
    public void IntoASlotHoldingTheSameStackableItem_MergesIntoIt()
    {
        using NpcServiceKit kit = Kit(Bread);
        Item stack = kit.Give(Bread, 5);
        BuyAt(kit, Bread, kit.Player.Guid, stack.Slot);   // BuyCount 5 per purchase

        Assert.Equal(10u, stack.Count);
        Assert.Equal(10u, kit.Player.Inventory.GetItemCount(Bread));
        Assert.Equal(10_000u - 25, kit.Player.Money);
    }

    [Fact]
    public void IntoAnEquipmentSlot_WearsTheItem()
    {
        // Player.cpp:18469-18491: IsEquipmentPos, one item, CanEquipNewItem, EquipNewItem.
        using NpcServiceKit kit = Kit(Helm);
        BuyAt(kit, Helm, kit.Player.Guid, InventorySlots.Head);

        Assert.Equal(Helm, kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Head)?.Entry);
        Assert.Equal(10_000u - 400, kit.Player.Money);
        Assert.True(kit.Sent(WorldOpcode.SmsgBuyItem));
    }

    [Fact]
    public void IntoAnEquipmentSlot_MoreThanOneIsNotEquippable()
    {
        // Player.cpp:18471-18475.
        using NpcServiceKit kit = Kit(Helm);
        BuyAt(kit, Helm, kit.Player.Guid, InventorySlots.Head, count: 2);

        Assert.Equal([ItemsResult.ItemCantBeEquipped], ItemTestData.EquipErrors(kit.Session));
        Assert.Null(kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Head));
        Assert.Equal(10_000u, kit.Player.Money);
    }

    [Fact]
    public void IntoTheWrongEquipmentSlot_ItCannotBeEquipped()
    {
        using NpcServiceKit kit = Kit(Helm);
        BuyAt(kit, Helm, kit.Player.Guid, InventorySlots.Chest);

        Assert.Equal([ItemsResult.ItemCantBeEquipped], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(10_000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Helm));
    }

    [Fact]
    public void IntoAWornEquipmentSlot_IsRefusedWithoutSwapping()
    {
        using NpcServiceKit kit = Kit(Helm);
        BuyAt(kit, Helm, kit.Player.Guid, InventorySlots.Head);
        kit.Session.Clear();
        BuyAt(kit, Helm, kit.Player.Guid, InventorySlots.Head);

        // FindEquipSlot with a named, occupied slot and no swap finds nothing (Player.cpp:8290-8299, 8322-8323),
        // which CanEquipItem reports as ITEM_CANT_BE_EQUIPPED.
        Assert.Equal([ItemsResult.ItemCantBeEquipped], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(10_000u - 400, kit.Player.Money);
    }

    [Fact]
    public void IntoAPositionThatIsNeitherInventoryNorEquipment_DoesNotGoToThatSlot()
    {
        // Player.cpp:18492-18496: bank and buyback slots are not valid vendor destinations.
        using NpcServiceKit kit = Kit(Lantern);
        BuyAt(kit, Lantern, kit.Player.Guid, InventorySlots.BankItemStart);
        BuyAt(kit, Lantern, kit.Player.Guid, InventorySlots.BuybackStart);

        Assert.Equal([ItemsResult.ItemDoesntGoToSlot, ItemsResult.ItemDoesntGoToSlot], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(10_000u, kit.Player.Money);
        Assert.Equal(0u, kit.Player.Inventory.GetItemCount(Lantern));
    }

    [Fact]
    public void TheVendorChecksStillRunFirst()
    {
        // BuyItemFromVendor checks the vendor list and the money before it looks at the position.
        using NpcServiceKit poor = Kit(Lantern);
        poor.Player.Money = 10;
        BuyAt(poor, Lantern, poor.Player.Guid, InventorySlots.ItemStart + 3);
        Assert.Equal((byte)BuyResult.NotEnoughMoney, poor.Single(WorldOpcode.SmsgBuyFailed)[12]);

        using NpcServiceKit unlisted = Kit(Bread);
        BuyAt(unlisted, Lantern, unlisted.Player.Guid, InventorySlots.ItemStart + 3);
        Assert.Equal((byte)BuyResult.CantFindItem, unlisted.Single(WorldOpcode.SmsgBuyFailed)[12]);
    }

    [Fact]
    public void ThePlainBuyOpcodeStillPlacesAnywhere()
    {
        using NpcServiceKit kit = Kit(Lantern);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Lantern, 1);

        Assert.Equal(Lantern, kit.Player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)?.Entry);
    }
}
