using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The generic cast rules of the warrior and rogue abilities in the world daemon (discovered <see cref="IWorldFeature"/>):
/// <see cref="GeneralCastChecks"/> (standing, combat-forbidden, stealth-only, behind and in-front spells).
/// </summary>
public sealed class CastCheckFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GeneralCastChecks.Install(services.GetRequiredService<SpellFeature>().System);
    }
}
