using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// The terrain of one map (vmangos <c>TerrainInfo</c>, GridMap.cpp): its 64 × 64 tiles, loaded
/// from the data directory the first time a position on them is looked up, reference-counted by
/// the map's loaded grids, and dropped by a periodic clean-up once unreferenced. The maps of its
/// map id hold it (<see cref="MapRefCount"/>); <see cref="TerrainManager"/> lets it go after the last one.
/// <para>
/// Without terrain data (no data directory, or no file for a tile) a tile reads as vmangos'
/// empty <c>GridMap</c>: no height (<see cref="TerrainTile.InvalidHeightValue"/>), area flag 0
/// and no liquid — lookups degrade, nothing fails.
/// </para>
/// <para>Thread affinity: world thread, except <see cref="TryGetLoadedHeight"/> and <see cref="TryGetLoadedLiquidStatus"/>, which a map thread
/// outside the serial gate may call for tiles already in memory (tiles are freed only by the world tick's clean-up, never during the maps phase).</para>
/// </summary>
public sealed class TerrainInfo
{
    /// <summary>vmangos <c>TerrainInfo</c>: "clean up GridMap objects every minute".</summary>
    public const int CleanUpIntervalMs = 60 * 1000;

    private const int Size = GridDefines.MaxNumberOfGrids;

    private readonly TerrainManager _manager;
    private readonly TerrainTile?[] _tiles = new TerrainTile?[Size * Size];
    private readonly int[] _refs = new int[Size * Size];
    private long _cleanUpTimerMs = CleanUpIntervalMs;
    private int _mapRefs;

    internal TerrainInfo(uint mapId, TerrainManager manager)
    {
        MapId = mapId;
        _manager = manager;
    }

    public uint MapId { get; }

    /// <summary>Number of tiles currently held in memory (loaded files and empty placeholders).</summary>
    public int LoadedTileCount => _tiles.Count(t => t is not null);

    /// <summary>The tile under a world position (loaded on demand), or <see cref="TerrainTile.Empty"/> outside the map.</summary>
    public TerrainTile GetTile(float x, float y)
        => TerrainTile.TileOf(x, y) is { } tile ? GetTile(tile.X, tile.Y) : TerrainTile.Empty;

    /// <summary>A tile by index (vmangos <c>TerrainInfo::LoadMapAndVMap</c>).</summary>
    public TerrainTile GetTile(int tileX, int tileY)
    {
        int index = (tileX * Size) + tileY;
        return _tiles[index] ??= _manager.LoadTile(MapId, tileX, tileY);
    }

    /// <summary>
    /// The ground height at (x, y) from a tile already in memory, never loading one (a map thread outside the serial gate: tiles are
    /// shared by the instances of a map id and loaded and freed by the gated owner). The slot is read once, so a tile freed meanwhile is
    /// still a whole tile. False when the tile is not loaded (the caller asks the owner).
    /// </summary>
    public bool TryGetLoadedHeight(float x, float y, out float height)
    {
        height = 0f;
        if (TerrainTile.TileOf(x, y) is not { } index)
        {
            height = TerrainTile.Empty.GetHeight(x, y);
            return true;
        }

        TerrainTile? tile = Volatile.Read(ref _tiles[(index.X * Size) + index.Y]);
        if (tile is null)
        {
            return false;
        }

        height = tile.GetHeight(x, y);
        return true;
    }

