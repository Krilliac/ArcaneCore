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

        if (casterLevel > 0 && ShouldScaleWithCreatureLevel(spell, effect))
        {
            value *= CreatureLevelScaleFactor(casterLevel, spell.SpellLevel);
        }

        return (int)value;
    }

    /// <summary>
    /// vmangos <c>SpellCaster::CalculateSpellEffectValue</c> (<c>SpellCaster.cpp:1215-1218</c>):
    /// <c>SPELL_ATTR_SCALES_WITH_CREATURE_LEVEL</c> (<c>0x00080000</c>) with a positive <c>spellLevel</c> scales every effect except
    /// <c>SPELL_EFFECT_WEAPON_PERCENT_DAMAGE</c>, <c>SPELL_EFFECT_KNOCK_BACK</c>, and <c>SPELL_AURA_MOD_DECREASE_SPEED</c>.
    /// </summary>
    public static bool ShouldScaleWithCreatureLevel(SpellInfo spell, SpellEffectInfo effect)
        => ((uint)spell.Attributes & Casters.CasterAttributes.ScalesWithCreatureLevel) != 0
            && spell.SpellLevel > 0
            && effect.Effect != SpellEffectName.WeaponPercentDamage
            && effect.Effect != SpellEffectName.KnockBack
            && (effect.Effect != SpellEffectName.ApplyAura || effect.AuraType != AuraType.ModDecreaseSpeed);

    /// <summary>
    /// vmangos <c>SpellCaster::CalculateSpellEffectValue</c> (<c>SpellCaster.cpp:1220-1224</c>):
    /// <c>CLSPowerCreature(1, casterLevel) / CLSPowerSpell(1, spellLevel)</c> from <c>creature_classlevelstats</c> class 1 <c>melee_damage</c>.
    /// </summary>
    public static float CreatureLevelScaleFactor(uint casterLevel, uint spellLevel)
    {
        if (casterLevel == 0 || spellLevel == 0) return 1f;
        int casterIdx = Math.Clamp((int)casterLevel, 1, CreatureClass1MeleeDamage.Length) - 1;
        int spellIdx = Math.Clamp((int)spellLevel, 1, CreatureClass1MeleeDamage.Length) - 1;
        return CreatureClass1MeleeDamage[casterIdx] / CreatureClass1MeleeDamage[spellIdx];
    }

    // vmangos creature_classlevelstats (class = 1, levels 1..63) melee_damage column (20220610151831_world.sql:34-96).
    private static ReadOnlySpan<float> CreatureClass1MeleeDamage =>
    [
        1.57945f, 2.31493f, 3.25816f, 4.50155f, 6.0127f, 8.00805f, 10.3671f, 12.0481f, 13.8113f, 15.2392f,
        17.199f, 19.6717f, 21.4254f, 23.6166f, 25.7096f, 27.1742f, 29.1278f, 30.5059f, 32.3644f, 34.2057f,
        35.4908f, 37.1973f, 38.4533f, 40.1049f, 41.657f, 42.9703f, 44.696f, 45.9923f, 47.6437f, 49.3875f,
        50.6313f, 52.1686f, 53.4785f, 55.0217f, 56.5113f, 57.8888f, 59.7047f, 61.252f, 63.1787f, 65.1712f,
        68.2851f, 72.5719f, 77.4149f, 83.8275f, 87.9851f, 89.791f, 92.043f, 93.8584f, 96.0594f, 98.4032f,
        100.32f, 102.77f, 104.738f, 107.125f, 109.366f, 111.526f, 113.879f, 116.063f, 118.431f, 120.906f,
        123.205f, 125.504f, 127.722f,
    ];
}
