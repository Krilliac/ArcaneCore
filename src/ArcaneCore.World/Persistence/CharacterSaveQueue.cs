using System.Diagnostics;
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
    private long _lastReportCheck;
    private long _lastWarning;
    private bool _warning;

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
            Slot? slot;
            lock (_gate)
            {
                slot = TakeNext();
                if (slot is null && _stopped)
                {
                    break;
                }
            }

            if (slot is null)
            {
                // Wake at least once a second so an idle queue still reports that its backlog cleared.
                await _signal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
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
                    await SaveRetainingFailureAsync(state, token, settlement).ConfigureAwait(false);
                }
            }

            if (barrier && !dropped)
            {
                await RecoverAsync(slot.Id, default).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

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

        lock (_gate)
        {
            slot.InFlight = false;
            slot.InFlightTicks = 0;
            Refresh(slot);
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
    }

    private async Task RecoverAsync(int id, CancellationToken cancellationToken)
    {
        CharacterState? retained;
        lock (_gate)
        {
            // A quarantined barrier is an ordered drain only. Fresh login must load
            // authoritative storage before the owner explicitly resumes this ID.
            if (_quarantined.Contains(id))
            {
                return;
            }

            retained = _failed.GetValueOrDefault(id)?.State;
        }

        if (retained is not null)
        {
            await SaveRetainingFailureAsync(retained, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveRetainingFailureAsync(CharacterState state, CancellationToken cancellationToken, bool trustedSettlement = false)
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

                    return;
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
                }

                return;
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
    }

    // ---- slot bookkeeping (every method below runs under _gate) ----

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

    private static CharacterState Merge(CharacterState newer, CharacterState failed) => newer with
    {
        ActionButtons = newer.ActionButtons ?? failed.ActionButtons,
        Home = newer.Home ?? failed.Home,
        Inventory = newer.Inventory ?? failed.Inventory,
        Life = newer.Life ?? failed.Life, // always complete when present, so the newest one wins
    };

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

    private sealed record FailedSave(CharacterState State, Exception Failure);

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
