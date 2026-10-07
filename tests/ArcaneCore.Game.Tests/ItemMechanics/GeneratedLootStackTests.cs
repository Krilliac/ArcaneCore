using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Items holding generated loot: never a merge target (Item::CanBeMergedPartlyWith, Item.cpp:1192-1200) and never split
/// (Player::SplitItem, Player.cpp:10976-10981).
/// </summary>
public sealed class GeneratedLootStackTests
{
    private const uint StackedClam = 94105; // a lootable item that stacks

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = StackedClam, Class = 15, Name = "Test Stacked Clam", DisplayId = 1, Stackable = 20, Flags = 0x04 },
        ], []);

    private static (Player Player, FakeSession Session) Make(uint zone = 2557)
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        player.ZoneId = zone;
        return (player, session);
    }

    private static readonly ItemLootData PartLooted = new(0, [new ItemLootEntry(0, ToughJerky, 1, false)]);

    [Fact]
    public void StackHoldingGeneratedLoot_CannotBeSplit()
    {
        (Player player, FakeSession session) = Make();
        PlayerInventory inv = player.Inventory;
        Item clams = Give(inv, StackedClam, 5);
        clams.Loot = PartLooted;
        byte free = (byte)(clams.Slot + 1);
        session.Clear();

        inv.SplitItem(clams.BagSlot, clams.Slot, InventorySlots.Bag0, free, 2);

        Assert.Equal([InventoryResult.CouldntSplitItems], EquipErrors(session));
        Assert.Null(inv.GetItem(InventorySlots.Bag0, free));
        Assert.Equal(5u, clams.Count);
    }

    [Fact]
    public void StackHoldingGeneratedLoot_IsNotAMergeTarget()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        Item looted = Give(inv, StackedClam, 3);
        looted.Loot = PartLooted;
        byte lootedSlot = looted.Slot;

        // A new stack never fills the looted one (vmangos _CanStoreItem merge pass).
        Assert.Equal(InventoryResult.Ok, inv.AddItem(StackedClam, 1, out Item? added));
        Assert.NotSame(looted, added);
        Assert.Equal(3u, looted.Count);

        // Dropping a plain stack on it does not merge either: the two stacks change places.
        Item plain = added!;
        byte plainSlot = plain.Slot;
        inv.SwapItem(plain.BagSlot, plain.Slot, InventorySlots.Bag0, lootedSlot);

        Assert.Equal(3u, looted.Count);
        Assert.Equal(1u, plain.Count);
        Assert.Same(plain, inv.GetItem(InventorySlots.Bag0, lootedSlot));
        Assert.Same(looted, inv.GetItem(InventorySlots.Bag0, plainSlot));
        Assert.Equal(4u, inv.GetItemCount(StackedClam));
    }
}
