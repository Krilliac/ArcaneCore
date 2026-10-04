using System.Threading.Channels;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
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
public sealed class CreatureRespawnQueue(IServiceScopeFactory scopes, ILogger logger) : ICreatureRespawnPersistence
{
    private const int MaxAttempts = 3;

    private readonly Dictionary<(uint Map, uint Instance), Dictionary<uint, long>> _times = [];
    private readonly Channel<Op> _channel = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<OpKey, Retained> _retained = [];
    private readonly Dictionary<uint, long> _forgottenAt = [];
    private long _sequence;
    private Task? _consumer;
    private int _pending;

    /// <summary>One queued store write. A spawn write names its key; an instance delete has <see cref="OpKey.SpawnGuid"/> 0 and <see cref="OpKey.InstanceWide"/> set.</summary>
    private sealed record Op(OpKey Key, long Sequence, Func<ICreatureRespawnStore, Task> Write);

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

    /// <summary>Put the stored rows into the in-memory copy (before the world thread runs).</summary>
    public void LoadInitial(IEnumerable<CreatureRespawnRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (CreatureRespawnRecord record in records)
        {
            For(record.MapId, record.InstanceId)[record.SpawnGuid] = record.RespawnTime;
        }
    }

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId)
        => _times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) ? new Dictionary<uint, long>(times) : new Dictionary<uint, long>();

    public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
    {
        For(mapId, instanceId)[spawnGuid] = respawnUnixSeconds;
        var record = new CreatureRespawnRecord(mapId, instanceId, spawnGuid, respawnUnixSeconds);
        Enqueue(new OpKey(instanceId, spawnGuid, false), store => store.SaveAsync([record], []));
    }

    public void Delete(uint mapId, uint instanceId, uint spawnGuid)
    {
        if (_times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) && times.Remove(spawnGuid) && times.Count == 0)
        {
            _times.Remove((mapId, instanceId));
        }

        var key = new CreatureRespawnKey(instanceId, spawnGuid);
        Enqueue(new OpKey(instanceId, spawnGuid, false), store => store.SaveAsync([], [key]));
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

            EnqueueCore(new OpKey(instanceId, 0, true), store => store.DeleteInstanceAsync(instanceId), forgetsInstance: true);
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

        foreach (Op op in retry) // instance deletes first: a spawn write retained for the same instance is newer than it
        {
            Exception? failure = await TryWriteAsync(op).ConfigureAwait(false);
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

    private void Enqueue(OpKey key, Func<ICreatureRespawnStore, Task> write)
    {
        lock (_gate)
        {
            EnqueueCore(key, write, forgetsInstance: false);
        }
    }

    /// <summary>Under <see cref="_gate"/>: number the write and queue it; a write numbered after an instance delete belongs to the instance's next life.</summary>
    private void EnqueueCore(OpKey key, Func<ICreatureRespawnStore, Task> write, bool forgetsInstance)
    {
        long sequence = ++_sequence;
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Op(key, sequence, write)))
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

    /// <summary>Attempt <paramref name="op"/> up to <see cref="MaxAttempts"/> times; null when it landed (or there is no store), else the last failure.</summary>
    private async Task<Exception?> TryWriteAsync(Op op)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ICreatureRespawnStore>() is { } store)
                {
                    await op.Write(store).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "creature respawn write failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "creature respawn write failed; retained for retry");
                return ex;
            }
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Op op in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Exception? failure = await TryWriteAsync(op).ConfigureAwait(false);
            lock (_gate)
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

            Interlocked.Decrement(ref _pending);
        }
    }
}
