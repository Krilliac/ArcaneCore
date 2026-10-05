using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using Xunit;

namespace ArcaneCore.Game.Tests;

public sealed class ItemInventoryWeaponRemovalTests
{
    [Fact]
    public void RemovingCommittedWeaponResetsExtraAttacks_ButArmorAndBackpackRemovalDoNot()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        Item weapon = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        player.Inventory.AutoEquipItem(weapon.BagSlot, weapon.Slot);
        Assert.True(player.Combat.QueueExtraAttacks(2));
        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Equal(0u, player.Combat.ExtraAttacks);

        Item offhand = ItemTestData.Give(player.Inventory, ItemTestData.WornWoodenShield);
        player.Inventory.AutoEquipItem(offhand.BagSlot, offhand.Slot);
        Assert.True(player.Combat.QueueExtraAttacks(2));
        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.OffHand);
        Assert.Equal(0u, player.Combat.ExtraAttacks);

        Item armor = ItemTestData.Give(player.Inventory, ItemTestData.RecruitsPants);
        Assert.True(player.Combat.QueueExtraAttacks(2));
        player.Inventory.RemoveItem(armor.BagSlot, armor.Slot);
        Assert.Equal(2u, player.Combat.ExtraAttacks);
    }
}
