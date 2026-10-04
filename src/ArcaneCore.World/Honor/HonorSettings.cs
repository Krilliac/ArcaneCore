using ArcaneCore.Game.Honor;

namespace ArcaneCore.World.Honor;

/// <summary>
/// The configuration section <c>World:Honor</c> as the binder sees it (mutable properties); <see cref="ToOptions"/> builds
/// the immutable <see cref="HonorOptions"/> the game code reads. Every default is the retail 1.12 value
/// (docs/areas/honor.md lists the keys).
/// </summary>
public sealed class HonorSettings
{
    public const string SectionName = "World:Honor";

    /// <summary>Master switch of the honor system (kills, ranks, the weekly calculation).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Civilian kills below gray cost honor (vmangos CONFIG_BOOL_ENABLE_DK).</summary>
    public bool DishonorableKills { get; set; } = true;

    /// <summary>Honorable kills a week to be ranked; 0 selects 15 (MIN_HONOR_KILLS_POST_1_10).</summary>
    public uint MinHonorKills { get; set; }

    /// <summary>Weekly rank point decay, clamped to 0..1.</summary>
    public float RpDecay { get; set; } = 0.2f;

    /// <summary>Weekday of the weekly calculation (Sunday 0, clamped to 6).</summary>
    public uint MaintenanceDay { get; set; } = 3;

    /// <summary>Hours added to UTC for the game day and the weekday of the weekly calculation.</summary>
    public int TimeZoneOffsetHours { get; set; }

    /// <summary>Standing pool size per faction; 0 uses the number of ranked players.</summary>
    public uint PoolSizePerFaction { get; set; }

    /// <summary>Assign the City Protector titles (vmangos default off).</summary>
    public bool CityProtector { get; set; }

    /// <summary>Creature entries that are never racial leaders.</summary>
    public uint[] RacialLeaderExcludedEntries { get; set; } = [];

    /// <summary>When the weekly calculation runs: Startup (default) or Live (an opt-in in-process weekly job).</summary>
    public HonorMaintenanceMode MaintenanceMode { get; set; } = HonorMaintenanceMode.Startup;

    /// <summary>Directory that receives the vmangos HCR calculation report; empty writes none.</summary>
    public string ReportDirectory { get; set; } = string.Empty;

    /// <summary>The validated options: the decay is clamped to 0..1 and the weekday to 0..6 (vmangos setConfigMinMax).</summary>
    public HonorOptions ToOptions() => new()
    {
        Enabled = Enabled,
        DishonorableKills = DishonorableKills,
        MinHonorKills = MinHonorKills,
        RpDecay = float.IsFinite(RpDecay) ? Math.Clamp(RpDecay, 0f, 1f) : 0.2f,
        MaintenanceDay = Math.Min(MaintenanceDay, 6u),
        TimeZoneOffsetHours = Math.Clamp(TimeZoneOffsetHours, -23, 23),
        PoolSizePerFaction = PoolSizePerFaction,
        CityProtector = CityProtector,
        RacialLeaderExcludedEntries = [.. RacialLeaderExcludedEntries],
        MaintenanceMode = MaintenanceMode,
        ReportDirectory = ReportDirectory ?? string.Empty,
    };
}
