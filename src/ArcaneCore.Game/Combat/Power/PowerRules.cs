using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The retail power economy as pure functions (vmangos at the 1.12.1 branch). Power fields hold the displayed
/// value x 10 for rage ("raw").
/// </summary>
public static class PowerRules
{
    /// <summary>Berserker Rage (vmangos Player.cpp:2258: HasAura(18499, EFFECT_INDEX_0)).</summary>
    public const uint BerserkerRageSpell = 18499;

    /// <summary>The taken-damage multiplier of Berserker Rage (Player.cpp:2259).</summary>
    public const float BerserkerRageRageTakenFactor = 1.3f;

    /// <summary>
    /// The fraction of the cost refunded on a miss (vmangos Spell.cpp:1272-1284 <c>lroundf(m_powerCost * 0.82f)</c>;
    /// the nearby comment says 80%, the code uses 0.82).
    /// </summary>
    public const float MissRefundFraction = 0.82f;

    /// <summary>Raw rage lost per regeneration tick before the rate (vmangos Player.cpp:2312-2313: <c>20 * RageDecreaseRate</c>).</summary>
    public const float RageDecayPerTickRaw = 20.0f;

    /// <summary>Raw energy gained per regeneration tick before the rate (vmangos Player.cpp:2319-2320).</summary>
    public const float EnergyPerTickRaw = 20.0f;

    /// <summary>
    /// vmangos Player::RewardRage (Player.cpp:2243-2267, Kalgan's formula): conversion = 0.0091107836 L^2 +
    /// 3.225598133 L + 4.2652911; dealing earns damage / conversion x 7.5, taking x 2.5 (x 1.3 under Berserker Rage);
    /// the result is multiplied by Rate.Rage.Income and stored x 10.
    /// </summary>
    public static uint RageFromDamage(byte level, uint damage, bool dealt, bool berserkerRage, float rateIncome)
    {
        float conversion = (float)((0.0091107836 * level * level) + (3.225598133 * level)) + 4.2652911f;
        float add;
        if (dealt)
        {
            add = damage / conversion * 7.5f;
        }
        else
        {
            add = damage / conversion * 2.5f;
            if (berserkerRage)
            {
                add *= BerserkerRageRageTakenFactor;
            }
        }

        add *= rateIncome;
        return (uint)(add * 10);
    }

    /// <summary>
    /// The raw rage that decays in one regeneration tick (vmangos Player::Regenerate, Player.cpp:2310-2331):
    /// <c>20 * Rate.Rage.Loss</c>, scaled by every MOD_POWER_REGEN_PERCENT aura, truncated.
    /// </summary>
    public static uint RageDecayPerTick(float rateLoss, float regenFactor) => (uint)(RageDecayPerTickRaw * rateLoss * regenFactor);

    /// <summary>The raw energy gained in one regeneration tick (vmangos Player.cpp:2315-2330): <c>20 * Rate.Energy</c> scaled by the regen auras.</summary>
    public static uint EnergyPerTick(float rateEnergy, float regenFactor) => (uint)(EnergyPerTickRaw * rateEnergy * regenFactor);

    /// <summary>Mana gained in one tick from the per-spirit regen (vmangos Player.cpp:2289-2296: <c>regen * Rate.Mana * 2</c>).</summary>
    public static uint ManaPerTick(float regenPerSecond, float rateMana) => (uint)(regenPerSecond * rateMana * 2.0f);

    /// <summary>
    /// The power returned to the caster of an <see cref="SpellAttributesExCombat.DiscountPowerOnMiss"/> spell when
    /// the target avoided it (vmangos Spell.cpp:1267-1285): 82% of the cost as energy on a miss, dodge, parry or
    /// immune result, and as rage on a dodge or parry only. Null when nothing is refunded.
    /// </summary>
    public static (PowerType Power, int Amount)? Refund(SpellInfo spell, SpellMissInfo miss, uint powerCost)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (!spell.HasAttribute(SpellAttributesExCombat.DiscountPowerOnMiss))
        {
            return null;
        }

        int amount = (int)MathF.Round(powerCost * MissRefundFraction, MidpointRounding.AwayFromZero);
        if (spell.PowerType == (int)PowerType.Energy
            && miss is SpellMissInfo.Miss or SpellMissInfo.Dodge or SpellMissInfo.Parry or SpellMissInfo.Immune or SpellMissInfo.Immune2)
        {
            return (PowerType.Energy, amount);
        }

        if (spell.PowerType == (int)PowerType.Rage && miss is SpellMissInfo.Parry or SpellMissInfo.Dodge)
        {
            return (PowerType.Rage, amount);
        }

        return null;
    }
}
