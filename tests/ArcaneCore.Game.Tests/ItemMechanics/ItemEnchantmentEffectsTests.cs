using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class ItemEnchantmentEffectsTests
{
    [Fact]
    public void RangedMixedEnchant_PreservesItsEligibleStatEffectAndOriginalDamageHandOnRemoval()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Shaman);
        var catalog = new ItemEnchantmentCatalog([new ItemEnchantmentDefinition(7010,
            [new(6, 0, 7), new(5, 4, 3), new(2, 0, 5)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        Item item = new(7010, new ItemTemplate { Entry = 7010, Class = 2, InventoryType = 15, Delay = 1800 }, player.Guid)
            { Inventory = player.Inventory, Slot = InventorySlots.Ranged };
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7010);
        int strength = player.GetInt32(UpdateFields.UnitFieldStat0);

        sink.ApplyEnchantment(player, item, 0, true);
        Assert.Equal(strength + 3, player.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(5f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.RangedAttack]);
        Assert.Equal(0f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.BaseAttack]);
        item.Slot = InventorySlots.MainHand;
        sink.ApplyEnchantment(player, item, 0, false);
        Assert.Equal(strength, player.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(0f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.RangedAttack]);
        Assert.Equal(0f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.BaseAttack]);
    }

    [Fact]
    public void EquippedStatEnchant_AppliesThroughInventory_AndReplacementRemovesTheOldSnapshot()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        var stats = new PlayerStatSystem();
        stats.Attach(player);
        var catalog = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(7001, [new ItemEnchantmentEffect(5, 4, 10)]),
            new ItemEnchantmentDefinition(7002, [new ItemEnchantmentEffect(5, 4, 20)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        player.Inventory.EnchantmentSink = sink;
        Item item = ItemTestData.Give(player.Inventory, ItemTestData.StrengthRing);
        int before = player.GetInt32(UpdateFields.UnitFieldStat0);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7001);

        player.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Same(item, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Finger1));
        Assert.Equal(before + 5 + 10, player.GetInt32(UpdateFields.UnitFieldStat0)); // ring + enchant
        Assert.Equal(7001u, player.GetUInt32(UpdateFields.PlayerVisibleItem10 + item.Slot * 12 + 1));
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7002);
        sink.ApplyEnchantment(player, item, 0, apply: true);
        Assert.Equal(before + 5 + 20, player.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(7002u, player.GetUInt32(UpdateFields.PlayerVisibleItem10 + item.Slot * 12 + 1));
        sink.ApplyEnchantment(player, item, 0, apply: false);
        Assert.Equal(before + 5, player.GetInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void ResistanceEnchant_ChangesRawResistanceWithoutInventingTooltipBuffContribution()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        var catalog = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(7003, [new ItemEnchantmentEffect(4, (uint)SpellSchool.Fire, 15)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        Item item = new(7003, new ItemTemplate { Entry = 7003, Class = 4, InventoryType = 11 }, player.Guid);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7003);
        item.Slot = InventorySlots.Finger1;
        item.Inventory = player.Inventory;
        player.Inventory.EnchantmentSink = sink;
        int before = player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire);

        sink.ApplyEnchantment(player, item, 0, apply: true);
        Assert.Equal(before + 15, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerFieldResistancebuffmodspositive + (int)SpellSchool.Fire), 3);
        sink.ApplyEnchantment(player, item, 0, apply: false);
        Assert.Equal(before, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
    }

    [Fact]
    public void StatEnchantManaAndHealthIdsFollowSourceAndClampOnRemoval()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        var catalog = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(7004, [new ItemEnchantmentEffect(5, 0, 25), new ItemEnchantmentEffect(5, 1, 40)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        Item item = new(7004, new ItemTemplate { Entry = 7004, Class = 4, InventoryType = 11 }, player.Guid)
        {
            Inventory = player.Inventory,
            Slot = InventorySlots.Finger1,
        };
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7004);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 10);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 10);
        player.MaxHealth = 100;
        player.Health = 100;

        sink.ApplyEnchantment(player, item, 0, true);
        Assert.Equal(35u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.Equal(140u, player.MaxHealth);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 35);
        player.Health = player.MaxHealth;
        sink.ApplyEnchantment(player, item, 0, false);
        Assert.Equal(10u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.Equal(100u, player.MaxHealth);
        Assert.Equal(10u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void DamageAndTotemEnchantmentsKeepFractionalStateBeforeMaintainer_AndRejectRangedTotem()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Shaman);
        var catalog = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(7005, [new ItemEnchantmentEffect(2, 0, 5)]),
            new ItemEnchantmentDefinition(7006, [new ItemEnchantmentEffect(6, 0, 7)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        Item main = new(7005, new ItemTemplate { Entry = 7005, Class = 2, InventoryType = 13, Delay = 1800 }, player.Guid)
        {
            Inventory = player.Inventory, Slot = InventorySlots.MainHand,
        };
        main.SetUInt32(UpdateFields.ItemFieldEnchantment, 7005);
        sink.ApplyEnchantment(player, main, 0, true);
        var stats = new PlayerStatSystem();
        stats.Attach(player); // rebuilds from the pre-maintainer enchant bonus
        Assert.Equal(5f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.BaseAttack], 3);
        sink.ApplyEnchantment(player, main, 0, false);
        Assert.Equal(0f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.BaseAttack], 3);

        Item ranged = new(7006, new ItemTemplate { Entry = 7006, Class = 2, InventoryType = 15, Delay = 1800 }, player.Guid)
        {
            Inventory = player.Inventory, Slot = InventorySlots.Ranged,
        };
        ranged.SetUInt32(UpdateFields.ItemFieldEnchantment, 7006);
        sink.ApplyEnchantment(player, ranged, 0, true);
        Assert.Equal(0f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.RangedAttack], 3);
        ranged.Slot = InventorySlots.MainHand;
        sink.ApplyEnchantment(player, ranged, 0, true);
        Assert.Equal(12.6f, player.StatState.EnchantmentDamageBonus[(int)WeaponAttackType.BaseAttack], 3);
    }

    [Fact]
    public void EquipSpellEnchant_TracksExactItemHolder_AndPreservesForeignSameIdAura()
    {
        SpellInfo enchantSpell = SpellTestKit.Spell(7010,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.ModStat),
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 2, SpellImplicitTarget.UnitFriend, AuraType.ModStat, misc: 0))
            with { RangeIndex = 4, Range = new SpellRange(0, 30) };
        using var kit = new SpellTestKit(enchantSpell);
        (Player owner, _) = kit.AddPlayer(1);
        (Player foreign, _) = kit.AddPlayer(2);
        var catalog = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(7011, [new ItemEnchantmentEffect(3, 7010, 0)])]);
        var sink = new ItemEnchantmentEffects(catalog);
        ItemTestData.Wire(owner.Inventory);
        Item item = ItemTestData.Give(owner.Inventory, ItemTestData.WornShortsword);
        owner.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Same(item, owner.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
        item.SetUInt32(UpdateFields.ItemFieldEnchantment, 7011);
        owner.Inventory.EnchantmentSpellSink = new SpellSystemEnchantmentEquipSink(kit.System);

        sink.ApplyEnchantment(owner, item, 0, apply: true);
        SpellAuraHolder owned = Assert.Single(kit.System.GetAuras(owner), h => h.Spell.Id == 7010);
        Assert.True(owned.IsItemEquipAura);
        Assert.Equal(item.Guid, owned.ItemGuid);
        Assert.Equal(owner.Guid, owned.CasterGuid);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(foreign, 7010, SpellCastTargets.ForUnit(owner.Guid), triggered: true));
        // The existing positive-aura policy replaces the prior holder across casters.
        SpellAuraHolder foreignReplacement = Assert.Single(kit.System.GetAuras(owner), h => h.Spell.Id == 7010);
        Assert.Equal(foreign.Guid, foreignReplacement.CasterGuid);
        sink.ApplyEnchantment(owner, item, 0, apply: false);
        SpellAuraHolder remaining = Assert.Single(kit.System.GetAuras(owner), h => h.Spell.Id == 7010);
        Assert.False(remaining.IsItemEquipAura);
        Assert.Equal(foreign.Guid, remaining.CasterGuid);
        Assert.Same(foreignReplacement, remaining);
    }
}
