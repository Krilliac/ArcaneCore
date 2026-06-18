using ArcaneCore.Kernel.Realms;

namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// Realms to seed into an empty realm list on first startup, bound from the "Realms"
/// section. Lets a fresh dev environment reach the realm-list screen with no manual SQL.
/// </summary>
public sealed class RealmSeedOptions
{
    public const string SectionName = "Realms";

    public List<RealmSeedEntry> Seed { get; set; } = [];
}

/// <summary>A single seed realm row.</summary>
public sealed class RealmSeedEntry
{
    public string Name { get; set; } = "ArcaneCore";

    /// <summary>"ip:port" the client connects to for the world server.</summary>
    public string Address { get; set; } = "127.0.0.1:8085";

    public RealmType Type { get; set; } = RealmType.Normal;

    public RealmFlags Flags { get; set; } = RealmFlags.None;

    public float Population { get; set; }

    public byte Category { get; set; }
}
