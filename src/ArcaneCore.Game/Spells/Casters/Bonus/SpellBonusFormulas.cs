namespace ArcaneCore.Game.Spells.Casters.Bonus;

/// <summary>
/// The combiners of vmangos' spell power pipeline over plain numbers (no unit access): the
/// caster side (SpellDamageBonusDone / SpellHealingBonusDone) and the target side
/// (SpellDamageBonusTaken / SpellHealingBonusTaken). Callers gather the aura inputs; this class
/// only does the arithmetic so it can be tested against oracle values.
/// Sources: D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:1457-1526, 1560-1700, 1737-1803 and
/// D:\refs\vmangos\src\game\Objects\Unit.cpp:5175-5195, 5328-5385.
/// </summary>
public static class SpellBonusFormulas
{
    /// <summary>
    /// vmangos SpellBonusWithCoeffs: <c>total + benefit * coefficient * levelPenalty</c> when the benefit is
    /// non-zero, <paramref name="total"/> otherwise.
    /// </summary>
    public static float WithCoefficient(float total, float benefit, EffectiveCoefficient coefficient)
        => benefit != 0 ? total + (benefit * coefficient.Coefficient * coefficient.LevelPenalty) : total;

    /// <summary>
    /// Caster side for damage and healing: <c>(amount + (flat + benefit * coeff * penalty) * stack) * percent</c>,
    /// floored at 0. <paramref name="flat"/> carries the amounts that bypass the coefficient (damage done versus
    /// creature types); <paramref name="stack"/> is the aura stack count (1 for direct effects).
    /// </summary>
    public static float AmountDone(float amount, float flat, float benefit, EffectiveCoefficient coefficient, uint stack, float percentMultiplier)
    {
        float total = WithCoefficient(flat, benefit, coefficient);
        float result = (amount + (total * (int)stack)) * percentMultiplier;
        return result > 0 ? result : 0;
    }

    /// <summary>
    /// Target side for damage and healing: the flat benefit (Amplify Magic, Dampen Magic, +healing taken) goes through
    /// the same coefficient and stack, a negative flat amount may remove at most half of the amount, and the percent
    /// multiplier applies last; floored at 0.
    /// </summary>
    public static float AmountTaken(float amount, float takenBenefit, EffectiveCoefficient coefficient, uint stack, float percentMultiplier)
    {
        float flat = WithCoefficient(0, takenBenefit, coefficient) * (int)stack;
        if (flat < 0 && -flat > amount / 2)
        {
            flat = -(amount / 2);
        }

        float result = (amount + flat) * percentMultiplier;
        return result > 0 ? result : 0;
    }

    /// <summary>
    /// Healing taken percent (Unit::SpellHealingBonusTaken): the most negative and the most positive
    /// MOD_HEALING_PCT amounts apply separately (never summed, never every aura); 0 means none.
    /// </summary>
    public static float HealingTakenPercentMultiplier(int mostNegative, int mostPositive)
    {
        float multiplier = 1.0f;
        if (mostNegative != 0)
        {
            multiplier *= (100.0f + mostNegative) / 100.0f;
        }

        if (mostPositive != 0)
        {
            multiplier *= (100.0f + mostPositive) / 100.0f;
        }

        return multiplier;
    }

    /// <summary>Percent auras (value in percent) combine multiplicatively: <c>prod (100 + v) / 100</c>.</summary>
    public static float MultiplicativePercent(IEnumerable<int> percents)
    {
        ArgumentNullException.ThrowIfNull(percents);
        float multiplier = 1.0f;
        foreach (int percent in percents)
        {
            multiplier *= (100.0f + percent) / 100.0f;
        }

        return multiplier;
    }

    /// <summary>SPELL_ATTR_EX3_IGNORE_CASTER_MODIFIERS (vmangos SpellDefines.h:978): done modifiers are skipped.</summary>
    public static bool IgnoresCasterModifiers(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (spell.AttributesEx3 & CasterAttributes.Ex3IgnoreCasterModifiers) != 0;
    }

    /// <summary>SPELL_ATTR_EX4_IGNORE_DAMAGE_TAKEN_MODIFIERS (vmangos SpellDefines.h:994): damage taken modifiers are skipped.</summary>
    public static bool IgnoresDamageTakenModifiers(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return (spell.AttributesEx4 & CasterAttributes.Ex4IgnoreDamageTakenModifiers) != 0;
    }
}
