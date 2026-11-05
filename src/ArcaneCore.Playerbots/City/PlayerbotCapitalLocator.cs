using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Where a position stands, (zone, area), for the capital catalog build: the WMO group the position is inside (vmaps and the world's
/// <see cref="WmoAreaTable"/>, the retail rules: exact key, first row wins, no fallback), else the terrain's area flag. Reads each terrain
/// tile once (a build asks hundreds of positions of the same few tiles).
/// <para>
/// Cities built as one big model (Ironforge, Undercity) are not marked on the terrain at all: the ADT area flags there say Dun Morogh and
/// Tirisfal Glades, and only the WMO group a position stands in says which city it is (vmangos <c>TerrainInfo::GetZoneAndAreaId</c> with
/// vmaps). The group is found from 1 yard above the position and rejected when terrain rises to the point, the same test as
/// <see cref="TerrainInfo.TryGetWmoGroup"/> (<see cref="TerrainInfo.WmoGroupCounts"/>), so a spawn on the ground over a building is not in it.
/// </para>
/// <para>
/// Thread affinity: one building thread; the instance owns the model service it is given (production: a private <see cref="VMapManager"/>,
/// the world's is world-thread only) and a tiny tile cache, and is dropped after the build. It reads the data directories and the immutable
/// <see cref="AreaTable"/> and <see cref="WmoAreaTable"/> only.
/// </para>
/// </summary>
internal sealed class PlayerbotCapitalLocator
{
    private const int MaxTerrainTiles = 24;
    private const int MaxModelTiles = 6;

    private readonly TerrainManager _terrain;
    private readonly AreaTable _areas;
    private readonly WmoAreaTable _wmoAreas;
    private readonly ILineOfSight? _models;
    private readonly Func<uint, int, int, bool>? _loadModelTile;
    private readonly Action<uint, int, int>? _unloadModelTile;
    private readonly Dictionary<(uint Map, int X, int Y), TerrainTile> _tiles = [];
    private readonly Queue<(uint Map, int X, int Y)> _modelTiles = new();
    private readonly HashSet<(uint Map, int X, int Y)> _modelTileSet = [];

    /// <summary>A locator over a model service; <paramref name="loadModelTile"/> and <paramref name="unloadModelTile"/> bring the model tile of a terrain tile in and out (null: the service needs none).</summary>
    internal PlayerbotCapitalLocator(TerrainManager terrain, AreaTable areas, WmoAreaTable wmoAreas, ILineOfSight? models,
        Func<uint, int, int, bool>? loadModelTile = null, Action<uint, int, int>? unloadModelTile = null)
    {
        _terrain = terrain;
        _areas = areas;
        _wmoAreas = wmoAreas;
        _models = models is { Enabled: true } ? models : null;
        _loadModelTile = loadModelTile;
        _unloadModelTile = unloadModelTile;
    }

    /// <summary>Whether a position can be placed inside a WMO city at all (the model data and the WMO area rows are both present).</summary>
    internal bool HasWmoAreas => _models is not null && _wmoAreas.Count > 0;

    /// <summary>
    /// Make a locator over the world's terrain (and its <see cref="TerrainManager.WmoAreas"/>) and the vmap directory named. A missing vmap
    /// directory leaves the terrain-only answer.
    /// </summary>
    internal static PlayerbotCapitalLocator Create(TerrainManager terrain, AreaTable areas, string? vmapDirectory, ILogger? logger)
    {
        VMapManager? vmaps = vmapDirectory is not null && Directory.Exists(vmapDirectory) ? new VMapManager(vmapDirectory, enableLineOfSight: false, enableHeight: true, logger) : null;
        if (vmaps is not { HeightEnabled: true })
        {
            return new PlayerbotCapitalLocator(terrain, areas, terrain.WmoAreas, null);
        }

        return new PlayerbotCapitalLocator(terrain, areas, terrain.WmoAreas, vmaps, vmaps.LoadTile, vmaps.UnloadTile);
    }

    /// <summary>(zone, area) at a position: the WMO group's area when the position is inside a mapped WMO, else the terrain's; (0, 0) when unknown.</summary>
    internal (uint ZoneId, uint AreaId) Locate(uint mapId, float x, float y, float z)
    {
        if (TerrainTile.TileOf(x, y) is not { } index) return (0, 0);
        TerrainTile tile = Tile(mapId, index.X, index.Y);
        if (_models is not null && _wmoAreas.Count > 0 && float.IsFinite(z)
            && InsideWmo(mapId, index, tile, x, y, z) is { } wmoArea && _areas.GetById(wmoArea) is { } area)
        {
            return (area.IsZone ? area.Entry : area.ZoneId, area.Entry);
        }

        return tile.HasData ? _areas.GetZoneAndAreaId(tile.GetAreaFlag(x, y), mapId) : (0, 0);
    }

    private uint? InsideWmo(uint mapId, (int X, int Y) index, TerrainTile tile, float x, float y, float z)
    {
        LoadModelTile(mapId, index.X, index.Y);
        float raised = z + 1.0f;
        if (!_models!.TryGetAreaInfo(mapId, x, y, raised, out ModelAreaInfo info)
            || !TerrainInfo.WmoGroupCounts(raised, tile.GetHeight(x, y), info.GroundZ))
        {
            return null;
        }

        return _wmoAreas.TryGetAreaId(info.RootId, info.AdtId, info.GroupId, out uint id) ? id : null;
    }

    private TerrainTile Tile(uint mapId, int tileX, int tileY)
    {
        var key = (mapId, tileX, tileY);
        if (_tiles.TryGetValue(key, out TerrainTile? cached)) return cached;
        if (_tiles.Count >= MaxTerrainTiles) _tiles.Clear();
        return _tiles[key] = _terrain.ReadTileDetached(mapId, tileX, tileY);
    }

    private void LoadModelTile(uint mapId, int tileX, int tileY)
    {
        var key = (mapId, tileX, tileY);
        if (!_modelTileSet.Add(key)) return;
        _modelTiles.Enqueue(key);
        _loadModelTile?.Invoke(mapId, tileX, tileY);
        if (_modelTiles.Count > MaxModelTiles)
        {
            var old = _modelTiles.Dequeue();
            _modelTileSet.Remove(old);
            _unloadModelTile?.Invoke(old.Map, old.X, old.Y);
        }
    }
}
