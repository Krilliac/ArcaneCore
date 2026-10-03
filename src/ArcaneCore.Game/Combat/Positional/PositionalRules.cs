using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Front/behind rules for opener abilities. They wrap the existing <see cref="MapCombat.HasInArc"/> and
/// <see cref="CombatConstants.DefaultArc"/> (a 180 degree arc) so the stealth detection formula and the cast checks
/// share one definition of "in front of".
/// </summary>
public static class PositionalRules
{
    /// <summary>vmangos SPELL_ATTR_EX_UNK9 (AttributesEx 0x200): part of the behind-only shape (SpellEntry.h:911).</summary>
    public const uint BehindOnlyAttributesEx = 0x200;

    /// <summary>vmangos IsFromBehindOnlySpell AttributesEx2 value (SpellEntry.h:911).</summary>
    public const uint BehindOnlyAttributesEx2 = 0x100000;

    /// <summary>Spell.dbc Attributes of the "target must be facing you" shape, Gouge (vmangos Spell.cpp:5646).</summary>
    public const uint FacingRequiredAttributes = 0x150010;

    /// <summary>vmangos WorldObject::HasInArc(target, M_PI): is <paramref name="target"/> within the 180 degree front arc of <paramref name="source"/>?</summary>
    public static bool HasInArc(WorldObject source, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return MapCombat.HasInArc(source, target, CombatConstants.DefaultArc);
    }

    /// <summary>
    /// vmangos Unit::IsBehindTarget (Unit.cpp:2806-2821): <paramref name="attacker"/> is behind <paramref name="target"/> when the
    /// attacker is outside the target's front arc. With <paramref name="strict"/> a creature that is currently fighting the attacker
    /// is treated as facing them (it always faces its victim) unless it is stunned, confused, fleeing or possessed.
    /// The creature-is-casting-at-a-target (m_castingTargetGuid) exception is not modelled: creature spell casting does not exist yet.
    /// </summary>
    public static bool IsBehindTarget(Unit attacker, Unit target, bool strict)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        if (strict && target is ICombatCreature && ReferenceEquals(target.Combat.Victim, attacker)
            && (target.UnitFlags & (UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing | UnitFlags.Possessed)) == 0)
        {
            return false;
        }

        return !HasInArc(target, attacker);
    }

    /// <summary>
    /// vmangos SpellEntry::IsFromBehindOnlySpell (SpellEntry.h:909-912): AttributesEx2 exactly 0x100000 with AttributesEx bit 0x200.
    /// vmangos' second clause (the hand-maintained SPELL_CUSTOM_BEHIND_TARGET table, SpellDefines.h:1008) is not part of the data
    /// this repo imports, so spells that only that table marks are not recognised (docs/areas/rogue.md).
    /// </summary>
    public static bool IsFromBehindOnly(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (uint)spell.AttributesEx2 == BehindOnlyAttributesEx2 && ((uint)spell.AttributesEx & BehindOnlyAttributesEx) == BehindOnlyAttributesEx;
    }

    /// <summary>vmangos Spell.cpp:5646: Attributes exactly 0x150010 means the target must be facing the caster (Gouge).</summary>
    public static bool IsFacingRequired(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (uint)spell.Attributes == FacingRequiredAttributes;
    }
}
