using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>vmangos ItemHandler.cpp:92-106 (equip slot), 417-441 (read item), 911-986 (bank shortcuts).</summary>
public sealed class ItemMiscTests
{
    private const uint Letter = 93001; // PageText set

    private static readonly ItemTemplateStore Store = new(
        [.. Templates, new ItemTemplate { Entry = Letter, Class = 15, Name = "A Letter", DisplayId = 1, PageText = 7 }], []);

    private static (Player Player, FakeSession Session) Make()
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        return (player, session);
    }

    [Fact]
    public void AutoBank_NeedsBanker_ThenPlacesInFirstBankSlot_AndAutoStoreBankReturnsIt()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Item shirt = Give(inv, RecruitsShirt);

        inv.AutoBankItem(shirt.BagSlot, shirt.Slot);
        Assert.Equal([InventoryResult.TooFarAwayFromBank], EquipErrors(session));
        Assert.True(InventorySlots.IsInventoryPos(shirt.BagSlot, shirt.Slot));

        inv.CanUseBank = () => true;
        inv.AutoBankItem(shirt.BagSlot, shirt.Slot);
        Assert.Same(shirt, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));

        inv.AutoStoreBankItem(InventorySlots.Bag0, InventorySlots.BankItemStart);
        Assert.Null(inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
        Assert.Equal(1u, inv.GetItemCount(RecruitsShirt));

        inv.AutoStoreBankItem(shirt.BagSlot, shirt.Slot); // backpack item: auto-store sends it to the bank
        Assert.Equal(0u, inv.GetItemCount(RecruitsShirt));
        Assert.Equal(1u, inv.GetItemCount(RecruitsShirt, inBankAlso: true));
    }

    [Fact]
    public void AutoStoreBank_BankFull_ReportsInventoryFullFromBank()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        inv.CanUseBank = () => true;
        Item shirt = Give(inv, RecruitsShirt);
        inv.AutoBankItem(shirt.BagSlot, shirt.Slot);
        for (int i = 0; i < 16; i++)
        {
            Give(inv, RecruitsBoots);
        }

        session.Clear();
        inv.AutoStoreBankItem(InventorySlots.Bag0, InventorySlots.BankItemStart);
        Assert.Equal([InventoryResult.InventoryFull], EquipErrors(session));
        Assert.Same(shirt, inv.GetItem(InventorySlots.Bag0, InventorySlots.BankItemStart));
    }

    [Fact]
    public void AutoEquipItemSlot_SwapsIntoSlot_IgnoresNonEquipmentAndSamePosition()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Item ring = Give(inv, StrengthRing);

        inv.AutoEquipItemSlot(ring.Guid, InventorySlots.ItemStart); // not an equipment slot
        Assert.Same(ring, inv.GetItem(ring.BagSlot, ring.Slot));
        Assert.True(InventorySlots.IsInventoryPos(ring.BagSlot, ring.Slot));

        inv.AutoEquipItemSlot(ring.Guid, InventorySlots.Finger2);
        Assert.Same(ring, inv.GetItem(InventorySlots.Bag0, InventorySlots.Finger2));

        inv.AutoEquipItemSlot(ring.Guid, InventorySlots.Finger2); // already there: ignored, no error
        Assert.Empty(EquipErrors(session));
        inv.AutoEquipItemSlot(ObjectGuid.Item(424242), InventorySlots.Finger1); // unknown item
        Assert.Null(inv.GetItem(InventorySlots.Bag0, InventorySlots.Finger1));
    }

    [Fact]
    public void ReadItem_OnlyItemsWithPageText_OkCarriesGuidTwice()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Item letter = Give(inv, Letter);
        Item shirt = Give(inv, RecruitsShirt);
        session.Clear();

        Assert.True(inv.TryReadItem(letter.BagSlot, letter.Slot));
        (WorldOpcode opcode, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgReadItemOk, opcode);
        Assert.Equal(16, payload.Length);
        Assert.Equal(letter.Guid.Value, BitConverter.ToUInt64(payload, 0));
        Assert.Equal(letter.Guid.Value, BitConverter.ToUInt64(payload, 8));

        Assert.False(inv.TryReadItem(shirt.BagSlot, shirt.Slot));
        Assert.Equal([InventoryResult.ItemNotFound], EquipErrors(session));
    }

    [Fact]
    public void ReadItemFailed_Layout_GuidReasonGuid()
    {
        byte[] payload = ItemMiscPackets.ReadItemFailed(ObjectGuid.Item(5));
        Assert.Equal(17, payload.Length);
        Assert.Equal(0, payload[8]);
        Assert.Equal(BitConverter.ToUInt64(payload, 0), BitConverter.ToUInt64(payload, 9));
    }

    [Fact]
    public void NameQueryResponse_Layout_EntryThenCString()
    {
        byte[] payload = ItemMiscPackets.ItemNameQueryResponse(25, "Worn Shortsword");
        Assert.Equal(25u, BitConverter.ToUInt32(payload, 0));
        Assert.Equal("Worn Shortsword\0", System.Text.Encoding.UTF8.GetString(payload, 4, payload.Length - 4));
    }
}
