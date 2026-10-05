using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_INSTAKILL, HEAL_MAX_HEALTH, THREAT and DISPEL_MECHANIC after vmangos
/// <c>Spell::EffectInstaKill</c> (SpellEffects.cpp:268), <c>EffectHealMaxHealth</c> (:3516),
/// <c>EffectThreat</c> (:3503) and <c>EffectDispelMechanic</c> (:5507). Only the 1.12.1 build branch of
/// vmangos is followed (SUPPORTED_CLIENT_BUILD = 1.12.1, shared/Progression.h:36).
/// </summary>
public sealed class DirectCombatEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Instakill, EffectInstakill);
        system.RegisterEffect(SpellEffectName.HealMaxHealth, EffectHealMaxHealth);
        system.RegisterEffect(SpellEffectName.Threat, EffectThreat);
        system.RegisterEffect(SpellEffectName.DispelMechanic, EffectDispelMechanic);
    }

    /// <summary>
    /// vmangos Spell::EffectInstaKill: SMSG_SPELLINSTAKILLLOG (victim guid, spell; sent for builds after
    /// 1.11.2 to the caster's set, self included), then the caster deals the target's whole health as
    /// direct damage. The "caster == target: finish()" line only suppresses the interrupt message of a
    /// cast that killed itself; the repo's cast has already finished when its effects run.
    /// </summary>
    private static void EffectInstakill(SpellEffectContext context)
    {
        Unit target = context.Target;
        if (!target.IsAlive)
        {
            return;
        }

        var writer = new PacketWriter(12);
        writer.WriteUInt64(target.Guid.Value);
        writer.WriteUInt32(context.Spell.Id);
        SpellSystem.SendToSet(context.Caster, WorldOpcode.SmsgSpellinstakilllog, writer.ToArray(), includeSelf: true);
        context.System.Damage.DealSpellDamage(context.Caster, target, context.Spell, target.Health,
            periodic: false, startsCombat: true, durabilityLoss: false);
    }

    /// <summary>
    /// vmangos Spell::EffectHealMaxHealth: the heal is the CASTER's maximum health (Lay on Hands), times
    /// the caster's SPELL_AURA_MOD_HEALING_DONE_PERCENT auras (each (100 + amount) / 100) and the target's
    /// strongest negative and strongest positive SPELL_AURA_MOD_HEALING_PCT; it then follows the heal
    /// path (crit, SMSG_SPELLHEALLOG) with rand_ditheru rounding.
    /// </summary>
    private static void EffectHealMaxHealth(SpellEffectContext context)
    {
        SpellSystem system = context.System;
        Unit target = context.Target;
        if (!target.IsAlive)
        {
            return;
        }

        float heal = context.Caster.MaxHealth;
        foreach (SpellAura aura in system.AurasOfType(context.Caster, AuraType.ModHealingDonePercent))
        {
            heal *= (100.0f + aura.Amount) / 100.0f;
        }

        int negative = system.MaxNegativeAuraModifier(target, AuraType.ModHealingPct);
        if (negative != 0)
        {
            heal *= (100.0f + negative) / 100.0f;
        }

        int positive = system.MaxPositiveAuraModifier(target, AuraType.ModHealingPct);
        if (positive != 0)
        {
            heal *= (100.0f + positive) / 100.0f;
        }

        uint amount = Dither(system, heal);
        if (amount > 0)
        {
            system.DeliverHeal(context, amount);
        }
    }

    /// <summary>
    /// vmangos Spell::EffectThreat -> Unit::AddThreat: both units must be alive; the rules of the threat list
    /// are in <see cref="SpellThreat"/>.
    /// </summary>
    private static void EffectThreat(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || !context.Caster.IsAlive)
        {
            return;
        }

        SpellThreat.Add(context.System, context.Caster, context.Target, context.Spell, context.Value);
    }

    /// <summary>
    /// vmangos Spell::EffectDispelMechanic: every aura holder of the target whose spell, or one of its
    /// aura-carrying effects, has the mechanic named by EffectMiscValue is removed (SpellAuraHolder::
    /// HasMechanic, SpellAuras.cpp:7435), whatever its polarity or caster.
    /// </summary>
    private static void EffectDispelMechanic(SpellEffectContext context)
    {
        uint mechanic = (uint)context.Effect.MiscValue;
        foreach (SpellAuraHolder holder in context.System.GetAuras(context.Target).ToArray())
        {
            if (!holder.IsRemoved && HasMechanic(holder, mechanic))
            {
                context.System.RemoveAuras(context.Target, holder.Spell.Id);
            }
        }
    }

    private static bool HasMechanic(SpellAuraHolder holder, uint mechanic)
    {
        if (holder.Spell.Mechanic == mechanic)
        {
            return true;
        }

        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if (holder.Auras[i] is not null && holder.Spell.Effects[i].Mechanic == mechanic)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos rand_ditheru: round down or up with probability equal to the fraction.</summary>
    private static uint Dither(SpellSystem system, float value)
    {
        if (value <= 0f)
        {
            return 0;
        }

        uint whole = (uint)value;
        float fraction = value - whole;
        return fraction > 0f && system.Random.NextDouble() < fraction ? whole + 1 : whole;
    }
}
