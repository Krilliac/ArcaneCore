using ArcaneCore.Game.Death;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Graveyards;

/// <summary>
/// The world daemon's graveyard feature (docs/areas/graveyards-resurrection.md): it loads <c>world_safe_locs</c> and
/// <c>game_graveyard_zone</c> when an <see cref="IGraveyardDataStore"/> is registered and registers the
/// <see cref="GraveyardRepopService"/> as the world's <see cref="IGraveyardRepop"/>, so a released spirit goes to its
/// graveyard. Without graveyard data nothing moves a spirit and the ghost stays on its body, as before.
/// </summary>
public sealed class GraveyardFeature(IServiceScopeFactory scopes, IServiceProvider services, ILogger<GraveyardFeature> logger) : IWorldFeature
{
    /// <summary>The service registered as the world's graveyard seam (after <see cref="Attach"/>).</summary>
    public GraveyardRepopService? Service { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IGraveyardDataStore>() is not { } store)
            {
                logger.LogInformation("no graveyard data store registered; released spirits stay at their body");
                return;
            }

            GraveyardContent content = store.LoadAsync().GetAwaiter().GetResult();
            WorldGraveyards.Of(world).Load(content, logger);
        }

        TeleportFeature? teleports = services.GetService<TeleportFeature>();
        Service = new GraveyardRepopService(world, () => teleports?.Teleports);
        if (!DeathSeams.Of(world).TryRegisterGraveyards(Service))
        {
            logger.LogWarning("a graveyard implementation was already registered for this world; the graveyard feature's is not used");
            Service = null;
        }
    }
}
