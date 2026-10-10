using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// vmangos Player::Update → RegenerateAll every 2 s while alive: health and rage decay only
    /// out of combat, then energy and mana.
    /// </summary>
    private void UpdatePlayerRegen(Player player, uint diff)
    {
        UnitCombat c = player.Combat;
        if (!IsAliveState(player))
        {
            c.RegenTimer = 0;
            return;
        }

        c.RegenTimer -= (int)diff;
        if (c.RegenTimer > 0)
        {
            return;
        }

        CombatEnvironment power = CombatEnvironment.For(_world);
        bool inCombat = c.IsInCombat;

        // vmangos Player::RegenerateAll (Player.cpp:2272-2281): health also regenerates in combat with MOD_REGEN_DURING_COMBAT, MOD_HEALTH_REGEN_IN_COMBAT
        // (Demon Armor) or while polymorphed; rage still only decays out of combat.
        if (!inCombat || power.HasAuraType(player, AuraType.ModRegenDuringCombat) || power.HasAuraType(player, AuraType.ModHealthRegenInCombat)
            || power.IsPolymorphed(player))
        {
            RegenerateHealth(player, power, inCombat);

            // vmangos Player::RegenerateAll (Player.cpp:2278): rage only decays without SPELL_AURA_INTERRUPT_REGEN (Bloodrage).
            if (!inCombat && !power.HasAuraType(player, AuraType.InterruptRegen))
            {
                RegeneratePower(player, PowerType.Rage, power);
            }
        }

        RegeneratePower(player, PowerType.Energy, power);
        RegeneratePower(player, PowerType.Mana, power);
        c.RegenTimer += CombatConstants.PlayerRegenIntervalMs;
    }

    /// <summary>
    /// vmangos Player::RegenerateHealth (Player.cpp:2366-2402): GetRegenHPPerSpirit, ×1.5 when not
    /// standing, shaped by the regeneration auras (<see cref="RegenModifiers.HealthPerTick"/>: polymorph, Health Funnel, Food,
    /// Demon Armor, MOD_REGEN_DURING_COMBAT); fractions carry over to the next tick.
    /// </summary>
    private static void RegenerateHealth(Player player, CombatEnvironment environment, bool inCombat)
    {
        uint cur = player.Health;
        uint max = player.MaxHealth;
        if (cur >= max)
        {
            return;
        }

        UnitCombat c = player.Combat;
        float add = RegenModifiers.HealthPerTick(
            RegenHealthPerSpirit(player.Class, player.GetUInt32(UpdateFields.UnitFieldStat0 + 4)),
            max,
            inCombat,
            IsStandingUp(player),
            environment.IsPolymorphed(player),
            environment.HasAuraType(player, AuraType.ModRegenDuringCombat),
            environment.GetTotalAuraModifier(player, AuraType.ModRegenDuringCombat),
            [.. environment.GetRegenAuras(player, AuraType.ModHealthRegenPercent).Select(a => a.Amount)],
            environment.GetRegenAuras(player, AuraType.ModRegen),
            environment.GetTotalAuraModifier(player, AuraType.ModHealthRegenInCombat),
            environment.Options.RateHealth);

        add += c.HealthRegenCarry;
        c.HealthRegenCarry = add - (int)add;
        uint gain = (uint)Math.Max(0, (int)add);
        player.Health = Math.Min(max, cur + gain);
    }

    /// <summary>
    /// vmangos Player::Regenerate: mana GetRegenMPPerSpirit × 2 per tick (0 within five seconds
    /// of spending mana), rage −20 (2 rage) per tick, energy +20.
    /// </summary>
    private static void RegeneratePower(Player player, PowerType power, CombatEnvironment environment)
    {
        uint cur = GetPower(player, power);
        uint max = GetMaxPower(player, power);

        // Rates and MOD_POWER_REGEN_PERCENT auras (not for mana): vmangos Player::Regenerate, Player.cpp:2292-2328.
        CombatOptions options = environment.Options;
        float regenFactor = power == PowerType.Mana ? 1.0f : environment.GetPowerRegenFactor(player, power);
        uint add = power switch
        {
            // vmangos UpdateManaRegen: the percent aura on the spirit part, mp5 (Drink), the interrupt percentage inside the five second window.
            PowerType.Mana => PowerRules.ManaPerTick(
                Math.Max(
                    0f,
                    RegenModifiers.ManaPerSecond(
                        RegenManaPerSpirit(player.Class, player.GetUInt32(UpdateFields.UnitFieldStat0 + 4)),
                        environment.GetPowerRegenFactor(player, PowerType.Mana),
                        environment.GetTotalAuraModifierByMisc(player, AuraType.ModPowerRegen, (int)PowerType.Mana),
                        environment.GetTotalAuraModifier(player, AuraType.ModManaRegenInterrupt),
                        player.Combat.LastManaUseTimer > 0)),
                options.RateMana),
            PowerType.Rage => PowerRules.RageDecayPerTick(options.RateRageLoss, regenFactor),
            PowerType.Energy => PowerRules.EnergyPerTick(options.RateEnergy, regenFactor),
            _ => 0u,
        };

        if (power != PowerType.Rage)
        {
            cur = Math.Min(max, cur + add);
        }
        else
        {
            cur = cur <= add ? 0 : cur - add;
        }

        SetPower(player, power, cur);
    }

    /// <summary>vmangos Unit::GetRegenHPPerSpirit (per 2 s tick), floored at 0.</summary>
    public static float RegenHealthPerSpirit(Class @class, float spirit)
    {
        float regen = @class switch
        {
            Class.Druid => (spirit * 0.11f) + 1f,
            Class.Hunter => (spirit * 0.43f) - 5.5f,
            Class.Mage => (spirit * 0.11f) + 1f,
            Class.Paladin => spirit * 0.25f,
            Class.Priest => (spirit * 0.15f) + 1.4f,
            Class.Rogue => (spirit * 0.84f) - 13f,
            Class.Shaman => (spirit * 0.28f) - 3.6f,
            Class.Warlock => (spirit * 0.12f) + 1.5f,
            Class.Warrior => (spirit * 1.26f) - 22.6f,
            _ => 0f,
        };
        return Math.Max(0f, regen);
    }

    /// <summary>vmangos Unit::GetRegenMPPerSpirit: per-tick values halved ("given per tick which occurs every 2 seconds").</summary>
    public static float RegenManaPerSpirit(Class @class, float spirit)
    {
        float add = @class switch
        {
            Class.Druid or Class.Hunter or Class.Paladin or Class.Warlock => (spirit / 5f) + 15f,
            Class.Mage or Class.Priest => (spirit / 4f) + 12.5f,
            Class.Shaman => (spirit / 5f) + 17f,
            _ => 0f,
        };
        return add / 2.0f;
    }

    /// <summary>
    /// vmangos Creature::RegenerateAll every 5 s (Objects/Creature.cpp:1087-1100). Health returns out of combat, while polymorphed and in the
    /// unreachable-target soft evade (RegenerateHealth :1127-1162): a third of the maximum, or a tenth for a polymorphed creature a player
    /// charms or owns. Mana returns on every tick (RegenerateMana :1102-1125): a third of the maximum out of combat, else (in combat, the soft
    /// evade included, or player controlled) <see cref="CreatureManaRegenPerTick"/> unless mana was spent in the last five seconds.
    /// Not delivered: a player-controlled creature's health out of polymorph still returns a third, not GetRegenHPPerSpirit x 4.
    /// </summary>
    private void UpdateCreatureRegen(Unit unit, uint diff)
    {
        UnitCombat c = unit.Combat;
        if (!IsAliveState(unit))
        {
            return;
        }

        c.RegenTimer -= (int)diff;
        if (c.RegenTimer > 0)
        {
            return;
        }

        c.RegenTimer = CombatConstants.CreatureRegenIntervalMs; // vmangos sets, not adds
        CombatEnvironment environment = CombatEnvironment.For(_world);
        bool inCombat = c.IsInCombat;
        bool polymorphed = environment.IsPolymorphed(unit);
        bool playerControlled = unit.CharmerOrOwnerGuid.IsPlayer;

        // vmangos RegenerateAll(diff, IsEvadeBecauseTargetNotReachable()) skips the in-combat check (Creature.cpp:1057, :1094): a creature
        // that has not reached its victim for 3 s regenerates health as if out of combat.
        bool softEvade = inCombat && unit is ICombatCreature { IsInEvadeMode: true };
        if ((!inCombat || polymorphed || softEvade) && (unit is not ICombatCreature creature || creature.RegeneratesHealth))
        {
            uint heal = playerControlled && polymorphed ? unit.MaxHealth / 10 : unit.MaxHealth / 3;
            unit.Health = Math.Min(unit.MaxHealth, unit.Health + heal);
        }

        uint maxMana = GetMaxPower(unit, PowerType.Mana);
        uint mana = GetPower(unit, PowerType.Mana);
        if (maxMana == 0 || mana >= maxMana)
        {
            // vmangos Creature::RegenerateAll (Creature.cpp:1094): a polymorphed creature heals in combat too, a third of its maximum per
            // tick (RegenerateHealth :1155-1158). In-combat mana is still left out.
            if (CombatEnvironment.For(_world).IsPolymorphed(unit) && (unit is not ICombatCreature pc || pc.RegeneratesHealth))
            {
                unit.Health = Math.Min(unit.MaxHealth, unit.Health + (unit.MaxHealth / 3));
            }

            return;
        }

        uint add;
        if (inCombat || playerControlled)
        {
            add = c.LastManaUseTimer != 0 ? 0u : RoundFloatChance(CreatureManaRegenPerTick(unit, environment), _regenRandom);
        }
        else
        {
            add = maxMana / 3;
        }

        SetPower(unit, PowerType.Mana, Math.Min(maxMana, mana + add));
    }

    private readonly Random _regenRandom = new();

    /// <summary>
    /// vmangos Creature::UpdateManaRegen (StatSystem.cpp:808-818), the mana a creature gains per 5 s tick in combat:
    /// (GetRegenMPPerSpirit x MOD_POWER_REGEN_PERCENT + MOD_POWER_REGEN(mana) / 5 + 0.6 x sqrt(max(1, intellect)) / 5) x rate x 5.
    /// </summary>
    internal static float CreatureManaRegenPerTick(Unit unit, CombatEnvironment environment)
    {
        float spirit = unit.GetUInt32(UpdateFields.UnitFieldStat0 + 4);
        float intellect = Math.Max(1f, unit.GetUInt32(UpdateFields.UnitFieldStat0 + 3));
        float percent = environment.GetPowerRegenFactor(unit, PowerType.Mana);
        float mp5 = environment.GetTotalAuraModifierByMisc(unit, AuraType.ModPowerRegen, (int)PowerType.Mana) / 5.0f;
        float perSecond = (RegenManaPerSpirit(unit.Class, spirit) * percent) + mp5 + (0.6f * MathF.Sqrt(intellect) / 5.0f);
        return Math.Max(0f, perSecond * environment.Options.RateMana * 5.0f);
    }

    /// <summary>vmangos round_float_chance: the whole part, plus one with the probability of the fraction.</summary>
    internal static uint RoundFloatChance(float value, Random random)
    {
        float floor = MathF.Floor(value);
        return (uint)floor + (random.NextDouble() < value - floor ? 1u : 0u);
    }
}
