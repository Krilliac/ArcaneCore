using System.Numerics;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>Immutable identity of one navmesh data source; it carries no live collision state.</summary>
public readonly record struct NavMeshDatasetDescriptor(string Directory, Guid Identity);

/// <summary>
/// The navmesh-backed <see cref="IPathfinder"/> (vmangos <c>MMapManager</c> + <c>PathFinder</c>):
/// one <see cref="NavMesh"/> per map from <c>NNN.mmap</c>, tiles loaded and released with the
/// map's grids (<see cref="ICollisionTileLifecycle"/>), paths by <see cref="NavMeshQuery"/>.
/// <para>
/// A map without a <c>.mmap</c> answers straight lines (<see cref="PathType.NotUsingPath"/>), as
/// vmangos does for maps without navmesh. With a navmesh, a start or end farther than the search
/// box from any loaded polygon (on a loaded tile; an endpoint on an unloaded tile goes straight,
/// <see cref="PathType.Normal"/> | <see cref="PathType.NotUsingPath"/>, as vmangos' <c>HaveTiles</c> shortcut) answers <see cref="PathType.NoPath"/> (a two-point shortcut, so a
/// caller may still decide to move straight); an end off the mesh but above or below it is
/// projected and the path is <see cref="PathType.Incomplete"/>. Corrupt files are logged and read
/// as missing; nothing here throws into the world thread.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class NavMeshPathfinder : IMapAwarePathfinder, ICollisionTileLifecycle, ICollisionTilePrefetch
{
    /// <summary>vmangos <c>PathFinder</c> search box half-extents (Recast x, y, z) for the nearest polygon.</summary>
    public static readonly Vector3 NearExtents = new(3, 5, 3);

    /// <summary>Second, taller box for points high above or below the mesh (flagged as off the mesh).</summary>
    public static readonly Vector3 FarExtents = new(3, 200, 3);

    private readonly string _directory;
    private readonly ILogger _logger;
    public NavMeshDatasetDescriptor Dataset { get; }
    private readonly Dictionary<uint, NavMesh?> _meshes = [];
    private readonly TilePrefetchCache<NavMeshTile> _prefetch = new();

    public NavMeshPathfinder(string directory, ILogger? logger = null, int maxConcurrentTileReads = DefaultConcurrentTileReads)
        : this(directory, logger, Guid.NewGuid(), maxConcurrentTileReads)
    {
    }

    private NavMeshPathfinder(string directory, ILogger? logger, Guid identity, int maxConcurrentTileReads = DefaultConcurrentTileReads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger ?? NullLogger.Instance;
        MaxConcurrentTileReads = Math.Clamp(maxConcurrentTileReads, 1, 16);
        _tileReadSlots = new SemaphoreSlim(MaxConcurrentTileReads, MaxConcurrentTileReads);
        Dataset = new(_directory, identity);
    }

    public bool Enabled => true;

    /// <summary>
    /// A private, serially used ground-navigation context over the same immutable data directory.
    /// No live tiles, query state, prefetch jobs or collision-model service are shared with the owner.
    /// </summary>
    public NavMeshPathfinder CreateIndependent() => CreateIndependent(Dataset, _logger);

    /// <summary>Create a private context for a detached request without consulting live collision services.</summary>
    public static NavMeshPathfinder CreateIndependent(NavMeshDatasetDescriptor dataset, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset.Directory);
        if (dataset.Identity == Guid.Empty) throw new ArgumentException("dataset identity is required", nameof(dataset));
        return new(dataset.Directory, logger, dataset.Identity);
    }

    /// <summary>
    /// The collision-model line test a flier's straight shortcut needs (vmangos
    /// <c>Map::FindCollisionModel</c>); <see cref="WorldCollision.Install"/> keeps it current.
    /// Without vmaps nothing blocks, as everywhere else.
    /// </summary>
    public ILineOfSight LineOfSight { get; set; } = OpenLineOfSight.Instance;

    /// <summary>Whether <c>NNN.mmap</c> loaded for the map (vmangos: a navmesh exists for it).</summary>
    public bool HasNavigationData(uint mapId) => GetNavMesh(mapId) is not null;

    /// <summary>The map's navmesh, reading <c>NNN.mmap</c> on first use; null when the map has none (or it is unusable).</summary>
    public NavMesh? GetNavMesh(uint mapId)
    {
        if (_meshes.TryGetValue(mapId, out NavMesh? cached))
        {
            return cached;
        }

        NavMesh? mesh = null;
        string path = Path.Combine(_directory, NavMeshFormat.ParamsFileName(mapId));
        if (ReadFile(path) is { } bytes)
        {
            try
            {
                mesh = new NavMesh(mapId, NavMeshParams.Parse(bytes));
                _logger.LogInformation("MMaps: map {MapId} navmesh parameters loaded", mapId);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError("MMaps: {Path} is not usable navmesh parameters ({Reason}); map {MapId} paths are straight lines", path, ex.Message, mapId);
            }
        }

        _meshes[mapId] = mesh;
        return mesh;
    }

    /// <summary>Load a terrain tile's navmesh tile (vmangos <c>MMapManager::loadMap</c>). False when nothing was loaded.</summary>
    public bool LoadTile(uint mapId, int tileX, int tileY)
        => LoadTile(mapId, tileX, tileY, out _);

    private bool LoadTile(uint mapId, int tileX, int tileY, out bool prefetchHit)
        => LoadTile(mapId, tileX, tileY, CancellationToken.None, out prefetchHit);

    private bool LoadTile(uint mapId, int tileX, int tileY, CancellationToken cancellationToken, out bool prefetchHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        prefetchHit = false;
        if (GetNavMesh(mapId) is not { } mesh)
        {
            return false;
        }

        if (mesh.IsTerrainTileLoaded(tileX, tileY))
        {
            return true;
        }

        string path = Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY));
        if (_prefetch.TryTake(mapId, tileX, tileY, out NavMeshTile? prefetched))
        {
            prefetchHit = true;
            if (mesh.AddTile(tileX, tileY, prefetched))
            {
                return true;
            }

            _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored", path, prefetched.X, prefetched.Y);
            return false;
        }

        if (ReadFile(path) is not { } bytes)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            NavMeshTile tile = NavMeshTile.ParseFile(bytes);
            if (mesh.AddTile(tileX, tileY, tile))
            {
                return true;
            }

            _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored", path, tile.X, tile.Y);
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError("MMaps: {Path} is not a usable navmesh tile ({Reason}); ignored", path, ex.Message);
        }

        return false;
    }

    /// <summary>Release a terrain tile's navmesh tile (vmangos <c>MMapManager::unloadMap</c>).</summary>
    public void UnloadTile(uint mapId, int tileX, int tileY)
    {
        if (_meshes.GetValueOrDefault(mapId) is { } mesh)
        {
            mesh.RemoveTile(tileX, tileY);
        }
    }

    void ICollisionTileLifecycle.OnTileLoaded(uint mapId, int tileX, int tileY)
    {
        _gridTiles.Add((mapId, tileX, tileY));
        RemoveOnDemand((mapId, tileX, tileY)); // the grid holds it now
        _async.Remove((mapId, tileX, tileY));
        if (GetNavMesh(mapId) is not { } mesh || mesh.IsTerrainTileLoaded(tileX, tileY))
        {
            LoadTile(mapId, tileX, tileY);
            return;
        }

        // The grid's tile is installed on the world thread: a prefetch hit only adds the parsed tile, a miss reads the file here.
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool installed = LoadTile(mapId, tileX, tileY, out bool prefetchHit);
        PathSearchCounters.RecordGridTileLoad(System.Diagnostics.Stopwatch.GetTimestamp() - started, mapId, tileX, tileY, prefetchHit, installed);
    }

    void ICollisionTileLifecycle.OnTileUnloaded(uint mapId, int tileX, int tileY)
    {
        _gridTiles.Remove((mapId, tileX, tileY));
        RemoveOnDemand((mapId, tileX, tileY));
        UnloadTile(mapId, tileX, tileY);
    }

    /// <summary>
    /// The most navigation tiles kept loaded per map for queries with <see cref="PathOptions.LoadTiles"/> beyond those the grids
    /// hold; the one used longest ago is released first. A tile is about a megabyte on disk.
    /// </summary>
    public const int MaxOnDemandTiles = 32;

    /// <summary>The most tiles one query may read (<see cref="PathOptions.LoadTiles"/>).</summary>
    public const int MaxTilesPerQuery = 12;

    private readonly HashSet<(uint Map, int X, int Y)> _gridTiles = [];
    private readonly Dictionary<(uint Map, int X, int Y), long> _onDemand = [];
    private readonly Dictionary<uint, int> _onDemandPerMap = [];
    private readonly HashSet<(uint Map, int X, int Y)> _noTile = [];
    private long _queryStamp;
    private int _queryLoads;

    /// <summary>Drop a tile from <see cref="_onDemand"/>, keeping the per-map count (<see cref="_onDemandPerMap"/>) in step.</summary>
    private void RemoveOnDemand((uint Map, int X, int Y) key)
    {
        if (!_onDemand.Remove(key))
        {
            return;
        }

        if (_onDemandPerMap.TryGetValue(key.Map, out int count) && count > 1)
        {
            _onDemandPerMap[key.Map] = count - 1;
        }
        else
        {
            _onDemandPerMap.Remove(key.Map);
        }
    }

    /// <summary>Record a tile as loaded for a query at <paramref name="stamp"/>, keeping the per-map count in step.</summary>
    private void SetOnDemand((uint Map, int X, int Y) key, long stamp)
    {
        if (_onDemand.TryAdd(key, stamp))
        {
            _onDemandPerMap[key.Map] = _onDemandPerMap.GetValueOrDefault(key.Map) + 1;
        }
        else
        {
            _onDemand[key] = stamp;
        }
    }

    /// <summary>Tiles loaded for queries and not held by a grid (tests and inspection).</summary>
    public int OnDemandTileCount => _onDemand.Count;

    /// <summary>Prefetched tiles the loader took instead of reading them (diagnostics and tests).</summary>
    internal int PrefetchHits => _prefetch.Hits;

    /// <summary>
    /// Read and parse a tile's <c>.mmtile</c> on the thread pool (<see cref="TilePrefetchCache{T}"/>) so <see cref="LoadTile"/> only
    /// adds it. Nothing happens for a map without a navmesh or a tile already loaded. World thread.
    /// </summary>
    public void Prefetch(uint mapId, int tileX, int tileY)
    {
        if (GetNavMesh(mapId) is not { } mesh || mesh.IsTerrainTileLoaded(tileX, tileY))
        {
            return;
        }

        string path = Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY));
        _prefetch.Request(mapId, tileX, tileY, () => File.Exists(path) ? NavMeshTile.ParseFile(File.ReadAllBytes(path)) : null);
    }

    private readonly Dictionary<(uint Map, NavTerrain Include, NavTerrain Exclude), Task<NavRegionGraph?>> _regionGraphs = [];
    private readonly object _regionGraphsLock = new();
    private readonly SemaphoreSlim _regionBuildSlots = new(MaxConcurrentRegionBuilds, MaxConcurrentRegionBuilds);

    /// <summary>
    /// The most region graph builds running at once (each reads every tile of a map: seconds and hundreds of megabytes for a continent).
    /// More requests wait for a slot without holding a thread.
    /// </summary>
    public const int MaxConcurrentRegionBuilds = 2;

    /// <summary>Builds one region graph; <see cref="NavRegionGraph.Build"/> unless a test substitutes it.</summary>
    internal Func<string, uint, NavTerrain, NavTerrain, NavRegionGraph> RegionGraphBuilder { get; set; }
        = (directory, mapId, include, exclude) => NavRegionGraph.Build(directory, mapId, include, exclude);

    /// <summary>
    /// The map's region graph for polygons with some flag of <paramref name="include"/> and none of <paramref name="exclude"/>
    /// (<see cref="NavRegionGraph"/>), or null: the map has no navmesh, the graph is still being built, or building it failed. The
    /// first call starts the build on the thread pool (it reads every tile of the map once: seconds for a continent) and every call
    /// answers at once; nothing is read on the calling thread. At most <see cref="MaxConcurrentRegionBuilds"/> builds run at once, and one
    /// map/filter is built once however often and from however many threads it is asked for. A failed build is remembered (null), not
    /// retried: the answer stays "no graph" until the process restarts. The request table is locked; the navmesh lookup itself is
    /// the world thread's (<see cref="GetNavMesh"/>).
    /// </summary>
    public NavRegionGraph? RegionGraph(uint mapId, NavTerrain include, NavTerrain exclude)
    {
        Task<NavRegionGraph?>? build = RegionGraphTask(mapId, include, exclude);
        return build is { IsCompletedSuccessfully: true } ? build.Result : null;
    }

    /// <summary>The build of <see cref="RegionGraph"/>, started if it was not (null: the map has no navmesh). Tests await it.</summary>
    public Task<NavRegionGraph?>? RegionGraphTask(uint mapId, NavTerrain include, NavTerrain exclude)
    {
        if (GetNavMesh(mapId) is null)
        {
            return null;
        }

        (uint, NavTerrain, NavTerrain) key = (mapId, include, exclude);
        lock (_regionGraphsLock)
        {
            if (_regionGraphs.TryGetValue(key, out Task<NavRegionGraph?>? existing))
            {
                return existing;
            }

            string directory = _directory;
            ILogger logger = _logger;
            SemaphoreSlim slots = _regionBuildSlots;
            Func<string, uint, NavTerrain, NavTerrain, NavRegionGraph> builder = RegionGraphBuilder;
            Task<NavRegionGraph?> build = Task.Run<NavRegionGraph?>(async () =>
            {
                await slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    NavRegionGraph graph = builder(directory, mapId, include, exclude);
                    ReportSkippedTiles(logger, mapId, graph);
                    logger.LogInformation("MMaps: map {MapId} region graph built from {Tiles} tiles in {Ms} ms: {Regions} regions, {Links} portal links",
                        mapId, graph.TileCount, watch.ElapsedMilliseconds, graph.RegionCount, graph.LinkCount);
                    return graph;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MMaps: map {MapId} region graph could not be built; long routes there plan without it", mapId);
                    return null;
                }
                finally
                {
                    slots.Release();
                }
            });
            _regionGraphs[key] = build;
            return build;
        }
    }

    /// <summary>Most skipped tiles written as their own line per graph; the rest are counted in a summary line (every one is recorded in the ledger).</summary>
    private const int MaxSkippedTileLines = 20;

    /// <summary>Log each tile the graph build left out as a Warning (file and tile coordinates) and count it in <see cref="FaultLedger.Shared"/>; once per graph, as a graph is built once per map and filter.</summary>
    internal static void ReportSkippedTiles(ILogger logger, uint mapId, NavRegionGraph graph)
    {
        int logged = 0;
        foreach (SkippedNavTile skipped in graph.SkippedTiles)
        {
            FaultLedger.Shared.Record("mmaps.regiongraph", skipped.Error);
            if (logged++ < MaxSkippedTileLines)
            {
                logger.LogWarning(skipped.Error, "MMaps: map {MapId} tile ({X}, {Y}) was skipped by the region graph build: {File} is unreadable or corrupt; routes across that tile plan without it",
                    mapId, skipped.TileX, skipped.TileY, skipped.File);
            }
        }

        if (logged > MaxSkippedTileLines)
        {
            logger.LogWarning("MMaps: map {MapId} region graph skipped {Count} unreadable tiles in all ({Shown} listed above)", mapId, logged, MaxSkippedTileLines);
        }
    }

    /// <summary>
    /// The region of <paramref name="graph"/> under world position <paramref name="at"/>: the polygon a path query would start or end on
    /// (the same search boxes and filter, <paramref name="options"/>), its tile read when <see cref="PathOptions.LoadTiles"/> allows. -1
    /// when no polygon is found there.
    /// </summary>
    public int RegionAt(NavRegionGraph graph, Vector3 at, PathOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsFinite(at) || GetNavMesh(graph.MapId) is not { } mesh)
        {
            return -1;
        }

        if (options.LoadTiles)
        {
            _queryStamp++;
            _queryLoads = 0;
            EnsureTile(graph.MapId, at, options.NonBlockingTiles, options.CancellationToken);
        }

        Vector3 recast = NavMeshFormat.ToRecast(at);
        return mesh.HaveTileAt(recast) && TryLocate(mesh, recast, options, out NavPolyRef poly, out _, out _)
            ? graph.RegionOf(poly.Tile.X, poly.Tile.Y, poly.Poly) : -1;
    }

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        options ??= PathOptions.Default;
        options.CancellationToken.ThrowIfCancellationRequested();
        if (!IsFinite(start) || !IsFinite(end))
        {
            return PathResult.None(start);
        }

        if (GetNavMesh(mapId) is not { } mesh)
        {
            return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
        }

        if (!options.LoadTiles)
        {
            return FindPath(mesh, mapId, start, end, options);
        }

        bool nonBlocking = options.NonBlockingTiles;
        long pendingBefore = PathSearchCounters.TilesPending;
        _queryStamp++;
        _queryLoads = 0;
        EnsureTile(mapId, start, nonBlocking, options.CancellationToken);
        EnsureTile(mapId, end, nonBlocking, options.CancellationToken);
        if (nonBlocking && PathSearchCounters.TilesPending != pendingBefore)
        {
            // An endpoint's tile is still being read: the search cannot even locate it (an endpoint on an unloaded tile would answer a
            // straight line, which is not what this query will answer once the tile is in).
            return TilesPendingResult(start);
        }

        mesh.TileLoader = (x, y) =>
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            Vector3 center = NavMeshFormat.ToWorld(mesh.TileCenter(x, y));
            return EnsureTile(mapId, center, nonBlocking, options.CancellationToken) ? mesh.GetTile(x, y) : null;
        };
        try
        {
            PathResult result = FindPath(mesh, mapId, start, end, options);
            // A tile the search wanted was not in yet. A whole path found without it stands (the tile could only have offered another
            // way); any other answer (a partial path, no path, a shortcut) might be only for want of that tile.
            return nonBlocking && PathSearchCounters.TilesPending != pendingBefore
                && (result.Type & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath)) != 0
                ? TilesPendingResult(start) : result;
        }
        finally
        {
            mesh.TileLoader = null;
        }
    }

    private static PathResult TilesPendingResult(Vector3 start) => new(PathType.TilesPending, [start]);

    /// <summary>
    /// Make sure the navigation tile under world position <paramref name="at"/> is loaded for this query (<see cref="PathOptions.LoadTiles"/>),
    /// within <see cref="MaxTilesPerQuery"/> and <see cref="MaxOnDemandTiles"/>. False when it is not (no such tile, or over a bound).
    /// With <paramref name="nonBlocking"/> (<see cref="PathOptions.NonBlockingTiles"/>) the file is never read here: the tile is read on
    /// the pool (<see cref="PollTileRead"/>), a tile whose read has not finished counts as pending
    /// (<see cref="PathSearchCounters.TilesPending"/>) and is false, and one whose read has finished is installed.
    /// </summary>
    private bool EnsureTile(uint mapId, Vector3 at, bool nonBlocking, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Terrain.TerrainTile.TileOf(at.X, at.Y) is not { } terrain || GetNavMesh(mapId) is not { } mesh)
        {
            return false;
        }

        (uint, int, int) key = (mapId, terrain.X, terrain.Y);
        if (mesh.IsTerrainTileLoaded(terrain.X, terrain.Y))
        {
            if (_onDemand.ContainsKey(key))
            {
                _onDemand[key] = _queryStamp; // existing key: the per-map count is unchanged
            }

            return true;
        }

        if (_noTile.Contains(key) || _queryLoads >= MaxTilesPerQuery)
        {
            return false;
        }

        NavMeshTile? readTile = null;
        if (nonBlocking)
        {
            int startedBefore = TileReadsStarted;
            switch (PollTileRead(mapId, terrain.X, terrain.Y, out readTile))
            {
                case TileReadState.Pending:
                    PathSearchCounters.RecordTilePending();
                    // A read started here is work this query asked for, like a tile loaded: it counts against MaxTilesPerQuery.
                    if (TileReadsStarted != startedBefore)
                    {
                        _queryLoads++;
                    }

                    return false;
                case TileReadState.Unavailable:
                    if (_noTile.Count < 16_384)
                    {
                        _noTile.Add(key);
                    }

                    return false;
            }
        }

        if (_onDemandPerMap.GetValueOrDefault(mapId) >= MaxOnDemandTiles)
        {
            // Release the tile used longest ago, never one this query reached.
            (uint Map, int X, int Y) oldest = default;
            long oldestStamp = long.MaxValue;
            foreach (((uint Map, int X, int Y) tile, long stamp) in _onDemand)
            {
                if (tile.Map == mapId && stamp < _queryStamp && stamp < oldestStamp)
                {
                    oldest = tile;
                    oldestStamp = stamp;
                }
            }

            if (oldestStamp == long.MaxValue)
            {
                return false;
            }

            RemoveOnDemand(oldest);
            UnloadTile(oldest.Map, oldest.X, oldest.Y);
        }

        _queryLoads++;
        long loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        bool prefetchHit = readTile is not null;
        bool loaded = readTile is not null
            ? InstallReadTile(mapId, terrain.X, terrain.Y, readTile)
            : LoadTile(mapId, terrain.X, terrain.Y, cancellationToken, out prefetchHit);
        cancellationToken.ThrowIfCancellationRequested();
        PathSearchCounters.RecordTileLoad(System.Diagnostics.Stopwatch.GetTimestamp() - loadStarted,
            mapId, terrain.X, terrain.Y, prefetchHit, loaded);
        if (!loaded)
        {
            if (_noTile.Count < 16_384)
            {
                _noTile.Add(key);
            }

            return false;
        }

        SetOnDemand(key, _queryStamp);
        return true;
    }

    /// <summary>What <see cref="PollTileRead"/> found.</summary>
    private enum TileReadState
    {
        /// <summary>The tile was read; it is returned.</summary>
        Ready,

        /// <summary>A read of it is under way (or was just requested, or waits for a free read slot).</summary>
        Pending,

        /// <summary>It does not exist or could not be read (reported once); the tile is treated as absent.</summary>
        Unavailable,
    }

    /// <summary>One background tile read: the parsed tile, null when the file does not exist, or why it failed.</summary>
    private readonly record struct TileRead(NavMeshTile? Tile, Exception? Error);

    /// <summary>
    /// The most tile reads in flight or finished and waiting at once (<see cref="_async"/>). A request over it is pending without starting a
    /// read; finished reads nobody collected are dropped oldest first to make room.
    /// </summary>
    public const int MaxPendingTileReads = 16;

    /// <summary>The default for <see cref="MaxConcurrentTileReads"/> (<see cref="CollisionOptions.NavTileReadConcurrency"/>).</summary>
    public const int DefaultConcurrentTileReads = 2;

    /// <summary>The most tile files read at the same time; further reads wait for a slot without holding a thread.</summary>
    public int MaxConcurrentTileReads { get; }

    /// <summary>Held reads by tile: the read, and the sequence number of the last time a query asked for it (least recently asked is dropped first).</summary>
    private readonly Dictionary<(uint Map, int X, int Y), (Task<TileRead> Read, long Sequence)> _async = [];
    private readonly SemaphoreSlim _tileReadSlots;
    private long _asyncSequence;

    /// <summary>Background tile reads started (diagnostics and tests).</summary>
    internal int TileReadsStarted { get; private set; }

    /// <summary>Tile reads that can still start before the concurrency bound is reached (<see cref="MaxConcurrentTileReads"/>; tests).</summary>
    internal int FreeTileReadSlots => _tileReadSlots.CurrentCount;

    /// <summary>The background read of a tile, or null when none is held (tests await it).</summary>
    internal Task? PendingTileRead(uint mapId, int tileX, int tileY)
        => _async.TryGetValue((mapId, tileX, tileY), out (Task<TileRead> Read, long Sequence) entry) ? entry.Read : null;

    /// <summary>
    /// Where the background read of a terrain tile stands, starting it when none exists (World thread; the only writer of
    /// <see cref="_async"/>). A tile the grid prefetch (<see cref="Prefetch"/>) is already reading is waited for, not read twice. The read
    /// itself runs on the pool and touches nothing but the file and its own result; the tile is added to the navmesh, which the world
    /// thread owns, by <see cref="InstallReadTile"/> on a later query. A failed read is logged once (the caller then remembers the
    /// tile as missing, so nothing asks again).
    /// </summary>
    private TileReadState PollTileRead(uint mapId, int tileX, int tileY, out NavMeshTile? tile)
    {
        tile = null;
        (uint, int, int) key = (mapId, tileX, tileY);
        if (_async.TryGetValue(key, out (Task<TileRead> Read, long Sequence) entry))
        {
            _async[key] = (entry.Read, ++_asyncSequence); // asked for now: the last to be dropped when the table is full
            if (!entry.Read.IsCompleted)
            {
                return TileReadState.Pending;
            }

            TileRead done = entry.Read.IsCompletedSuccessfully ? entry.Read.Result : new TileRead(null, entry.Read.Exception);
            if (done.Tile is { } ready)
            {
                tile = ready;
                return TileReadState.Ready; // stays held until installed (InstallReadTile), in case the install must wait for a slot
            }

            _async.Remove(key);
            if (done.Error is { } error)
            {
                string path = Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY));
                FaultLedger.Shared.Record("navtile-read", error);
                _logger.LogError(error, "MMaps: {Path} could not be read or is not a usable navmesh tile; ignored", path);
            }

            return TileReadState.Unavailable;
        }

        if (_prefetch.IsRunning(mapId, tileX, tileY))
        {
            return TileReadState.Pending;
        }

        // The held reads are bounded whatever their source (a prefetched tile is held until installed, as a read is): with the table
        // full and no finished read to drop, nothing starts and nothing is taken from the prefetch cache; the answer is pending.
        if (_async.Count >= MaxPendingTileReads && !DropLeastRecentFinishedRead())
        {
            return TileReadState.Pending;
        }

        if (_prefetch.TryTake(mapId, tileX, tileY, out NavMeshTile prefetched))
        {
            _async[key] = (Task.FromResult(new TileRead(prefetched, null)), ++_asyncSequence);
            tile = prefetched;
            return TileReadState.Ready;
        }

        string file = Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY));
        _async[key] = (StartTileRead(file), ++_asyncSequence);
        TileReadsStarted++;
        return TileReadState.Pending;
    }

    /// <summary>Drop the finished read that was asked for longest ago (one a query just asked for is the last): true when one was dropped.</summary>
    private bool DropLeastRecentFinishedRead()
    {
        (uint, int, int)? oldest = null;
        long oldestSequence = long.MaxValue;
        foreach (((uint, int, int) key, (Task<TileRead> read, long sequence)) in _async)
        {
            if (read.IsCompleted && sequence < oldestSequence)
            {
                oldest = key;
                oldestSequence = sequence;
            }
        }

        return oldest is { } drop && _async.Remove(drop);
    }

    /// <summary>Read and parse one tile file on the pool, at most <see cref="MaxConcurrentTileReads"/> at a time. Never faults: a failure is the result.</summary>
    private Task<TileRead> StartTileRead(string path)
    {
        Func<string, byte[]?> reader = FileReader;
        SemaphoreSlim slots = _tileReadSlots;
        return Task.Run(async () =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[]? bytes = reader(path);
                return new TileRead(bytes is null ? null : NavMeshTile.ParseFile(bytes), null);
            }
            catch (Exception ex)
            {
                return new TileRead(null, ex);
            }
            finally
            {
                slots.Release();
            }
        });
    }

    /// <summary>Add a tile read in the background to the navmesh (world thread), dropping its held read. False when its Detour slot is taken.</summary>
    private bool InstallReadTile(uint mapId, int tileX, int tileY, NavMeshTile tile)
    {
        _async.Remove((mapId, tileX, tileY));
        if (GetNavMesh(mapId) is { } mesh && mesh.AddTile(tileX, tileY, tile))
        {
            return true;
        }

        _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored",
            Path.Combine(_directory, NavMeshFormat.TileFileName(mapId, tileX, tileY)), tile.X, tile.Y);
        return false;
    }

    private PathResult FindPath(NavMesh mesh, uint mapId, Vector3 start, Vector3 end, PathOptions options)
    {
        Vector3 startRc = NavMeshFormat.ToRecast(start);
        Vector3 endRc = NavMeshFormat.ToRecast(end);
        if (!mesh.HaveTileAt(startRc) || !mesh.HaveTileAt(endRc))
        {
            // vmangos PathFinder.cpp:99-105: an endpoint on an unloaded .mmtile is not an error, the
            // mover simply goes straight (BuildShortcut, NORMAL | NOT_USING_PATH).
            return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
        }

        // vmangos PathFinder.cpp:172-186: a flier goes straight through the air unless a collision
        // model is in the way; then it follows the mesh and the destination is forced afterwards.
        bool flier = options.Mover is { CanFly: true };
        bool forceDestination = false;
        if (flier)
        {
            if (LineOfSight.IsInLineOfSight(mapId, start, end, ignoreM2: false))
            {
                return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath | PathType.FlyPath);
            }

            forceDestination = true;
        }

        if (!TryLocate(mesh, startRc, options, out NavPolyRef startPoly, out Vector3 startOnMesh, out _)
            || !TryLocate(mesh, endRc, options, out NavPolyRef endPoly, out Vector3 endOnMesh, out bool endFar))
        {
            // PathFinder.cpp:190-198: a hole in the mesh is a flying shortcut for a flier, NOPATH otherwise.
            return PathResult.StraightLine(start, end, flier ? PathType.Normal | PathType.NotUsingPath | PathType.FlyPath : PathType.NoPath);
        }

        (List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor, bool complete) =
            NavMeshQuery.FindCorridor(mesh, startPoly, startOnMesh, endPoly, endOnMesh, options);
        if (!complete && !options.AllowPartial)
        {
            return PathResult.StraightLine(start, end, PathType.NoPath);
        }

        Vector3 goal = complete ? endOnMesh : corridor[^1].Poly.Tile.ClosestPointOnPoly(corridor[^1].Poly.Poly, endRc);
        List<Vector3> corners = NavMeshQuery.StringPull(startOnMesh, [.. corridor.Skip(1).Select(c => (c.Left, c.Right))], goal);

        var points = new List<Vector3>(corners.Count) { start };
        for (int i = 1; i < corners.Count; i++)
        {
            points.Add(NavMeshFormat.ToWorld(corners[i]));
        }

        if (points.Count == 1)
        {
            points.Add(NavMeshFormat.ToWorld(goal));
        }

        PathType type = complete && !endFar ? PathType.Normal : PathType.Incomplete;
        if (forceDestination && ((type & PathType.Normal) == 0 || !InRange(end, points[^1], 1f, 1f)))
        {
            // PathFinder.cpp:451-472: keep a partial subpath only when it already covers 70% of the way.
            if (Vector3.DistanceSquared(points[^1], end) < 0.3f * Vector3.DistanceSquared(start, end))
            {
                points[^1] = end;
            }
            else
            {
                points.Clear();
                points.Add(start);
                points.Add(end);
            }

            type |= PathType.DestForced | PathType.FlyPath;
        }

        int max = Math.Max(2, options.MaxPoints);
        if (points.Count > max)
        {
            points.RemoveRange(max, points.Count - max);
            type |= PathType.Short;
        }

        return new PathResult(type, points);
    }

    // vmangos inRange(p1, p2, r, h): horizontal distance within r and vertical within h.
    private static bool InRange(Vector3 a, Vector3 b, float r, float h)
        => (((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y))) <= r * r && MathF.Abs(a.Z - b.Z) <= h;

    private static bool TryLocate(NavMesh mesh, Vector3 position, PathOptions options, out NavPolyRef poly, out Vector3 onMesh, out bool far)
    {
        far = false;
        if (mesh.TryFindNearestPoly(position, NearExtents, options, out poly, out onMesh))
        {
            far = NavMeshTile.DistanceXzSquared(onMesh, position) > 0.01f || MathF.Abs(onMesh.Y - position.Y) > NearExtents.Y;
            return true;
        }

        far = true;
        return mesh.TryFindNearestPoly(position, FarExtents, options, out poly, out onMesh);
    }

    /// <summary>Reads a navmesh file's bytes (null when it does not exist); the disk unless a test substitutes it. Runs on the world thread or, for background tile reads, on the pool.</summary>
    internal Func<string, byte[]?> FileReader { get; set; } = static path => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private byte[]? ReadFile(string path)
    {
        try
        {
            return FileReader(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "MMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
