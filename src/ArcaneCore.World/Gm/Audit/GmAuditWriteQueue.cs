using System.Threading.Channels;
using ArcaneCore.Kernel.Gm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// Persists mute and ticket writes made on the world thread, off it and in order (one consumer; the pattern of
/// <see cref="WorldState.ExploredZonesWriteQueue"/> keyed by a string instead of a character). Every write sets
/// one absolute value for its key (<c>mute:&lt;account&gt;</c>, <c>ticket:&lt;id&gt;</c>), so a newer write for a key that
/// is still waiting replaces the older one. A write that fails all <c>MaxAttempts</c> attempts is RETAINED, never
/// dropped: the next write of the key, the periodic retry (<see cref="RetainedRetryInterval"/>),
/// <see cref="RetryRetainedAsync"/> and <see cref="StopAsync"/> (a final retry that throws if storage is still failing)
/// all carry it. <see cref="FlushAsync"/> is a pure ordered barrier that never retries and never throws. Retention is
/// in process only; the world keeps its own in-memory working set, so a retained write never changes what staff see.
/// </summary>
public sealed class GmAuditWriteQueue(IServiceScopeFactory scopes, ILogger logger)
{
    private const int MaxAttempts = 3;

    /// <summary>Pause between attempts of one write (tests shorten it).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often retained writes are retried while the queue runs (tests shorten it).</summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Unsaved> _keys = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _timerStop = new();
    private Task? _consumer;
    private Task? _timer;
    private Task? _stop;
    private volatile bool _stopped;
    private int _pending;

    private enum Kind
    {
        Write,
        Barrier,
        RetryRetained,
    }

    private sealed class Unsaved
    {
        public Func<IGmAuditStore, Task> Write = static _ => Task.CompletedTask;
        public int Version;
        public bool Queued;
        public Exception? Failure;
    }

    private readonly record struct Work(string Key, Kind Kind, TaskCompletionSource? Done);

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Keys whose write failed all attempts and is still not durable.</summary>
    public IReadOnlyList<string> RetainedKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _keys.Where(k => k.Value.Failure is not null).Select(k => k.Key).Order(StringComparer.Ordinal)];
            }
        }
    }

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _consumer = Task.Run(ConsumeAsync);
        _timer = Task.Run(RetryTimerAsync);
    }

    /// <summary>Record the write for <paramref name="key"/> (replacing an older waiting one) and queue it unless one is waiting.</summary>
    public void Save(string key, Func<IGmAuditStore, Task> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(write);
        lock (_gate)
        {
            Unsaved unsaved = Entry(key);
            unsaved.Write = write;
            unsaved.Version++;
            if (_stopped)
            {
                unsaved.Failure = new InvalidOperationException("the GM audit write queue is stopped; the change was not persisted");
                logger.LogError("GM audit write {Key} arrived after the queue stopped; not persisted", key);
                return;
            }

            if (unsaved.Queued)
            {
                return;
            }

            unsaved.Queued = true;
            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(new Work(key, Kind.Write, null)))
            {
                unsaved.Queued = false;
                Interlocked.Decrement(ref _pending);
                unsaved.Failure = new InvalidOperationException("the GM audit write queue is closed; the change was not persisted");
            }
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
        return _channel.Writer.TryWrite(new Work(string.Empty, Kind.Barrier, done)) ? done.Task : Task.CompletedTask;
    }

    /// <summary>After every earlier write, retry each retained write once more; faults while any is still not durable.</summary>
    public Task RetryRetainedAsync()
    {
        if (_consumer is null || _stopped)
        {
            return RetainedFailureTask();
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(string.Empty, Kind.RetryRetained, done)) ? done.Task : RetainedFailureTask();
    }

    /// <summary>Stop accepting writes, drain, retry every retained key once; throws naming the keys still not durable. Idempotent.</summary>
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
        await _timerStop.CancelAsync().ConfigureAwait(false);
        if (_timer is not null)
        {
            await _timer.ConfigureAwait(false);
        }

        _channel.Writer.TryWrite(new Work(string.Empty, Kind.RetryRetained, null));
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        string[] failed;
        Exception? first;
        lock (_gate)
        {
            KeyValuePair<string, Unsaved>[] failures = [.. _keys.Where(k => k.Value.Failure is not null).OrderBy(k => k.Key, StringComparer.Ordinal)];
            failed = [.. failures.Select(k => k.Key)];
            first = failures.Length > 0 ? failures[0].Value.Failure : null;
        }

        if (failed.Length > 0)
        {
            throw new InvalidOperationException($"GM audit writes did not drain for {string.Join(", ", failed)}", first);
        }
    }

    private Unsaved Entry(string key)
    {
        if (!_keys.TryGetValue(key, out Unsaved? unsaved))
        {
            _keys[key] = unsaved = new Unsaved();
        }

        return unsaved;
    }

    private Task RetainedFailureTask()
    {
        string[] failed = [.. RetainedKeys];
        Exception? first;
        lock (_gate)
        {
            first = failed.Length > 0 ? _keys[failed[0]].Failure : null;
        }

        return failed.Length == 0 ? Task.CompletedTask : Task.FromException(NotDurable(failed, first));
    }

    private static InvalidOperationException NotDurable(IReadOnlyList<string> keys, Exception? failure)
        => new($"GM audit writes are not durable: {string.Join(", ", keys)}", failure);

    private async Task RetryTimerAsync()
    {
        using var timer = new PeriodicTimer(RetainedRetryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_timerStop.Token).ConfigureAwait(false))
            {
                if (RetainedKeys.Count > 0)
                {
                    _channel.Writer.TryWrite(new Work(string.Empty, Kind.RetryRetained, null));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
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
                            await ExecuteAsync(work.Key, onlyIfFailed: false, clearQueued: true).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _pending);
                        }

                        break;
                    case Kind.RetryRetained:
                        foreach (string key in RetainedKeys)
                        {
                            await ExecuteAsync(key, onlyIfFailed: true, clearQueued: false).ConfigureAwait(false);
                        }

                        string[] failed = [.. RetainedKeys];
                        if (failed.Length == 0)
                        {
                            work.Done?.TrySetResult();
                        }
                        else
                        {
                            Exception? first;
                            lock (_gate)
                            {
                                first = _keys.TryGetValue(failed[0], out Unsaved? unsaved) ? unsaved.Failure : null;
                            }

                            work.Done?.TrySetException(NotDurable(failed, first));
                        }

                        break;
                    default:
                        work.Done?.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GM audit queue item {Kind} for {Key} failed unexpectedly", work.Kind, work.Key);
                work.Done?.TrySetException(ex);
            }
        }
    }

    private async Task ExecuteAsync(string key, bool onlyIfFailed, bool clearQueued)
    {
        Func<IGmAuditStore, Task> write;
        int version;
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out Unsaved? unsaved))
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

            write = unsaved.Write;
            version = unsaved.Version;
        }

        Exception? failure = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetService<IGmAuditStore>() is { } store)
                {
                    await write(store).ConfigureAwait(false);
                }

                failure = null;
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogWarning(ex, "GM audit write {Key} failed (attempt {Attempt} of {Max})", key, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out Unsaved? unsaved))
            {
                return;
            }

            unsaved.Failure = failure;
            if (failure is null && unsaved.Version == version && !unsaved.Queued)
            {
                _keys.Remove(key); // durable and unchanged since: nothing to retain
            }
        }

        if (failure is not null)
        {
            logger.LogError(failure, "GM audit write {Key} is retained after {Max} failed attempts", key, MaxAttempts);
        }
    }
}
