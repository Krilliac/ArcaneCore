using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The stat-driven spell crit chance of a unit (vmangos Player::UpdateSpellCritChance / Unit::GetSpellCritPercent).
/// The stats area implements it (<c>IntellectSpellCritSource</c>, installed by the spell rules feature); <see cref="Flat"/> is the fallback when none is set.
/// </summary>
public interface ISpellCritSource
{
    /// <summary>
    /// The same flat chance for every unit: vmangos gives non-players 5% (Unit::GetSpellCritFromIntellect,
    /// Unit.cpp:2598-2640, "MUST BE CHECKED" for players, which the intellect source replaces).
    /// </summary>
    static ISpellCritSource Flat(float percent) => new FlatSource(percent);

    /// <summary>The caster's base spell crit chance for <paramref name="school"/> in percent, before auras.</summary>
    float SpellCritPercent(Unit caster, SpellSchool school);

    private sealed class FlatSource(float percent) : ISpellCritSource
    {
        public float SpellCritPercent(Unit caster, SpellSchool school) => percent;
    }
}
