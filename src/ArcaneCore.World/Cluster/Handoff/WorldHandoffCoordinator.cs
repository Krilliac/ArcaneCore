using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Handoff;

/// <summary>The draining world A of a whole-World handoff (Tier 3b, docs/design/hot-reload-tiers.md section 5.3).</summary>
public interface IWorldHandoffSource
{
    WorkerNodeId WorkerId { get; }

    /// <summary>The characters ownership A holds now (the handoff's source token, epoch N).</summary>
    CharacterOwnershipToken Ownership { get; }

    /// <summary>Stop admitting new sessions. Idempotent.</summary>
    Task StopAdmissionsAsync(CancellationToken cancellationToken);

    /// <summary>Freeze the simulation at a tick boundary, capturing every online player's save as at logout. Idempotent.</summary>
    Task FreezeAsync(CancellationToken cancellationToken);

    /// <summary>Wait until every save captured by the freeze is durable.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Abort before the point of no return: unfreeze and admit sessions again, under the unchanged ownership. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>After the point of no return: A never runs or admits again (its late writes are fenced anyway). Idempotent.</summary>
    Task RetireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// After the epoch takeover and before B is promoted: give up the characters database's process-lifetime writer lock (SQLite sidecar file or server advisory lock), which B's promotion
    /// takes and refuses to promote without. A is fenced out of the database by then, so it writes nothing more. Idempotent. A source that holds no such lock (the default) does nothing.
    /// <para>
    /// The window between this release and B's promotion taking the lock is guarded by the ownership row, not by the lock: the takeover has already made B's incarnation the owner, so a third
    /// world that starts in the window cannot claim the characters (fencing is mandatory in worker mode and the claim is refused), and A's own late writes are refused by the fence. A failed
    /// promotion leaves the lock free; it is not re-taken for A.
    /// </para>
    /// </summary>
    Task ReleaseWriterLockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The warmed standby world B of a whole-World handoff.</summary>
public interface IWorldHandoffTarget
{
    WorkerNodeId WorkerId { get; }

    /// <summary>The owner node B claims under.</summary>
    string OwnerNode { get; }

    /// <summary>B's ownership incarnation: the epoch takeover hands the ownership to it.</summary>
    Guid Incarnation { get; }

    /// <summary>Check that B is warm and still a standby (throws otherwise). Idempotent.</summary>
    Task WarmAsync(CancellationToken cancellationToken);

    /// <summary>After the epoch takeover: run B under the taken-over ownership. Idempotent.</summary>
    Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken);
}

/// <summary>Routes the gateway's sessions from A to B after the epoch takeover.</summary>
public interface IWorldHandoffRouter
{
    /// <summary>Idempotent: a roll-forward repeats it after a crash.</summary>
    Task RerouteAsync(CancellationToken cancellationToken);
}

/// <summary>A handoff that aborted before its point of no return: A kept its ownership and was resumed.</summary>
public sealed class WorldHandoffAbortedException(WorldHandoffRecord record, Exception? cause)
    : Exception($"world handoff {record.HandoffId} aborted at {LastStage(record)}: {record.AbortReason}", cause)
{
    public WorldHandoffRecord Record { get; } = record;

    private static string LastStage(WorldHandoffRecord record) => record.Stage.ToString();
}

