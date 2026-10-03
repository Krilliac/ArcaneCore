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
}
