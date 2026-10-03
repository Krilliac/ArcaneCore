using ArcaneCore.Data.Characters.Ranged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ranged;

/// <summary>
/// Write-through persistence of PLAYER_AMMO_ID (vmangos saves <c>characters.ammo_id</c> with the
/// character, Player.cpp:16501; re-implemented as its own small write). Writes run off the world
/// thread, one chain per character so a quick relog waits for its own write; a value whose write
/// failed (or that has no store to go to) stays in memory and a later login of that character in
/// this process still restores it.
/// </summary>
public sealed class AmmoPersistence
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<int, Task> _chains = [];
    private readonly Dictionary<int, (uint Item, long Version)> _unsaved = [];
    private long _version;

    public AmmoPersistence(IServiceScopeFactory? scopes, ILogger logger)
    {
        _scopes = scopes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Queue a write of the ammo of one character (world thread; never blocks).</summary>
    public void Save(int characterId, uint item)
    {
        lock (_lock)
        {
            long version = ++_version;
            _unsaved[characterId] = (item, version);
            Task previous = _chains.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _chains[characterId] = previous.ContinueWith(_ => WriteAsync(characterId, item, version),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// The ammo of one character for login (session task): waits for that character's queued
    /// writes, then prefers a value that has not reached storage over the stored one.
    /// </summary>
    public async Task<uint> LoadAsync(int characterId, ICharacterAmmoStore? store)
    {
        await FlushCharacterAsync(characterId).ConfigureAwait(false);
        lock (_lock)
        {
            if (_unsaved.TryGetValue(characterId, out (uint Item, long Version) pending))
            {
                return pending.Item;
            }
        }

        return store is null ? 0u : await store.GetAsync(characterId).ConfigureAwait(false);
    }

    /// <summary>Wait for the queued writes of one character.</summary>
    public async Task FlushCharacterAsync(int characterId)
    {
        Task? chain;
        lock (_lock)
        {
            chain = _chains.GetValueOrDefault(characterId);
        }

        if (chain is not null)
        {
            await chain.ConfigureAwait(false);
        }
    }

    /// <summary>Wait for every queued write (shutdown, tests).</summary>
    public async Task FlushAsync()
    {
        Task[] chains;
        lock (_lock)
        {
            chains = [.. _chains.Values];
        }

        await Task.WhenAll(chains).ConfigureAwait(false);
    }

    /// <summary>Whether a character has a value that has not reached storage yet.</summary>
    public bool HasUnsaved(int characterId)
    {
        lock (_lock)
        {
            return _unsaved.ContainsKey(characterId);
        }
    }

    /// <summary>Drop a deleted character's ammo (memory and, conditionally, the table).</summary>
    public void DeleteCharacter(int characterId)
    {
        lock (_lock)
        {
            _unsaved.Remove(characterId);
            if (_scopes is null)
            {
                return;
            }

            Task previous = _chains.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _chains[characterId] = previous.ContinueWith(_ => DeleteAsync(characterId),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task WriteAsync(int characterId, uint item, long version)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ICharacterAmmoStore>() is not { } store)
            {
                return; // no store: the value lives in memory for this process only
            }

            await store.SetAsync(characterId, item).ConfigureAwait(false);
            lock (_lock)
            {
                if (_unsaved.TryGetValue(characterId, out (uint Item, long Version) current) && current.Version == version)
                {
                    _unsaved.Remove(characterId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the ammo of character {Character} failed; kept in memory", characterId);
        }
    }

    private async Task DeleteAsync(int characterId)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ICharacterAmmoStore>() is { } store)
            {
                await store.DeleteCharacterAsync(characterId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting the ammo of character {Character} failed", characterId);
        }
    }
}
