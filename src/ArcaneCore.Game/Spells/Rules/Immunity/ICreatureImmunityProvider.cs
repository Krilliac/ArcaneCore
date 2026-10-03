using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// The static immunities of a creature (creature_template mechanic_immune_mask / school_immune_mask, vmangos
/// Creature::IsImmuneToSpell, Creature.cpp:2438-2452). The creature data area implements it; with none installed
/// creatures are immune to nothing but what their auras grant.
/// </summary>
public interface ICreatureImmunityProvider
{
    /// <summary>Bit <c>mechanic - 1</c> set for every <see cref="SpellMechanic"/> the unit is immune to (0 for non-creatures).</summary>
    uint MechanicImmuneMask(Unit unit);

    /// <summary>Bit <c>school</c> set for every <see cref="SpellSchool"/> the unit is immune to (0 for non-creatures).</summary>
    uint SchoolImmuneMask(Unit unit);
}
