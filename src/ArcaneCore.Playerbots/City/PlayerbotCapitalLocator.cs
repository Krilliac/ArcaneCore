using ArcaneCore.Data.ClientData;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The WMOAreaTable.dbc rows the capital locator needs: (WMO root id, name set id, group id) to the AreaTable id of the WMO group. Immutable.
/// Cities built as one big model (Ironforge, Undercity) are not marked on the terrain at all: the ADT area flags there say Dun Morogh and
/// Tirisfal Glades, and only the WMO group a position stands in says which city it is (vmangos <c>TerrainInfo::GetZoneAndAreaId</c> with vmaps).
/// </summary>
internal sealed class WmoAreaMap
{
    private const int FieldCount = 20;
    private const int RecordSize = 80;
    private readonly Dictionary<(int Root, int NameSet, int Group), uint> _exact;
    private readonly Dictionary<(int Root, int Group), uint> _loose;

    private WmoAreaMap(Dictionary<(int, int, int), uint> exact, Dictionary<(int, int), uint> loose)
    {
        _exact = exact;
        _loose = loose;
    }

    internal static WmoAreaMap Empty { get; } = new([], []);

    internal int Count => _exact.Count;

    /// <summary>Rows from (WMOID, NameSetID, WMOGroupID, AreaTableID) tuples (tests and the DBC reader).</summary>
    internal static WmoAreaMap Of(IEnumerable<(int Root, int NameSet, int Group, uint AreaId)> rows)
    {
        var exact = new Dictionary<(int, int, int), uint>();
        var loose = new Dictionary<(int, int), uint>();
        foreach ((int root, int nameSet, int group, uint areaId) in rows)
        {
            if (areaId == 0) continue;
            exact[(root, nameSet, group)] = areaId;
            loose.TryAdd((root, group), areaId);
        }

        return new WmoAreaMap(exact, loose);
    }

    /// <summary>Read the 1.12.1 WMOAreaTable.dbc (20 four-byte fields). Throws for a file of another layout.</summary>
    internal static WmoAreaMap Read(string path)
    {
        DbcFile file = DbcFile.Load(path);
        if (file.FieldCount != FieldCount || file.RecordSize != RecordSize)
            throw new InvalidDataException($"WMOAreaTable.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}/{file.RecordSize}");
        var rows = new List<(int, int, int, uint)>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
            rows.Add((file.GetInt32(row, 1), file.GetInt32(row, 2), file.GetInt32(row, 3), file.GetUInt32(row, 10)));
        return Of(rows);
    }

    /// <summary>The AreaTable id of a WMO group: the exact triple, else the same root and group under any name set; 0 when unknown.</summary>
    internal uint AreaOf(ModelAreaInfo info)
        => _exact.TryGetValue((info.RootId, info.AdtId, info.GroupId), out uint exact) ? exact
            : _loose.GetValueOrDefault((info.RootId, info.GroupId));
}

/// <summary>
/// Where a position stands, (zone, area), for the capital catalog build: the WMO group the position is inside (vmaps and WMOAreaTable.dbc),
/// else the terrain's area flag. Reads each terrain tile once (a build asks hundreds of positions of the same few tiles).
/// <para>
/// Thread affinity: one building thread; the instance owns a private <see cref="VMapManager"/> (the world's is world-thread only) and a tiny
/// tile cache, and is dropped after the build. It reads the data directories and the immutable <see cref="AreaTable"/> only.
/// </para>
/// </summary>
internal sealed class PlayerbotCapitalLocator
{
    private const int MaxTerrainTiles = 24;
    private const int MaxModelTiles = 6;

    private readonly TerrainManager _terrain;
    private readonly AreaTable _areas;
    private readonly VMapManager? _vmaps;
    private readonly WmoAreaMap _wmo;
    private readonly Dictionary<(uint Map, int X, int Y), TerrainTile?> _tiles = [];
    private readonly Queue<(uint Map, int X, int Y)> _modelTiles = new();
    private readonly HashSet<(uint Map, int X, int Y)> _modelTileSet = [];

    internal PlayerbotCapitalLocator(TerrainManager terrain, AreaTable areas, VMapManager? vmaps, WmoAreaMap wmo)
    {
        _terrain = terrain;
        _areas = areas;
        _vmaps = vmaps is { HeightEnabled: true } ? vmaps : null;
        _wmo = wmo;
    }

    /// <summary>Whether a position can be placed inside a WMO city at all (the model data and the WMO area rows are both present).</summary>
    internal bool HasWmoAreas => _vmaps is not null && _wmo.Count > 0;

    /// <summary>
    /// Make a locator over the world's terrain and the vmap and WMOAreaTable files named. A missing vmap directory or an unreadable
    /// WMOAreaTable.dbc is logged and leaves the terrain-only answer.
    /// </summary>
    internal static PlayerbotCapitalLocator Create(TerrainManager terrain, AreaTable areas, string? vmapDirectory, string? wmoAreaTablePath, ILogger? logger)
    {
        VMapManager? vmaps = vmapDirectory is not null && Directory.Exists(vmapDirectory) ? new VMapManager(vmapDirectory, enableLineOfSight: false, enableHeight: true, logger) : null;
        WmoAreaMap wmo = WmoAreaMap.Empty;
        if (!string.IsNullOrWhiteSpace(wmoAreaTablePath))
        {
            try
            {
                wmo = WmoAreaMap.Read(wmoAreaTablePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger?.LogWarning(ex, "Playerbot capital catalog: could not read WMOAreaTable.dbc at {Path}; cities built as one model (Ironforge, Undercity) are not found", wmoAreaTablePath);
            }
        }

        return new PlayerbotCapitalLocator(terrain, areas, vmaps, wmo);
    }

    /// <summary>(zone, area) at a position: the WMO group's area when the position is inside a mapped WMO, else the terrain's; (0, 0) when unknown.</summary>
    internal (uint ZoneId, uint AreaId) Locate(uint mapId, float x, float y, float z)
    {
        if (TerrainTile.TileOf(x, y) is not { } tile) return (0, 0);
        if (_vmaps is not null && _wmo.Count > 0 && float.IsFinite(z) && InsideWmo(mapId, tile, x, y, z + 0.5f) is { } wmoArea
            && _areas.GetById(wmoArea) is { } area)
        {
            return (area.IsZone ? area.Entry : area.ZoneId, area.Entry);
        }

        return Tile(mapId, tile.X, tile.Y) is { } terrain ? _areas.GetZoneAndAreaId(terrain.GetAreaFlag(x, y), mapId) : (0, 0);
    }

    private uint? InsideWmo(uint mapId, (int X, int Y) tile, float x, float y, float z)
    {
        LoadModelTile(mapId, tile.X, tile.Y);
        if (!_vmaps!.TryGetAreaInfo(mapId, x, y, z, out ModelAreaInfo info)) return null;
        return _wmo.AreaOf(info) is var id and not 0 ? id : null;
    }

    private TerrainTile? Tile(uint mapId, int tileX, int tileY)
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
        _vmaps!.LoadTile(mapId, tileX, tileY);
        if (_modelTiles.Count > MaxModelTiles)
        {
            var old = _modelTiles.Dequeue();
            _modelTileSet.Remove(old);
            _vmaps.UnloadTile(old.Map, old.X, old.Y);
        }
    }
}
