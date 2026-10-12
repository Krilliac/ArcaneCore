using System.Diagnostics;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Persists character snapshots taken on the world thread, off the world thread, by one consumer.
/// Snapshots are kept per character: a snapshot that arrives while an earlier one of the same character is
/// still waiting supersedes it atomically (the newer one wins; optional sections it leaves out are inherited from
/// the superseded one, exactly like a retained failed save), so a character's saves never overtake each other and
/// the queue holds at most one waiting snapshot per character. A write in flight is never replaced: the newer
/// snapshot follows it. Settlement pre-reward saves and character barriers (<see cref="FlushCharacterAsync"/>) use a
/// priority lane so they do not wait behind unrelated characters' autosaves; every eighth pick is an ordinary
/// snapshot, so the priority lane cannot starve them. Ordering is per character only, as before.
/// Thread-safe; all state is guarded by one lock that is never held across an await or a store call.
/// </summary>
public sealed class CharacterSaveQueue : ICharacterSaveQueue
{
    private const int MaxAttempts = 3;

    /// <summary>Consecutive priority picks after which an ordinary snapshot is taken when one is waiting.</summary>
    private const int PriorityBurst = 7;

    /// <summary>Picks after which a retained busy failure is retried even though other snapshots are still waiting, so a saturated queue cannot starve it.</summary>
    private const int RetainedEvery = 16;

    /// <summary>Pause before the next write after consecutive "database is locked" failures: 1 s, 2 s, then 5 s.</summary>
    private static readonly long[] BusyBackoffTicks = [Stopwatch.Frequency, 2 * Stopwatch.Frequency, 5 * Stopwatch.Frequency];

    private static readonly long ReportGateTicks = Stopwatch.Frequency;
    private static readonly long ReportRepeatTicks = 30 * Stopwatch.Frequency;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CharacterSaveQueue> _logger;
    private readonly WorldRuntimeOptions _settings; // the shared options object: the two warning thresholds are live (.reload config)
    private readonly Lock _gate = new();
    private readonly Lock _reportGate = new();
    private readonly Dictionary<int, FailedSave> _failed = [];
    private readonly HashSet<int> _held = [];
    private readonly HashSet<int> _quarantined = [];
    private readonly Dictionary<int, Slot> _slots = [];
    private readonly Queue<int> _priorityHints = new();
    private readonly Queue<int> _normalHints = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Task? _consumer;
    private int _pending;
    private int _priorityRun;
    private bool _stopped;
    private long _busyUntil; // Stopwatch ticks before which the consumer starts no write (a lock holder was just seen); 0 = none
    private int _busyStreak; // consecutive "database is locked" failures since the last successful write
    private int _sinceRetained; // picks since a retained busy failure was last retried
    private long _lastReportCheck;
    private long _lastWarning;
    private bool _warning;
    private Exception? _terminalFailure;

    /// <summary>Raised once when a durable ownership fence rejects a write.</summary>
    public event Action<Exception>? FatalFailure;

    // Test seam: observe the hand-off to waiters before the consumer takes another slot.
    internal Action? BeforeWaitersCompletedForTesting { get; set; }

    /// <param name="scopes">Where the scoped <see cref="ICharacterStore"/> comes from.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="options">Backlog warning thresholds (<c>World:SaveQueueWarnDepth</c>, <c>World:SaveQueueWarnAgeMs</c>); the defaults when null.</param>
    public CharacterSaveQueue(IServiceScopeFactory scopes, ILogger<CharacterSaveQueue> logger, IOptions<WorldRuntimeOptions>? options = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = options?.Value ?? new WorldRuntimeOptions();
    }

    /// <summary>Characters with a snapshot waiting or being written.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>How long the longest-waiting snapshot has been waiting or being written (zero when nothing is pending).</summary>
    public TimeSpan OldestPendingAge
    {
        get
        {
            lock (_gate)
            {
                return OldestAgeLocked();
            }
        }
    }

