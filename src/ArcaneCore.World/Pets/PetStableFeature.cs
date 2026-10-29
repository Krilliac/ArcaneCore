using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

/// <summary>
/// The hunter pet stable in the world daemon: wires <see cref="PetStableController"/> to the characters database (a scope per call), the world
/// thread, the stable master interaction rule and the stable master gossip option (vmangos GOSSIP_OPTION_STABLEPET: SendStablePet). Without a
/// registered <see cref="IPetStableStore"/> every stable request is refused.
/// </summary>
public sealed class PetStableFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<PetStableFeature> logger) : IWorldFeature
{
    private QuestNpcServices? _wiredNpcs;

    /// <summary>The stable, once attached to a world with pets.</summary>
    public PetStableController? Controller { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (services.GetService<PetsFeature>() is not { } pets)
        {
            return;
        }

        bool stored;
        using (IServiceScope probe = scopes.CreateScope())
        {
            stored = probe.ServiceProvider.GetService<IPetStableStore>() is not null;
        }

        PetStableController controller = new(pets.Service, logger)
        {
            Store = stored ? new ScopedPetStableStore(scopes) : null,
            Post = world.Post,
            InteractingCancels = player => services.GetService<QuestNpcFeature>()?.Services?.InteractingCancels?.Invoke(player),
            IsStableMaster = (player, npc) => services.GetService<QuestNpcFeature>()?.Services?.InteractableNpc(player, npc, NpcFlags.StableMaster) is not null,
        };
        controller.MoneyChanged += world.SavePlayer;
        Controller = controller;
        world.PlayerLoggingOut += player => controller.Forget(checked((int)player.Guid.Low));
        world.PlayerLoggedIn += controller.Prefetch;
        world.WorldTick += _ => WireGossip();
        WireGossip();
    }

    // The NPC services are rebuilt when the content loads, so the gossip hook follows the current instance.
    private void WireGossip()
    {
        if (services.GetService<QuestNpcFeature>()?.Services is not { } npcs || ReferenceEquals(npcs, _wiredNpcs))
        {
            return;
        }

        if (_wiredNpcs is { } old)
        {
            old.ForeignOptionSelected -= OnForeignOptionSelected;
        }

        npcs.ForeignOptionSelected += OnForeignOptionSelected;
        _wiredNpcs = npcs;
    }

    private void OnForeignOptionSelected(Player player, NpcInfo npc, GossipOption option)
    {
        if (option == GossipOption.StablePet)
        {
            Controller?.HandleList(player, npc.Guid);
        }
    }

    private sealed class ScopedPetStableStore(IServiceScopeFactory scopes) : IPetStableStore
    {
        public async Task<PetStableState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPetStableStore>().LoadAsync(characterId, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetPurchasedSlotsAsync(int characterId, byte slots, CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPetStableStore>().SetPurchasedSlotsAsync(characterId, slots, cancellationToken).ConfigureAwait(false);
        }

        public async Task StableAsync(int characterId, byte slot, PersistentPetSnapshot active, CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPetStableStore>().StableAsync(characterId, slot, active, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PersistentPetSnapshot?> UnstableAsync(int characterId, uint petNumber, PersistentPetSnapshot? active, CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPetStableStore>().UnstableAsync(characterId, petNumber, active, cancellationToken).ConfigureAwait(false);
        }
    }
}