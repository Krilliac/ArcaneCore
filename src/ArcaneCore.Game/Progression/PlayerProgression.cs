using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Progression;

/// <summary>
/// Player experience and leveling on the world thread, reimplemented from vmangos/core
/// 4b3d241 Player::GiveXP, GiveLevel, GetXPRestBonus and SetRestBonus. Base values come from
/// an optional <see cref="IPlayerLevelStatsSource"/> and are applied as deltas, so item and
/// aura modifiers already on the player compose with them. Characters held by a pending quest
/// reward receive no experience (the settlement owns their state until it is published).
/// </summary>
public sealed class PlayerProgression : IQuestExperience
{
    /// <summary>PLAYER_BYTES_2 byte 3 REST_STATE_RESTED / REST_STATE_NORMAL.</summary>
    public const byte RestStateRested = 0x01;

    public const byte RestStateNormal = 0x02;

    private readonly ConditionalWeakTable<Player, State> _states = new();
    private readonly Func<uint> _nowMs;

    public PlayerProgression(ProgressionOptions options, IPlayerLevelStatsSource? stats = null, Func<uint>? nowMs = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Stats = stats ?? PlayerLevelStatsTable.Empty;
        _nowMs = nowMs ?? (() => 0);
    }

    public ProgressionOptions Options { get; }

    public IPlayerLevelStatsSource Stats { get; private set; }

    /// <summary>
    /// Replace the level stats source (the stats feature installs the imported world data when no developer file is
    /// configured). Call before players are initialised; a player already initialised keeps the values it has.
    /// </summary>
    public void UseLevelStats(IPlayerLevelStatsSource source) => Stats = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Raised on the world thread after a level-up has been applied (persistence requests a save).</summary>
    public event Action<Player>? LevelChanged;

    /// <summary>
    /// Raised after the base values of a level were applied to a player (login and level-up), so the derived combat
    /// stats (attack power, crit, dodge, armor) can be recomputed from the new stat fields.
    /// </summary>
    public event Action<Player>? BaseValuesApplied;

    /// <summary>
    /// Prepare a loaded player before it enters the world: the next-level requirement for its
    /// level, its stored XP (clamped below that requirement) and the base values of its level.
    /// </summary>
    public void InitializeLoadedPlayer(Player player, uint storedXp = 0)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint next = PlayerXpTable.XpForLevel(player.Level, Options.MaxPlayerLevel);
        player.SetUInt32(UpdateFields.PlayerNextLevelXp, next);
        player.SetUInt32(UpdateFields.PlayerXp, next == 0 ? 0 : Math.Min(storedXp, next - 1));
        State state = _states.GetValue(player, _ => new State());
        if (state.Applied is null && Stats.Find((byte)player.Race, (byte)player.Class, player.Level) is { } levelStats)
        {
            ApplyBase(player, state, levelStats, out _, out _, refill: true);
        }

