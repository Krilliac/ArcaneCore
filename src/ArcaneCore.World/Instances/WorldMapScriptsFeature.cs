using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Features;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Gives each continent with a cmangos world map script its <see cref="WorldMapData"/> (ScriptedMap on an open-world map: world_kalimdor.cpp),
/// so area trigger scripts and EventAI SET_INST_DATA find it with <c>map.FindUpdater&lt;InstanceData&gt;()</c>, as cmangos'
/// <c>Player::GetInstanceData</c> does outside dungeons too. Instance maps keep the data <c>InstanceManager</c> gives them.
/// It also tells the continent scripts when a game event starts or stops (mangos-classic GameEventMgr.cpp:1153-1160 OnEventHappened);
/// each script runs the change on its own map thread.
/// </summary>
public sealed class WorldMapScriptsFeature(IServiceProvider? services = null) : IWorldFeature, IGameEventListener
{
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.MapCreated += map => Install(map);
        foreach (Map map in world.Maps)
        {
            _ = Install(map);
        }

        services?.GetService<GameEventFeature>()?.AddListener(this);
    }

    /// <inheritdoc />
    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        if (_world is null)
        {
            return;
        }

        foreach (Map map in _world.Maps.ToArray())
        {
            if (map.InstanceId == 0 && map.FindUpdater<InstanceData>() is WorldMapData data)
            {
                data.QueueGameEvent(eventId, active, resume);
            }
        }
    }

    /// <summary>Install the map's world map script, unless it has none or already has script data. Returns the data installed.</summary>
    public static WorldMapData? Install(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.FindUpdater<InstanceData>() is not null || WorldMapData.Create(map) is not { } data)
        {
            return null;
        }

        data.Initialize();
        map.AddUpdater(data);
        return data;
    }
}
