using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.World;

/// <summary>
/// cmangos <c>ScriptedMap</c> for an open-world map (AI/ScriptDevAI/scripts/world/world_map_scripts.h, kalimdor/world_kalimdor.cpp,
/// eastern_kingdoms/world_eastern_kingdoms.cpp): the same encounter array and creature store an instance script keeps, on a continent. It is
/// an <see cref="InstanceData"/>, so the creature hooks every map already calls (create, death, evade, respawn) and the EventAI
/// SET_INST_DATA writes reach it, and anything on the map finds it with <c>map.FindUpdater&lt;InstanceData&gt;()</c>
/// (<c>Player::GetInstanceData</c>). Nothing is saved: cmangos keeps world map state in memory only.
/// </summary>
public abstract class WorldMapData : ScriptedInstance
{
    protected WorldMapData(Map map, int maxEncounter)
        : base(map, maxEncounter)
    {
    }

    /// <summary>World map data is never saved (cmangos world map scripts have no Save()).</summary>
    public override string? GetSaveData() => null;

    /// <summary>SD2 GetSingleCreatureFromStorage(entry, skipDebugLog) for an area trigger script.</summary>
    public Creature? FindStoredCreature(uint entry) => GetSingleCreatureFromStorage(entry);

    /// <summary>Random time source, replaceable by tests.</summary>
    internal Random Random { get; set; } = Random.Shared;

    /// <summary>
    /// cmangos WorldObject::GetRandomPoint(x, y, z, distance): a point within <paramref name="radius"/> yards around the given one, put on
    /// the ground near its height (the given height where the terrain has none).
    /// </summary>
    protected (float X, float Y, float Z) RandomPointAround(float x, float y, float z, float radius)
    {
        double angle = Random.NextDouble() * Math.PI * 2, distance = Random.NextDouble() * radius;
        float px = x + (float)(distance * Math.Cos(angle)), py = y + (float)(distance * Math.Sin(angle));
        float ground = Instance.GetHeight(px, py, z + 2f);
        return (px, py, ground > ArcaneCore.Game.Maps.Terrain.TerrainTile.InvalidHeight && Math.Abs(ground - z) <= 10f ? ground : z);
    }

    /// <summary>The world map script of a continent (cmangos ScriptMgr's per-map <c>GetInstanceData</c>), or null for a map without one.</summary>
    public static WorldMapData? Create(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.InstanceId != 0)
        {
            return null;
        }

        return map.MapId switch
        {
            KalimdorWorldMapData.MapKalimdor => new KalimdorWorldMapData(map),
            _ => null,
        };
    }
}
