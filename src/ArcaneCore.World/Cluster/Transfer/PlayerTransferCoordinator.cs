using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>Source worker participant contract for cross-worker player handoff.</summary>
public interface ITransferSourceWorker
{
    WorkerNodeId WorkerId { get; }

    /// <summary>Stage 2: suspends player input/ticking on the source worker, flushes durable state, and captures the complete transfer bundle.</summary>
    Task<PlayerTransferBundle> SuspendAndCaptureAsync(
        Guid transferId,
        ulong characterGuid,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default);

    /// <summary>Pre-commit abort recovery: unfreezes the suspended player on the source worker under the unchanged source ownership token.</summary>
    Task ResumeAfterAbortAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken ownershipToken,
        string abortReason,
        CancellationToken cancellationToken = default);

    /// <summary>Stage 6 / Post-commit roll-forward: permanently retires the frozen player entity from the source worker.</summary>
    Task RetireSourcePlayerAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken supersededToken,
        CancellationToken cancellationToken = default);
}

/// <summary>Target worker participant contract for cross-worker player handoff.</summary>
public interface ITransferTargetWorker
{
    WorkerNodeId WorkerId { get; }

    Guid Incarnation { get; }

    /// <summary>Stage 1: reserves a target slot on the destination worker for `(targetMapId, targetInstanceId)`.</summary>
    Task<bool> TryReserveSlotAsync(
        Guid transferId,
        ulong characterGuid,
        uint targetMapId,
        uint targetInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Stage 3: installs the passive shadow entity from the bundle without starting world ticks or durable writes.</summary>
    Task InstallPassiveShadowAsync(
        Guid transferId,
        PlayerTransferBundle bundle,
        CancellationToken cancellationToken = default);

    /// <summary>Pre-commit abort cleanup: discards any reserved slot and passive shadow entity on the target worker.</summary>
    Task DiscardPassiveShadowAsync(
        Guid transferId,
        ulong characterGuid,
        string abortReason,
        CancellationToken cancellationToken = default);

    /// <summary>Stage 6 / Post-commit roll-forward: activates the passive shadow entity under the newly committed <paramref name="targetOwnershipToken"/>.</summary>
    Task ActivateTargetPlayerAsync(
        Guid transferId,
        PlayerTransferBundle bundle,
        CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default);
}

/// <summary>Gateway session routing participant contract for cross-worker player handoff.</summary>
public interface ITransferGatewayRouter
{
    /// <summary>Stage 4: stages the pending world-port transition and buffers inbound client packets during the commit window.</summary>
    Task StageWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default);

    /// <summary>Pre-commit abort cleanup: cancels a staged world-port and resumes routing to <paramref name="restoredWorkerId"/>.</summary>
    Task CancelStagedWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId restoredWorkerId,
        string abortReason,
        CancellationToken cancellationToken = default);

    /// <summary>Stage 6 / Post-commit roll-forward: switches client packet routing to <paramref name="targetWorkerId"/> under <paramref name="targetOwnershipToken"/>.</summary>
    Task CommitRouteSwitchAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId targetWorkerId,
        CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default);
}

/// <summary>Parameters for initiating a 6-stage cross-worker player transfer.</summary>
public sealed record PlayerTransferRequest(
    Guid TransferId,
    ulong CharacterGuid,
    int AccountId,
    long SessionGeneration,
    WorkerNodeId SourceWorkerId,
    uint SourceMapId,
    uint SourceInstanceId,
    uint TargetMapId,
    uint TargetInstanceId,
    TransferPosition TargetPosition,
    CharacterOwnershipToken SourceOwnershipToken,
    WorkerNodeId? ExplicitTargetWorkerId = null);

/// <summary>Outcome of a coordinated transfer or recovery operation.</summary>
public sealed record PlayerTransferOutcome(
    bool Succeeded,
    PlayerTransferStage FinalStage,
    PlayerTransferDecisionState DecisionState,
    string? FailureCode,
    string? FailureMessage,
    TransferDecisionRecord? DecisionRecord);

