namespace ArcaneCore.Kernel.Realms;

/// <summary>
/// Realm "icon"/type shown on the realm-list screen. Values are the build-5875 wire
/// values (vmangos: realm <c>icon</c> field, sent as uint32).
/// </summary>
public enum RealmType : uint
{
    Normal = 0,
    PvP = 1,
    Normal2 = 4,
    RP = 6,
    RpPvP = 8,
}

/// <summary>
/// Realm flags (vmangos shared/Common.h RealmFlags). 1.x clients do not support
/// SPECIFYBUILD/locked display; OFFLINE hides selectability.
/// </summary>
[Flags]
public enum RealmFlags : byte
{
    None = 0x00,
    Invalid = 0x01,
    Offline = 0x02,
    SpecifyBuild = 0x04,
    NewPlayers = 0x20,
    Recommended = 0x40,
}

/// <summary>A realm advertised to the client in the realm-list reply.</summary>
public sealed class RealmEntry
{
    public int Id { get; set; }

    /// <summary>Display name shown on the realm-list screen.</summary>
    public required string Name { get; set; }

    /// <summary>Connection address the client uses for the world server, "ip:port".</summary>
    public required string Address { get; set; }

    public RealmType Type { get; set; } = RealmType.Normal;

    public RealmFlags Flags { get; set; } = RealmFlags.None;

    /// <summary>Population hint (0.0 low .. higher = busier).</summary>
    public float Population { get; set; }

    /// <summary>Realm category id (timezone grouping) for the build-5875 list layout.</summary>
    public byte Category { get; set; }
}
