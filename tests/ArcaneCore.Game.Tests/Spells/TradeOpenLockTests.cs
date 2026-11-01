using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// An open-lock on-use item spell (a lockpick aimed at a partner's lockbox in the "Will not be traded" slot) is planned to be cast once the
/// trade committed, as the spellbook path does (vmangos Spell.cpp:6455-6477), instead of being forced through the enchantment image builder
/// (which refused it with ITEM_NOT_READY).
/// </summary>
public sealed class TradeOpenLockTests
{
    private const uint OpenLockSpell = 49921;
    private const uint DummySpell = 49922;
    private const uint PickEntry = 99121;

    private static void Wire(Player caster, Player recipient, uint entry, ItemSpell spell)
    {
        var templates = new List<ItemTemplate>(ItemTestData.Templates)
        {
            new() { Entry = entry, Class = 0, Stackable = 20, Spells = [spell] },
        };
        var store = new ItemTemplateStore(templates, []);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        caster.Inventory.Templates = store;
        recipient.Inventory.Templates = store;
    }

    private static SpellInfo ItemSpellInfo(uint id, SpellEffectName effect)
        => SpellTestKit.Spell(id, SpellTestKit.Effect(effect, 0)) with { StartRecoveryTime = 0, StartRecoveryCategory = 0 };

    [Fact]
    public void OpenLockItemSpell_IsPlannedToCastOnCommit_WithTheImageUnchanged()
    {
        using var kit = new SpellTestKit(ItemSpellInfo(OpenLockSpell, SpellEffectName.OpenLock));
        Item? seen = null;
        kit.System.RegisterEffectCheck(SpellEffectName.OpenLock, context =>
        {
            seen = context.Targets.ServerValidatedTradeItem;
            return seen is null ? SpellCastResult.BadTargets : SpellCastResult.CastOk;
        });
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, PickEntry, new ItemSpell(OpenLockSpell, 0, -1, 0, 0, 0, 0));
        Item castItem = ItemTestData.Give(caster.Inventory, PickEntry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);

        SpellCastResult result = kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out TradeEnchantmentPlan? plan);

        Assert.Equal(SpellCastResult.CastOk, result);
        Assert.NotNull(plan);
        Assert.True(plan!.CastOnCommit);
        Assert.NotNull(plan.ItemCast);
        Assert.Same(target, seen);
        ItemInstanceData before = target.ToData();
        Assert.Equal(before.Guid, plan.UpdatedRecipientItem.Guid);
        Assert.Equal(before.Enchantments, plan.UpdatedRecipientItem.Enchantments);
    }

    [Fact]
    public void NonEnchantNonOpenLockItemSpell_IsStillRefused()
    {
        using var kit = new SpellTestKit(ItemSpellInfo(DummySpell, SpellEffectName.Dummy));
        (Player caster, _) = kit.AddPlayer(1);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        Wire(caster, recipient, PickEntry, new ItemSpell(DummySpell, 0, -1, 0, 0, 0, 0));
        Item castItem = ItemTestData.Give(caster.Inventory, PickEntry);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeItemEnchantment(caster, recipient, target, castItem, 0, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
    }
}
