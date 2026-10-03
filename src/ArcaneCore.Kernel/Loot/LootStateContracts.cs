using ArcaneCore.Kernel.Economy;

namespace ArcaneCore.Kernel.Loot;

/// <summary>
/// Identity of one chest in a dungeon instance: the logical instance save and the database
/// spawn of the game object. Instance 0 (the shared copy of a map) never has durable state.
/// </summary>
public readonly record struct LootStateKey(uint InstanceId, uint SpawnGuid);

/// <summary>
/// One stack of a stored chest (a LootItem without its client display id, which is recomputed
/// from the item template). The looter lists are character ids, compared as sets.
/// </summary>
public sealed record LootStateItem(
    byte Slot,
    uint ItemId,
    uint Count,
    bool IsQuest,
    bool IsPerPlayer,
    bool IsLooted,
    IReadOnlyList<int> AllowedLooters,
    IReadOnlyList<int> LootedBy)
{
    public bool Equals(LootStateItem? other)
        => other is not null && Slot == other.Slot && ItemId == other.ItemId && Count == other.Count
            && IsQuest == other.IsQuest && IsPerPlayer == other.IsPerPlayer && IsLooted == other.IsLooted
            && LootStateRules.SameSet(AllowedLooters, other.AllowedLooters) && LootStateRules.SameSet(LootedBy, other.LootedBy);

    public override int GetHashCode() => HashCode.Combine(Slot, ItemId, Count, IsQuest, IsPerPlayer, IsLooted);
}

/// <summary>
/// The durable loot of one chest: what was generated, who may take it and what has been taken.
/// <see cref="Generation"/> increases each time the chest is generated afresh (first opening, or
/// reopening after it was consumed and respawned). <see cref="Consumed"/> is true exactly when
/// every item is taken; <see cref="RespawnAtUnix"/> (Unix seconds, <see cref="long.MaxValue"/> =
/// never) is then the earliest time the chest is available again. Lists are compared as sets.
/// </summary>
public sealed record LootStateRecord(
    LootStateKey Key,
    uint SourceEntry,
    int LootOwnerCharacterId,
    uint Generation,
    bool Consumed,
    long RespawnAtUnix,
    IReadOnlyList<int> Recipients,
    IReadOnlyList<LootStateItem> Items)
{
    public bool Equals(LootStateRecord? other)
    {
        if (other is null || Key != other.Key || SourceEntry != other.SourceEntry || LootOwnerCharacterId != other.LootOwnerCharacterId
            || Generation != other.Generation || Consumed != other.Consumed || RespawnAtUnix != other.RespawnAtUnix
            || !LootStateRules.SameSet(Recipients, other.Recipients) || Items.Count != other.Items.Count)
        {
            return false;
        }

        LootStateItem[] left = [.. Items.OrderBy(i => i.Slot)];
        LootStateItem[] right = [.. other.Items.OrderBy(i => i.Slot)];
        return left.Zip(right).All(pair => pair.First.Equals(pair.Second));
    }

    public override int GetHashCode() => HashCode.Combine(Key, SourceEntry, Generation, Consumed, RespawnAtUnix);
}

/// <summary>One item stack a character takes from a chest in a commit (the transition's cause).</summary>
public sealed record LootAward(int CharacterId, byte Slot, uint ItemId, uint Count);

/// <summary>
/// One atomic chest-loot operation. <see cref="Expected"/> must still equal the stored state
/// (null = no state), <see cref="Updated"/> must be a legal successor (<see cref="LootStateRules.IsLegalSuccessor"/>)
/// and each participant's inventory difference must be exactly its <see cref="Awards"/>. The
/// operation id is recorded in the same transaction and is the idempotency key. A fresh
/// generation or an owner release has no participants.
/// </summary>
public sealed record LootCommitRequest(
    Guid OperationId,
    LootStateKey Key,
    LootStateRecord? Expected,
    LootStateRecord Updated,
    IReadOnlyList<EconomyParticipant> Participants,
    IReadOnlyList<LootAward> Awards);

public enum LootCommitResult
{
    Committed,
    AlreadyCommitted,

    /// <summary>The stored state, or a participant's durable money/inventory, no longer matches; nothing was written.</summary>
    Conflict,

    CharacterMissing,

    /// <summary>The logical instance save does not exist in storage (reset, deleted, or its queued write was lost).</summary>
    ScopeMissing,

    /// <summary>Updated is not a legal successor of Expected for the awards, or the participants' inventory differs from the awards.</summary>
    InvalidTransition,
}

/// <summary>
/// Storage of consumed/remaining chest loot of dungeon instances (characters schema, loot-state
/// module). Commits are serializable transactions in a dedicated context; failures propagate
/// after rollback.
/// </summary>
public interface ILootStateStore
{
    Task<LootCommitResult> CommitAsync(LootCommitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the operation's ledger row exists (resolves a lost commit acknowledgement).</summary>
    Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Startup: delete the rows of instances that no longer exist, then return every stored chest.
    /// Must run before the instance system loads, so a reused instance id cannot inherit old state.
    /// </summary>
    Task<IReadOnlyList<LootStateRecord>> LoadInstanceStatesAsync(CancellationToken cancellationToken = default);
}
