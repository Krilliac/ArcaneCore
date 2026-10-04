namespace ArcaneCore.Game.Spells;

/// <summary>
/// Periodic aura timing as vmangos does it (Aura::CalculatePeriodic SpellAuras.cpp:8053-8110, Aura::Update :553-572,
/// Aura::UpdatePeriodicTimer :528-543, SpellAuraHolder::HandleAuraModTotalManaPercentRegen :4855-4866).
/// </summary>
public static class PeriodicTiming
{
    /// <summary>
    /// The period of an aura with no amplitude: SPELL_AURA_OBS_MOD_MANA defaults to 1000 ms (HandleAuraModTotalManaPercentRegen,
    /// SpellAuras.cpp:4861-4864). Every other type without an amplitude is not periodic here. (vmangos would tick an
    /// amplitude-less DoT or HoT on every update; no retail data relies on it and a data slip would hit a target every
    /// 50 ms, so that case stays non-periodic: docs/areas/aura-engine.md.)
    /// </summary>
    public static uint PeriodFor(AuraType type, uint amplitude)
        => amplitude != 0 ? amplitude : type == AuraType.ObsModMana ? 1000u : 0u;

    /// <summary>
    /// Whether the first tick is due at the first update rather than one period after application: the totem passives, Immolation
    /// Trap Effect, Mark of Frost/Nature and the Stoneclaw rule (SpellVisual 0 with SpellIconID 689), SpellAuras.cpp:8078-8104.
    /// </summary>
    public static bool FirstTickImmediate(SpellInfo spell)
    {
        if (spell.SpellVisual == 0 && spell.SpellIconId == 689)
        {
            return true;
        }

        return spell.Id is 8145 or 6474 or 8179 or 8172 or 8167 or 8515 or 10609 or 10612
            or 13797 or 14298 or 14299 or 14300 or 14301 or 23184 or 25041;
    }

    /// <summary>The timer a freshly applied or refreshed aura starts with (vmangos CalculatePeriodic with create = true).</summary>
    public static int InitialTimer(SpellInfo spell, SpellAura aura) => FirstTickImmediate(spell) ? 0 : (int)aura.Period;

    /// <summary>
    /// vmangos Aura::UpdatePeriodicTimer(duration): after a pushback shortened the duration the tick timer follows it, so
    /// the aura does not keep its maximum ticks: the timer becomes the remainder of the duration over the period, or a whole
    /// period when that is zero.
    /// </summary>
    public static void SyncToDuration(SpellAura aura, int duration)
    {
        if (!aura.IsPeriodic)
        {
            return;
        }

        int period = (int)aura.Period;
        int timer = duration > period ? duration % period : duration;
        aura.PeriodicTimer = timer == 0 ? period : timer;
    }

    /// <summary>
    /// One update of a periodic aura (vmangos Aura::Update). Returns how many ticks are due: vmangos delivers at most one per
    /// update and resets a timer that drifted more than a period behind to 0; <paramref name="catchUp"/> keeps the older
    /// engine behaviour of delivering every missed tick in one update (Auras:PeriodicCatchUp).
    /// </summary>
    public static int Advance(SpellAura aura, uint diffMs, bool catchUp)
    {
        if (!aura.IsPeriodic)
        {
            return 0;
        }

        int period = (int)aura.Period;
        aura.PeriodicTimer -= (int)(diffMs & 0x7FFFFFFF);
        if (aura.PeriodicTimer > 0)
        {
            return 0;
        }

        if (catchUp)
        {
            int ticks = 0;
            while (aura.PeriodicTimer <= 0)
            {
                aura.PeriodicTimer += period;
                ticks++;
            }

            return ticks;
        }

        // "dont allow timer to drift off to huge negative value" (SpellAuras.cpp:561-566).
        aura.PeriodicTimer = aura.PeriodicTimer < -period ? 0 : aura.PeriodicTimer + period;
        return 1;
    }
}
