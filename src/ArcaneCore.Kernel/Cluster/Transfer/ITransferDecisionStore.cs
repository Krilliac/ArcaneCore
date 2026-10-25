using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Kernel.Cluster.Transfer;

/// <summary>
/// Durable transfer record tracked by <see cref="ITransferDecisionStore"/> across all 6 handoff stages.
/// </summary>
public sealed record TransferDecisionRecord(
    Guid TransferId,
    ulong CharacterGuid,
    int AccountId,
    long SessionGeneration,
    WorkerNodeId SourceWorkerId,
    WorkerNodeId TargetWorkerId,
    uint SourceMapId,
    uint SourceInstanceId,
    uint TargetMapId,
    uint TargetInstanceId,
    PlayerTransferStage CurrentStage,
    PlayerTransferDecisionState DecisionState,
    CharacterOwnershipToken SourceOwnershipToken,
    CharacterOwnershipToken? TargetOwnershipToken,
    PlayerTransferBundle? CapturedBundle,
    string? AbortReason,
    long UpdatedAtUtcMs)
{
    public bool IsCommittedOrBeyond =>
        DecisionState is PlayerTransferDecisionState.Committed or PlayerTransferDecisionState.Completed;

    public bool CanAbort =>
        DecisionState == PlayerTransferDecisionState.PreCommit &&
        CurrentStage is PlayerTransferStage.Reserve
            or PlayerTransferStage.SuspendAndCapture
            or PlayerTransferStage.InstallPassive
            or PlayerTransferStage.StageWorldPort;
}

/// <summary>
/// Durable store for cross-worker transfer decisions and atomic ownership epoch handoff.
/// </summary>
public interface ITransferDecisionStore
{
    /// <summary>Seeds or updates the active ownership token for a character prior to transfer.</summary>
    void SeedCharacterOwnership(ulong characterGuid, CharacterOwnershipToken ownershipToken);

    /// <summary>Returns the currently active ownership token for <paramref name="characterGuid"/>.</summary>
    CharacterOwnershipToken? GetActiveOwnership(ulong characterGuid);

    /// <summary>Stage 1: creates a durable pre-commit reservation record.</summary>
    Task<TransferDecisionRecord> CreateReservationAsync(
        Guid transferId,
        ulong characterGuid,
        int accountId,
        long sessionGeneration,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        uint sourceMapId,
        uint sourceInstanceId,
        uint targetMapId,
        uint targetInstanceId,
        CharacterOwnershipToken sourceOwnershipToken,
        CancellationToken cancellationToken = default);

    /// <summary>Advances a pre-commit transfer through stages 2..4 (<see cref="PlayerTransferStage.SuspendAndCapture"/>, <see cref="PlayerTransferStage.InstallPassive"/>, <see cref="PlayerTransferStage.StageWorldPort"/>).</summary>
    Task<TransferDecisionRecord> AdvancePreCommitStageAsync(
        Guid transferId,
        PlayerTransferStage nextStage,
        PlayerTransferBundle? capturedBundle = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage 5 (Single Point of No Return): atomically commits the transfer decision and swaps active ownership
    /// from <see cref="TransferDecisionRecord.SourceOwnershipToken"/> (`Epoch = N`) to the new target token (`Epoch = N + 1`).
    /// </summary>
    Task<TransferDecisionRecord> CommitDecisionAsync(
        Guid transferId,
        Guid targetIncarnation,
        CancellationToken cancellationToken = default);

    /// <summary>Stage 6: marks a committed transfer as completed once target activation and source retirement finish.</summary>
    Task<TransferDecisionRecord> MarkCompletedAsync(
        Guid transferId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts an uncommitted transfer (stages 1..4 only) and keeps <see cref="TransferDecisionRecord.SourceOwnershipToken"/> active.
    /// Throws <see cref="InvalidOperationException"/> if called after <see cref="CommitDecisionAsync"/>.
    /// </summary>
    Task<TransferDecisionRecord> AbortPreCommitAsync(
        Guid transferId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches a transfer decision record by its unique identifier.</summary>
    Task<TransferDecisionRecord?> GetDecisionAsync(
        Guid transferId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists all transfers for <paramref name="characterGuid"/> that are in <see cref="PlayerTransferDecisionState.PreCommit"/> or <see cref="PlayerTransferDecisionState.Committed"/>.</summary>
    Task<IReadOnlyList<TransferDecisionRecord>> GetUnresolvedForCharacterAsync(
        ulong characterGuid,
        CancellationToken cancellationToken = default);

    /// <summary>Counts every transfer, across all characters, in <see cref="PlayerTransferDecisionState.PreCommit"/> or <see cref="PlayerTransferDecisionState.Committed"/>.</summary>
    Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default);
}
