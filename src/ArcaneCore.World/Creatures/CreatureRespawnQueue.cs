using System.Threading.Channels;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// The world's <see cref="ICreatureRespawnPersistence"/>: reads answer from memory (loaded once at start), and every change updates that copy and
/// is written by one background consumer, in order, off the world thread (like <see cref="Instances.InstanceWriteQueue"/>). A write is retried a
/// few times; one that still fails is <b>retained</b> (the latest failed write per spawn, and per instance for an instance delete), never dropped:
/// a newer write of the same spawn supersedes it, and <see cref="StopAsync"/> makes a final retry that throws, naming what is still not durable
/// (the Honor queue does the same). Without a registered <see cref="ICreatureRespawnStore"/> writes are discarded.
/// <para>
/// The in-memory copy is touched by the world thread only (after <see cref="LoadInitial"/> at start); the consumer works from the queued
/// operations, never from the copy.
/// </para>
/// <para>
/// A reset or deleted dungeon instance is forgotten through <see cref="ForgetInstance"/>, which queues the store's instance delete on this same
/// queue: the instance store deletes the rows on its own consumer, so only a delete ordered after this queue's earlier saves of that instance
/// cannot be undone by one of them still waiting here.
/// </para>
/// </summary>
public sealed class CreatureRespawnQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null) : ICreatureRespawnPersistence
{
    private const int MaxAttempts = 3;

    /// <summary>Most spawn writes sent to the store in one call. Matches the store's own chunk size (EfCreatureRespawnStore.ChunkSize, 500), so a batch is one store transaction per instance.</summary>
    private const int MaxBatch = 500;

    /// <summary>The marker the retry timer queues: not a store write, so the consumer sweeps the retained writes when it reaches it.</summary>
    private static readonly Op RetrySweep = new(default, 0, null, null, IsSweep: true);

    private readonly Dictionary<(uint Map, uint Instance), Dictionary<uint, long>> _times = [];
    private readonly Channel<Op> _channel = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<OpKey, Retained> _retained = [];
    private readonly Dictionary<uint, long> _forgottenAt = [];
    private readonly CancellationTokenSource _timerStop = new();
    private int _sweepQueued; // 1 while a retry marker is in the channel or being swept: the timer never stacks another
    private long _sequence;
    private Task? _timer;
    private Task? _consumer;
    private int _pending;
    private volatile bool _stopping; // set by StopAsync: one attempt per write, and the stop drain's stall bound applies
    private readonly StopDrainBudget _drain = drain ?? new StopDrainBudget();

    /// <summary>One queued store write. A spawn write names its key; an instance delete has <see cref="OpKey.SpawnGuid"/> 0 and <see cref="OpKey.InstanceWide"/> set.</summary>
    private sealed record Op(OpKey Key, long Sequence, CreatureRespawnRecord? SaveRecord, CreatureRespawnKey? DeleteKey, bool IsSweep = false)
    {
        public Task ExecuteAsync(ICreatureRespawnStore store)
        {
            if (Key.InstanceWide)
            {
                return store.DeleteInstanceAsync(Key.InstanceId);
            }

            if (SaveRecord is { } record)
            {
                return store.SaveAsync([record], []);
            }

            if (DeleteKey is { } key)
            {
                return store.SaveAsync([], [key]);
            }

            return Task.CompletedTask;
        }
    }

    private readonly record struct OpKey(uint InstanceId, uint SpawnGuid, bool InstanceWide);

    private sealed record Retained(Op Op, Exception Failure);

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>The number of writes that failed all attempts and are still retained.</summary>
    public int RetainedCount
    {
        get
        {
            lock (_gate)
            {
                return _retained.Count;
            }
        }
    }

