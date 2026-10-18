using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Core;

/// <summary>A point in WoW world coordinates (x north, y west, z up, yards) kept in double precision.</summary>
public readonly record struct WorldPoint(double X, double Y, double Z)
{
    public static WorldPoint operator +(WorldPoint a, WorldPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static WorldPoint operator -(WorldPoint a, WorldPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

/// <summary>
/// One terrain tile of one map, in the server's tile indices (<see cref="TerrainTile.TileOf(float, float)"/>): tile X
/// comes from world X and tile Y from world Y. <see cref="Global"/> (-1, -1) stands for a map's untiled content (the
/// vmap pseudo-tile of untiled maps and a WDT's global WMO).
/// </summary>
public readonly record struct TileKey(uint MapId, int X, int Y)
{
    public const int GlobalIndex = -1;

    public static TileKey Global(uint mapId) => new(mapId, GlobalIndex, GlobalIndex);

    public bool IsGlobal => X == GlobalIndex && Y == GlobalIndex;

    /// <summary>A real tile inside the 64x64 grid (the global pseudo-tile is not one).</summary>
    public bool IsInGrid => X is >= 0 and < WorldGrid.TilesPerSide && Y is >= 0 and < WorldGrid.TilesPerSide;

    public bool IsValid => IsGlobal || IsInGrid;

    public override string ToString() => IsGlobal ? $"{MapId}/global" : $"{MapId}/{X}/{Y}";
}

/// <summary>
/// Tile and coordinate math shared by every layer. Every constant and rule delegates to the server's own definitions
/// (<see cref="GridDefines"/>, <see cref="TerrainTile"/>, <see cref="VMapFormat"/>, <see cref="NavMeshFormat"/>) so
/// Atlas cannot drift from what the server does.
/// </summary>
public static class WorldGrid
{
    /// <summary>Tiles per map side (<see cref="GridDefines.MaxNumberOfGrids"/>).</summary>
    public const int TilesPerSide = GridDefines.MaxNumberOfGrids;

    /// <summary>Tile edge in yards, the server's float constant widened (<see cref="GridDefines.SizeOfGrids"/>).</summary>
    public const double TileSize = GridDefines.SizeOfGrids;

    /// <summary>Height samples per tile edge in a .map file (<see cref="GridDefines.MapResolution"/>).</summary>
    public const int MapResolution = GridDefines.MapResolution;

    /// <summary>Edge of one .map height square in yards.</summary>
    public const double SquareSize = TileSize / MapResolution;

    /// <summary>The tile holding a world position, exactly as the server computes it; null outside the map.</summary>
    public static TileKey? TileOf(uint mapId, double x, double y)
        => TerrainTile.TileOf((float)x, (float)y) is { } t ? new TileKey(mapId, t.X, t.Y) : null;

    /// <summary>
    /// The tile's corner with the largest world X and Y (the .map file's first sample, row 0 column 0). Rows advance
    /// toward -X and columns toward -Y.
    /// </summary>
    public static WorldPoint Origin(TileKey tile)
    {
        if (tile.IsGlobal)
        {
            return new WorldPoint(0, 0, 0);
        }

        return new WorldPoint((32 - tile.X) * TileSize, (32 - tile.Y) * TileSize, 0);
    }

    /// <summary>World X/Y extent of a tile: X in (MinX, MaxX], Y in (MinY, MaxY].</summary>
    public static (double MinX, double MaxX, double MinY, double MaxY) Bounds(TileKey tile)
    {
        WorldPoint o = Origin(tile);
        return (o.X - TileSize, o.X, o.Y - TileSize, o.Y);
    }

    /// <summary>Centre of a tile in world X/Y.</summary>
    public static (double X, double Y) Centre(TileKey tile)
    {
        WorldPoint o = Origin(tile);
        return (o.X - (TileSize / 2), o.Y - (TileSize / 2));
    }

    /// <summary>Server terrain file name (<see cref="TerrainTile.FileName"/>).</summary>
    public static string MapFileName(TileKey tile) => TerrainTile.FileName(tile.MapId, tile.X, tile.Y);

    /// <summary>Server navmesh tile file name (<see cref="NavMeshFormat.TileFileName"/>).</summary>
    public static string NavTileFileName(TileKey tile) => NavMeshFormat.TileFileName(tile.MapId, tile.X, tile.Y);

    /// <summary>Server vmap tile file name (<see cref="VMapFormat.TileFileName"/>; tile Y comes first in the name).</summary>
    public static string VMapTileFileName(TileKey tile) => VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y);

    /// <summary>Tiles whose centres lie within <paramref name="radiusTiles"/> tiles (Chebyshev) of a world point.</summary>
    public static IEnumerable<TileKey> Around(uint mapId, double x, double y, int radiusTiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radiusTiles);
        if (TileOf(mapId, x, y) is not { } centre)
        {
            yield break;
        }

        for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
        {
            for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
            {
                // IsInGrid, not IsValid: the neighbour (-1, -1) of tile (0, 0) is off the map, not the global tile.
                var t = new TileKey(mapId, centre.X + dx, centre.Y + dy);
                if (t.IsInGrid)
                {
                    yield return t;
                }
            }
        }
    }
}
