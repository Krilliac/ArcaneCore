using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Persists taxi-route saves and deletes requested on the world thread, off it and in order (one consumer, as
/// <see cref="Reputation.ReputationWriteQueue"/>), and keeps them until they are durable.
/// <para>
/// Each character has at most one outstanding operation, a save of a route or a delete; a newer request replaces an older
/// one (both are idempotent, so the last request wins and the older is never applied after it). The operation is removed
/// after a write only if no newer request replaced it meanwhile. A write that fails all <c>MaxAttempts</c> attempts is
/// <b>retained</b>, never dropped, and carried by the next trigger for the character: a newer request,
/// <see cref="FlushCharacterAsync"/> (the login barrier, which throws while the character is still unrecovered),
/// <see cref="RequestRetry"/> (logout) and <see cref="StopAsync"/> (a final retry that throws, naming the characters).
/// </para>
/// <para>
/// A save may carry an <c>abandon</c> fallback (<see cref="Save(int, TaxiFlightRoute, Action?)"/>). When such a save
/// fails every attempt and no newer request replaced it, the route is given up instead of retained: the fallback runs
/// (the owner puts the character somewhere it can stand) and the operation becomes a delete of the row, which is itself
/// retained until durable. This keeps the character row and the route row consistent on disk, the guarantee the
/// synchronous save had, while the write still leaves the world thread.
/// </para>
/// <para>
/// <see cref="FlushAsync"/> stays a pure ordered barrier: it never retries and never throws. Retention is in process
/// only: a crash, or a store that is still down at exit, loses what is retained (loudly).
/// </para>
/// </summary>
public sealed class TaxiFlightWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
{
    private const int MaxAttempts = 3;

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per character, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Unsaved> _characters = [];
    private Exception? _terminalFailure;
    private Task? _consumer;
    private Task? _stop;
    private RetainedRetryTimer? _retry;
    private volatile bool _stopped;
    private readonly StopDrainBudget _drain = drain ?? new StopDrainBudget();
    private int _pending;

    private enum Kind
    {
        Write,
        Barrier,
        FlushCharacter,
        Retry,
        DrainAll,
        Sweep,
    }

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Ids of characters whose last write failed and is still retained, ascending.</summary>
    public IReadOnlyList<int> RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                return [.. _characters.Where(c => c.Value.Failure is not null).Select(c => c.Key).Order()];
            }
        }
    }

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
            return _terminalFailure is null && _characters.Values.Any(c => c.Failure is not null);
        }
    }

    /// <summary>Queue saving the route of a character that logged out mid-flight; replaces any older request.</summary>
    public void Save(int characterId, TaxiFlightRoute route) => Save(characterId, route, abandon: null);

    /// <summary>
    /// Queue saving the route of a character that logged out mid-flight; replaces any older request. When the save
    /// fails every attempt (and was not replaced meanwhile), <paramref name="abandon"/> runs off the world thread and
    /// the route is deleted instead of retained; a null <paramref name="abandon"/> retains the failed save.
    /// </summary>
    public void Save(int characterId, TaxiFlightRoute route, Action? abandon)
    {
        ArgumentNullException.ThrowIfNull(route);
        Request(characterId, route, abandon);
    }

    /// <summary>Queue removing the character's saved route; replaces any older request.</summary>
    public void Delete(int characterId) => Request(characterId, null, null);

    /// <summary>True while the character's last write failed all attempts and is still retained.</summary>
    public bool HasRetainedFailure(int characterId)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Failure is not null;
        }
    }

    /// <summary>
    /// Completes once every write queued before the call has been attempted. A pure ordered
    /// barrier: it never retries retained failures and never throws (see <see cref="FlushCharacterAsync"/>).
    /// </summary>
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
    /// The login barrier. After every earlier write, retries this character's retained write once
    /// more and faults with <see cref="InvalidOperationException"/> while it is still not durable,
    /// so storage is never read under an unrecovered failure.
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

    /// <summary>Queue a fire-and-forget retry of the character's retained write (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null && !_stopped)
        {
            _channel.Writer.TryWrite(new Work(characterId, Kind.Retry, null));
        }
    }

    /// <summary>
    /// Stop accepting writes, drain the queue and retry every retained character once. Throws
    /// <see cref="InvalidOperationException"/> naming the characters whose writes are still not
    /// durable. Idempotent: later calls return the same outcome.
    /// </summary>
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
                "queue TaxiFlightWriteQueue",
                $"queue=TaxiFlightWriteQueue undrainedCharacters={failed.Length} firstCharacter={failed[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.taxi-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-taxi-stop-drain",
                    refusal,
                    "taxi route write queue shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    _drain.Refused,
                    failed.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"taxi route writes did not drain for characters {string.Join(", ", failed)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException("taxi route write queue stopped after terminal ownership rejection", terminal);
        }
    }

    /// <summary>A null <paramref name="route"/> is a delete.</summary>
    private void Request(int characterId, TaxiFlightRoute? route, Action? abandon)
    {
        bool droppedStopped = false;
        bool droppedClosed = false;
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                _characters[characterId] = unsaved = new Unsaved();
            }

            unsaved.Operation = new Operation(route, ++unsaved.Generation, abandon);
            unsaved.Failure = null; // the new request supersedes the failed one and is itself attempted
            if (_stopped)
            {
                unsaved.Failure = _terminalFailure ?? new InvalidOperationException("the taxi route write queue is stopped; the change was not persisted");
                logger.LogError("taxi route change for character {Character} arrived after the queue stopped; not persisted", characterId);
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
                    unsaved.Failure = new InvalidOperationException("the taxi route write queue is closed; the change was not persisted");
                    logger.LogError("taxi route change for character {Character} arrived after the queue closed; not persisted", characterId);
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
                $"queue=TaxiFlightWriteQueue character={characterId} reason={(droppedStopped ? "queue-stopped" : "queue-closed")}",
                AnomalySeverity.Error);
        }
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

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"taxi route writes for character {characterId} are not durable", failure);

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
            "queue TaxiFlightWriteQueue",
            $"queue=TaxiFlightWriteQueue reason=terminal-ownership-failure error={ex.GetType().Name}",
            AnomalySeverity.Warning);
        logger.LogCritical(ex, "taxi route write queue entered terminal failure after character ownership rejection");
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
            FaultLedger.Shared.Record("writequeue.taxi-stop-host", stopEx);
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
                // ExecuteAsync reports store failures itself; this is a queue bug and must not kill the consumer.
                logger.LogFault("writequeue.taxi-consume", ex, "taxi route write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
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

        if (failed > 0 && Invariant.Check(first is not null, "taxi route retry recorded failures without a first exception") && first is { } firstFailure)
        {
            logger.LogWarning("taxi route retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", firstFailure.Message);
        }
    }

    /// <summary>Persist the character's outstanding operation (nothing if there is none, or it is not failed and <paramref name="onlyIfFailed"/>).</summary>
    private async Task<Exception?> ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued, bool sweep = false)
    {
        Operation operation;
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
                if (unsaved.Operation is not null)
                {
                    unsaved.Failure ??= terminal;
                }

                return terminal;
            }

            if ((onlyIfFailed && unsaved.Failure is null) || unsaved.Operation is not { } current)
            {
                Release(characterId, unsaved);
                return null;
            }

            operation = current;
        }

        while (true)
        {
            var made = new WriteAttemptCount();
            try
            {
                await WriteAsync(characterId, operation, sweep || _stopped ? 1 : MaxAttempts, made).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Operation? replacement = null;
                Action? abandonAction = operation.Abandon;
                bool newEpisode = true;
                lock (_gate)
                {
                    // Only the request that failed is retained; a newer one replaced it and is queued itself.
                    if (_characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Operation?.Generation == operation.Generation)
                    {
                        if (!sweep && abandonAction is not null && !StopDrainBudget.IsNoVerdict(ex))
                        {
                            // Give the route up rather than keep it: the owner relocates the character and the row goes. Only on a failure a real store attempt returned
                            // and that says something about the route: not when the database is merely locked by another writer, and not when the stop drain refused the
                            // attempt (nothing was tried; abandoning then would write a departure-node snapshot over the logout snapshot). A retry sweep never abandons.
                            replacement = unsaved.Operation = new Operation(null, ++unsaved.Generation, null);
                        }
                        else
                        {
                            newEpisode = unsaved.Failure is null; // a retained write failing again is the same episode
                            unsaved.Failure = ex;
                        }
                    }
                }

                if (replacement is null || !Invariant.Check(abandonAction is not null, "taxi route abandonment selected without an abandon callback") || abandonAction is not { } validAbandon)
                {
                    if (ex is StopDrainExhaustedException)
                    {
                        return ex; // refused by the stop drain: nothing was attempted, so no anomaly or fault here; the queue reports the refusal once, and the write stays retained
                    }

                    if (!sweep)
                    {
                        if (newEpisode && ex is not CharacterOwnershipRejectedException)
                        {
                            AnomalyLedger.Shared.Report(
                                "persist",
                                "write-retry-exhausted",
                                $"character {characterId}",
                                $"queue=TaxiFlightWriteQueue character={characterId} action={(operation.Route is null ? "delete" : "save")} attempts={made.Made} error={ex.GetType().Name}",
                                AnomalySeverity.Error);
                        }

                        logger.LogFault("writequeue.taxi", ex, "taxi route {Action} for character {Character} failed; retained for retry", operation.Route is null ? "delete" : "save", characterId);
                    }
                    else
                    {
                        FaultLedger.Shared.Record("writequeue.taxi-sweep", ex);
                    }

                    return ex;
                }

                AnomalyLedger.Shared.Report(
                    "persist",
                    "write-retry-exhausted",
                    $"character {characterId}",
                    $"queue=TaxiFlightWriteQueue character={characterId} action=save-abandoned attempts={made.Made} error={ex.GetType().Name}",
                    AnomalySeverity.Error);
                logger.LogFault("writequeue.taxi", ex, "taxi route save for character {Character} failed; the route is abandoned and its row deleted", characterId);
                Abandon(characterId, validAbandon);
                operation = replacement;
                continue;
            }

            lock (_gate)
            {
                if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    return null;
                }

                if (unsaved.Operation?.Generation == operation.Generation)
                {
                    unsaved.Operation = null;
                    unsaved.Failure = null;
                }

                Release(characterId, unsaved);
            }

            return null;
        }
    }

    private void Abandon(int characterId, Action abandon)
    {
        try
        {
            abandon();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogFault("writequeue.taxi-abandon", ex, "taxi route fallback for character {Character} failed; the route row is still deleted", characterId);
        }
    }

    /// <summary>Under <see cref="_gate"/>: forget an entry with nothing left to write or wait for.</summary>
    private void Release(int characterId, Unsaved unsaved)
    {
        if (unsaved.Operation is null && !unsaved.Queued && unsaved.Failure is null)
        {
            _characters.Remove(characterId);
        }
    }

    private async Task WriteAsync(int characterId, Operation operation, int attempts, WriteAttemptCount made)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (attempt == 1 && (_stopped || _drain.IsSuppressed) && _drain.Refuse() is { } refused)
            {
                throw refused; // retained by the caller; never a verdict (see StopDrainBudget.IsNoVerdict)
            }

            made.Made = attempt;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                using StopDrainBudget.Attempt progress = _drain.BeginAttempt(); // the attempt is the store call: its verdict is in before the catch's backoff
                if (scope.ServiceProvider.GetService<ICharacterTaxiFlightStore>() is not { } store)
                {
                    progress.Succeeded(); // no store: nothing to land, and not a stall
                    return; // hosts without persistence keep nothing
                }

                // Both calls are idempotent, so a retry after a commit whose acknowledgement was lost is safe.
                if (operation.Route is { } route)
                {
                    await store.SaveAsync(characterId, route).ConfigureAwait(false);
                }
                else
                {
                    await store.DeleteAsync(characterId).ConfigureAwait(false);
                }

                progress.Succeeded();
                return;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                FaultLedger.Shared.Record("writequeue.taxi-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
                throw;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.taxi-attempt", ex);
                logger.LogWarning(ex, "taxi route write for character {Character} failed (attempt {Attempt}); retrying", characterId, attempt);
                if (_stopped && _drain.Exhausted)
                {
                    throw; // the lane has stalled: no retry, the caller retains the real failure
                }

                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private sealed record Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    /// <summary>A null <paramref name="Route"/> is a delete; <paramref name="Abandon"/> is the save's fallback (see the class summary).</summary>
    private sealed record Operation(TaxiFlightRoute? Route, long Generation, Action? Abandon);

    /// <summary>One character's not-yet-durable state. Every member is guarded by the queue's gate.</summary>
    private sealed class Unsaved
    {
        public Operation? Operation { get; set; }
        public long Generation { get; set; }
        public bool Queued { get; set; }
        public Exception? Failure { get; set; }
    }
}
