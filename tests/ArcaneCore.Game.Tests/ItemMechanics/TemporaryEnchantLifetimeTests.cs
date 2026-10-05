using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class TemporaryEnchantLifetimeTests
{
    private static Item Enchanted(uint duration, uint charges = 2)
    {
        var item = new Item(7001, new ItemTemplate
        {
            Entry = 7002, Class = 2, Name = "Lifetime Test Weapon", DisplayId = 1,
            InventoryType = 13, Delay = 2000,
        }, ObjectGuid.Player(1));
        int offset = UpdateFields.ItemFieldEnchantment + 3;
        item.SetUInt32(offset, 9001);
        item.SetUInt32(offset + 1, duration);
        item.SetUInt32(offset + 2, charges);
        return item;
    }

    [Fact]
    public void Update_DecrementsByMilliseconds_AndFlushPersistsRemaining()
    {
        Item item = Enchanted(5000);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);

        lifetime.Update(1200);
        lifetime.FlushToItems();

        Assert.Equal(3800u, item.EnchantmentDuration(1));
        Assert.Equal(9001u, item.EnchantmentId(1));
        Assert.Equal(2u, item.EnchantmentCharges(1));
    }

    [Fact]
    public void Update_WhenSlotIsReplaced_AdoptsNewIdAndDuration_WithoutOldRestore()
    {
        Item item = Enchanted(5000);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);
        lifetime.Update(1000);
        int offset = UpdateFields.ItemFieldEnchantment + 3;
        item.SetUInt32(offset, 9002);
        item.SetUInt32(offset + 1, 9000);
        item.SetUInt32(offset + 2, 1);

        lifetime.Update(1000);
        lifetime.FlushToItems();

        Assert.Equal(9002u, item.EnchantmentId(1));
        Assert.Equal(9000u, item.EnchantmentDuration(1));
        Assert.Equal(1u, item.EnchantmentCharges(1));
    }

    [Fact]
    public void FlushToItems_SameIdRefresh_AdoptsNewDurationBeforeSave()
    {
        Item item = Enchanted(5000);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);
        lifetime.Update(1000);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 4, 9000);

        lifetime.FlushToItems();

        Assert.Equal(9000u, item.EnchantmentDuration(1));
    }

    [Fact]
    public void FlushToItems_ChargeClear_DropsTheStaleTrackedSlot()
    {
        Item item = Enchanted(5000);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 3, 0);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 4, 0);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 5, 0);

        lifetime.FlushToItems();

        Assert.Equal(0, lifetime.Count);
        Assert.Equal((0u, 0u, 0u), (item.EnchantmentId(1), item.EnchantmentDuration(1), item.EnchantmentCharges(1)));
    }

    [Fact]
    public void Reconcile_DiscoversTransferredOrNewTimedItems_WithoutResettingExistingEntry()
    {
        Item existing = Enchanted(5000);
        Item acquired = Enchanted(2000);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Reconcile([existing]);
        lifetime.Update(500);

        lifetime.Reconcile([existing, acquired]);

        Assert.Equal(2, lifetime.Count);
        Assert.Equal(4500u, existing.EnchantmentDuration(1));
        Assert.Equal(2000u, acquired.EnchantmentDuration(1));
    }

    [Fact]
    public void Update_ExactOrOverrunExpiry_RemovesAppliedEffectThenClearsTriple()
    {
        Item item = Enchanted(1000);
        var removals = new List<(Item Item, int Slot)>();
        var lifetime = new TemporaryEnchantLifetime((expired, slot) => removals.Add((expired, slot)));
        lifetime.Register(item, 1);

        lifetime.Update(1001);

        Assert.Equal((item, 1), Assert.Single(removals));
        Assert.Equal(0u, item.EnchantmentId(1));
        Assert.Equal(0u, item.EnchantmentDuration(1));
        Assert.Equal(0u, item.EnchantmentCharges(1));
        Assert.Equal(0, lifetime.Count);
    }

    [Fact]
    public void ZeroDuration_IsUntimed_AndPreservesCharges()
    {
        Item item = Enchanted(0, charges: 0);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);
        lifetime.Update(uint.MaxValue);

        Assert.Equal(9001u, item.EnchantmentId(1));
        Assert.Equal(0u, item.EnchantmentDuration(1));
        Assert.Equal(0u, item.EnchantmentCharges(1));
        Assert.Equal(0, lifetime.Count);
    }

    [Fact]
    public void Unregister_PreservesCurrentRemainingDuration()
    {
        Item item = Enchanted(2500);
        var lifetime = new TemporaryEnchantLifetime();
        lifetime.Register(item, 1);
        lifetime.Update(500);
        lifetime.Unregister(item, 1);

        Assert.Equal(2000u, item.EnchantmentDuration(1));
        Assert.Equal(0, lifetime.Count);
    }

    [Fact]
    public void InventoryLoad_RegistersRestoredDuration_AndSnapshotFlushesCurrentValue()
    {
        (Player player, _) = CreatePlayer();
        player.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData
        {
            Guid = 7003,
            Entry = WornShortsword,
            Durability = 20,
            Charges = [0, 0, 0, 0, 0],
            Enchantments = [0, 0, 0, 9001, 5000, 2, .. new uint[15]],
        })]);

        Assert.Equal(1, player.Inventory.TrackedEnchantDurationCount);
        player.Inventory.UpdateEnchantDurations(1200);
        ItemInstanceData saved = Assert.Single(player.Inventory.CreateSnapshot().Items).Item;

        Assert.Equal(3800u, saved.Enchantments[4]);
        Assert.Equal(9001u, saved.Enchantments[3]);
        Assert.Equal(2u, saved.Enchantments[5]);
    }
}
