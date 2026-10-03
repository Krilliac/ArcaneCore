namespace ArcaneCore.Game.Stats;

/// <summary>Player stat settings (configuration section "Stats").</summary>
public sealed class StatsOptions
{
    public const string SectionName = "Stats";

    /// <summary>
    /// Refuse to log a player in unless the player base data (class health/mana, race/class stats, XP per level,
    /// crit and dodge per agility) is imported and complete, as vmangos refuses to start without it
    /// (ObjectMgr.cpp:4876-4882, 4988-4994, 5011-5029, 5117-5129). The default is false: a host that has not
    /// imported the data yet still runs, the agility terms of crit and dodge are left out and the level stats
    /// come from <c>Progression:LevelStatsPath</c> or are absent. This is the one deliberate difference from
    /// retail behaviour of the stat feature; set it to true on a host that has imported the data.
    /// </summary>
    public bool RequireImportedData { get; set; }
}
