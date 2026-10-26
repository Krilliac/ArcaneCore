using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Persists writes made on the world thread, off it and in order, against a scoped store <typeparamref name="TStore"/> (one
/// consumer; the pattern of <see cref="WorldState.ExploredZonesWriteQueue"/> keyed by a string instead of a character).
/// Every write sets one absolute value for its key, so a newer write for a key that is still waiting replaces the older
/// one. A write that fails all <c>MaxAttempts</c> attempts is RETAINED, never dropped: the next write of the key, the
/// periodic retry (<see cref="RetainedRetryInterval"/>), <see cref="RetryRetainedAsync"/> and <see cref="StopAsync"/> (a
/// final retry that throws if storage is still failing) all carry it. <see cref="FlushAsync"/> is a pure ordered barrier
/// that never retries and never throws. Retention is in process only. Without a registered <typeparamref name="TStore"/> a
/// write is a no-op (the feature then lives in memory only).
/// <para>
/// One implementation for every keyed store: <see cref="Gm.Audit.GmAuditWriteQueue"/> (mutes, tickets) is this queue over
/// <c>IGmAuditStore</c>, and the group persistence (<see cref="Social.SocialGroupPersistenceFeature"/>) and the account
/// last-address record use it directly. Tests: KeyedStoreWriteQueueTests and GmAuditUnitTests.
/// </para>
/// </summary>
/// <param name="scopes">Creates the scope each attempt resolves <typeparamref name="TStore"/> from.</param>
/// <param name="logger">Failures and retention.</param>
/// <param name="name">The queue's name in log lines and error messages ("GM audit", "group", …).</param>
/// <param name="drain">The shutdown stall lane (see <see cref="ShutdownDrain"/>); null: a private lane, so a queue stopped outside the world host is still bounded.</param>
public class KeyedStoreWriteQueue<TStore>(IServiceScopeFactory scopes, ILogger logger, string name, StopDrainBudget? drain = null)
    where TStore : class
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
    private readonly StopDrainBudget _drain = drain ?? new StopDrainBudget();
    private int _pending;

    private enum Kind
    {
        Write,
        Barrier,
        RetryRetained,
    }

    private sealed class Unsaved
    {
        public Func<TStore, Task> Write = static _ => Task.CompletedTask;
        public int Version;
        public bool Queued;
        public Exception? Failure;
        public bool Reported; // this key's failure episode was already reported to the anomaly ledger; cleared when it finally writes
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

    /// <summary>The failure of the first retained key (ascending), or null when nothing is retained.</summary>
    public Exception? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _keys.Where(k => k.Value.Failure is not null).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value.Failure).FirstOrDefault();
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
    public void Save(string key, Func<TStore, Task> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(write);
        bool droppedStopped = false;
        bool droppedClosed = false;
        lock (_gate)
        {
            Unsaved unsaved = Entry(key);
            unsaved.Write = write;
            unsaved.Version++;
            if (_stopped)
            {
                unsaved.Failure = new InvalidOperationException($"the {name} write queue is stopped; the change was not persisted");
                logger.LogError("{Queue} write {Key} arrived after the queue stopped; not persisted", name, key);
                droppedStopped = true;
            }
            else if (!unsaved.Queued)
            {
                unsaved.Queued = true;
                Interlocked.Increment(ref _pending);
                if (!_channel.Writer.TryWrite(new Work(key, Kind.Write, null)))
                {
                    unsaved.Queued = false;
                    Interlocked.Decrement(ref _pending);
                    unsaved.Failure = new InvalidOperationException($"the {name} write queue is closed; the change was not persisted");
                    droppedClosed = true;
                }
            }
        }

        if (droppedStopped || droppedClosed)
        {
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                $"queue {name}",
                $"queue={name} key={key} reason={(droppedStopped ? "queue-stopped" : "queue-closed")}",
                AnomalySeverity.Error);
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
        _drain.Begin();
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
            AnomalyLedger.Shared.Report(
                "persist",
                "write-dropped-on-shutdown",
                $"queue {name}",
                $"queue={name} undrainedKeys={failed.Length} firstKey={failed[0]} refusedByStopDrain={_drain.Refused} stalledMs={(long)_drain.StalledMs}",
                AnomalySeverity.Error);
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.keyed-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-keyed-stop-drain",
                    refusal,
                    "{Queue} shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    name,
                    _drain.Refused,
                    failed.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"{name} writes did not drain for {string.Join(", ", failed)}", first);
        }
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

    private InvalidOperationException NotDurable(IReadOnlyList<string> keys, Exception? failure)
        => new($"{name} writes are not durable: {string.Join(", ", keys)}", failure);

    private Unsaved Entry(string key)
    {
        if (!_keys.TryGetValue(key, out Unsaved? unsaved))
        {
            _keys[key] = unsaved = new Unsaved();
        }

        return unsaved;
    }

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
            /* expected on shutdown */
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("persist.keyed-write-queue", ex);
            logger.LogFault("writequeue.keyed-timer", ex, "{Queue} retained-retry timer failed unexpectedly", name);
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
            catch (OperationCanceledException)
            {
                /* expected on shutdown */
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("persist.keyed-write-queue", ex);
                logger.LogFault("writequeue.keyed-consume", ex, "{Queue} queue item {Kind} for {Key} failed unexpectedly", name, work.Kind, work.Key);
                work.Done?.TrySetException(ex);
            }
        }
    }

    private async Task ExecuteAsync(string key, bool onlyIfFailed, bool clearQueued)
    {
        Func<TStore, Task> write;
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
        int attemptsMade = 0;
        bool refused = false;
        int attempts = _stopped ? 1 : MaxAttempts; // at stop: one attempt per write, busy or not, no inline retry
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (_stopped && _drain.Refuse() is { } exhausted)
            {
                failure = exhausted; // shutdown drain only: nothing is attempted, the write stays retained
                refused = true;
                break;
            }

            attemptsMade = attempt;
            using StopDrainBudget.Attempt progress = _drain.BeginAttempt();
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetService<TStore>() is { } store)
                {
                    await write(store).ConfigureAwait(false);
                }

                progress.Succeeded();
                failure = null;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
                FaultLedger.Shared.Record("writequeue.keyed-attempt", ex);
                logger.LogWarning(ex, "{Queue} write {Key} failed (attempt {Attempt} of {Max})", name, key, attempt, attempts);
                if (DatabaseTransience.IsBusy(ex))
                {
                    break; // another connection holds the database: keep the write retained, never wait out the lock again inline
                }

                if (attempt < attempts)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        bool report = false;
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out Unsaved? unsaved))
            {
                return;
            }

            unsaved.Failure = failure;
            if (failure is null)
            {
                unsaved.Reported = false; // the episode is over: a later failure of this key is a new one
            }
            else if (!unsaved.Reported && !refused)
            {
                unsaved.Reported = true;
                report = true;
            }

            if (failure is null && unsaved.Version == version && !unsaved.Queued)
            {
                _keys.Remove(key); // durable and unchanged since: nothing to retain
            }
        }

        if (failure is not null && !refused)
        {
            if (report)
            {
                AnomalyLedger.Shared.Report(
                    "persist",
                    "write-retry-exhausted",
                    $"queue {name}",
                    $"queue={name} key={key} attempts={attemptsMade} error={failure.GetType().Name}",
                    AnomalySeverity.Error);
            }

            logger.LogFault("writequeue.keyed", failure, "{Queue} write {Key} is retained after {Max} failed attempts", name, key, attemptsMade);
        }
    }
}
