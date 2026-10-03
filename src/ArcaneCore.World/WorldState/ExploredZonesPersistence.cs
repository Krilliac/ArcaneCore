using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Explored zones across logins (docs/areas/world-state.md): loaded while the character loads
/// (vmangos <c>_LoadIntoDataField(PLAYER_EXPLORED_ZONES_1)</c>, Player.cpp:14648), written when
/// the words change (a discovery) and when the character leaves the world (Player.cpp:16481). The
/// load fails closed: a malformed row, or a character whose earlier write is still not durable, fails
/// the login instead of showing a re-explored map. With no <see cref="IExploredZonesStore"/>
/// registered the words live in memory only.
/// </summary>
public sealed class ExploredZonesPersistence(IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, ICharacterHooks, IExploredZonesSink
{
    private readonly ExploredZonesWriteQueue _writes = new(scopes, loggers.CreateLogger<ExploredZonesWriteQueue>());

    public ExploredZonesWriteQueue Writes => _writes;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _writes.Start();
        WorldStateHooks.For(world).ExploredZonesSink = this;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
    }

    /// <summary>A change in the player's words: persist the current state (world thread).</summary>
    public void Changed(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _writes.Save((int)player.Guid.Low, ExploredZonesFields.Read(player));
    }

    private void OnPlayerLoggingOut(Player player)
    {
        int characterId = (int)player.Guid.Low;
        _writes.Save(characterId, ExploredZonesFields.Read(player));
        if (_writes.HasRetainedFailure(characterId))
        {
            _writes.RequestRetry(characterId);
        }
    }

    /// <summary>Clear a row left by a deleted character whose id is being reused (after older writes drained).</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        await _writes.FlushAsync().ConfigureAwait(false);
        _writes.Forget(character.Id);
        if (session.Services.GetService<IExploredZonesStore>() is { } store)
        {
            await store.DeleteAsync(character.Id).ConfigureAwait(false);
        }
    }

    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        await _writes.FlushCharacterAsync(character.Id).ConfigureAwait(false); // faults while an earlier write is not durable
        if (session.Services.GetService<IExploredZonesStore>() is { } store
            && await store.LoadAsync(character.Id).ConfigureAwait(false) is { } words)
        {
            ExploredZonesFields.Write(player, words);
        }
    }

    public Task FlushAsync() => _writes.FlushAsync();

    public Task StopAsync() => _writes.StopAsync();
}

/// <summary>Character deletion for explored zones: queued writes drain before the row is removed, and nothing retained can resurrect it.</summary>
public sealed class ExploredZonesDeleteHook(ExploredZonesPersistence persistence) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => persistence.FlushAsync();

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        persistence.Writes.Forget(character.Id);
        await persistence.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
        persistence.Writes.Forget(character.Id);
    }
}
