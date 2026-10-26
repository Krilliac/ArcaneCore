using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Honor;

/// <summary>
/// Persists honor writes made on the world thread, off it and in order (one consumer, like
/// <see cref="Reputation.ReputationWriteQueue"/>), and keeps them until they are durable.
/// <para>
/// Every change is merged into a per-character overlay: contribution rows to append (in order), the latest state
/// (last value wins) and generation counters for a reset and a deletion. A queued write persists the whole overlay in
/// the order reset or delete first, then state, then rows; what it wrote is removed afterwards only if it was not
/// changed meanwhile. A write that fails all <c>MaxAttempts</c> attempts is therefore <b>retained</b>, never dropped, and
/// the next trigger for that character carries it: the next change, <see cref="FlushCharacterAsync"/> (the login
/// barrier, which throws while the character is still unrecovered), <see cref="RequestRetry"/> (logout) and
/// <see cref="StopAsync"/> (a final retry that throws, naming the characters, if storage is still failing).
/// </para>
/// <para>
/// Appending rows is not idempotent: if a commit succeeds but its acknowledgement is lost, the retry appends the batch
/// again. The batch is one transaction, so the case needs a connection that dies after the commit; it is documented as a
/// limit (docs/areas/honor.md). <see cref="FlushAsync"/> stays a pure ordered barrier: it never retries and never throws.
/// </para>
/// </summary>
public sealed class HonorWriteQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
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

    /// <summary>Queue one contribution row.</summary>
    public void AppendCp(int characterId, HonorCpRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Add(row);
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>Queue the character's new state (replaces a state still waiting).</summary>
    public void SaveState(int characterId, CharacterHonorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.State = state;
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>Queue a reset (HonorMgr::Reset): rows and numbers go, and anything still waiting for the character is discarded.</summary>
    public void Reset(int characterId)
    {
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Clear();
            unsaved.State = null;
            unsaved.Failure = null;
            unsaved.ResetRequested++;
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>
    /// Queue removal of a deleted character's rows (conditional on the id still having no character row). Anything retained
    /// for the character is discarded first, so a failed older write can never bring a row back.
    /// </summary>
    public void DeleteCharacter(int characterId)
    {
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Clear();
            unsaved.State = null;
            unsaved.Failure = null;
            unsaved.DeleteRequested++;
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>Drop anything retained for an id about to be reused (the creation hook drained the queue and removes the rows directly).</summary>
    public void ForgetCharacter(int characterId)
    {
        lock (_gate)
        {
            _characters.Remove(characterId);
        }
    }

    /// <summary>True while the character's last write failed all attempts and is still retained.</summary>
    public bool HasRetainedFailure(int characterId)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Failure is not null;
        }
    }

    /// <summary>Completes once every write queued before the call has been attempted. Never retries and never throws.</summary>
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
    /// The login barrier: after every earlier write, retries this character's retained writes once more and faults with
    /// <see cref="InvalidOperationException"/> while they are still not durable, so storage is never read under an
    /// unrecovered failure.
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

    /// <summary>Queue a fire-and-forget retry of the character's retained writes (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null && !_stopped)
        {
            _channel.Writer.TryWrite(new Work(characterId, Kind.Retry, null));
        }
    }

    /// <summary>
    /// Stop accepting writes, drain the queue and retry every retained character once. Throws
    /// <see cref="InvalidOperationException"/> naming the characters whose writes are still not durable. Idempotent.
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
                "queue HonorWriteQueue",
                $"queue=HonorWriteQueue undrainedCharacters={failed.Length} firstCharacter={failed[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.honor-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-honor-stop-drain",
                    refusal,
                    "honor write queue shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    _drain.Refused,
                    failed.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"honor writes did not drain for characters {string.Join(", ", failed)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException("honor write queue stopped after terminal ownership rejection", terminal);
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

    /// <summary>Under <see cref="_gate"/>: a change was merged; queue one write unless one is already waiting.</summary>
    private string? Changed(int characterId, Unsaved unsaved)
    {
        if (_stopped)
        {
            unsaved.Failure = _terminalFailure ?? new InvalidOperationException("the honor write queue is stopped; the change was not persisted");
            logger.LogError("honor change for character {Character} arrived after the queue stopped; not persisted", characterId);
            return "queue-stopped";
        }

        if (unsaved.Queued)
        {
            return null;
        }

        unsaved.Queued = true;
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Work(characterId, Kind.Write, null)))
        {
            unsaved.Queued = false;
            Interlocked.Decrement(ref _pending);
            unsaved.Failure = new InvalidOperationException("the honor write queue is closed; the change was not persisted");
            logger.LogError("honor change for character {Character} arrived after the queue closed; not persisted", characterId);
            return "queue-closed";
        }

        return null;
    }

    /// <summary>Report a change refused by <see cref="Changed"/>; called after the gate is released.</summary>
    private static void ReportDropped(int characterId, string? reason)
    {
        if (reason is null)
        {
            return;
        }

        AnomalyLedger.Shared.Report(
            "persist",
            "write-dropped-on-shutdown",
            $"character {characterId}",
            $"queue=HonorWriteQueue character={characterId} reason={reason}",
            AnomalySeverity.Error);
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
        => new($"honor writes for character {characterId} are not durable", failure);

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
            "queue HonorWriteQueue",
            $"queue=HonorWriteQueue reason=terminal-ownership-failure error={ex.GetType().Name}",
            AnomalySeverity.Warning);
        logger.LogCritical(ex, "honor write queue entered terminal failure after character ownership rejection");
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
            FaultLedger.Shared.Record("writequeue.honor-stop-host", stopEx);
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
                FaultLedger.Shared.Record("persist.honor-write-queue", ex);
                logger.LogFault("writequeue.honor-consume", ex, "honor write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
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

        if (failed > 0 && Invariant.Check(first is not null, "honor retry recorded failures without a first exception") && first is { } firstFailure)
        {
            logger.LogWarning("honor retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", firstFailure.Message);
        }
    }

    /// <summary>Persist the character's overlay (nothing if there is none, or it is not failed and <paramref name="onlyIfFailed"/>).</summary>
    private async Task<Exception?> ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued, bool sweep = false)
    {
        Snapshot snapshot;
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
                if (!unsaved.IsEmpty)
                {
                    unsaved.Failure ??= terminal;
                }

                return terminal;
            }

            if ((onlyIfFailed && unsaved.Failure is null) || unsaved.IsEmpty)
            {
                Release(characterId, unsaved);
                return null;
            }

            snapshot = new Snapshot(
                unsaved.DeleteRequested > unsaved.DeleteDone ? unsaved.DeleteRequested : null,
                unsaved.ResetRequested > unsaved.ResetDone ? unsaved.ResetRequested : null,
                unsaved.State,
                [.. unsaved.Cp],
                unsaved.ResetRequested,
                unsaved.DeleteRequested);
        }

        var made = new WriteAttemptCount();
        try
        {
            await WriteAsync(characterId, snapshot, sweep || _stopped ? 1 : MaxAttempts, made).ConfigureAwait(false);
        }
        catch (StopDrainExhaustedException refused)
        {
            // The stop drain refused the attempt: nothing was tried, so no per-write anomaly or fault; the queue reports the refusal once. The write stays retained.
            lock (_gate)
            {
                if (_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    unsaved.Failure = refused;
                }
            }

            return refused;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("persist.honor-write-queue", ex);
            bool newEpisode = true;
            lock (_gate)
            {
                // Never recreate an entry that a delete or a creation removed while the write was in flight.
                if (_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    newEpisode = unsaved.Failure is null; // a retained write failing again is the same episode
                    unsaved.Failure = ex;
                }
            }

            if (!sweep)
            {
                if (newEpisode && ex is not CharacterOwnershipRejectedException)
                {
                    AnomalyLedger.Shared.Report(
                        "persist",
                        "write-retry-exhausted",
                        $"character {characterId}",
                        $"queue=HonorWriteQueue character={characterId} attempts={made.Made} error={ex.GetType().Name}",
                        AnomalySeverity.Error);
                }

                logger.LogFault("writequeue.honor", ex, "honor write for character {Character} failed; retained for retry", characterId);
            }
            else
            {
                FaultLedger.Shared.Record("writequeue.honor-sweep", ex);
            }

            return ex;
        }

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return null;
            }

            if (snapshot.DeleteGeneration is { } deleted && unsaved.DeleteDone < deleted)
            {
                unsaved.DeleteDone = deleted;
            }

            if (snapshot.ResetGeneration is { } reset && unsaved.ResetDone < reset)
            {
                unsaved.ResetDone = reset;
            }

            // A reset or delete requested while the write was in flight cleared the overlay itself: what is left is newer.
            bool clearedMeanwhile = unsaved.ResetRequested != snapshot.ResetRequestedAt || unsaved.DeleteRequested != snapshot.DeleteRequestedAt;
            if (!clearedMeanwhile)
            {
                unsaved.Cp.RemoveRange(0, Math.Min(snapshot.Cp.Length, unsaved.Cp.Count));
                if (snapshot.State is { } state && unsaved.State == state)
                {
                    unsaved.State = null;
                }
            }

            unsaved.Failure = null;
            Release(characterId, unsaved);
        }

        return null;
    }

    /// <summary>Under <see cref="_gate"/>: forget an entry with nothing left to write or wait for.</summary>
    private void Release(int characterId, Unsaved unsaved)
    {
        if (unsaved.IsEmpty && !unsaved.Queued && unsaved.Failure is null)
        {
            _characters.Remove(characterId);
        }
    }

    private async Task WriteAsync(int characterId, Snapshot snapshot, int attempts, WriteAttemptCount made)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (_stopped && _drain.Refuse() is { } refused)
            {
                throw refused; // retained by the caller; never a verdict (see StopDrainBudget)
            }

            made.Made = attempt;
            using StopDrainBudget.Attempt progress = _drain.BeginAttempt();
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                if (scope.ServiceProvider.GetService<IHonorStore>() is not { } store)
                {
                    progress.Succeeded(); // no store: nothing to land, and not a stall
                    return; // hosts without persistence keep nothing
                }

                if (snapshot.DeleteGeneration is not null)
                {
                    await store.DeleteDeletedCharacterAsync(characterId).ConfigureAwait(false); // conditional: skipped while the id has a character row again
                }

                if (snapshot.ResetGeneration is not null)
                {
                    await store.ResetAsync(characterId).ConfigureAwait(false);
                }

                if (snapshot.State is { } state)
                {
                    await store.SaveStateAsync(characterId, state).ConfigureAwait(false);
                }

                if (snapshot.Cp.Length > 0)
                {
                    await store.AppendCpAsync(characterId, snapshot.Cp).ConfigureAwait(false);
                }

                progress.Succeeded();
                return;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                FaultLedger.Shared.Record("writequeue.honor-ownership", ex);
                FailTerminal(ex, scope.ServiceProvider);
                throw;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.honor-attempt", ex);
                logger.LogWarning(ex, "honor write for character {Character} failed (attempt {Attempt}); retrying", characterId, attempt);
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private sealed record Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    private sealed record Snapshot(
        long? DeleteGeneration, long? ResetGeneration, CharacterHonorState? State, HonorCpRecord[] Cp, long ResetRequestedAt, long DeleteRequestedAt);

    /// <summary>One character's not-yet-durable state. Every member is guarded by the queue's gate.</summary>
    private sealed class Unsaved
    {
        public List<HonorCpRecord> Cp { get; } = [];
        public CharacterHonorState? State { get; set; }
        public long DeleteRequested { get; set; }
        public long DeleteDone { get; set; }
        public long ResetRequested { get; set; }
        public long ResetDone { get; set; }
        public bool Queued { get; set; }
        public Exception? Failure { get; set; }

        public bool IsEmpty => Cp.Count == 0 && State is null && DeleteRequested <= DeleteDone && ResetRequested <= ResetDone;
    }
}
