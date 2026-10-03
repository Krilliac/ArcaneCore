using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Creatures;

/// <summary>Creature movement uses the owning map's extracted terrain; missing data stays unknown.</summary>
public sealed class MapCreatureHeightProvider(Map map) : ICreatureHeightProvider
{
    public float? GetHeight(uint mapId, float x, float y, float z)
    {
        if (map.MapId != mapId)
        {
            return null;
        }

        float height = map.GetHeight(x, y, z);
        return float.IsFinite(height) && height > TerrainTile.InvalidHeight ? height : null;
    }
}
