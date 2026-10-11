using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class ReputationWriteQueue(IServiceScopeFactory scopes, ILogger logger)
{
    private const int MaxAttempts = 3;

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

    /// <summary>Tests: queue one retained-write sweep now, exactly as a timer tick would; false when one is already pending or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest() => _retry?.TryPost() ?? false;

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _retry = new RetainedRetryTimer(RetainedRetryInterval, () => RetainedCharacters.Count > 0, () => _channel.Writer.TryWrite(new Work(0, Kind.Sweep, null)));
        _retry.Start();
        _consumer = Task.Run(ConsumeAsync);
    }

    public void SaveFactions(int characterId, IReadOnlyList<CharacterReputationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            foreach (CharacterReputationRow row in rows)
            {
                unsaved.Rows[row.Faction] = row;
            }

            Changed(characterId, unsaved);
        }
    }

    public void SaveWatchedFaction(int characterId, int watched)
    {
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Watched = watched;
            Changed(characterId, unsaved);
        }
    }

    /// <summary>
    /// Queue removal of a deleted character's rows (conditional on the id still having no character row). Anything retained for the character is
    /// discarded first, so a failed older write can never bring a row back.
    /// </summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Rows.Clear();
            unsaved.Watched = null;
            unsaved.Failure = null;
            unsaved.DeleteRequested++;
            Changed(characterId, unsaved);
        }
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
    private void Changed(int characterId, Unsaved unsaved)
    {
        if (_stopped)
        {
            unsaved.Failure = new InvalidOperationException("the reputation write queue is stopped; the change was not persisted");
            logger.LogError("reputation change for character {Character} arrived after the queue stopped; not persisted", characterId);
            return;
        }

        if (unsaved.Queued)
        {
            return;
        }

        unsaved.Queued = true;
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Work(characterId, Kind.Write, null)))
        {
            unsaved.Queued = false;
            Interlocked.Decrement(ref _pending);
            unsaved.Failure = new InvalidOperationException("the reputation write queue is closed; the change was not persisted");
            logger.LogError("reputation change for character {Character} arrived after the queue closed; not persisted", characterId);
        }
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
            catch (Exception ex)
            {
                // ExecuteAsync reports store failures itself; this is a queue bug and must not kill the consumer.
                logger.LogError(ex, "reputation write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
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

        if (failed > 0)
        {
            logger.LogWarning("reputation retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", first!.Message);
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

            if ((onlyIfFailed && unsaved.Failure is null) || unsaved.IsEmpty)
            {
                Release(characterId, unsaved);
                return null;
            }

            snapshot = new Snapshot(
                unsaved.DeleteRequested > unsaved.DeleteDone ? unsaved.DeleteRequested : null,
                [.. unsaved.Rows.Values],
                unsaved.Watched);
        }

        try
        {
            await WriteAsync(characterId, snapshot, sweep ? 1 : MaxAttempts).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                // Never recreate an entry that a delete or a creation removed while the write was in flight.
                if (_characters.TryGetValue(characterId, out Unsaved? unsaved))
                {
                    unsaved.Failure = ex;
                }
            }

            if (!sweep)
            {
                logger.LogError(ex, "reputation write for character {Character} failed; retained for retry", characterId);
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
            }

            foreach (CharacterReputationRow row in snapshot.Rows)
            {
                if (unsaved.Rows.TryGetValue(row.Faction, out CharacterReputationRow? current) && current == row)
                {
                    unsaved.Rows.Remove(row.Faction);
                }
            }

            if (snapshot.Watched is { } watched && unsaved.Watched == watched)
            {
                unsaved.Watched = null;
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

    private async Task WriteAsync(int characterId, Snapshot snapshot, int attempts)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ICharacterReputationStore>() is not { } store)
                {
                    return; // hosts without persistence (the empty-catalog test host) keep nothing
                }

                // Every call is an idempotent upsert or delete, so a retry after a commit whose acknowledgement was lost is safe.
                if (snapshot.DeleteGeneration is not null)
                {
                    await store.DeleteDeletedCharacterAsync(characterId).ConfigureAwait(false); // conditional: skipped while the id has a character row again
                }

                if (snapshot.Rows.Length > 0)
                {
                    await store.SaveFactionsAsync(characterId, snapshot.Rows).ConfigureAwait(false);
                }

                if (snapshot.Watched is { } watched)
                {
                    await store.SaveWatchedFactionAsync(characterId, watched).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                logger.LogWarning(ex, "reputation write for character {Character} failed (attempt {Attempt}); retrying", characterId, attempt);
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private sealed record Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    private sealed record Snapshot(long? DeleteGeneration, CharacterReputationRow[] Rows, int? Watched);

    /// <summary>One character's not-yet-durable state. Every member is guarded by the queue's gate.</summary>
    private sealed class Unsaved
    {
        public Dictionary<uint, CharacterReputationRow> Rows { get; } = [];
        public int? Watched { get; set; }
        public long DeleteRequested { get; set; }
        public long DeleteDone { get; set; }
        public bool Queued { get; set; }
        public Exception? Failure { get; set; }

        public bool IsEmpty => Rows.Count == 0 && Watched is null && DeleteRequested <= DeleteDone;
    }
}
