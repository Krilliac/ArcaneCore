using System.Collections.Concurrent;
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
/// projected and the path is <see cref="PathType.Incomplete"/>. With <see cref="PathOptions.EndTolerance"/> both endpoints follow vmangos
/// instead: each is looked for as <c>FindWalkPoly</c> does, and an endpoint off the mesh makes the path incomplete (a flier's the
/// straight line) only when it is farther than the tolerance from its polygon. Corrupt files are logged and read as missing; nothing here
/// throws into the world thread.
/// </para>
/// <para>Thread affinity: world thread, except <see cref="FindPathShared"/> (any thread, under the shared read lock).</para>
/// </summary>
public sealed partial class NavMeshPathfinder : IMapAwarePathfinder, IMeshPointQuery, IMeshFloorQuery, ICollisionTileLifecycle, ICollisionTilePrefetch, ISharedPathSearch, ISharedTiledPathSearch,
    ISharedMeshPointQuery, ICollisionTileDemand
{
    /// <summary>
    /// First search box half-extents (Recast x, y, z) for the nearest polygon of an endpoint of a query without an end tolerance: the
    /// first box of MaNGOS Zero's <c>getPolyByLocation</c> (mangos-unified expansions/zero/MotionGenerators/PathFinder.cpp:213) and of
    /// the TrinityCore line's (AzerothCore PathGenerator.cpp:234). vmangos looks 5 around and 10 up and down instead (<c>FindWalkPoly</c>,
    /// PathFinder.cpp:128-142, PathFinder.h:89), which is the search of a query with <see cref="PathOptions.EndTolerance"/>.
    /// </summary>
    public static readonly Vector3 NearExtents = new(3, 5, 3);

    /// <summary>
    /// Second, taller box for points high above or below the mesh (flagged as off the mesh): MaNGOS Zero's <c>getPolyByLocation</c> retry
    /// with <c>extents[1] = 200</c> (mangos-unified expansions/zero/MotionGenerators/PathFinder.cpp:223; AzerothCore retries 50 tall,
    /// PathGenerator.cpp:245). vmangos (<c>FindWalkPoly</c>) and cmangos-classic (<c>NearPolySearchBound</c>, <c>FarPolySearchBound</c>,
    /// MotionGenerators/PathFinder.h:58-59) have no such column.
    /// </summary>
    public static readonly Vector3 FarExtents = new(3, 200, 3);

    /// <summary>
    /// cmangos-classic <c>FarPolySearchBound</c> {10, 10, 10} (MotionGenerators/PathFinder.h:59): the box an endpoint is looked for in when
    /// <see cref="PathOptions.WideEndSearch"/> is set and neither column above holds a usable polygon (flagged as off the mesh).
    /// </summary>
    public static readonly Vector3 WideExtents = new(10, 10, 10);

    private readonly string _directory;
    private readonly ILogger _logger;
    public NavMeshDatasetDescriptor Dataset { get; }
    private readonly Dictionary<uint, NavMesh?> _meshes = [];
    private readonly TilePrefetchCache<NavMeshTile> _prefetch = new();

    /// <summary>
    /// Held for reading by every <see cref="FindPathShared"/> search (path-job workers) and for writing by every change of the loaded
    /// tiles or the mesh table (<see cref="EnterMeshWrite"/>). The owner thread's own queries take nothing: it is the only writer, and the
    /// lazily built per-tile caches a search fills are safe for concurrent readers (<see cref="NavMeshTile.GetCrossPortals"/>).
    /// </summary>
    private readonly ReaderWriterLockSlim _sharedLock = new(LockRecursionPolicy.SupportsRecursion);

    /// <summary>Cancelled when the owner needs the write lock while shared searches hold it for reading, so they stop at their next check.</summary>
    private CancellationTokenSource _sharedCancel = new();

    /// <summary>
    /// The caller's options with the current shared cancellation token, per options instance (creature options are a few static
    /// instances): one copy per generation instead of one per search (10 MB per 30 s on live). Swapped with <see cref="_sharedCancel"/>.
    /// </summary>
    private sealed class SharedOptions(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;

        public ConcurrentDictionary<PathOptions, PathOptions> ByOptions { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private SharedOptions? _sharedOptions;

    private long _sharedSearchesCanceled;

    /// <summary>Shared searches stopped because the owner changed the loaded tiles under them (diagnostics, tests).</summary>
    public long SharedSearchesCanceled => Interlocked.Read(ref _sharedSearchesCanceled);

    /// <summary>
    /// Take the write lock for a change of the loaded tiles or the mesh table. Shared searches in progress are asked to stop first, so the
    /// owner waits for one cancellation check, not for whole searches.
    /// </summary>
    private void EnterMeshWrite()
    {
        if (!_sharedLock.IsWriteLockHeld && _sharedLock.CurrentReadCount > 0)
        {
            CancellationTokenSource previous = Interlocked.Exchange(ref _sharedCancel, new CancellationTokenSource());
            previous.Cancel(); // not disposed: a worker may still read its token
            Interlocked.Increment(ref _sharedSearchesCanceled);
        }

        _sharedLock.EnterWriteLock();
    }

    private void ExitMeshWrite() => _sharedLock.ExitWriteLock();

    private bool AddTileLocked(NavMesh mesh, int tileX, int tileY, NavMeshTile tile)
    {
        EnterMeshWrite();
        try
        {
            return mesh.AddTile(tileX, tileY, tile);
        }
        finally
        {
            ExitMeshWrite();
        }
    }

    /// <inheritdoc />
    public bool CanSearchShared(uint mapId) => GetNavMesh(mapId) is not null;

    /// <inheritdoc />
    public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
        => FindPathSharedCore(mapId, start, end, options);

    /// <inheritdoc />
    public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options, List<(int X, int Y)> missedTiles)
    {
        ArgumentNullException.ThrowIfNull(missedTiles);
        List<(int X, int Y)>? outer = NavMesh.MissedTileSink;
        NavMesh.MissedTileSink = missedTiles;
        try
        {
            return FindPathSharedCore(mapId, start, end, options);
        }
        finally
        {
            NavMesh.MissedTileSink = outer;
        }
    }

    /// <inheritdoc />
    public bool PrepareShared(uint mapId, Vector3 start, Vector3 end)
    {
        if (!IsFinite(start) || !IsFinite(end) || GetNavMesh(mapId) is null)
        {
            return true;
        }

        _queryStamp++;
        _queryLoads = 0;
        _pendingThisQuery.Clear();
        _trackPendingThisQuery = true;
        try
        {
            EnsureTile(mapId, start, nonBlocking: true);
            EnsureTile(mapId, end, nonBlocking: true);
            return !QueryTileStillOpen();
        }
        finally
        {
            _trackPendingThisQuery = false;
        }
    }

    /// <inheritdoc />
    public SharedMissVerdict ResolveSharedMisses(uint mapId, IReadOnlyList<(int X, int Y)> missedTiles)
    {
        ArgumentNullException.ThrowIfNull(missedTiles);
        if (missedTiles.Count == 0 || GetNavMesh(mapId) is not { } mesh)
        {
            return SharedMissVerdict.None;
        }

        _queryStamp++;
        _queryLoads = 0;
        _pendingThisQuery.Clear();
        _trackPendingThisQuery = true;
        bool changed = false;
        try
        {
            int asked = 0;
            foreach ((int x, int y) in missedTiles)
            {
                if (asked >= MaxTilesPerQuery)
                {
                    break;
                }

                asked++;
                if (mesh.GetTile(x, y) is not null)
                {
                    changed = true;
                    continue;
                }

                Vector3 center = NavMeshFormat.ToWorld(mesh.TileCenter(x, y));
                if (Terrain.TerrainTile.TileOf(center.X, center.Y) is { } terrain && _noTile.Contains((mapId, terrain.X, terrain.Y)))
                {
                    continue;
                }

                if (EnsureTile(mapId, center, nonBlocking: true) && mesh.GetTile(x, y) is not null)
                {
                    changed = true;
                }
            }

            return changed ? SharedMissVerdict.Changed : QueryTileStillOpen() ? SharedMissVerdict.Pending : SharedMissVerdict.None;
        }
        finally
        {
            _trackPendingThisQuery = false;
        }
    }

    private PathResult FindPathSharedCore(uint mapId, Vector3 start, Vector3 end, PathOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LoadTiles || options.NonBlockingTiles)
        {
            throw new ArgumentException("a shared search reads no tiles", nameof(options));
        }

        if (options.Mover is { CanFly: true })
        {
            throw new ArgumentException("a flier's search consults the collision models, which are the owner's", nameof(options));
        }

        if (!IsFinite(start) || !IsFinite(end))
        {
            return PathResult.None(start);
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _sharedLock.EnterReadLock();
        try
        {
            if (!_meshes.TryGetValue(mapId, out NavMesh? mesh) || mesh is null)
            {
                return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
            }

            CancellationToken cancel = Volatile.Read(ref _sharedCancel).Token;
            PathOptions shared = options;
            if (!options.CancellationToken.CanBeCanceled)
            {
                SharedOptions? generation = Volatile.Read(ref _sharedOptions);
                if (generation is null || generation.Token != cancel)
                {
                    generation = new SharedOptions(cancel);
                    Volatile.Write(ref _sharedOptions, generation);
                }

                shared = generation.ByOptions.Count < 256
                    ? generation.ByOptions.GetOrAdd(options, static (o, token) => o with { CancellationToken = token }, cancel)
                    : options with { CancellationToken = cancel };
            }

            cancel.ThrowIfCancellationRequested();
            PathResult result = FindPath(mesh, mapId, start, end, shared);
            QueryLatency.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
            return result;
        }
        finally
        {
            _sharedLock.ExitReadLock();
        }
    }

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
                FaultLedger.Shared.Record("collision.mmap-params", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId}", ex.Message);
                _logger.LogError(ex, "MMaps: {Path} is not usable navmesh parameters ({Reason}); map {MapId} paths are straight lines", path, ex.Message, mapId);
            }
        }

        EnterMeshWrite();
        try
        {
            _meshes[mapId] = mesh;
        }
        finally
        {
            ExitMeshWrite();
        }

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
            if (AddTileLocked(mesh, tileX, tileY, prefetched))
            {
                return true;
            }

            _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored", path, prefetched.X, prefetched.Y);
            return false;
        }

        try
        {
            if (!TryReadTileFile(path, FileReader, out NavMeshTile? tile))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (AddTileLocked(mesh, tileX, tileY, tile))
            {
                return true;
            }

            _logger.LogError("MMaps: {Path} claims Detour tile ({X}, {Y}), which is already loaded; ignored", path, tile.X, tile.Y);
        }
        catch (InvalidDataException ex)
        {
            FaultLedger.Shared.Record("collision.mmap-tile", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
            _logger.LogError(ex, "MMaps: {Path} is not a usable navmesh tile ({Reason}); ignored", path, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FaultLedger.Shared.Record("collision.mmap-tile", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
            _logger.LogError(ex, "MMaps: could not read {Path}", path);
        }

        return false;
    }

    /// <summary>
    /// Read and parse one <c>.mmtile</c>; false when the file does not exist. Without a test <paramref name="reader"/> the file is read into a
    /// pooled buffer (<see cref="PooledFileReader"/>) that is returned once the parse has copied what it keeps: a tile file is 0.1-2 MB, so a
    /// fresh array per read went to the large object heap, and the bots' tile reads (prefetch, background and world-thread loads) were the
    /// World's main LOH churn (2.3 MB/s live, 2026-10-17; issues #744/#755). Read and parse errors propagate to the caller.
    /// </summary>
    private static bool TryReadTileFile(string path, Func<string, byte[]?>? reader, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NavMeshTile? tile)
    {
        if (reader is not null)
        {
            byte[]? bytes = reader(path);
            tile = bytes is null ? null : NavMeshTile.ParseFile(bytes);
            return tile is not null;
        }

        return PooledFileReader.TryParse(path, static data => NavMeshTile.ParseFile(data), out tile);
    }

    /// <summary>Release a terrain tile's navmesh tile (vmangos <c>MMapManager::unloadMap</c>).</summary>
    public void UnloadTile(uint mapId, int tileX, int tileY)
    {
        if (_meshes.GetValueOrDefault(mapId) is { } mesh)
        {
            EnterMeshWrite();
            try
            {
                mesh.RemoveTile(tileX, tileY);
            }
            finally
            {
                ExitMeshWrite();
            }
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
    /// <see cref="ICollisionTileDemand.LoadTileOnDemand"/>: the navigation tile under (<paramref name="x"/>, <paramref name="y"/>), loaded
    /// as one query with <see cref="PathOptions.LoadTiles"/> loads it (read on the calling thread when it is not resident) and held among
    /// the <see cref="MaxOnDemandTiles"/> of its map. False when there is no usable tile there or no mesh for the map.
    /// </summary>
    public bool LoadTileOnDemand(uint mapId, float x, float y)
    {
        _queryStamp++;
        _queryLoads = 0;
        return EnsureTile(mapId, new Vector3(x, y, 0f), nonBlocking: false);
    }

    /// <summary>
    /// The most navigation tiles kept loaded per map for queries with <see cref="PathOptions.LoadTiles"/> beyond those the grids
    /// hold; the one used longest ago is released first. A tile is about a megabyte on disk. 56 rather than 32: with hundreds of
    /// bots pathing across a continent 32 thrashed (live: ~30 tile re-parses a minute, each a few large-object-heap arrays, which
    /// fragmented the LOH; LS-001). It stays under 64 so a single row of a map's tile grid can still exceed it in tests.
    /// </summary>
    public const int MaxOnDemandTiles = 56;

    /// <summary>The most tiles one query may read (<see cref="PathOptions.LoadTiles"/>).</summary>
    public const int MaxTilesPerQuery = 12;

    private readonly HashSet<(uint Map, int X, int Y)> _gridTiles = [];
    private readonly Dictionary<(uint Map, int X, int Y), long> _onDemand = [];
    private readonly Dictionary<uint, int> _onDemandPerMap = [];
    private readonly HashSet<(uint Map, int X, int Y)> _noTile = [];
    private readonly List<(uint Map, int X, int Y)> _pendingThisQuery = [];
    private long _queryStamp;
    private int _queryLoads;
    private bool _trackPendingThisQuery;

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
        ILogger logger = _logger;
        _prefetch.Request(mapId, tileX, tileY, () =>
        {
            try
            {
                // Always the disk, as before the pooled read: the FileReader test seam covers the load paths, not the prefetch.
                return TryReadTileFile(path, reader: null, out NavMeshTile? tile) ? tile : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("collision.mmap-prefetch", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId} tile ({tileX}, {tileY})", ex.Message);
                logger.LogFault("mmaps.prefetch", ex, "MMaps: prefetch of {Path} for map {MapId} tile ({TileX}, {TileY}) failed", path, mapId, tileX, tileY);
                return null;
            }
        });
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
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("collision.mmap-regiongraph", ex);
                    AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {mapId}", ex.Message);
                    logger.LogFault("mmaps.regiongraph", ex, "MMaps: map {MapId} region graph could not be built; long routes there plan without it", mapId);
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
        => RegionAt(graph, at, options, out _);

    /// <summary>
    /// <see cref="RegionAt(NavRegionGraph, Vector3, PathOptions)"/> that also says whether the located point is <paramref name="far"/> from <paramref name="at"/>
    /// (a vertical miss: it lies more than <c>NearExtents.Y</c> (5) above or below <paramref name="at"/>, whether the far box snapped to the floor
    /// under or over it or a near-box hit on a slope was that far off). With <c>EndTolerance</c> above 0 the single {5, 10, 5} box is taken and
    /// <paramref name="far"/> is never set. A point merely outside a polygon's footprint but within the near box is not far, whatever its
    /// horizontal distance. A far point is the floor beside a point in the air or on excluded ground, not the place itself. Meaningful only when the result is not -1.
    /// </summary>
    public int RegionAt(NavRegionGraph graph, Vector3 at, PathOptions options, out bool far)
    {
        far = false;
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        if (!Invariant.Check(IsFinite(at), $"NavMeshPathfinder.RegionAt called with non-finite coordinates {at} on map {graph.MapId}")
            || GetNavMesh(graph.MapId) is not { } mesh)
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
        if (!mesh.HaveTileAt(recast) || !TryLocate(mesh, recast, options, options.EndTolerance, out NavPolyRef poly, out Vector3 onMesh, out _))
        {
            return -1;
        }

        // Not TryLocate's own flag, which also counts a near-box hit past a tenth of a yard horizontally (a spawn beside an eroded edge).
        far = options.EndTolerance <= 0 && MathF.Abs(onMesh.Y - recast.Y) > NearExtents.Y;
        return graph.RegionOf(poly.Tile.X, poly.Tile.Y, poly.Poly);
    }

    /// <summary>The histogram every completed <see cref="FindPath"/> adds to (the shared <c>nav.path_query</c> one unless a test supplies its own).</summary>
    internal ArcaneCore.Kernel.Diagnostics.LatencyHistogram QueryLatency { get; init; } = ArcaneCore.Kernel.Diagnostics.LatencyRegistry.Shared.Get(ArcaneCore.Kernel.Diagnostics.LatencyNames.PathQuery);

    /// <summary>
    /// The path from <paramref name="start"/> to <paramref name="end"/>. Every completed query (not one that throws, e.g. on cancellation) adds its wall time, tile reads included, to the
    /// <c>nav.path_query</c> latency histogram (one clock read and a dozen atomic operations).
    /// </summary>
    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        PathResult result = FindPathUntimed(mapId, start, end, options);
        QueryLatency.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
        return result;
    }

    private PathResult FindPathUntimed(uint mapId, Vector3 start, Vector3 end, PathOptions? options)
    {
        options ??= PathOptions.Default;
        options.CancellationToken.ThrowIfCancellationRequested();
        if (!Invariant.Check(IsFinite(start) && IsFinite(end),
                $"NavMeshPathfinder.FindPath called with non-finite coordinates start={start}, end={end} on map {mapId}"))
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
        _queryStamp++;
        _queryLoads = 0;
        // Tiles this query found unread. A read that finishes as "no such file" is recorded in _noTile and is no longer open;
        // only a tile that is still open keeps the answer pending. Reporting pending after that read has left the table makes
        // the caller await PendingTileRead and find null.
        _pendingThisQuery.Clear();
        _trackPendingThisQuery = nonBlocking;
        try
        {
            EnsureTile(mapId, start, nonBlocking, options.CancellationToken);
            EnsureTile(mapId, end, nonBlocking, options.CancellationToken);
            if (nonBlocking && QueryTileStillOpen())
            {
                // An endpoint's tile is still being read: the search cannot even locate it (an endpoint on an unloaded tile would
                // answer a straight line, which is not what this query will answer once the tile is in).
                return TilesPendingResult(start);
            }

            mesh.TileLoader = (x, y) =>
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                Vector3 center = NavMeshFormat.ToWorld(mesh.TileCenter(x, y));
                return EnsureTile(mapId, center, nonBlocking, options.CancellationToken) ? mesh.GetTile(x, y) : null;
            };
            PathResult result = FindPath(mesh, mapId, start, end, options);
            // A tile the search wanted was not in yet. A whole path found without it stands (the tile could only have offered another
            // way); any other answer (a partial path, no path, a shortcut) might be only for want of that tile.
            return nonBlocking && QueryTileStillOpen()
                && (result.Type & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath)) != 0
                ? TilesPendingResult(start) : result;
        }
        finally
        {
            _trackPendingThisQuery = false;
            mesh.TileLoader = null;
        }
    }

    /// <summary>
    /// <see cref="IMeshPointQuery.TryGetRandomPointAround"/> over the loaded tiles (no tile is read). Unavailable (false, nothing drawn
    /// from <paramref name="random"/>) when the map has no mesh, the tile under <paramref name="center"/> is not loaded, no polygon lies
    /// within <see cref="NearExtents"/> of it, or an argument is unusable. The start polygon is the one a path from
    /// <paramref name="center"/> without an end tolerance would start on, so the point is reachable by
    /// <see cref="FindPath(uint, Vector3, Vector3, PathOptions?)"/>.
    /// </summary>
    public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
    {
        point = center;
        if (random is null
            || !Invariant.Check(IsFinite(center) && float.IsFinite(radius),
                $"NavMeshPathfinder.TryGetRandomPointAround called with non-finite center={center} or radius={radius} on map {mapId}")
            || radius <= 0
            || GetNavMesh(mapId) is not { } mesh)
        {
            return false;
        }

        return RandomPointAround(mesh, center, radius, random, options, out point);
    }

    /// <inheritdoc />
    public bool? TryGetRandomPointAroundShared(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
    {
        point = center;
        _sharedLock.EnterReadLock();
        try
        {
            if (!_meshes.TryGetValue(mapId, out NavMesh? mesh))
            {
                return null; // the owner reads the map's navmesh parameters on first use; a shared query never does
            }

            if (random is null
                || !Invariant.Check(IsFinite(center) && float.IsFinite(radius),
                    $"NavMeshPathfinder.TryGetRandomPointAroundShared called with non-finite center={center} or radius={radius} on map {mapId}")
                || radius <= 0
                || mesh is null)
            {
                return false;
            }

            return RandomPointAround(mesh, center, radius, random, options, out point);
        }
        finally
        {
            _sharedLock.ExitReadLock();
        }
    }

    private static bool RandomPointAround(NavMesh mesh, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
    {
        point = center;
        options ??= PathOptions.Default;
        Vector3 centerRc = NavMeshFormat.ToRecast(center);
        if (!mesh.HaveTileAt(centerRc) || !mesh.TryFindNearestPoly(centerRc, NearExtents, options, out NavPolyRef startPoly, out Vector3 onMesh))
        {
            return false;
        }

        if (!NavMeshQuery.TryRandomPointAround(mesh, startPoly, onMesh, radius, options, random, out Vector3 found))
        {
            return false;
        }

        point = NavMeshFormat.ToWorld(found);
        return true;
    }

    /// <summary>
    /// <see cref="IMeshFloorQuery.TryGetMeshFloor"/> over the loaded tiles (no tile is read): Detour <c>findNearestPoly</c> with a box
    /// <see cref="IMeshFloorQuery.MeshFloorHorizontalYards"/> wide spanning the vertical window, accepted only when the polygon's surface
    /// point is that close sideways and inside the window. Default filter (ground and water polygons).
    /// </summary>
    public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
    {
        height = 0f;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(below) || !float.IsFinite(above)
            || below < 0f || above < 0f || GetNavMesh(mapId) is not { } mesh)
        {
            return false;
        }

        const float side = IMeshFloorQuery.MeshFloorHorizontalYards;
        float half = (below + above) * 0.5f;
        Vector3 center = NavMeshFormat.ToRecast(new Vector3(x, y, z + ((above - below) * 0.5f)));
        if (!mesh.HaveTileAt(center)
            || !mesh.TryFindNearestPoly(center, new Vector3(side, half, side), PathOptions.Default, out _, out Vector3 onMesh)
            || NavMeshTile.DistanceXzSquared(onMesh, center) > side * side
            || onMesh.Y < z - below || onMesh.Y > z + above)
        {
            return false;
        }

        height = onMesh.Y;
        return true;
    }

    /// <summary>
    /// <see cref="IMeshPointQuery.Raycast"/> over the loaded tiles (no tile is read). Unavailable when the map has no mesh, the tile under
    /// <paramref name="start"/> is not loaded, or the start is farther than <see cref="NearExtents"/> from any polygon. Only the horizontal
    /// direction matters; the returned point's height is the mesh's.
    /// </summary>
    public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        if (!Invariant.Check(IsFinite(start) && IsFinite(end),
                $"NavMeshPathfinder.Raycast called with non-finite coordinates start={start}, end={end} on map {mapId}")
            || GetNavMesh(mapId) is not { } mesh)
        {
            return MeshRaycastResult.Unavailable(end);
        }

        return RaycastCore(mesh, start, end, options);
    }

    public MeshRaycastResult? RaycastShared(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        _sharedLock.EnterReadLock();
        try
        {
            if (!_meshes.TryGetValue(mapId, out NavMesh? mesh))
            {
                return null;
            }

            if (!Invariant.Check(IsFinite(start) && IsFinite(end),
                    $"NavMeshPathfinder.RaycastShared called with non-finite coordinates start={start}, end={end} on map {mapId}")
                || mesh is null)
            {
                return MeshRaycastResult.Unavailable(end);
            }

            return RaycastCore(mesh, start, end, options);
        }
        finally
        {
            _sharedLock.ExitReadLock();
        }
    }

    private static MeshRaycastResult RaycastCore(NavMesh mesh, Vector3 start, Vector3 end, PathOptions? options)
    {
        options ??= PathOptions.Default;
        Vector3 startRc = NavMeshFormat.ToRecast(start);
        if (!mesh.HaveTileAt(startRc) || !mesh.TryFindNearestPoly(startRc, NearExtents, options, out NavPolyRef startPoly, out Vector3 onMesh))
        {
            return MeshRaycastResult.Unavailable(end);
        }

        NavMeshQuery.RayResult ray = NavMeshQuery.Raycast(mesh, startPoly, onMesh, NavMeshFormat.ToRecast(end), options);
        return new MeshRaycastResult(true, ray.Hit, ray.Fraction, NavMeshFormat.ToWorld(ray.Point));
    }

    /// <summary>
    /// Whether a tile this query asked for is still unread (in flight, waiting for a read slot, or not yet installed).
    /// A tile already known to be missing has settled and is not open.
    /// </summary>
    private bool QueryTileStillOpen()
    {
        foreach ((uint, int, int) key in _pendingThisQuery)
        {
            if (!_noTile.Contains(key))
            {
                return true;
            }
        }

        return false;
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
                    PathSearchCounters.RecordTilePendingOnRealRead();
                    if (_trackPendingThisQuery)
                    {
                        _pendingThisQuery.Add((mapId, terrain.X, terrain.Y));
                    }

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
                FaultLedger.Shared.Record("nav.read", error);
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
        Task<TileRead>? oldestTask = null;
        long oldestSequence = long.MaxValue;
        foreach (((uint, int, int) key, (Task<TileRead> read, long sequence)) in _async)
        {
            if (read.IsCompleted && sequence < oldestSequence)
            {
                oldest = key;
                oldestTask = read;
                oldestSequence = sequence;
            }
        }

        if (oldest is not { } drop || !_async.Remove(drop))
        {
            return false;
        }

        if (oldestTask is { IsCompletedSuccessfully: true, Result.Error: { } droppedError })
        {
            FaultLedger.Shared.Record("nav.read", droppedError);
        }
        else if (oldestTask is { IsFaulted: true, Exception: { } taskException })
        {
            FaultLedger.Shared.Record("nav.read", taskException);
        }

        return true;
    }

    /// <summary>Read and parse one tile file on the pool, at most <see cref="MaxConcurrentTileReads"/> at a time. Never faults: a failure is the result.</summary>
    private Task<TileRead> StartTileRead(string path)
    {
        Func<string, byte[]?>? reader = FileReader;
        SemaphoreSlim slots = _tileReadSlots;
        return Task.Run(async () =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return new TileRead(TryReadTileFile(path, reader, out NavMeshTile? tile) ? tile : null, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("collision.mmap-tile-read", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", path, ex.Message);
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
        if (GetNavMesh(mapId) is { } mesh && AddTileLocked(mesh, tileX, tileY, tile))
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
        // model is in the way; then it follows the mesh and the destination is forced afterwards, unless a query with an end
        // tolerance finds either endpoint far from its polygon, which goes straight (farFromPoly below).
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

        // vmangos BuildPolyPath locates the start and the end alike (getPolyByLocation, PathFinder.cpp:188-189). So does a query with an end
        // tolerance; one without locates both in the search boxes and grades only the end.
        float tolerance = options.EndTolerance;
        if (!TryLocate(mesh, startRc, options, tolerance, out NavPolyRef startPoly, out Vector3 startOnMesh, out bool startFar)
            || !TryLocate(mesh, endRc, options, tolerance, out NavPolyRef endPoly, out Vector3 endOnMesh, out bool endFar, options.WideEndSearch))
        {
            // PathFinder.cpp:190-198: a hole in the mesh is a flying shortcut for a flier, NOPATH otherwise.
            return PathResult.StraightLine(start, end, flier ? PathType.Normal | PathType.NotUsingPath | PathType.FlyPath : PathType.NoPath);
        }

        // farFromPoly (PathFinder.cpp:209-235): with an end tolerance, either endpoint farther than it from its polygon. A flier then goes
        // straight (BuildShortcut, NORMAL | NOT_USING_PATH, :220-225); anything else still follows the mesh to the end's polygon and the path
        // is incomplete (:227-235). The swimmers' answers (the underwater path, :213-218; a swimmable end stays whole, :234) are not modelled.
        bool farFromPoly = endFar || (tolerance > 0 && startFar);
        if (flier && tolerance > 0 && farFromPoly)
        {
            return PathResult.StraightLine(start, end, PathType.Normal | PathType.NotUsingPath);
        }

        List<Vector3>? points = NavMeshQuery.FindWorldPath(mesh, start, startPoly, startOnMesh, endPoly, endOnMesh, endRc, options, out bool complete);
        if (points is null)
        {
            return PathResult.StraightLine(start, end, PathType.NoPath);
        }

        PathType type = complete && !farFromPoly ? PathType.Normal : PathType.Incomplete;
        if (forceDestination && ((type & PathType.Normal) == 0 || !InRange(end, points[^1], 1f, 1f)))
        {
            // PathFinder.cpp:451-472: keep a partial subpath only when its end is under about 55% of the way from the destination (squared distances against 0.3).
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

    private static bool TryLocate(
        NavMesh mesh, Vector3 position, PathOptions options, float tolerance, out NavPolyRef poly, out Vector3 onMesh, out bool far, bool wide = false)
    {
        far = false;
        NavTerrain include = options.EffectiveIncludeFlags;
        // ExcludeOnlyInSearch: vmangos getPolyByLocation locates by the include flags alone (PathFinder.cpp:145-157).
        NavTerrain exclude = options.ExcludeOnlyInSearch ? NavTerrain.Empty : options.ExcludeFlags;
        if (tolerance > 0)
        {
            // vmangos FindWalkPoly (PathFinder.cpp:128-142), for either endpoint (getPolyByLocation, :145-157): extents {5, 10, 5}, and a polygon whose nearest
            // point is more than 3 above the query point is refused; "far" is the farFromPoly test of BuildPolyPath (3D distance past tolerance).
            if (!mesh.TryFindNearestPoly(position, new Vector3(5, 10, 5), include, exclude, out poly, out onMesh) || onMesh.Y > position.Y + 3.0f)
            {
                far = true;
                return false;
            }

            far = Vector3.DistanceSquared(onMesh, position) > tolerance * tolerance;
            return true;
        }

        // MaNGOS Zero's getPolyByLocation boxes (NearExtents, then FarExtents).
        if (mesh.TryFindNearestPoly(position, NearExtents, include, exclude, out poly, out onMesh))
        {
            far = NavMeshTile.DistanceXzSquared(onMesh, position) > 0.01f || MathF.Abs(onMesh.Y - position.Y) > NearExtents.Y;
            return true;
        }

        far = true;
        if (mesh.TryFindNearestPoly(position, FarExtents, include, exclude, out poly, out onMesh))
        {
            return true;
        }

        // cmangos PathFinder::getPolyByLocation's second, wider box (FarPolySearchBound): the destination is off the usable mesh (a
        // walker's target swimming off a shore: the water polygons under it are not in the walker's filter), and the path goes to the
        // nearest usable polygon within it, incomplete (BuildPolyPath farFromPoly, PathFinder.cpp:452-475).
        return wide && mesh.TryFindNearestPoly(position, WideExtents, include, exclude, out poly, out onMesh);
    }

    /// <summary>Reads a navmesh file's bytes (null when it does not exist); the disk unless a test substitutes it. Runs on the world thread or, for background tile reads, on the pool.</summary>
    /// <remarks>Null (production): tile files are read into a pooled buffer by <see cref="TryReadTileFile"/>, the small <c>.mmap</c> parameters with <see cref="File.ReadAllBytes(string)"/>.</remarks>
    internal Func<string, byte[]?>? FileReader { get; set; }

    private byte[]? ReadFile(string path)
    {
        try
        {
            return FileReader is { } reader ? reader(path) : File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FaultLedger.Shared.Record("collision.mmap-read", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", path, ex.Message);
            _logger.LogError(ex, "MMaps: could not read {Path}", path);
            return null;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
