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

    /// <summary>Queue a packet for this client. Thread-safe; never blocks on the socket.</summary>
    void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload);

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
    /// True while a snapshot of this character is waiting to be written (not merely being written), so another snapshot of
    /// it would only supersede that one. Queues that do not coalesce return false.
    /// </summary>
    bool IsPending(int characterId) => false;
}
