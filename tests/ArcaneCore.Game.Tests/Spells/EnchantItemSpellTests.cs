using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.WorldData.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>The enchant effects through the cast pipeline (reagent staging, target identity, charges), on the enchanting engine.</summary>
public sealed class EnchantItemSpellTests
{
    private static void InstallEnchanting(SpellTestKit kit, params uint[] enchantIds)
        => new EnchantItemSpells(new EnchantCatalog(enchantIds.Select(id =>
                new SpellItemEnchantment(id, [0, 0, 0], [0, 0, 0], [0, 0, 0], "Enchant " + id, 0, 0))))
            .Install(kit.System);

    [Fact]
    public void PermanentEnchant_NonTriggeredCastConsumesReagentOnceAndKeepsTargetGuid()
    {
        const uint spellId = 49741;
        const uint enchantId = 49742;
        SpellInfo spell = SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with
        { Reagents = [new SpellReagent((int)ItemTestData.ToughJerky, 1)] };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        player.Inventory.Load([]); // reagent staging requires a loaded inventory
        Item target = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky);
        InstallEnchanting(kit, enchantId);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = target.Guid }, triggered: false));
        Assert.Equal(enchantId, target.EnchantmentId(0));
        Assert.Same(target, player.Inventory.GetItemByGuid(target.Guid));
        Assert.Null(player.Inventory.GetItemByGuid(reagent.Guid));
    }

    [Fact]
    public void PermanentEnchant_LowTargetItemLevelRefusesBeforeReagentConsumption()
    {
        const uint spellId = 49751;
        const uint enchantId = 49752;
        SpellInfo spell = SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with
        { BaseLevel = 10, Reagents = [new SpellReagent((int)ItemTestData.ToughJerky, 1)] };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        Item target = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky);
        uint countBefore = reagent.Count;
        InstallEnchanting(kit, enchantId);

        // vmangos Spell::CheckItems: a target item level below the spell's base level is SPELL_FAILED_LOWLEVEL.
        Assert.Equal(SpellCastResult.Lowlevel, kit.System.CastSpell(player, spellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = target.Guid }, triggered: false));
        Assert.Equal(0u, target.EnchantmentId(0));
        Assert.Equal(countBefore, reagent.Count);
    }

    [Fact]
    public void TemporaryEnchant_UsesEffectSecondsAndChargeCatalog()
    {
        const uint spellId = 49701;
        const uint enchantId = 49702;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 12, misc: (int)enchantId)));
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        Item item = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        InstallEnchanting(kit, enchantId);
        kit.System.SpellEnchantCharges = new SpellEnchantChargesCatalog([new SpellEnchantCharges(spellId, 3)]);

        SpellCastTargets targets = new() { Mask = SpellCastTargetFlags.Item, Item = item.Guid };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, targets, triggered: true));
        Assert.Equal(enchantId, item.EnchantmentId(1));
        Assert.Equal(12_000u, item.EnchantmentDuration(1));
        Assert.Equal(3u, item.EnchantmentCharges(1));
    }

    [Fact]
    public void TemporaryEnchant_ReplacesDifferentExistingEnchant()
    {
        const uint spellId = 49711;
        const uint enchantId = 49712;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 5, misc: (int)enchantId)));
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        Item item = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 3, 49713);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + 5, 2);
        InstallEnchanting(kit, enchantId, 49713);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid }, triggered: true));
        Assert.Equal(enchantId, item.EnchantmentId(1));
        Assert.Equal(5_000u, item.EnchantmentDuration(1));
        Assert.Equal(0u, item.EnchantmentCharges(1));
    }

    [Fact]
    public void EnchantTradeTargetWithoutATrade_IsRefusedBeforeReagentStaging()
    {
        const uint spellId = 49721;
        const uint enchantId = 49722;
        SpellInfo spell = SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with
        { Reagents = [new SpellReagent((int)ItemTestData.ToughJerky, 1)] };
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        Item reagent = ItemTestData.Give(player.Inventory, ItemTestData.ToughJerky);
        uint countBefore = reagent.Count;
        InstallEnchanting(kit, enchantId);

        // No trade names the slot: the target item is gone (vmangos CheckItems SPELL_FAILED_ITEM_GONE), and nothing is paid.
        Assert.Equal(SpellCastResult.ItemGone, kit.System.CastSpell(player, spellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = reagent.Guid }, triggered: true));
        Assert.Same(reagent, player.Inventory.GetItemByGuid(reagent.Guid));
        Assert.Equal(countBefore, reagent.Count);
    }

    [Fact]
    public void TemporaryEnchant_OverflowDurationIsRejectedBeforeReagents()
    {
        const uint spellId = 49731;
        const uint enchantId = 49732;
        SpellInfo spell = SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, int.MaxValue, misc: (int)enchantId));
        using var kit = new SpellTestKit(spell);
        (Player player, _) = kit.AddPlayer(1);
        ItemTestData.Wire(player.Inventory);
        Item item = ItemTestData.Give(player.Inventory, ItemTestData.WornShortsword);
        InstallEnchanting(kit, enchantId);

        Assert.Equal(SpellCastResult.ItemNotReady, kit.System.CastSpell(player, spellId,
            new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = item.Guid }, triggered: true));
        Assert.Equal(0u, item.EnchantmentId(1));
    }
}
