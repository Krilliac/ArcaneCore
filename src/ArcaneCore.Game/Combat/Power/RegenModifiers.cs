namespace ArcaneCore.Game.Combat;

/// <summary>
/// Player regeneration with the auras that change it, as pure functions (vmangos at the 1.12.1 branch).
/// <list type="bullet">
/// <item>Mana, <c>Player::UpdateManaRegen</c> (StatSystem.cpp:642-661): the spirit regen times the MOD_POWER_REGEN_PERCENT factor (Evocation) plus a fifth of the
/// MOD_POWER_REGEN total per second (Drink, mp5); inside the five second window only mp5 plus the spirit part times min(100, the MOD_MANA_REGEN_INTERRUPT
/// total) percent (Mage Armor 30, Evocation 100); <c>Player::Regenerate</c> (Player.cpp:2282-2297) turns it into a tick: <c>perSecond * Rate.Mana * 2</c>.</item>
/// <item>Health, <c>Player::RegenerateHealth</c> (Player.cpp:2366-2402): a polymorphed unit gets a tenth of its maximum; otherwise out of combat (or with
/// MOD_REGEN_DURING_COMBAT) the spirit regen, times (100 + amount) / 100 of each MOD_HEALTH_REGEN_PERCENT aura out of combat (Health Funnel -100 stops it)
/// or times the MOD_REGEN_DURING_COMBAT total / 100 in combat, times 1.5 when not standing, plus the Food auras (MOD_REGEN: amount * (2000 / interval))
/// out of combat; every case then adds <c>2 * (MOD_HEALTH_REGEN_IN_COMBAT total / 5)</c> (Demon Armor).</item>
/// </list>
/// </summary>
public static class RegenModifiers
{
    /// <summary>One regeneration tick in ms (vmangos REGEN_TIME_PLAYER_FULL).</summary>
    public const float TickMs = CombatConstants.PlayerRegenIntervalMs;

    /// <summary>The period of a MOD_REGEN aura without an amplitude (vmangos HandleModRegen).</summary>
    public const uint DefaultModRegenPeriodMs = 5000;

    /// <summary>The mana per second (vmangos m_modManaRegen, or m_modManaRegenInterrupt inside the five second window).</summary>
    public static float ManaPerSecond(float spiritRegen, float percentFactor, int powerRegenTotal, int interruptTotal, bool recentCast)
    {
        float spirit = spiritRegen * percentFactor;
        float mp5 = powerRegenTotal / 5.0f;
        int interrupt = Math.Min(interruptTotal, 100);
        return recentCast ? mp5 + (spirit * interrupt / 100.0f) : mp5 + spirit;
    }

    /// <summary>
    /// The health of one regeneration tick before the fractional carry (vmangos Player::RegenerateHealth up to <c>addValue += m_carryHealthRegen</c>).
    /// <paramref name="healthRate"/> is Rate.Health (CONFIG_FLOAT_RATE_HEALTH): it scales the spirit part and the always-on MOD_HEALTH_REGEN_IN_COMBAT
    /// part (Player.cpp:2372,2396).
    /// </summary>
    public static float HealthPerTick(
        float spiritRegen,
        float maxHealth,
        bool inCombat,
        bool standing,
        bool polymorphed,
        bool hasRegenDuringCombat,
        int regenDuringCombatTotal,
        IReadOnlyList<int> healthRegenPercent,
        IReadOnlyList<RegenAura> foodAuras,
        int healthRegenInCombatTotal,
        float healthRate = 1f)
    {
        ArgumentNullException.ThrowIfNull(healthRegenPercent);
        ArgumentNullException.ThrowIfNull(foodAuras);
        float add = 0f;
        if (polymorphed)
        {
            add = maxHealth / 10;
        }
        else if (!inCombat || hasRegenDuringCombat)
        {
            add = spiritRegen * healthRate;
            if (!inCombat)
            {
                foreach (int percent in healthRegenPercent)
                {
                    add *= (100.0f + percent) / 100.0f;
                }
            }
            else if (hasRegenDuringCombat)
            {
                add *= regenDuringCombatTotal / 100.0f;
            }

            if (!standing)
            {
                add *= 1.5f;
            }

            if (!inCombat)
            {
                foreach (RegenAura food in foodAuras)
                {
                    // vmangos HandleModRegen (SpellAuras.cpp:4795-4803): an amplitude of 0 means a 5000 ms period.
                    uint period = food.PeriodMs == 0 ? DefaultModRegenPeriodMs : food.PeriodMs;
                    add += food.Amount * (TickMs / period);
                }
            }
        }

        // The always-on part, including combat; the function runs every 2 seconds.
        return add + (healthRate * 2.0f * (healthRegenInCombatTotal / 5.0f));
    }
}