/// <summary>
/// Coordinates the 6-stage transactional cross-worker player handoff state machine:
/// <list type="number">
/// <item><description><see cref="PlayerTransferStage.Reserve"/></description></item>
/// <item><description><see cref="PlayerTransferStage.SuspendAndCapture"/></description></item>
/// <item><description><see cref="PlayerTransferStage.InstallPassive"/></description></item>
/// <item><description><see cref="PlayerTransferStage.StageWorldPort"/></description></item>
/// <item><description><see cref="PlayerTransferStage.CommitDecision"/> (Single Point of No Return)</description></item>
/// <item><description><see cref="PlayerTransferStage.ActivateAndRetire"/></description></item>
/// </list>
/// Guarantees that pre-commit aborts (stages 1..4) cleanly restore the source worker and discard target state,
/// while post-commit failures (stages 5..6) forbid abort and deterministically roll forward to the target worker
/// without ever permitting dual-active ownership.
/// </summary>
public sealed class PlayerTransferCoordinator
{
    private readonly IClusterMapRouter _mapRouter;
    private readonly ITransferDecisionStore _decisionStore;
    private readonly ITransferGatewayRouter _gatewayRouter;
    private readonly Dictionary<WorkerNodeId, ITransferSourceWorker> _sourceWorkers = [];
    private readonly Dictionary<WorkerNodeId, ITransferTargetWorker> _targetWorkers = [];

    public PlayerTransferCoordinator(
        IClusterMapRouter mapRouter,
        ITransferDecisionStore decisionStore,
        ITransferGatewayRouter gatewayRouter,
        IEnumerable<ITransferSourceWorker>? sourceWorkers = null,
        IEnumerable<ITransferTargetWorker>? targetWorkers = null)
    {
        ArgumentNullException.ThrowIfNull(mapRouter);
        ArgumentNullException.ThrowIfNull(decisionStore);
        ArgumentNullException.ThrowIfNull(gatewayRouter);

        _mapRouter = mapRouter;
        _decisionStore = decisionStore;
        _gatewayRouter = gatewayRouter;

        if (sourceWorkers is not null)
        {
            foreach (ITransferSourceWorker source in sourceWorkers)
                RegisterSourceWorker(source);
        }

        if (targetWorkers is not null)
        {
            foreach (ITransferTargetWorker target in targetWorkers)
                RegisterTargetWorker(target);
        }
    }

    public void RegisterSourceWorker(ITransferSourceWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _sourceWorkers[worker.WorkerId] = worker;
    }

    public void RegisterTargetWorker(ITransferTargetWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _targetWorkers[worker.WorkerId] = worker;
    }

