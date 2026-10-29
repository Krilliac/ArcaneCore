using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// The static immunities of a creature: creature_template mechanic_immune_mask / school_immune_mask (vmangos Creature::IsImmuneToSpell,
/// Creature.cpp:2438-2452) and the active creature_immunities set (mangos-classic Creature::UpdateImmunitiesSet, Creature.cpp:2462-2481).
/// <see cref="CreatureDataImmunities"/> serves the world database's rows; a script module may wrap it (see <c>TwinEmperorSpellModule</c>)
/// and must forward every member to the provider it wraps. With none installed creatures are immune to nothing but what their auras grant.
/// </summary>
public interface ICreatureImmunityProvider
{
    /// <summary>Bit <c>mechanic - 1</c> set for every <see cref="SpellMechanic"/> the unit is immune to (0 for non-creatures).</summary>
    uint MechanicImmuneMask(Unit unit);

    /// <summary>Bit <c>school</c> set for every <see cref="SpellSchool"/> the unit is immune to (0 for non-creatures).</summary>
    uint SchoolImmuneMask(Unit unit);

    /// <summary>
    /// Bit <c>school</c> set for every school whose damage the unit absorbs entirely (a <c>creature_immunities</c> row of type damage,
    /// mangos-classic IMMUNITY_DAMAGE); unlike <see cref="SchoolImmuneMask"/> it does not make harmful spells of the school immune. 0 by default.
    /// </summary>
    uint DamageImmuneMask(Unit unit) => 0;

    /// <summary>Bit <c>dispel type</c> (<see cref="DispelType"/>) set for every dispel type the unit is immune to (0 by default).</summary>
    uint DispelImmuneMask(Unit unit) => 0;

    /// <summary>Whether the unit is immune to spell effect <paramref name="effect"/> (a <c>creature_immunities</c> row of type effect). False by default.</summary>
    bool IsImmuneToEffect(Unit unit, SpellEffectName effect) => false;

    /// <summary>Whether the unit is immune to effects that apply aura <paramref name="aura"/> (a row of type state). False by default.</summary>
    bool IsImmuneToAuraState(Unit unit, AuraType aura) => false;
}
