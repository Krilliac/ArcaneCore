using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>
/// The durable <see cref="ITransferDecisionStore"/> and <see cref="IWorldHandoffStore"/> (characters schema 62,
/// <see cref="ClusterTransferDataModule"/>). Every call opens its own scope and context, so one instance serves the process and every state
/// change is committed before the call returns: a coordinator that crashes after any call finds the record where the call left it, and
/// <c>PlayerTransferCoordinator.RecoverTransferAsync</c> rolls it back (before commit) or forward (after).
/// <para>
/// Guarantees, on every provider: one unresolved transfer per character (a compare-and-swap claim on <c>cluster_transfer_owner</c>), one
/// unresolved handoff at a time (a unique open key), stages only in order, no abort after commit, and the commit of a per-player transfer
/// or of a whole handoff in one transaction with its ownership compare-and-swap. The journal is the handoff's own fence, like the
/// ownership store it extends, so its writes are exempt from the characters write fence (<see cref="CharacterWriteFence.Exempt"/>).
/// </para>
/// </summary>
public sealed class EfTransferDecisionStore(IServiceScopeFactory scopes, Func<long>? clockUtcMs = null) : ITransferDecisionStore, ITransferJournalScan, IWorldHandoffStore
{
    /// <summary>The characters ownership scope a whole-World handoff moves (<see cref="ICharacterOwnershipStore"/>).</summary>
    public const string WorldScope = "world";

    private readonly Func<long> _clock = clockUtcMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    // ---------------------------------------------------------------- per-character ownership

