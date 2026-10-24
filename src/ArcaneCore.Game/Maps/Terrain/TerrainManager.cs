using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// Terrain for every map of a world (vmangos <c>TerrainManager</c>): where the extractor output
/// lives, one <see cref="TerrainInfo"/> per map id, and the area table zone lookups use. A map
/// holds the terrain of its id from <see cref="Acquire"/> to <see cref="Release"/>; the terrain
/// goes once the last map of the id is unloaded and the clean-up has freed its tiles.
/// <para>
/// The data directory is optional. When it is empty or has no <c>maps/</c> folder the manager is
/// disabled and every tile reads as empty (logged once); a missing file is normal (instances and
/// open sea have none — vmangos <c>GridMap::loadData</c> "Not return error if file not found");
/// an unreadable or incompatible file is logged as an error and also reads as empty.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class TerrainManager
{
    private readonly Dictionary<uint, TerrainInfo> _maps = [];
    private readonly List<uint> _unloadRequests = [];
    private readonly ILogger _logger;
    private readonly string? _mapsDirectory;
    private readonly TilePrefetchCache<TerrainTile> _prefetch = new();

    public TerrainManager(string? dataDirectory, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            string maps = Path.Combine(dataDirectory, "maps");
            if (Directory.Exists(maps))
            {
                _mapsDirectory = maps;
                _logger.LogInformation("Terrain: reading .map files from {Directory}", maps);
            }
            else
            {
                _logger.LogWarning("Terrain: {Directory} not found; heights, areas and liquids are unavailable", maps);
            }
        }
        else
        {
            _logger.LogInformation("Terrain: no data directory configured (World:Maps:DataDirectory); heights, areas and liquids are unavailable");
        }
    }

    /// <summary>Whether a <c>maps/</c> directory was found.</summary>
    public bool Enabled => _mapsDirectory is not null;

    /// <summary>Area/zone lookup table (replaced when the world database is loaded).</summary>
    public AreaTable Areas { get; set; } = AreaTable.Empty;

    /// <summary>
    /// The area of each WMO group (replaced when the world database is loaded). While it is empty, a position inside a WMO reads the
    /// terrain's area, as before the table existed.
    /// </summary>
    public WmoAreaTable WmoAreas { get; set; } = WmoAreaTable.Empty;

    /// <summary>
    /// The world's collision services, whose model query names the WMO group a position is in (<see cref="TerrainInfo.GetAreaFlag"/>); read
    /// at each lookup, so a model service installed after startup is used. Null: no WMO areas (a terrain built on its own).
    /// </summary>
    public WorldCollision? Collision { get; set; }

    /// <summary>Number of tile files read so far (diagnostics and tests).</summary>
    public int FilesLoaded { get; private set; }

    /// <summary>
    /// The terrain of a map id (created on first use; vmangos <c>TerrainManager::LoadTerrain</c>, GridMap.cpp:1238-1253). A map holds
    /// its own (<see cref="Acquire"/>); anything else asks again for each use: once the last map of the id has been unloaded the
    /// terrain goes (<see cref="Release"/>), and one kept from before would still answer but load tiles no clean-up frees.
    /// </summary>
    public TerrainInfo For(uint mapId)
    {
        if (!_maps.TryGetValue(mapId, out TerrainInfo? terrain))
        {
            terrain = new TerrainInfo(mapId, this);
            _maps[mapId] = terrain;
        }

        return terrain;
    }

    /// <summary>
    /// A new map's hold on the terrain of its map id (vmangos <c>Map::Map</c>: <c>sTerrainMgr.LoadTerrain(id)</c>, Map.cpp:127, then
    /// <c>m_terrainData-&gt;AddRef()</c>, Map.cpp:153), shared by every instance of the id and kept until <see cref="Release"/>. World
    /// thread, or inside the serial gate for a map made while maps update in parallel; never at the same time as <see cref="Release"/>
    /// or <see cref="Update"/>, which run on the world thread after the maps.
    /// </summary>
    internal TerrainInfo Acquire(uint mapId)
    {
        TerrainInfo terrain = For(mapId);
        terrain.AddMapRef();
        return terrain;
    }

    /// <summary>
    /// An unloaded map lets go of its terrain (vmangos <c>Map::~Map</c>, Map.cpp:76-78, after its grids have released their tiles). The
    /// last map of a map id asks for the terrain to go (<c>TerrainManager::UnloadTerrain</c>, GridMap.cpp:1255-1273), except with
    /// <paramref name="gridUnload"/> off (<see cref="ArcaneCore.Game.Maps.Grid.MapOptions.GridUnload"/>), where vmangos keeps it
    /// (GridMap.cpp:1257-1258).
    /// vmangos deletes it there and then, its tiles with it; here it goes in <see cref="Update"/> once no tile of it is in memory or
    /// referenced, so the tiles keep the minute clean-up's timing and an instance made again within that minute takes the same terrain.
    /// World thread.
    /// </summary>
    internal void Release(TerrainInfo terrain, bool gridUnload)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (terrain.ReleaseMapRef() && gridUnload && !_unloadRequests.Contains(terrain.MapId))
        {
            _unloadRequests.Add(terrain.MapId);
        }
    }

    /// <summary>
    /// vmangos <c>TerrainManager::Update</c> (GridMap.cpp:1275-1280): every map's terrain clean-up (<see cref="TerrainInfo.CleanUp"/>),
    /// once per world tick. One <see cref="TerrainInfo"/> serves every instance of its map id, so a clean-up run by each instance would
    /// count the tick once per instance, and one whose instances are all gone would never run again. Then the terrains whose last map
    /// was unloaded (<see cref="Release"/>) go once their clean-up has freed every tile. World thread, after the requested map unloads.
    /// </summary>
    public void Update(long diffMs)
    {
        foreach (TerrainInfo terrain in _maps.Values)
        {
            terrain.CleanUp(diffMs);
        }

        if (_unloadRequests.Count > 0)
        {
            UnloadReleasedTerrain();
        }
    }

    /// <summary>
    /// vmangos <c>TerrainManager::UnloadTerrain</c> (GridMap.cpp:1264-1271): a terrain no map holds is erased; one a new map has taken
    /// since stays, and asks again when that map goes. A terrain still holding a tile waits for its clean-up.
    /// </summary>
    private void UnloadReleasedTerrain()
    {
        for (int i = _unloadRequests.Count - 1; i >= 0; i--)
        {
            uint mapId = _unloadRequests[i];
            if (_maps.TryGetValue(mapId, out TerrainInfo? terrain) && terrain.MapRefCount == 0)
            {
                if (terrain.HoldsAnyTile())
                {
                    continue;
                }

                _maps.Remove(mapId);
            }

            _unloadRequests.RemoveAt(i);
        }
    }

    /// <summary>Terrains in memory, one per map id (diagnostics and tests).</summary>
    internal int TerrainCount => _maps.Count;

    /// <summary>Prefetches the loader took instead of reading the file itself (diagnostics and tests).</summary>
    internal int PrefetchHits => _prefetch.Hits;

    /// <summary>
    /// Read and parse a tile's <c>.map</c> file on the thread pool so a later load only installs it (<see cref="TilePrefetchCache{T}"/>);
    /// nothing happens without terrain data or when the tile is already in memory. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null || (_maps.TryGetValue(mapId, out TerrainInfo? terrain) && terrain.IsTileLoaded(tileX, tileY)))
        {
            return;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        _prefetch.Request(mapId, tileX, tileY, () => File.Exists(path) ? TerrainTile.Parse(File.ReadAllBytes(path)) : null);
    }

    /// <summary>
    /// The zone and area at a position, read straight from the tile's <c>.map</c> file without the tile cache (vmangos
    /// <c>TerrainInfo::GetZoneAndAreaId</c> from the tile's cell alone: the WMO group's area (<see cref="TerrainInfo.GetAreaFlag"/>) needs the
    /// model service, which belongs to the world thread, so inside a WMO it can differ from the cached lookup): (0, 0) without terrain data, outside the map or for a missing or
    /// unreadable file. Thread affinity: any thread. It reads only the data directory (fixed at construction) and the given
    /// <paramref name="areas"/> (immutable), never the world-thread tile cache. It reads one file per call, so it is for background work.
    /// </summary>
    public (uint ZoneId, uint AreaId) ReadZoneAndAreaIdDetached(uint mapId, float x, float y, AreaTable areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (_mapsDirectory is null || TerrainTile.TileOf(x, y) is not { } tile)
        {
            return (0, 0);
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tile.X, tile.Y));
        try
        {
            if (!File.Exists(path))
            {
                return (0, 0);
            }

            return areas.GetZoneAndAreaId(TerrainTile.Parse(File.ReadAllBytes(path)).GetAreaFlag(x, y), mapId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            FaultLedger.Shared.Record("terrain.area-lookup", ex);
            if (ex is InvalidDataException)
            {
                AnomalyLedger.Shared.Report("terrain", "tile-corrupt", $"map {mapId}", ex.Message);
            }
            _logger.LogWarning(ex, "Terrain: could not read {Path}", path);
            return (0, 0);
        }
    }

    /// <summary>
    /// One tile read straight from its <c>.map</c> file, without the tile cache or the prefetch (so no counter and no shared state is
    /// touched): <see cref="TerrainTile.Empty"/> without terrain data or for a missing or unreadable file. Thread affinity: any thread
    /// (it reads only the data directory, fixed at construction). It reads a file per call, so it is for background work.
    /// </summary>
    public TerrainTile ReadTileDetached(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null)
        {
            return TerrainTile.Empty;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        try
        {
            return File.Exists(path) ? TerrainTile.Parse(File.ReadAllBytes(path)) : TerrainTile.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            FaultLedger.Shared.Record("terrain.tile-read", ex);
            if (ex is InvalidDataException)
            {
                AnomalyLedger.Shared.Report("terrain", "tile-corrupt", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
            }
            _logger.LogWarning(ex, "Terrain: could not read {Path}", path);
            return TerrainTile.Empty;
        }
    }

    internal TerrainTile LoadTile(uint mapId, int tileX, int tileY)
    {
        if (_mapsDirectory is null)
        {
            return TerrainTile.Empty;
        }

        if (_prefetch.TryTake(mapId, tileX, tileY, out TerrainTile prefetched))
        {
            FilesLoaded++;
            return prefetched;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return TerrainTile.Empty;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            FaultLedger.Shared.Record("terrain.tile-read", ex);
            _logger.LogError(ex, "Terrain: could not read {Path}", path);
            return TerrainTile.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            FaultLedger.Shared.Record("terrain.tile-read", ex);
            _logger.LogError(ex, "Terrain: could not read {Path}", path);
            return TerrainTile.Empty;
        }

        try
        {
            TerrainTile tile = TerrainTile.Parse(bytes);
            FilesLoaded++;
            return tile;
        }
        catch (InvalidDataException ex)
        {
            // vmangos: "Map file '%s' is non-compatible version (outdated?)" — logged, tile left empty.
            FaultLedger.Shared.Record("terrain.tile-parse", ex);
            AnomalyLedger.Shared.Report("terrain", "tile-corrupt", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
            _logger.LogError(ex, "Terrain: {Path} is not a usable z1.4 .map file ({Reason}); re-extract the maps", path, ex.Message);
            return TerrainTile.Empty;
        }
    }
}
