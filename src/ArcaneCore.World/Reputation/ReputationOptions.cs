namespace ArcaneCore.World.Reputation;

/// <summary>
/// Reputation settings (configuration section "Reputation"). Rates default to the vmangos
/// mangosd.conf.dist values (Rate.Reputation.Gain 1, Rate.Reputation.LowLevel.Kill 0.2).
/// </summary>
public sealed class ReputationOptions
{
    public const string SectionName = "Reputation";

    /// <summary>
    /// Optional developer-supplied build-5875 Faction.dbc. Absent means no reputation factions:
    /// SMSG_INITIALIZE_FACTIONS stays empty and nonzero NPC factions keep failing closed.
    /// </summary>
    public string? FactionDbcPath { get; set; }

    public float RateGain { get; set; } = 1f;

    public float RateLowLevelKill { get; set; } = 0.2f;

    /// <summary>
    /// Retail (false) lets forced peace be lifted by the RELATIVE standing only (ReputationMgr.cpp:334-336);
    /// true compares the effective rank including the race base. Deliberate deviation, default retail.
    /// </summary>
    public bool PeaceForcedUsesEffectiveStanding { get; set; }

    /// <summary>Retail (true): reputation_spillover_template applies (ReputationMgr.cpp:211-243). False switches every spillover off.</summary>
    public bool SpilloverEnabled { get; set; } = true;

    /// <summary>
    /// Retail (true): combat attackability and creature aggro follow player reputation (at war, Hated guards, contested guards,
    /// forced reactions; Object.cpp:3608-3816). False keeps the template-only hooks. Needs Faction.dbc and FactionTemplate.dbc.
    /// </summary>
    public bool CombatReactions { get; set; } = true;
}
