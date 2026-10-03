namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The arithmetic of the magic spell hit chance (vmangos SpellCaster::MagicSpellHitChance,
/// SpellCaster.cpp:795-880). The modifiers (auras, spell mods) are gathered by
/// <see cref="VanillaSpellCombatRules.MagicHitPercent"/>; this class owns the level curve, the floor and the clamp.
/// </summary>
public static class MagicHitChance
{
    /// <summary>Lowest final hit chance in percent (vmangos clamps the scaled value to 100 of 10000).</summary>
    public const float MinPercent = 1.0f;

    /// <summary>Highest final hit chance in percent (vmangos clamps the scaled value to 9900 of 10000).</summary>
    public const float MaxPercent = 99.0f;

    /// <summary>
    /// The base chance from the level difference (target level minus caster level, both as each sees the
    /// other): 96 minus the difference while it is below 3, otherwise 94 minus 7 (player target) or
    /// 11 (creature) per level beyond 2 (SpellCaster.cpp:803,821-827); never below
    /// <paramref name="floorPercent"/> (the 22% floor from a classic duel test, :829-834).
    /// </summary>
    public static float Base(int levelDiff, bool victimIsPlayer, float floorPercent)
    {
        float chance = levelDiff < 3
            ? 96 - levelDiff
            : 94 - ((levelDiff - 2) * (victimIsPlayer ? 7 : 11));
        return chance < floorPercent ? floorPercent : chance;
    }

    /// <summary>
    /// The final chance: binary spells are scaled by <c>1 - resistChance</c> (SpellCaster.cpp:869-874),
    /// then the result is clamped to <see cref="MinPercent"/>..<see cref="MaxPercent"/> (:877-878).
    /// </summary>
    public static float Finish(float chance, bool binary, float resistChance)
    {
        if (binary)
        {
            chance *= 1.0f - resistChance;
        }

        return Math.Clamp(chance, MinPercent, MaxPercent);
    }
}
