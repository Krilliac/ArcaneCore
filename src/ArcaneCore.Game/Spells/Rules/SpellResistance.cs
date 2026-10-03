namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The resistance-to-resist-chance conversion (vmangos SpellCaster::GetSpellResistChance,
/// SpellCaster.cpp:882-925, branch for client builds above 1.8.4 which is the 1.12 one).
/// </summary>
public static class SpellResistance
{
    /// <summary>The cap of the resist chance (and of the vulnerability), :904-905 and :920-921.</summary>
    public const float MaxChance = 0.75f;

    /// <summary>
    /// The resist chance of damage against a victim: positive 0..0.75, or negative -0.75..0 for a
    /// vulnerability (the victim has negative resistance).
    /// </summary>
    /// <param name="victimResistance">The victim's resistance in the school.</param>
    /// <param name="penetration">The caster's MOD_TARGET_RESISTANCE total (negative values penetrate).</param>
    /// <param name="skillMax">The caster's maximum skill against the victim (level x 5), the vulnerability divisor (floored at 100).</param>
    /// <param name="casterLevel">The caster's own level (the conversion divides by it).</param>
    /// <param name="innateLevelDiff">Victim level minus caster level as each sees the other (boss-relative).</param>
    /// <param name="applyInnate">Whether the creature innate resistance applies (creature victim, damage rather than hit).</param>
    public static float Chance(int victimResistance, int penetration, int skillMax, int casterLevel, int innateLevelDiff, bool applyInnate)
    {
        float resistance = victimResistance + penetration;

        // 1.12: penetration alone can never push a non-negative resistance below zero (:896-898).
        if (resistance < 0.0f && victimResistance >= 0)
        {
            resistance = 0.0f;
        }

        if (resistance < 0.0f)
        {
            float skill = Math.Max(skillMax, 100);
            return Math.Max(resistance / skill, -MaxChance);
        }

        int level = Math.Max(casterLevel, 1);
        if (applyInnate)
        {
            // int(8 * diff * level / 63): truncated before the conversion (:912-915).
            resistance += (int)(8.0f * innateLevelDiff * level / 63.0f);
        }

        resistance *= 0.15f / level;
        return Math.Clamp(resistance, 0.0f, MaxChance);
    }
}
