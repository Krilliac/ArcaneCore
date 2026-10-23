using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Npc.NpcServiceTestKit;
using ItemsResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Pins the inventory result codes the NPC item service hands to the vendor flows and the SMSG_INVENTORY_CHANGE_FAILURE
/// byte they end up as, independent of which enum type carries them (the numeric values are the wire values).
/// </summary>
public sealed class ItemServiceResultMappingTests
{
    private const uint Relic = 91101; // unique (max_count 1), buy 10

    private static readonly ItemTemplateStore RelicStore = new(
        [.. Templates, new ItemTemplate { Entry = Relic, Class = 15, Name = "Relic", DisplayId = 18, Quality = 1, BuyPrice = 10, SellPrice = 2, MaxCount = 1 }], []);

    private static NpcContent Vendor(params uint[] items)
        => NpcContent.Empty with { VendorItems = [.. items.Select(i => new VendorItem { Entry = NpcServiceTestKit.Entry, Item = i })] };

    [Fact]
    public void CanStoreNewItem_ReturnsTheWireValuesForOkAndInventoryFull()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor);
        IItemService items = kit.Items;
        Assert.Equal((byte)0, (byte)items.CanStoreNewItem(kit.Player, Lantern, 1));
        for (int i = 0; i < 16; i++)
        {
            kit.Give(Lantern);
        }

        Assert.Equal((byte)50, (byte)items.CanStoreNewItem(kit.Player, Lantern, 1));
    }

    [Fact]
    public void CanStoreNewItem_ForAUniqueItemAlreadyOwned_ReturnsCantCarryMoreOfThis()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor, itemStore: RelicStore);
        IItemService items = kit.Items;
        Assert.Equal((byte)0, (byte)items.CanStoreNewItem(kit.Player, Relic, 1));
        kit.Give(Relic);
        Assert.Equal((byte)17, (byte)items.CanStoreNewItem(kit.Player, Relic, 1));
    }

    [Fact]
    public void BuyItem_OfAUniqueItemAlreadyOwned_SendsCantCarryMoreOfThisAndKeepsTheMoney()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor, Vendor(Relic), itemStore: RelicStore);
        kit.Give(Relic);
        kit.Player.Money = 1000;
        kit.Session.Clear();
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, Relic, 1);
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Equal([ItemsResult.CantCarryMoreOfThis], ItemTestData.EquipErrors(kit.Session));
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(Relic, inBankAlso: true));
    }

    [Fact]
    public void RestoreBuyback_ReturnsItemNotFoundForAnEmptySlotAndOkWhenItRestores()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor);
        IItemService items = kit.Items;
        Assert.Equal((byte)23, (byte)items.RestoreBuyback(kit.Player, InventorySlots.BuybackStart));

        Item sword = kit.Give(Sword);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        Assert.Same(sword, kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
        Assert.Equal((byte)0, (byte)items.RestoreBuyback(kit.Player, InventorySlots.BuybackStart));
        Assert.Same(sword, kit.Player.Inventory.GetItemByGuid(sword.Guid));
        Assert.Null(kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
    }

    [Fact]
    public void BuybackItem_WithFullBags_SendsInventoryFullAndKeepsTheMoneyAndTheBuybackSlot()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor);
        Item sword = kit.Give(Sword);
        kit.Services.SellItem(kit.Player, kit.Npc.Guid, sword.Guid, 0);
        for (int i = 0; i < 16; i++)
        {
            kit.Give(Lantern);
        }

        kit.Player.Money = 1000;
        kit.Session.Clear();
        kit.Services.BuybackItem(kit.Player, kit.Npc.Guid, InventorySlots.BuybackStart);
        Assert.Equal(1000u, kit.Player.Money);
        Assert.Equal([ItemsResult.InventoryFull], ItemTestData.EquipErrors(kit.Session));
        Assert.Same(sword, kit.Player.Inventory.GetBuybackItem(InventorySlots.BuybackStart));
    }
}
