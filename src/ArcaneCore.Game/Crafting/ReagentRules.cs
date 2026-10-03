using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Crafting;

/// <summary>
/// Reagent and tool rules of a cast (crafting lane), after vmangos <c>Spell::CheckItems</c> (Spells/Spell.cpp:7249-7306),
/// <c>Spell::TakeReagents</c> (:5082-5128) and <c>Spell::IgnoreItemRequirements</c> (:7069-7083).
/// <para>
/// Triggered casts: vmangos only still requires reagents when the item target is not the caster's own or when the triggering spell
/// has no first reagent of its own; ArcaneCore does not track the triggering spell (<c>m_triggeredBySpellInfo</c>), so every
/// triggered cast ignores reagents and tools, which is the rule for the common case (a master spell that carries the reagents).
/// Documented limit in docs/areas/crafting.md.
/// </para>
/// </summary>
public static class ReagentRules
{
    /// <summary>
    /// Register the check (<see cref="ReagentCastCheck"/>) and the taker (<see cref="ReagentCostTaker"/>) on <paramref name="system"/>.
    /// A second call throws: the pair must exist exactly once or reagents would be checked or consumed twice.
    /// </summary>
    public static void Install(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (IsInstalled(system))
        {
            throw new InvalidOperationException("the reagent check and cost taker are already installed");
        }

        system.RegisterCastCheck(new ReagentCastCheck());
        system.RegisterCostTaker(new ReagentCostTaker());
    }

    /// <summary>Whether <see cref="Install"/> already ran on <paramref name="system"/>.</summary>
    public static bool IsInstalled(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.CostTakers.OfType<ReagentCostTaker>().Any();
    }

    /// <summary>vmangos <c>IgnoreItemRequirements</c>: only a player pays reagents, and a triggered cast never does (see the class remarks).</summary>
    public static bool IgnoresItemRequirements(Unit caster, bool triggered) => caster is not Player || triggered;

    /// <summary>
    /// The count of <paramref name="reagent"/> a cast really needs. When the cast item is itself the reagent, is used up by the
    /// cast (a negative <c>spellcharges</c> with fewer than two charges left) and the recipe needs more than one, the item does
    /// not count as a reagent, so the count grows by one (Spell.cpp:7269-7280 check, :5101-5113 take).
    /// </summary>
    public static uint EffectiveCount(Item? castItem, SpellReagent reagent)
    {
        uint count = reagent.Count;
        if (castItem is null || castItem.Entry != reagent.ItemId)
        {
            return count;
        }

        for (int s = 0; s < Item.SpellChargeSlots && s < castItem.Template.Spells.Count; s++)
        {
            int charges = castItem.GetInt32(UpdateFields.ItemFieldSpellCharges + s);
            if (castItem.Template.Spells[s].Charges < 0 && Math.Abs(charges) < 2 && count > 1)
            {
                return count + 1;
            }
        }

        return count;
    }
}

/// <summary>
/// Spell::CheckItems reagents then totems (Spell.cpp:7249-7306): after the equipment checks and the spell focus
/// (<see cref="SpellFocusCastCheck.FocusOrder"/>, :7230), before the effect checks (:7311). A missing reagent is
/// <see cref="SpellCastResult.ItemNotReady"/>, a missing tool <see cref="SpellCastResult.ItemGone"/>.
/// </summary>
public sealed class ReagentCastCheck : ISpellCastCheck
{
    /// <summary>After the focus check (Equipment + 50), before the item-target fit (Equipment + 70).</summary>
    public const int ReagentOrder = SpellCastCheckOrder.Equipment + 60;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => ReagentOrder;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellInfo spell = context.Spell;
        if (ReagentRules.IgnoresItemRequirements(context.Caster, context.Triggered) || context.Caster is not Player player)
        {
            return SpellCastResult.CastOk;
        }

        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (player.Inventory.GetItemCount(reagent.ItemId) < ReagentRules.EffectiveCount(context.CastItem, reagent))
            {
                return SpellCastResult.ItemNotReady;
            }
        }

        foreach (uint tool in spell.Totems)
        {
            if (player.Inventory.GetItemCount(tool) < 1)
            {
                return SpellCastResult.ItemGone;
            }
        }

        return SpellCastResult.CastOk;
    }
}

/// <summary>Spell::TakeReagents (Spell.cpp:5082-5128): destroy every reagent (the bank is not touched) right after the power is spent.</summary>
public sealed class ReagentCostTaker : ISpellCostTaker
{
    public void TakeCost(SpellCast cast)
    {
        if (ReagentRules.IgnoresItemRequirements(cast.Caster, cast.IsTriggered) || cast.Caster is not Player player)
        {
            return;
        }

        foreach (SpellReagent reagent in cast.Spell.Reagents)
        {
            uint count = ReagentRules.EffectiveCount(cast.CastItem, reagent);
            if (cast.CastItem is { } item && item.Entry == reagent.ItemId)
            {
                cast.CastItem = null; // the cast item is consumed as a reagent: vmangos clears m_CastItem so TakeCastItem does not use it up twice
            }

            player.Inventory.DestroyItemCount(reagent.ItemId, count, includeBank: false);
        }
    }
}
