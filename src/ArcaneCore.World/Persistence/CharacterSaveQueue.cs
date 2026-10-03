using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
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

    private readonly Lock _gate = new();
    private readonly Dictionary<int, FailedSave> _failed = [];
    private readonly HashSet<int> _held = [];
    private readonly HashSet<int> _quarantined = [];
    private readonly Channel<PendingWrite> _channel = Channel.CreateUnbounded<PendingWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private Task? _consumer;
    private int _pending;
    private bool _stopped;

    /// <summary>Snapshots queued or being written.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _consumer ??= Task.Run(ConsumeAsync);
        }
    }

    public void Enqueue(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_held.Contains(state.Id) || _quarantined.Contains(state.Id))
            {
                return;
            }

            if (_stopped)
            {
                logger.LogError("save queue closed; character {Id} state not accepted", state.Id);
                return;
            }

            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(new PendingWrite(state.Id, Copy(state), null, default)))
            {
                Interlocked.Decrement(ref _pending);
                throw new InvalidOperationException("the character save queue is closed");
            }
        }
    }

    /// <summary>Suppress new ordinary snapshots; snapshots queued before the hold still drain.</summary>
    public void HoldCharacter(int characterId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _held.Add(characterId);
        }
    }

    /// <summary>
    /// Save the captured pre-settlement snapshot after earlier writes, bypassing a hold.
    /// The returned task observes the entire write, including cooperative cancellation.
    /// Quarantine always refuses the snapshot.
    /// </summary>
    public Task SaveForSettlementAsync(CharacterState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_quarantined.Contains(state.Id))
            {
                throw new InvalidOperationException($"character {state.Id} is quarantined");
            }

            _consumer ??= Task.Run(ConsumeAsync);
            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(new PendingWrite(state.Id, Copy(state), done, cancellationToken)))
            {
                Interlocked.Decrement(ref _pending);
                throw new InvalidOperationException("the character save queue is closed");
            }
        }

        return done.Task;
    }

    /// <summary>Drain earlier snapshots in order and retry this character's retained failure. Cancellation never removes retained state.</summary>
    public Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _consumer ??= Task.Run(ConsumeAsync);
            if (!_channel.Writer.TryWrite(new PendingWrite(characterId, null, done, cancellationToken)))
            {
                throw new InvalidOperationException("the character save queue is closed");
            }
        }

        return done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Suppress new and still-queued snapshots until authoritative loading explicitly resumes the character.</summary>
    public void QuarantineCharacter(int characterId)
    {
        lock (_gate)
        {
            _quarantined.Add(characterId);
        }
    }

    public void ResumeCharacter(int characterId)
    {
        lock (_gate)
        {
            _held.Remove(characterId);
            if (_quarantined.Remove(characterId))
            {
                // Authoritative publication/loading supersedes any retained old-session state.
                _failed.Remove(characterId);
            }
        }
    }

    /// <summary>
    /// The character was deleted: forget its holds, quarantine and retained failed snapshot, so a
    /// later character that reuses the id never inherits them. Call after its writes drained.
    /// </summary>
    public void ForgetCharacter(int characterId)
    {
        lock (_gate)
        {
            _held.Remove(characterId);
            _quarantined.Remove(characterId);
            _failed.Remove(characterId);
        }
    }

    public bool IsHeld(int characterId)
    {
        lock (_gate)
        {
            return _held.Contains(characterId);
        }
    }

    public bool IsQuarantined(int characterId)
    {
        lock (_gate)
        {
            return _quarantined.Contains(characterId);
        }
    }

    /// <summary>Stop accepting snapshots and wait until every queued one is written.</summary>
    public async Task StopAsync()
    {
        Task consumer;
        lock (_gate)
        {
            _stopped = true;
            _channel.Writer.TryComplete();
            consumer = _consumer ??= Task.Run(ConsumeAsync);
        }

        await consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (PendingWrite write in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (write.State is { } state)
                {
                    lock (_gate)
                    {
                        if (_quarantined.Contains(write.CharacterId))
                        {
                            write.Done?.TrySetException(new InvalidOperationException($"character {write.CharacterId} is quarantined"));
                            continue;
                        }

                        if (_failed.TryGetValue(write.CharacterId, out FailedSave? earlier))
                        {
                            state = Merge(state, earlier.State);
                        }
                    }

                    await SaveRetainingFailureAsync(state, write.CancellationToken, write.Done is not null).ConfigureAwait(false);
                }
                else
                {
                    await RecoverAsync(write.CharacterId, write.CancellationToken).ConfigureAwait(false);
                }

                write.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                write.Done?.TrySetException(ex);
            }
            finally
            {
                if (write.State is not null)
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }

        int[] failed;
        lock (_gate)
        {
            failed = _failed.Keys.ToArray();
        }

        foreach (int id in failed)
        {
            try
            {
                await RecoverAsync(id, default).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // SaveRetainingFailureAsync keeps the snapshot and original failure below.
            }
        }

        lock (_gate)
        {
            if (_failed.Count > 0)
            {
                throw new InvalidOperationException($"character saves did not drain for characters {string.Join(", ", _failed.Keys.Order())}",
                    _failed.Values.First().Failure);
            }
        }
    }

    private async Task RecoverAsync(int id, CancellationToken cancellationToken)
    {
        CharacterState? retained;
        lock (_gate)
        {
            // A quarantined barrier is an ordered drain only. Fresh login must load
            // authoritative storage before the owner explicitly resumes this ID.
            if (_quarantined.Contains(id))
            {
                return;
            }

            retained = _failed.GetValueOrDefault(id)?.State;
        }

        if (retained is not null)
        {
            await SaveRetainingFailureAsync(retained, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveRetainingFailureAsync(CharacterState state, CancellationToken cancellationToken, bool trustedSettlement = false)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lock (_gate)
            {
                if (_quarantined.Contains(state.Id))
                {
                    if (trustedSettlement)
                    {
                        throw new InvalidOperationException($"character {state.Id} is quarantined");
                    }

                    return;
                }
            }

            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
                await store.SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _failed.Remove(state.Id);
                }

                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _failed[state.Id] = new FailedSave(state, ex);
                }

                logger.LogWarning(ex, "saving character {Id} failed (attempt {Attempt}); retrying", state.Id, attempt);
                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failed[state.Id] = new FailedSave(state, ex);
                }

                logger.LogError(ex, "saving character {Id} failed; authoritative snapshot retained", state.Id);
                throw;
            }
        }
    }

    private static CharacterState Merge(CharacterState newer, CharacterState failed) => newer with
    {
        ActionButtons = newer.ActionButtons ?? failed.ActionButtons,
        Home = newer.Home ?? failed.Home,
        Inventory = newer.Inventory ?? failed.Inventory,
        Life = newer.Life ?? failed.Life, // always complete when present, so the newest one wins
    };

    private static CharacterState Copy(CharacterState state) => state with
    {
        ActionButtons = state.ActionButtons is { } buttons ? Array.AsReadOnly(buttons.ToArray()) : null,
        Inventory = state.Inventory is { } inventory ? new InventorySnapshot(Array.AsReadOnly(inventory.Items.Select(row => row with
        {
            Item = row.Item with
            {
                Charges = Array.AsReadOnly(row.Item.Charges.ToArray()),
                Enchantments = Array.AsReadOnly(row.Item.Enchantments.ToArray()),
            },
        }).ToArray()), inventory.AmmoId) : null,
    };

    private sealed record FailedSave(CharacterState State, Exception Failure);
    private sealed record PendingWrite(int CharacterId, CharacterState? State, TaskCompletionSource? Done, CancellationToken CancellationToken);
}