    /// <summary>The retained writes by key (<c>spawn:INSTANCE/GUID</c>, or <c>instance:ID</c> for an instance-wide delete), ordinal order.</summary>
    public IReadOnlyList<string> RetainedKeys
    {
        get
        {
            lock (_gate)
            {
                if (_retained.Count == 0) return [];
                string[] keys = [.. _retained.Keys.Select(k => k.InstanceWide
                    ? $"instance:{k.InstanceId}"
                    : $"spawn:{k.InstanceId}/{k.SpawnGuid}")];
                Array.Sort(keys, StringComparer.Ordinal);
                return keys;
            }
        }
    }

    /// <summary>Tests: queue one retained-write sweep now, as a timer tick would; false when one is already queued or the queue is not started.</summary>
    internal bool PostRetainedSweepForTest()
        => _consumer is not null && Interlocked.CompareExchange(ref _sweepQueued, 1, 0) == 0 && _channel.Writer.TryWrite(RetrySweep);

    /// <summary>Put the stored rows into the in-memory copy (before the world thread runs).</summary>
    public void LoadInitial(IEnumerable<CreatureRespawnRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (CreatureRespawnRecord record in records)
        {
            For(record.MapId, record.InstanceId)[record.SpawnGuid] = record.RespawnTime;
        }
    }

    /// <summary>
    /// How often writes retained after a failure (a locked SQLite database fails a write after the store's bounded wait and is not retried inline, so the
    /// consumer is never held) are retried while the queue runs. Tests shorten it.
    /// </summary>
    public TimeSpan RetainedRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public void Start()
    {
        if (_consumer is not null)
        {
            return;
        }

        _consumer = Task.Run(ConsumeAsync);
        _timer = Task.Run(RetryTimerAsync);
    }

    private async Task RetryTimerAsync()
    {
        using var timer = new PeriodicTimer(RetainedRetryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_timerStop.Token).ConfigureAwait(false))
            {
                if (RetainedCount > 0 && Interlocked.CompareExchange(ref _sweepQueued, 1, 0) == 0)
                {
                    _channel.Writer.TryWrite(RetrySweep);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("writequeue.respawn-timer", ex);
            logger.LogError(ex, "creature respawn retry timer faulted");
        }
    }

    /// <summary>
    /// On the consumer: retry every retained write once, in the order <see cref="StopAsync"/> uses. Stops at the first locked database so the rest do not
    /// each wait out the same lock; a write superseded meanwhile (its key was rewritten or forgotten) is left alone.
    /// </summary>
    private async Task SweepRetainedAsync()
    {
        Op[] retry;
        lock (_gate)
        {
            retry = [.. _retained.Values.Select(r => r.Op).OrderByDescending(o => o.Key.InstanceWide).ThenBy(o => o.Key.InstanceId).ThenBy(o => o.Key.SpawnGuid)];
        }

        int failed = 0;
        bool locked = false;
        Exception? firstFailure = null;
        foreach ((Op op, Exception? failure) in await RetryRetainedAsync(retry, sweep: true).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (!_retained.TryGetValue(op.Key, out Retained? current) || !ReferenceEquals(current.Op, op))
                {
                    continue;
                }

                if (failure is null)
                {
                    _retained.Remove(op.Key);
                }
                else
                {
                    _retained[op.Key] = new Retained(op, failure);
                    failed++;
                    firstFailure ??= failure;
                    if (DatabaseTransience.IsBusy(failure))
                    {
                        locked = true;
                    }
                }
            }
        }

