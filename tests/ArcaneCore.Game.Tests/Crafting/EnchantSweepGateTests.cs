using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// World-thread audit lane 5: the enchantment timer sweep walks every held item times seven slots, so it runs only when the inventory layout version moved,
/// when a timed enchantment was written through <see cref="ItemEnchantments.Set"/>, or at the slow staggered backstop.
/// </summary>
public sealed class EnchantSweepGateTests
{
    private const uint Sword = 9101;
    private const uint FakeEnchantId = 4001;

    private static CraftingTestKit NewKit(out PlayerEnchantments enchantments)
    {
        var kit = new CraftingTestKit(
            [],
            [new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Gate Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = 50, Quality = 2 }]);
        enchantments = new PlayerEnchantments(kit.Player, EnchantCatalog.Empty, kit.System);
        kit.Player.AttachEnchantments(enchantments);
        return kit;
    }

    /// <summary>Writes a timed temporary enchantment straight into the item fields: no hook sees it, only the layout version or the backstop can.</summary>
    private static void WriteRaw(Item item, uint durationMs)
    {
        int field = UpdateFields.ItemFieldEnchantment + (EnchantSlots.Temporary * EnchantSlots.FieldsPerSlot);
        item.SetUInt32(field + EnchantSlots.IdOffset, FakeEnchantId);
        item.SetUInt32(field + EnchantSlots.DurationOffset, durationMs);
    }

    [Fact]
    public void UnchangedBags_RunNoFullSweepBetweenBackstops()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        kit.Give(Sword);

        enchantments.Update(1000);
        long runAfterFirst = enchantments.SweepsRun;
        Assert.Equal(1, runAfterFirst);

        for (int i = 0; i < 20; i++)
        {
            enchantments.Update(1000);
        }

        Assert.Equal(runAfterFirst, enchantments.SweepsRun);
        Assert.Equal(20, enchantments.SweepsSkipped);
    }

    [Fact]
    public void AnItemMovedWithinTheBags_StartsItsTimerWithinOneSweep()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        Item sword = kit.Give(Sword);
        enchantments.Update(1000);
        WriteRaw(sword, 60_000);
        enchantments.Update(1000);
        Assert.Null(enchantments.LeftMs(sword, EnchantSlots.Temporary));   // nothing announced the raw write: the gate skipped

        kit.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, (byte)(InventorySlots.ItemStart + 7));
        enchantments.Update(1000);

        Assert.Equal(60_000u, enchantments.LeftMs(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void AnItemStoredAfterTheLastSweep_StartsItsTimerWithinOneSweep()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        enchantments.Update(1000);
        long run = enchantments.SweepsRun;

        Item sword = kit.Give(Sword);
        WriteRaw(sword, 45_000);
        enchantments.Update(1000);

        Assert.Equal(run + 1, enchantments.SweepsRun);
        Assert.Equal(45_000u, enchantments.LeftMs(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void AnItemDestroyed_DropsItsTimerAtTheNextSweep()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        Item sword = kit.Give(Sword);
        WriteRaw(sword, 60_000);
        enchantments.Update(1000);
        Assert.NotNull(enchantments.LeftMs(sword, EnchantSlots.Temporary));

        kit.Inventory.DestroyItem(sword.BagSlot, sword.Slot);
        enchantments.Update(1000);

        Assert.Equal(0, enchantments.TrackedCount);
    }

    [Fact]
    public void ATimedEnchantmentWrittenThroughSet_IsFoundWithoutAnInventoryChange()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        Item sword = kit.Give(Sword);
        enchantments.Update(1000);

        ItemEnchantments.Set(sword, EnchantSlots.Temporary, FakeEnchantId, duration: 30_000, charges: 0);
        enchantments.Update(1000);

        Assert.Equal(30_000u, enchantments.LeftMs(sword, EnchantSlots.Temporary));
    }

    [Fact]
    public void TheBackstop_CatchesARawWrite_WithinItsStaggeredInterval()
    {
        using CraftingTestKit kit = NewKit(out PlayerEnchantments enchantments);
        Item sword = kit.Give(Sword);
        enchantments.Update(1000);
        WriteRaw(sword, 600_000);

        enchantments.Update(1000);
        Assert.Null(enchantments.LeftMs(sword, EnchantSlots.Temporary));

        // 30 s plus a per-character offset below 30 s: 60 s always covers it.
        for (int i = 0; i < 60; i++)
        {
            enchantments.Update(1000);
        }

        Assert.NotNull(enchantments.LeftMs(sword, EnchantSlots.Temporary));
    }
}
