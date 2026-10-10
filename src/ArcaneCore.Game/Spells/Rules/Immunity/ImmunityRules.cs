using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Totems;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// Unit immunities as vmangos Unit::IsImmuneToDamage / IsImmuneToSpell / IsImmuneToSpellEffect (Unit.cpp:5404-5600)
/// and the creature overrides (Creature.cpp:2438-2480). The immunity lists are not stored: they are read from the
/// live auras of the unit (school, damage, dispel, mechanic, mechanic-mask, effect and state immunity auras), so an
/// expiring aura can never leave a stale entry. An immunity spell protects against spells of the opposite polarity
/// (a positive immunity against harmful spells) and against both only with
/// <see cref="SpellRuleFlags.ExImmunityToHostileAndFriendly"/>. Positivity is <see cref="SpellInfo.IsPositive"/>,
/// the engine's simplification of vmangos IsPositiveSpell, and per-effect positivity uses the whole spell.
/// </summary>
public static class ImmunityRules
{
    /// <summary>vmangos IsIgnoringCasterAndTargetRestrictions (SpellEntry.h:925-929).</summary>
    public static bool IgnoresRestrictions(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return ((uint)spell.AttributesEx & SpellRuleFlags.ExIgnoreCasterAndTargetRestrictions) != 0
            || (spell.AttributesEx3 & SpellRuleFlags.Ex3IgnoreCasterAndTargetRestrictions) != 0;
    }

    /// <summary>Whether an immunity granted by <paramref name="immunitySpell"/> applies to an incoming spell of the given polarity.</summary>
    public static bool Applies(SpellInfo immunitySpell, bool incomingPositive) =>
        immunitySpell.IsPositive != incomingPositive || ((uint)immunitySpell.AttributesEx & SpellRuleFlags.ExImmunityToHostileAndFriendly) != 0;

