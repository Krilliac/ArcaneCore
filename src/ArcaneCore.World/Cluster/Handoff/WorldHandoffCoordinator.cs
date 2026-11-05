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

    /// <summary>
    /// Wait until every save captured by the freeze, and every other write A has open at that point in a character, instance or social queue, offline characters and writes of no one character
    /// included, and in the group, GM audit, account data, account address, creature and game object respawn and world-state save queues, is durable; throws, before the commit, when one cannot be, with one exception: a keyed write refused with RealmLeaseNotHeldException (a non-holder) is not checked. For a group the holder has the data; a GM ticket taken on a non-holder is not durable, is not on B, and is only named at stop. Pet saves are waited for (per character); the running game-event set is not, nor are other world-state writers of the characters database (for example the war effort and Scourge invasion state). On success A's writes are sealed until
    /// <see cref="ResumeAsync"/> or <see cref="RetireWritesAsync"/> decides: the takeover can land at any moment, so a fence refusal is expected. A flush that outlives its freeze (A resumed
    /// meanwhile) throws and seals nothing. A write made after this returns has no durability promise: it lands only if the takeover has not.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Abort before the point of no return: unfreeze and admit sessions again, under the unchanged ownership. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>After the point of no return: A never runs or admits again (its late writes are fenced anyway). Idempotent.</summary>
    Task RetireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Right after the epoch takeover, before anything else of the roll-forward: stop every write the character, instance and social queues and the save queue could still attempt, of any character, because the takeover moved them all (pets, groups, GM audit, accounts, respawns and world-state saves have no fence handling and are not stopped; the flush made them durable before the commit). A retry that fires
    /// while B is being promoted is skipped. Between the commit and this step the seal of <see cref="FlushAsync"/> covers the same retry: there it is refused and dropped, not terminal. Runs only
    /// after the commit (A still owned them before it) and on every roll-forward, a recovery included. Idempotent. <see cref="RetireAsync"/> includes it, for a world that retires without a
    /// coordinator. A source with no such queue (the default) does nothing.
    /// </summary>
    Task RetireWritesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    /// <summary>
    /// Worker scope: the maps B serves (its configured Cluster:Worker:OwnedMaps), written to the shard's ownership row by the epoch takeover so the handed-over shard
    /// does not keep the old owner's map set. Null (the default, and the Realm-mode answer) leaves the row's maps untouched.
    /// </summary>
    IReadOnlyCollection<uint>? WorkerOwnedMaps => null;

    /// <summary>Check that B is warm and still a standby (throws otherwise). Idempotent.</summary>
    Task WarmAsync(CancellationToken cancellationToken);

    /// <summary>After the epoch takeover: run B under the taken-over ownership. Idempotent.</summary>
    Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken);
}

