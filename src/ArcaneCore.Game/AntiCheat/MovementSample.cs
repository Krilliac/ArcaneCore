using ArcaneCore.Protocol;

namespace ArcaneCore.Game.AntiCheat;

/// <summary>What the caller found out about the transport a movement block names (ships and elevators are checked differently).</summary>
public enum TransportClaim
{
    /// <summary>The block does not claim a transport.</summary>
    None,

    /// <summary>The named transport exists on the map near the player.</summary>
    Known,

    /// <summary>The caller cannot judge (transports disabled, no game object system): never scored.</summary>
    Unknown,

    /// <summary>No such transport near the player: the flag is spoofed to skip the movement checks.</summary>
    Fake,
}

/// <summary>
/// The world geometry the terrain-dependent checks ask about. Every answer is null when the data for that spot is not
/// loaded, and a null answer never scores (docs/areas/anticheat.md: no data is never a finding).
/// </summary>
public interface IAntiCheatTerrain
{
    /// <summary>Whether liquid is at the point; null without terrain and model data there (indoor water lives in the models).</summary>
    bool? IsInLiquid(float x, float y, float z);

    /// <summary>The floor under the point from terrain and models; null without both kinds of data there.</summary>
    float? FloorHeight(float x, float y, float z);

    /// <summary>Whether a static model blocks the segment; null without model data at both ends.</summary>
    bool? IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2);
}

/// <summary>
/// One client movement block and the server state it is judged against, gathered by the caller (the world's movement
/// handler) so <see cref="MovementAntiCheat"/> stays pure and testable with synthetic sequences.
/// </summary>
public readonly record struct MovementSample
{
    /// <summary>The movement opcode (MSG_MOVE_*).</summary>
    public WorldOpcode Opcode { get; init; }

    /// <summary>The block as the client sent it.</summary>
    public MovementInfo Movement { get; init; }

    /// <summary>When the packet arrived (monotonic milliseconds, independent of the client).</summary>
    public uint ReceivedMs { get; init; }

    /// <summary>The session's ping latency average in milliseconds.</summary>
    public int LatencyMs { get; init; }

    /// <summary>The fastest speed the server allows right now in yards per second (every move type and every pending change).</summary>
    public float AllowedSpeed { get; init; }

    /// <summary>The player is alive (the capability-flag checks judge the living only, as the fork).</summary>
    public bool Alive { get; init; }

    /// <summary>The server's root is in force and acknowledged (no root change pending).</summary>
    public bool Rooted { get; init; }

    /// <summary>The movement flags the server granted: acknowledged or enforced orders, pending orders to set them, auras, GM grants.</summary>
    public MovementFlags GrantedFlags { get; init; }

    /// <summary>The verdict on the transport the block names.</summary>
    public TransportClaim Transport { get; init; }

    /// <summary>The geometry probe for the terrain-dependent checks; null runs none of them.</summary>
    public IAntiCheatTerrain? Terrain { get; init; }
}
