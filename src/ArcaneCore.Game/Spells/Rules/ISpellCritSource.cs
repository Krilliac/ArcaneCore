using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The stat-driven spell crit chance of a unit (vmangos Player::UpdateSpellCritChance / Unit::GetSpellCritPercent).
/// The stats area implements it; <see cref="Flat"/> is the interim a player gets before that area merges.
/// </summary>
public interface ISpellCritSource
{
    /// <summary>A flat chance for players and none for any other unit.</summary>
    static ISpellCritSource Flat(float playerPercent) => new FlatSource(playerPercent);

    /// <summary>The caster's base spell crit chance for <paramref name="school"/> in percent, before auras.</summary>
    float SpellCritPercent(Unit caster, SpellSchool school);

    private sealed class FlatSource(float playerPercent) : ISpellCritSource
    {
        public float SpellCritPercent(Unit caster, SpellSchool school) => caster is Player ? playerPercent : 0f;
    }
}
