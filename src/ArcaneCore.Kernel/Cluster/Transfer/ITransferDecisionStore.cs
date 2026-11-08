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
    Task SeedCharacterOwnershipAsync(ulong characterGuid, CharacterOwnershipToken ownershipToken, CancellationToken cancellationToken = default);

    /// <summary>Returns the currently active ownership token for <paramref name="characterGuid"/>.</summary>
    Task<CharacterOwnershipToken?> GetActiveOwnershipAsync(ulong characterGuid, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Q8: records the instance id the target worker allocated for an instance-0 request, so the record's <see cref="TransferDecisionRecord.TargetInstanceId"/> and the bundle captured next agree.
    /// Only while the transfer is pre-commit at <see cref="PlayerTransferStage.Reserve"/> and still owned by the source token; the same id again is a no-op, a different id at a later stage throws
    /// <see cref="InvalidOperationException"/>. Needs no schema change (the <c>target_instance</c> column exists).
    /// </summary>
    Task<TransferDecisionRecord> RebindTargetInstanceAsync(
        Guid transferId,
        uint targetInstanceId,
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
    /// <paramref name="targetScope"/> is the ownership scope the target world claims under (per-worker writer lock, design _wf/writer-lock-DESIGN.md
    /// section 1b): the character's claim moves to it, so the source's retained saves are refused. Null keeps the source token's scope (the realm-wide
    /// 'world' scope, where source and target share one scope).
    /// </summary>
    Task<TransferDecisionRecord> CommitDecisionAsync(
        Guid transferId,
        Guid targetIncarnation,
        CancellationToken cancellationToken = default,
        string? targetScope = null);

    /// <summary>
    /// Roll-forward after the target worker restarted (design _wf/handoff-control-DESIGN.md, follow-up C18): moves a <see cref="PlayerTransferDecisionState.Committed"/> transfer's target token, and the character's claim
    /// with it, from the dead incarnation it was committed under to <paramref name="targetIncarnation"/>, the live incarnation of the same worker scope. Only the scope's active owner may be named: a store with
    /// ownership rows requires an active row of the committed scope under <paramref name="targetIncarnation"/> (the old incarnation is then fenced out) and takes its epoch; the claim moves by compare-and-swap
    /// from the committed token. The same incarnation again returns the record unchanged; any other state throws <see cref="InvalidOperationException"/>. A store that cannot rebind throws
    /// <see cref="NotSupportedException"/> (the default), which leaves the transfer journalled for recovery to retry.
    /// </summary>
    Task<TransferDecisionRecord> RebindCommittedTargetAsync(
        Guid transferId,
        Guid targetIncarnation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} cannot rebind a committed transfer to a restarted target.");

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

/// <summary>
/// The startup journal scan of a durable transfer store (S5e): lists every unresolved transfer so a restarted worker, gateway or coordinator can
/// resolve each one by its stage. A separate contract so a store that cannot enumerate its journal does not pretend to (an empty answer would read as "nothing to recover").
/// </summary>
public interface ITransferJournalScan
{
    /// <summary>Every transfer, across all characters, in <see cref="PlayerTransferDecisionState.PreCommit"/> or <see cref="PlayerTransferDecisionState.Committed"/>, oldest update first.</summary>
    Task<IReadOnlyList<TransferDecisionRecord>> ListUnresolvedAsync(CancellationToken cancellationToken = default);
}
