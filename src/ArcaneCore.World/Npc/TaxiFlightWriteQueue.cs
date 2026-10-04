using System.Threading.Channels;
using ArcaneCore.Kernel.Npc;
using Microsoft.Extensions.DependencyInjection;
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
/// <see cref="FlushAsync"/> stays a pure ordered barrier: it never retries and never throws. Retention is in process
/// only: a crash, or a store that is still down at exit, loses what is retained (loudly).
/// </para>
/// </summary>
public sealed class TaxiFlightWriteQueue(IServiceScopeFactory scopes, ILogger logger)
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

    /// <summary>Queue saving the route of a character that logged out mid-flight; replaces any older request.</summary>
    public void Save(int characterId, TaxiFlightRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        Request(characterId, route);
    }

    /// <summary>Queue removing the character's saved route; replaces any older request.</summary>
    public void Delete(int characterId) => Request(characterId, null);

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
            throw new InvalidOperationException($"taxi route writes did not drain for characters {string.Join(", ", failed)}", first);
        }
    }

    /// <summary>A null <paramref name="route"/> is a delete.</summary>
    private void Request(int characterId, TaxiFlightRoute? route)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                _characters[characterId] = unsaved = new Unsaved();
            }

            unsaved.Operation = new Operation(route, ++unsaved.Generation);
            unsaved.Failure = null; // the new request supersedes the failed one and is itself attempted
            if (_stopped)
            {
                unsaved.Failure = new InvalidOperationException("the taxi route write queue is stopped; the change was not persisted");
                logger.LogError("taxi route change for character {Character} arrived after the queue stopped; not persisted", characterId);
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
                unsaved.Failure = new InvalidOperationException("the taxi route write queue is closed; the change was not persisted");
                logger.LogError("taxi route change for character {Character} arrived after the queue closed; not persisted", characterId);
            }
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
        => new($"taxi route writes for character {characterId} are not durable", failure);

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
                logger.LogError(ex, "taxi route write queue item {Kind} for character {Character} failed unexpectedly", work.Kind, work.CharacterId);
                work.Done?.TrySetException(ex);
            }
        }
    }

    /// <summary>Persist the character's outstanding operation (nothing if there is none, or it is not failed and <paramref name="onlyIfFailed"/>).</summary>
    private async Task ExecuteAsync(int characterId, bool onlyIfFailed, bool clearQueued)
    {
        Operation operation;
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

            if ((onlyIfFailed && unsaved.Failure is null) || unsaved.Operation is not { } current)
            {
                Release(characterId, unsaved);
                return;
            }

            operation = current;
        }

        try
        {
            await WriteAsync(characterId, operation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                // Only the request that failed is retained; a newer one replaced it and is queued itself.
                if (_characters.TryGetValue(characterId, out Unsaved? unsaved) && unsaved.Operation?.Generation == operation.Generation)
                {
                    unsaved.Failure = ex;
                }
            }

            logger.LogError(ex, "taxi route {Action} for character {Character} failed; retained for retry", operation.Route is null ? "delete" : "save", characterId);
            return;
        }

        lock (_gate)
        {
            if (!_characters.TryGetValue(characterId, out Unsaved? unsaved))
            {
                return;
            }

            if (unsaved.Operation?.Generation == operation.Generation)
            {
                unsaved.Operation = null;
                unsaved.Failure = null;
            }

            Release(characterId, unsaved);
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

    private async Task WriteAsync(int characterId, Operation operation)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ICharacterTaxiFlightStore>() is not { } store)
                {
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

                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "taxi route write for character {Character} failed (attempt {Attempt}); retrying", characterId, attempt);
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private sealed record Work(int CharacterId, Kind Kind, TaskCompletionSource? Done);

    private sealed record Operation(TaxiFlightRoute? Route, long Generation);

    /// <summary>One character's not-yet-durable state. Every member is guarded by the queue's gate.</summary>
    private sealed class Unsaved
    {
        public Operation? Operation { get; set; }
        public long Generation { get; set; }
        public bool Queued { get; set; }
        public Exception? Failure { get; set; }
    }
}
