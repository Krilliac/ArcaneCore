using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The aura side of player regeneration, read each 2 s regen tick (vmangos Player::RegenerateAll / RegenerateHealth,
/// Player.cpp:2269-2400, and Player::UpdateManaRegen, StatSystem.cpp:642-662). Food is SPELL_AURA_MOD_REGEN, drink is
/// SPELL_AURA_MOD_POWER_REGEN; neither ticks as a periodic aura, the regen tick reads their amounts.
/// </summary>
public static class RegenAuraRules
{
    /// <summary>vmangos HandleModRegen: an amplitude of 0 means a 5000 ms period (SpellAuras.cpp:4795-4803).</summary>
    public const uint DefaultModRegenPeriodMs = 5000;

    /// <summary>vmangos REGEN_TIME_PLAYER_FULL (Player.h): the regen tick, 2000 ms.</summary>
    public const float RegenTickMs = CombatConstants.PlayerRegenIntervalMs;

    /// <summary>vmangos GetTotalAuraModifier: the sum of the amounts of every aura of <paramref name="type"/>.</summary>
    public static int Total(IPowerAuraSource? source, Unit unit, AuraType type)
    {
        int total = 0;
        if (source is not null)
        {
            foreach (SpellAura aura in source.GetAuras(unit, type))
            {
                total += aura.Amount;
            }
        }

        return total;
    }

    /// <summary>vmangos GetTotalAuraModifierByMiscValue: the sum over the auras whose misc value is <paramref name="misc"/>.</summary>
    public static int TotalByMisc(IPowerAuraSource? source, Unit unit, AuraType type, int misc)
    {
        int total = 0;
        if (source is not null)
        {
            foreach (SpellAura aura in source.GetAuras(unit, type))
            {
                if (aura.MiscValue == misc)
                {
                    total += aura.Amount;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Health a food aura adds in one regen tick: amount * (2000 / period) per MOD_REGEN aura (Player.cpp:2378-2381); the
    /// period is the spell's amplitude, 5000 ms when it has none.
    /// </summary>
    public static float FoodPerTick(IPowerAuraSource? source, Unit unit)
    {
        float add = 0.0f;
        if (source is not null)
        {
            foreach (SpellAura aura in source.GetAuras(unit, AuraType.ModRegen))
            {
                uint period = aura.Amplitude == 0 ? DefaultModRegenPeriodMs : aura.Amplitude;
                add += aura.Amount * (RegenTickMs / period);
            }
        }

        return add;
    }

    /// <summary>
    /// Mana per second from the regen auras (Player::UpdateManaRegen, StatSystem.cpp:642-660): the spirit regen scaled by the
    /// MOD_POWER_REGEN_PERCENT auras for mana, plus the mp5 of the MOD_POWER_REGEN auras for mana divided by 5; inside the
    /// five second rule only <c>MOD_MANA_REGEN_INTERRUPT</c> percent (capped at 100) of the spirit part counts, the mp5 part
    /// always does.
    /// </summary>
    public static float ManaPerSecond(IPowerAuraSource? source, Unit unit, float spiritRegen, bool insideFiveSecondRule)
    {
        float spirit = spiritRegen * (source?.GetPowerRegenFactor(unit, PowerType.Mana) ?? 1.0f);
        float mp5 = TotalByMisc(source, unit, AuraType.ModPowerRegen, (int)PowerType.Mana) / 5.0f;
        if (!insideFiveSecondRule)
        {
            return mp5 + spirit;
        }

        int interrupt = Math.Min(Total(source, unit, AuraType.ModManaRegenInterrupt), 100);
        return mp5 + (spirit * interrupt / 100.0f);
    }
}
