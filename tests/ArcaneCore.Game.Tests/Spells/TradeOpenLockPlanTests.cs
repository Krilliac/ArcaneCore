using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A spell aimed at the "Will not be traded" slot that is not an enchantment (Pick Lock on a partner's lockbox) is planned to be cast on
/// trade completion (vmangos Spell.cpp:6455-6477, TradeHandler.cpp:347-416) instead of being refused with ITEM_NOT_READY.
/// </summary>
public sealed class TradeOpenLockPlanTests
{
    private const uint PickLock = 49901;

    private static SpellInfo TradeSpell(uint id, params SpellEffectInfo[] effects)
        => SpellTestKit.Spell(id, effects) with { StartRecoveryTime = 0, StartRecoveryCategory = 0 };

    [Fact]
    public void OpenLockOnPartnersItem_IsPlannedToCastOnCommit_WithTheImageUnchanged()
    {
        using var kit = new SpellTestKit(TradeSpell(PickLock, SpellTestKit.Effect(SpellEffectName.OpenLock, 0)));
        Item? seen = null;
        kit.System.RegisterEffectCheck(SpellEffectName.OpenLock, context =>
        {
            seen = context.Targets.ServerValidatedTradeItem;
            return seen is null ? SpellCastResult.BadTargets : SpellCastResult.CastOk;
        });
        (Player caster, _) = kit.AddPlayer(1);
        kit.Spellbook.LearnSpell(caster, PickLock);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);

        SpellCastResult result = kit.System.TryPlanTradeEnchantment(caster, recipient, target, PickLock, default, out TradeEnchantmentPlan? plan);

        Assert.Equal(SpellCastResult.CastOk, result);
        Assert.NotNull(plan);
        Assert.True(plan!.CastOnCommit);
        Assert.Same(target, seen);
        ItemInstanceData before = target.ToData();
        Assert.Equal(before.Guid, plan.UpdatedRecipientItem.Guid);
        Assert.Equal(before.Entry, plan.UpdatedRecipientItem.Entry);
        Assert.Equal(before.Enchantments, plan.UpdatedRecipientItem.Enchantments);
        Assert.Empty(plan.Reagents);
    }

    [Fact]
    public void ANonEnchantNonOpenLockSpell_IsStillRefused()
    {
        using var kit = new SpellTestKit(TradeSpell(PickLock, SpellTestKit.Effect(SpellEffectName.Dummy, 0)));
        (Player caster, _) = kit.AddPlayer(1);
        kit.Spellbook.LearnSpell(caster, PickLock);
        (Player recipient, _) = kit.AddPlayer(2, 2, 0);
        ItemTestData.Wire(caster.Inventory);
        ItemTestData.Wire(recipient.Inventory);
        Item target = ItemTestData.Give(recipient.Inventory, ItemTestData.WornShortsword);

        Assert.Equal(SpellCastResult.ItemNotReady,
            kit.System.TryPlanTradeEnchantment(caster, recipient, target, PickLock, default, out TradeEnchantmentPlan? plan));
        Assert.Null(plan);
    }
}