/// <summary>
/// The drain protocol of a whole-World handoff (Tier 3b, docs/design/hot-reload-tiers.md section 5.3), journalled stage by stage in the
/// characters database through <see cref="IWorldHandoffStore"/>:
/// <list type="number">
/// <item>Begun; B warm (<see cref="IWorldHandoffTarget.WarmAsync"/>);</item>
/// <item>A stops admissions, freezes at a tick boundary, and flushes the forced saves;</item>
/// <item>the epoch takeover: the ownership moves from A (epoch N) to B (N + 1) in one transaction with the decision. Point of no return;</item>
/// <item>B is promoted, the gateway reroutes, A retires.</item>
/// </list>
/// Any failure before the takeover aborts: the journal records it and A resumes under its unchanged ownership. After the takeover the handoff
/// only rolls forward; a failure leaves it committed for <see cref="RecoverAsync"/> (or a repeat of it) to finish. One coordinator drives one
/// handoff at a time; the store refuses a second unresolved one.
/// </summary>
public sealed class WorldHandoffCoordinator(
    IWorldHandoffStore store,
    IWorldHandoffSource source,
    IWorldHandoffTarget target,
    IWorldHandoffRouter router,
    ILogger<WorldHandoffCoordinator>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<WorldHandoffCoordinator>.Instance;

    /// <summary>
    /// Run a handoff to completion. <paramref name="afterStage"/> runs after each stage is journalled (tests use it to fail or crash at a
    /// stage). Throws <see cref="WorldHandoffAbortedException"/> when the handoff aborted; any other exception after the takeover leaves it
    /// committed for <see cref="RecoverAsync"/>.
    /// </summary>
    public async Task<WorldHandoffRecord> RunAsync(
        Guid handoffId,
        Func<WorldHandoffStage, CancellationToken, Task>? afterStage = null,
        CancellationToken cancellationToken = default)
    {
        WorldHandoffRecord record = await store.BeginAsync(handoffId, source.WorkerId, target.WorkerId, source.Ownership, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("World handoff {Handoff} begun: {Source} -> {Target} at ownership epoch {Epoch}",
            handoffId, source.WorkerId, target.WorkerId, record.SourceOwnershipToken.Epoch);

        try
        {
            await Hook(afterStage, WorldHandoffStage.Begun, cancellationToken).ConfigureAwait(false);
            await target.WarmAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(handoffId, WorldHandoffStage.Warm, afterStage, cancellationToken).ConfigureAwait(false);
            await source.StopAdmissionsAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(handoffId, WorldHandoffStage.AdmissionsStopped, afterStage, cancellationToken).ConfigureAwait(false);
            await source.FreezeAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(handoffId, WorldHandoffStage.Frozen, afterStage, cancellationToken).ConfigureAwait(false);
            await source.FlushAsync(cancellationToken).ConfigureAwait(false);
            await AdvanceAsync(handoffId, WorldHandoffStage.Flushed, afterStage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.handoff", ex);
            AnomalyLedger.Shared.Report("cluster", "handoff-failed", handoffId.ToString(), $"phase=prepare error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            throw await AbortAsync(handoffId, ex).ConfigureAwait(false);
        }

        WorldHandoffRecord? committed;
        try
        {
            committed = await store.CommitEpochTakeoverAsync(handoffId, target.OwnerNode, target.Incarnation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.handoff", ex);
            AnomalyLedger.Shared.Report("cluster", "handoff-failed", handoffId.ToString(), $"phase=commit-takeover error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            // The takeover is one transaction: either it committed (then only roll forward) or nothing changed (then abort). Ask the journal.
            WorldHandoffRecord? after = await store.GetAsync(handoffId, CancellationToken.None).ConfigureAwait(false);
            if (after is { IsCommittedOrBeyond: true })
                return await RollForwardAsync(after, afterStage, cancellationToken).ConfigureAwait(false);
            throw await AbortAsync(handoffId, ex).ConfigureAwait(false);
        }

        if (committed is null)
            throw await AbortAsync(handoffId, new InvalidOperationException("the source no longer holds the characters ownership")).ConfigureAwait(false);

        _logger.LogInformation("World handoff {Handoff}: epoch takeover committed at epoch {Epoch}; rolling forward",
            handoffId, committed.TargetOwnershipToken!.Epoch);
        await Hook(afterStage, WorldHandoffStage.EpochTakeover, cancellationToken).ConfigureAwait(false);
        return await RollForwardAsync(committed, afterStage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finish an unresolved handoff after a crash, from its journal: before the takeover it aborts and resumes A; after it, it rolls forward
    /// (promote B, reroute, retire A). A resolved handoff is returned as it is.
    /// </summary>
    public async Task<WorldHandoffRecord> RecoverAsync(Guid handoffId, CancellationToken cancellationToken = default)
    {
        WorldHandoffRecord record = await store.GetAsync(handoffId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"world handoff {handoffId} is not journalled");
        if (record.IsResolved)
            return record;
        if (record.IsCommittedOrBeyond)
            return await RollForwardAsync(record, afterStage: null, cancellationToken).ConfigureAwait(false);

        // Before the takeover A still owns the characters, so resuming it is safe; resume first, so a journal write that fails leaves A running
        // (the handoff stays open, pre-commit, for a later recovery to abort) rather than parked with its admission closed.
        try
        {
            await source.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            record = await store.AbortAsync(handoffId, "recovered before the epoch takeover", CancellationToken.None).ConfigureAwait(false);
        }

        return record;
    }

    private async Task<WorldHandoffRecord> RollForwardAsync(
        WorldHandoffRecord record, Func<WorldHandoffStage, CancellationToken, Task>? afterStage, CancellationToken cancellationToken)
    {
        CharacterOwnershipToken takenOver = record.TargetOwnershipToken
            ?? throw new InvalidOperationException($"committed world handoff {record.HandoffId} has no target ownership token");
        if (record.Stage < WorldHandoffStage.Rerouted)
        {
            // The takeover has fenced A out of the database: it gives up the writer lock so B's promotion can take it (a source still alive elsewhere would keep it and B would be refused).
            await source.ReleaseWriterLockAsync(cancellationToken).ConfigureAwait(false);
            await target.PromoteAsync(takenOver, cancellationToken).ConfigureAwait(false);
            await router.RerouteAsync(cancellationToken).ConfigureAwait(false);
            record = await store.MarkReroutedAsync(record.HandoffId, cancellationToken).ConfigureAwait(false);
            await Hook(afterStage, WorldHandoffStage.Rerouted, cancellationToken).ConfigureAwait(false);
        }

        if (record.Stage < WorldHandoffStage.Retired)
        {
            await source.RetireAsync(cancellationToken).ConfigureAwait(false);
            record = await store.MarkRetiredAsync(record.HandoffId, cancellationToken).ConfigureAwait(false);
            await Hook(afterStage, WorldHandoffStage.Retired, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("World handoff {Handoff} complete: {Target} owns the characters at epoch {Epoch}",
            record.HandoffId, record.TargetWorkerId, takenOver.Epoch);
        return record;
    }

    private async Task AdvanceAsync(
        Guid handoffId, WorldHandoffStage stage, Func<WorldHandoffStage, CancellationToken, Task>? afterStage, CancellationToken cancellationToken)
    {
        await store.AdvancePreCommitAsync(handoffId, stage, cancellationToken).ConfigureAwait(false);
        await Hook(afterStage, stage, cancellationToken).ConfigureAwait(false);
    }

    private static Task Hook(Func<WorldHandoffStage, CancellationToken, Task>? afterStage, WorldHandoffStage stage, CancellationToken cancellationToken)
        => afterStage?.Invoke(stage, cancellationToken) ?? Task.CompletedTask;

    private async Task<WorldHandoffAbortedException> AbortAsync(Guid handoffId, Exception cause)
    {
        // Not cancellable: an abort that half-ran would leave A frozen. Called only while the takeover has not committed, when A still owns the
        // characters: resume A first, so a journal write that fails (a transient database error) leaves A running and the handoff open, pre-commit,
        // for RecoverAsync to abort, instead of leaving A parked with its admission closed.
        string reason = cause.Message;
        WorldHandoffRecord aborted;
        try
        {
            await source.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            aborted = await store.AbortAsync(handoffId, reason, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.LogWarning(cause, "World handoff {Handoff} aborted; {Source} resumed under ownership epoch {Epoch}",
            handoffId, source.WorkerId, aborted.SourceOwnershipToken.Epoch);
        return new WorldHandoffAbortedException(aborted, cause);
    }
}
