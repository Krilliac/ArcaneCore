namespace ArcaneCore.Game.Combat;

/// <summary>Combat constants for build 5875, each with the reference that confirmed it.</summary>
public static class CombatConstants
{
    /// <summary>Minimum melee reach, yards (vmangos ObjectDefines.h ATTACK_DISTANCE).</summary>
    public const float AttackDistance = 5.0f;

    /// <summary>Added to both combat reaches for melee range (vmangos UnitDefines.h BASE_MELEERANGE_OFFSET).</summary>
    public const float BaseMeleeRangeOffset = 1.333333373069763f;

    /// <summary>Combat reach floor used for melee range (vmangos Unit::GetCombatReach(forMeleeRange): reach &lt; 1.5 → 1.5).</summary>
    public const float MinMeleeCombatReach = 1.5f;

    /// <summary>Extra melee range while both units move (vmangos ObjectDefines.h LEEWAY_BONUS_RANGE).</summary>
    public const float LeewayBonusRange = 2.66f;

    /// <summary>Vertical melee limit; compared against dz² as vmangos does (UnitDefines.h UNIT_DEFAULT_MELEE_Z_LIMIT).</summary>
    public const float DefaultMeleeZLimit = 36.0f;

    /// <summary>No facing check for auto attacks closer than this, center to center (vmangos UnitDefines.h NO_FACING_CHECKS_DISTANCE).</summary>
    public const float NoFacingChecksDistance = 1.4f;

    /// <summary>Arc in front of the attacker an auto attack needs (vmangos Unit::CanAutoAttackTarget: 2π/3).</summary>
    public const float AutoAttackArc = 2.0f * MathF.PI / 3.0f;

    /// <summary>"In front" arc used by the hit table (vmangos WorldObject::HasInArc default: π).</summary>
    public const float DefaultArc = MathF.PI;

    /// <summary>Delay between main- and off-hand swings (vmangos UnitDefines.h ATTACK_DISPLAY_DELAY).</summary>
    public const uint AttackDisplayDelayMs = 200;

    /// <summary>Minimum delay a blocked swing (range/facing) is pushed back (vmangos Unit::DelayAutoAttacks: max(batch delay, 100)).</summary>
    public const uint AutoAttackRetryDelayMs = 100;

    /// <summary>Player combat lingers this long after PvP contact (vmangos UnitDefines.h UNIT_PVP_COMBAT_TIMER).</summary>
    public const uint PvpCombatTimerMs = 5500;

    /// <summary>How often the leave-combat check runs (vmangos Unit.h UNIT_COMBAT_CHECK_TIMER_MAX).</summary>
    public const uint CombatCheckIntervalMs = 1200;

    /// <summary>Default base attack time (vmangos UnitDefines.h BASE_ATTACK_TIME).</summary>
    public const uint BaseAttackTimeMs = 2000;

    /// <summary>Player regeneration tick (vmangos UnitDefines.h REGEN_TIME_PLAYER_FULL).</summary>
    public const int PlayerRegenIntervalMs = 2000;

    /// <summary>Creature regeneration tick (vmangos UnitDefines.h REGEN_TIME_CREATURE_FULL).</summary>
    public const int CreatureRegenIntervalMs = 5000;

    /// <summary>The "5 second rule" after spending mana (vmangos Unit::m_lastManaUseTimer, set to 5000 by Spell::TakePower).</summary>
    public const uint ManaRegenInterruptMs = 5000;

    /// <summary>Corpse reclaim and resurrection dialog radius (vmangos Corpse.h CORPSE_RECLAIM_RADIUS).</summary>
    public const float CorpseReclaimRadius = 39.0f;

    /// <summary>Automatic spirit release after death on non-instanced maps (vmangos Player.h CORPSE_REPOP_TIME = 6 minutes).</summary>
    public const uint CorpseRepopTimeMs = 6 * 60 * 1000;

    /// <summary>Corpse reclaim delays by recent-death count, seconds (vmangos Player.cpp copseReclaimDelay {30, 60, 120}).</summary>
    public static ReadOnlySpan<uint> CorpseReclaimDelaySeconds => [30, 60, 120];

    /// <summary>One step of the recent-death window (vmangos Player.cpp DEATH_EXPIRE_STEP = 5 minutes).</summary>
    public const uint DeathExpireStepSeconds = 5 * 60;

    /// <summary>Recent deaths counted (vmangos Player.cpp MAX_DEATH_COUNT).</summary>
    public const int MaxDeathCount = 3;

    /// <summary>Fraction of health/mana restored at corpse reclaim outside battlegrounds (vmangos HandleReclaimCorpseOpcode: 0.5).</summary>
    public const float CorpseReclaimRestorePercent = 0.5f;

    /// <summary>PvP flag lingers this long after it stops being wanted (vmangos Player::UpdatePvP: timerPvPRemaining = 300000).</summary>
    public const uint PvpFlagTimerMs = 300_000;

    /// <summary>Fallback max damage when a unit has none (vmangos Unit::CalculateDamage: max_damage == 0 → 5).</summary>
    public const float FallbackMaxDamage = 5.0f;

    /// <summary>Hit table roll range, hundredths of a percent (vmangos RollMeleeOutcomeAgainst: urand(0, 9999)).</summary>
    public const int RollRange = 10000;

    /// <summary>World bosses count as this many levels above their target (vmangos World.cpp WorldBossLevelDiff default 3).</summary>
    public const int WorldBossLevelDiff = 3;

    /// <summary>Crit damage, percent (vmangos CalculateMeleeDamage MELEE_HIT_CRIT: 200 + SPELL_AURA_MOD_CRIT_PERCENT_VERSUS).</summary>
    public const float CritDamagePercent = 200.0f;

    /// <summary>Ghost spell (vmangos Player.cpp SPELL_GHOST = 8326), applied through <see cref="CombatHooks.ApplyGhostForm"/>.</summary>
    public const uint GhostSpellId = 8326;

    /// <summary>Night elf wisp ghost (vmangos Player.cpp SPELL_WISP_SPIRIT_GHOST = 20584; needs the passive 20585 SPELL_WISP_SPIRIT).</summary>
    public const uint WispGhostSpellId = 20584;
}
