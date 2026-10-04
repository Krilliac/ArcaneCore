using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Ghost;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Death;

/// <summary>
/// Registers the world's <see cref="IGhostForm"/> (docs/areas/graveyards-resurrection.md): a released spirit gets the ghost
/// aura through the spell feature's spell system. The spell system is looked up when a spirit is released, not here, because the
/// spell feature may attach after this one.
/// </summary>
public sealed class GhostFormFeature(IServiceProvider services, ILogger<GhostFormFeature> logger) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var form = new GhostForm(world, () => services.GetService<SpellFeature>()?.System, message => logger.LogWarning("{Message}", message));
        if (!DeathSeams.Of(world).TryRegisterGhostForm(form))
        {
            logger.LogWarning("a ghost form implementation was already registered for this world; the ghost form feature's is not used");
        }
    }
}
