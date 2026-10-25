using System.Text.Json;
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
public sealed class EfTransferDecisionStore(IServiceScopeFactory scopes, Func<long>? clockUtcMs = null) : ITransferDecisionStore, IWorldHandoffStore
{
    /// <summary>The characters ownership scope a whole-World handoff moves (<see cref="ICharacterOwnershipStore"/>).</summary>
    public const string WorldScope = "world";

    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.General);
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
                row.Bundle = JsonSerializer.Serialize(capturedBundle, BundleJson);
            if (nextStage == PlayerTransferStage.SuspendAndCapture && row.Bundle is null)
                throw new InvalidOperationException($"Stage {PlayerTransferStage.SuspendAndCapture} requires a non-null PlayerTransferBundle.");

            row.Stage = (int)nextStage;
            row.UpdatedAtUtcMs = _clock();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TransferDecisionRecord> CommitDecisionAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default)
        => CommitDecisionCoreAsync(transferId, targetIncarnation, cancellationToken);

    private async Task<TransferDecisionRecord> CommitDecisionCoreAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetIncarnation == Guid.Empty)
            throw new ArgumentException("Target incarnation must be non-empty.", nameof(targetIncarnation));

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

            await CommitRowAsync(db, row, target, ct).ConfigureAwait(false);
            return ToRecord(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves one transfer to Committed: the per-character ownership compare-and-swap (epoch N to N + 1) and the decision row, in the caller's transaction.</summary>
    private async Task CommitRowAsync(CharacterDbContext db, ClusterTransferDecisionRow row, string targetIncarnation, CancellationToken ct)
    {
        long now = _clock();
        long targetEpoch = checked(row.SourceEpoch + 1);
        int swapped = await db.Set<ClusterTransferOwnerRow>()
            .Where(r => r.CharacterGuid == row.CharacterGuid && r.ActiveTransfer == row.TransferId && r.Scope == row.SourceScope
                && r.Epoch == row.SourceEpoch && r.Incarnation == row.SourceIncarnation)
            .ExecuteUpdateAsync(s => s
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
        row.UpdatedAtUtcMs = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        if (sourceOwnershipToken.Scope != WorldScope || sourceOwnershipToken.Epoch <= 0 || sourceOwnershipToken.Incarnation == Guid.Empty)
            throw new ArgumentException("A handoff moves the 'world' characters ownership; the source token must be a held world token.", nameof(sourceOwnershipToken));

        return await InTransactionAsync(async (db, ct) =>
        {
            if (await db.Set<ClusterWorldHandoffRow>().AnyAsync(r => r.OpenKey != null, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Another handoff is unresolved; finish or abort it first.");

            long now = _clock();
            var row = new ClusterWorldHandoffRow
            {
                HandoffId = Key(handoffId),
                OpenKey = ClusterTransferDataModule.OpenHandoffKey,
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
    public async Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
        Guid handoffId, string targetOwnerNode, Guid targetIncarnation, CancellationToken cancellationToken = default)
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
                long sourceEpoch = row.SourceEpoch;
                int swapped = await db.Set<CharacterOwnershipRow>()
                    .Where(o => o.Scope == WorldScope && o.Active && o.Epoch == sourceEpoch && o.OwnerIncarnation == sourceIncarnation
                        && o.Epoch < long.MaxValue && o.WriteSequence < long.MaxValue)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.OwnerNode, targetOwnerNode)
                        .SetProperty(o => o.OwnerIncarnation, target)
                        .SetProperty(o => o.Epoch, o => o.Epoch + 1)
                        .SetProperty(o => o.WriteSequence, o => o.WriteSequence + 1), ct)
                    .ConfigureAwait(false);
                if (swapped == 0)
                    return null; // the source no longer holds the ownership: nothing changes, the handoff stays abortable
            }

            foreach (ClusterTransferDecisionRow transfer in transfers)
                await CommitRowAsync(db, transfer, target, ct).ConfigureAwait(false);

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

        public Task<TransferDecisionRecord> AdvancePreCommitStageAsync(Guid transferId, PlayerTransferStage nextStage, PlayerTransferBundle? capturedBundle = null, CancellationToken cancellationToken = default)
            => inner.AdvancePreCommitStageAsync(transferId, nextStage, capturedBundle, cancellationToken);

        public Task<TransferDecisionRecord> CommitDecisionAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default)
            => inner.CommitDecisionAsync(transferId, targetIncarnation, cancellationToken); // refused for a handoff transfer unless already committed by the takeover

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
        => row.TargetEpoch is { } epoch && row.TargetIncarnation is { } incarnation ? new(row.SourceScope, epoch, Guid.Parse(incarnation)) : null;

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
        CapturedBundle: row.Bundle is null ? null : JsonSerializer.Deserialize<PlayerTransferBundle>(row.Bundle, BundleJson),
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