    /// <inheritdoc />
    public void SeedCharacterOwnership(ulong characterGuid, CharacterOwnershipToken ownershipToken)
    {
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid), "CharacterGuid must be non-zero.");
        ArgumentNullException.ThrowIfNull(ownershipToken);
        using IServiceScope scope = scopes.CreateScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        using IDisposable exempt = CharacterWriteFence.Exempt();
        long guid = (long)characterGuid;
        ClusterTransferOwnerRow? row = db.Set<ClusterTransferOwnerRow>().SingleOrDefault(r => r.CharacterGuid == guid);
        if (row is null)
        {
            row = new ClusterTransferOwnerRow { CharacterGuid = guid };
            db.Set<ClusterTransferOwnerRow>().Add(row);
        }
        else if (row.ActiveTransfer is not null)
        {
            throw new InvalidOperationException($"Character {characterGuid} has a transfer in flight; its ownership cannot be reseeded.");
        }

        row.Scope = ownershipToken.Scope;
        row.Epoch = ownershipToken.Epoch;
        row.Incarnation = ownershipToken.Incarnation.ToString("D");
        row.UpdatedAtUtcMs = _clock();
        db.SaveChanges();
    }

    /// <inheritdoc />
    public CharacterOwnershipToken? GetActiveOwnership(ulong characterGuid)
    {
        using IServiceScope scope = scopes.CreateScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        long guid = (long)characterGuid;
        ClusterTransferOwnerRow? row = db.Set<ClusterTransferOwnerRow>().AsNoTracking().SingleOrDefault(r => r.CharacterGuid == guid);
        return row is null ? null : OwnerToken(row);
    }

    // ---------------------------------------------------------------- per-character claim (design _wf/writer-lock-DESIGN.md section 1b)

    /// <summary>
    /// The login claim: a compare-and-swap on the character's <c>cluster_transfer_owner</c> row that makes <paramref name="token"/> (a worker-shard token)
    /// the holder. Allowed when the row is missing, when its scope is the presented scope (this shard, possibly a restarted incarnation), or when its
    /// (scope, incarnation) is not the active incarnation of that scope in <c>character_ownership</c> (the owner is dead or was reclaimed). Refused while
    /// <c>active_transfer</c> is set (coordinator recovery resolves that first). When a live-looking owner refuses and <paramref name="reapPolicy"/> is passed,
    /// the login-time reap (design section 5.4) runs: only if the shard's advisory lock is free (<see cref="CharacterClaimReapPolicy.IsShardLockFree"/>) and
    /// its heartbeat is older than <see cref="CharacterClaimReapPolicy.ReapAfter"/> is the shard row set inactive with its epoch bumped, and the claim then
    /// taken over. A realm-scope token yields <see cref="CharacterClaimOutcome.NotWorkerScope"/> and issues no SQL.
    /// <para>
    /// The probe runs before the claim transaction opens; everything after it runs in one transaction (fence-exempt: the claim table is the claim's own fence).
    /// A concurrent first claim of the same character loses on the primary key and is reported as <see cref="CharacterClaimOutcome.RefusedLiveOwner"/>.
    /// </para>
    /// </summary>
    public async Task<CharacterClaimResult> TryClaimCharacterAsync(
        ulong characterGuid, CharacterOwnershipToken token, CharacterClaimReapPolicy? reapPolicy = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid), "CharacterGuid must be non-zero.");
        ArgumentNullException.ThrowIfNull(token);
        if (!CharacterOwnershipScopes.IsWorker(token.Scope))
            return new CharacterClaimResult(CharacterClaimOutcome.NotWorkerScope, null, null);

        // The shard-lock probe is an external round trip, so it runs before the claim transaction opens (on SQLite an open transaction holds the database write lock).
        // It is only a pre-check: the reap's compare-and-swap below still requires the holder's exact epoch, incarnation and a stale heartbeat, so a shard that restarted
        // after the probe (new incarnation) is never reaped on a stale "lock is free" answer.
        string? probedScope = null;
        bool probedFree = false;
        if (reapPolicy is not null)
        {
            string? heldScope = await ReadClaimScopeAsync((long)characterGuid, cancellationToken).ConfigureAwait(false);
            if (heldScope is not null && CharacterOwnershipScopes.IsWorker(heldScope) && !string.Equals(heldScope, token.Scope, StringComparison.Ordinal))
            {
                probedScope = heldScope;
                probedFree = await reapPolicy.IsShardLockFree(heldScope, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            return await ClaimInTransactionAsync(characterGuid, token, reapPolicy, probedScope, probedFree, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            // A concurrent first claim of the same character won the primary key between this call's read and its insert (or a competing write raced the same row): the other
            // login holds the claim, so this one is refused like any live owner. A failure that left no claim row behind is a real storage fault and propagates.
            string? now = await ReadClaimScopeAsync((long)characterGuid, cancellationToken).ConfigureAwait(false);
            if (now is null)
                throw;
            return new CharacterClaimResult(CharacterClaimOutcome.RefusedLiveOwner, null, now);
        }
    }

    private async Task<string?> ReadClaimScopeAsync(long guid, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        return await db.Set<ClusterTransferOwnerRow>().AsNoTracking().Where(r => r.CharacterGuid == guid).Select(r => r.Scope)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CharacterClaimResult> ClaimInTransactionAsync(
        ulong characterGuid, CharacterOwnershipToken token, CharacterClaimReapPolicy? reapPolicy, string? probedScope, bool probedFree, CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async (db, ct) =>
        {
            long guid = (long)characterGuid;
            string incarnation = token.Incarnation.ToString("D");
            long now = _clock();
            ClusterTransferOwnerRow? row = await db.Set<ClusterTransferOwnerRow>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.CharacterGuid == guid, ct).ConfigureAwait(false);

            if (row is null)
            {
                db.Set<ClusterTransferOwnerRow>().Add(new ClusterTransferOwnerRow
                {
                    CharacterGuid = guid,
                    Scope = token.Scope,
                    Epoch = token.Epoch,
                    Incarnation = incarnation,
                    UpdatedAtUtcMs = now,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new CharacterClaimResult(CharacterClaimOutcome.Claimed, token, null);
            }

            if (row.ActiveTransfer is not null)
                return new CharacterClaimResult(CharacterClaimOutcome.RefusedTransferInFlight, null, row.Scope);

            if (OwnerToken(row) == token)
                return new CharacterClaimResult(CharacterClaimOutcome.AlreadyHeld, token, null);

            CharacterClaimOutcome outcome = CharacterClaimOutcome.Claimed;
            string? previousScope = null;
            if (!string.Equals(row.Scope, token.Scope, StringComparison.Ordinal))
            {
                string heldScope = row.Scope;
                string heldIncarnation = row.Incarnation;
                CharacterOwnershipRow? holder = await db.Set<CharacterOwnershipRow>().AsNoTracking()
                    .SingleOrDefaultAsync(o => o.Scope == heldScope && o.Active && o.OwnerIncarnation == heldIncarnation, ct).ConfigureAwait(false);
                if (holder is not null)
                {
                    // A live-looking owner. Only the login-time reap can still free the claim.
                    if (reapPolicy is null || !CharacterOwnershipScopes.IsWorker(heldScope))
                        return new CharacterClaimResult(CharacterClaimOutcome.RefusedLiveOwner, null, row.Scope);
                    if (!probedFree || !string.Equals(probedScope, heldScope, StringComparison.Ordinal))
                        return new CharacterClaimResult(CharacterClaimOutcome.RefusedLiveOwner, null, row.Scope); // the lock was not free, or the claim moved to another shard since the probe

                    long newestReapable = now - (long)reapPolicy.ReapAfter.TotalMilliseconds;
                    long holderEpoch = holder.Epoch;
                    int reaped = await db.Set<CharacterOwnershipRow>()
                        .Where(o => o.Scope == heldScope && o.Active && o.Epoch == holderEpoch && o.Epoch < long.MaxValue
                            && o.WriteSequence < long.MaxValue && o.OwnerIncarnation == heldIncarnation
                            && o.HeartbeatAtMs != null && o.HeartbeatAtMs <= newestReapable)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(o => o.OwnerNode, (string?)null)
                            .SetProperty(o => o.OwnerIncarnation, (string?)null)
                            .SetProperty(o => o.Active, false)
                            .SetProperty(o => o.Epoch, o => o.Epoch + 1)
                            .SetProperty(o => o.WriteSequence, o => o.WriteSequence + 1)
                            .SetProperty(o => o.OwnedMaps, (string?)null), ct)
                        .ConfigureAwait(false);
                    if (reaped == 0)
                        return new CharacterClaimResult(CharacterClaimOutcome.RefusedLiveOwner, null, row.Scope);
                }

                outcome = CharacterClaimOutcome.TookOverDeadOwner;
                previousScope = row.Scope;
            }

            string rowScope = row.Scope;
            long rowEpoch = row.Epoch;
            string rowIncarnation = row.Incarnation;
            int claimed = await db.Set<ClusterTransferOwnerRow>()
                .Where(r => r.CharacterGuid == guid && r.ActiveTransfer == null && r.Scope == rowScope && r.Epoch == rowEpoch && r.Incarnation == rowIncarnation)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Scope, token.Scope)
                    .SetProperty(r => r.Epoch, token.Epoch)
                    .SetProperty(r => r.Incarnation, incarnation)
                    .SetProperty(r => r.UpdatedAtUtcMs, now), ct)
                .ConfigureAwait(false);
            // The row moved between the read and the swap (another login, or a transfer began): the other party won.
            return claimed == 0
                ? new CharacterClaimResult(CharacterClaimOutcome.RefusedLiveOwner, null, rowScope)
                : new CharacterClaimResult(outcome, token, previousScope);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops this worker's claim on the character (logout, after the final save is durable). True when a claim row held by exactly
    /// <paramref name="token"/> with no transfer in flight was deleted; false when there was none, another holder has it, or the token is not a worker
    /// token (no SQL is issued then).
    /// </summary>
    public async Task<bool> ReleaseCharacterClaimAsync(ulong characterGuid, CharacterOwnershipToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid), "CharacterGuid must be non-zero.");
        ArgumentNullException.ThrowIfNull(token);
        if (!CharacterOwnershipScopes.IsWorker(token.Scope))
            return false;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        using IDisposable exempt = CharacterWriteFence.Exempt();
        long guid = (long)characterGuid;
        string claimScope = token.Scope;
        string incarnation = token.Incarnation.ToString("D");
        int deleted = await db.Set<ClusterTransferOwnerRow>()
            .Where(r => r.CharacterGuid == guid && r.Scope == claimScope && r.Incarnation == incarnation && r.ActiveTransfer == null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    // ---------------------------------------------------------------- ITransferDecisionStore

    /// <inheritdoc />
    public Task<TransferDecisionRecord> CreateReservationAsync(
        Guid transferId, ulong characterGuid, int accountId, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId,
        uint sourceMapId, uint sourceInstanceId, uint targetMapId, uint targetInstanceId, CharacterOwnershipToken sourceOwnershipToken,
        CancellationToken cancellationToken = default)
        => CreateReservationCoreAsync(null, transferId, characterGuid, accountId, sessionGeneration, sourceWorkerId, targetWorkerId,
            sourceMapId, sourceInstanceId, targetMapId, targetInstanceId, sourceOwnershipToken, cancellationToken);

    private async Task<TransferDecisionRecord> CreateReservationCoreAsync(
        Guid? handoffId, Guid transferId, ulong characterGuid, int accountId, long sessionGeneration, WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId, uint sourceMapId, uint sourceInstanceId, uint targetMapId, uint targetInstanceId,
        CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (transferId == Guid.Empty)
            throw new ArgumentException("TransferId must be non-empty.", nameof(transferId));
        if (characterGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(characterGuid));
        ArgumentNullException.ThrowIfNull(sourceOwnershipToken);
        ValidateWorker(sourceWorkerId, nameof(sourceWorkerId));
        ValidateWorker(targetWorkerId, nameof(targetWorkerId));

        return await InTransactionAsync(async (db, ct) =>
        {
            string id = Key(transferId);
            if (await db.Set<ClusterTransferDecisionRow>().AnyAsync(r => r.TransferId == id, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"Transfer '{transferId}' already exists.");

            if (handoffId is { } handoff)
            {
                string handoffKey = Key(handoff);
                ClusterWorldHandoffRow? owner = await db.Set<ClusterWorldHandoffRow>().AsNoTracking()
                    .SingleOrDefaultAsync(r => r.HandoffId == handoffKey, ct).ConfigureAwait(false);
                if (owner is null || owner.State != (int)PlayerTransferDecisionState.PreCommit)
                    throw new InvalidOperationException($"Handoff '{handoff}' is not open; no transfer can be recorded under it.");
            }

            long guid = (long)characterGuid;
            long now = _clock();
            ClusterTransferOwnerRow? owned = await db.Set<ClusterTransferOwnerRow>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.CharacterGuid == guid, ct).ConfigureAwait(false);
            if (owned is null)
            {
                // Worker scope: a claim row only exists after the login claim (TryClaimCharacterAsync), so a missing row means this worker never
                // claimed the character and must not transfer it (design _wf/writer-lock-DESIGN.md section 1b). Seeding it here would mint a claim.
                if (CharacterOwnershipScopes.IsWorker(sourceOwnershipToken.Scope))
                    throw new CharacterClaimRejectedException(checked((int)characterGuid), sourceOwnershipToken.Scope);

                db.Set<ClusterTransferOwnerRow>().Add(new ClusterTransferOwnerRow
                {
                    CharacterGuid = guid,
                    Scope = sourceOwnershipToken.Scope,
                    Epoch = sourceOwnershipToken.Epoch,
                    Incarnation = sourceOwnershipToken.Incarnation.ToString("D"),
                    ActiveTransfer = id,
                    UpdatedAtUtcMs = now,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else
            {
                if (owned.ActiveTransfer is not null)
                    throw new InvalidOperationException($"Character {characterGuid} already has an unresolved transfer in flight.");
                if (OwnerToken(owned) != sourceOwnershipToken)
                    throw new CharacterOwnershipRejectedException(sourceOwnershipToken.Scope, sourceOwnershipToken.Epoch);

                // The in-flight claim: compare-and-swap on the token the caller presented and on "no transfer in flight".
                string incarnation = sourceOwnershipToken.Incarnation.ToString("D");
                int claimed = await db.Set<ClusterTransferOwnerRow>()
                    .Where(r => r.CharacterGuid == guid && r.ActiveTransfer == null && r.Scope == sourceOwnershipToken.Scope
                        && r.Epoch == sourceOwnershipToken.Epoch && r.Incarnation == incarnation)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ActiveTransfer, id).SetProperty(r => r.UpdatedAtUtcMs, now), ct)
                    .ConfigureAwait(false);
                if (claimed == 0)
                    throw new InvalidOperationException($"Character {characterGuid} already has an unresolved transfer in flight.");
            }

            var row = new ClusterTransferDecisionRow
            {
                TransferId = id,
                HandoffId = handoffId is { } h ? Key(h) : null,
                CharacterGuid = guid,
                AccountId = accountId,
                SessionGeneration = sessionGeneration,
                SourceWorker = sourceWorkerId.Value,
                TargetWorker = targetWorkerId.Value,
                SourceMapId = sourceMapId,
                SourceInstanceId = sourceInstanceId,
                TargetMapId = targetMapId,
                TargetInstanceId = targetInstanceId,
                Stage = (int)PlayerTransferStage.Reserve,
                State = (int)PlayerTransferDecisionState.PreCommit,
                SourceScope = sourceOwnershipToken.Scope,
                SourceEpoch = sourceOwnershipToken.Epoch,
                SourceIncarnation = sourceOwnershipToken.Incarnation.ToString("D"),
                UpdatedAtUtcMs = now,
            };
            db.Set<ClusterTransferDecisionRow>().Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransferDecisionRecord> RebindTargetInstanceAsync(Guid transferId, uint targetInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            if (row.TargetInstanceId == targetInstanceId)
                return ToRecord(row);

            if (row.State != (int)PlayerTransferDecisionState.PreCommit || row.Stage != (int)PlayerTransferStage.Reserve)
                throw new InvalidOperationException($"Cannot rebind the target instance of transfer '{transferId}' in state {(PlayerTransferDecisionState)row.State} at stage {(PlayerTransferStage)row.Stage}; only a pre-commit Reserve may.");

            await EnsureOwnerAsync(db, row.CharacterGuid, SourceToken(row), ct).ConfigureAwait(false);
            row.TargetInstanceId = targetInstanceId;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransferDecisionRecord> AdvancePreCommitStageAsync(
        Guid transferId, PlayerTransferStage nextStage, PlayerTransferBundle? capturedBundle = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nextStage is not (PlayerTransferStage.SuspendAndCapture or PlayerTransferStage.InstallPassive or PlayerTransferStage.StageWorldPort))
            throw new ArgumentOutOfRangeException(nameof(nextStage), $"AdvancePreCommitStageAsync only accepts stages 2..4; got {nextStage}.");

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            if (row.State != (int)PlayerTransferDecisionState.PreCommit)
                throw new InvalidOperationException($"Cannot advance pre-commit stage for transfer '{transferId}' in state {(PlayerTransferDecisionState)row.State}.");

            PlayerTransferStage expectedPrevious = nextStage switch
            {
                PlayerTransferStage.SuspendAndCapture => PlayerTransferStage.Reserve,
                PlayerTransferStage.InstallPassive => PlayerTransferStage.SuspendAndCapture,
                _ => PlayerTransferStage.InstallPassive,
            };
            if (row.Stage != (int)expectedPrevious)
                throw new InvalidOperationException($"Invalid stage transition for transfer '{transferId}': {(PlayerTransferStage)row.Stage} -> {nextStage} (expected {expectedPrevious}).");

            await EnsureOwnerAsync(db, row.CharacterGuid, SourceToken(row), ct).ConfigureAwait(false);
            if (capturedBundle is not null)
                row.Bundle = PlayerTransferBundleJson.Serialize(capturedBundle);
            if (nextStage == PlayerTransferStage.SuspendAndCapture && row.Bundle is null)
                throw new InvalidOperationException($"Stage {PlayerTransferStage.SuspendAndCapture} requires a non-null PlayerTransferBundle.");

            row.Stage = (int)nextStage;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> CommitDecisionAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default, string? targetScope = null)
        => CommitDecisionCoreAsync(transferId, targetIncarnation, targetScope, cancellationToken);

    private async Task<TransferDecisionRecord> CommitDecisionCoreAsync(Guid transferId, Guid targetIncarnation, string? targetScope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetIncarnation == Guid.Empty)
            throw new ArgumentException("Target incarnation must be non-empty.", nameof(targetIncarnation));
        if (targetScope is not null && !CharacterOwnershipScopes.IsValid(targetScope))
            throw new ArgumentException($"'{targetScope}' is not a characters ownership scope.", nameof(targetScope));

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            string target = targetIncarnation.ToString("D");
            if (row.State is (int)PlayerTransferDecisionState.Committed or (int)PlayerTransferDecisionState.Completed && row.TargetIncarnation == target)
                return ToRecord(row);

            if (row.HandoffId is not null)
                throw new InvalidOperationException($"Transfer '{transferId}' belongs to handoff '{row.HandoffId}'; it commits with the handoff's epoch takeover, never on its own.");
            if (row.State != (int)PlayerTransferDecisionState.PreCommit || row.Stage != (int)PlayerTransferStage.StageWorldPort)
                throw new InvalidOperationException($"Transfer '{transferId}' cannot commit from stage {(PlayerTransferStage)row.Stage} (state={(PlayerTransferDecisionState)row.State}); expected StageWorldPort.");
            if (row.Bundle is null)
                throw new InvalidOperationException($"Transfer '{transferId}' cannot commit without a captured PlayerTransferBundle.");

            await CommitRowAsync(db, row, target, targetScope, ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves one transfer to Committed: the per-character ownership compare-and-swap and the decision row, in the caller's transaction.
    /// The character's claim moves to <paramref name="targetScope"/> (the source scope when null) and the target incarnation in the same statement, so from this
    /// commit on the source's claim check (<see cref="CharacterWriteClaim"/>) refuses every save the source still holds: the refusal is durable, not in memory.
    /// <para>
    /// The committed token: within one scope (the realm-wide 'world' scope) the claim's epoch moves N to N + 1. Into another worker scope it is that worker's own
    /// live scope token (its active <c>character_ownership</c> row under <paramref name="targetIncarnation"/>), so the token the target gives the arrived player is the one
    /// the write fence verifies its saves against (the scope row) and the one its next transfer reserves with (the claim row): one token, one writer (writer-lock design
    /// section 1b). A worker scope with no live row under that incarnation cannot write the character at all, so the commit is refused and the transfer stays abortable.
    /// </para>
    /// </summary>
    private async Task CommitRowAsync(CharacterDbContext db, ClusterTransferDecisionRow row, string targetIncarnation, string? targetScope, CancellationToken ct)
    {
        long now = _clock();
        long targetEpoch = checked(row.SourceEpoch + 1);
        string newScope = targetScope ?? row.SourceScope;
        if (!string.Equals(newScope, row.SourceScope, StringComparison.Ordinal) && CharacterOwnershipScopes.IsWorker(newScope))
        {
            long? live = await db.Set<CharacterOwnershipRow>().AsNoTracking()
                .Where(o => o.Scope == newScope && o.Active && o.OwnerIncarnation == targetIncarnation)
                .Select(o => (long?)o.Epoch)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            targetEpoch = live ?? throw new CharacterOwnershipRejectedException(newScope, 0);
        }

        int swapped = await db.Set<ClusterTransferOwnerRow>()
            .Where(r => r.CharacterGuid == row.CharacterGuid && r.ActiveTransfer == row.TransferId && r.Scope == row.SourceScope
                && r.Epoch == row.SourceEpoch && r.Incarnation == row.SourceIncarnation)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Scope, newScope)
                .SetProperty(r => r.Epoch, targetEpoch)
                .SetProperty(r => r.Incarnation, targetIncarnation)
                .SetProperty(r => r.UpdatedAtUtcMs, now), ct)
            .ConfigureAwait(false);
        if (swapped == 0)
            throw new CharacterOwnershipRejectedException(row.SourceScope, row.SourceEpoch);

        row.Stage = (int)PlayerTransferStage.CommitDecision;
        row.State = (int)PlayerTransferDecisionState.Committed;
        row.TargetEpoch = targetEpoch;
        row.TargetIncarnation = targetIncarnation;
        row.TargetScope = string.Equals(newScope, row.SourceScope, StringComparison.Ordinal) ? null : newScope;
        row.UpdatedAtUtcMs = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only a transfer committed into a worker scope can rebind: that scope's <c>character_ownership</c> row names the one live incarnation, so the new token is
    /// exactly the restarted target's scope token (the one its write fence checks), and the dead incarnation it replaces can no longer write. A transfer committed
    /// within the realm-wide 'world' scope has no such row to prove the new incarnation is the scope's owner, so it is refused with <see cref="NotSupportedException"/>.
    /// </remarks>
    public async Task<TransferDecisionRecord> RebindCommittedTargetAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetIncarnation == Guid.Empty)
            throw new ArgumentException("Target incarnation must be non-empty.", nameof(targetIncarnation));

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            string target = targetIncarnation.ToString("D");
            if (row.State != (int)PlayerTransferDecisionState.Committed || row.TargetEpoch is not { } oldEpoch || row.TargetIncarnation is not { } oldIncarnation)
                throw new InvalidOperationException($"Transfer '{transferId}' cannot rebind its target from state {(PlayerTransferDecisionState)row.State}; only a committed transfer may.");
            if (oldIncarnation == target)
                return ToRecord(row);
            if (row.TargetScope is not { } scope || !CharacterOwnershipScopes.IsWorker(scope))
                throw new NotSupportedException($"Transfer '{transferId}' was committed within scope '{row.TargetScope ?? row.SourceScope}', not into a worker scope; it cannot rebind to another incarnation.");

            long newEpoch = await db.Set<CharacterOwnershipRow>().AsNoTracking()
                .Where(o => o.Scope == scope && o.Active && o.OwnerIncarnation == target)
                .Select(o => (long?)o.Epoch)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new CharacterOwnershipRejectedException(scope, 0);

            long now = _clock();
            string id = row.TransferId;
            int swapped = await db.Set<ClusterTransferOwnerRow>()
                .Where(r => r.CharacterGuid == row.CharacterGuid && r.ActiveTransfer == id && r.Scope == scope
                    && r.Epoch == oldEpoch && r.Incarnation == oldIncarnation)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Epoch, newEpoch)
                    .SetProperty(r => r.Incarnation, target)
                    .SetProperty(r => r.UpdatedAtUtcMs, now), ct)
                .ConfigureAwait(false);
            if (swapped == 0)
                throw new CharacterOwnershipRejectedException(scope, oldEpoch);

            row.TargetEpoch = newEpoch;
            row.TargetIncarnation = target;
            row.UpdatedAtUtcMs = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransferDecisionRecord> MarkCompletedAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            if (row.State == (int)PlayerTransferDecisionState.Completed)
                return ToRecord(row);
            if (row.State != (int)PlayerTransferDecisionState.Committed || row.TargetEpoch is null || row.TargetIncarnation is null)
                throw new InvalidOperationException($"Transfer '{transferId}' cannot complete from state {(PlayerTransferDecisionState)row.State} (stage={(PlayerTransferStage)row.Stage}); CommitDecision must succeed first.");

            await EnsureOwnerAsync(db, row.CharacterGuid, TargetToken(row)!, ct).ConfigureAwait(false);
            await ReleaseClaimAsync(db, row, ct).ConfigureAwait(false);
            row.Stage = (int)PlayerTransferStage.ActivateAndRetire;
            row.State = (int)PlayerTransferDecisionState.Completed;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransferDecisionRecord> AbortPreCommitAsync(Guid transferId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason))
            reason = "aborted";

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterTransferDecisionRow row = await RequireDecisionAsync(db, transferId, ct).ConfigureAwait(false);
            if (row.State == (int)PlayerTransferDecisionState.Aborted)
                return ToRecord(row);
            if (row.State is (int)PlayerTransferDecisionState.Committed or (int)PlayerTransferDecisionState.Completed)
                throw new InvalidOperationException($"Transfer '{transferId}' has already reached {(PlayerTransferStage)row.Stage} ({(PlayerTransferDecisionState)row.State}); post-commit abort is forbidden and must roll forward.");

            AbortRow(row, reason);
            await ReleaseClaimAsync(db, row, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransferDecisionRecord?> GetDecisionAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        string id = Key(transferId);
        ClusterTransferDecisionRow? row = await db.Set<ClusterTransferDecisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.TransferId == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransferDecisionRecord>> GetUnresolvedForCharacterAsync(ulong characterGuid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        long guid = (long)characterGuid;
        List<ClusterTransferDecisionRow> rows = await db.Set<ClusterTransferDecisionRow>().AsNoTracking()
            .Where(r => r.CharacterGuid == guid
                && (r.State == (int)PlayerTransferDecisionState.PreCommit || r.State == (int)PlayerTransferDecisionState.Committed))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.UpdatedAtUtcMs).Select(ToRecord)];
    }

    /// <inheritdoc />
    /// <remarks>One query over the indexed <c>state</c> column (schema 65), so the cost follows the unresolved rows, not the ever-growing table. Each call reads committed rows only.</remarks>
    public async Task<IReadOnlyList<TransferDecisionRecord>> ListUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        List<ClusterTransferDecisionRow> rows = await db.Set<ClusterTransferDecisionRow>().AsNoTracking()
            .Where(r => r.State == (int)PlayerTransferDecisionState.PreCommit || r.State == (int)PlayerTransferDecisionState.Committed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.UpdatedAtUtcMs).Select(ToRecord)];
    }

    /// <inheritdoc />
    /// <remarks>One COUNT query over the indexed <c>state</c> column (schema 65), so the cost follows the unresolved rows, not the ever-growing table.</remarks>
    public async Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        return await db.Set<ClusterTransferDecisionRow>().AsNoTracking()
            .CountAsync(r => r.State == (int)PlayerTransferDecisionState.PreCommit || r.State == (int)PlayerTransferDecisionState.Committed, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- IWorldHandoffStore

    /// <inheritdoc />
    public async Task<WorldHandoffRecord> BeginAsync(
        Guid handoffId, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, CharacterOwnershipToken sourceOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (handoffId == Guid.Empty)
            throw new ArgumentException("HandoffId must be non-empty.", nameof(handoffId));
        ArgumentNullException.ThrowIfNull(sourceOwnershipToken);
        ValidateWorker(sourceWorkerId, nameof(sourceWorkerId));
        ValidateWorker(targetWorkerId, nameof(targetWorkerId));
        if (sourceWorkerId == targetWorkerId)
            throw new ArgumentException("A handoff needs two different worlds.", nameof(targetWorkerId));
        if (!CharacterOwnershipScopes.IsValid(sourceOwnershipToken.Scope) || sourceOwnershipToken.Epoch <= 0 || sourceOwnershipToken.Incarnation == Guid.Empty)
            throw new ArgumentException("A handoff moves one characters ownership scope ('world' or 'worker:<shard>'); the source token must be a held token of such a scope.", nameof(sourceOwnershipToken));
        string openScope = sourceOwnershipToken.Scope;
        bool isWorld = openScope == WorldScope;

        return await InTransactionAsync(async (db, ct) =>
        {
            // One open handoff per scope (design _wf/writer-lock-DESIGN.md step C7): two shards may hand off at once. A 'world' handoff also sees one opened before
            // schema 73 (open key, no open scope). The unique indexes on open_scope and open_key refuse a concurrent second one at SaveChanges.
            bool open = isWorld
                ? await db.Set<ClusterWorldHandoffRow>().AnyAsync(r => r.OpenKey != null || r.OpenScope == openScope, ct).ConfigureAwait(false)
                : await db.Set<ClusterWorldHandoffRow>().AnyAsync(r => r.OpenScope == openScope, ct).ConfigureAwait(false);
            if (open)
                throw new InvalidOperationException($"Another handoff of '{openScope}' is unresolved; finish or abort it first.");

            long now = _clock();
            var row = new ClusterWorldHandoffRow
            {
                HandoffId = Key(handoffId),
                OpenKey = isWorld ? ClusterTransferDataModule.OpenHandoffKey : null, // the legacy unique key serialises only realm-wide handoffs
                OpenScope = openScope,
                SourceWorker = sourceWorkerId.Value,
                TargetWorker = targetWorkerId.Value,
                Stage = (int)WorldHandoffStage.Begun,
                State = (int)PlayerTransferDecisionState.PreCommit,
                SourceScope = sourceOwnershipToken.Scope,
                SourceEpoch = sourceOwnershipToken.Epoch,
                SourceIncarnation = sourceOwnershipToken.Incarnation.ToString("D"),
                CreatedAtUtcMs = now,
                UpdatedAtUtcMs = now,
            };
            db.Set<ClusterWorldHandoffRow>().Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // the unique open key refuses a concurrent second handoff here
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nextStage is not (WorldHandoffStage.Warm or WorldHandoffStage.AdmissionsStopped or WorldHandoffStage.Frozen or WorldHandoffStage.Flushed))
            throw new ArgumentOutOfRangeException(nameof(nextStage), $"Only the pre-commit stages Warm..Flushed advance here; got {nextStage}.");

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterWorldHandoffRow row = await RequireHandoffAsync(db, handoffId, ct).ConfigureAwait(false);
            if (row.State != (int)PlayerTransferDecisionState.PreCommit)
                throw new InvalidOperationException($"Handoff '{handoffId}' is {(PlayerTransferDecisionState)row.State}; it cannot advance to {nextStage}.");
            if (row.Stage != (int)nextStage - 1)
                throw new InvalidOperationException($"Invalid handoff stage transition for '{handoffId}': {(WorldHandoffStage)row.Stage} -> {nextStage}.");

            row.Stage = (int)nextStage;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
        Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
        => CommitEpochTakeoverCoreAsync(handoffId, targetOwnerNode, targetIncarnation, setMaps: false, targetOwnedMaps: null, cancellationToken);

    /// <inheritdoc />
    public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
        Guid handoffId, string targetOwnerNode, Guid targetIncarnation, IReadOnlyCollection<uint>? targetOwnedMaps, CancellationToken cancellationToken)
        => CommitEpochTakeoverCoreAsync(handoffId, targetOwnerNode, targetIncarnation, setMaps: true, targetOwnedMaps, cancellationToken);

    private async Task<WorldHandoffRecord?> CommitEpochTakeoverCoreAsync(
        Guid handoffId, string targetOwnerNode, Guid targetIncarnation, bool setMaps, IReadOnlyCollection<uint>? targetOwnedMaps, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(targetOwnerNode) || targetOwnerNode.Length > 128)
            throw new ArgumentException("The target owner node is required (at most 128 characters).", nameof(targetOwnerNode));
        if (targetIncarnation == Guid.Empty)
            throw new ArgumentException("Target incarnation must be non-empty.", nameof(targetIncarnation));

        return await InTransactionAsync<WorldHandoffRecord?>(async (db, ct) =>
        {
            ClusterWorldHandoffRow row = await RequireHandoffAsync(db, handoffId, ct).ConfigureAwait(false);
            string target = targetIncarnation.ToString("D");
            if (row.State is (int)PlayerTransferDecisionState.Committed or (int)PlayerTransferDecisionState.Completed)
            {
                return row.TargetIncarnation == target
                    ? ToRecord(row)
                    : throw new InvalidOperationException($"Handoff '{handoffId}' was committed to another incarnation.");
            }

            if (row.State != (int)PlayerTransferDecisionState.PreCommit || row.Stage != (int)WorldHandoffStage.Flushed)
                throw new InvalidOperationException($"Handoff '{handoffId}' cannot take over from stage {(WorldHandoffStage)row.Stage} (state={(PlayerTransferDecisionState)row.State}); expected Flushed.");

            string handoffKey = row.HandoffId;
            List<ClusterTransferDecisionRow> transfers = await db.Set<ClusterTransferDecisionRow>()
                .Where(r => r.HandoffId == handoffKey).ToListAsync(ct).ConfigureAwait(false);
            ClusterTransferDecisionRow? notReady = transfers.FirstOrDefault(t =>
                t.State != (int)PlayerTransferDecisionState.PreCommit || t.Stage != (int)PlayerTransferStage.StageWorldPort || t.Bundle is null);
            if (notReady is not null)
                throw new InvalidOperationException($"Handoff '{handoffId}' cannot take over: transfer '{notReady.TransferId}' stands at {(PlayerTransferStage)notReady.Stage} ({(PlayerTransferDecisionState)notReady.State}); every transfer must be at StageWorldPort.");

            // The point of no return: the characters ownership moves from the source token (epoch N) to the target (N + 1) by compare-and-swap.
            long targetEpoch = checked(row.SourceEpoch + 1);
            using (CharacterWriteFence.Exempt())
            {
                string sourceIncarnation = row.SourceIncarnation;
                string sourceScope = row.SourceScope;
                long sourceEpoch = row.SourceEpoch;
                bool writeMaps = setMaps && CharacterOwnershipScopes.IsWorker(sourceScope);
                if (setMaps && !writeMaps && targetOwnedMaps is not null)
                    throw new ArgumentException("the world ownership scope owns every map and takes no owned map set", nameof(targetOwnedMaps));
                string? storedMaps = writeMaps && targetOwnedMaps is not null ? EfCharacterOwnershipStore.FormatMaps(targetOwnedMaps) : null;
                // Gated like a worker claim, before anything is written: the realm row must be inactive (this locks it for the transaction) and the new maps must not overlap
                // another active worker. A refusal changes nothing and the handoff stays abortable.
                if (writeMaps
                    && (!await EfCharacterOwnershipStore.LockGateAsync(db, ct).ConfigureAwait(false)
                        || await EfCharacterOwnershipStore.OverlapsOtherActiveWorkerAsync(db, sourceScope, targetOwnedMaps, ct).ConfigureAwait(false)))
                    return null;
                int swapped = await db.Set<CharacterOwnershipRow>()
                    .Where(o => o.Scope == sourceScope && o.Active && o.Epoch == sourceEpoch && o.OwnerIncarnation == sourceIncarnation
                        && o.Epoch < long.MaxValue && o.WriteSequence < long.MaxValue)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.OwnerNode, targetOwnerNode)
                        .SetProperty(o => o.OwnerIncarnation, target)
                        .SetProperty(o => o.Epoch, o => o.Epoch + 1)
                        .SetProperty(o => o.WriteSequence, o => o.WriteSequence + 1), ct)
                    .ConfigureAwait(false);
                if (swapped == 0)
                    return null; // the source no longer holds the ownership: nothing changes, the handoff stays abortable
                if (CharacterOwnershipScopes.IsWorker(sourceScope))
                {
                    // The new owner of a live shard starts with a fresh heartbeat (as CharacterOwnershipDataModule's takeover gives it) and, when the caller names them, its own
                    // owned maps in this same transaction (a standby serves its configured maps, so the durable row must not keep the old owner's set).
                    long heartbeat = _clock();
                    if (writeMaps)
                    {
                        await db.Set<CharacterOwnershipRow>().Where(o => o.Scope == sourceScope)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(o => o.OwnedMaps, storedMaps)
                                .SetProperty(o => o.HeartbeatAtMs, (long?)heartbeat), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await db.Set<CharacterOwnershipRow>().Where(o => o.Scope == sourceScope)
                            .ExecuteUpdateAsync(s => s.SetProperty(o => o.HeartbeatAtMs, (long?)heartbeat), ct).ConfigureAwait(false);
                    }
                }
            }

            foreach (ClusterTransferDecisionRow transfer in transfers)
                await CommitRowAsync(db, transfer, target, null, ct).ConfigureAwait(false); // a whole-World handoff keeps its own scope

            row.Stage = (int)WorldHandoffStage.EpochTakeover;
            row.State = (int)PlayerTransferDecisionState.Committed;
            row.TargetEpoch = targetEpoch;
            row.TargetIncarnation = target;
            row.TargetOwnerNode = targetOwnerNode;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default)
        => AdvancePostCommitAsync(handoffId, WorldHandoffStage.Rerouted, cancellationToken);

    /// <inheritdoc />
    public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default)
        => AdvancePostCommitAsync(handoffId, WorldHandoffStage.Retired, cancellationToken);

    private async Task<WorldHandoffRecord> AdvancePostCommitAsync(Guid handoffId, WorldHandoffStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterWorldHandoffRow row = await RequireHandoffAsync(db, handoffId, ct).ConfigureAwait(false);
            if (row.Stage >= (int)stage && row.State != (int)PlayerTransferDecisionState.Aborted)
                return ToRecord(row); // idempotent: a roll-forward that repeats a step
            if (row.State != (int)PlayerTransferDecisionState.Committed)
                throw new InvalidOperationException($"Handoff '{handoffId}' is {(PlayerTransferDecisionState)row.State}; {stage} needs a committed handoff.");
            if (row.Stage != (int)stage - 1)
                throw new InvalidOperationException($"Invalid handoff stage transition for '{handoffId}': {(WorldHandoffStage)row.Stage} -> {stage}.");

            row.Stage = (int)stage;
            if (stage == WorldHandoffStage.Retired)
            {
                row.State = (int)PlayerTransferDecisionState.Completed;
                row.OpenKey = null;
                row.OpenScope = null;
            }

            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reason))
            reason = "aborted";
        if (reason.Length > 256)
            reason = reason[..256];

        return await InTransactionAsync(async (db, ct) =>
        {
            ClusterWorldHandoffRow row = await RequireHandoffAsync(db, handoffId, ct).ConfigureAwait(false);
            if (row.State == (int)PlayerTransferDecisionState.Aborted)
                return ToRecord(row);
            if (row.State != (int)PlayerTransferDecisionState.PreCommit)
                throw new InvalidOperationException($"Handoff '{handoffId}' passed the epoch takeover ({(WorldHandoffStage)row.Stage}); it cannot be aborted and must roll forward.");

            string handoffKey = row.HandoffId;
            List<ClusterTransferDecisionRow> transfers = await db.Set<ClusterTransferDecisionRow>()
                .Where(r => r.HandoffId == handoffKey && r.State == (int)PlayerTransferDecisionState.PreCommit).ToListAsync(ct).ConfigureAwait(false);
            foreach (ClusterTransferDecisionRow transfer in transfers)
            {
                AbortRow(transfer, reason);
                await ReleaseClaimAsync(db, transfer, ct).ConfigureAwait(false);
            }

            row.Stage = (int)WorldHandoffStage.Aborted;
            row.State = (int)PlayerTransferDecisionState.Aborted;
            row.AbortReason = reason;
            row.OpenKey = null;
            row.OpenScope = null;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        string id = Key(handoffId);
        ClusterWorldHandoffRow? row = await db.Set<ClusterWorldHandoffRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.HandoffId == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        List<ClusterWorldHandoffRow> rows = await db.Set<ClusterWorldHandoffRow>().AsNoTracking()
            .Where(r => r.State == (int)PlayerTransferDecisionState.PreCommit || r.State == (int)PlayerTransferDecisionState.Committed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.CreatedAtUtcMs).Select(ToRecord)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        string id = Key(handoffId);
        List<string> ids = await db.Set<ClusterTransferDecisionRow>().AsNoTracking()
            .Where(r => r.HandoffId == id).Select(r => r.TransferId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. ids.Select(Guid.Parse).Order()];
    }

    /// <inheritdoc />
    public ITransferDecisionStore ForHandoff(Guid handoffId)
    {
        if (handoffId == Guid.Empty)
            throw new ArgumentException("HandoffId must be non-empty.", nameof(handoffId));
        return new HandoffScopedStore(this, handoffId);
    }

    /// <summary>Records reservations under one handoff; such a transfer commits only with the handoff's epoch takeover.</summary>
    private sealed class HandoffScopedStore(EfTransferDecisionStore inner, Guid handoffId) : ITransferDecisionStore
    {
        public void SeedCharacterOwnership(ulong characterGuid, CharacterOwnershipToken ownershipToken) => inner.SeedCharacterOwnership(characterGuid, ownershipToken);

        public CharacterOwnershipToken? GetActiveOwnership(ulong characterGuid) => inner.GetActiveOwnership(characterGuid);

        public Task<TransferDecisionRecord> CreateReservationAsync(
            Guid transferId, ulong characterGuid, int accountId, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId,
            uint sourceMapId, uint sourceInstanceId, uint targetMapId, uint targetInstanceId, CharacterOwnershipToken sourceOwnershipToken,
            CancellationToken cancellationToken = default)
            => inner.CreateReservationCoreAsync(handoffId, transferId, characterGuid, accountId, sessionGeneration, sourceWorkerId, targetWorkerId,
                sourceMapId, sourceInstanceId, targetMapId, targetInstanceId, sourceOwnershipToken, cancellationToken);

        public Task<TransferDecisionRecord> RebindTargetInstanceAsync(Guid transferId, uint targetInstanceId, CancellationToken cancellationToken = default)
            => inner.RebindTargetInstanceAsync(transferId, targetInstanceId, cancellationToken);

        public Task<TransferDecisionRecord> AdvancePreCommitStageAsync(Guid transferId, PlayerTransferStage nextStage, PlayerTransferBundle? capturedBundle = null, CancellationToken cancellationToken = default)
            => inner.AdvancePreCommitStageAsync(transferId, nextStage, capturedBundle, cancellationToken);

        public Task<TransferDecisionRecord> CommitDecisionAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default, string? targetScope = null)
            => inner.CommitDecisionAsync(transferId, targetIncarnation, cancellationToken, targetScope); // refused for a handoff transfer unless already committed by the takeover

        public Task<TransferDecisionRecord> RebindCommittedTargetAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default)
            => inner.RebindCommittedTargetAsync(transferId, targetIncarnation, cancellationToken);

        public Task<TransferDecisionRecord> MarkCompletedAsync(Guid transferId, CancellationToken cancellationToken = default)
            => inner.MarkCompletedAsync(transferId, cancellationToken);

        public Task<TransferDecisionRecord> AbortPreCommitAsync(Guid transferId, string reason, CancellationToken cancellationToken = default)
            => inner.AbortPreCommitAsync(transferId, reason, cancellationToken);

        public Task<TransferDecisionRecord?> GetDecisionAsync(Guid transferId, CancellationToken cancellationToken = default)
            => inner.GetDecisionAsync(transferId, cancellationToken);

        public Task<IReadOnlyList<TransferDecisionRecord>> GetUnresolvedForCharacterAsync(ulong characterGuid, CancellationToken cancellationToken = default)
            => inner.GetUnresolvedForCharacterAsync(characterGuid, cancellationToken);

        public Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default)
            => inner.CountUnresolvedAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<T> InTransactionAsync<T>(Func<CharacterDbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        using IDisposable exempt = CharacterWriteFence.Exempt(); // the journal is the handoff's own fence (see the class remarks)
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        T result = await work(db, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<ClusterTransferDecisionRow> RequireDecisionAsync(CharacterDbContext db, Guid transferId, CancellationToken ct)
    {
        string id = Key(transferId);
        return await db.Set<ClusterTransferDecisionRow>().SingleOrDefaultAsync(r => r.TransferId == id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Transfer decision '{transferId}' was not found.");
    }

    private static async Task<ClusterWorldHandoffRow> RequireHandoffAsync(CharacterDbContext db, Guid handoffId, CancellationToken ct)
    {
        string id = Key(handoffId);
        return await db.Set<ClusterWorldHandoffRow>().SingleOrDefaultAsync(r => r.HandoffId == id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Handoff '{handoffId}' was not found.");
    }

    private static async Task EnsureOwnerAsync(CharacterDbContext db, long characterGuid, CharacterOwnershipToken expected, CancellationToken ct)
    {
        ClusterTransferOwnerRow? row = await db.Set<ClusterTransferOwnerRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterGuid == characterGuid, ct).ConfigureAwait(false);
        if (row is null || OwnerToken(row) != expected)
            throw new CharacterOwnershipRejectedException(expected.Scope, expected.Epoch);
    }

    private async Task ReleaseClaimAsync(CharacterDbContext db, ClusterTransferDecisionRow row, CancellationToken ct)
    {
        long now = _clock();
        string transferId = row.TransferId;
        await db.Set<ClusterTransferOwnerRow>()
            .Where(r => r.CharacterGuid == row.CharacterGuid && r.ActiveTransfer == transferId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ActiveTransfer, (string?)null).SetProperty(r => r.UpdatedAtUtcMs, now), ct)
            .ConfigureAwait(false);
    }

    private void AbortRow(ClusterTransferDecisionRow row, string reason)
    {
        row.Stage = (int)PlayerTransferStage.Aborted;
        row.State = (int)PlayerTransferDecisionState.Aborted;
        row.AbortReason = reason.Length > 256 ? reason[..256] : reason;
        row.UpdatedAtUtcMs = _clock();
    }

    private static void ValidateWorker(WorkerNodeId worker, string name)
    {
        if (worker.IsEmpty || worker.Value.Length > 128)
            throw new ArgumentException("A worker id is required (at most 128 characters).", name);
    }

    private static string Key(Guid id) => id.ToString("D");

    private static CharacterOwnershipToken OwnerToken(ClusterTransferOwnerRow row) => new(row.Scope, row.Epoch, Guid.Parse(row.Incarnation));

    private static CharacterOwnershipToken SourceToken(ClusterTransferDecisionRow row) => new(row.SourceScope, row.SourceEpoch, Guid.Parse(row.SourceIncarnation));

    private static CharacterOwnershipToken? TargetToken(ClusterTransferDecisionRow row)
        => row.TargetEpoch is { } epoch && row.TargetIncarnation is { } incarnation ? new(row.TargetScope ?? row.SourceScope, epoch, Guid.Parse(incarnation)) : null;

    private static TransferDecisionRecord ToRecord(ClusterTransferDecisionRow row) => new(
        TransferId: Guid.Parse(row.TransferId),
        CharacterGuid: (ulong)row.CharacterGuid,
        AccountId: row.AccountId,
        SessionGeneration: row.SessionGeneration,
        SourceWorkerId: new WorkerNodeId(row.SourceWorker),
        TargetWorkerId: new WorkerNodeId(row.TargetWorker),
        SourceMapId: (uint)row.SourceMapId,
        SourceInstanceId: (uint)row.SourceInstanceId,
        TargetMapId: (uint)row.TargetMapId,
        TargetInstanceId: (uint)row.TargetInstanceId,
        CurrentStage: (PlayerTransferStage)row.Stage,
        DecisionState: (PlayerTransferDecisionState)row.State,
        SourceOwnershipToken: SourceToken(row),
        TargetOwnershipToken: TargetToken(row),
        CapturedBundle: row.Bundle is null ? null : PlayerTransferBundleJson.Deserialize(row.Bundle),
        AbortReason: row.AbortReason,
        UpdatedAtUtcMs: row.UpdatedAtUtcMs);

    private static WorldHandoffRecord ToRecord(ClusterWorldHandoffRow row) => new(
        HandoffId: Guid.Parse(row.HandoffId),
        SourceWorkerId: new WorkerNodeId(row.SourceWorker),
        TargetWorkerId: new WorkerNodeId(row.TargetWorker),
        Stage: (WorldHandoffStage)row.Stage,
        DecisionState: (PlayerTransferDecisionState)row.State,
        SourceOwnershipToken: new CharacterOwnershipToken(row.SourceScope, row.SourceEpoch, Guid.Parse(row.SourceIncarnation)),
        TargetOwnershipToken: row.TargetEpoch is { } epoch && row.TargetIncarnation is { } incarnation
            ? new CharacterOwnershipToken(row.SourceScope, epoch, Guid.Parse(incarnation))
            : null,
        TargetOwnerNode: row.TargetOwnerNode,
        AbortReason: row.AbortReason,
        CreatedAtUtcMs: row.CreatedAtUtcMs,
        UpdatedAtUtcMs: row.UpdatedAtUtcMs);
}
