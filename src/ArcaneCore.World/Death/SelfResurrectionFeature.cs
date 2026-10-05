using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Death;

/// <summary>Persist the completed SELF_RESURRECT effect through the normal character snapshot.</summary>
public sealed class SelfResurrectionFeature : IWorldFeature, IDisposable
{
    private readonly HashSet<MapCombat> _subscribed = [];
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the self resurrection feature is already attached");
        }

        _world = world;
        world.MapCreated += Subscribe;
        world.MapUnloading += Unsubscribe;
        foreach (Map map in world.Maps)
        {
            Subscribe(map);
        }
    }

    private void Subscribe(Map map)
    {
        if (_subscribed.Add(map.Combat))
        {
            map.Combat.PlayerSelfResurrected += Save;
        }
    }

    private void Unsubscribe(Map map)
    {
        if (_subscribed.Remove(map.Combat))
        {
            map.Combat.PlayerSelfResurrected -= Save;
        }
    }

    private void Save(Player player)
    {
        if (_world is { } world && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player))
        {
            // A source-valid 0% effect completes the death-state transition at health zero.
            // Save that result as well; do not gate persistence on Unit.IsAlive's health test.
            world.SavePlayer(player);
        }
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.MapCreated -= Subscribe;
            world.MapUnloading -= Unsubscribe;
        }

        foreach (MapCombat combat in _subscribed)
        {
            combat.PlayerSelfResurrected -= Save;
        }

        _subscribed.Clear();
        _world = null;
    }
}
