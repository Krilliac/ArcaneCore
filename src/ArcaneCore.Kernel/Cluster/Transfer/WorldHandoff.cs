using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Kernel.Cluster.Transfer;

/// <summary>
/// Stages of a whole-World handoff (Tier 3b, docs/design/hot-reload-tiers.md section 5.3): every player of a draining world A is
/// moved to a warmed standby world B. <see cref="EpochTakeover"/> is the single durable point of no return: before it the handoff
/// aborts (A resumes, B is discarded), after it the handoff only rolls forward.
/// </summary>
public enum WorldHandoffStage : byte
{
    /// <summary>Recorded, nothing done yet.</summary>
    Begun = 0,

    /// <summary>B is loaded and passed its readiness and manifest checks.</summary>
    Warm = 1,

    /// <summary>A admits no new sessions.</summary>
    AdmissionsStopped = 2,

    /// <summary>A's simulation is frozen at a tick boundary; the gateway buffers client input.</summary>
    Frozen = 3,

    /// <summary>Every player of A was saved as at logout and every save queue and barrier was flushed.</summary>
    Flushed = 4,

    /// <summary>B took over the characters ownership (epoch N to N + 1). Point of no return.</summary>
    EpochTakeover = 5,

    /// <summary>B loaded the players and the gateway routes their sessions to B.</summary>
    Rerouted = 6,

    /// <summary>A retired; the handoff is complete.</summary>
    Retired = 7,

    /// <summary>Aborted before <see cref="EpochTakeover"/>; A kept (or got back) its players and its ownership.</summary>
    Aborted = 8,
}

/// <summary>The durable record of one whole-World handoff.</summary>
public sealed record WorldHandoffRecord(
    Guid HandoffId,
    WorkerNodeId SourceWorkerId,
    WorkerNodeId TargetWorkerId,
    WorldHandoffStage Stage,
    PlayerTransferDecisionState DecisionState,
    CharacterOwnershipToken SourceOwnershipToken,
    CharacterOwnershipToken? TargetOwnershipToken,
    string? TargetOwnerNode,
    string? AbortReason,
    long CreatedAtUtcMs,
    long UpdatedAtUtcMs)
{
    public bool IsCommittedOrBeyond =>
        DecisionState is PlayerTransferDecisionState.Committed or PlayerTransferDecisionState.Completed;

    public bool IsResolved =>
        DecisionState is PlayerTransferDecisionState.Completed or PlayerTransferDecisionState.Aborted;
}

/// <summary>
/// Durable journal of whole-World handoffs, kept in the characters database next to the per-player transfer decisions
/// (<see cref="ITransferDecisionStore"/>) so an abort or a roll-forward survives a crash of either world or of the coordinator.
/// </summary>
public interface IWorldHandoffStore
{
    /// <summary>Records a new handoff at <see cref="WorldHandoffStage.Begun"/>. Refused while another handoff is unresolved.</summary>
    Task<WorldHandoffRecord> BeginAsync(
        Guid handoffId,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        CharacterOwnershipToken sourceOwnershipToken,
        CancellationToken cancellationToken = default);

    /// <summary>Advances a pre-commit handoff to the next stage (<see cref="WorldHandoffStage.Warm"/> to <see cref="WorldHandoffStage.Flushed"/>, in order).</summary>
    Task<WorldHandoffRecord> AdvancePreCommitAsync(Guid handoffId, WorldHandoffStage nextStage, CancellationToken cancellationToken = default);

    /// <summary>
    /// The point of no return, in one database transaction: compare-and-swap the characters ownership from the handoff's source token
    /// (epoch N) to <paramref name="targetIncarnation"/> at epoch N + 1, mark the handoff <see cref="WorldHandoffStage.EpochTakeover"/>
    /// (Committed), and commit every player transfer of the handoff, all of which must stand at
    /// <see cref="PlayerTransferStage.StageWorldPort"/>. Returns null, changing nothing, when the ownership is no longer the source
    /// token (another world took over, or A released it); throws, changing nothing, when the handoff or one of its transfers is not
    /// ready to commit. Idempotent for the same target incarnation.
    /// </summary>
    Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
        Guid handoffId,
        string targetOwnerNode,
        Guid targetIncarnation,
        CancellationToken cancellationToken = default);

    /// <summary>Records <see cref="WorldHandoffStage.Rerouted"/> on a committed handoff.</summary>
    Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default);

    /// <summary>Records <see cref="WorldHandoffStage.Retired"/> (Completed) on a committed handoff.</summary>
    Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default);

    /// <summary>Aborts a pre-commit handoff and every unresolved transfer of it. Throws after the point of no return.</summary>
    Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default);

    Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default);

    /// <summary>Every handoff that is neither completed nor aborted, oldest first (crash recovery).</summary>
    Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>The player transfer ids recorded under <paramref name="handoffId"/>.</summary>
    Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default);

    /// <summary>A decision store whose new reservations are recorded under <paramref name="handoffId"/> (for <c>PlayerTransferCoordinator</c>).</summary>
    ITransferDecisionStore ForHandoff(Guid handoffId);
}
