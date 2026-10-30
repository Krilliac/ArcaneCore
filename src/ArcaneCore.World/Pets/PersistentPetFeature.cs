using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

public sealed class PersistentPetFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<PersistentPetFeature> logger)
    : IWorldFeature, ICharacterHooks, ICharacterDeleteHook
{
    private readonly Lock _pendingGate = new();
    // Weak keys: a login that aborts after the loading hooks (a later hook throws, TryBeginLogin/EnterWorld refuse) never raises
    // PlayerLoggedIn, so nothing removes its entry; the table must not keep that Player (and its session) alive. Same idiom as SpellFeature._staged.
    private readonly ConditionalWeakTable<Player, LoadedPet> _pending = [];
    private PetsFeature? _pets;
    private WorldRuntime? _world;
    private Action<Player>? _onLoggedIn;
    private Action<Player>? _onLoggingOut;

    public void Attach(WorldRuntime world)
    {
        using IServiceScope probe = scopes.CreateScope();
        IPersistentPetStore? configuredStore = probe.ServiceProvider.GetService<IPersistentPetStore>();
        if (configuredStore is null || services.GetService<PetsFeature>() is not { } pets) return;
        _pets = pets;
        pets.Service.DetachedPersistenceConfigured = true;
        pets.Service.DetachedPersistenceSupported = configuredStore.SupportsDetachedState;
        pets.Service.LoadPersistence = async (id, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>().LoadCurrentAsync(id, ct).ConfigureAwait(false);
        };
        pets.Service.LoadCallablePersistence = async (id, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>().LoadCallableAsync(id, ct).ConfigureAwait(false);
        };
        pets.Service.SavePersistence = async (snapshot, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>().SaveCurrentAsync(snapshot, ct).ConfigureAwait(false);
        };
        pets.Service.SaveDetachedPersistence = async (snapshot, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>().SaveDetachedAsync(snapshot, ct).ConfigureAwait(false);
        };
        pets.Service.DeletePersistence = async (id, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>().DeleteAsync(id, ct).ConfigureAwait(false);
        };
        _world = world;
        _onLoggedIn = player => PublishLoaded(world, player, pets);
        _onLoggingOut = player => _ = SaveAsync(player, pets);
        world.PlayerLoggedIn += _onLoggedIn;
        world.PlayerLoggingOut += _onLoggingOut;
    }

    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (_pets is null) return;
        if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            await saves.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        await _pets.Service.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        LoadedPet loaded = await ReadAsync(player, _pets).ConfigureAwait(false);
        lock (_pendingGate) _pending.AddOrUpdate(player, loaded);
    }

    /// <summary>The stored pet with the pet generation it was read at (taken before the read, so any change during it shows).</summary>
    private static async Task<LoadedPet> ReadAsync(Player player, PetsFeature pets)
    {
        long generation = pets.Service.PetGeneration((int)player.Guid.Low);
        PersistentPetSnapshot? snapshot = await pets.Service.ReadCurrentPetAsync(player).ConfigureAwait(false);
        if (snapshot is null)
        {
            snapshot = await pets.Service.ReadCallablePetAsync(player).ConfigureAwait(false);
        }
        return new LoadedPet(snapshot, generation);
    }

    private void PublishLoaded(WorldRuntime world, Player player, PetsFeature pets)
    {
        LoadedPet? loaded;
        lock (_pendingGate) { if (!_pending.TryGetValue(player, out loaded)) return; _pending.Remove(player); }
        Publish(world, player, pets, loaded);
    }

    /// <summary>
    /// World thread. A GM .pet rename/.pet delete may have run between the login's read and its world entry (the owner was not online yet to
    /// refuse it); then the read is stale and restoring it would bring the old pet back and save it over the change at logout, so read again.
    /// </summary>
    private void Publish(WorldRuntime world, Player player, PetsFeature pets, LoadedPet loaded)
    {
        if (!player.IsInWorld) return;
        if (!pets.Service.IsPetGenerationCurrent((int)player.Guid.Low, loaded.Generation))
        {
            _ = ReloadAsync(world, player, pets);
            return;
        }
        if (loaded.Snapshot is { } snapshot && player.Class == ArcaneCore.Game.Class.Hunter
            && player.PetGuid.IsEmpty && !player.IsQuestSettlementPending)
            pets.Service.RestoreCurrentPet(player, snapshot);
    }

    private async Task ReloadAsync(WorldRuntime world, Player player, PetsFeature pets)
    {
        try
        {
            await pets.Service.WaitForStoredPetChangesAsync((int)player.Guid.Low).ConfigureAwait(false);
            LoadedPet loaded = await ReadAsync(player, pets).ConfigureAwait(false);
            world.Post(() => Publish(world, player, pets, loaded));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.reload", ex);
            AnomalyLedger.Shared.Report("pet", "save-failed", $"character {player.Guid.Low}", $"reload error={ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "rereading the persistent pet of {CharacterId} after a stored pet change failed", player.Guid.Low);
        }
    }

    private async Task SaveAsync(Player player, PetsFeature pets)
    {
        try { await pets.Service.SaveCurrentPetAsync(player).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.save", ex);
            AnomalyLedger.Shared.Report("pet", "save-failed", $"character {player.Guid.Low}", $"save error={ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "persistent pet save failed for {CharacterId}", player.Guid.Low);
        }
    }

    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        if (_pets is not null) await _pets.Service.FlushCharacterAsync(character.Id).ConfigureAwait(false);
    }

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        lock (_pendingGate) foreach (Player player in _pending.Select(e => e.Key).Where(p => p.Guid.Low == (uint)character.Id).ToArray()) _pending.Remove(player);
        _pets?.Service.ForgetCharacter(character.Id);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_world is { } world)
        {
            world.PlayerLoggedIn -= _onLoggedIn;
            world.PlayerLoggingOut -= _onLoggingOut;
            _world = null;
            _onLoggedIn = null;
            _onLoggingOut = null;
        }

        return _pets?.Service.StopAsync() ?? Task.CompletedTask;
    }

    private sealed record LoadedPet(PersistentPetSnapshot? Snapshot, long Generation);
}
