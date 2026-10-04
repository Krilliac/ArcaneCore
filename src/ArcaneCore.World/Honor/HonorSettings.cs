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

    public bool Enabled { get; set; } = true;

    public bool DishonorableKills { get; set; } = true;

    public uint MinHonorKills { get; set; }

    public float RpDecay { get; set; } = 0.2f;

    public uint MaintenanceDay { get; set; } = 3;

    public int TimeZoneOffsetHours { get; set; }

    public uint PoolSizePerFaction { get; set; }

    public bool CityProtector { get; set; }

    public uint[] RacialLeaderExcludedEntries { get; set; } = [];

    public HonorMaintenanceMode MaintenanceMode { get; set; } = HonorMaintenanceMode.Startup;

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
