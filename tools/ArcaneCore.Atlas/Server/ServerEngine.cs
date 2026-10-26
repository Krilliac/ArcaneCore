using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Characters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Server;

/// <summary>Sizes of the engine's resident data. Each limit is a count of tiles (or files) held by the server's own managers.</summary>
/// <param name="TerrainTiles">Terrain tiles one map keeps before the unreferenced ones are dropped.</param>
/// <param name="ModelTiles">Model tiles kept loaded across all maps.</param>
/// <param name="NavTiles">Navmesh tiles kept loaded across all maps by point queries (path queries load and release their own).</param>
/// <param name="ModelFiles">Model files the vmap manager may cache before it is replaced by an empty one (the server's cache never evicts).</param>
/// <param name="SegmentTiles">Most model tiles a line-of-sight segment may span.</param>
/// <param name="Surfaces">Most surfaces one column query returns.</param>
public sealed record ServerEngineLimits(int TerrainTiles = 16, int ModelTiles = 24, int NavTiles = 24, int ModelFiles = 3000, int SegmentTiles = 24, int Surfaces = 256);

/// <summary>A character save queue for a world that never holds characters: the headless engine has no players, so a save request is a bug.</summary>
internal sealed class NoCharacterSaveQueue : ICharacterSaveQueue
{
    public void Enqueue(CharacterState state)
        => throw new InvalidOperationException("The Atlas server engine holds no characters, so nothing can be saved.");
}

/// <summary>
/// The server's own collision stack, built over the extracted data directory and queried on one thread: a headless
/// <see cref="WorldRuntime"/> (never started), its <see cref="TerrainManager"/> (through <see cref="WorldMaps"/>), the
/// <see cref="VMapManager"/> and <see cref="NavMeshPathfinder"/> that <see cref="CollisionServices.Install"/> creates, and one
/// <see cref="Map"/> with its real <see cref="MapCollision"/> per map id.
/// <para>
/// State ownership: the engine owns the runtime, the managers and the maps. Thread affinity: every call into them runs on
/// the engine's <see cref="OwnerThread"/> (the server types are world-thread affine and keep unsynchronised caches); the public
/// methods may be called from any thread and return tasks. The engine is created lazily: nothing is read until the first query.
/// Tiles are loaded explicitly around each query (a headless map has no grids to load them) and released least recently used first
/// within <see cref="ServerEngineLimits"/>.
/// </para>
/// </summary>
public sealed class ServerEngine : IDisposable
{
    /// <summary>How far above the query point <c>MapCollision.GetHeight</c> starts looking for a floor (<c>FloorProbeOffset</c>, MapCollision.cs:34).</summary>
    public const float FloorProbeOffset = 2.0f;

    private readonly ServerData _data;
    private readonly ILoggerFactory _loggers;
    private readonly ServerEngineLimits _limits;
    private readonly OwnerThread _owner = new("atlas-server-engine");
    private readonly Dictionary<uint, MapContext> _maps = [];
    private readonly Residency<(uint Map, int X, int Y)> _modelTiles;
    private readonly Residency<(uint Map, int X, int Y)> _navTiles;
    private readonly ILogger _log;
    private WorldRuntime? _world;
    private WorldCollision? _collision;
    private VMapManager? _vmaps;
    private NavMeshPathfinder? _nav;
    private ILogger? _engineLogger;
    private int _modelCacheResets;
    private long _queriesRun;
    private long _queriesRefused;
    private long _queriesFailed;
    private double _lastQueryMs;
    private volatile ServerStatus? _snapshot;

    /// <summary>Creates an engine over <paramref name="data"/>; nothing is read yet.</summary>
    public ServerEngine(ServerData data, ILoggerFactory loggers, ServerEngineLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(loggers);
        _data = data;
        _loggers = loggers;
        _limits = limits ?? new ServerEngineLimits();
        _modelTiles = new(_limits.ModelTiles);
        _navTiles = new(_limits.NavTiles);
        _log = loggers.CreateLogger("ArcaneCore.Atlas.Server.Engine");
    }

    /// <summary>The parsed-file caches the layer sources share.</summary>
    public ServerData Data => _data;

    /// <summary>The limits in force.</summary>
    public ServerEngineLimits Limits => _limits;

    /// <summary>True when the calling thread is the engine's owner thread.</summary>
    public bool OnOwnerThread => _owner.IsCurrent;

