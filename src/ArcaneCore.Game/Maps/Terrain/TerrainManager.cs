using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
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

    /// <summary>Whether a prefetch of the tile is waiting to be taken (<paramref name="finished"/>: its read is done) (diagnostics and tests).</summary>
    internal bool IsPrefetched(uint mapId, int tileX, int tileY, out bool finished)
    {
        finished = !_prefetch.IsRunning(mapId, tileX, tileY);
        return _prefetch.Contains(mapId, tileX, tileY);
    }

    /// <summary>
    /// Read and parse a tile's <c>.map</c> file on the thread pool so a later load only installs it (<see cref="TilePrefetchCache{T}"/>);
    /// nothing happens without terrain data or when the tile is already in memory. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY) => Prefetch(mapId, tileX, tileY, arrival: false);

    /// <summary><see cref="Prefetch(uint, int, int)"/> for a teleport's destination, against the arrival budget (<see cref="TilePrefetchCache{T}.MaxArrivalEntries"/>). World thread.</summary>
    public void PrefetchArrival(uint mapId, int tileX, int tileY) => Prefetch(mapId, tileX, tileY, arrival: true);

    private void Prefetch(uint mapId, int tileX, int tileY, bool arrival)
    {
        if (_mapsDirectory is null || (_maps.TryGetValue(mapId, out TerrainInfo? terrain) && terrain.IsTileLoaded(tileX, tileY)))
        {
            return;
        }

        string path = Path.Combine(_mapsDirectory, TerrainTile.FileName(mapId, tileX, tileY));
        _prefetch.Request(mapId, tileX, tileY, () => File.Exists(path) ? TerrainTile.Parse(File.ReadAllBytes(path)) : null, arrival);
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
            _logger.LogFault("terrain-area-lookup", ex, "Terrain: could not read {Path}", path);
            return (0, 0);
        }
    }

    /// <summary>
    /// The area flags of a tile for a lookup that must not read a file on the calling thread (<see cref="TerrainInfo.TryGetZoneAndAreaIdWithoutLoading"/>):
    /// true with the flag of the cell under (<paramref name="x"/>, <paramref name="y"/>) once the tile's area section has been read in the
    /// background; false while it is not, after asking for it (a read on the thread pool, once per tile). The flags are static content,
    /// 512 bytes a tile, kept for the manager's life apart from the tile cache, so a far lookup neither pins a whole tile nor reads it again.
    /// Without terrain data every tile reads as empty: true, flag 0. A lookup never waits for ever: a read still running after
    /// <see cref="BackgroundAreaPendingLimitMs"/> reads as empty (flag 0; the real flags are used once the read lands), and a read that failed
    /// for want of the file (sharing violation, permissions) reads as empty for <see cref="BackgroundAreaRetryMs"/>, then is read again. World thread
    /// (the per-map table is made here; the pool only fills slots).
    /// </summary>
    internal bool TryGetBackgroundAreaFlag(uint mapId, int tileX, int tileY, float x, float y, out ushort flag)
    {
        flag = 0;
        if (_mapsDirectory is null)
        {
            return true;
        }

        AreaAtlas atlas = AtlasOf(mapId);
        int index = (tileX * GridDefines.MaxNumberOfGrids) + tileY;
        if (Volatile.Read(ref atlas.Tiles[index]) is { } flags)
        {
            flag = flags[TerrainTile.AreaCellIndex(x, y)];
            return true;
        }

        long now = Environment.TickCount64;
        switch (Volatile.Read(ref atlas.State[index]))
        {
            case SlotIdle:
                StartAreaRead(atlas, mapId, tileX, tileY, index, SlotIdle);
                return false;
            case SlotFailed:
                if (now - Volatile.Read(ref atlas.ChangedMs[index]) < BackgroundAreaRetryMs)
                {
                    return true; // the tile reads as empty for now, as the tile cache reads an unreadable file
                }

                StartAreaRead(atlas, mapId, tileX, tileY, index, SlotFailed);
                return false;
            default: // SlotReading
                long started = Volatile.Read(ref atlas.ChangedMs[index]);
                return started != 0 && now - started > BackgroundAreaPendingLimitMs; // given up on: no area, nothing published
        }
    }

    /// <summary>Claim an area slot (<paramref name="from"/> to reading) and read it on the pool; nothing when another caller claimed it first.</summary>
    private void StartAreaRead(AreaAtlas atlas, uint mapId, int tileX, int tileY, int index, int from)
    {
        if (Interlocked.CompareExchange(ref atlas.State[index], SlotReading, from) != from)
        {
            return;
        }

        Volatile.Write(ref atlas.ChangedMs[index], Environment.TickCount64);
        Interlocked.Increment(ref _areaReadsInFlight);
        string path = Path.Combine(_mapsDirectory!, TerrainTile.FileName(mapId, tileX, tileY));
        _ = Task.Run(() =>
        {
            try
            {
                FillAreaSlot(atlas, mapId, tileX, tileY, index, path);
            }
            finally
            {
                Interlocked.Decrement(ref _areaReadsInFlight);
            }
        });
    }

    /// <summary>
    /// Read the area flags of every tile file of a map in the background, one after another on one pool thread, so the lookups of
    /// <see cref="TryGetBackgroundAreaFlag"/> rarely wait (about 560 bytes a file). Once per map; nothing without terrain data. A tile that
    /// fails does not end the pass: the next one is read. World thread.
    /// </summary>
    internal void WarmBackgroundAreas(uint mapId)
    {
        if (_mapsDirectory is null)
        {
            return;
        }

        AreaAtlas atlas = AtlasOf(mapId);
        if (atlas.Warmed)
        {
            return;
        }

        atlas.Warmed = true;
        string directory = _mapsDirectory;
        Interlocked.Increment(ref _areaReadsInFlight);
        _ = Task.Run(() =>
        {
            try
            {
                for (int tileX = 0; tileX < GridDefines.MaxNumberOfGrids; tileX++)
                {
                    for (int tileY = 0; tileY < GridDefines.MaxNumberOfGrids; tileY++)
                    {
                        int index = (tileX * GridDefines.MaxNumberOfGrids) + tileY;
                        string path = Path.Combine(directory, TerrainTile.FileName(mapId, tileX, tileY));
                        if (Volatile.Read(ref atlas.Tiles[index]) is null && File.Exists(path)
                            && Interlocked.CompareExchange(ref atlas.State[index], SlotReading, SlotIdle) == SlotIdle)
                        {
                            Volatile.Write(ref atlas.ChangedMs[index], Environment.TickCount64);
                            FillAreaSlot(atlas, mapId, tileX, tileY, index, path);
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _areaReadsInFlight);
            }
        });
    }

    /// <summary>Background area reads (<see cref="TryGetBackgroundAreaFlag"/>, <see cref="WarmBackgroundAreas"/>) not finished yet (tests).</summary>
    internal int BackgroundAreaReadsInFlight => Volatile.Read(ref _areaReadsInFlight);

    /// <summary>How long a lookup may find its tile's area read still running before it reads the tile as empty (test seam; the playerbots' <c>TileWait.MaxMs</c> is the same 15 s).</summary>
    internal long BackgroundAreaPendingLimitMs { get; set; } = 15_000;

    /// <summary>How long an area read that failed for want of the file reads as empty before it is tried again (the tile cache's clean-up interval; test seam).</summary>
    internal long BackgroundAreaRetryMs { get; set; } = TerrainInfo.CleanUpIntervalMs;

    /// <summary>Test seam: reads a tile file's area section (<see cref="TerrainTile.ReadAreaFlags"/>); a test installs one that fails or blocks.</summary>
    internal Func<string, ushort[]> AreaFileReader { get; set; } = TerrainTile.ReadAreaFlags;

    /// <summary>Test seam: how many tile area sections the calling thread has read (<see cref="TryGetBackgroundAreaFlag"/>' pool reads).</summary>
    internal static int AreaReadsOnThisThread => t_areaReadsOnThisThread;

    [ThreadStatic]
    private static int t_areaReadsOnThisThread;

    private const int SlotIdle = 0;
    private const int SlotReading = 1;
    private const int SlotFailed = 2;

    private int _areaReadsInFlight;
    private readonly Dictionary<uint, AreaAtlas> _areaAtlas = [];

    /// <summary>No area anywhere: a missing or unreadable file (the tile cache reads it as <see cref="TerrainTile.Empty"/>, flag 0).</summary>
    private static readonly ushort[] NoAreas = new ushort[16 * 16];

    private AreaAtlas AtlasOf(uint mapId)
    {
        if (!_areaAtlas.TryGetValue(mapId, out AreaAtlas? atlas))
        {
            atlas = new AreaAtlas();
            _areaAtlas[mapId] = atlas;
        }

        return atlas;
    }

    /// <summary>
    /// Read one claimed slot and publish the result: the flags, <see cref="NoAreas"/> for a file that will not read any better later (missing,
    /// corrupt, any other failure), or the failed state for a transient failure (retried after <see cref="BackgroundAreaRetryMs"/>). Never
    /// throws except for out of memory, so a pass over many tiles goes on and a slot is never left reading. Pool thread.
    /// </summary>
    private void FillAreaSlot(AreaAtlas atlas, uint mapId, int tileX, int tileY, int index, string path)
    {
        try
        {
            if (ReadAreaFlagsDetached(mapId, tileX, tileY, path) is { } flags)
            {
                Volatile.Write(ref atlas.Tiles[index], flags);
            }
            else
            {
                Volatile.Write(ref atlas.ChangedMs[index], Environment.TickCount64);
                Volatile.Write(ref atlas.State[index], SlotFailed);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("terrain.area-task", ex);
            _logger.LogFault("terrain-area-task", ex, "Terrain: reading the areas of {Path} failed; the tile reads as empty", path);
            Volatile.Write(ref atlas.Tiles[index], NoAreas);
        }
    }

    /// <summary>The tile's area flags; <see cref="NoAreas"/> for a missing or corrupt file; null for a failure that may pass (IO, access).</summary>
    private ushort[]? ReadAreaFlagsDetached(uint mapId, int tileX, int tileY, string path)
    {
        t_areaReadsOnThisThread++;
        try
        {
            return File.Exists(path) ? AreaFileReader(path) : NoAreas;
        }
        catch (InvalidDataException ex)
        {
            FaultLedger.Shared.Record("terrain.area-read", ex);
            AnomalyLedger.Shared.Report("terrain", "tile-corrupt", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
            _logger.LogFault("terrain-area-read", ex, "Terrain: {Path} has no usable area section; the tile reads as empty", path);
            return NoAreas;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FaultLedger.Shared.Record("terrain.area-read", ex);
            _logger.LogFault("terrain-area-read", ex, "Terrain: could not read the areas of {Path}; trying again later", path);
            return null;
        }
    }

    /// <summary>One map's background area flags: a slot per tile, filled once by the pool (<see cref="TryGetBackgroundAreaFlag"/>).</summary>
    private sealed class AreaAtlas
    {
        public readonly ushort[]?[] Tiles = new ushort[]?[GridDefines.MaxNumberOfGrids * GridDefines.MaxNumberOfGrids];

        /// <summary>Idle, reading (started by a lookup or the warm-up; one caller at a time) or failed (waiting to be retried).</summary>
        public readonly int[] State = new int[GridDefines.MaxNumberOfGrids * GridDefines.MaxNumberOfGrids];

        /// <summary>When the slot last changed to reading or failed (<see cref="Environment.TickCount64"/>; 0 until set).</summary>
        public readonly long[] ChangedMs = new long[GridDefines.MaxNumberOfGrids * GridDefines.MaxNumberOfGrids];

        /// <summary>Whether <see cref="WarmBackgroundAreas"/> ran for the map (world thread).</summary>
        public bool Warmed;
    }

    // Test seam kept in release builds too (the tests run in either): one thread-local increment per tile read, next to a file read.
    [ThreadStatic]
    private static int t_detachedReadsOnThisThread;

    /// <summary>Test seam: how many <see cref="ReadTileDetached"/> calls the calling thread has made. A test reads it on the world thread to prove none ran there.</summary>
    internal static int DetachedReadsOnThisThread => t_detachedReadsOnThisThread;

    /// <summary>
    /// One tile read straight from its <c>.map</c> file, without the tile cache or the prefetch (so no counter and no shared state is
    /// touched): <see cref="TerrainTile.Empty"/> without terrain data or for a missing or unreadable file. Thread affinity: any thread
    /// (it reads only the data directory, fixed at construction). It reads a file per call, so it is for background work.
    /// </summary>
    public TerrainTile ReadTileDetached(uint mapId, int tileX, int tileY)
    {
        t_detachedReadsOnThisThread++;
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
            _logger.LogFault("terrain-tile-read", ex, "Terrain: could not read {Path}", path);
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
