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

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void InstanceSaved(InstanceSave save)
    {
        var record = new InstanceRecord(save.InstanceId, save.MapId, save.ResetTime);
        Enqueue(store => store.SaveInstanceAsync(record));
    }

    public void InstanceDeleted(uint instanceId) => Enqueue(store => store.DeleteInstanceAsync(instanceId));

    public void PlayerBound(uint characterId, uint instanceId, bool permanent)
        => Enqueue(store => store.SaveBindAsync(new CharacterInstanceBindRecord((int)characterId, instanceId, permanent)));

    public void PlayerUnbound(uint characterId, uint instanceId) => Enqueue(store => store.DeleteBindAsync((int)characterId, instanceId));

    public void RaidResetTimeChanged(uint mapId, long resetTime) => Enqueue(store => store.SaveResetTimeAsync(new InstanceResetRecord(mapId, resetTime)));

    public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId)
        => Enqueue(store => store.SaveLastInstanceAsync(new CharacterLastInstanceRecord((int)characterId, mapId, instanceId)));

    /// <summary>Delete a character's binds and last instance (queued by the character delete hook).</summary>
    public void CharacterDeleted(int characterId) => Enqueue(store => store.DeleteCharacterAsync(characterId));

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
        if (!_channel.Writer.TryWrite(write))
        {
            Interlocked.Decrement(ref _pending);
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

            Interlocked.Decrement(ref _pending);
        }
    }
}
