using System.Threading.Channels;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>How often retained writes are retried by a background sweep while any exist (one attempt per character, stopping at the first locked one; tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Unsaved> _characters = [];
    private Exception? _terminalFailure;
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
            return _terminalFailure is null && _characters.Values.Any(u => u.Failure is not null);
        }
    }

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
                unsaved.Failure = _terminalFailure ?? new InvalidOperationException("the rest write queue is stopped; the change was not persisted");
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
            throw new InvalidOperationException($"rested-state writes did not drain for characters {string.Join(", ", failed)}", first);
        }

        if (terminal is not null)
        {
            throw new InvalidOperationException("rest write queue stopped after terminal ownership rejection", terminal);
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

    private static InvalidOperationException NotDurable(int characterId, Exception failure)
        => new($"rested-state writes for character {characterId} are not durable", failure);

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

        logger.LogCritical(ex, "rest write queue entered terminal failure after character ownership rejection");
        try
        {
            scopedServices?.GetService<IHostApplicationLifetime>()?.StopApplication();
        }
        catch (ObjectDisposedException)
        {
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
            catch (Exception ex)
            {
                logger.LogError(ex, "rest queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
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
            logger.LogWarning("rested-state retry: {Failed} of {Total} retained characters still failing{Locked}; first: {Reason}", failed, retained.Length, locked ? " (the database is locked; the rest wait for the next sweep)" : "", first!.Message);
        }
    }

    private async Task<Exception?> ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued, bool sweep = false)
    {
        CharacterRestState state;
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
                unsaved.Failure ??= terminal;
                return terminal;
            }

            if (onlyIfFailed && unsaved.Failure is null)
            {
                return null;
            }

            state = unsaved.State;
            version = unsaved.Version;
        }

        int attempts = sweep ? 1 : MaxAttempts;
        Exception? failure = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            using IServiceScope scope = scopes.CreateScope();
            try
            {
                if (scope.ServiceProvider.GetService<ICharacterRestStore>() is { } store)
                {
                    await store.SaveAsync(characterId, state).ConfigureAwait(false);
                }

                failure = null;
                break;
            }
            catch (CharacterOwnershipRejectedException ex)
            {
                failure = ex;
                FailTerminal(ex, scope.ServiceProvider);
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
                if (!sweep)
                {
                    logger.LogWarning(ex, "rested-state write for character {Character} failed (attempt {Attempt} of {Max})", characterId, attempt, attempts);
                }

                if (attempt < attempts)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return null;
            }

            unsaved.Failure = failure;
            if (failure is null && unsaved.Version == version && !unsaved.Queued)
            {
                _characters.Remove(characterId); // durable and unchanged since: nothing to retain
            }
        }

        if (failure is not null && !sweep)
        {
            logger.LogError(failure, "rested-state writes for character {Character} are retained after {Max} failed attempts", characterId, attempts);
        }

        return failure;
    }
}
