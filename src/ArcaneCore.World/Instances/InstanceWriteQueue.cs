using System.Threading.Channels;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Persists instance changes made on the world thread, off the world thread and in order (one
/// consumer, like <see cref="Social.SocialWriteQueue"/>). Each write is retried a few times,
/// then logged and dropped. Without a registered <see cref="IInstanceStore"/> writes are discarded.
/// </summary>
public sealed class InstanceWriteQueue(IServiceScopeFactory scopes, ILogger logger) : IInstancePersistence
{
    private const int MaxAttempts = 3;

    private readonly Channel<Func<IInstanceStore, Task>> _channel = Channel.CreateUnbounded<Func<IInstanceStore, Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _consumer;
    private int _pending;
    private long _enqueued;
    private long _attempted;

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void InstanceSaved(InstanceSave save)
    {
        var record = new InstanceRecord(save.InstanceId, save.MapId, save.ResetTime);
        Enqueue(store => store.SaveInstanceAsync(record));
    }

    /// <summary>
    /// Raised on the calling (world) thread for every instance whose deletion is queued, before it is queued: a deleted save, and also the saves
    /// the manager drops while it loads (which do not reach <c>InstanceManager.InstanceDeleted</c>).
    /// </summary>
    public event Action<uint>? InstanceRemoved;

    public void InstanceDeleted(uint instanceId)
    {
        InstanceRemoved?.Invoke(instanceId);
        Enqueue(store => store.DeleteInstanceAsync(instanceId));
    }

    public void PlayerBound(uint characterId, uint instanceId, bool permanent)
        => Enqueue(store => store.SaveBindAsync(new CharacterInstanceBindRecord((int)characterId, instanceId, permanent)));

    public void PlayerUnbound(uint characterId, uint instanceId) => Enqueue(store => store.DeleteBindAsync((int)characterId, instanceId));

    public void RaidResetTimeChanged(uint mapId, long resetTime) => Enqueue(store => store.SaveResetTimeAsync(new InstanceResetRecord(mapId, resetTime)));

    public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId)
        => Enqueue(store => store.SaveLastInstanceAsync(new CharacterLastInstanceRecord((int)characterId, mapId, instanceId)));

    /// <summary>A stored group bind (vmangos <c>group_instance</c>), under the leader's character id.</summary>
    public void GroupBound(uint leaderCharacterId, uint instanceId, bool permanent)
        => Enqueue(store => store.SaveGroupBindAsync(new GroupInstanceBindRecord((int)leaderCharacterId, instanceId, permanent)));

    public void GroupUnbound(uint leaderCharacterId, uint instanceId) => Enqueue(store => store.DeleteGroupBindAsync((int)leaderCharacterId, instanceId));

    /// <summary>Delete a character's binds and last instance (queued by the character delete hook).</summary>
    public void CharacterDeleted(int characterId) => Enqueue(store => store.DeleteCharacterAsync(characterId));

    /// <summary>The number of writes queued so far: a watermark for <see cref="WaitForAsync"/>.</summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>
    /// Wait until the first <paramref name="watermark"/> writes (<see cref="Enqueued"/> read earlier)
    /// have been attempted, whatever is queued after them. Unlike <see cref="FlushAsync"/> a steady
    /// stream of later writes cannot starve it, and it honours cancellation.
    /// </summary>
    public async Task WaitForAsync(long watermark, CancellationToken cancellationToken)
    {
        while (Interlocked.Read(ref _attempted) < watermark)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Wait until every write queued so far has been attempted (tests).</summary>
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

    private void Enqueue(Func<IInstanceStore, Task> write)
    {
        Interlocked.Increment(ref _pending);
        Interlocked.Increment(ref _enqueued);
        if (!_channel.Writer.TryWrite(write))
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Decrement(ref _enqueued);
            logger.LogError("instance write queue closed; write lost");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Func<IInstanceStore, Task> write in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    if (scope.ServiceProvider.GetService<IInstanceStore>() is { } store)
                    {
                        await write(store).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "instance write failed (attempt {Attempt}); retrying", attempt);
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "instance write failed; lost");
                }
            }

            Interlocked.Increment(ref _attempted);
            Interlocked.Decrement(ref _pending);
        }
    }
}
