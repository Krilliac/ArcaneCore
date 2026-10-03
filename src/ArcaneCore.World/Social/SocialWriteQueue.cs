using System.Threading.Channels;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>
/// Persists social writes made on the world thread, off the world thread and in order (one
/// consumer, like <see cref="Persistence.CharacterSaveQueue"/>), so a guild's snapshots never
/// overtake each other. Each write is retried a few times, then logged and dropped.
/// </summary>
public sealed class SocialWriteQueue(IServiceScopeFactory scopes, ILogger logger) : ISocialPersistence, IPetitionPersistence
{
    private const int MaxAttempts = 3;

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _consumer;
    private int _pending;

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void SetSocial(int characterId, int otherId, SocialFlags flags)
        => EnqueueSocial(store => store.SetSocialAsync(characterId, otherId, flags));

    public void SaveGuild(GuildData guild) => EnqueueSocial(store => store.SaveGuildAsync(guild));

    public void DeleteGuild(int guildId) => EnqueueSocial(store => store.DeleteGuildAsync(guildId));

    /// <summary>The social rows and, after them in the same order, the petition rows of a deleted character.</summary>
    public void PurgeCharacter(int characterId)
    {
        EnqueueSocial(store => store.PurgeCharacterAsync(characterId));
        EnqueuePetitions(store => store.PurgeCharacterAsync(characterId));
    }

    public void SavePetition(PetitionData petition) => EnqueuePetitions(store => store.SavePetitionAsync(petition));

    public void DeletePetition(int petitionId) => EnqueuePetitions(store => store.DeletePetitionAsync(petitionId));

    public void CompletePetition(GuildData guild, int petitionId) => EnqueuePetitions(store => store.CompletePetitionAsync(guild, petitionId));

    /// <summary>Completes once every write queued before the call has been attempted (failures were logged and dropped).</summary>
    public Task FlushAsync()
    {
        if (_consumer is null)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _channel.Writer.TryWrite(new Work(null, done)) ? done.Task : Task.CompletedTask;
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

    private void EnqueueSocial(Func<ISocialStore, Task> write)
        => Enqueue(services => services.GetService<ISocialStore>() is { } store ? write(store) : Task.CompletedTask);

    private void EnqueuePetitions(Func<IPetitionStore, Task> write)
        => Enqueue(services => services.GetService<IPetitionStore>() is { } store ? write(store) : Task.CompletedTask);

    private void Enqueue(Func<IServiceProvider, Task> write)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Work(write, null)))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogError("social write queue closed; write lost");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Work work in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Write is not { } write)
            {
                work.Done?.TrySetResult();
                continue;
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await write(scope.ServiceProvider).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "social write failed (attempt {Attempt}); retrying", attempt);
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "social write failed; lost");
                }
            }

            Interlocked.Decrement(ref _pending);
        }
    }

    private sealed record Work(Func<IServiceProvider, Task>? Write, TaskCompletionSource? Done);
}