    /// <summary>
    /// A snapshot of the engine. Before the first query it describes the data directories only; afterwards it is the state
    /// after the last finished query, with the owner thread's live queue depth.
    /// </summary>
    public ServerStatus Status()
    {
        ServerStatus? last = _snapshot;
        if (last is null)
        {
            return Compose(started: false, [], 0, 0, 0, 0);
        }

        return last with { PendingJobs = _owner.Pending, CompletedJobs = _owner.Completed, Caches = _data.CacheStats };
    }

    /// <summary>The state of the three data files of a tile, read by the Atlas parsers (any thread).</summary>
    public JsonObject DescribeFiles(TileKey tile)
    {
        var o = new JsonObject();
        if (!tile.IsInGrid)
        {
            return o;
        }

        ParsedFile<TerrainTile> terrain = _data.TerrainFile(tile);
        o["map"] = FileState(terrain.IsOk && terrain.Value.Storage == TerrainTile.HeightStorage.Flat ? TileState.Empty : terrain.State, terrain.Detail, TerrainName(tile));
        ParsedFile<VMapTreeData> tree = _data.VMapTreeFile(tile.MapId);
        if (tree.IsOk && !tree.Value.IsTiled)
        {
            o["vmtile"] = FileState(TileState.NotApplicable, $"{tree.Detail} holds all models of the map", VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y));
        }
        else
        {
            ParsedFile<VMapTileData> models = _data.VMapTileFile(tile);
            TileState state = models.IsOk && models.Value.Spawns.Count == 0 ? TileState.Empty : models.State;
            o["vmtile"] = FileState(state, models.Detail, VMapFormat.TileFileName(tile.MapId, tile.X, tile.Y));
        }

