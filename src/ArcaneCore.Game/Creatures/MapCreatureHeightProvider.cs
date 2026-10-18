using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature heights from the owning map: the floor under a point, terrain and static models (<see cref="Floor"/>).
/// vmangos asks <c>Map::GetHeight</c> with vmaps on for every creature point (Map.cpp:3407-3411: <c>GetHeightStatic(x, y, z, vmap =
/// true)</c>, GridMap.cpp:815-871), e.g. the random point drawn off the navmesh (Map::GetWalkRandomPosition, Map.cpp:3399-3401) and a
/// summon's ground (WorldObject::UpdateGroundPositionZ, Object.cpp:2079-2084): a point over a WMO floor is on the floor, not on the
/// terrain under the building. Missing data stays unknown.
/// </summary>
public sealed class MapCreatureHeightProvider(Map map) : ICreatureHeightProvider
{
    public float? GetHeight(uint mapId, float x, float y, float z) => map.MapId == mapId ? Floor(map, x, y, z) : null;

    /// <summary>The terrain height (<see cref="Map.GetHeight"/>), as the provider answered before it knew the models.</summary>
    public float? GetTerrainHeight(uint mapId, float x, float y, float z) => map.MapId == mapId ? Known(map.GetHeight(x, y, z)) : null;

    /// <summary>
    /// The height vmangos <c>Map::GetHeight</c> gives a point: the floor under it (<see cref="MapCollision.GetHeight"/>, terrain and static
    /// models) or, where none is found under it, the terrain height above it (GetHeightStatic returns the terrain's own height then,
    /// GridMap.cpp:871): a point drawn up a slope is put on the slope, as on a map without models. Null where no height is known.
    /// A non-finite point asks no model (MapCollision.GetHeight refuses it as a broken invariant, and a creature's own non-finite position
    /// is reported by its anomaly check): the terrain answers, null without a tile.
    /// </summary>
    public static float? Floor(Map map, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return Known(map.GetHeight(x, y, z));
        }

        return Known(map.Collision.GetHeight(x, y, z)) ?? Known(map.GetHeight(x, y, z));
    }

    /// <summary>A height as this provider gives it: null where no height is known.</summary>
    internal static float? Known(float height) => float.IsFinite(height) && height > TerrainTile.InvalidHeight ? height : null;
}