    /// <summary>
    /// Executes the full 6-stage cross-worker player handoff. An optional <paramref name="afterStageHook"/> may be supplied
    /// by tests or supervisors to inspect state, trigger a pre-commit abort, or simulate a crash after any stage.
    /// </summary>
    public async Task<PlayerTransferOutcome> ExecuteTransferAsync(
        PlayerTransferRequest request,
        Func<PlayerTransferStage, TransferDecisionRecord, CancellationToken, Task>? afterStageHook = null,
        CancellationToken cancellationToken = default)
    {
        PlayerTransferOutcome prepared = await PrepareTransferAsync(request, afterStageHook, cancellationToken);
        if (!prepared.Succeeded)
            return prepared;

        ITransferTargetWorker targetWorker = GetTargetWorker(prepared.DecisionRecord!.TargetWorkerId);

        // Stage 5: CommitDecision (Single Point of No Return!)
        TransferDecisionRecord record = await _decisionStore.CommitDecisionAsync(
            request.TransferId,
            targetWorker.Incarnation,
            cancellationToken);

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.CommitDecision, record, cancellationToken);
        }

        // Stage 6: ActivateAndRetire
        record = await CompleteCommittedTransferAsync(request.TransferId, cancellationToken);

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.ActivateAndRetire, record, cancellationToken);
        }

        return new PlayerTransferOutcome(
            Succeeded: true,
            FinalStage: record.CurrentStage,
            DecisionState: record.DecisionState,
            FailureCode: null,
            FailureMessage: null,
            DecisionRecord: record);
    }

    /// <summary>
    /// Runs the pre-commit stages 1 to 4 (reserve, suspend and capture, install passive, stage the world-port) and stops before the
    /// commit. <see cref="PlayerTransferOutcome.Succeeded"/> means the transfer stands at <see cref="PlayerTransferStage.StageWorldPort"/>,
    /// still abortable; a failure has already been aborted. A whole-World handoff (Tier 3b) prepares every player this way, then commits
    /// them all at once with the epoch takeover (<c>IWorldHandoffStore.CommitEpochTakeoverAsync</c>) and finishes each one with
    /// <see cref="CompleteCommittedTransferAsync"/>.
    /// </summary>
    public async Task<PlayerTransferOutcome> PrepareTransferAsync(
        PlayerTransferRequest request,
        Func<PlayerTransferStage, TransferDecisionRecord, CancellationToken, Task>? afterStageHook = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve target worker from placement router unless explicitly pinned and valid.
        WorkerNodeId targetWorkerId;
        if (request.ExplicitTargetWorkerId is { IsEmpty: false } explicitWorker)
        {
            if (!_mapRouter.TryGetWorker(explicitWorker, out ClusterWorkerStatus status) || !status.CanAcceptPlayer)
            {
                return new PlayerTransferOutcome(
                    Succeeded: false,
                    FinalStage: PlayerTransferStage.None,
                    DecisionState: PlayerTransferDecisionState.Aborted,
                    FailureCode: ClusterMapPlacementTable.FailureWorkerNotReady,
                    FailureMessage: $"Target worker '{explicitWorker}' is not ready or has no available capacity.",
                    DecisionRecord: null);
            }

            targetWorkerId = explicitWorker;
        }
        else if (!_mapRouter.TryResolveTargetWorker(
                     request.TargetMapId,
                     request.TargetInstanceId,
                     out targetWorkerId,
                     out string? placementFailure))
        {
            return new PlayerTransferOutcome(
                Succeeded: false,
                FinalStage: PlayerTransferStage.None,
                DecisionState: PlayerTransferDecisionState.Aborted,
                FailureCode: placementFailure ?? ClusterMapPlacementTable.FailureNoEligibleWorker,
                FailureMessage: $"Target map ({request.TargetMapId}, instance {request.TargetInstanceId}) could not be routed: {placementFailure}.",
                DecisionRecord: null);
        }

        if (targetWorkerId == request.SourceWorkerId)
        {
            return new PlayerTransferOutcome(
                Succeeded: false,
                FinalStage: PlayerTransferStage.None,
                DecisionState: PlayerTransferDecisionState.Aborted,
                FailureCode: PlayerTransferValidator.ErrorInvalidWorkers,
                FailureMessage: "Source and target workers are identical; use local teleport instead of cross-worker transfer.",
                DecisionRecord: null);
        }

        ITransferSourceWorker sourceWorker = GetSourceWorker(request.SourceWorkerId);
        ITransferTargetWorker targetWorker = GetTargetWorker(targetWorkerId);

        // Stage 1: Reserve
        TransferDecisionRecord record = await _decisionStore.CreateReservationAsync(
            request.TransferId,
            request.CharacterGuid,
            request.AccountId,
            request.SessionGeneration,
            request.SourceWorkerId,
            targetWorkerId,
            request.SourceMapId,
            request.SourceInstanceId,
            request.TargetMapId,
            request.TargetInstanceId,
            request.SourceOwnershipToken,
            cancellationToken);

        bool reserved = await targetWorker.TryReserveSlotAsync(
            request.TransferId,
            request.CharacterGuid,
            request.TargetMapId,
            request.TargetInstanceId,
            cancellationToken);

        if (!reserved)
        {
            TransferDecisionRecord aborted = await AbortInternalAsync(record, "target-reserve-rejected", cancellationToken);
            return new PlayerTransferOutcome(
                Succeeded: false,
                FinalStage: aborted.CurrentStage,
                DecisionState: aborted.DecisionState,
                FailureCode: "target-reserve-rejected",
                FailureMessage: "Target worker rejected slot reservation.",
                DecisionRecord: aborted);
        }

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.Reserve, record, cancellationToken);
            TransferDecisionRecord? refreshed = await _decisionStore.GetDecisionAsync(request.TransferId, cancellationToken);
            if (refreshed is { DecisionState: PlayerTransferDecisionState.Aborted })
                return AbortedOutcome(refreshed);
        }

        // Stage 2: SuspendAndCapture
        PlayerTransferBundle bundle = await sourceWorker.SuspendAndCaptureAsync(
            request.TransferId,
            request.CharacterGuid,
            targetWorkerId,
            request.TargetMapId,
            request.TargetInstanceId,
            request.TargetPosition,
            cancellationToken);

        PlayerTransferValidationResult validation = PlayerTransferValidator.ValidateBundle(bundle);
        if (!validation.IsValid)
        {
            TransferDecisionRecord aborted = await AbortInternalAsync(
                record,
                validation.FailureCode ?? "bundle-validation-failed",
                cancellationToken);

            return new PlayerTransferOutcome(
                Succeeded: false,
                FinalStage: aborted.CurrentStage,
                DecisionState: aborted.DecisionState,
                FailureCode: validation.FailureCode,
                FailureMessage: string.Join("; ", validation.Errors),
                DecisionRecord: aborted);
        }

        record = await _decisionStore.AdvancePreCommitStageAsync(
            request.TransferId,
            PlayerTransferStage.SuspendAndCapture,
            bundle,
            cancellationToken);

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.SuspendAndCapture, record, cancellationToken);
            TransferDecisionRecord? refreshed = await _decisionStore.GetDecisionAsync(request.TransferId, cancellationToken);
            if (refreshed is { DecisionState: PlayerTransferDecisionState.Aborted })
                return AbortedOutcome(refreshed);
        }

        // Stage 3: InstallPassive
        await targetWorker.InstallPassiveShadowAsync(request.TransferId, bundle, cancellationToken);
        record = await _decisionStore.AdvancePreCommitStageAsync(
            request.TransferId,
            PlayerTransferStage.InstallPassive,
            bundle,
            cancellationToken);

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.InstallPassive, record, cancellationToken);
            TransferDecisionRecord? refreshed = await _decisionStore.GetDecisionAsync(request.TransferId, cancellationToken);
            if (refreshed is { DecisionState: PlayerTransferDecisionState.Aborted })
                return AbortedOutcome(refreshed);
        }

        // Stage 4: StageWorldPort
        await _gatewayRouter.StageWorldPortAsync(
            request.TransferId,
            request.CharacterGuid,
            request.SessionGeneration,
            request.SourceWorkerId,
            targetWorkerId,
            request.TargetMapId,
            request.TargetInstanceId,
            request.TargetPosition,
            cancellationToken);

        record = await _decisionStore.AdvancePreCommitStageAsync(
            request.TransferId,
            PlayerTransferStage.StageWorldPort,
            bundle,
            cancellationToken);

        if (afterStageHook is not null)
        {
            await afterStageHook(PlayerTransferStage.StageWorldPort, record, cancellationToken);
            TransferDecisionRecord? refreshed = await _decisionStore.GetDecisionAsync(request.TransferId, cancellationToken);
            if (refreshed is { DecisionState: PlayerTransferDecisionState.Aborted })
                return AbortedOutcome(refreshed);
        }

        return new PlayerTransferOutcome(
            Succeeded: true,
            FinalStage: record.CurrentStage,
            DecisionState: record.DecisionState,
            FailureCode: null,
            FailureMessage: null,
            DecisionRecord: record);
    }

    /// <summary>
    /// Attempts to abort a transfer. Succeeds ONLY when the transfer is still in a pre-commit stage (stages 1..4).
    /// Returns <c>false</c> if the transfer has already committed or completed.
    /// </summary>
    public async Task<bool> TryAbortTransferAsync(
        Guid transferId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        TransferDecisionRecord? record = await _decisionStore.GetDecisionAsync(transferId, cancellationToken);
        if (record is null || record.IsCommittedOrBeyond)
            return false;

        if (record.DecisionState == PlayerTransferDecisionState.Aborted)
            return true;

        await AbortInternalAsync(record, reason, cancellationToken);
        return true;
    }

    /// <summary>
    /// Aborts a pre-commit transfer (stages 1..4). Throws <see cref="InvalidOperationException"/> if called after
    /// <see cref="PlayerTransferStage.CommitDecision"/>.
    /// </summary>
    public async Task<TransferDecisionRecord> AbortTransferAsync(
        Guid transferId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        TransferDecisionRecord? record = await _decisionStore.GetDecisionAsync(transferId, cancellationToken)
            ?? throw new KeyNotFoundException($"Transfer '{transferId}' was not found.");

        if (record.IsCommittedOrBeyond)
        {
            throw new InvalidOperationException(
                $"Transfer '{transferId}' has already committed (stage={record.CurrentStage}, state={record.DecisionState}) and cannot be aborted; roll forward instead.");
        }

        return await AbortInternalAsync(record, reason, cancellationToken);
    }

    /// <summary>
    /// Rolls forward a transfer that has reached <see cref="PlayerTransferStage.CommitDecision"/>:
    /// retires the source worker entity first (preventing any dual-active window), ensures passive installation
    /// on the target worker, activates the target player under <see cref="TransferDecisionRecord.TargetOwnershipToken"/>,
    /// switches the gateway route, and marks the transfer <see cref="PlayerTransferDecisionState.Completed"/>.
    /// </summary>
    public async Task<TransferDecisionRecord> CompleteCommittedTransferAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        TransferDecisionRecord record = await _decisionStore.GetDecisionAsync(transferId, cancellationToken)
            ?? throw new KeyNotFoundException($"Transfer '{transferId}' was not found.");

        if (record.DecisionState == PlayerTransferDecisionState.Completed)
            return record;

        if (record.DecisionState != PlayerTransferDecisionState.Committed ||
            record.TargetOwnershipToken is null ||
            record.CapturedBundle is null)
        {
            throw new InvalidOperationException(
                $"Transfer '{transferId}' is not in Committed state (state={record.DecisionState}, stage={record.CurrentStage}).");
        }

        ITransferSourceWorker sourceWorker = GetSourceWorker(record.SourceWorkerId);
        ITransferTargetWorker targetWorker = GetTargetWorker(record.TargetWorkerId);

        // 1. Retire source entity first so source and target are never simultaneously active.
        await sourceWorker.RetireSourcePlayerAsync(
            record.TransferId,
            record.CharacterGuid,
            record.SourceOwnershipToken,
            cancellationToken);

        // 2. Ensure target passive state exists (in case target worker restarted after CommitDecision) and activate it.
        await targetWorker.InstallPassiveShadowAsync(
            record.TransferId,
            record.CapturedBundle,
            cancellationToken);

        await targetWorker.ActivateTargetPlayerAsync(
            record.TransferId,
            record.CapturedBundle,
            record.TargetOwnershipToken,
            cancellationToken);

        // 3. Switch gateway routing to the target worker under the new ownership token.
        await _gatewayRouter.CommitRouteSwitchAsync(
            record.TransferId,
            record.CharacterGuid,
            record.SessionGeneration,
            record.TargetWorkerId,
            record.TargetOwnershipToken,
            cancellationToken);

        // 4. Mark durable decision Completed.
        return await _decisionStore.MarkCompletedAsync(record.TransferId, cancellationToken);
    }

    /// <summary>
    /// Crash-recovery entrypoint for an unresolved transfer:
    /// <list type="bullet">
    /// <item><description>If <see cref="PlayerTransferDecisionState.Committed"/> (stage 5 or 6 crash), rolls forward via <see cref="CompleteCommittedTransferAsync"/>.</description></item>
    /// <item><description>If <see cref="PlayerTransferDecisionState.PreCommit"/> (stages 1..4 crash), rolls back via <see cref="AbortTransferAsync"/>.</description></item>
    /// </list>
    /// </summary>
    public async Task<TransferDecisionRecord> RecoverTransferAsync(
        Guid transferId,
        string recoveryReason = "crash-recovery",
        CancellationToken cancellationToken = default)
    {
        TransferDecisionRecord record = await _decisionStore.GetDecisionAsync(transferId, cancellationToken)
            ?? throw new KeyNotFoundException($"Transfer '{transferId}' was not found.");

        return record.DecisionState switch
        {
            PlayerTransferDecisionState.Committed => await CompleteCommittedTransferAsync(transferId, cancellationToken),
            PlayerTransferDecisionState.PreCommit => await AbortInternalAsync(record, recoveryReason, cancellationToken),
            _ => record,
        };
    }

    private async Task<TransferDecisionRecord> AbortInternalAsync(
        TransferDecisionRecord record,
        string reason,
        CancellationToken cancellationToken)
    {
        TransferDecisionRecord aborted = await _decisionStore.AbortPreCommitAsync(record.TransferId, reason, cancellationToken);

        if (_targetWorkers.TryGetValue(record.TargetWorkerId, out ITransferTargetWorker? targetWorker))
        {
            await targetWorker.DiscardPassiveShadowAsync(record.TransferId, record.CharacterGuid, reason, cancellationToken);
        }

        await _gatewayRouter.CancelStagedWorldPortAsync(
            record.TransferId,
            record.CharacterGuid,
            record.SessionGeneration,
            record.SourceWorkerId,
            reason,
            cancellationToken);

        if (_sourceWorkers.TryGetValue(record.SourceWorkerId, out ITransferSourceWorker? sourceWorker))
        {
            await sourceWorker.ResumeAfterAbortAsync(
                record.TransferId,
                record.CharacterGuid,
                record.SourceOwnershipToken,
                reason,
                cancellationToken);
        }

        return aborted;
    }

    private static PlayerTransferOutcome AbortedOutcome(TransferDecisionRecord aborted) =>
        new(
            Succeeded: false,
            FinalStage: aborted.CurrentStage,
            DecisionState: aborted.DecisionState,
            FailureCode: aborted.AbortReason ?? "aborted",
            FailureMessage: $"Transfer '{aborted.TransferId}' was aborted before commit: {aborted.AbortReason}.",
            DecisionRecord: aborted);

    private ITransferSourceWorker GetSourceWorker(WorkerNodeId workerId)
    {
        if (!_sourceWorkers.TryGetValue(workerId, out ITransferSourceWorker? worker))
            throw new InvalidOperationException($"Source worker '{workerId}' is not registered with PlayerTransferCoordinator.");

        return worker;
    }

    private ITransferTargetWorker GetTargetWorker(WorkerNodeId workerId)
    {
        if (!_targetWorkers.TryGetValue(workerId, out ITransferTargetWorker? worker))
            throw new InvalidOperationException($"Target worker '{workerId}' is not registered with PlayerTransferCoordinator.");

        return worker;
    }
}
