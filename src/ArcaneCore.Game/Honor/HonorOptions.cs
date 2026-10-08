namespace ArcaneCore.Game.Honor;

/// <summary>When the weekly honor calculation runs.</summary>
public enum HonorMaintenanceMode
{
    /// <summary>In-process at the first minute tick on or after the maintenance day (deliberate deviation: vmangos needs a restart).</summary>
    Live,

    /// <summary>Only at process start, like vmangos (HonorMgr.cpp:617-633 flags it, the restart runs it).</summary>
    Startup,
}

/// <summary>
/// The configuration section <c>World:Honor</c>. Every default is the retail 1.12 value (vmangos
/// World.cpp:641-654, 1090-1095); a deviation is a deliberate option, never a default.
/// </summary>
public sealed record HonorOptions
{
    /// <summary>Master switch; false makes the feature attach nothing and every consumer fall back to rank 0.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Civilian kills below gray level cost honor (vmangos Player::RewardHonor).</summary>
    public bool DishonorableKills { get; init; } = true;

    /// <summary>Honorable kills needed in a week to be ranked; 0 selects the retail 15.</summary>
    public uint MinHonorKills { get; init; }

    /// <summary>Weekly rank point decay fraction, retail 0.2.</summary>
    public float RpDecay { get; init; } = 0.2f;

    /// <summary>
    /// Weekday (Sunday 0) of the weekly calculation. vmangos code defaults to 4 but its configuration text says
    /// 3 (Wednesday in EU); the documented value is used (open question recorded in docs/areas/honor.md).
    /// </summary>
    public uint MaintenanceDay { get; init; } = 3;

    /// <summary>One offset drives both the game day and the weekday (vmangos mixes localtime and TimeZoneOffset).</summary>
    public int TimeZoneOffsetHours { get; init; }

    /// <summary>Faction pool size for the standing curve; 0 uses the number of ranked players.</summary>
    public uint PoolSizePerFaction { get; init; }

    /// <summary>The City Protector title (vmangos default off).</summary>
    public bool CityProtector { get; init; }

    /// <summary>Creature entries that are never racial leaders for honor (retail data: none).</summary>
    public IReadOnlyList<uint> RacialLeaderExcludedEntries { get; init; } = [];

    public HonorMaintenanceMode MaintenanceMode { get; init; } = HonorMaintenanceMode.Startup;

    /// <summary>Directory for the HCR calculation report; empty writes none.</summary>
    public string ReportDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Non-retail when not 1 (default 1): multiplies the honor (contribution points) earned from kills, battleground bonuses and quests. vmangos has no
    /// such rate; the MaNGOS Zero fork and mangos-classic declare Rate.Honor (WorldConfig.cpp:215, World.cpp:452) but never read it, so the semantics
    /// are cmangos-tbc / TrinityCore's Player::RewardHonor (<c>honor *= Rate.Honor</c>). Dishonor and a GM's exact <c>.honor add</c> are not scaled.
    /// </summary>
    public float Rate { get; init; } = 1.0f;

    /// <summary>The weekly calculation's options.</summary>
    public HonorMaintenanceOptions Maintenance => new(RpDecay, MinHonorKills, PoolSizePerFaction);
}
