using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The network side of an in-world player, as seen by the world. Implemented by the world
/// daemon's session; tests substitute their own.
/// </summary>
public interface IPlayerSession
{
    int AccountId { get; }

    /// <summary>Queue a packet for this client. Thread-safe; never blocks on the socket.</summary>
    void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>Handle the in-world packets queued since the last tick (world thread).</summary>
    void ProcessWorldPackets(Player player);

    /// <summary>Close the connection. Thread-safe and idempotent.</summary>
    void Kick();
}

/// <summary>Accepts character snapshots taken on the world thread for asynchronous persistence.</summary>
public interface ICharacterSaveQueue
{
    void Enqueue(CharacterState state);
}
