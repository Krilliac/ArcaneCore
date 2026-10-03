using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// The terrain of one map (vmangos <c>TerrainInfo</c>, GridMap.cpp): its 64 × 64 tiles, loaded
/// from the data directory the first time a position on them is looked up, reference-counted by
/// the map's loaded grids, and dropped by a periodic clean-up once unreferenced.
/// <para>
/// Without terrain data (no data directory, or no file for a tile) a tile reads as vmangos'
/// empty <c>GridMap</c>: no height (<see cref="TerrainTile.InvalidHeightValue"/>), area flag 0
/// and no liquid — lookups degrade, nothing fails.
/// </para>
/// <para>Thread affinity: world thread.</para>
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

    /// <summary>Whether a tile is in memory.</summary>
    public bool IsTileLoaded(int tileX, int tileY) => _tiles[(tileX * Size) + tileY] is not null;

    /// <summary>The tile's reference count (grids holding it).</summary>
    public int RefCount(int tileX, int tileY) => _refs[(tileX * Size) + tileY];

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
    /// interval, so behaviour is deterministic.)
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
    /// <c>.map</c> surface; <paramref name="z"/> only matters to the vmap search ArcaneCore
    /// does not have yet).
    /// </summary>
    public float GetHeight(float x, float y, float z)
    {
        _ = z;
        return GetTile(x, y).GetHeight(x, y);
    }

    /// <summary>
    /// The area flag at a position (vmangos <c>TerrainInfo::GetAreaFlag</c> without WMO area
    /// info: the tile's cell value).
    /// </summary>
    public ushort GetAreaFlag(float x, float y, float z)
    {
        _ = z;
        return GetTile(x, y).GetAreaFlag(x, y);
    }

    /// <summary>The zone and area at a position (vmangos <c>TerrainInfo::GetZoneAndAreaId</c>).</summary>
    public (uint ZoneId, uint AreaId) GetZoneAndAreaId(float x, float y, float z)
        => _manager.Areas.GetZoneAndAreaId(GetAreaFlag(x, y, z), MapId);

    /// <summary>
    /// Where a position is relative to liquid (vmangos <c>TerrainInfo::getLiquidStatus</c> without
    /// vmaps): the tile's answer, kept only when the surface is above the ground height.
    /// </summary>
    public LiquidStatus GetLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
    {
        data = default;
        TerrainTile tile = GetTile(x, y);
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
