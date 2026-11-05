using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>The verdict of <see cref="EfTransferDecisionStore.TryClaimCharacterAsync"/>.</summary>
public enum CharacterClaimOutcome
{
    /// <summary>The claim is now this worker's (there was none, or this worker's own claim was refreshed).</summary>
    Claimed = 0,

    /// <summary>The claim was already exactly this worker's token; nothing changed.</summary>
    AlreadyHeld = 1,

    /// <summary>The previous claimant is dead (its incarnation is no longer the active one of its scope, or it was reaped); the claim was taken over.</summary>
    TookOverDeadOwner = 2,

    /// <summary>Another live worker holds the claim; nothing changed. The login is refused (the character is online elsewhere).</summary>
    RefusedLiveOwner = 3,

    /// <summary>A transfer of the character is unresolved; coordinator recovery resolves it first. Nothing changed.</summary>
    RefusedTransferInFlight = 4,

    /// <summary>The token is not a worker-shard token (Realm scope): no claim exists in that mode, and no SQL was issued.</summary>
    NotWorkerScope = 5,
}

/// <param name="Outcome">The verdict.</param>
/// <param name="Claim">The claim now held (the presented token) when the character is claimed, else null.</param>
/// <param name="PreviousScope">The scope that held the claim before (a takeover) or still holds it (a refusal), else null.</param>
public sealed record CharacterClaimResult(CharacterClaimOutcome Outcome, CharacterOwnershipToken? Claim, string? PreviousScope)
{
    public bool IsClaimed => Outcome is CharacterClaimOutcome.Claimed or CharacterClaimOutcome.AlreadyHeld or CharacterClaimOutcome.TookOverDeadOwner;
}

/// <summary>
/// The login-time reap of a dead shard (design _wf/writer-lock-DESIGN.md section 5.4), offered to a claim that a live-looking owner refuses.
/// A worker may reap shard S only when all three hold: <c>Cluster:Fencing:ReapDeadOwners</c> (a policy is passed at all), S's advisory lock can be taken
/// (<paramref name="IsShardLockFree"/>: probe-take the lock and release it at once) and S's heartbeat is older than <paramref name="ReapAfter"/>
/// (checked by the ownership store's CAS). Holding the lock is the liveness proof; the heartbeat age stops a short network blip from triggering a reap.
/// </summary>
/// <param name="ReapAfter"><c>Cluster:Fencing:ReapAfterSeconds</c>.</param>
/// <param name="IsShardLockFree">True when the advisory lock of the given worker scope can be taken, so no process holds it. The caller owns the lock probing (it holds the connection options).</param>
public sealed record CharacterClaimReapPolicy(TimeSpan ReapAfter, Func<string, CancellationToken, Task<bool>> IsShardLockFree)
{
    /// <summary>
    /// The newest heartbeat (unix ms) a dead-looking shard may have at <paramref name="nowMs"/> and still be reaped: a holder whose heartbeat is at or before this is stale. The claim's
    /// reap and the read-only live-owner probe (<see cref="EfTransferDecisionStore.FindLiveOtherOwnerAsync"/>) both use this one cut-off, so they agree on what is stale.
    /// </summary>
    public long NewestReapableHeartbeat(long nowMs) => nowMs - (long)ReapAfter.TotalMilliseconds;
}
