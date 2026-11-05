namespace ArcaneCore.Kernel.Characters;

/// <summary>The durable authority carried by a character snapshot.</summary>
public sealed record CharacterOwnershipToken(string Scope, long Epoch, Guid Incarnation);

/// <summary>Characters-database ownership authority. It has no lease or automatic failover.</summary>
public interface ICharacterOwnershipStore
{
    Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active ownership of <paramref name="scope"/> as the database holds it now, or null when no one owns it. Read-only: unlike
    /// <see cref="TryAcquireAsync"/> it never claims an inactive row, so a caller that only verifies a token changes nothing.
    /// </summary>
    Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default);

    // The members below are the per-worker writer lock's (design _wf/writer-lock-DESIGN.md, step C3). They are default interface members so a
    // store that only knows the realm-wide 'world' row (the test fakes) keeps compiling and behaving as before; EfCharacterOwnershipStore overrides all of them.

    /// <summary>
    /// True when any ownership row is active: the combined World or at least one worker shard. The claim watch of an unfenced world uses it. The default
    /// answers for the 'world' row only, which is identical while no worker row exists.
    /// </summary>
    Task<bool> IsAnyOwnedAsync(CancellationToken cancellationToken = default) => IsOwnedAsync("world", cancellationToken);

    /// <summary>
    /// Claims a worker scope (<c>worker:&lt;shard&gt;</c>) owning <paramref name="ownedMaps"/> (null owns every map); the realm-wide 'world' scope takes only null.
    /// The default supports only a null map set (it is <see cref="TryAcquireAsync(string, string, Guid, CancellationToken)"/>).
    /// </summary>
    Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken = default)
        => ownedMaps is null
            ? TryAcquireAsync(scope, ownerNode, incarnation, cancellationToken)
            : throw new NotSupportedException("this ownership store has no per-worker owned map sets");

    /// <summary>
    /// A handoff's takeover of a worker shard that also sets the shard's owned maps to <paramref name="ownedMaps"/> (null owns every map), so the new owner does not inherit the old owner's map set.
    /// The default supports a store that has no per-worker map sets: it is the plain takeover, and refuses a map set it cannot record.
    /// </summary>
    Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken = default)
        => ownedMaps is null
            ? TryTakeoverAsync(observed, ownerNode, newIncarnation, cancellationToken)
            : throw new NotSupportedException("this ownership store has no per-worker owned map sets");

    /// <summary>Crash recovery of a worker shard: CAS the observed epoch and incarnation to the next epoch under <paramref name="newIncarnation"/>. The caller must hold the shard's advisory lock. Null when the row moved or the gate refuses.</summary>
    Task<CharacterOwnershipToken?> TryReclaimAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("this ownership store has no per-worker scopes");

    /// <summary>Deactivates a worker shard whose heartbeat is at least <paramref name="minHeartbeatAgeMs"/> old; false when the row moved or is too fresh.</summary>
    Task<bool> ReapAsync(CharacterOwnershipToken observed, long nowUnixMs, long minHeartbeatAgeMs, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("this ownership store has no per-worker scopes");

    /// <summary>Records that a worker shard's owner is alive; false when the token no longer owns the shard.</summary>
    Task<bool> HeartbeatAsync(CharacterOwnershipToken token, long nowUnixMs, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("this ownership store has no per-worker scopes");

    Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default);
}

/// <summary>
/// A save carrying an ownership token was rejected by the durable fence, or the process fenced itself off the characters database
/// (a subclass, <c>CharacterDatabaseFencedException</c>). Every writer that treats this as terminal treats both alike.
/// </summary>
public class CharacterOwnershipRejectedException : InvalidOperationException
{
    public CharacterOwnershipRejectedException(string scope, long epoch)
        : this(scope, epoch, $"character ownership token for scope '{scope}' at epoch {epoch} is no longer active")
    {
    }

    protected CharacterOwnershipRejectedException(string scope, long epoch, string message)
        : base(message)
    {
        Scope = scope;
        Epoch = epoch;
    }

    public string Scope { get; }
    public long Epoch { get; }
}
