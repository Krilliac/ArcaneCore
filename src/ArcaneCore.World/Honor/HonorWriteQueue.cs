using System.Threading.Channels;
using ArcaneCore.Kernel.Honor;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class HonorWriteQueue(IServiceScopeFactory scopes, ILogger logger)
{
    private const int MaxAttempts = 3;

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Unsaved> _characters = [];
    private Task? _consumer;
    private Task? _stop;
    private volatile bool _stopped;
    private int _pending;

    private enum Kind
    {
        Write,
        Barrier,
        FlushCharacter,
        Retry,
        DrainAll,
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

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    /// <summary>Queue one contribution row.</summary>
    public void AppendCp(int characterId, HonorCpRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Add(row);
            Changed(characterId, unsaved);
        }
    }

    /// <summary>Queue the character's new state (replaces a state still waiting).</summary>
    public void SaveState(int characterId, CharacterHonorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.State = state;
            Changed(characterId, unsaved);
        }
    }

    /// <summary>Queue a reset (HonorMgr::Reset): rows and numbers go, and anything still waiting for the character is discarded.</summary>
    public void Reset(int characterId)
    {
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Clear();
            unsaved.State = null;
            unsaved.Failure = null;
            unsaved.ResetRequested++;
            Changed(characterId, unsaved);
        }
    }

    /// <summary>
    /// Queue removal of a deleted character's rows (conditional on the id still having no character row). Anything retained
    /// for the character is discarded first, so a failed older write can never bring a row back.
    /// </summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.Cp.Clear();
            unsaved.State = null;
            unsaved.Failure = null;
            unsaved.DeleteRequested++;
            Changed(characterId, unsaved);
        }
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
        if (_consumer is not null)
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
            throw new InvalidOperationException($"honor writes did not drain for characters {string.Join(", ", failed)}", first);
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
            unsaved.Failure = new InvalidOperationException("the honor write queue is stopped; the change was not persisted");
            logger.LogError("honor change for character {Character} arrived after the queue stopped; not persisted", characterId);
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
            unsaved.Failure = new InvalidOperationException("the honor write queue is closed; the change was not persisted");
            logger.LogError("honor change for character {Character} arrived after the queue closed; not persisted", characterId);
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
        => new($"honor writes for character {characterId} are not durable", failure);

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
                    default:
                        work.Done?.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                // ExecuteAsync reports store failures itself; this is a queue bug and must not kill the consumer.
                logger.LogError(ex, "honor write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
                work.Done?.TrySetException(ex);
            }
        }
    }

    /// <summary>Persist the character's overlay (nothing if there is none, or it is not failed and <paramref name="onlyIfFailed"/>).</summary>
    private async Task ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued)
    {
        Snapshot snapshot;
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return;
            }

            if (clearQueued)
            {
                unsaved.Queued = false;
            }

            if ((onlyIfFailed && unsaved.Failure is null) || unsaved.IsEmpty)
            {
                Release(characterId, unsaved);
                return;
            }

            snapshot = new Snapshot(
                unsaved.DeleteRequested > unsaved.DeleteDone ? unsaved.DeleteRequested : null,
                unsaved.ResetRequested > unsaved.ResetDone ? unsaved.ResetRequested : null,
                unsaved.State,
                [.. unsaved.Cp],
                unsaved.ResetRequested,
                unsaved.DeleteRequested);
        }

        try
        {
            await WriteAsync(characterId, snapshot).ConfigureAwait(false);
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

            logger.LogError(ex, "honor write for character {Character} failed; retained for retry", characterId);
            return;
        }

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return;
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
    }

    /// <summary>Under <see cref="_gate"/>: forget an entry with nothing left to write or wait for.</summary>
    private void Release(int characterId, Unsaved unsaved)
    {
        if (unsaved.IsEmpty && !unsaved.Queued && unsaved.Failure is null)
        {
            _characters.Remove(characterId);
        }
    }

    private async Task WriteAsync(int characterId, Snapshot snapshot)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IHonorStore>() is not { } store)
                {
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

                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
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
