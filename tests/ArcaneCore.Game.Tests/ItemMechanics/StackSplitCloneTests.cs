using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// vmangos Item::CloneItem (Item.cpp:1134-1146) calls SetItemRandomProperties, which writes the three property enchantment
/// slots (Item.cpp:816-833). SplitItem clones the moved portion, so a new stack must keep those enchantments.
/// </summary>
public sealed class StackSplitCloneTests
{
    [Fact]
    public void Split_NewStackKeepsRandomPropertyEnchantments_NotThePermanentEnchant()
    {
        (Player player, _) = CreatePlayer();
        PlayerInventory inv = player.Inventory;
        var bag = (Container)Give(inv, SmallBrownPouch);
        Assert.True(inv.AutoEquipItem(bag.BagSlot, bag.Slot));
        Item stack = Give(inv, ToughJerky, 10);
        inv.SwapItem(stack.BagSlot, stack.Slot, bag.Slot, 0);
        Assert.Same(bag, stack.Container);
        stack.SetInt32(UpdateFields.ItemFieldRandomPropertiesId, 1002);
        ItemEnchantments.Set(stack, EnchantSlots.Property0, 76, 0, 0);
        ItemEnchantments.Set(stack, EnchantSlots.Property0 + 1, 77, 0, 0);
        ItemEnchantments.Set(stack, EnchantSlots.Property0 + 2, 78, 0, 0);
        ItemEnchantments.Set(stack, EnchantSlots.Permanent, 999, 0, 0);

        inv.SplitItem(bag.Slot, 0, bag.Slot, 1, 4);

        Item part = inv.GetItem(bag.Slot, 1)!;
        Assert.NotSame(stack, part);
        Assert.Equal((6u, 4u), (stack.Count, part.Count));
        Assert.Equal(1002, part.RandomPropertyId);
        Assert.Equal((76u, 77u, 78u), (
            ItemEnchantments.Id(part, EnchantSlots.Property0),
            ItemEnchantments.Id(part, EnchantSlots.Property0 + 1),
            ItemEnchantments.Id(part, EnchantSlots.Property0 + 2)));
        Assert.Equal(0u, ItemEnchantments.Duration(part, EnchantSlots.Property0));
        Assert.Equal(0u, ItemEnchantments.Id(part, EnchantSlots.Permanent));
        Assert.Equal(999u, ItemEnchantments.Id(stack, EnchantSlots.Permanent));
        Assert.Equal((76u, 77u, 78u), (
            ItemEnchantments.Id(stack, EnchantSlots.Property0),
            ItemEnchantments.Id(stack, EnchantSlots.Property0 + 1),
            ItemEnchantments.Id(stack, EnchantSlots.Property0 + 2)));
    }
}