    /// <summary>
    /// <see cref="GetLiquidStatus"/> from a tile already in memory, never loading one (a ground drape on a map thread outside the serial
    /// gate; the slot is read once, like <see cref="TryGetLoadedHeight"/>). False, with nothing else meaningful, when the tile is not in memory.
    /// </summary>
    public bool TryGetLoadedLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidStatus status, out LiquidData data)
    {
        status = LiquidStatus.NoWater;
        data = default;
        TerrainTile tile;
        if (TerrainTile.TileOf(x, y) is not { } index)
        {
            tile = TerrainTile.Empty;
        }
        else if (Volatile.Read(ref _tiles[(index.X * Size) + index.Y]) is { } loaded)
        {
            tile = loaded;
        }
        else
        {
            return false;
        }

        status = LiquidStatusOn(tile, x, y, z, requiredType, out data);
        return true;
    }

    /// <summary>Whether a tile is in memory.</summary>
    public bool IsTileLoaded(int tileX, int tileY) => _tiles[(tileX * Size) + tileY] is not null;

    /// <summary>The tile's reference count (grids holding it).</summary>
    public int RefCount(int tileX, int tileY) => _refs[(tileX * Size) + tileY];

    /// <summary>
    /// The maps holding this terrain (vmangos <c>Referencable</c>, GridMap.h:123-130): <see cref="TerrainManager.Acquire"/> counts a
    /// map in when it is made (<c>Map::Map</c>, Map.cpp:153) and <see cref="TerrainManager.Release"/> counts it out when it is unloaded
    /// (<c>Map::~Map</c>, Map.cpp:76-78).
    /// </summary>
    public int MapRefCount => _mapRefs;

    /// <summary>Count a map in (vmangos <c>Referencable::AddRef</c>).</summary>
    internal void AddMapRef() => _mapRefs++;

    /// <summary>Count a map out; true when it was the last one (vmangos <c>Referencable::Release</c>).</summary>
    internal bool ReleaseMapRef()
    {
        if (_mapRefs > 0)
        {
            _mapRefs--;
        }

        return _mapRefs == 0;
    }

    /// <summary>
    /// Whether a tile is still in memory or referenced by a grid: a terrain is let go only once its clean-up has freed every tile
    /// (<see cref="TerrainManager.Update"/>).
    /// </summary>
    internal bool HoldsAnyTile()
    {
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] is not null || _refs[i] > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos <c>TerrainInfo::Load</c>: reference a tile and make sure it is loaded.</summary>
    public TerrainTile Load(int tileX, int tileY)
    {
        _refs[(tileX * Size) + tileY]++;
        return GetTile(tileX, tileY);
    }

    /// <summary>vmangos <c>TerrainInfo::Unload</c>: drop a reference; the clean-up frees unreferenced tiles.</summary>
    public void Unload(int tileX, int tileY)
    {
        int index = (tileX * Size) + tileY;
        if (_tiles[index] is not null && _refs[index] > 0)
        {
            _refs[index]--;
        }
    }

    /// <summary>
    /// vmangos <c>TerrainInfo::CleanUpGrids</c>: once a minute, free every loaded tile nobody
    /// references. (vmangos starts the first run at a random 20–40 s; ArcaneCore uses the
    /// interval, so behaviour is deterministic.) Run once per world tick by
    /// <see cref="TerrainManager.Update"/>, never by the maps that share this terrain.
    /// </summary>
    public void CleanUp(long diffMs)
    {
        _cleanUpTimerMs -= diffMs;
        if (_cleanUpTimerMs > 0)
        {
            return;
        }

        for (int i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] is not null && _refs[i] == 0)
            {
                _tiles[i] = null;
            }
        }

        _cleanUpTimerMs = CleanUpIntervalMs;
    }

    /// <summary>
    /// The ground height under a position, or <see cref="TerrainTile.InvalidHeightValue"/> where
    /// there is no data (vmangos <c>TerrainInfo::GetHeightStatic</c> without vmaps: the raw
    /// <c>.map</c> surface; <paramref name="z"/> only matters to the vmap search, which
    /// <see cref="Collision.MapCollision.GetHeight"/> runs on top of this; this method and <c>Map.GetHeight</c> skip it).
    /// </summary>
    public float GetHeight(float x, float y, float z)
    {
        _ = z;
        return GetTile(x, y).GetHeight(x, y);
    }

    /// <summary>
    /// The area flag at a position (vmangos <c>TerrainInfo::GetAreaFlag</c>, GridMap.cpp:914-950): the explore flag of the area of the WMO
    /// group the position is inside (<see cref="WmoAreaTable"/>), else the tile's cell value. The group is the one the world's model
    /// service finds (<see cref="TryGetWmoGroup"/>); without model data, without <c>wmo_area_template</c> rows, in a group with no row or
    /// no area, or whose area is not in the area table, the cell value is the answer, as it was before WMO areas were read.
    /// </summary>
    public ushort GetAreaFlag(float x, float y, float z)
    {
        WmoAreaTable wmoAreas = _manager.WmoAreas;
        if (wmoAreas.Count != 0 && _manager.Collision?.LineOfSight is { Enabled: true } models
            && TryGetWmoGroup(models, x, y, z, out ModelAreaInfo group)
            && wmoAreas.TryGetAreaId(group.RootId, group.AdtId, group.GroupId, out uint areaId)
            && _manager.Areas.GetById(areaId) is { } area)
        {
            // vmangos keeps the flag in a uint16.
            return unchecked((ushort)area.ExploreFlag);
        }

        return GetTile(x, y).GetAreaFlag(x, y);
    }

    /// <summary>
    /// vmangos <c>TerrainInfo::GetAreaInfo</c> (GridMap.cpp:892-912): the WMO group a point is inside, found from 1 yard above it. A group
    /// under terrain that rises to the point (the terrain above the group's floor and no more than 2 yards over the raised point) does not
    /// count: the position is on the ground over a building, not in it. False outside every WMO, without model data, and where the model
    /// tile is not loaded (vmangos loads it with the grid).
    /// </summary>
    public bool TryGetWmoGroup(ILineOfSight models, float x, float y, float z, out ModelAreaInfo group)
    {
        ArgumentNullException.ThrowIfNull(models);
        z += 1.0f;
        if (!models.TryGetAreaInfo(MapId, x, y, z, out group))
        {
            return false;
        }

        float terrain = GetTile(x, y).GetHeight(x, y);
        return !(z + 2.0f > terrain && terrain > group.GroundZ);
    }

    /// <summary>The zone and area at a position (vmangos <c>TerrainInfo::GetZoneAndAreaId</c>).</summary>
    public (uint ZoneId, uint AreaId) GetZoneAndAreaId(float x, float y, float z)
        => _manager.Areas.GetZoneAndAreaId(GetAreaFlag(x, y, z), MapId);

    /// <summary>
    /// <see cref="GetZoneAndAreaId"/> for a caller that must not read a file on its thread (bot planning over far positions, which used to
    /// read whole tiles inside the world tick). On a tile in memory, outside the map or without terrain data the answer is
    /// <see cref="GetZoneAndAreaId"/>'s. On a tile not in memory it is the tile's own cell value (<see cref="TerrainManager.TryGetBackgroundAreaFlag"/>,
    /// the area section read once on the thread pool and kept; no WMO group's area, which needs the tile's heights), and false until that read
    /// is done: ask again on a later think. The tile cache is never touched, so no tile is loaded or pinned. World thread.
    /// </summary>
    public bool TryGetZoneAndAreaIdWithoutLoading(float x, float y, float z, out (uint ZoneId, uint AreaId) zoneAndArea)
    {
        if (TerrainTile.TileOf(x, y) is not { } index || !_manager.Enabled || _tiles[(index.X * Size) + index.Y] is not null)
        {
            zoneAndArea = GetZoneAndAreaId(x, y, z);
            return true;
        }

        if (_manager.TryGetBackgroundAreaFlag(MapId, index.X, index.Y, x, y, out ushort flag))
        {
            zoneAndArea = _manager.Areas.GetZoneAndAreaId(flag, MapId);
            return true;
        }

        zoneAndArea = (0, 0);
        return false;
    }

    /// <summary>
    /// Read the area flags of all this map's tiles on the thread pool now (<see cref="TerrainManager.WarmBackgroundAreas"/>), for a caller
    /// that will ask <see cref="TryGetZoneAndAreaIdWithoutLoading"/> about far positions; once per map id. World thread.
    /// </summary>
    public void WarmAreasInBackground() => _manager.WarmBackgroundAreas(MapId);

    /// <summary>
    /// Where a position is relative to liquid (vmangos <c>TerrainInfo::getLiquidStatus</c> without
    /// vmaps): the tile's answer, kept only when the surface is above the ground height.
    /// </summary>
    public LiquidStatus GetLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
    {
        return LiquidStatusOn(GetTile(x, y), x, y, z, requiredType, out data);
    }

    /// <summary>
    /// <see cref="GetLiquidStatus"/> over a tile already in hand (the live path passes the cached tile, a background read passes
    /// <see cref="TerrainManager.ReadTileDetached"/>'s): one implementation for both. Pure over the tile; any thread.
    /// </summary>
    public static LiquidStatus LiquidStatusOn(TerrainTile tile, float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
    {
        data = default;
        float ground = tile.GetHeight(x, y);
        LiquidStatus status = tile.GetLiquidStatus(x, y, z, requiredType, out LiquidData mapData);
        if (status != LiquidStatus.NoWater && mapData.Level > ground)
        {
            data = mapData;
            return status;
        }

        return LiquidStatus.NoWater;
    }

    /// <summary>
    /// The liquid surface or the ground under a position, whichever is higher (vmangos
    /// <c>TerrainInfo::GetWaterOrGroundLevel</c> without vmaps and without the swim offset);
    /// <see cref="TerrainTile.InvalidHeightValue"/> where there is no terrain data.
    /// </summary>
    public float GetWaterOrGroundLevel(float x, float y, float z)
    {
        float ground = GetHeight(x, y, z);
        LiquidStatus status = GetLiquidStatus(x, y, ground, LiquidTypeFlags.AllLiquids, out LiquidData liquid);
        return Math.Max(status != LiquidStatus.NoWater ? liquid.Level : ground, ground);
    }

    /// <summary>The liquid surface height under a position (vmangos <c>GridMap::getLiquidLevel</c>).</summary>
    public float GetLiquidLevel(float x, float y) => GetTile(x, y).GetLiquidLevel(x, y);
}
