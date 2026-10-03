using System.Threading.Channels;
using ArcaneCore.Kernel.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Persists reputation writes made on the world thread, off it and in order (one consumer, as
/// <see cref="Social.SocialWriteQueue"/>). <see cref="FlushAsync"/> is the barrier a login or a
/// character creation waits on so it never reads rows an older write is about to replace.
/// Each write is retried a few times, then logged and dropped.
/// </summary>
public sealed class ReputationWriteQueue(IServiceScopeFactory scopes, ILogger logger)
{
    private const int MaxAttempts = 3;

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _consumer;
    private int _pending;

    /// <summary>Writes queued or in progress.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void SaveFactions(int characterId, IReadOnlyList<CharacterReputationRow> rows)
    {
        CharacterReputationRow[] copy = [.. rows];
        Enqueue(store => store.SaveFactionsAsync(characterId, copy));
    }

    public void SaveWatchedFaction(int characterId, int watched) => Enqueue(store => store.SaveWatchedFactionAsync(characterId, watched));

    public void DeleteCharacter(int characterId) => Enqueue(store => store.DeleteCharacterAsync(characterId));

    /// <summary>Completes once every write queued before the call has been attempted.</summary>
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

    private void Enqueue(Func<ICharacterReputationStore, Task> write)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Work(write, null)))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogError("reputation write queue closed; write lost");
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Work work in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (work.Write is null)
            {
                work.Done?.TrySetResult();
                continue;
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    if (scope.ServiceProvider.GetService<ICharacterReputationStore>() is { } store)
                    {
                        await work.Write(store).ConfigureAwait(false);
                    }

                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "reputation write failed (attempt {Attempt}); retrying", attempt);
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "reputation write failed; lost");
                }
            }

            Interlocked.Decrement(ref _pending);
        }
    }

    private sealed record Work(Func<ICharacterReputationStore, Task>? Write, TaskCompletionSource? Done);
}
