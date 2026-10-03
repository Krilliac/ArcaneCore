using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules.Immunity;

namespace ArcaneCore.Game.Spells.Rules.Gating;

/// <summary>
/// What the caster's own crowd-control state prevents (vmangos Spell::CheckCasterAuras, Spell.cpp:6565-6672):
/// stunned (instant spells always, cast-bar spells only with the stun interrupt flag), confused, fleeing,
/// silenced (PreventionType SILENCE, not tested while stunned, also an interrupt school lockout) and pacified
/// (PreventionType PACIFY). A spell that grants the matching school, mechanic or dispel immunity (the PvP trinket, Divine
/// Shield, Ice Block shapes: IMMUNITY_PURGES_EFFECT) may be cast through the states it covers; the right
/// failure is still returned for a state it does not cover.
/// </summary>
public static class CasterAuraGate
{
    /// <summary>The result for casting <paramref name="spell"/> (cast time <paramref name="castTime"/> ms) from <paramref name="caster"/>'s current state.</summary>
    public static SpellCastResult Check(SpellSystem system, Unit caster, SpellInfo spell, int castTime)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (ImmunityRules.IgnoresRestrictions(spell))
        {
            return SpellCastResult.CastOk;
        }

        uint schoolImmune = 0;
        uint mechanicImmune = 0;
        uint dispelImmune = 0;
        if (((uint)spell.AttributesEx & SpellRuleFlags.ExImmunityPurgesEffect) != 0)
        {
            foreach (SpellEffectInfo effect in spell.Effects)
            {
                switch (effect.AuraType)
                {
                    case AuraType.SchoolImmunity:
                        schoolImmune |= (uint)effect.MiscValue;
                        break;
                    case AuraType.MechanicImmunity:
                        mechanicImmune |= SpellMechanics.Mask((uint)effect.MiscValue);
                        break;
                    case AuraType.MechanicImmunityMask:
                        mechanicImmune |= (uint)effect.MiscValue;
                        break;
                    case AuraType.DispelImmunity:
                        dispelImmune |= DispelTypes.GetDispelMask((uint)effect.MiscValue);
                        break;
                }
            }
        }

        UnitFlags flags = caster.UnitFlags;
        bool stunned = (flags & UnitFlags.Stunned) != 0;
        SpellCastResult prevented = SpellCastResult.CastOk;
        if (stunned && (mechanicImmune & SpellMechanics.Mask(SpellMechanic.Stun)) == 0
            && (castTime == 0 || spell.InterruptFlags.HasFlag(SpellInterruptFlags.Stun)))
        {
            prevented = SpellCastResult.Stunned;
        }
        else if ((flags & UnitFlags.Confused) != 0 && (mechanicImmune & SpellMechanics.ConfusedMask) == 0)
        {
            prevented = SpellCastResult.Confused;
        }
        else if ((flags & UnitFlags.Fleeing) != 0 && (mechanicImmune & SpellMechanics.Mask(SpellMechanic.Fear)) == 0)
        {
            prevented = SpellCastResult.Fleeing;
        }
        else if (spell.PreventionType == SpellConstants.PreventionTypeSilence && !stunned
            && ((flags & UnitFlags.Silenced) != 0 || system.IsSchoolLocked(caster, spell.School)))
        {
            prevented = SpellCastResult.Silenced;
        }
        else if ((flags & UnitFlags.Pacified) != 0 && spell.PreventionType == SpellConstants.PreventionTypePacify)
        {
            prevented = SpellCastResult.Pacified;
        }

        if (prevented == SpellCastResult.CastOk)
        {
            return prevented;
        }

        if (schoolImmune == 0 && mechanicImmune == 0 && dispelImmune == 0)
        {
            return prevented;
        }

        // Prevented by a state, but the spell grants immunity: the cast still fails for any aura the immunity does not cover.
        foreach (SpellAuraHolder holder in system.GetAuras(caster))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            SpellInfo source = holder.Spell;
            if ((source.SchoolMask() & schoolImmune) != 0 || (source.Dispel < 32 && ((1u << (int)source.Dispel) & dispelImmune) != 0))
            {
                continue;
            }

            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                SpellAura? aura = holder.Auras[i];
                if (aura is null || (source.EffectMechanicMask(i) & mechanicImmune) != 0)
                {
                    continue;
                }

                switch (aura.Type)
                {
                    case AuraType.ModStun when (mechanicImmune & SpellMechanics.Mask(SpellMechanic.Stun)) == 0:
                        return SpellCastResult.Stunned;
                    case AuraType.ModConfuse when (mechanicImmune & SpellMechanics.ConfusedMask) == 0:
                        return SpellCastResult.Confused;
                    case AuraType.ModFear when (mechanicImmune & SpellMechanics.Mask(SpellMechanic.Fear)) == 0:
                        return SpellCastResult.Fleeing;
                    case AuraType.ModSilence or AuraType.ModPacify or AuraType.ModPacifySilence:
                        if (spell.PreventionType == SpellConstants.PreventionTypePacify)
                        {
                            return SpellCastResult.Pacified;
                        }

                        if (spell.PreventionType == SpellConstants.PreventionTypeSilence)
                        {
                            return SpellCastResult.Silenced;
                        }

                        break;
                }
            }
        }

        return SpellCastResult.CastOk;
    }
}
