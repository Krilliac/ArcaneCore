using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ItemEquipSpellTests
{
    [Fact]
    public void EquipAndRemove_ApplyAndRemoveTheActualEquipAura()
    {
        const uint spellId = 49201;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)));
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49202, Class = 4, InventoryType = 12,
            Stackable = 1, Spells = [new ItemSpell(spellId, 1, 0, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        player.Inventory.EquipSpellSink = new Sink(kit.System);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49202, 1, out Item? item));
        player.Inventory.EquipItem(InventorySlots.MainHand, item!);
        Assert.True(kit.System.HasAura(player, spellId));
        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.False(kit.System.HasAura(player, spellId));
    }

    [Fact]
    public void DuplicateItemsAndBrokenRepair_KeepItemOwnedAurasIndependent()
    {
        const uint spellId = 49203;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)));
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49204, Class = 4, InventoryType = 12,
            Stackable = 1, MaxDurability = 10, Spells = [new ItemSpell(spellId, 1, 0, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        player.Inventory.EquipSpellSink = new Sink(kit.System);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49204, 1, out Item? first));
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(49204, 1, out Item? second));
        player.Inventory.EquipItem(InventorySlots.Trinket1, first!);
        player.Inventory.EquipItem(InventorySlots.Trinket2, second!);
        Assert.Equal(2, kit.System.GetAuras(player).Count(h => h.ItemGuid != ObjectGuid.Empty));
        player.Inventory.DurabilityPointsLoss(first!, 10);
        Assert.Single(kit.System.GetAuras(player), h => h.ItemGuid == second!.Guid);
        player.Inventory.RepairDurability(first!);
        Assert.Equal(2, kit.System.GetAuras(player).Count(h => h.ItemGuid != ObjectGuid.Empty));
    }

    private sealed class Sink(SpellSystem spells) : IItemEquipSpellSink
    {
        public void OnItemEquipped(Player player, Item item, byte slot, bool apply) => spells.ApplyItemEquipSpell(player, item, slot, apply);
    }

    [Fact]
    public void OrdinaryItemUseBuffsFromDifferentItemsKeepTheirReplacementBehavior()
    {
        const uint spellId = 49205;
        const uint entry = 49206;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)) with
            { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (Player player, _) = kit.AddPlayer(1);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
            { Entry = entry, Class = 0, Stackable = 1, Spells = [new ItemSpell(spellId, 0, 3, 0, 0, 0, 0)] }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? first));
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? second));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, first!.BagSlot, first.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(player, second!.BagSlot, second.Slot, 0, SpellCastTargets.ForSelf()));
        Assert.Equal(second.Guid, Assert.Single(kit.System.GetAuras(player), h => h.Spell.Id == spellId).ItemGuid);
    }
}
