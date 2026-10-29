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

/// <summary>
/// What the world daemon says about itself in its realm-list row (auth schema v7). <see cref="Unknown"/> is a row nobody manages
/// (a hand-edited row, a world that never writes it): the realm list sends the stored flags and enforces no heartbeat.
/// The realm daemon turns a state and the heartbeat age into the flags it sends (docs/ops/realm-liveness.md).
/// </summary>
public enum RealmRuntimeState : byte
{
    /// <summary>Not managed by a world: stored flags, no heartbeat enforcement.</summary>
    Unknown = 0,

    /// <summary>The process is up but not yet accepting connections.</summary>
    Starting = 1,

    /// <summary>Accepting connections and ticking.</summary>
    Online = 2,

    /// <summary>A restart countdown is running and the world still serves.</summary>
    Restarting = 3,

    /// <summary>A shutdown countdown is running and the world still serves.</summary>
    ShuttingDown = 4,

    /// <summary>The world stopped cleanly.</summary>
    Stopped = 5,
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

    /// <summary>The world's own state for this realm (auth schema v7); <see cref="RealmRuntimeState.Unknown"/> for an unmanaged row.</summary>
    public RealmRuntimeState State { get; set; }

    /// <summary>
    /// When the world last proved it was ticking, UTC (auth schema v7); null before any beat. Written by the world's clock and
    /// compared with the realm daemon's, so split hosts need synchronised clocks (docs/ops/realm-liveness.md).
    /// </summary>
    public DateTime? HeartbeatUtc { get; set; }

    /// <summary>
    /// Lowest account security level that sees this realm online (auth schema v7; vmangos <c>allowedSecurityLevel</c>); 0 = everyone.
    /// A lower account sees it offline (vmangos AuthSocket.cpp:1088-1090).
    /// </summary>
    public byte AllowedSecurityLevel { get; set; }
}
