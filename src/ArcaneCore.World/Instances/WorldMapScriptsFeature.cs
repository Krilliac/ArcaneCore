using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Gives each continent with a cmangos world map script its <see cref="WorldMapData"/> (ScriptedMap on an open-world map: world_kalimdor.cpp),
/// so area trigger scripts and EventAI SET_INST_DATA find it with <c>map.FindUpdater&lt;InstanceData&gt;()</c>, as cmangos'
/// <c>Player::GetInstanceData</c> does outside dungeons too. Instance maps keep the data <c>InstanceManager</c> gives them.
/// </summary>
public sealed class WorldMapScriptsFeature : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.MapCreated += map => Install(map);
        foreach (Map map in world.Maps)
        {
            _ = Install(map);
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
