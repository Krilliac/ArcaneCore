using System.Numerics;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.AntiCheat;

/// <summary>
/// The terrain-dependent checks' view of one map (docs/areas/anticheat.md): every answer is null unless the data for that
/// spot is actually loaded. Liquid needs the terrain tile with data AND the map's vmap tile, and is only answered outdoors
/// (indoor water lives in the WMO models, which carry no liquid here); the floor needs both too; line of sight needs the
/// vmap tile at both ends. Nothing here loads a tile: a tile that is not in memory is "no data". World thread only.
/// </summary>
public sealed class MapAntiCheatTerrain(Map map) : IAntiCheatTerrain
{
    public bool? IsInLiquid(float x, float y, float z)
    {
        if (!HasTerrain(x, y) || !HasModels(x, y) || map.Collision.LineOfSight.TryGetAreaInfo(map.MapId, x, y, z, out _))
        {
            return null;
        }

        return map.Terrain.GetLiquidStatus(x, y, z, LiquidTypeFlags.None, out _) != LiquidStatus.NoWater;
    }

    public float? FloorHeight(float x, float y, float z)
    {
        if (!HasTerrain(x, y) || !HasModels(x, y))
        {
            return null;
        }

        float floor = map.Collision.GetHeight(x, y, z);
        return floor > TerrainTile.InvalidHeight ? floor : null;
    }

    public bool? IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        if (!HasModels(x1, y1) || !HasModels(x2, y2))
        {
            return null;
        }

        return map.Collision.LineOfSight.IsInLineOfSight(map.MapId, new Vector3(x1, y1, z1), new Vector3(x2, y2, z2));
    }

    private bool HasTerrain(float x, float y)
        => TerrainTile.TileOf(x, y) is { } tile && map.Terrain.IsTileLoaded(tile.X, tile.Y) && map.Terrain.GetTile(tile.X, tile.Y).HasData;

    private bool HasModels(float x, float y)
    {
        if (map.Collision.LineOfSight is not VMapManager { LineOfSightEnabled: true } vmaps || TerrainTile.TileOf(x, y) is not { } tile)
        {
            return false;
        }

        return vmaps.GetTree(map.MapId) is { } tree && (!tree.IsTiled || tree.IsTileLoaded(tile.X, tile.Y));
    }
}
