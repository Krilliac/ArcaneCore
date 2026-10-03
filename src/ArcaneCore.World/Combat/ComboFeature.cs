using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Combo points in the world daemon (discovered <see cref="IWorldFeature"/>): installs <see cref="ComboPointService"/> on the
/// spell system (finishing move check, value and duration scaling, spend on finish, the add-combo-points effect) and drops
/// points when their target dies or their owner dies or logs out. The service is also what the aura-state reactives use
/// for Overpower. A unit that leaves the world without dying keeps its holders' points until they next change.
/// </summary>
public sealed class ComboFeature(IServiceProvider services) : IWorldFeature
{
    public ComboPointService? Service { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        ComboPointService combos = new(spells.System, static (player, guid) => player.Map?.Combat.FindUnit(guid));
        combos.Install();
        Service = combos;

        world.PlayerLoggingOut += combos.OnPlayerGone;
        world.MapCreated += Subscribe;
        foreach (Map map in world.Maps)
        {
            Subscribe(map);
        }
    }

    private void Subscribe(Map map)
    {
        if (Service is not { } combos || map.FindUpdater<MapCombat>() is not { } combat)
        {
            return;
        }

        combos.Observe(combat);
    }
}