        ParsedFile<NavTileData> nav = _data.NavFile(tile);
        TileState navState = nav.IsOk && nav.Value.Tile.Polys.All(p => p.IsOffMeshConnection) ? TileState.Empty : nav.State;
        o["mmtile"] = FileState(navState, nav.Detail, NavMeshFormat.TileFileName(tile.MapId, tile.X, tile.Y));
        return o;
    }

    /// <summary>The terrain, liquid, model, mesh and combined answers of the server at a position (<c>query.height</c>).</summary>
    public Task<HeightReport> QueryHeightAsync(uint mapId, Vector3 at, float search, CancellationToken cancellationToken = default)
        => Run(() => HeightCore(mapId, at, search), cancellationToken);

    /// <summary>Line of sight between two points: the spell test, the movement test and a terrain sample (<c>query.los</c>).</summary>
    public Task<LosReport> QueryLosAsync(uint mapId, Vector3 from, Vector3 to, CancellationToken cancellationToken = default)
        => Run(() => LosCore(mapId, from, to), cancellationToken);

    /// <summary>The server's path between two points and the corridor of polygons it crosses (<c>query.path</c>).</summary>
    public Task<PathReport> QueryPathAsync(uint mapId, Vector3 from, Vector3 to, PathMover mover, PathOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Run(() => PathCore(mapId, from, to, mover, options), cancellationToken);
    }

    /// <summary>Every surface on a vertical column, lowest first (<c>query.floorstack</c>).</summary>
    public Task<FloorStackReport> QueryFloorStackAsync(uint mapId, float x, float y, float zMin, float zMax, CancellationToken cancellationToken = default)
        => Run(() => FloorStackCore(mapId, x, y, zMin, zMax), cancellationToken);

    /// <summary>Runs <paramref name="work"/> on the owner thread with the engine started (tests and probes that need the server objects).</summary>
    public Task<T> RunOnOwnerAsync<T>(Func<EngineAccess, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(() =>
        {
            EnsureStarted();
            return work(new EngineAccess(this));
        }, cancellationToken);
    }

    /// <summary>Stops the owner thread (finishing queued queries) and disposes the headless world.</summary>
    public void Dispose()
    {
        if (_owner.Started)
        {
            try
            {
                _owner.RunAsync(() =>
                {
                    _world?.Dispose();
                    _world = null;
                }).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is AggregateException or ObjectDisposedException)
            {
                _log.LogWarning(e, "The server engine's world did not dispose cleanly.");
            }
        }

        _owner.Dispose();
    }

    private static JsonObject FileState(TileState state, string? detail, string fileName)
        => new() { ["file"] = fileName, ["state"] = state.ToString(), ["detail"] = detail };

    private static string TerrainName(TileKey tile) => TerrainTile.FileName(tile.MapId, tile.X, tile.Y);

    private Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
        => _owner.RunAsync(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                T result = work();
                Trim();
                Interlocked.Increment(ref _queriesRun);
                return result;
            }
            catch (ServerEngineException)
            {
                Interlocked.Increment(ref _queriesRefused);
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                Interlocked.Increment(ref _queriesFailed);
                _log.LogWarning(e, "A server engine query failed ({Type}).", e.GetType().Name);
                throw;
            }
            finally
            {
                _lastQueryMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _snapshot = Snapshot();
            }
        }, cancellationToken);

    private void EnsureStarted()
    {
        if (_world is not null)
        {
            return;
        }

        string? directory = _data.Paths.TerrainData;
        _engineLogger = new WarningCeilingLogger(_loggers.CreateLogger("ArcaneCore.Atlas.Server.Game"));
        var options = new WorldRuntimeOptions
        {
            AutosaveIntervalMs = 0,
            Maps = new MapOptions { DataDirectory = directory ?? string.Empty },
        };
        var world = new WorldRuntime(options, new NoCharacterSaveQueue(), new WarningCeilingLogger<WorldRuntime>(_engineLogger));
        WorldMaps.Attach(world, _engineLogger);
        WorldCollision collision = WorldCollision.Of(world);
        CollisionServices.Install(collision, new CollisionOptions(), directory, _engineLogger);
        _collision = collision;
        _vmaps = collision.LineOfSight as VMapManager;
        _nav = collision.Pathfinder as NavMeshPathfinder;
        _world = world;
    }

    private MapContext Context(uint mapId)
    {
        if (_maps.TryGetValue(mapId, out MapContext? known))
        {
            return known;
        }

        if (_data.Paths.TerrainData is null)
        {
            throw new ServerEngineException(ServerErrorKind.Unavailable, "No terrain data directory is configured, so the server engine has nothing to read.");
        }

        if (!_data.MapHasAnyData(mapId))
        {
            throw new ServerEngineException(ServerErrorKind.Unavailable, $"Map {mapId} has no maps, vmaps or mmaps data under '{_data.Paths.TerrainData}'.");
        }

        EnsureStarted();

        // The map is built directly (its constructor is internal to ArcaneCore.Game): WorldRuntime.GetMap would also attach every default
        // per-map system, none of which a collision query reads. MapCollision is the class the server's maps use, over this world's services.
        var map = new Map(mapId, 0, _world!, _engineLogger!);
        var context = new MapContext(map, new MapCollision(map, _collision!, followGrids: false));
        _maps[mapId] = context;
        return context;
    }

    private void EnsureModelTile(uint mapId, int tileX, int tileY)
    {
        if (_vmaps is null || !_modelTiles.Touch((mapId, tileX, tileY)))
        {
            return;
        }

        _vmaps.LoadTile(mapId, tileX, tileY);
    }

    private void EnsureNavTile(uint mapId, int tileX, int tileY)
    {
        if (_nav is null || !_navTiles.Touch((mapId, tileX, tileY)))
        {
            return;
        }

        _nav.LoadTile(mapId, tileX, tileY);
    }

    /// <summary>Releases tiles beyond the limits: the least recently used model and navmesh tiles, unreferenced terrain, and the model file cache.</summary>
    private void Trim()
    {
        foreach ((uint map, int x, int y) in _modelTiles.Overflow())
        {
            _vmaps?.UnloadTile(map, x, y);
        }

        foreach ((uint map, int x, int y) in _navTiles.Overflow())
        {
            _nav?.UnloadTile(map, x, y);
        }

        foreach (MapContext context in _maps.Values)
        {
            if (context.Map.Terrain.LoadedTileCount > _limits.TerrainTiles)
            {
                // Nothing references a tile of a headless map (no grids), so this frees every loaded tile; the next query reads again.
                context.Map.Terrain.CleanUp(TerrainInfo.CleanUpIntervalMs);
            }
        }

        if (_vmaps is { } vmaps && vmaps.ModelFilesLoaded > _limits.ModelFiles && _collision is not null)
        {
            // VMapManager keeps every model it has parsed for good, so a long session over many maps would only grow: start again
            // with an empty one. The next query loads the tiles it needs.
            foreach ((uint map, int x, int y) in _modelTiles.Clear())
            {
                vmaps.UnloadTile(map, x, y);
            }

            ILineOfSight? fresh = CollisionServices.CreateLineOfSight(new CollisionOptions(), _data.Paths.TerrainData, _engineLogger!);
            if (fresh is VMapManager replacement)
            {
                _collision.Install(lineOfSight: replacement);
                _vmaps = replacement;
                _modelCacheResets++;
            }
        }
    }

    private ServerStatus Snapshot()
        => Compose(
            _world is not null,
            [.. _maps.Keys.Order()],
            _maps.Values.Sum(m => m.Map.Terrain.LoadedTileCount),
            _modelTiles.Count,
            _navTiles.Count,
            _vmaps?.ModelFilesLoaded ?? 0);

    private ServerStatus Compose(bool started, IReadOnlyList<uint> maps, int terrainTiles, int modelTiles, int navTiles, int modelFiles)
        => new(
            started,
            _data.Paths.TerrainData,
            _data.MapsDirectory is not null,
            _data.VMapsDirectory is not null,
            _data.MMapsDirectory is not null,
            maps,
            terrainTiles,
            modelTiles,
            navTiles,
            modelFiles,
            _modelCacheResets,
            Interlocked.Read(ref _queriesRun),
            Interlocked.Read(ref _queriesRefused),
            Interlocked.Read(ref _queriesFailed),
            _lastQueryMs,
            _owner.Pending,
            _owner.Completed,
            _data.CacheStats);

    // ----- query.height -----

    private HeightReport HeightCore(uint mapId, Vector3 at, float search)
    {
        MapContext context = Context(mapId);
        (int X, int Y)? index = TerrainTile.TileOf(at.X, at.Y);
        if (index is { } tile)
        {
            EnsureModelTile(mapId, tile.X, tile.Y);
            EnsureNavTile(mapId, tile.X, tile.Y);
        }

        TerrainSample terrain = SampleTerrain(context, at.X, at.Y);
        LiquidSample liquid = SampleLiquid(context, at);
        ModelSample models = SampleModels(mapId, index, at, search);
        float collision = context.Collision.GetHeight(at.X, at.Y, at.Z, useModels: true, search);
        IndoorSample indoor = SampleIndoor(context, mapId, at);
        MeshSample mesh = SampleMesh(context, mapId, at, search);
        UnderMapVerdict under = context.Collision.ClassifyUnderMap(at.X, at.Y, at.Z);
        return new HeightReport(mapId, at, terrain, liquid, models, ReportJson.Height(collision), indoor, mesh, under);
    }

    private static TerrainSample SampleTerrain(MapContext context, float x, float y)
    {
        if (TerrainTile.TileOf(x, y) is not { } index)
        {
            return new TerrainSample(true, -1, -1, false, "none", 0, 0, null, false, false, false, 0);
        }

        TerrainTile tile = context.Map.Terrain.GetTile(index.X, index.Y);

        // The squares the lookup picks: GridMap::getHeightFromFloat's integer part of MAP_RESOLUTION * (32 - x / SIZE_OF_GRIDS), wrapped to the tile.
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:483-500 square index; vmangos@0e3ff01 src/game/Maps/GridMap.cpp:291-299 x = MAP_RESOLUTION * (32 - x / SIZE_OF_GRIDS), masked to 0..127
        int row = (int)(GridDefines.MapResolution * (32 - (x / GridDefines.SizeOfGrids))) & (GridDefines.MapResolution - 1);
        int col = (int)(GridDefines.MapResolution * (32 - (y / GridDefines.SizeOfGrids))) & (GridDefines.MapResolution - 1);
        float z = tile.GetHeight(x, y);
        bool flat = tile.Storage == TerrainTile.HeightStorage.Flat;
        return new TerrainSample(
            OutsideMap: false,
            index.X,
            index.Y,
            tile.HasData,
            TerrainLayerSource.StorageName(tile),
            row,
            col,
            ReportJson.Height(z),
            Hole: tile.IsServerHole(row, col),
            HoleMaskCovers: tile.HoleMaskCovers(row, col),
            NoHeight: tile.HasData && flat,
            tile.GetAreaFlag(x, y));
    }

    private static LiquidSample SampleLiquid(MapContext context, Vector3 at)
    {
        TerrainInfo terrain = context.Map.Terrain;
        LiquidStatus status = terrain.GetLiquidStatus(at.X, at.Y, at.Z, LiquidTypeFlags.AllLiquids, out LiquidData data);
        TerrainTile tile = terrain.GetTile(at.X, at.Y);
        LiquidStatus any = tile.GetLiquidStatus(at.X, at.Y, at.Z, LiquidTypeFlags.None, out LiquidData anyData);
        LiquidData shown = status != LiquidStatus.NoWater ? data : anyData;
        bool present = status != LiquidStatus.NoWater || any != LiquidStatus.NoWater;
        return new LiquidSample(
            status.ToString(),
            any.ToString(),
            present ? (byte)shown.TypeFlags : (byte)0,
            present ? TerrainLayerSource.LiquidTypeName((byte)shown.TypeFlags) : "None",
            present ? shown.Entry : 0,
            present ? ReportJson.Height(shown.Level) : null,
            present ? ReportJson.Height(shown.DepthLevel) : null);
    }

    private ModelSample SampleModels(uint mapId, (int X, int Y)? index, Vector3 at, float search)
    {
        float probe = at.Z + FloorProbeOffset;
        if (_vmaps is not { HeightEnabled: true } vmaps)
        {
            return new ModelSample(false, "Unavailable", probe, search, null, null);
        }

        ModelCoverage coverage = index is { } t ? vmaps.CoverageAt(mapId, t.X, t.Y) : ModelCoverage.TileNotLoaded;
        float? withM2 = vmaps.GetModelHeight(mapId, at.X, at.Y, probe, search);
        float? withoutM2 = null;
        if (vmaps.GetTree(mapId) is { } tree)
        {
            // The server's tree search always counts doodads (VMapTree.GetHeight); the same ray with them ignored shows what is only a doodad.
            Vector3 origin = VMapFormat.ToInternal(new Vector3(at.X, at.Y, probe));
            float distance = MathF.Abs(search);
            if (tree.GetIntersectionTime(origin, -Vector3.UnitZ, ref distance, stopAtFirstHit: false, ignoreM2: true))
            {
                withoutM2 = probe - distance;
            }
        }

        return new ModelSample(true, coverage.ToString(), probe, search, withM2, withoutM2);
    }

    private IndoorSample SampleIndoor(MapContext context, uint mapId, Vector3 at)
    {
        ILineOfSight los = _collision!.LineOfSight;
        bool outdoors = context.Collision.IsOutdoors(at.X, at.Y, at.Z);
        if (los.TryGetAreaInfo(mapId, at.X, at.Y, at.Z, out ModelAreaInfo info))
        {
            return new IndoorSample(true, info.MogpFlags, info.AdtId, info.RootId, info.GroupId, info.GroundZ, outdoors);
        }

        return new IndoorSample(false, 0, 0, 0, 0, 0, outdoors);
    }

    private MeshSample SampleMesh(MapContext context, uint mapId, Vector3 at, float search)
    {
        if (_nav?.GetNavMesh(mapId) is not { } mesh)
        {
            return new MeshSample(false, false, null, null);
        }

        Vector3 recast = NavMeshFormat.ToRecast(at);
        bool tileLoaded = mesh.HaveTileAt(recast);
        float? floor = context.Collision.TryGetMeshFloor(at.X, at.Y, at.Z, search, FloorProbeOffset, out float height) ? height : null;
        NavPolyInfo? nearest = null;
        if (tileLoaded && mesh.TryFindNearestPoly(recast, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef poly, out Vector3 point))
        {
            NavPoly p = poly.Tile.Polys[poly.Poly];
            Vector3 world = NavMeshFormat.ToWorld(point);
            nearest = new NavPolyInfo(poly.Tile.X, poly.Tile.Y, poly.Poly, p.Flags, p.Area, world.Z, Vector3.Distance(world, at));
        }

        return new MeshSample(true, tileLoaded, floor, nearest);
    }

    // ----- query.los -----

    private LosReport LosCore(uint mapId, Vector3 from, Vector3 to)
    {
        MapContext context = Context(mapId);
        float length = Vector3.Distance(from, to);
        int tilesLoaded = 0;
        var tiles = new List<(int X, int Y)>();
        foreach ((int X, int Y) tile in TilesAlong(from, to))
        {
            if (tiles.Count >= _limits.SegmentTiles)
            {
                throw new ServerEngineException(ServerErrorKind.BadRequest, $"The segment spans more than {_limits.SegmentTiles} tiles; shorten it.");
            }

            tiles.Add(tile);
        }

        foreach ((int x, int y) in tiles)
        {
            EnsureModelTile(mapId, x, y);
            tilesLoaded++;
        }

        bool spellClear = context.Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z);
        Vector3? spellHit = null;
        bool movementClear = true;
        Vector3? movementHit = null;
        if (_vmaps is { LineOfSightEnabled: true } vmaps)
        {
            movementClear = vmaps.IsInLineOfSight(mapId, from, to, ignoreM2: false);
            if (!spellClear && vmaps.GetTree(mapId) is { } tree)
            {
                spellHit = FirstHit(tree, from, to, ignoreM2: true);
            }

            if (!movementClear && vmaps.TryGetObjectHit(mapId, from, to, 0f, out Vector3 hit))
            {
                movementHit = hit;
            }
        }

        TerrainLos terrain = SampleTerrainAlong(context, from, to);
        return new LosReport(
            mapId,
            from,
            to,
            length,
            new LosSide(spellClear, spellHit),
            new LosSide(movementClear, movementHit),
            terrain,
            _vmaps is { LineOfSightEnabled: true },
            tilesLoaded);
    }

    private static Vector3? FirstHit(VMapTree tree, Vector3 from, Vector3 to, bool ignoreM2)
    {
        Vector3 a = VMapFormat.ToInternal(from);
        Vector3 b = VMapFormat.ToInternal(to);
        float length = Vector3.Distance(a, b);
        if (!(length >= 1e-10f))
        {
            return null;
        }

        Vector3 direction = (b - a) / length;
        float distance = length;
        return tree.GetIntersectionTime(a, direction, ref distance, stopAtFirstHit: false, ignoreM2)
            ? VMapFormat.ToWorld(a + (direction * distance))
            : null;
    }

    private static TerrainLos SampleTerrainAlong(MapContext context, Vector3 from, Vector3 to)
    {
        int steps = PlanarSteps(from, to);
        int unknown = 0;
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(from, to, (float)i / steps);
            float ground = context.Map.Terrain.GetHeight(p.X, p.Y, p.Z);
            if (ReportJson.Height(ground) is not { } g)
            {
                unknown++;
                continue;
            }

            if (p.Z < g)
            {
                return new TerrainLos(false, p, i + 1, unknown);
            }
        }

        return new TerrainLos(true, null, steps + 1, unknown);
    }

    /// <summary>
    /// One step per yard of the segment's horizontal length. Terrain is a function of (x, y), so the vertical part of a
    /// segment needs no samples of its own: a near-vertical segment of 800000 yards costs one lookup, not 800000.
    /// </summary>
    private static int PlanarSteps(Vector3 from, Vector3 to) =>
        Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y))));

    /// <summary>The terrain tiles a segment crosses (every horizontal yard of it, deduplicated, in order).</summary>
    private static IEnumerable<(int X, int Y)> TilesAlong(Vector3 from, Vector3 to)
    {
        int steps = PlanarSteps(from, to);
        (int, int)? last = null;
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(from, to, (float)i / steps);
            if (TerrainTile.TileOf(p.X, p.Y) is { } tile && last != tile)
            {
                last = tile;
                yield return tile;
            }
        }
    }

    // ----- query.path -----

    private PathReport PathCore(uint mapId, Vector3 from, Vector3 to, PathMover mover, PathOptions options)
    {
        MapContext context = Context(mapId);
        if (_nav is null || _nav.GetNavMesh(mapId) is not { } mesh)
        {
            throw new ServerEngineException(ServerErrorKind.Unavailable, $"Map {mapId} has no navmesh parameters (no usable {NavMeshFormat.ParamsFileName(mapId)} under '{_data.Paths.TerrainData}').");
        }

        PathOptions effective = options with { Mover = mover };
        PathResult result = context.Collision.FindPath(from, to, effective);
        var polys = new List<PolyStep>();
        bool complete = false;
        string note;
        Vector3 startRc = NavMeshFormat.ToRecast(from);
        Vector3 endRc = NavMeshFormat.ToRecast(to);
        if (!mesh.HaveTileAt(startRc) || !mesh.HaveTileAt(endRc))
        {
            note = "a tile under an endpoint is not loaded, so no corridor was worked out";
        }
        else if (!mesh.TryFindNearestPoly(startRc, NavMeshPathfinder.NearExtents, effective, out NavPolyRef startPoly, out Vector3 startOn)
            || !mesh.TryFindNearestPoly(endRc, NavMeshPathfinder.NearExtents, effective, out NavPolyRef endPoly, out Vector3 endOn))
        {
            note = "no polygon this mover may use lies within the near extents of an endpoint, so no corridor was worked out";
        }
        else
        {
            (List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor, bool reached) = NavMeshQuery.FindCorridor(mesh, startPoly, startOn, endPoly, endOn, effective);
            complete = reached;
            foreach ((NavPolyRef poly, _, _) in corridor)
            {
                NavPoly p = poly.Tile.Polys[poly.Poly];
                polys.Add(new PolyStep(poly.Tile.X, poly.Tile.Y, poly.Poly, p.Flags, p.Area));
            }

            note = "worked out again with NavMeshQuery.FindCorridor from the polygons nearest the endpoints within the near extents; the path itself is NavMeshPathfinder.FindPath's";
        }

        return new PathReport(mapId, from, to, result.Type, result.Points, result.Length, polys, complete, note, mover, effective);
    }

    // ----- query.floorstack -----

    private FloorStackReport FloorStackCore(uint mapId, float x, float y, float zMin, float zMax)
    {
        MapContext context = Context(mapId);
        (int X, int Y)? index = TerrainTile.TileOf(x, y);
        if (index is { } tile)
        {
            EnsureModelTile(mapId, tile.X, tile.Y);
            EnsureNavTile(mapId, tile.X, tile.Y);
        }

        var surfaces = new List<FloorSurface>();
        bool truncated = false;

        // Terrain: one surface.
        float ground = context.Map.Terrain.GetHeight(x, y, zMax);
        if (ReportJson.Height(ground) is { } g && g >= zMin && g <= zMax)
        {
            TerrainSample sample = SampleTerrain(context, x, y);
            surfaces.Add(new FloorSurface("terrain", g, new JsonObject { ["storage"] = sample.Storage, ["square"] = new JsonArray(sample.Row, sample.Col), ["areaFlag"] = sample.AreaFlag }));
        }

        // Liquid: the surface of the cell, when it lies in the window.
        TerrainTile terrainTile = context.Map.Terrain.GetTile(x, y);
        LiquidStatus liquidStatus = terrainTile.GetLiquidStatus(x, y, zMax, LiquidTypeFlags.None, out LiquidData liquid);
        float level = terrainTile.GetLiquidLevel(x, y);
        if (liquidStatus != LiquidStatus.NoWater || ReportJson.Height(level) is not null)
        {
            if (ReportJson.Height(level) is { } l && l >= zMin && l <= zMax)
            {
                surfaces.Add(new FloorSurface("liquid", l, new JsonObject
                {
                    ["type"] = (byte)liquid.TypeFlags,
                    ["typeName"] = TerrainLayerSource.LiquidTypeName((byte)liquid.TypeFlags),
                    ["entry"] = liquid.Entry,
                }));
            }
        }

        // Models: rays straight down from zMax, restarted just under each hit, once counting doodads and once ignoring them.
        if (_vmaps is { HeightEnabled: true } vmaps && vmaps.GetTree(mapId) is { } tree && zMax >= zMin)
        {
            var withoutM2 = new List<float>();
            foreach (float z in ColumnHits(tree, x, y, zMin, zMax, ignoreM2: true))
            {
                withoutM2.Add(z);
            }

            foreach (float z in ColumnHits(tree, x, y, zMin, zMax, ignoreM2: false))
            {
                if (surfaces.Count >= _limits.Surfaces)
                {
                    truncated = true;
                    break;
                }

                bool doodadOnly = !withoutM2.Any(w => MathF.Abs(w - z) <= 0.01f);
                surfaces.Add(new FloorSurface("model", z, new JsonObject { ["doodadOnly"] = doodadOnly }));
            }
        }

        // Navmesh polygons whose footprint holds the point.
        if (_nav?.GetNavMesh(mapId) is { } mesh)
        {
            NavMeshParams parameters = mesh.Parameters;
            Vector3 recast = NavMeshFormat.ToRecast(new Vector3(x, y, 0f));
            int detourX = (int)MathF.Floor((recast.X - parameters.Origin.X) / parameters.TileWidth);
            int detourY = (int)MathF.Floor((recast.Z - parameters.Origin.Z) / parameters.TileHeight);
            if (mesh.GetTile(detourX, detourY) is { } meshTile)
            {
                for (int i = 0; i < meshTile.Polys.Length; i++)
                {
                    if (!meshTile.Polys[i].IsOffMeshConnection && meshTile.TryHeightAtXz(i, recast, out float h) && h >= zMin && h <= zMax)
                    {
                        if (surfaces.Count >= _limits.Surfaces)
                        {
                            truncated = true;
                            break;
                        }

                        NavPoly p = meshTile.Polys[i];
                        surfaces.Add(new FloorSurface("mesh", h, new JsonObject
                        {
                            ["tile"] = new JsonArray(detourX, detourY),
                            ["poly"] = i,
                            ["flags"] = $"0x{p.Flags:X}",
                            ["flagNames"] = NavLayerSource.NavFlagNames(p.Flags),
                            ["area"] = p.Area,
                        }));
                    }
                }
            }
        }

        surfaces.Sort(static (a, b) => a.Z.CompareTo(b.Z));
        return new FloorStackReport(mapId, x, y, zMin, zMax, surfaces, truncated);
    }

    /// <summary>The heights of the model surfaces on a column between <paramref name="zMin"/> and <paramref name="zMax"/>, highest first.</summary>
    private static IEnumerable<float> ColumnHits(VMapTree tree, float x, float y, float zMin, float zMax, bool ignoreM2)
    {
        const float step = 0.001f;
        const int maxHits = 64;
        float top = zMax;
        for (int i = 0; i < maxHits && top >= zMin; i++)
        {
            Vector3 origin = VMapFormat.ToInternal(new Vector3(x, y, top));
            float distance = top - zMin;
            if (!tree.GetIntersectionTime(origin, -Vector3.UnitZ, ref distance, stopAtFirstHit: false, ignoreM2))
            {
                yield break;
            }

            float z = top - distance;
            yield return z;
            top = z - step;
        }
    }

    /// <summary>The server objects behind the engine, for tests and probes that run on the owner thread.</summary>
    public readonly struct EngineAccess
    {
        private readonly ServerEngine _engine;

        internal EngineAccess(ServerEngine engine) => _engine = engine;

        /// <summary>The headless world.</summary>
        public WorldRuntime World => _engine._world!;

        /// <summary>The installed collision services.</summary>
        public WorldCollision Collision => _engine._collision!;

        /// <summary>The vmap manager, or null when the data has none.</summary>
        public VMapManager? VMaps => _engine._vmaps;

        /// <summary>The navmesh pathfinder, or null when the data has none.</summary>
        public NavMeshPathfinder? Nav => _engine._nav;

        /// <summary>The engine's terrain manager.</summary>
        public TerrainManager Terrain => WorldMaps.Of(_engine._world!).Terrain;

        /// <summary>The map object for <paramref name="mapId"/> with its real <see cref="MapCollision"/>.</summary>
        public (Map Map, MapCollision Collision) Map(uint mapId)
        {
            MapContext context = _engine.Context(mapId);
            return (context.Map, context.Collision);
        }

        /// <summary>Loads the model and navmesh tiles of a terrain tile the way a query does.</summary>
        public void LoadTile(uint mapId, int tileX, int tileY)
        {
            _engine.EnsureModelTile(mapId, tileX, tileY);
            _engine.EnsureNavTile(mapId, tileX, tileY);
        }
    }

    private sealed class MapContext(Map map, MapCollision collision)
    {
        public Map Map { get; } = map;

        public MapCollision Collision { get; } = collision;
    }

    /// <summary>Keys in use, least recently used first out. Owner thread only.</summary>
    private sealed class Residency<TKey>(int capacity)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, long> _stamp = [];
        private long _clock;

        public int Count => _stamp.Count;

        /// <summary>Marks the key as just used; true when it was not resident (the caller loads it).</summary>
        public bool Touch(TKey key)
        {
            bool added = !_stamp.ContainsKey(key);
            _stamp[key] = ++_clock;
            return added;
        }

        /// <summary>Removes and returns the least recently used keys beyond the capacity.</summary>
        public IReadOnlyList<TKey> Overflow()
        {
            if (_stamp.Count <= capacity)
            {
                return [];
            }

            List<TKey> victims = [.. _stamp.OrderBy(p => p.Value).Take(_stamp.Count - capacity).Select(p => p.Key)];
            foreach (TKey key in victims)
            {
                _stamp.Remove(key);
            }

            return victims;
        }

        /// <summary>Removes and returns every key.</summary>
        public IReadOnlyList<TKey> Clear()
        {
            List<TKey> all = [.. _stamp.Keys];
            _stamp.Clear();
            return all;
        }
    }
}
