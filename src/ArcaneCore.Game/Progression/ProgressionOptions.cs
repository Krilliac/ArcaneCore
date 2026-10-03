namespace ArcaneCore.Game.Progression;

/// <summary>
/// Experience and leveling settings (configuration section "Progression"). Defaults are the
/// vmangos mangosd.conf.dist values: MaxPlayerLevel 60, Rate.XP.Kill 1, Rate.XP.Kill.Elite 1,
/// MaxGroupXPDistance 74.
/// </summary>
public sealed class ProgressionOptions
{
    public const string SectionName = "Progression";

    /// <summary>MaxPlayerLevel: no experience is gained at or above it.</summary>
    public uint MaxPlayerLevel { get; set; } = 60;

    /// <summary>Rate.XP.Kill.</summary>
    public float RateXpKill { get; set; } = 1.0f;

    /// <summary>Rate.XP.Kill.Elite.</summary>
    public float RateXpKillElite { get; set; } = 1.0f;

    /// <summary>MaxGroupXPDistance (yards, 3D): group members farther from the victim get no XP or kill credit.</summary>
    public float GroupXpDistance { get; set; } = 74.0f;

    /// <summary>
    /// Optional developer-supplied level stats file (see <see cref="PlayerLevelStatsTable"/>).
    /// Absent: level-ups change level and XP but no base health, mana or stats.
    /// </summary>
    public string? LevelStatsPath { get; set; }
}
