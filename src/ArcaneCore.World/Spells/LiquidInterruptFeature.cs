using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Interrupts;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Attaches the <see cref="LiquidAuraInterruptUpdater"/> (vmangos HIGH_LIQUID aura/channel interrupts) to every
/// map. The spell system is resolved lazily from <see cref="SpellFeature"/>, which attaches after this feature.
/// </summary>
public sealed class LiquidInterruptFeature(IServiceProvider services) : IWorldFeature
{
    private readonly ILiquidProbe _probe = new TerrainLiquidProbe();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.MapCreated += AttachTo;
        world.Post(() =>
        {
            foreach (Map map in world.Maps.ToArray())
            {
                AttachTo(map);
            }
        });
    }

    private void AttachTo(Map map)
    {
        if (map.FindUpdater<LiquidAuraInterruptUpdater>() is null)
        {
            map.AddUpdater(new LiquidAuraInterruptUpdater(() => services.GetService<SpellFeature>()?.System, _probe));
        }
    }
}
