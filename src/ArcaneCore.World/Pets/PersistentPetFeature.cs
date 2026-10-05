using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

public sealed class PersistentPetFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<PersistentPetFeature> logger)
    : IWorldFeature, ICharacterHooks, ICharacterDeleteHook
{
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<Player, PersistentPetSnapshot?> _pending = [];
    private PetsFeature? _pets;

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
        world.PlayerLoggedIn += player => PublishLoaded(player, pets);
        world.PlayerLoggingOut += player => _ = SaveAsync(player, pets);
    }

    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (_pets is null) return;
        if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            await saves.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        await _pets.Service.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        PersistentPetSnapshot? snapshot = await _pets.Service.ReadCurrentPetAsync(player).ConfigureAwait(false);
        if (snapshot is null)
        {
            snapshot = await _pets.Service.ReadCallablePetAsync(player).ConfigureAwait(false);
        }
        lock (_pendingGate) _pending[player] = snapshot;
    }

    private void PublishLoaded(Player player, PetsFeature pets)
    {
        PersistentPetSnapshot? snapshot;
        lock (_pendingGate) { if (!_pending.Remove(player, out snapshot)) return; }
        if (snapshot is not null && player.IsInWorld && player.Class == ArcaneCore.Game.Class.Hunter
            && player.PetGuid.IsEmpty && !player.IsQuestSettlementPending)
            pets.Service.RestoreCurrentPet(player, snapshot);
    }

    private async Task SaveAsync(Player player, PetsFeature pets)
    {
        try { await pets.Service.SaveCurrentPetAsync(player).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { logger.LogError(ex, "persistent pet save failed for {CharacterId}", player.Guid.Low); }
    }

    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        if (_pets is not null) await _pets.Service.FlushCharacterAsync(character.Id).ConfigureAwait(false);
    }

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        lock (_pendingGate) foreach (Player player in _pending.Keys.Where(p => p.Guid.Low == (uint)character.Id).ToArray()) _pending.Remove(player);
        _pets?.Service.ForgetCharacter(character.Id);
        return Task.CompletedTask;
    }

    public Task StopAsync() => _pets?.Service.StopAsync() ?? Task.CompletedTask;
}