        if (failed > 0 && firstFailure is not null)
        {
            logger.LogWarning(
                "creature respawn retry: {Failed} of {Total} retained writes still failing{Locked}; first: {Reason}",
                failed,
                retry.Length,
                locked ? " (the database is locked; the rest wait for the next sweep)" : "",
                firstFailure.Message);
        }
    }

    public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId)
        => _times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) ? new Dictionary<uint, long>(times) : new Dictionary<uint, long>();

    public bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds)
    {
        if (_times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) && times.TryGetValue(spawnGuid, out respawnUnixSeconds)) return true;
        respawnUnixSeconds = 0;
        return false;
    }

    public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
    {
        For(mapId, instanceId)[spawnGuid] = respawnUnixSeconds;
        var record = new CreatureRespawnRecord(mapId, instanceId, spawnGuid, respawnUnixSeconds);
        Enqueue(new OpKey(instanceId, spawnGuid, false), record, null);
    }

    public void Delete(uint mapId, uint instanceId, uint spawnGuid)
    {
        if (_times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) && times.Remove(spawnGuid) && times.Count == 0)
        {
            _times.Remove((mapId, instanceId));
        }

        var key = new CreatureRespawnKey(instanceId, spawnGuid);
        Enqueue(new OpKey(instanceId, spawnGuid, false), null, key);
    }

    /// <summary>
    /// A dungeon instance was reset or deleted (world thread): drop its times from memory and from every write still retained, and queue the
    /// removal of its stored rows behind the saves already queued here. A save of that instance that is still in flight and fails afterwards is
    /// not retained. Instance 0 (the shared copy of a map) is never forgotten.
    /// </summary>
    public void ForgetInstance(uint instanceId)
    {
        if (instanceId == 0)
        {
            return;
        }

        foreach ((uint Map, uint Instance) key in _times.Keys.Where(k => k.Instance == instanceId).ToArray())
        {
            _times.Remove(key);
        }

        lock (_gate)
        {
            foreach (OpKey key in _retained.Keys.Where(k => k.InstanceId == instanceId).ToArray())
            {
                _retained.Remove(key);
            }

            EnqueueCore(new OpKey(instanceId, 0, true), null, null, forgetsInstance: true);
        }
    }

    /// <summary>Wait until every write queued so far has been attempted (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        while (Pending > 0)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stop accepting writes, wait until every queued one is done, then retry what is still retained once more. Throws
    /// <see cref="InvalidOperationException"/> naming the spawns and instances whose writes are still not durable.
    /// </summary>
    public async Task StopAsync()
    {
        _stopping = true;
        _drain.Begin();
        if (!_timerStop.IsCancellationRequested)
        {
            await _timerStop.CancelAsync().ConfigureAwait(false);
        }

        if (_timer is not null)
        {
            await _timer.ConfigureAwait(false);
        }

        _timerStop.Dispose(); // the timer task has ended; a second StopAsync only reads IsCancellationRequested

        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }

        Op[] retry;
        lock (_gate)
        {
            retry = [.. _retained.Values.Select(r => r.Op).OrderByDescending(o => o.Key.InstanceWide).ThenBy(o => o.Key.InstanceId).ThenBy(o => o.Key.SpawnGuid)];
        }

        // instance deletes first: a spawn write retained for the same instance is newer than it
        foreach ((Op op, Exception? failure) in await RetryRetainedAsync(retry, sweep: false).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (failure is null)
                {
                    _retained.Remove(op.Key);
                }
                else
                {
                    _retained[op.Key] = new Retained(op, failure);
                }
            }
        }

        string[] left;
        Exception? first;
        lock (_gate)
        {
            left = [.. _retained.Keys.OrderByDescending(k => k.InstanceWide).ThenBy(k => k.InstanceId).ThenBy(k => k.SpawnGuid)
                .Select(k => k.InstanceWide ? $"instance {k.InstanceId}" : $"spawn {k.SpawnGuid} (instance {k.InstanceId})")];
            first = _retained.Values.Select(r => r.Failure).FirstOrDefault();
        }

        if (left.Length > 0)
        {
            if (_drain.LastRefusal is { } refusal)
            {
                FaultLedger.Shared.Record("writequeue.respawn-stop-drain", refusal);
                logger.LogFault(
                    "writequeue-respawn-stop-drain",
                    refusal,
                    "creature respawn queue shutdown drain stalled; {Refused} of {Undrained} retained writes were not attempted: {Summary}",
                    _drain.Refused,
                    left.Length,
                    _drain.Describe());
            }

            throw new InvalidOperationException($"creature respawn writes did not drain for {string.Join(", ", left)}", first);
        }
    }

    private Dictionary<uint, long> For(uint mapId, uint instanceId)
    {
        if (!_times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times))
        {
            _times[(mapId, instanceId)] = times = [];
        }

        return times;
    }

    private void Enqueue(OpKey key, CreatureRespawnRecord? saveRecord, CreatureRespawnKey? deleteKey)
    {
        lock (_gate)
        {
            EnqueueCore(key, saveRecord, deleteKey, forgetsInstance: false);
        }
    }

    /// <summary>Under <see cref="_gate"/>: number the write and queue it; a write numbered after an instance delete belongs to the instance's next life.</summary>
    private void EnqueueCore(OpKey key, CreatureRespawnRecord? saveRecord, CreatureRespawnKey? deleteKey, bool forgetsInstance)
    {
        long sequence = ++_sequence;
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Op(key, sequence, saveRecord, deleteKey)))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogError("creature respawn queue closed; write lost");
            return;
        }

        if (forgetsInstance)
        {
            _forgottenAt[key.InstanceId] = sequence;
        }
    }

    /// <summary>
    /// Attempt <paramref name="op"/> up to <see cref="MaxAttempts"/> times (once on a retained <paramref name="sweep"/>); null when it landed (or there is
    /// no store), else the last failure.
    /// </summary>
    private Task<Exception?> TryWriteAsync(Op op, bool sweep = false)
        => TryWriteAsync(store => op.ExecuteAsync(store), sweep);

    /// <summary>
    /// Retry the retained writes <paramref name="retry"/> (instance deletes first, then spawn writes, as ordered by the caller): each instance delete
    /// on its own, the spawn writes as coalesced batch calls of up to <see cref="MaxBatch"/> so a lone failure does not turn them back into one
    /// transaction each. Only when a batch fails (and not because the database is busy, which a per-key retry would wait out again, nor once the
    /// stop drain has stalled) are its keys retried one by one, which isolates a bad key from the good ones. A sweep stops after the first busy
    /// verdict, so the writes not yet tried are absent from the result.
    /// </summary>
    private async Task<List<(Op Op, Exception? Failure)>> RetryRetainedAsync(Op[] retry, bool sweep)
    {
        List<(Op Op, Exception? Failure)> results = [];
        List<Op> spawns = [];
        foreach (Op op in retry)
        {
            if (!op.Key.InstanceWide)
            {
                spawns.Add(op);
                continue;
            }

            Exception? failure = await TryWriteAsync(op, sweep).ConfigureAwait(false);
            results.Add((op, failure));
            if (sweep && failure is not null && DatabaseTransience.IsBusy(failure))
            {
                return results;
            }
        }

        foreach (Op[] chunk in spawns.Chunk(MaxBatch))
        {
            Exception? failure = await TryWriteAsync(store => SaveBatchAsync(store, chunk), sweep, chunk.Length).ConfigureAwait(false);
            bool busy = failure is not null && DatabaseTransience.IsBusy(failure);
            if (failure is null || chunk.Length == 1 || busy || (_stopping && _drain.Exhausted))
            {
                foreach (Op op in chunk)
                {
                    results.Add((op, failure));
                }

                if (sweep && busy)
                {
                    return results;
                }

                continue;
            }

            foreach (Op op in chunk)
            {
                Exception? single = await TryWriteAsync(op, sweep).ConfigureAwait(false);
                results.Add((op, single));
                if (sweep && single is not null && DatabaseTransience.IsBusy(single))
                {
                    return results;
                }
            }
        }

        return results;
    }

    /// <summary>The store call of a batch of spawn writes that were already coalesced by key (no key is in both lists).</summary>
    private static Task SaveBatchAsync(ICreatureRespawnStore store, IReadOnlyCollection<Op> batch)
    {
        List<CreatureRespawnRecord> upserts = [];
        List<CreatureRespawnKey> deletes = [];
        foreach (Op op in batch)
        {
            if (op.SaveRecord is { } record)
            {
                upserts.Add(record);
            }
            else if (op.DeleteKey is { } key)
            {
                deletes.Add(key);
            }
        }

        return store.SaveAsync(upserts, deletes);
    }

    private async Task<Exception?> TryWriteAsync(Func<ICreatureRespawnStore, Task> write, bool sweep = false, int keys = 1)
    {
        int attempts = sweep || _stopping ? 1 : MaxAttempts; // at stop: one attempt per write, busy or not, no inline retry
        for (int attempt = 1; ; attempt++)
        {
            if (attempt == 1 && _stopping && _drain.Refuse() is { } refused)
            {
                return refused; // shutdown drain only: nothing is attempted, the write stays retained
            }

            try
            {
                using StopDrainBudget.Attempt progress = _drain.BeginAttempt(); // the attempt is the store call: its verdict is in before the catch's backoff
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ICreatureRespawnStore>() is { } store)
                {
                    await write(store).ConfigureAwait(false);
                }

                progress.Succeeded();
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && attempt < attempts && !DatabaseTransience.IsBusy(ex))
            {
                ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("writequeue.respawn-attempt", ex);
                logger.LogWarning(ex, "creature respawn write failed (attempt {Attempt}); retrying", attempt);
                if (_stopping && _drain.Exhausted)
                {
                    return ex; // the lane has stalled: no retry, the write stays retained with its real failure
                }

                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (sweep)
                {
                    ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("writequeue.respawn-sweep", ex);
                }
                else
                {
                    ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("writequeue.respawn", ex);
                    logger.LogError(ex, "creature respawn write of {Keys} key(s) failed; retained for retry", keys);
                }

                return ex;
            }
        }
    }

    /// <summary>Under <see cref="_gate"/>, on the consumer: record the verdict of the write that settled <paramref name="op"/>'s key.</summary>
    private void Settle(Op op, Exception? failure)
    {
        if (failure is null)
        {
            _retained.Remove(op.Key); // the consumer is in order: this write is newer than anything retained for its key
        }
        else if (op.Key.InstanceWide || !_forgottenAt.TryGetValue(op.Key.InstanceId, out long forgotten) || forgotten < op.Sequence)
        {
            _retained[op.Key] = new Retained(op, failure); // a spawn write older than its instance's delete is moot: that delete removes the row
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            ChannelReader<Op> reader = _channel.Reader;
            Op? carried = null; // the op that ended a batch (a sweep marker or an instance delete): handled next, after the batch
            while (true)
            {
                Op op;
                if (carried is not null)
                {
                    op = carried;
                    carried = null;
                }
                else if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    break; // completed and drained
                }
                else if (!reader.TryRead(out Op? read))
                {
                    continue;
                }
                else
                {
                    op = read;
                }

                if (op.IsSweep)
                {
                    try
                    {
                        await SweepRetainedAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _sweepQueued, 0); // a write retained during this sweep is caught by the next marker
                    }

                    continue; // not a counted write
                }

                if (op.Key.InstanceWide)
                {
                    Exception? deleteFailure = await TryWriteAsync(op).ConfigureAwait(false);
                    lock (_gate)
                    {
                        Settle(op, deleteFailure);
                    }

                    Interlocked.Decrement(ref _pending);
                    continue;
                }

                // Drain what is already queued (never waiting for more) into one store call. The last write of a spawn wins; an instance
                // delete or a sweep marker ends the batch, so order around it is kept.
                Dictionary<OpKey, Op> latest = new() { [op.Key] = op };
                int consumed = 1;
                while (consumed < MaxBatch && reader.TryRead(out Op? next))
                {
                    if (next.IsSweep || next.Key.InstanceWide)
                    {
                        carried = next;
                        break;
                    }

                    latest[next.Key] = next;
                    consumed++;
                }

                List<Op> batch = [.. latest.Values];
                Exception? failure = await TryWriteAsync(store => SaveBatchAsync(store, batch), keys: batch.Count).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (Op settled in batch)
                    {
                        Settle(settled, failure);
                    }
                }

                Interlocked.Add(ref _pending, -consumed);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("writequeue.respawn-consume", ex);
            logger.LogError(ex, "creature respawn queue consumer loop faulted");
        }
    }
}
