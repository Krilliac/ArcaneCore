using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
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

    /// <summary>The account's GM level.</summary>
    AccountSecurity Security { get; }

    /// <summary>Queue a packet for this client. Thread-safe; never blocks on the socket.</summary>
    void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>Handle the in-world packets queued since the last tick (world thread).</summary>
    void ProcessWorldPackets(Player player);

    /// <summary>Close the connection. Thread-safe and idempotent.</summary>
    void Kick();

    /// <summary>
    /// The player left the world through a logout (not a disconnect): the client returns to
    /// the character screen (world thread; sends SMSG_LOGOUT_COMPLETE).
    /// </summary>
    void OnLoggedOut();
}

/// <summary>Accepts character snapshots taken on the world thread for asynchronous persistence.</summary>
public interface ICharacterSaveQueue
{
    void Enqueue(CharacterState state);
}
