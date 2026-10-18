using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Optional capability of an <see cref="ISpellCombatRules"/>: the signed resist roll of damage that no spell deals, vmangos
/// Unit::CalculateDamageAbsorbAndResist with no spell entry (Unit.cpp:1920-1955), which environmental damage asks for
/// (Player::EnvironmentalDamage, Player.cpp:719-736). Without a spell nothing makes the damage binary or unresistable, and it
/// is direct damage, so the damage over time tenth (Unit.cpp:2406-2425) never applies.
/// </summary>
public interface ISchoolResistRoll
{
    /// <summary>
    /// The part of <paramref name="damage"/> of <paramref name="school"/> resisted: positive for a partial resist, negative
    /// for the extra damage of a vulnerability (Unit.cpp:1948-1953), 0 for none.
    /// </summary>
    int RollResist(SpellSystem system, Unit caster, Unit target, SpellSchool school, uint damage);
}
