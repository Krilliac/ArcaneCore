using System.Threading.Channels;
using ArcaneCore.Data.Characters.Life;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Persists rested-experience states queued on the world thread, off it and in order (one consumer; the pattern of
/// <see cref="WorldState.ExploredZonesWriteQueue"/>: one value per character). The latest state of a character is an in-memory
/// overlay; a queued write persists the overlay, and one that fails all <c>MaxAttempts</c> attempts is RETAINED, never
/// dropped: the next change, <see cref="FlushCharacterAsync"/> (the login barrier, which faults while the character is still
/// not durable), <see cref="RequestRetry"/> (logout) and <see cref="StopAsync"/> (a final retry that throws if storage is still
/// failing) all carry it. <see cref="FlushAsync"/> is a pure ordered barrier that never retries and never throws.
/// Retention is in process only.
/// </summary>
public sealed class RestWriteQueue(IServiceScopeFactory scopes, ILogger logger)
{
    private const int MaxAttempts = 3;

    /// <summary>Pause between attempts of one write (tests shorten it).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
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

    private sealed class Unsaved
    {
        public CharacterRestState State;
        public int Version;
        public bool Queued;
        public Exception? Failure;
    }

    private readonly record struct Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    /// <summary>Record the character's current state and queue a write unless one is waiting.</summary>
    public void Save(int characterId, CharacterRestState state)
    {
        lock (_gate)
        {
            Unsaved unsaved = Entry(characterId);
            unsaved.State = state;
            unsaved.Version++;
            if (_stopped)
            {
                unsaved.Failure = new InvalidOperationException("the rest write queue is stopped; the change was not persisted");
                logger.LogError("rested state of character {Character} arrived after the queue stopped; not persisted", characterId);
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
                unsaved.Failure = new InvalidOperationException("the rest write queue is closed; the change was not persisted");
            }
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
        if (_consumer is not null)
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
            throw new InvalidOperationException($"rested-state writes did not drain for characters {string.Join(", ", failed)}", first);
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
        => new($"rested-state writes for character {characterId} are not durable", failure);

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
                logger.LogError(ex, "rest queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
                work.Done?.TrySetException(ex);
            }
        }
    }

    private async Task ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued)
    {
        CharacterRestState state;
        int version;
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

            if (onlyIfFailed && unsaved.Failure is null)
            {
                return;
            }

            state = unsaved.State;
            version = unsaved.Version;
        }

        Exception? failure = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetService<ICharacterRestStore>() is { } store)
                {
                    await store.SaveAsync(characterId, state).ConfigureAwait(false);
                }

                failure = null;
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogWarning(ex, "rested-state write for character {Character} failed (attempt {Attempt} of {Max})", characterId, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return;
            }

            unsaved.Failure = failure;
            if (failure is null && unsaved.Version == version && !unsaved.Queued)
            {
                _characters.Remove(characterId); // durable and unchanged since: nothing to retain
            }
        }

        if (failure is not null)
        {
            logger.LogError(failure, "rested-state writes for character {Character} are retained after {Max} failed attempts", characterId, MaxAttempts);
        }
    }
}
