using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells.Utility;

/// <summary>Installs the pet, master and minion-position implicit targets (<see cref="PetTargets"/>) on the world spell system.</summary>
public sealed class PetTargetFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        PetTargets.Install(services.GetRequiredService<SpellFeature>().System);
    }
}
