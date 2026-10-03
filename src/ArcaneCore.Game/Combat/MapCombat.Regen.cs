using ArcaneCore.Game.Entities;

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

        if (!c.IsInCombat)
        {
            RegenerateHealth(player);
            RegeneratePower(player, PowerType.Rage);
        }

        RegeneratePower(player, PowerType.Energy);
        RegeneratePower(player, PowerType.Mana);
        c.RegenTimer += CombatConstants.PlayerRegenIntervalMs;
    }

    /// <summary>
    /// vmangos Player::RegenerateHealth (out of combat): GetRegenHPPerSpirit, ×1.5 when not
    /// standing; fractions carry over to the next tick.
    /// </summary>
    private static void RegenerateHealth(Player player)
    {
        uint cur = player.Health;
        uint max = player.MaxHealth;
        if (cur >= max)
        {
            return;
        }

        UnitCombat c = player.Combat;
        float add = RegenHealthPerSpirit(player.Class, player.GetUInt32(UpdateFields.UnitFieldStat0 + 4));
        if (!IsStandingUp(player))
        {
            add *= 1.5f;
        }

        add += c.HealthRegenCarry;
        c.HealthRegenCarry = add - (int)add;
        uint gain = (uint)Math.Max(0, (int)add);
        player.Health = Math.Min(max, cur + gain);
    }

    /// <summary>
    /// vmangos Player::Regenerate: mana GetRegenMPPerSpirit × 2 per tick (0 within five seconds
    /// of spending mana), rage −20 (2 rage) per tick, energy +20.
    /// </summary>
    private static void RegeneratePower(Player player, PowerType power)
    {
        uint cur = GetPower(player, power);
        uint max = GetMaxPower(player, power);
        float add = power switch
        {
            PowerType.Mana => player.Combat.LastManaUseTimer > 0 ? 0f : RegenManaPerSpirit(player.Class, player.GetUInt32(UpdateFields.UnitFieldStat0 + 4)) * 2.0f,
            PowerType.Rage => 20f,
            PowerType.Energy => 20f,
            _ => 0f,
        };

        if (power != PowerType.Rage)
        {
            cur = Math.Min(max, cur + (uint)add);
        }
        else
        {
            cur = cur <= (uint)add ? 0 : cur - (uint)add;
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
    /// vmangos Creature::RegenerateAll every 5 s (src/game/Objects/Creature.cpp): out of combat
    /// health and mana return a third of their maximum per tick (Creature::RegenerateHealth / RegenerateMana); in-combat mana
    /// regeneration needs creature stats and is left to the creatures area.
    /// </summary>
    private static void UpdateCreatureRegen(Unit unit, uint diff)
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
        if (c.IsInCombat)
        {
            return;
        }

        if (unit is not ICombatCreature creature || creature.RegeneratesHealth)
        {
            unit.Health = Math.Min(unit.MaxHealth, unit.Health + (unit.MaxHealth / 3));
        }

        uint maxMana = GetMaxPower(unit, PowerType.Mana);
        if (maxMana > 0)
        {
            SetPower(unit, PowerType.Mana, Math.Min(maxMana, GetPower(unit, PowerType.Mana) + (maxMana / 3)));
        }
    }
}