/// <summary>Routes the gateway's sessions from A to B after the epoch takeover.</summary>
public interface IWorldHandoffRouter
{
    /// <summary>
    /// Before A freezes (after it stopped admissions): start holding the clients' input instead of sending it to A, so the input sent during the freeze reaches B.
    /// Idempotent. A router that does not hold (the default) does nothing.
    /// </summary>
    Task HoldAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The handoff aborted before the takeover and A resumed: return the held input to A. Idempotent; never throws for a router that holds nothing.</summary>
    Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// After B is promoted: move the sessions to B (a seamless router re-points each one and delivers the held input to B). Idempotent: a roll-forward repeats it after a crash.
    /// </summary>
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
/// <item>A retires its writes of the moved characters, B is promoted, the gateway reroutes, A retires.</item>
/// </list>
/// Any failure before the takeover aborts: the journal records it and A resumes under its unchanged ownership. After the takeover the handoff
/// only rolls forward; a failure leaves it committed for <see cref="RecoverAsync"/> (or a repeat of it) to finish. One coordinator drives one
/// handoff at a time; the store refuses a second unresolved one of the same ownership scope (handoffs of different worker shards may be open at once).
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
    /// committed for <see cref="RecoverAsync"/>. The token applies only before the epoch takeover; the roll-forward after it is not cancellable.
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
            await router.HoldAsync(cancellationToken).ConfigureAwait(false);
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
            IReadOnlyCollection<uint>? targetMaps = target.WorkerOwnedMaps;
            committed = targetMaps is null
                ? await store.CommitEpochTakeoverAsync(handoffId, target.OwnerNode, target.Incarnation, cancellationToken).ConfigureAwait(false)
                : await store.CommitEpochTakeoverAsync(handoffId, target.OwnerNode, target.Incarnation, targetMaps, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.handoff", ex);
            AnomalyLedger.Shared.Report("cluster", "handoff-failed", handoffId.ToString(), $"phase=commit-takeover error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            // The takeover is one transaction: either it committed (then only roll forward) or nothing changed (then abort). Ask the journal.
            WorldHandoffRecord? after = await store.GetAsync(handoffId, CancellationToken.None).ConfigureAwait(false);
            if (after is { IsCommittedOrBeyond: true })
                return await RollForwardAsync(after, afterStage, CancellationToken.None).ConfigureAwait(false);
            throw await AbortAsync(handoffId, ex).ConfigureAwait(false);
        }

        if (committed is null)
            throw await AbortAsync(handoffId, new InvalidOperationException("the source no longer holds the characters ownership")).ConfigureAwait(false);

        _logger.LogInformation("World handoff {Handoff}: epoch takeover committed at epoch {Epoch}; rolling forward",
            handoffId, committed.TargetOwnershipToken!.Epoch);
        // Past the point of no return the caller's token no longer applies: a host stop or a dropped operator connection must not leave a half handoff.
        // It finishes here, or RecoverAsync finishes it on the next start.
        await Hook(afterStage, WorldHandoffStage.EpochTakeover, CancellationToken.None).ConfigureAwait(false);
        return await RollForwardAsync(committed, afterStage, CancellationToken.None).ConfigureAwait(false);
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
            return await RollForwardAsync(record, afterStage: null, CancellationToken.None).ConfigureAwait(false);

        // Before the takeover A still owns the characters, so resuming it is safe; resume first, so a journal write that fails leaves A running
        // (the handoff stays open, pre-commit, for a later recovery to abort) rather than parked with its admission closed.
        try
        {
            await source.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await router.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                record = await store.AbortAsync(handoffId, "recovered before the epoch takeover", CancellationToken.None).ConfigureAwait(false);
            }
        }

        return record;
    }

    private async Task<WorldHandoffRecord> RollForwardAsync(
        WorldHandoffRecord record, Func<WorldHandoffStage, CancellationToken, Task>? afterStage, CancellationToken cancellationToken)
    {
        CharacterOwnershipToken takenOver = record.TargetOwnershipToken
            ?? throw new InvalidOperationException($"committed world handoff {record.HandoffId} has no target ownership token");
        // First of all, and on every path here (a recovery too): from the commit on, A's retries are refused by the fence. The flush's seal keeps a refusal from stopping A until this step ends them.
        // The journal does not record it: every roll-forward repeats it, and the repeat changes nothing.
        if (record.Stage < WorldHandoffStage.Retired)
            await source.RetireWritesAsync(cancellationToken).ConfigureAwait(false);

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
            // A runs again (or is at least no longer waited for): the clients' held input goes back to it.
            try
            {
                await router.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                aborted = await store.AbortAsync(handoffId, reason, CancellationToken.None).ConfigureAwait(false);
            }
        }

        _logger.LogWarning(cause, "World handoff {Handoff} aborted; {Source} resumed under ownership epoch {Epoch}",
            handoffId, source.WorkerId, aborted.SourceOwnershipToken.Epoch);
        return new WorldHandoffAbortedException(aborted, cause);
    }
}
