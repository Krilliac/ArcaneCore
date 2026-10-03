using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Optional capability of an <see cref="ISpellCombatRules"/>: the exact critical-strike amounts (talent
/// bonus and creature-type multiplier included), used by <see cref="SpellSystem"/> instead of the plain
/// <see cref="ISpellCombatRules.CritMultiplier"/> when the installed rules offer it.
/// </summary>
public interface ISpellCritAmounts
{
    /// <summary>The damage of a critical hit that would have dealt <paramref name="damage"/> (vmangos SpellCriticalDamageBonus).</summary>
    uint CriticalDamage(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage);

    /// <summary>The healing of a critical heal that would have healed <paramref name="amount"/> (vmangos SpellCriticalHealingBonus).</summary>
    uint CriticalHeal(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint amount);
}
