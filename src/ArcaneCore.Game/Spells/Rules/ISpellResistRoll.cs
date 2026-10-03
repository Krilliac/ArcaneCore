using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Optional capability of an <see cref="ISpellCombatRules"/>: the signed resist roll of vmangos
/// Unit::CalculateDamageAbsorbAndResist, used by <see cref="SpellSystem"/> instead of the unsigned
/// <see cref="ISpellCombatRules.RollPartialResist"/> when the installed rules offer it. It knows whether the damage is
/// periodic (damage over time is resisted a tenth as often, Unit.cpp:2406-2425) and reports a vulnerability as extra damage.
/// </summary>
public interface ISpellResistRoll
{
    /// <summary>
    /// The part of <paramref name="damage"/> resisted: positive for a partial resist, negative for the extra damage
    /// of a vulnerability (Unit.cpp:2229-2230), 0 for none.
    /// </summary>
    int RollResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage, bool periodic);
}