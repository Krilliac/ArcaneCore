using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class TradeEnchantPlanningTests
{
    [Fact]
    public void RawTradeTarget_RequiresExactlyTradeItemAndPackedSlotSix()
    {
        var writer = new PacketWriter(10);
        writer.WriteUInt16((ushort)SpellCastTargetFlags.TradeItem);
        writer.WritePackedGuid(6);
        PacketReader reader = new(writer.ToArray());
        SpellCastTargets target = SpellCastTargets.Read(ref reader);
        Assert.True(target.IsRawNonTradedTradeTarget);
        target.Mask |= SpellCastTargetFlags.Item;
        Assert.False(target.IsRawNonTradedTradeTarget);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0x1234)]
    public void RawTradeTarget_RejectsOtherPackedValues(uint raw)
    {
        var writer = new PacketWriter(10);
        writer.WriteUInt16((ushort)SpellCastTargetFlags.TradeItem);
        writer.WritePackedGuid(raw);
        PacketReader reader = new(writer.ToArray());
        Assert.False(SpellCastTargets.Read(ref reader).IsRawNonTradedTradeTarget);
    }

    [Fact]
    public void PlanForeignOwnerEnchant_ProducesAfterImageAndReagentCostWithoutMutation()
    {
        const uint spellId = 49801;
        const uint enchantId = 49802;
        using var kit = new SpellTestKit(TradeSpell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 4, misc: (int)enchantId)) with
        { Reagents = [new SpellReagent((int)ItemTestData.ToughJerky, 1)] });
        (Player caster, _) = kit.AddPlayer(1);
        foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        Item reagent = ItemTestData.Give(caster.Inventory, ItemTestData.ToughJerky);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        uint oldEnchant = target.EnchantmentId(1);

        SpellCastResult result = kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan);

        Assert.Equal(SpellCastResult.CastOk, result);
        Assert.NotNull(plan);
        Assert.Equal(oldEnchant, target.EnchantmentId(1));
        Assert.Equal(reagent.Count, caster.Inventory.GetItemByGuid(reagent.Guid)!.Count);
        Assert.Equal(enchantId, plan!.UpdatedRecipientItem.Enchantments[3]);
        Assert.Equal(1u, Assert.Single(plan.Reagents).Count);
        Assert.Null(plan.CasterLifeAfter);
        IList<uint> snapshot = (IList<uint>)plan.UpdatedRecipientItem.Enchantments;
        Assert.Throws<NotSupportedException>(() => snapshot[3] = 0);
    }

    [Fact]
    public void TemporaryPlanUsesEffectBaseValueSecondsAndCharges()
    {
        const uint spellId = 49861;
        const uint enchantId = 49862;
        SpellInfo spell = TradeSpell(spellId, SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 7,
            misc: (int)enchantId)) with { StartRecoveryTime = 0 };
        using var kit = new SpellTestKit(spell);
        (Player caster, _) = kit.AddPlayer(1); foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        kit.System.SpellEnchantCharges = new ArcaneCore.Kernel.WorldData.Items.SpellEnchantChargesCatalog([
            new ArcaneCore.Kernel.WorldData.Items.SpellEnchantCharges(spellId, 4)]);

        Assert.Equal(SpellCastResult.CastOk, kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan));
        Assert.Equal(7_000u, plan!.UpdatedRecipientItem.Enchantments[4]);
        Assert.Equal(4u, plan.UpdatedRecipientItem.Enchantments[5]);
    }

    [Fact]
    public void PlanRejectsItemNotOwnedByRecipientWithoutMutation()
    {
        using var kit = new SpellTestKit(TradeSpell(49811,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: 49812)));
        (Player caster, _) = kit.AddPlayer(1);
        foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(caster.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(49812, [])]);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, 49811, default, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
        Assert.Equal(0u, target.EnchantmentId(0));
    }

    [Fact]
    public void PlanRejectsDeadCasterBeforeBuildingAfterImage()
    {
        const uint spellId = 49821;
        const uint enchantId = 49822;
        using var kit = new SpellTestKit(TradeSpell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)));
        (Player caster, _) = kit.AddPlayer(1);
        foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        caster.Health = 0;
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);

        Assert.Equal(SpellCastResult.CasterDead,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
    }

    [Fact]
    public void PlanRejectsStunnedCasterAndUnsupportedCosts()
    {
        const uint spellId = 49831;
        const uint enchantId = 49832;
        SpellInfo spell = TradeSpell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 2, misc: (int)enchantId)) with
        { ManaCost = 5, PowerType = (int)PowerType.Mana };
        using var kit = new SpellTestKit(spell);
        (Player caster, _) = kit.AddPlayer(1);
        foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 50);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan));
        Assert.NotNull(plan);
        caster.UnitFlags |= UnitFlags.Stunned;
        spell = spell with { ManaCost = 0, PowerType = 0 };
        kit.System.Store = new SpellStore([spell], [], []);
        Assert.NotEqual(SpellCastResult.CastOk,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out _));
    }

    [Fact]
    public void PlanCapturesManaAfterImageWithoutChangingLivePower()
    {
        const uint spellId = 49881;
        const uint enchantId = 49882;
        using var kit = new SpellTestKit(TradeSpell(spellId, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with
        { ManaCost = 10, PowerType = (int)PowerType.Mana });
        (Player caster, _) = kit.AddPlayer(1); foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 50);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        uint before = SpellSystem.GetPower(caster, PowerType.Mana);
        Assert.Equal(SpellCastResult.CastOk, kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan, acceptance: true));
        Assert.Equal(10u, plan!.PowerCost);
        Assert.Equal(before, SpellSystem.GetPower(caster, PowerType.Mana));
        Assert.Equal(before - 10, plan.CasterLifeAfter!.Powers[(int)PowerType.Mana]);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)plan.CasterLifeAfter.Powers)[0] = 999);
    }

    [Fact]
    public void PlanCapturesHealthAfterImage_AndRejectsInsufficientHealthWithoutMutation()
    {
        const uint spellId = 49891;
        const uint enchantId = 49892;
        using var kit = new SpellTestKit(TradeSpell(spellId, SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with
        { ManaCost = 10, PowerType = SpellMath.PowerHealth });
        (Player caster, _) = kit.AddPlayer(1); foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        caster.Health = 50;
        uint beforeHealth = caster.Health;
        Assert.Equal(SpellCastResult.CastOk, kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out TradeEnchantmentPlan? plan, acceptance: true));
        Assert.Equal(40u, plan!.CasterLifeAfter!.Health);
        Assert.Equal(beforeHealth, caster.Health);
        caster.Health = 5;
        Assert.Equal(SpellCastResult.CasterAurastate, kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out _));
        Assert.Equal(5u, caster.Health);
    }

    [Fact]
    public void PlanRejectsCastItemIdentityAndReturnsReadOnlySnapshots()
    {
        const uint spellId = 49841;
        const uint enchantId = 49842;
        using var kit = new SpellTestKit(TradeSpell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 2, misc: (int)enchantId)));
        (Player caster, _) = kit.AddPlayer(1);
        foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);
        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, ObjectGuid.Item(999999), out _));
        Assert.Equal(0u, target.EnchantmentId(1));
    }

    [Fact]
    public void PlanAllowsCooldownMetadataWithoutAddingCooldownDuringPlanning()
    {
        const uint spellId = 49871;
        const uint enchantId = 49872;
        using var kit = new SpellTestKit(TradeSpell(spellId,
            SpellTestKit.Effect(SpellEffectName.EnchantItem, 0, misc: (int)enchantId)) with { RecoveryTime = 1_000 });
        (Player caster, _) = kit.AddPlayer(1); foreach (SpellInfo known in kit.System.Store.All) kit.Spellbook.LearnSpell(caster, known.Id);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory); ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);
        kit.System.ItemEnchantments = new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentCatalog([
            new ArcaneCore.Kernel.WorldData.Items.ItemEnchantmentDefinition(enchantId, [])]);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, spellId, default, out _));
    }

    private static SpellInfo TradeSpell(uint id, params SpellEffectInfo[] effects)
        => SpellTestKit.Spell(id, effects) with { StartRecoveryTime = 0, StartRecoveryCategory = 0 };
}
