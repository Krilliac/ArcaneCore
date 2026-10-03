using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// The immunity aura handlers (vmangos SpellAuras.cpp:4042-4180). The immunity lists themselves are read from the
/// live auras by <see cref="ImmunityRules"/>, so a handler only does what vmangos does besides listing: with
/// IMMUNITY_PURGES_EFFECT it removes the auras the new immunity covers (mechanic, mechanic mask, state, dispel type,
/// and for a positive school immunity the negative auras of those schools, which also sets UNIT_FLAG_IMMUNE).
/// </summary>
internal static class ImmunityAuraHandlers
{
    public static Dictionary<AuraType, AuraHandler> Install(Dictionary<AuraType, AuraHandler> handlers)
    {
        handlers[AuraType.MechanicImmunity] = new AuraHandler(MechanicImmunity, null);
        handlers[AuraType.MechanicImmunityMask] = new AuraHandler(MechanicImmunityMask, null);
        handlers[AuraType.EffectImmunity] = new AuraHandler(null, null);
        handlers[AuraType.StateImmunity] = new AuraHandler(StateImmunity, null);
        handlers[AuraType.SchoolImmunity] = new AuraHandler(SchoolImmunity, null);
        handlers[AuraType.DamageImmunity] = new AuraHandler(null, null);
        handlers[AuraType.DispelImmunity] = new AuraHandler(DispelImmunity, null);
        return handlers;
    }

    private static bool Purges(SpellAuraHolder holder) => ((uint)holder.Spell.AttributesEx & SpellRuleFlags.ExImmunityPurgesEffect) != 0;

    private static void MechanicImmunity(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply && Purges(holder) && aura.MiscValue > 0)
        {
            RemoveAurasAtMechanicImmunity(system, holder.Target, SpellMechanics.Mask((uint)aura.MiscValue), holder.Spell.Id);
        }
    }

    private static void MechanicImmunityMask(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply && Purges(holder))
        {
            RemoveAurasAtMechanicImmunity(system, holder.Target, (uint)aura.MiscValue, holder.Spell.Id);
        }
    }

    /// <summary>vmangos Unit::RemoveAurasAtMechanicImmunity (Unit.cpp:9775-9799): every aura with one of the mechanics, except NO_IMMUNITIES spells and the immunity spell itself.</summary>
    private static void RemoveAurasAtMechanicImmunity(SpellSystem system, Unit target, uint mechanicMask, uint exceptSpellId)
    {
        foreach (SpellAuraHolder other in system.GetAuras(target).ToArray())
        {
            if (!other.IsRemoved && other.Spell.Id != exceptSpellId && !other.Spell.IgnoresImmunities() && (other.Spell.AllMechanicMask() & mechanicMask) != 0)
            {
                system.RemoveAuras(target, other.Spell.Id);
            }
        }
    }

    private static void StateImmunity(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (!apply || !Purges(holder))
        {
            return;
        }

        foreach (SpellAuraHolder other in system.GetAuras(holder.Target).ToArray())
        {
            if (!other.IsRemoved && !ReferenceEquals(other, holder) && other.HasAura((AuraType)aura.MiscValue))
            {
                system.RemoveAuras(holder.Target, other.Spell.Id);
            }
        }
    }

    private static void SchoolImmunity(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (!Purges(holder) || !holder.Spell.IsPositive)
        {
            return;
        }

        Unit target = holder.Target;
        if (apply)
        {
            uint schools = (uint)aura.MiscValue;
            foreach (SpellAuraHolder other in system.GetAuras(target).ToArray())
            {
                if (!other.IsRemoved && (other.Spell.SchoolMask() & schools) != 0 && !other.Spell.IgnoresImmunities()
                    && !other.IsPositive && other.Spell.Id != holder.Spell.Id)
                {
                    system.RemoveAuras(target, other.Spell.Id);
                }
            }

            target.UnitFlags |= UnitFlags.Immune;
        }
        else if (!system.HasLiveAura(target, AuraType.SchoolImmunity))
        {
            target.UnitFlags &= ~UnitFlags.Immune;
        }
    }

    /// <summary>vmangos Unit::ApplySpellDispelImmunity: with PURGES, every aura of the dispel type goes (Unit::RemoveAurasWithDispelType).</summary>
    private static void DispelImmunity(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (!apply || !Purges(holder) || aura.MiscValue <= 0)
        {
            return;
        }

        uint mask = DispelTypes.GetDispelMask((uint)aura.MiscValue);
        foreach (SpellAuraHolder other in system.GetAuras(holder.Target).ToArray())
        {
            if (!other.IsRemoved && !ReferenceEquals(other, holder) && other.Spell.Dispel < 32 && (mask & (1u << (int)other.Spell.Dispel)) != 0)
            {
                system.RemoveAuras(holder.Target, other.Spell.Id);
            }
        }
    }
}
