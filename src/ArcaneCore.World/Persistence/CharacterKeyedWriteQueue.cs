using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The shared engine of the single-value-per-character write queues (<see cref="Progression.RestWriteQueue"/>,
/// <see cref="WorldState.ExploredZonesWriteQueue"/>): writes queued on the world thread are persisted off it and in order (one
/// consumer). The latest value of a character is an in-memory overlay; a queued write persists the overlay, and one that fails all
/// <c>MaxAttempts</c> attempts is RETAINED, never dropped: the next change, <see cref="FlushCharacterAsync"/> (the login barrier,
/// which faults while the character is still not durable), <see cref="RequestRetry"/> (logout) and <see cref="StopAsync"/> (a final
/// retry that throws if storage is still failing) all carry it. <see cref="FlushAsync"/> is a pure ordered barrier that never
/// retries and never throws. Retention is in process only. The derived sealed queues supply the store call, the names, the ledger
/// keys and the log lines; the public <see cref="KeyedStoreWriteQueue{TStore}"/> is deliberately not part of this.
/// </summary>
/// <typeparam name="TValue">The overlay value held per character.</typeparam>
public abstract class CharacterKeyedWriteQueue<TValue>
{
    private const int MaxAttempts = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Unsaved> _characters = [];
    private Exception? _terminalFailure;
    private Task? _consumer;
    private Task? _stop;
    private RetainedRetryTimer? _retry;
    private volatile bool _stopped;
    private readonly StopDrainBudget _drain;
    private int _pending;

    private protected CharacterKeyedWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
    {
        _scopes = scopes;
        Logger = logger;
        _drain = drain ?? new StopDrainBudget();
    }

    private protected ILogger Logger { get; }

    /// <summary>Pause between attempts of one write (tests shorten it).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per character, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private enum Kind
    {
        Write,
        Barrier,
        FlushCharacter,
        Retry,
        DrainAll,
        Sweep,
    }

    private sealed class Unsaved
    {
        public TValue? Value;
        public int Version;
        public bool Queued;
        public Exception? Failure;
    }

    private readonly record struct Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Terminal failure that halted the queue (such as <see cref="CharacterOwnershipRejectedException"/>), if any.</summary>
    public Exception? TerminalFailure
    {
        get
        {
            lock (_gate)
            {
                return _terminalFailure;
            }
        }
    }

    /// <summary>Raised once when the queue enters terminal failure (for example on <see cref="CharacterOwnershipRejectedException"/>).</summary>
    public event Action<Exception>? FatalFailure;

    /// <summary>The concrete queue's literal name, as it appears in the anomaly details and subjects (never derived from the runtime type).</summary>
    private protected abstract string QueueName { get; }

    /// <summary>The queue as named in exception messages, such as "rest write queue".</summary>
    private protected abstract string Description { get; }

    /// <summary>What the writes are called in exception messages, such as "rested-state".</summary>
    private protected abstract string WritesNoun { get; }

    /// <summary>The fault-site prefix, such as "writequeue.rest"; the attempt, ownership, stop-host and consume sites and the retained-write log key extend it.</summary>
    private protected abstract string SitePrefix { get; }

    /// <summary>The fault site of an unexpected consumer failure.</summary>
    private protected abstract string ConsumeFaultSite { get; }

    /// <summary>Whether a busy database is left out of the per-attempt fault until the final attempt (the inline retries will usually clear it).</summary>
    private protected abstract bool SkipsBusyAttemptFault { get; }

    /// <summary>True when the value carries nothing to persist (such a character is never written and never marked failed by a terminal failure).</summary>
    private protected abstract bool IsEmpty(TValue? value);

    /// <summary>Persist the value through the store resolved from the scope (nothing happens when no store is registered).</summary>
    private protected abstract Task SaveToStoreAsync(IServiceProvider services, int characterId, TValue? value);

    private protected abstract void LogDroppedAfterStop(ILogger logger, int characterId);

    private protected abstract void LogTerminalFailure(ILogger logger, Exception exception);

    private protected abstract void LogConsumeFault(ILogger logger, Exception exception, object kind, int characterId);

    private protected abstract void LogRetryExhausted(ILogger logger, Exception failure, int characterId, int attempts);

    private protected abstract void LogAttemptFailed(ILogger logger, Exception exception, int characterId, int attempt, int attempts);

    private protected abstract void LogSweepSummary(ILogger logger, int failed, int total, string locked, string reason);

    private protected abstract string SweepInvariantMessage { get; }

    /// <summary>Tests: queue one retained-write sweep now, exactly as a timer tick would; false when one is already pending or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest() => _retry?.TryPost() ?? false;

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _retry = new RetainedRetryTimer(RetainedRetryInterval, HasAnyRetained, () => _channel.Writer.TryWrite(new Work(0, Kind.Sweep, null)));
        _retry.Start();
        _consumer = Task.Run(ConsumeAsync);
    }

    private bool HasAnyRetained()
    {
        lock (_gate)
        {
            return _terminalFailure is null && _characters.Values.Any(u => u.Failure is not null);
        }
    }

    /// <summary>Record the character's current value and queue a write unless one is waiting.</summary>
    private protected void SaveCore(int characterId, TValue value)
    {
        bool droppedStopped = false;
        bool droppedClosed = false;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Value = value;
            unsaved.Version++;
            if (_stopped)
            {
                unsaved.Failure = _terminalFailure ?? new InvalidOperationException($"the {Description} is stopped; the change was not persisted");
                LogDroppedAfterStop(Logger, characterId);
                droppedStopped = true;
            }
            else if (!unsaved.Queued)
            {
                unsaved.Queued = true;
                Interlocked.Increment(ref _pending);
                if (!_channel.Writer.TryWrite(new Work(characterId, Kind.Write, null)))
                {
                    unsaved.Queued = false;
                    Interlocked.Decrement(ref _pending);
                    unsaved.Failure = new InvalidOperationException($"the {Description} is closed; the change was not persisted");
                    droppedClosed = true;
                }
            }
        }

        if (droppedStopped || droppedClosed)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                $"character {characterId}",
                $"queue={QueueName} character={characterId} reason={(droppedStopped ? "queue-stopped" : "queue-closed")}",
                AnomalySeverity.Error);
        }
    }

    /// <summary>Drop anything retained for a deleted character or a reused id (a later write would only be ignored by the store, but never retry it).</summary>
    public void Forget(int characterId)
    {
        lock (_gate)
        {
            _characters.Remove(characterId);
        }
    }

    public bool HasRetainedFailure(int characterId)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Failure is not null;
        }
    }

    /// <summary>Completes once every write queued before the call has been attempted. Never retries, never throws.</summary>
    public Task FlushAsync()
    {
        if (_consumer is null)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(0, Kind.Barrier, done)) ? done.Task : Task.CompletedTask;
    }

    /// <summary>
    /// The login barrier: after every earlier write, retry this character's retained write once more and
    /// fault with <see cref="InvalidOperationException"/> while it is still not durable.
    /// </summary>
    public Task FlushCharacterAsync(int characterId)
    {
        if (_consumer is null || _stopped)
        {
            return RetainedFailureTask(characterId);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(characterId, Kind.FlushCharacter, done)) ? done.Task : RetainedFailureTask(characterId);
    }

    /// <summary>Queue a fire-and-forget retry of a retained write (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null && !_stopped)
        {
            _channel.Writer.TryWrite(new Work(characterId, Kind.Retry, null));
        }
    }

    /// <summary>Stop accepting writes, drain, retry every retained character once; throws naming the characters still not durable. Idempotent.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            return _stop ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        _stopped = true;
        _drain.Begin();
        if (_retry is not null)
        {
            await _retry.StopAsync().ConfigureAwait(false);
        }

        _channel.Writer.TryWrite(new Work(0, Kind.DrainAll, null));
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        int[] failed;
        Exception? first;
        Exception? terminal;
        lock (_gate)
        {
            terminal = _terminalFailure;
            KeyValuePair<int, Unsaved>[] failures = [.. _characters.Where(c => c.Value.Failure is not null).OrderBy(c => c.Key)];
            failed = [.. failures.Select(c => c.Key)];
            first = failures.Length > 0 ? failures[0].Value.Failure : terminal;
        }

        if (failed.Length > 0)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                $"queue {QueueName}",
                $"queue={QueueName} undrainedCharacters={failed.Length} firstCharacter={failed[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record($"{SitePrefix}-stop-drain", refusal);
                Logger.LogFault(
                    $"{SitePrefix.Replace('.', '-')}-stop-drain",
                    refusal,
                    "{Queue} shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    QueueName,
                    _drain.Refused,
                    failed.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"{WritesNoun} writes did not drain for characters {string.Join(", ", failed)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException($"{Description} stopped after terminal ownership rejection", terminal);
        }
    }

    private Unsaved Entry(int characterId)
    {
        if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
        {
            _characters[characterId] = unsaved = new Unsaved();
        }

        return unsaved;
    }

    private Exception? RetainedFailureOf(int characterId)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Failure is not null
                ? unsaved.Failure
                : _terminalFailure;
        }
    }

    private Task RetainedFailureTask(int characterId)
        => RetainedFailureOf(characterId) is { } failure ? Task.FromException(NotDurable(characterId, failure)) : Task.CompletedTask;

    private InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"{WritesNoun} writes for character {characterId} are not durable", failure);

    private void FailTerminal(CharacterOwnershipRejectedException ex, IServiceProvider? scopedServices)
    {
        bool first = false;
        lock (_gate)
        {
            if (_terminalFailure is null)
            {
                _terminalFailure = ex;
                _stopped = true;
                first = true;
            }
        }

        if (!first)
        {
            return;
        }

        AnomalyLedger.Shared.Report(
            "persist",
            "circuit-open",
            $"queue {QueueName}",
            $"queue={QueueName} reason=terminal-ownership-failure error={ex.GetType().Name}",
            AnomalySeverity.Warning);
        LogTerminalFailure(Logger, ex);
        try
        {
            scopedServices?.GetService<IHostApplicationLifetime>()?.StopApplication();
        }
        catch (OperationCanceledException)
        {
            /* expected on shutdown */
        }
        catch (Exception stopEx) when (stopEx is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record($"{SitePrefix}-stop-host", stopEx);
        }

        FatalFailure?.Invoke(ex);
    }

    private async Task ConsumeAsync()
    {
        await foreach (Work work in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (work.Kind)
                {
                    case Kind.Write:
                        try
                        {
                            await ExecuteAsync(work.CharacterId, onlyIfFailed: false, clearQueued: true).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _pending);
                        }

                        break;
                    case Kind.Retry:
                        await ExecuteAsync(work.CharacterId, onlyIfFailed: true, clearQueued: false).ConfigureAwait(false);
                        break;
                    case Kind.FlushCharacter:
                        await ExecuteAsync(work.CharacterId, onlyIfFailed: true, clearQueued: false).ConfigureAwait(false);
                        if (RetainedFailureOf(work.CharacterId) is { } failure)
                        {
                            work.Done?.TrySetException(NotDurable(work.CharacterId, failure));
                        }
                        else
                        {
                            work.Done?.TrySetResult();
                        }

                        break;
                    case Kind.DrainAll:
                        int[] retained;
                        lock (_gate)
                        {
                            retained = [.. _characters.Where(c => c.Value.Failure is not null).Select(c => c.Key).Order()];
                        }

                        foreach (int id in retained)
                        {
                            await ExecuteAsync(id, onlyIfFailed: true, clearQueued: false).ConfigureAwait(false);
                        }

                        break;
                    case Kind.Sweep:
                        try
                        {
                            if (!_stopped)
                            {
                                await SweepRetainedAsync().ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _retry?.SweepFinished();
                        }

                        break;
                    default:
                        work.Done?.TrySetResult();
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                /* expected on shutdown */
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record(ConsumeFaultSite, ex);
                LogConsumeFault(Logger, ex, work.Kind, work.CharacterId);
                work.Done?.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// On the consumer: one attempt for each retained character, ascending, so a lock that outlasts the inline retries does not leave the write held
    /// in memory with nobody to retry it. Stops at the first locked database (the rest would only wait out the same lock); any other failure does not stop it.
    /// </summary>
    private async Task SweepRetainedAsync()
    {
        int[] retained;
        lock (_gate)
        {
            retained = [.. _characters.Where(c => c.Value.Failure is not null).Select(c => c.Key).Order()];
        }

        int failed = 0;
        bool locked = false;
        Exception? first = null;
        for (int i = 0; i < retained.Length; i++)
        {
            Exception? failure = await ExecuteAsync(retained[i], onlyIfFailed: true, clearQueued: false, sweep: true).ConfigureAwait(false);
            if (failure is null)
            {
                continue;
            }

            failed++;
            first ??= failure;
            if (DatabaseTransience.IsBusy(failure))
            {
                locked = true;
                break;
            }
        }

        if (failed > 0 && Invariant.Check(first is not null, SweepInvariantMessage) && first is { } firstFailure)
        {
            LogSweepSummary(Logger, failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", firstFailure.Message);
        }
    }

    private async Task<Exception?> ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued, bool sweep = false)
    {
        TValue? value;
        int version;
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return null;
            }

            if (clearQueued)
            {
                unsaved.Queued = false;
            }

            if (_terminalFailure is { } terminal)
            {
                if (!IsEmpty(unsaved.Value))
                {
                    unsaved.Failure ??= terminal;
                }

                return terminal;
            }

            if (onlyIfFailed && unsaved.Failure is null)
            {
                return null;
            }

            if (IsEmpty(unsaved.Value))
            {
                return null;
            }

            value = unsaved.Value;
            version = unsaved.Version;
        }

        int attempts = sweep || _stopped ? 1 : MaxAttempts; // at stop: one attempt per write, busy or not, no inline retry
        int attemptsMade = 0;
        bool refused = false;
        Exception? failure = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt == 1 && (_stopped || _drain.IsSuppressed) && _drain.Refuse() is { } exhausted)
            {
                failure = exhausted; // shutdown drain only: nothing is attempted, the write stays retained
                refused = true;
                break;
            }

            attemptsMade = attempt;
            using IServiceScope scope = _scopes.CreateScope();
            try
            {
                using StopDrainBudget.Attempt progress = _drain.BeginAttempt(); // the attempt is the store call: its verdict is in before the catch's backoff
                await SaveToStoreAsync(scope.ServiceProvider, characterId, value).ConfigureAwait(false);
                progress.Succeeded();
                failure = null;
                break;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                failure = ex;
                FaultLedger.Shared.Record($"{SitePrefix}-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
                if (!(SkipsBusyAttemptFault && attempt < attempts && DatabaseTransience.IsBusy(ex)))
                {
                    FaultLedger.Shared.Record($"{SitePrefix}-attempt", ex);
                }

                if (!sweep)
                {
                    LogAttemptFailed(Logger, ex, characterId, attempt, attempts);
                }

                if (attempt < attempts)
                {
                    if (_stopped && _drain.Exhausted)
                    {
                        break; // the lane has stalled: no retry, the write stays retained with its real failure
                    }

                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        bool newEpisode;
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return null;
            }

            newEpisode = failure is not null && unsaved.Failure is null; // a retained write failing again is the same episode
            unsaved.Failure = failure;
            if (failure is null && unsaved.Version == version && !unsaved.Queued)
            {
                _characters.Remove(characterId); // durable and unchanged since: nothing to retain
            }
        }

        if (failure is not null && !sweep && !refused)
        {
            if (newEpisode && failure is not CharacterOwnershipRejectedException)
            {
                AnomalyLedger.Shared.Report(
                    "persist",
                    "write-retry-exhausted",
                    $"character {characterId}",
                    $"queue={QueueName} character={characterId} attempts={attemptsMade} error={failure.GetType().Name}",
                    AnomalySeverity.Error);
            }

            LogRetryExhausted(Logger, failure, characterId, attemptsMade);
        }

        return failure;
    }
}