    /// <summary>The live (not removed) auras of <paramref name="type"/> on the unit.</summary>
    public static IEnumerable<SpellAura> LiveAuras(SpellSystem system, Unit unit, AuraType type)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type)
                {
                    yield return aura;
                }
            }
        }
    }

    private static bool Matches(SpellSystem system, Unit unit, AuraType type, Func<SpellAura, bool> test, bool incomingPositive)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is not null && aura.Type == type && test(aura) && Applies(holder.Spell, incomingPositive))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::IsImmuneToSchoolMask (Unit.cpp:10497-10508): any school-immunity aura covers a school of the mask,
    /// whatever its polarity. Used to absorb all damage of an immune unit.
    /// </summary>
    public static bool IsImmuneToSchoolMask(SpellSystem system, Unit unit, uint schoolMask)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        if (!system.ImmunityEnforcement)
        {
            return false;
        }

        if (system.CreatureImmunities is { } creatures && (creatures.SchoolImmuneMask(unit) & schoolMask) != 0)
        {
            return true;
        }

        foreach (SpellAura aura in LiveAuras(system, unit, AuraType.SchoolImmunity))
        {
            if (((uint)aura.MiscValue & schoolMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::IsImmuneToDamage (+ Creature override): damage-immunity auras and creature school masks
    /// block outright; school-immunity auras block when their polarity applies and the spell does not carry
    /// NO_SCHOOL_IMMUNITIES. A spell with NO_IMMUNITIES or ignoring restrictions is never blocked.
    /// </summary>
    public static bool IsImmuneToDamage(SpellSystem system, Unit target, uint schoolMask, SpellInfo? spell)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        if (!system.ImmunityEnforcement || (spell is not null && (spell.IgnoresImmunities() || IgnoresRestrictions(spell))))
        {
            return false;
        }

        if (system.CreatureImmunities is { } creatures && (creatures.SchoolImmuneMask(target) & schoolMask) != 0)
        {
            return true;
        }

        foreach (SpellAura aura in LiveAuras(system, target, AuraType.DamageImmunity))
        {
            if (((uint)aura.MiscValue & schoolMask) != 0)
            {
                return true;
            }
        }

        if (spell is not null && (spell.AttributesEx2 & (SpellAttributesEx2)SpellRuleFlags.Ex2NoSchoolImmunities) != 0)
        {
            return false;
        }

        bool incoming = spell is not null && spell.IsPositive;
        return Matches(system, target, AuraType.SchoolImmunity, a => ((uint)a.MiscValue & schoolMask) != 0, incoming);
    }

    /// <summary>
    /// vmangos Unit::IsImmuneToSchool (Unit.cpp:5629-5657), asked by the heal and energize ticks (SpellAuras.cpp:6031, 6221, 6270): a
    /// school-immunity aura covering a school of the spell blocks it when its polarity applies to <paramref name="incomingPositive"/>
    /// (vmangos passes the polarity of the ticking effect). A spell that purges immunity, ignores school immunities or ignores
    /// caster and target restrictions is never blocked, and an immunity granted by the spell itself does not count.
    /// </summary>
    public static bool IsImmuneToSchool(SpellSystem system, Unit target, SpellInfo spell, bool incomingPositive)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (!system.ImmunityEnforcement
            || (spell.AttributesEx & (SpellAttributesEx)SpellRuleFlags.ExImmunityPurgesEffect) != 0
            || (spell.AttributesEx2 & (SpellAttributesEx2)SpellRuleFlags.Ex2NoSchoolImmunities) != 0
            || IgnoresRestrictions(spell))
        {
            return false;
        }

        uint schoolMask = spell.SchoolMask();
        foreach (SpellAuraHolder holder in system.GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.Id == spell.Id) // "do not let itself immune out"
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.SchoolImmunity } && ((uint)aura.MiscValue & schoolMask) != 0 && Applies(holder.Spell, incomingPositive))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::IsImmuneToSpell (+ Creature override): dispel-type immunity (not for passive spells),
    /// school immunity (unless the spell purges immunity or ignores school immunity), mechanic immunity and
    /// mechanic-mask immunity; for creatures the static mechanic mask (spells from others only) and the static
    /// school mask (harmful spells only).
    /// </summary>
    public static bool IsImmuneToSpell(SpellSystem system, Unit target, SpellInfo spell, bool castOnSelf)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (!system.ImmunityEnforcement || spell.IgnoresImmunities() || IgnoresRestrictions(spell))
        {
            return false;
        }

        if (!castOnSelf && system.CreatureImmunities is { } creatures)
        {
            if (spell.Mechanic != 0 && (creatures.MechanicImmuneMask(target) & SpellMechanics.Mask(spell.Mechanic)) != 0)
            {
                return true;
            }

            if ((creatures.SchoolImmuneMask(target) & SpellSchoolMasks.Of(spell.School)) != 0 && !spell.IsPositive)
            {
                return true;
            }
        }

        bool incoming = spell.IsPositive;
        if (!spell.HasAttribute(SpellAttributes.Passive)
            && Matches(system, target, AuraType.DispelImmunity, a => a.MiscValue == (int)spell.Dispel, incoming))
        {
            return true;
        }

        if ((spell.AttributesEx & (SpellAttributesEx)SpellRuleFlags.ExImmunityPurgesEffect) == 0
            && (spell.AttributesEx2 & (SpellAttributesEx2)SpellRuleFlags.Ex2NoSchoolImmunities) == 0
            && Matches(system, target, AuraType.SchoolImmunity, a => ((uint)a.MiscValue & spell.SchoolMask()) != 0, incoming))
        {
            return true;
        }

        if (spell.Mechanic != 0)
        {
            if (Matches(system, target, AuraType.MechanicImmunity, a => a.MiscValue == (int)spell.Mechanic, incoming)
                || Matches(system, target, AuraType.MechanicImmunityMask, a => ((uint)a.MiscValue & SpellMechanics.Mask(spell.Mechanic)) != 0, incoming))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::IsImmuneToSpellEffect (+ Creature override): the effect's mechanic (the creature mask for
    /// spells from others, mechanic and mechanic-mask auras), effect immunity by effect id, and state immunity by
    /// the aura the effect applies. Summoned totems first apply their intrinsic override
    /// (Totem.cpp:180-217), including its self-cast and Shaman regeneration-family exceptions.
    /// </summary>
    public static bool IsImmuneToSpellEffect(SpellSystem system, Unit target, SpellInfo spell, int index, bool castOnSelf)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (!system.ImmunityEnforcement)
        {
            return false;
        }

        if (TotemImmunity.TryGetEffectImmunity(target, spell, index, castOnSelf, out bool totemImmune))
        {
            return totemImmune;
        }

        if (IgnoresRestrictions(spell))
        {
            return false;
        }

        SpellEffectInfo effect = spell.Effects[index];
        uint mechanic = effect.Mechanic;
        if (!castOnSelf && mechanic != 0 && system.CreatureImmunities is { } creatures
            && (creatures.MechanicImmuneMask(target) & SpellMechanics.Mask(mechanic)) != 0)
        {
            return true;
        }

        bool incoming = spell.IsPositive;
        if (!effect.IsEmpty && Matches(system, target, AuraType.EffectImmunity, a => a.MiscValue == (int)effect.Effect, incoming))
        {
            return true;
        }

        if (mechanic != 0
            && (Matches(system, target, AuraType.MechanicImmunity, a => a.MiscValue == (int)mechanic, incoming)
                || Matches(system, target, AuraType.MechanicImmunityMask, a => ((uint)a.MiscValue & SpellMechanics.Mask(mechanic)) != 0, incoming)))
        {
            return true;
        }

        return effect.Effect == SpellEffectName.ApplyAura && effect.AuraType != AuraType.None
            && Matches(system, target, AuraType.StateImmunity, a => a.MiscValue == (int)effect.AuraType, incoming);
    }
}