        SetRestBonus(player, state.RestBonus);
        BaseValuesApplied?.Invoke(player);
    }

    /// <summary>The XP currently shown (PLAYER_XP).</summary>
    public static uint CurrentXp(Player player) => player.GetUInt32(UpdateFields.PlayerXp);

    public byte MaxPlayerLevel => (byte)Math.Min(Options.MaxPlayerLevel, byte.MaxValue);

    public uint GetCurrentXp(Player player) => CurrentXp(player);

    /// <summary>
    /// vmangos Player::GiveXP's first step (Player.cpp:3018-3019): a personal XP rate that is set (not negative, <see cref="Player.PersonalXpRate"/>)
    /// multiplies the gain, truncated as <c>uint32 *= float</c>. Every XP source (kills, quests, exploration) passes through it.
    /// </summary>
    public static uint ApplyPersonalRate(Player player, uint xp)
    {
        ArgumentNullException.ThrowIfNull(player);
        float rate = player.PersonalXpRate;
        return rate >= 0.0f ? (uint)Math.Min(xp * rate, (float)uint.MaxValue) : xp;
    }

    /// <summary>IPlayerExperience: non-kill experience (quests, exploration), no rested bonus.</summary>
    public void GiveXp(Player player, uint xp) => GiveXp(player, xp, ObjectGuid.Empty);

    /// <summary>
    /// vmangos Player::GiveXP. Returns the XP actually added (including rested bonus). Dead,
    /// held or max-level players get nothing; a kill (non-empty <paramref name="victim"/>)
    /// consumes rested bonus up to the gained amount.
    /// </summary>
    public uint GiveXp(Player player, uint xp, ObjectGuid victim)
    {
        ArgumentNullException.ThrowIfNull(player);
        xp = ApplyPersonalRate(player, xp);
        if (xp < 1 || !player.IsAlive || !player.CanMutateQuestSettlementState || player.Level >= Options.MaxPlayerLevel)
        {
            return 0;
        }

        uint rested = victim.IsEmpty ? 0 : ConsumeRestBonus(player, xp);
        player.Session.Send(WorldOpcode.SmsgLogXpgain, ProgressionPackets.LogXpGain(victim, xp, rested).AsSpan());
        ulong newXp = (ulong)CurrentXp(player) + xp + rested;
        uint next = player.GetUInt32(UpdateFields.PlayerNextLevelXp);
        bool leveled = false;
        while (next > 0 && newXp >= next && player.Level < Options.MaxPlayerLevel)
        {
            newXp -= next;
            GiveLevel(player, (byte)(player.Level + 1));
            leveled = true;
            next = player.GetUInt32(UpdateFields.PlayerNextLevelXp);
        }

        player.SetUInt32(UpdateFields.PlayerXp, next == 0 ? 0 : (uint)Math.Min(newXp, next - 1));
        if (leveled)
        {
            LevelChanged?.Invoke(player);
        }

        return xp + rested;
    }

    /// <summary>
    /// The level and XP <see cref="GiveXp(Player, uint)"/> would produce without side effects
    /// (rested bonus does not apply to non-kill XP). Quest settlement persists this result.
    /// </summary>
    public (byte Level, uint Xp) Preview(byte level, uint currentXp, uint xp)
    {
        if (xp < 1 || level >= Options.MaxPlayerLevel)
        {
            return (level, currentXp);
        }

        ulong value = (ulong)currentXp + xp;
        uint next = PlayerXpTable.XpForLevel(level, Options.MaxPlayerLevel);
        while (next > 0 && value >= next && level < Options.MaxPlayerLevel)
        {
            value -= next;
            level++;
            next = PlayerXpTable.XpForLevel(level, Options.MaxPlayerLevel);
        }

        return (level, next == 0 ? 0 : (uint)Math.Min(value, next - 1));
    }

    /// <summary>
    /// vmangos Player::GiveLevel: SMSG_LEVELUP_INFO with base health/mana (including the
    /// stamina/intellect bonus difference) and stat gains, the next-level requirement, level and
    /// played-time-at-level reset, new base values, then full health/mana/energy.
    /// </summary>
    public void GiveLevel(Player player, byte level)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (level == player.Level || level == 0)
        {
            return;
        }

        State state = _states.GetValue(player, _ => new State());
        PlayerLevelStats? stats = Stats.Find((byte)player.Race, (byte)player.Class, level);
        int healthGain = 0;
        int manaGain = 0;
        int[] statGains = new int[5];
        PlayerLevelStats? old = state.Applied;
        if (stats is not null)
        {
            for (int i = 0; i < 5; i++)
            {
                statGains[i] = (int)stats.Stat(i) - (int)(old?.Stat(i) ?? 0);
            }
        }

        // Build the packet from the values before they change (the reference sends first).
        if (stats is not null)
        {
            healthGain = (int)stats.BaseHealth - (int)player.GetUInt32(UpdateFields.UnitFieldBaseHealth)
                + (int)ExperienceFormulas.HealthBonusFromStamina(stats.Stamina)
                - (int)ExperienceFormulas.HealthBonusFromStamina(old?.Stamina ?? 0);
            manaGain = player.PowerType != PowerType.Mana ? 0
                : (int)stats.BaseMana - (int)player.GetUInt32(UpdateFields.UnitFieldBaseMana)
                    + (int)ExperienceFormulas.ManaBonusFromIntellect(stats.Intellect)
                    - (int)ExperienceFormulas.ManaBonusFromIntellect(old?.Intellect ?? 0);
        }

        player.Session.Send(WorldOpcode.SmsgLevelupInfo, ProgressionPackets.LevelUpInfo(level, healthGain, manaGain, statGains).AsSpan());
        player.SetUInt32(UpdateFields.PlayerNextLevelXp, PlayerXpTable.XpForLevel(level, Options.MaxPlayerLevel));
        player.ResetLevelPlayedTime(_nowMs());
        player.Level = level;
        if (stats is not null)
        {
            ApplyBase(player, state, stats, out _, out _, refill: false);
        }

        if (player.IsAlive)
        {
            player.Health = player.MaxHealth;
        }

        RefillPower(player, PowerType.Mana);
        RefillPower(player, PowerType.Energy);
        int rage = UpdateFields.UnitFieldPower1 + (int)PowerType.Rage;
        int maxRage = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage;
        if (player.GetUInt32(rage) > player.GetUInt32(maxRage))
        {
            player.SetUInt32(rage, player.GetUInt32(maxRage));
        }

        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Focus, 0);
        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Happiness, 0);
        SetRestBonus(player, state.RestBonus);
        BaseValuesApplied?.Invoke(player);
    }

    /// <summary>
    /// vmangos Player::GetRestBonus: the rested XP pool. This class owns the pool and what it does to kill XP; <see cref="RestService"/>
    /// fills it (resting, offline time) and the rest feature persists it.
    /// </summary>
    public float RestBonus(Player player) => _states.TryGetValue(player, out State? s) ? s.RestBonus : 0;

    /// <summary>vmangos Player::SetRestBonus: clamp to 1.5 levels / 2, publish the rest state byte and PLAYER_REST_STATE_EXPERIENCE.</summary>
    public void SetRestBonus(Player player, float bonus)
    {
        ArgumentNullException.ThrowIfNull(player);
        State state = _states.GetValue(player, _ => new State());
        if (player.Level >= Options.MaxPlayerLevel || !float.IsFinite(bonus) || bonus < 0)
        {
            bonus = 0;
        }

        float max = player.GetUInt32(UpdateFields.PlayerNextLevelXp) * 1.5f / 2.0f;
        state.RestBonus = Math.Min(bonus, max);
        if (state.RestBonus > 10)
        {
            player.SetByte(UpdateFields.PlayerBytes2, 3, RestStateRested);
        }
        else if (state.RestBonus <= 1)
        {
            player.SetByte(UpdateFields.PlayerBytes2, 3, RestStateNormal);
        }

        player.SetUInt32(UpdateFields.PlayerRestStateExperience, (uint)state.RestBonus);
    }

    /// <summary>vmangos Player::GetXPRestBonus: rested bonus up to the gained XP (at most double).</summary>
    private uint ConsumeRestBonus(Player player, uint xp)
    {
        float pool = RestBonus(player);
        uint bonus = Math.Min((uint)pool, xp);
        if (bonus > 0)
        {
            SetRestBonus(player, pool - bonus);
        }

        return bonus;
    }

    private static void ApplyBase(Player player, State state, PlayerLevelStats stats, out int healthDelta, out int manaDelta, bool refill)
    {
        PlayerLevelStats? old = state.Applied;
        for (int i = 0; i < 5; i++)
        {
            int field = UpdateFields.UnitFieldStat0 + i;
            int delta = (int)stats.Stat(i) - (int)(old?.Stat(i) ?? 0);
            player.SetUInt32(field, Add(player.GetUInt32(field), delta));
        }

        // The class base health and mana move the maximums here; the stamina and intellect bonuses (level base values
        // plus whatever items add) are StatBonuses', which keeps them in line with the stat fields.
        healthDelta = (int)stats.BaseHealth - (int)player.GetUInt32(UpdateFields.UnitFieldBaseHealth);
        player.SetUInt32(UpdateFields.UnitFieldBaseHealth, stats.BaseHealth);
        player.MaxHealth = Add(player.MaxHealth, healthDelta);
        manaDelta = 0;
        if (player.PowerType == PowerType.Mana)
        {
            manaDelta = (int)stats.BaseMana - (int)player.GetUInt32(UpdateFields.UnitFieldBaseMana);
            int maxMana = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana;
            player.SetUInt32(maxMana, Add(player.GetUInt32(maxMana), manaDelta));
        }

        player.SetUInt32(UpdateFields.UnitFieldBaseMana, stats.BaseMana);
        state.Applied = stats;
        StatBonuses.Update(player);
        if (refill)
        {
            player.Health = player.MaxHealth;
            RefillPower(player, PowerType.Mana);
        }
    }

    private static void RefillPower(Player player, PowerType power)
        => player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)power, player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power));

    private static uint Add(uint value, int delta) => (uint)Math.Clamp((long)value + delta, 0, uint.MaxValue);

    private sealed class State
    {
        public PlayerLevelStats? Applied { get; set; }

        public float RestBonus { get; set; }
    }
}