    /// <summary>True when a snapshot of this character is waiting (not merely being written): another one would only supersede it.</summary>
    public bool IsPending(int characterId)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(characterId, out Slot? slot) && slot.State is not null;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _consumer ??= Task.Run(ConsumeAsync);
        }
    }

    public void Enqueue(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        CharacterState copy = Copy(state);
        lock (_gate)
        {
            ThrowIfTerminalLocked();
            if (_held.Contains(state.Id) || _quarantined.Contains(state.Id))
            {
                return;
            }

            if (_stopped)
            {
                _logger.LogError("save queue closed; character {Id} state not accepted", state.Id);
                return;
            }

            Slot slot = GetSlot(state.Id);
            PutState(slot, copy);
            AddHint(slot, priority: false);
        }

        _signal.Release();
        MaybeReport();
    }

    /// <summary>Suppress new ordinary snapshots; snapshots queued before the hold still drain.</summary>
    public void HoldCharacter(int characterId)
    {
        lock (_gate)
        {
            ThrowIfTerminalLocked();
            ObjectDisposedException.ThrowIf(_stopped, this);
            _held.Add(characterId);
        }
    }

    /// <summary>
    /// Save the captured pre-settlement snapshot, bypassing a hold, ahead of other characters' ordinary snapshots.
    /// The snapshot is merged over any snapshot of the same character still waiting; callers capture it on the world
    /// thread and hold the character in the same critical section, so a waiting one is always older.
    /// The returned task observes the entire write, including cooperative cancellation once the write has started.
    /// If <paramref name="cancellationToken"/> fires before the write starts the task is cancelled, the snapshot stays
    /// queued as an ordinary one (the newest snapshot is never dropped) and nothing is logged as a failure.
    /// Quarantine always refuses the snapshot.
    /// </summary>
    public Task SaveForSettlementAsync(CharacterState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        CharacterState copy = Copy(state);
        var waiter = new Waiter(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), settlement: true, cancellationToken);
        lock (_gate)
        {
            ThrowIfTerminalLocked();
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_quarantined.Contains(state.Id))
            {
                throw new InvalidOperationException($"character {state.Id} is quarantined");
            }

            _consumer ??= Task.Run(ConsumeAsync);
            Slot slot = GetSlot(state.Id);
            PutState(slot, copy);
            if (cancellationToken.IsCancellationRequested)
            {
                AddHint(slot, priority: false);
                waiter.Done.TrySetCanceled(cancellationToken);
            }
            else
            {
                slot.Waiters.Add(waiter);
                AddHint(slot, priority: true);
                Register(slot, waiter);
            }
        }

        _signal.Release();
        return waiter.Done.Task;
    }

    /// <summary>Drain this character's waiting snapshot and retry its retained failure. Cancellation never removes retained state.</summary>
    public Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var waiter = new Waiter(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), settlement: false, cancellationToken);
        lock (_gate)
        {
            ThrowIfTerminalLocked();
            ObjectDisposedException.ThrowIf(_stopped, this);
            _consumer ??= Task.Run(ConsumeAsync);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            Slot slot = GetSlot(characterId);
            slot.Waiters.Add(waiter);
            AddHint(slot, priority: true);
            Register(slot, waiter);
        }

        _signal.Release();
        return waiter.Done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Suppress new and still-queued snapshots until authoritative loading explicitly resumes the character.</summary>
    public void QuarantineCharacter(int characterId)
    {
        lock (_gate)
        {
            _quarantined.Add(characterId);
        }
    }

    public void ResumeCharacter(int characterId)
    {
        lock (_gate)
        {
            _held.Remove(characterId);
            if (_quarantined.Remove(characterId))
            {
                // Authoritative publication/loading supersedes any retained old-session state.
                _failed.Remove(characterId);
            }
        }
    }

    /// <summary>
    /// The character was deleted: forget its holds, quarantine and retained failed snapshot, so a
    /// later character that reuses the id never inherits them. Call after its writes drained.
    /// </summary>
    public void ForgetCharacter(int characterId)
    {
        lock (_gate)
        {
            _held.Remove(characterId);
            _quarantined.Remove(characterId);
            _failed.Remove(characterId);
        }
    }

    public bool IsHeld(int characterId)
    {
        lock (_gate)
        {
            return _held.Contains(characterId);
        }
    }

    public bool IsQuarantined(int characterId)
    {
        lock (_gate)
        {
            return _quarantined.Contains(characterId);
        }
    }

    /// <summary>Stop accepting snapshots and wait until every queued one is written.</summary>
    public async Task StopAsync()
    {
        Task consumer;
        lock (_gate)
        {
            _stopped = true;
            consumer = _consumer ??= Task.Run(ConsumeAsync);
        }

        _signal.Release();
        await consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        while (true)
        {
            MaybeReport();
            Slot? slot = null;
            long pauseTicks = 0;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (!_stopped && _busyUntil > now)
                {
                    // A write just failed because another connection holds the database: starting another write now would wait out
                    // the whole write bound for nothing. Shutdown ignores the pause (it retries inline and must not sit idle).
                    pauseTicks = _busyUntil - now;
                }
                else
                {
                    if (_sinceRetained >= RetainedEvery)
                    {
                        slot = TakeRetained();
                        _sinceRetained = 0; // scan _failed at most once per RetainedEvery picks, whether or not anything was retained
                    }

                    slot ??= TakeNext();
                    if (slot is null)
                    {
                        slot = TakeRetained(); // idle: retry the retained busy failures
                    }
                    else
                    {
                        _sinceRetained++;
                    }

                    if (slot is null && _stopped)
                    {
                        break;
                    }
                }
            }

            if (slot is null)
            {
                // Wake at least once a second so an idle queue still reports that its backlog cleared; wake when a pause ends.
                TimeSpan wait = pauseTicks > 0
                    ? TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, (long)(pauseTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency) + TimeSpan.TicksPerMillisecond))
                    : TimeSpan.FromSeconds(1);
                await _signal.WaitAsync(wait).ConfigureAwait(false);
                continue;
            }

            await WriteSlotAsync(slot).ConfigureAwait(false);
        }

        int[] failed;
        lock (_gate)
        {
            failed = _failed.Keys.ToArray();
        }

        foreach (int id in failed)
        {
            try
            {
                await RecoverAsync(id, default).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // SaveRetainingFailureAsync keeps the snapshot and original failure below.
            }
        }

        lock (_gate)
        {
            if (_terminalFailure is { } terminal)
            {
                throw new InvalidOperationException("character saves stopped after a terminal ownership failure", terminal);
            }
            if (_failed.Count > 0)
            {
                throw new InvalidOperationException($"character saves did not drain for characters {string.Join(", ", _failed.Keys.Order())}",
                    _failed.Values.First().Failure);
            }
        }
    }

    /// <summary>Write one character's waiting snapshot, then complete the waiters that were attached before the write began.</summary>
    private async Task WriteSlotAsync(Slot slot)
    {
        CharacterState? state;
        Waiter[] waiters;
        lock (_gate)
        {
            state = slot.State;
            slot.State = null;
            slot.InFlightTicks = state is null ? Stopwatch.GetTimestamp() : slot.FirstTicks;
            waiters = [.. slot.Waiters];
            slot.Waiters.Clear();
            slot.InFlight = true;
            Refresh(slot);
        }

        // Disposed outside the lock: a cancellation callback that is running waits for the lock, and Dispose waits for it.
        bool settlement = false;
        bool barrier = false;
        CancellationToken token = default;
        int settlements = 0;
        foreach (Waiter waiter in waiters)
        {
            waiter.Registration.Dispose();
            if (waiter.Settlement)
            {
                settlement = true;
                token = waiter.Token;
                settlements++;
            }
            else
            {
                barrier = true;
            }
        }

        if (settlements != 1)
        {
            token = default; // several settlements share one write: none of them may cancel the others
        }

        Exception? error = null;
        bool dropped = false;
        bool deferred = false; // the database was locked: the snapshot is retained and the waiters stay queued for the retry
        try
        {
            if (state is not null)
            {
                lock (_gate)
                {
                    if (_quarantined.Contains(slot.Id))
                    {
                        dropped = true;
                    }
                    else if (_failed.TryGetValue(slot.Id, out FailedSave? earlier))
                    {
                        state = Merge(state, earlier.State);
                    }
                }

                if (!dropped)
                {
                    deferred = await SaveRetainingFailureAsync(state, token, settlement).ConfigureAwait(false);
                }
            }
            else if (settlement)
            {
                // A settlement waiter whose snapshot was retained by a locked database and is now being retried.
                lock (_gate)
                {
                    dropped = _quarantined.Contains(slot.Id);
                }
            }

            if ((barrier || state is null) && !dropped && !deferred)
            {
                deferred = await RecoverAsync(slot.Id, token, trustedSettlement: settlement && state is null).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        lock (_gate)
        {
            slot.InFlight = false;
            slot.InFlightTicks = 0;
            Refresh(slot);
            if (deferred && error is null)
            {
                // Keep the waiters (barriers and settlements) attached to the slot, ahead of any that arrived meanwhile: they complete
                // when the retained snapshot is finally written, or are cancelled by their own tokens.
                slot.Waiters.InsertRange(0, waiters);
                foreach (Waiter waiter in waiters)
                {
                    Register(slot, waiter);
                }
            }

            if (slot.State is not null || slot.Waiters.Count > 0)
            {
                // Arrived while this write was in flight: it follows the write.
                AddHint(slot, priority: slot.Waiters.Count > 0);
            }
            else
            {
                Reap(slot);
            }
        }

        if (deferred && error is null)
        {
            return;
        }

        // Publish a barrier result only after its slot no longer counts as in flight.
        BeforeWaitersCompletedForTesting?.Invoke();
        foreach (Waiter waiter in waiters)
        {
            if (dropped && waiter.Settlement)
            {
                waiter.Done.TrySetException(new InvalidOperationException($"character {slot.Id} is quarantined"));
            }
            else if (error is not null)
            {
                waiter.Done.TrySetException(error);
            }
            else
            {
                waiter.Done.TrySetResult();
            }
        }
    }

    /// <summary>Retry the retained snapshot of a character, if any. True when the database was locked again and the snapshot stays retained for a later retry.</summary>
    private async Task<bool> RecoverAsync(int id, CancellationToken cancellationToken, bool trustedSettlement = false)
    {
        CharacterState? retained;
        lock (_gate)
        {
            // A quarantined barrier is an ordered drain only. Fresh login must load
            // authoritative storage before the owner explicitly resumes this ID.
            if (_terminalFailure is not null)
            {
                return false;
            }
            if (_quarantined.Contains(id))
            {
                if (trustedSettlement)
                {
                    throw new InvalidOperationException($"character {id} is quarantined"); // a settlement is never told it was saved when nothing was written
                }

                return false;
            }

            retained = _failed.GetValueOrDefault(id)?.State;
        }

        return retained is not null && await SaveRetainingFailureAsync(retained, cancellationToken, trustedSettlement).ConfigureAwait(false);
    }

    /// <summary>
    /// Write <paramref name="state"/>; on failure retain it in <c>_failed</c>. A failure that is SQLite reporting the file busy or locked (another
    /// connection holds it; the store's bounded wait has already elapsed) is NOT retried inline: the snapshot is retained, the whole queue pauses
    /// for a backoff (1 s, 2 s, then 5 s while it keeps happening, reset by the next successful write) and the method returns true, so the consumer
    /// moves on and the snapshot is retried later (<see cref="TakeRetained"/>, a barrier, or a newer snapshot of the character). Any other failure, and any failure while
    /// the queue is stopping (the drain has nothing else to do), is retried inline up to <see cref="MaxAttempts"/> times and then thrown.
    /// Returns false when the snapshot was written (or dropped by quarantine).
    /// </summary>
    private async Task<bool> SaveRetainingFailureAsync(CharacterState state, CancellationToken cancellationToken, bool trustedSettlement = false)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lock (_gate)
            {
                if (_quarantined.Contains(state.Id))
                {
                    if (trustedSettlement)
                    {
                        throw new InvalidOperationException($"character {state.Id} is quarantined");
                    }

                    return false;
                }
            }

            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
                await store.SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _failed.Remove(state.Id);
                    _busyStreak = 0;
                    _busyUntil = 0;
                }

                return false;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                EnterTerminalFailure(state, ex);
                throw;
            }
            catch (Exception ex) when (DatabaseTransience.IsBusy(ex) && !IsStopping()) // also with a cancelled token: the lock wait ended, the snapshot must still be retried
            {
                TimeSpan pause;
                lock (_gate)
                {
                    _failed[state.Id] = new FailedSave(state, ex, Deferred: true);
                    long ticks = BusyBackoffTicks[Math.Min(_busyStreak, BusyBackoffTicks.Length - 1)];
                    _busyStreak++;
                    _busyUntil = Stopwatch.GetTimestamp() + ticks;
                    pause = Stopwatch.GetElapsedTime(0, ticks);
                }

                _logger.LogWarning(ex, "saving character {Id} failed: the database is locked by another writer; snapshot retained, saves pause {PauseMs} ms then retry", state.Id, (long)pause.TotalMilliseconds);
                return true;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _failed[state.Id] = new FailedSave(state, ex);
                }

                _logger.LogWarning(ex, "saving character {Id} failed (attempt {Attempt}); retrying", state.Id, attempt);
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failed[state.Id] = new FailedSave(state, ex);
                }

                _logger.LogError(ex, "saving character {Id} failed; authoritative snapshot retained", state.Id);
                throw;
            }
        }

        return false;
    }

    private bool IsStopping()
    {
        lock (_gate)
        {
            return _stopped;
        }
    }

    private void ThrowIfTerminalLocked()
    {
        if (_terminalFailure is { } failure)
            throw new InvalidOperationException("character save queue has a terminal ownership failure", failure);
    }

    private void EnterTerminalFailure(CharacterState current, Exception failure)
    {
        List<Waiter> waiters = [];
        bool publish;
        lock (_gate)
        {
            publish = _terminalFailure is null;
            _terminalFailure ??= failure;
            _stopped = true;
            _failed[current.Id] = new FailedSave(current, failure, Deferred: false, Terminal: true);

            // No queued snapshot may be replayed under an unknown owner. Keep each one in the
            // diagnostic failure ledger, but remove it from all scheduling lanes.
            foreach (Slot slot in _slots.Values)
            {
                if (slot.State is { } state)
                {
                    _failed[state.Id] = new FailedSave(state, failure, Deferred: false, Terminal: true);
                    slot.State = null;
                }

                waiters.AddRange(slot.Waiters);
                slot.Waiters.Clear();
                slot.NormalHint = false;
                slot.PriorityHint = false;
                Refresh(slot);
            }
            _priorityHints.Clear();
            _normalHints.Clear();
        }

        foreach (Waiter waiter in waiters)
        {
            waiter.Registration.Dispose();
            waiter.Done.TrySetException(failure);
        }

        if (publish)
        {
            try { FatalFailure?.Invoke(failure); }
            catch (Exception callbackError) when (callbackError is not OutOfMemoryException)
            { _logger.LogError(callbackError, "character save queue fatal-failure callback failed"); }
        }
    }

    // ---- slot bookkeeping (every method below runs under _gate) ----

    /// <summary>
    /// A character whose snapshot was retained because the database was locked and which nothing else is about to write (no waiting snapshot,
    /// no waiter, no write in flight): its slot, so the consumer retries it. Null when there is none.
    /// </summary>
    private Slot? TakeRetained()
    {
        foreach ((int id, FailedSave failed) in _failed)
        {
            if (!failed.Deferred || failed.Terminal || _quarantined.Contains(id))
            {
                continue;
            }

            if (_slots.TryGetValue(id, out Slot? existing) && (existing.InFlight || existing.State is not null || existing.Waiters.Count > 0 || existing.NormalHint || existing.PriorityHint))
            {
                continue; // the ordinary path writes it (merged with the retained snapshot)
            }

            _sinceRetained = 0;
            return existing ?? GetSlot(id);
        }

        return null;
    }

    private Slot GetSlot(int id)
    {
        if (!_slots.TryGetValue(id, out Slot? slot))
        {
            slot = new Slot(id);
            _slots[id] = slot;
        }

        return slot;
    }

    /// <summary>Make <paramref name="copy"/> the waiting snapshot: the newer one wins, optional sections it omits come from the superseded one.</summary>
    private void PutState(Slot slot, CharacterState copy)
    {
        if (slot.State is { } waiting)
        {
            slot.State = Merge(copy, waiting);
        }
        else
        {
            slot.State = copy;
            slot.FirstTicks = Stopwatch.GetTimestamp();
        }

        Refresh(slot);
    }

    /// <summary>Queue the slot for the consumer: always on the ordinary lane (a cancelled priority request falls back to it), and on the priority lane when asked.</summary>
    private void AddHint(Slot slot, bool priority)
    {
        if (!slot.NormalHint)
        {
            slot.NormalHint = true;
            _normalHints.Enqueue(slot.Id);
        }

        if (priority && !slot.PriorityHint)
        {
            slot.PriorityHint = true;
            _priorityHints.Enqueue(slot.Id);
        }
    }

    private void Refresh(Slot slot)
    {
        bool active = slot.State is not null || slot.InFlight;
        if (active != slot.Active)
        {
            slot.Active = active;
            Volatile.Write(ref _pending, _pending + (active ? 1 : -1));
        }
    }

    private void Reap(Slot slot)
    {
        if (slot.State is null && slot.Waiters.Count == 0 && !slot.InFlight && !slot.NormalHint && !slot.PriorityHint)
        {
            _slots.Remove(slot.Id);
        }
    }

    /// <summary>The next slot to write: the priority lane first, except that every eighth pick is an ordinary one when any is waiting.</summary>
    private Slot? TakeNext()
    {
        bool normalFirst = _priorityRun >= PriorityBurst;
        Slot? slot = normalFirst ? Dequeue(priority: false) : Dequeue(priority: true);
        if (slot is not null)
        {
            _priorityRun = normalFirst ? 0 : _priorityRun + 1;
            return slot;
        }

        slot = normalFirst ? Dequeue(priority: true) : Dequeue(priority: false);
        if (slot is not null)
        {
            _priorityRun = normalFirst ? _priorityRun + 1 : 0;
        }

        return slot;
    }

    private Slot? Dequeue(bool priority)
    {
        Queue<int> hints = priority ? _priorityHints : _normalHints;
        while (hints.TryDequeue(out int id))
        {
            if (!_slots.TryGetValue(id, out Slot? slot))
            {
                continue;
            }

            if (priority)
            {
                slot.PriorityHint = false;
            }
            else
            {
                slot.NormalHint = false;
            }

            bool ready = !slot.InFlight && (priority ? slot.Waiters.Count > 0 : slot.State is not null || slot.Waiters.Count > 0);
            if (ready)
            {
                return slot;
            }

            Reap(slot);
        }

        return null;
    }

    private void Register(Slot slot, Waiter waiter)
    {
        if (waiter.Token.CanBeCanceled)
        {
            waiter.Registration = waiter.Token.Register(
                static state =>
                {
                    var (queue, owner, pending) = ((CharacterSaveQueue, Slot, Waiter))state!;
                    queue.CancelWaiter(owner, pending);
                },
                (this, slot, waiter));
        }
    }

    /// <summary>A waiter whose token fired before its write began: complete it as cancelled and leave the snapshot queued as an ordinary one.</summary>
    private void CancelWaiter(Slot slot, Waiter waiter)
    {
        bool removed;
        lock (_gate)
        {
            removed = slot.Waiters.Remove(waiter);
        }

        if (removed)
        {
            waiter.Done.TrySetCanceled(waiter.Token);
        }
    }

    private TimeSpan OldestAgeLocked()
    {
        long oldest = long.MaxValue;
        foreach (Slot slot in _slots.Values)
        {
            if (slot.State is not null)
            {
                oldest = Math.Min(oldest, slot.FirstTicks);
            }

            if (slot.InFlight)
            {
                oldest = Math.Min(oldest, slot.InFlightTicks);
            }
        }

        return oldest == long.MaxValue ? TimeSpan.Zero : Stopwatch.GetElapsedTime(oldest);
    }

    /// <summary>
    /// At most once a second: warn (and again at most every 30 s) while the backlog is at or past
    /// <c>World:SaveQueueWarnDepth</c> characters or its oldest snapshot is at or past <c>World:SaveQueueWarnAgeMs</c>,
    /// and say once when it has cleared. A threshold of 0 disables that check.
    /// </summary>
    private void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _lastReportCheck) < ReportGateTicks)
        {
            return;
        }

        int depth;
        TimeSpan age;
        int warnDepth;
        int warnAgeMs;
        bool warn = false;
        bool cleared = false;
        lock (_reportGate)
        {
            if (now - _lastReportCheck < ReportGateTicks)
            {
                return;
            }

            _lastReportCheck = now;
            lock (_gate)
            {
                depth = _pending;
                age = OldestAgeLocked();
            }

            warnDepth = _settings.SaveQueueWarnDepth;
            warnAgeMs = _settings.SaveQueueWarnAgeMs;
            bool over = (warnDepth > 0 && depth >= warnDepth) || (warnAgeMs > 0 && age.TotalMilliseconds >= warnAgeMs);
            if (over)
            {
                if (!_warning || now - _lastWarning >= ReportRepeatTicks)
                {
                    warn = true;
                    _lastWarning = now;
                }

                _warning = true;
            }
            else if (_warning)
            {
                _warning = false;
                cleared = true;
            }
        }

        if (warn)
        {
            _logger.LogWarning("character save backlog: {Depth} characters pending, oldest waiting {AgeMs} ms (warns at {WarnDepth} characters or {WarnAgeMs} ms)",
                depth, (long)age.TotalMilliseconds, warnDepth, warnAgeMs);
        }
        else if (cleared)
        {
            _logger.LogInformation("character save backlog cleared: {Depth} characters pending, oldest waiting {AgeMs} ms", depth, (long)age.TotalMilliseconds);
        }
    }

    private static CharacterState Merge(CharacterState newer, CharacterState failed)
    {
        if (!Equals(newer.Ownership, failed.Ownership))
        {
            return newer;
        }

        return newer with
        {
            ActionButtons = newer.ActionButtons ?? failed.ActionButtons,
            Home = newer.Home ?? failed.Home,
            Inventory = newer.Inventory ?? failed.Inventory,
            Life = newer.Life ?? failed.Life, // always complete when present, so the newest one wins
        };
    }

    private static CharacterState Copy(CharacterState state) => state with
    {
        ActionButtons = state.ActionButtons is { } buttons ? Array.AsReadOnly(buttons.ToArray()) : null,
        Inventory = state.Inventory is { } inventory ? new InventorySnapshot(Array.AsReadOnly(inventory.Items.Select(row => row with
        {
            Item = row.Item with
            {
                Charges = Array.AsReadOnly(row.Item.Charges.ToArray()),
                Enchantments = Array.AsReadOnly(row.Item.Enchantments.ToArray()),
            },
        }).ToArray()), inventory.AmmoId) : null,
    };

    private sealed record FailedSave(CharacterState State, Exception Failure, bool Deferred = false, bool Terminal = false);

    /// <summary>Someone awaiting a character's write: a settlement pre-reward save (its own snapshot) or a barrier.</summary>
    private sealed class Waiter(TaskCompletionSource done, bool settlement, CancellationToken token)
    {
        public TaskCompletionSource Done { get; } = done;
        public bool Settlement { get; } = settlement;
        public CancellationToken Token { get; } = token;
        public CancellationTokenRegistration Registration;
    }

    /// <summary>One character's place in the queue.</summary>
    private sealed class Slot(int id)
    {
        public int Id { get; } = id;
        public CharacterState? State;
        public long FirstTicks;
        public long InFlightTicks;
        public readonly List<Waiter> Waiters = [];
        public bool InFlight;
        public bool Active;
        public bool NormalHint;
        public bool PriorityHint;
    }
}
