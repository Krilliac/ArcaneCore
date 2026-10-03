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
}
