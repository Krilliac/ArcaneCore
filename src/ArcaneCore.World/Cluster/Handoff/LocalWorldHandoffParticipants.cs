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
/// quiescent, so the freeze refuses (the handoff aborts and A resumes).
/// </summary>
public sealed class LocalWorldHandoffSource(
    WorkerNodeId workerId,
    WorldRuntime world,
    CharacterSaveQueue saveQueue,
    WorkerAdmission admission,
    CharacterPersistenceBarrier? barrier = null,
    WorldWriterGuard? writerGuard = null) : IWorldHandoffSource, IDisposable
{
    /// <summary>The admission closed reason while A drains.</summary>
    public const string DrainingReason = "draining";

    /// <summary>The admission closed reason of a retired A.</summary>
    public const string RetiredReason = "retired";

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
        if (barrier is null)
            return;

        // Then every other per-character queue and settlement (explored zones, rest, quests, taxi, honor, ...): what B will read must be durable.
        int[] characters;
        lock (_gate)
            characters = [.. _frozenCharacters];
        foreach (int id in characters)
        {
            CharacterPersistenceFlushResult flushed = await barrier.TryFlushCharacterAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!flushed.IsDurable)
                throw new InvalidOperationException($"character {id} is not durable ({string.Join(", ", flushed.FailedQueues)}); the handoff cannot commit");
        }
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

    public Task RetireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _retired = true;
            admission.Close(RetiredReason);
        }

        return Task.CompletedTask;
    }

    /// <summary>Gives up A's characters database writer lock (the guard of A's container) so the promoted standby can take it; A is fenced out by the epoch takeover. Idempotent.</summary>
    public Task ReleaseWriterLockAsync(CancellationToken cancellationToken)
    {
        writerGuard?.Dispose();
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
