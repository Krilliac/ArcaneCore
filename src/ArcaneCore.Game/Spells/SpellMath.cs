namespace ArcaneCore.Game.Spells;

/// <summary>Spell formulas shared by casts and auras.</summary>
public static class SpellMath
{
    /// <summary>Spell.dbc powerType value for health costs (vmangos POWER_HEALTH = -2, stored as 0xFFFFFFFE).</summary>
    public const int PowerHealth = -2;

    /// <summary>
    /// vmangos SpellCaster::CalculateSpellEffectValue (without spell mods and combo points):
    /// the caster level is clamped to [baseLevel, maxLevel] and made relative to spellLevel;
    /// value = basePoints + level * realPointsPerLevel; randomPoints = dieSides + level *
    /// dicePerLevel; randomPoints 0 or 1 adds baseDice, otherwise a uniform roll between baseDice
    /// and randomPoints (inclusive, either order). 1.12 stores BasePoints as "value - 1", so a
    /// BaseDice of 1 yields BasePoints + 1 for fixed values.
    /// </summary>
    public static int CalculateEffectValue(SpellInfo spell, SpellEffectInfo effect, byte casterLevel, Random random)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(random);

        int level = casterLevel;
        if (spell.MaxLevel > 0 && level > spell.MaxLevel)
        {
            level = (int)spell.MaxLevel;
        }
        else if (level < spell.BaseLevel)
        {
            level = (int)spell.BaseLevel;
        }

        level -= (int)spell.SpellLevel;

        float value = effect.BasePoints + (level * effect.RealPointsPerLevel);
        int randomPoints = (int)(effect.DieSides + (level * effect.DicePerLevel));
        int baseDice = effect.BaseDice;
        if (randomPoints is 0 or 1)
        {
            value += baseDice;
        }
        else
        {
            int low = Math.Min(baseDice, randomPoints);
            int high = Math.Max(baseDice, randomPoints);
            value += random.Next(low, high + 1);
        }

        return (int)value;
    }
}
