namespace ArcaneCore.Game.Death;

/// <summary>
/// Death settings, bound from the <c>World:Death</c> configuration section. Every default is the
/// vmangos <c>mangosd.conf.dist</c> value (retail behaviour); a deviation is a deliberate switch.
/// </summary>
public sealed class DeathOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "World:Death";

    /// <summary>
    /// vmangos <c>Death.CorpseReclaimDelay.PvP</c> (mangosd.conf.dist.in:2850, default 1): after a
    /// PvP death the corpse reclaim delay scales with recent deaths; off, it is always 30 s
    /// (Player.cpp:20184-20188).
    /// </summary>
    public bool CorpseReclaimDelayPvP { get; set; } = true;

    /// <summary>vmangos <c>Death.CorpseReclaimDelay.PvE</c> (mangosd.conf.dist.in:2851, default 1), for non-PvP deaths.</summary>
    public bool CorpseReclaimDelayPvE { get; set; } = true;

    /// <summary>
    /// Deviation switch, default off (retail/vmangos): when no graveyard is linked for the ghost's area or zone and team,
    /// vmangos leaves the ghost where it is (ObjectMgr.cpp:7512-7524 returns null; Player.cpp:5008). On, the
    /// mangos-classic fallback applies instead: the default graveyard of the team, safe location 4 (Alliance) or 10 (Horde)
    /// (GraveyardManager.cpp:146-147, 170-174). Matters where the classic-db data has a graveyard for one team only.
    /// </summary>
    public bool GraveyardFallbackToDefaults { get; set; }

    /// <summary>
    /// vmangos <c>Death.SicknessLevel</c> (mangosd.conf.dist.in:2744-2749, World.cpp:772, default 11): the level from which a
    /// spirit-healer resurrection gives resurrection sickness. It lasts one minute at that level and one more for each level
    /// above, up to the full ten minutes (Player.cpp:4675-4697). -10 gives full sickness at level 1; a level above the maximum
    /// player level gives none.
    /// </summary>
    public int SicknessLevel { get; set; } = 11;
}
