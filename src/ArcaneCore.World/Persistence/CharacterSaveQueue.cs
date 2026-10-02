using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Persists character snapshots taken on the world thread, off the world thread, in the
/// order they were taken (one consumer), so a character's saves never overtake each other.
/// </summary>
public sealed class CharacterSaveQueue(IServiceScopeFactory scopes, ILogger<CharacterSaveQueue> logger) : ICharacterSaveQueue
{
    private const int MaxAttempts = 3;

    private readonly Channel<CharacterState> _channel = Channel.CreateUnbounded<CharacterState>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _consumer;
    private int _pending;

    /// <summary>Snapshots queued or being written.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start() => _consumer ??= Task.Run(ConsumeAsync);

    public void Enqueue(CharacterState state)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(state))
        {
            Interlocked.Decrement(ref _pending);
            logger.LogError("save queue closed; character {Id} state lost", state.Id);
        }
    }

    /// <summary>Stop accepting snapshots and wait until every queued one is written.</summary>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (CharacterState state in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
                    await store.SaveStateAsync(state).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.LogWarning(ex, "saving character {Id} failed (attempt {Attempt}); retrying", state.Id, attempt);
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "saving character {Id} failed; state lost", state.Id);
                }
            }

            Interlocked.Decrement(ref _pending);
        }
    }
}
