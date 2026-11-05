using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Persistence;

namespace ArcaneCore.World.Cluster.Handoff;

/// <summary>
/// The draining world A, driven in its own process (Tier 3b). The freeze runs on the simulation thread between two ticks: it captures every
/// online player's save as at logout (the settlement path, which bypasses and then sets a save hold, so no later ordinary snapshot of that player is
/// written by A), then keeps the simulation thread parked until the handoff resumes A (abort) or A is disposed. A retired A stays frozen: after the
/// epoch takeover any write it still attempted would be refused by the characters fence anyway. A player with a quest settlement in flight is not
/// quiescent, so the freeze refuses (the handoff aborts and A resumes). After the epoch takeover the source also gives up A's characters database
/// writer lock (<paramref name="writerGuard"/>, A's own guard, required) so B's promotion can take it; see <see cref="ReleaseWriterLockAsync"/>.
/// The handoff moves the ownership scope A holds: <c>world</c> for a Combined world, or A's <c>worker:&lt;shard&gt;</c> row in Cluster:Fencing:Scope=Worker (then the lock released is A's shard
/// lock, and handoffs of two different shards are independent; design _wf/writer-lock-DESIGN.md step C7).
/// </summary>
public sealed class LocalWorldHandoffSource(
    WorkerNodeId workerId,
    WorldRuntime world,
    CharacterSaveQueue saveQueue,
    WorkerAdmission admission,
    WorldWriterGuard writerGuard,
    CharacterPersistenceBarrier? barrier = null,
    CharacterRetirementRegistry? retirement = null) : IWorldHandoffSource, IDisposable
{
    /// <summary>The admission closed reason while A drains.</summary>
    public const string DrainingReason = "draining";

    /// <summary>The admission closed reason of a retired A.</summary>
    public const string RetiredReason = "retired";

    /// <summary>Times the flush goes over the save queue's open characters before it gives up: nothing should open after the first pass, because the simulation is parked.</summary>
    private const int OpenSavePasses = 3;

    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly List<int> _heldHere = [];
    private Task<bool>? _parked;
    private Task<IReadOnlyList<Task>>? _frozen;
    private readonly List<int> _frozenCharacters = [];
    private bool _retired;

    public WorkerNodeId WorkerId => workerId;

    public CharacterOwnershipToken Ownership => world.CharacterOwnership
        ?? throw new InvalidOperationException("the source world holds no characters ownership");

    /// <summary>True while the simulation thread is parked by the freeze.</summary>
    public bool IsFrozen
    {
        get { lock (_gate) return _parked is { IsCompleted: false } && !_release.IsSet; }
    }

    public Task StopAdmissionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_retired)
                admission.Close(DrainingReason);
        }

        return Task.CompletedTask;
    }

    public async Task FreezeAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<Task>> frozen;
        lock (_gate)
        {
            if (_retired)
                throw new InvalidOperationException("a retired world is not frozen again");
            if (_frozen is null)
            {
                _release.Reset();
                var captured = new TaskCompletionSource<IReadOnlyList<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _frozen = captured.Task;
                _parked = world.InvokeAsync(() => Park(captured), CancellationToken.None);
                // A park that never ran (the world stopped) or failed before capturing fails the freeze too.
                _ = _parked.ContinueWith(
                    parked => captured.TrySetException(parked.Exception?.InnerException ?? new InvalidOperationException("the simulation did not freeze")),
                    CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }

            frozen = _frozen;
        }

        await frozen.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<Task>> frozen;
        lock (_gate)
            frozen = _frozen ?? throw new InvalidOperationException("the source world is not frozen; nothing to flush");
        IReadOnlyList<Task> saves = await frozen.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(saves).WaitAsync(cancellationToken).ConfigureAwait(false);

        // Then every other per-character queue and settlement (explored zones, rest, quests, taxi, honor, ...): what B will read must be durable.
        if (barrier is not null)
        {
            int[] characters;
            lock (_gate)
                characters = [.. _frozenCharacters];
            foreach (int id in characters)
                await FlushCharacterAsync(id, cancellationToken).ConfigureAwait(false);
        }

        // Then what the save queue still has open for characters the freeze did not capture: an offline character's retained, waiting or in-flight save. A still owns them here and can
        // write them; after the commit the fence would refuse them. One that cannot be written fails the flush, and the handoff aborts with nothing lost. A quarantined character has none
        // open: its retained snapshot is superseded by design and never retried. The simulation is parked, so a pass adds nothing new; the repeat catches a late settlement's write.
        for (int pass = 0; ; pass++)
        {
            int[] open = saveQueue.UnsettledCharacterIds();
            if (open.Length == 0)
                return;
            if (pass == OpenSavePasses)
                throw new InvalidOperationException($"characters {string.Join(", ", open)} still have saves open after the flush; the handoff cannot commit");
            foreach (int id in open)
                await FlushCharacterAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    // Every queue of the character through the barrier; without one, the save queue alone, under the barrier's time limit: a locked database must fail the handoff, not hold it open.
    private async Task FlushCharacterAsync(int id, CancellationToken cancellationToken)
    {
        if (barrier is null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(CharacterPersistenceBarrier.DefaultTimeout);
            await saveQueue.FlushCharacterAsync(id, budget.Token).ConfigureAwait(false);
            return;
        }

        CharacterPersistenceFlushResult flushed = await barrier.TryFlushCharacterAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!flushed.IsDurable)
            throw new InvalidOperationException($"character {id} is not durable ({string.Join(", ", flushed.FailedQueues)}); the handoff cannot commit");
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_retired)
                throw new InvalidOperationException("a retired world does not resume: the handoff passed its point of no return");
            foreach (int id in _heldHere)
                saveQueue.ResumeCharacter(id);
            _heldHere.Clear();
            _frozenCharacters.Clear();
            _frozen = null;
            _release.Set();
            admission.Open();
        }

        return Task.CompletedTask;
    }

    public async Task RetireAsync(CancellationToken cancellationToken)
    {
        // The self-heal reaches this without the coordinator's earlier step, so it runs here too; repeating it changes nothing.
        await RetireWritesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            admission.Close(RetiredReason);
    }

    /// <summary>The save-queue part of the retirement. Only after the commit: before it, these are writes A still owns. From here on A does not resume either.</summary>
    public Task RetireWritesAsync(CancellationToken cancellationToken)
    {
        int[] moved;
        lock (_gate)
        {
            _retired = true;
            moved = [.. _frozenCharacters];
        }

        // The characters this world froze now belong to the new owner: every write queue of this process drops their writes silently and counts them
        // (the shared registry, consulted before a write is enqueued and again before it runs), and the save queue drops their waiting and retained snapshots and skips later ones
        // (one informational line), instead of attempting writes the fence must refuse. The frozen characters are named, and so is every offline character the save queue still holds
        // anything for: retained, waiting or in flight (the flush normally leaves none; each would be refused too). Any other character keeps its writes.
        int[] retiring = [.. moved, .. saveQueue.RetainedCharacterIds(), .. saveQueue.UnsettledCharacterIds()];
        retirement?.Retire(retiring);
        saveQueue.RetireCharacters(retiring);
        return Task.CompletedTask;
    }

    /// <summary>Gives up A's characters database writer lock (the guard of A's container) so the promoted standby can take it; A is fenced out by the epoch takeover. Idempotent.</summary>
    public Task ReleaseWriterLockAsync(CancellationToken cancellationToken)
    {
        writerGuard.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Releases a parked simulation thread (the process is stopping), retired or not.</summary>
    public void Dispose()
    {
        _release.Set();
    }

    private bool Park(TaskCompletionSource<IReadOnlyList<Task>> captured)
    {
        // On the simulation thread, between two ticks.
        var saves = new List<Task>();
        try
        {
            uint now = world.NowMs;
            foreach (Player player in world.OnlinePlayers)
            {
                if (player.IsQuestSettlementPending)
                    throw new InvalidOperationException($"player {player.Name} has a quest settlement in flight; the world is not quiescent");
            }

            foreach (Player player in world.OnlinePlayers)
            {
                CharacterState state = player.CreateSnapshot(now);
                bool heldBefore = saveQueue.IsHeld(state.Id);
                saves.Add(saveQueue.SaveForSettlementAsync(state));
                saveQueue.HoldCharacter(state.Id);
                lock (_gate)
                    _frozenCharacters.Add(state.Id);
                if (!heldBefore)
                {
                    lock (_gate)
                        _heldHere.Add(state.Id);
                }
            }
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("cluster.handoff", ex);
            AnomalyLedger.Shared.Report("cluster", "handoff-failed", "local-freeze", $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            lock (_gate)
            {
                foreach (int id in _heldHere)
                    saveQueue.ResumeCharacter(id);
                _heldHere.Clear();
            }

            captured.TrySetException(ex);
            return false;
        }

        captured.TrySetResult(saves);
        _release.Wait();
        return true;
    }
}

/// <summary>The standby world B, driven in its own process (Tier 3b): warm means started and still a standby; promotion is <see cref="WorldHost.PromoteAsync"/>.</summary>
public sealed class StandbyWorldHandoffTarget(WorkerNodeId workerId, WorldHost host) : IWorldHandoffTarget
{
    public WorkerNodeId WorkerId => workerId;

    public string OwnerNode => host.OwnerNode;

    public Guid Incarnation => host.Incarnation;

    /// <summary>Worker scope: the standby's configured maps, written by the epoch takeover; null in Realm scope.</summary>
    public IReadOnlyCollection<uint>? WorkerOwnedMaps => host.WorkerOwnedMaps;

    public Task WarmAsync(CancellationToken cancellationToken)
    {
        if (!host.IsStandby)
            throw new InvalidOperationException("the target world is not a standby (Cluster:Worker:Standby), or was already promoted");
        return Task.CompletedTask;
    }

    /// <summary>Once promoted, the host returns its successful promotion again: a roll-forward may repeat the step.</summary>
    public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        => host.PromoteAsync(takenOver, cancellationToken);
}
