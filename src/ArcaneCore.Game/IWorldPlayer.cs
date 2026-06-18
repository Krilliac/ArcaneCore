using ArcaneCore.Protocol;

namespace ArcaneCore.Game;

/// <summary>
/// A connected player as seen by the world/visibility system. The session implements this;
/// the <see cref="Map"/> uses it to read position and push packets to the client.
/// </summary>
public interface IWorldPlayer
{
    PlayerObject Player { get; }

    /// <summary>Send a packet to this player's client. Implementations must be thread-safe.</summary>
    ValueTask SendToClientAsync(WorldOpcode opcode, ReadOnlyMemory<byte> payload);
}
