using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class TradeItemUsePlanningTests
{
    [Fact]
    public void ItemTemplateSpellIsAuthorizedWithoutSpellbookKnowledge()
    {
        const uint spellId = 49901;
        const uint entry = 99101;
        const uint enchantId = 49902;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, entry, new ItemSpell(spellId, 0, -1, 0, 1200, 77, 3400));
        Item castItem = ItemTestData.Give(caster.Inventory, entry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out TradeEnchantmentPlan? plan));
        Assert.NotNull(plan?.ItemCast);
        Assert.Equal(spellId, plan!.ItemCast!.ItemSpell.SpellId);
        Assert.Equal(enchantId, plan.UpdatedRecipientItem.Enchantments[0]);
        Assert.Equal(0u, target.EnchantmentId(0));
        Assert.Equal(0u, plan.PowerCost);
        Assert.Null(plan.CasterLifeAfter);
        Assert.Equal(SpellCastResult.NotKnown,
            kit.System.HandleCastRequest(caster, spellId, SpellCastTargets.ForSelf()));
    }

    [Fact]
    public void WrongSlotAndSecondApplicableSpellAreRefusedWithoutMutation()
    {
        const uint first = 49911;
        const uint second = 49912;
        const uint entry = 99111;
        const uint enchantId = 49913;
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(first, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)),
            SpellTestKit.Spell(second, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, entry,
            new ItemSpell(first, 0, -1, 0, 0, 0, 0),
            new ItemSpell(second, 0, -1, 0, 0, 0, 0));
        Item castItem = ItemTestData.Give(caster.Inventory, entry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        uint count = castItem.Count;

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 1, out _));
        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out _));
        Assert.Equal(count, castItem.Count);
        Assert.Equal(0u, target.EnchantmentId(0));
    }

    [Fact]
    public void TemporaryItemEnchantCapturesDurationAndChargesWithoutMutatingLiveTarget()
    {
        const uint spellId = 49915;
        const uint enchantId = 49916;
        const uint entry = 99115;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 7, misc: (int)enchantId)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, entry, new ItemSpell(spellId, 0, -1, 0, 0, 0, 0));
        Item castItem = ItemTestData.Give(caster.Inventory, entry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        kit.System.SpellEnchantCharges = new ArcaneCore.Kernel.WorldData.Items.SpellEnchantChargesCatalog([
            new ArcaneCore.Kernel.WorldData.Items.SpellEnchantCharges(spellId, 4)]);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out TradeEnchantmentPlan? plan));
        Assert.Equal(enchantId, plan!.UpdatedRecipientItem.Enchantments[3]);
        Assert.Equal(7_000u, plan.UpdatedRecipientItem.Enchantments[4]);
        Assert.Equal(4u, plan.UpdatedRecipientItem.Enchantments[5]);
        Assert.Equal(0u, target.EnchantmentId(1));
    }

    [Fact]
    public void NonEnchantmentItemSpellIsRefused()
    {
        const uint spellId = 49917;
        const uint entry = 99117;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, entry, new ItemSpell(spellId, 0, -1, 0, 0, 0, 0));
        Item castItem = ItemTestData.Give(caster.Inventory, entry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
        Assert.Equal(0u, target.EnchantmentId(0));
    }

    [Fact]
    public void MissingReagentAndWrongOwnerDoNotMutateSource()
    {
        const uint spellId = 49921;
        const uint entry = 99121;
        const uint reagent = 99122;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: 49923)) with
        { Reagents = [new SpellReagent((int)reagent, 1)] });
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, entry, new ItemSpell(spellId, 0, -1, 0, 0, 0, 0), reagent: reagent);
        Item castItem = ItemTestData.Give(caster.Inventory, entry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(49923, [])]);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out _));
        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target,
                ItemTestData.Give(recipient.Inventory, entry), 0, out _));
        Assert.Equal(1u, castItem.Count);
    }

    private static void Wire(Player caster, Player recipient, uint entry, ItemSpell spell, ItemSpell? second = null,
        uint reagent = 0)
    {
        var templates = new List<ItemTemplate>(ItemTestData.Templates)
        {
            new()
            {
                Entry = entry,
                Class = 0,
                Stackable = 20,
                Spells = second is { } extra ? [spell, extra] : [spell],
            },
        };
        if (reagent != 0) templates.Add(new ItemTemplate { Entry = reagent, Class = 0, Stackable = 20 });
        var store = new ItemTemplateStore(templates, []);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        caster.Inventory.EnchantmentSink = new PlanningSink();
        recipient.Inventory.EnchantmentSink = new PlanningSink();
    }

    private sealed class PlanningSink : IItemEnchantmentSink
    {
        public void ApplyEnchantment(Player player, Item item, int enchantmentSlot, bool apply) { }
    }
}
