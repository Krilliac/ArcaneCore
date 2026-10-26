using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Persists reputation writes made on the world thread, off it and in order (one consumer, as
/// <see cref="Social.SocialWriteQueue"/>), and keeps them until they are durable.
/// <para>
/// Every change is merged into an in-memory per-character overlay (absolute rows, last value
/// wins, plus the watched slot and a delete generation). A queued write persists the whole
/// overlay; the rows it wrote are removed afterwards only if they were not changed meanwhile. A
/// write that fails all <c>MaxAttempts</c> attempts is therefore <b>retained</b>, never dropped, and
/// the next trigger for that character carries it: the next change (it writes the whole overlay),
/// <see cref="FlushCharacterAsync"/> (the login barrier, which throws while the character is still
/// unrecovered), <see cref="RequestRetry"/> (logout) and <see cref="StopAsync"/> (a final retry that
/// throws, naming the characters, if storage is still failing).
/// </para>
/// <para>
/// <see cref="FlushAsync"/> stays a pure ordered barrier: it never retries and never throws, so one
/// character's persistent failure cannot stall unrelated creations or deletions. Retention is in
/// process only: a crash, or a store that is still down at exit, loses what is retained (loudly).
/// </para>
/// </summary>
public sealed class ReputationWriteQueue(IServiceScopeFactory scopes, ILogger logger, IHostApplicationLifetime? lifetime = null, StopDrainBudget? drain = null)
{
    private const int MaxAttempts = 3;

    /// <summary>Raised when a durable ownership fence rejects a reputation write.</summary>
    public event Action<Exception>? FatalFailure;

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per character, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Unsaved> _characters = [];
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

    /// <summary>Ids of characters whose last write failed and is still retained, ascending (includes ones rejected by the ownership fence, which are never retried).</summary>
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

    /// <summary>Ids whose retained write may succeed on a later attempt: failed, and not rejected by the ownership fence (a stale owner stays stale), ascending.</summary>
    private int[] RetryableCharacters()
    {
        lock (_gate)
        {
            return [.. _characters.Where(c => c.Value.HasRetryableFailure).Select(c => c.Key).Order()];
        }
    }

    /// <summary>Tests: queue one retained-write sweep now, exactly as a timer tick would; false when one is already pending or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest() => _retry?.TryPost() ?? false;

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _retry = new RetainedRetryTimer(RetainedRetryInterval, () => RetryableCharacters().Length > 0, () => _channel.Writer.TryWrite(new Work(0, Kind.Sweep, null)));
        _retry.Start();
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Merge changed faction rows. <paramref name="ownership"/> is the live character's ownership token (null with fencing off); the overlay
    /// captures it at its first mutation, never coalesces across different tokens, and every write of that overlay carries the captured token.
    /// </summary>
    public void SaveFactions(int characterId, IReadOnlyList<CharacterReputationRow> rows, CharacterOwnershipToken? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            Overlay overlay = unsaved.Target(ownership);
            foreach (CharacterReputationRow row in rows)
            {
                overlay.Rows[row.Faction] = row;
            }

            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>Merge the watched slot; the ownership token is captured and isolated per token exactly as <see cref="SaveFactions"/> does.</summary>
    public void SaveWatchedFaction(int characterId, int watched, CharacterOwnershipToken? ownership = null)
    {
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            Overlay overlay = unsaved.Target(ownership);
            overlay.Watched = watched;
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>
    /// Queue removal of a deleted character's rows (conditional on the id still having no character row). Anything retained for the character is
    /// discarded first, so a failed older write can never bring a row back.
    /// </summary>
    public void DeleteCharacter(int characterId)
    {
        string? dropped;
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Overlays.Clear(); // the removal is token-independent, so an earlier ownership rejection or retained write must not block it
            unsaved.DeleteFailure = null;
            unsaved.DeleteRequested++;
            dropped = Changed(characterId, unsaved);
        }

        ReportDropped(characterId, dropped);
    }

    /// <summary>
    /// Drop anything retained for an id about to be reused: the creation hook has just drained the
    /// queue and removes the old rows directly.
    /// </summary>
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
    /// The login barrier. After every earlier write, retries this character's retained writes once
    /// more and faults with <see cref="InvalidOperationException"/> while they are still not durable,
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

    /// <summary>Queue a fire-and-forget retry of the character's retained writes (logout).</summary>
    public void RequestRetry(int characterId)
    {
        if (_consumer is not null)
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
        lock (_gate)
        {
            KeyValuePair<int, Unsaved>[] failures = [.. _characters.Where(c => c.Value.Failure is not null).OrderBy(c => c.Key)];
            failed = [.. failures.Select(c => c.Key)];
            first = failures.Length > 0 ? failures[0].Value.Failure : null;
        }

        if (failed.Length > 0)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                "queue ReputationWriteQueue",
                $"queue=ReputationWriteQueue undrainedCharacters={failed.Length} firstCharacter={failed[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.reputation-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-reputation-stop-drain",
                    refusal,
                    "reputation write queue shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    _drain.Refused,
                    failed.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"reputation writes did not drain for characters {string.Join(", ", failed)}", first);
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
            unsaved.RecordQueueFailure(new InvalidOperationException("the reputation write queue is stopped; the change was not persisted"));
            logger.LogError("reputation change for character {Character} arrived after the queue stopped; not persisted", characterId);
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
            unsaved.RecordQueueFailure(new InvalidOperationException("the reputation write queue is closed; the change was not persisted"));
            logger.LogError("reputation change for character {Character} arrived after the queue closed; not persisted", characterId);
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
            $"queue=ReputationWriteQueue character={characterId} reason={reason}",
            AnomalySeverity.Error);
    }

    private Task RetainedFailureTask(int characterId)
    {
        Exception? failure;
        lock (_gate)
        {
            failure = _characters.TryGetValue(characterId, out Unsaved? unsaved) ? unsaved.Failure : null;
        }

        return failure is null ? Task.CompletedTask : Task.FromException(NotDurable(characterId, failure));
    }

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"reputation writes for character {characterId} are not durable", failure);

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
                        Exception? failure;
                        lock (_gate)
                        {
                            failure = _characters.TryGetValue(work.CharacterId, out Unsaved? unsaved) ? unsaved.Failure : null;
                        }

                        if (failure is null)
                        {
                            work.Done?.TrySetResult();
                        }
                        else
                        {
                            work.Done?.TrySetException(NotDurable(work.CharacterId, failure));
                        }

                        break;
                    case Kind.DrainAll:
                        foreach (int id in RetryableCharacters())
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
                logger.LogFault("writequeue.reputation-consume", ex, "reputation write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
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
        int[] retained = RetryableCharacters();
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

        if (failed > 0 && Invariant.Check(first is not null, "reputation retry recorded failures without a first exception") && first is { } firstFailure)
        {
            logger.LogWarning("reputation retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", firstFailure.Message);
        }
    }

    /// <summary>Persist the character's overlay (nothing if there is none, or it is not failed and <paramref name="onlyIfFailed"/>).</summary>
    private async Task<Exception?> ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued, bool sweep = false)
    {
        long? deleteGen;
        long deleteRequested;
        Overlay[] candidates;
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

            deleteRequested = unsaved.DeleteRequested;
            deleteGen = unsaved.DeleteRequested > unsaved.DeleteDone && (!onlyIfFailed || unsaved.DeleteFailure is not null)
                ? unsaved.DeleteRequested
                : null;
            candidates = [.. unsaved.Overlays.Where(o => !o.Rejected && !o.IsEmpty && (!onlyIfFailed || o.Failure is not null))];

            if (deleteGen is null && candidates.Length == 0)
            {
                Release(characterId, unsaved);
                return null;
            }
        }

        int steps = Math.Max(1, candidates.Length);
        for (int i = 0; i < steps; i++)
        {
            Overlay? candidate = i < candidates.Length ? candidates[i] : null;
            Snapshot snapshot;
            lock (_gate)
            {
                if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    return null;
                }

                long? stepDelete = i == 0 && deleteGen is { } gen && unsaved.DeleteDone < gen ? gen : null;
                Overlay? selectedCandidate = candidate is { Rejected: false, IsEmpty: false } activeCandidate
                    && unsaved.DeleteRequested == deleteRequested
                    && (!onlyIfFailed || activeCandidate.Failure is not null)
                    ? activeCandidate
                    : null;

                if (stepDelete is null && selectedCandidate is null)
                {
                    Release(characterId, unsaved);
                    continue;
                }

                Invariant.Check(stepDelete is not null || selectedCandidate is not null, "reputation write step must have either a delete generation or a candidate overlay");
                snapshot = new Snapshot(
                    stepDelete,
                    selectedCandidate is not null ? [.. selectedCandidate.Rows.Values] : [],
                    selectedCandidate?.Watched,
                    unsaved.DeleteRequested,
                    selectedCandidate?.Ownership,
                    selectedCandidate);
            }

            var made = new WriteAttemptCount();
            try
            {
                await WriteAsync(characterId, snapshot, sweep || _stopped ? 1 : MaxAttempts, made).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                bool rejected = IsOwnershipRejection(ex);
                bool newEpisode = true;
                lock (_gate)
                {
                    // Never recreate an entry that a delete or a creation removed while the write was in flight.
                    if (_characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.DeleteRequested == snapshot.DeleteRequested)
                    {
                        if (snapshot.Overlay is { } failedOverlay)
                        {
                            newEpisode = failedOverlay.Failure is null; // a retained write failing again is the same episode
                            failedOverlay.Failure = ex;
                            if (rejected)
                            {
                                failedOverlay.Rejected = true;
                            }
                        }
                        else
                        {
                            newEpisode = unsaved.DeleteFailure is null;
                            unsaved.DeleteFailure = ex;
                        }
                    }
                }

                if (ex is StopDrainExhaustedException)
                {
                    return ex; // refused by the stop drain: nothing was attempted, so no anomaly or fault here; the queue reports the refusal once, and the write stays retained
                }

                if (rejected)
                {
                    AnomalyLedger.Shared.Report(
                        "persist",
                        "circuit-open",
                        "queue ReputationWriteQueue",
                        $"queue=ReputationWriteQueue character={characterId} reason=terminal-ownership-failure error={ex.GetType().Name}",
                        AnomalySeverity.Warning);
                    logger.LogFault(LogLevel.Critical, "writequeue.reputation-ownership", ex, "reputation write for character {Character} was rejected by the character ownership fence; stopping application", characterId);
                    StopHost(ex);
                }
                else if (!sweep)
                {
                    if (newEpisode)
                    {
                        AnomalyLedger.Shared.Report(
                            "persist",
                            "write-retry-exhausted",
                            $"character {characterId}",
                            $"queue=ReputationWriteQueue character={characterId} attempts={made.Made} error={ex.GetType().Name}",
                            AnomalySeverity.Error);
                    }

                    logger.LogFault("writequeue.reputation", ex, "reputation write for character {Character} failed; retained for retry", characterId);
                }
                else
                {
                    FaultLedger.Shared.Record("writequeue.reputation-sweep", ex);
                }

                return ex;
            }

            lock (_gate)
            {
                if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    return null;
                }

                if (snapshot.DeleteGeneration is { } generation && unsaved.DeleteDone < generation)
                {
                    unsaved.DeleteDone = generation;
                    unsaved.DeleteFailure = null;
                }

                if (snapshot.Overlay is { } writtenOverlay && unsaved.DeleteRequested == snapshot.DeleteRequested)
                {
                    foreach (CharacterReputationRow row in snapshot.Rows)
                    {
                        if (writtenOverlay.Rows.TryGetValue(row.Faction, out CharacterReputationRow? current) && current == row)
                        {
                            writtenOverlay.Rows.Remove(row.Faction);
                        }
                    }

                    if (snapshot.Watched is { } watched && writtenOverlay.Watched == watched)
                    {
                        writtenOverlay.Watched = null;
                    }

                    writtenOverlay.Failure = null;
                    if (writtenOverlay.IsEmpty)
                    {
                        unsaved.Overlays.Remove(writtenOverlay);
                    }
                }

                Release(characterId, unsaved);
            }
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
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ICharacterReputationStore>() is not { } store)
                {
                    progress.Succeeded(); // no store: nothing to land, and not a stall
                    return; // hosts without persistence (the empty-catalog test host) keep nothing
                }

                // Every call is an idempotent upsert or delete, so a retry after a commit whose acknowledgement was lost is safe.
                if (snapshot.DeleteGeneration is not null)
                {
                    await store.DeleteDeletedCharacterAsync(characterId).ConfigureAwait(false); // conditional: skipped while the id has a character row again
                }

                if (snapshot.Rows.Length > 0)
                {
                    await store.SaveFactionsAsync(characterId, snapshot.Rows, ownership: snapshot.Ownership).ConfigureAwait(false);
                }

                if (snapshot.Watched is { } watched)
                {
                    await store.SaveWatchedFactionAsync(characterId, watched, ownership: snapshot.Ownership).ConfigureAwait(false);
                }

                progress.Succeeded();
                return;
            }
            catch (Exception ex) when (attempt < attempts && !IsOwnershipRejection(ex) && ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("writequeue.reputation-attempt", ex);
                logger.LogWarning(ex, "reputation write for character {Character} failed (attempt {Attempt}); retrying", characterId, attempt);
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private void StopHost(Exception ex)
    {
        try
        {
            FatalFailure?.Invoke(ex);
        }
        catch (Exception callbackError) when (callbackError is not OutOfMemoryException)
        {
            logger.LogFault("writequeue.reputation-callback", callbackError, "reputation write queue fatal-failure callback failed");
        }

        if (lifetime is not null)
        {
            lifetime.StopApplication();
            return;
        }

        using IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetService<IHostApplicationLifetime>()?.StopApplication();
    }

    private sealed record Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    /// <summary><paramref name="DeleteRequested"/> is the overlay's delete count when the snapshot was taken; <paramref name="Ownership"/> is the token captured at the overlay's first mutation.</summary>
    private sealed record Snapshot(long? DeleteGeneration, CharacterReputationRow[] Rows, int? Watched, long DeleteRequested, CharacterOwnershipToken? Ownership, Overlay? Overlay);

    /// <summary>True when <paramref name="ex"/>, or any inner exception (EF may wrap what an interceptor throws), is an ownership-fence rejection.</summary>
    private static bool IsOwnershipRejection(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One token-scoped overlay of reputation mutations for a character. Never coalesced across different ownership tokens.</summary>
    private sealed class Overlay(CharacterOwnershipToken? ownership)
    {
        public CharacterOwnershipToken? Ownership { get; } = ownership;
        public Dictionary<uint, CharacterReputationRow> Rows { get; } = [];
        public int? Watched { get; set; }
        public Exception? Failure { get; set; }

        /// <summary>The fence rejected this overlay's token: terminal, never retried (a stale owner stays stale). Cleared only by a delete.</summary>
        public bool Rejected { get; set; }

        public bool IsEmpty => Rows.Count == 0 && Watched is null;
    }

    /// <summary>One character's not-yet-durable state. Every member is guarded by the queue's gate.</summary>
    private sealed class Unsaved
    {
        public List<Overlay> Overlays { get; } = [];
        public long DeleteRequested { get; set; }
        public long DeleteDone { get; set; }
        public Exception? DeleteFailure { get; set; }
        public bool Queued { get; set; }

        public Exception? Failure => DeleteFailure ?? Overlays.FirstOrDefault(o => o.Failure is not null)?.Failure;

        public bool HasRetryableFailure => DeleteFailure is not null || Overlays.Any(o => o.Failure is not null && !o.Rejected);

        public bool IsEmpty => DeleteRequested <= DeleteDone && Overlays.All(o => o.IsEmpty);

        /// <summary>Return the tail overlay when its captured token matches <paramref name="ownership"/>; otherwise start a new token-isolated overlay.</summary>
        public Overlay Target(CharacterOwnershipToken? ownership)
        {
            if (Overlays.Count > 0 && Equals(Overlays[^1].Ownership, ownership))
            {
                return Overlays[^1];
            }

            var overlay = new Overlay(ownership);
            Overlays.Add(overlay);
            return overlay;
        }

        public void RecordQueueFailure(Exception ex)
        {
            if (Overlays.Count > 0)
            {
                Overlays[^1].Failure = ex;
            }
            else
            {
                DeleteFailure = ex;
            }
        }
    }
}
