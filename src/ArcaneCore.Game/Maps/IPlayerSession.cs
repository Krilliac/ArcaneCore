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

    /// <summary>
    /// Monotonic per-account session generation assigned when the session authenticated and claimed the account
    /// (<c>0</c> for unauthenticated or synthetic test doubles).
    /// </summary>
    long SessionGeneration => 0;

    /// <summary>The account's GM level.</summary>
    AccountSecurity Security { get; }

    /// <summary>
    /// False when nothing reads this session's SMSG_UPDATE_OBJECT packets (a lean playerbot): the map then keeps the viewer's visible
    /// set exactly as before but builds no create, values or out-of-range blocks for it. Real clients return true. World thread.
    /// </summary>
    bool WantsUpdateObjects => true;

    /// <summary>
    /// True for the session of a server-owned playerbot (no client behind it). Population-scaled respawn can leave bots out of its count
    /// (<c>World:Respawn:Dynamic:CountBots</c>). Real clients and test sessions return false. World thread.
    /// </summary>
    bool IsPlayerbot => false;

    /// <summary>
    /// True once the connection is closed for good (the client left or was kicked). A player detached for a cluster transfer while its session closed is not logged out by
    /// the close (it was in no map), so whoever puts it back must see this and log it out. Test doubles and playerbots return false. Thread-safe.
    /// </summary>
    bool IsDisconnected => false;

    /// <summary>Queue a packet for this client. Thread-safe; never blocks on the socket.</summary>
    void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>
    /// <see cref="Send"/> for a movement relay to an observer (vmangos <c>WorldSession::SendMovementPacket</c>): a session may batch it into
    /// SMSG_COMPRESSED_MOVES under <c>World:CompressMovementCount</c>. The default sends it at once.
    /// </summary>
    void SendMovement(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Send(opcode, payload);

    /// <summary>Handle the in-world packets queued since the last tick (world thread).</summary>
    void ProcessWorldPackets(Player player);

    /// <summary>Close the connection. Thread-safe and idempotent.</summary>
    void Kick();

    /// <summary>
    /// Close the connection and record <paramref name="reason"/>. Thread-safe and idempotent. The default closes without a reason;
    /// <c>WorldSession</c> overrides it so a caller that knows why does not raise a no-reason kick anomaly.
    /// </summary>
    void Kick(string reason) => Kick();

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

    /// <summary>
    /// <see cref="Enqueue(CharacterState)"/>, and <paramref name="onSettled"/> runs once when this snapshot has finished its write attempt (written, retained after a failure, or dropped: the
    /// attempt is over either way) or at once when the queue will never write it (closed, quarantined, held, retired). It may run on the queue's consumer thread or on the calling thread, before this method
    /// returns; it must be quick and must not throw. A snapshot superseded by a newer one of the same character settles with the newer one's write. <see cref="WorldRuntime"/> uses it to keep the manual clock
    /// from advancing past a save (<see cref="WorldPendingWork"/>). The default suits a queue that writes nothing asynchronously: it settles at once.
    /// </summary>
    void Enqueue(CharacterState state, Action onSettled)
    {
        ArgumentNullException.ThrowIfNull(onSettled);
        Enqueue(state);
        onSettled();
    }

    /// <summary>
    /// True while a snapshot of this character is waiting to be written (not merely being written), so another snapshot of
    /// it would only supersede that one. Queues that do not coalesce return false.
    /// </summary>
    bool IsPending(int characterId) => false;
}
