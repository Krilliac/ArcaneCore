using System.Numerics;
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

    public NavMeshPathfinder(string directory, ILogger? logger = null)
        : this(directory, logger, Guid.NewGuid())
    {
    }

    private NavMeshPathfinder(string directory, ILogger? logger, Guid identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _logger = logger ?? NullLogger.Instance;
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
        LoadTile(mapId, tileX, tileY);
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
            EnsureTile(graph.MapId, at);
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

        _queryStamp++;
        _queryLoads = 0;
        EnsureTile(mapId, start, options.CancellationToken);
        EnsureTile(mapId, end, options.CancellationToken);
        mesh.TileLoader = (x, y) =>
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            Vector3 center = NavMeshFormat.ToWorld(mesh.TileCenter(x, y));
            return EnsureTile(mapId, center, options.CancellationToken) ? mesh.GetTile(x, y) : null;
        };
        try
        {
            return FindPath(mesh, mapId, start, end, options);
        }
        finally
        {
            mesh.TileLoader = null;
        }
    }

    /// <summary>
    /// Make sure the navigation tile under world position <paramref name="at"/> is loaded for this query (<see cref="PathOptions.LoadTiles"/>),
    /// within <see cref="MaxTilesPerQuery"/> and <see cref="MaxOnDemandTiles"/>. False when it is not (no such tile, or over a bound).
    /// </summary>
    private bool EnsureTile(uint mapId, Vector3 at, CancellationToken cancellationToken = default)
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
        bool loaded = LoadTile(mapId, terrain.X, terrain.Y, cancellationToken, out bool prefetchHit);
        cancellationToken.ThrowIfCancellationRequested();
        PathSearchCounters.RecordTileLoad(System.Diagnostics.Stopwatch.GetTimestamp() - loadStarted,
            mapId, terrain.X, terrain.Y, prefetchHit);
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

    private byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "MMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
