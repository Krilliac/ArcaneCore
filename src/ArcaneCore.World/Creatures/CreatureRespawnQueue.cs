using System.Threading.Channels;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// The world's <see cref="ICreatureRespawnPersistence"/>: reads answer from memory (loaded once at start), and every change updates that copy and
/// is written by one background consumer, in order, off the world thread (like <see cref="Instances.InstanceWriteQueue"/>). A write is retried a
/// few times, then logged and dropped. Without a registered <see cref="ICreatureRespawnStore"/> writes are discarded.
/// <para>
/// The in-memory copy is touched by the world thread only (after <see cref="LoadInitial"/> at start); the consumer works from the queued
/// operations, never from the copy.
/// </para>
/// </summary>
public sealed class CreatureRespawnQueue(IServiceScopeFactory scopes, ILogger logger) : ICreatureRespawnPersistence
{
    private const int MaxAttempts = 3;

    private readonly Dictionary<(uint Map, uint Instance), Dictionary<uint, long>> _times = [];
    private readonly Channel<Func<ICreatureRespawnStore, Task>> _channel = Channel.CreateUnbounded<Func<ICreatureRespawnStore, Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _consumer;
    private int _pending;

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

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
        Enqueue(store => store.SaveAsync([record], []));
    }

    public void Delete(uint mapId, uint instanceId, uint spawnGuid)
    {
        if (_times.TryGetValue((mapId, instanceId), out Dictionary<uint, long>? times) && times.Remove(spawnGuid) && times.Count == 0)
        {
            _times.Remove((mapId, instanceId));
        }

        var key = new CreatureRespawnKey(instanceId, spawnGuid);
        Enqueue(store => store.SaveAsync([], [key]));
    }

    /// <summary>Wait until every write queued so far has been attempted (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        while (Pending > 0)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>Stop accepting writes and wait until every queued one is done.</summary>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
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

    private void Enqueue(Func<ICreatureRespawnStore, Task> write)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(write))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogError("creature respawn queue closed; write lost");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Func<ICreatureRespawnStore, Task> write in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    if (scope.ServiceProvider.GetService<ICreatureRespawnStore>() is { } store)
                    {
                        await write(store).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "creature respawn write failed (attempt {Attempt}); retrying", attempt);
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "creature respawn write failed; lost");
                }
            }

            Interlocked.Decrement(ref _pending);
        }
    }
}
