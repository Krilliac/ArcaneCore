using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using MapGrid = ArcaneCore.Game.Maps.Grid.Grid;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// One map's view of the world's collision services (<see cref="WorldCollision"/>): line of
/// sight between points and objects, paths, and the floor height that combines terrain and
/// models (vmangos <c>Map::isInLineOfSight</c>, <c>WorldObject::IsWithinLOSInMap</c>,
/// <c>TerrainInfo::GetHeightStatic</c>). Attached to every map as a default updater; reach it with
/// <c>map.Collision</c>. When the services implement <see cref="ICollisionTileLifecycle"/> the
/// map's grid creation and unloading are forwarded to them (vmangos loads vmap/mmap tiles with
/// the grid).
/// <para>Thread affinity: world thread.</para>
/// </summary>
[DefaultMapUpdater(Order = 1000)]
public sealed class MapCollision : IMapUpdater, IUnderMapClassifier
{
    /// <summary>vmangos <c>DEFAULT_HEIGHT_SEARCH</c>: how far below a point a floor is searched.</summary>
    public const float DefaultHeightSearch = 50.0f;

    /// <summary>
    /// Eye height used for object-to-object line of sight when a unit has no collision height
    /// (vmangos <c>WorldObject::IsWithinLOS</c> raises both ends by about two yards so the
    /// ground itself never blocks).
    /// </summary>
    public const float DefaultEyeHeight = 2.0f;

    /// <summary>vmangos <c>TerrainInfo::GetHeightStatic</c> looks for the floor from this far above the point.</summary>
    private const float FloorProbeOffset = 2.0f;

    private readonly WorldCollision _services;

    internal MapCollision(Map map, WorldRuntime world)
        : this(map, WorldCollision.Of(world))
    {
    }

    /// <summary>A collision view over explicit services (tests and tools).</summary>
    public MapCollision(Map map, WorldCollision services)
        : this(map, services, followGrids: true)
    {
    }

    /// <summary>A detached view (<paramref name="followGrids"/> false) does not subscribe to the map's grid events.</summary>
    internal MapCollision(Map map, WorldCollision services, bool followGrids)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(services);
        Map = map;
        _services = services;
        if (followGrids)
        {
            map.Grids.GridCreated += OnGridCreated;
            map.Grids.GridUnloaded += OnGridUnloaded;
            map.Grids.GridApproaching += OnGridApproaching;
        }
    }

    public Map Map { get; }

    /// <summary>The world's line-of-sight service.</summary>
    public ILineOfSight LineOfSight => _services.LineOfSight;

    /// <summary>The world's pathfinder.</summary>
    public IPathfinder Pathfinder => _services.Pathfinder;

    /// <summary>Whether a static model blocks the segment (no data: always in sight).</summary>
    public bool IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        if (!Invariant.Check(
                float.IsFinite(x1) && float.IsFinite(y1) && float.IsFinite(z1)
                && float.IsFinite(x2) && float.IsFinite(y2) && float.IsFinite(z2),
                $"MapCollision.IsInLineOfSight called with non-finite coordinates ({x1}, {y1}, {z1}) -> ({x2}, {y2}, {z2}) on map {Map.MapId}"))
        {
            return false;
        }

        return _services.LineOfSight.IsInLineOfSight(Map.MapId, new Vector3(x1, y1, z1), new Vector3(x2, y2, z2));
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinLOSInMap</c>: line of sight between two objects of this
    /// map, both ends raised to eye height. Objects in another map are never in sight.
    /// </summary>
    public bool IsWithinLineOfSight(WorldObject source, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target))
        {
            return true;
        }

        if (source.Map is { } a && target.Map is { } b && !ReferenceEquals(a, b))
        {
            return false;
        }

        return IsInLineOfSight(
            source.X, source.Y, source.Z + DefaultEyeHeight,
            target.X, target.Y, target.Z + DefaultEyeHeight);
    }

    /// <summary>Line of sight from an object (at eye height) to a point (raised the same, vmangos <c>IsWithinLOS</c>).</summary>
    public bool IsWithinLineOfSight(WorldObject source, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(source);
        return IsInLineOfSight(source.X, source.Y, source.Z + DefaultEyeHeight, x, y, z + DefaultEyeHeight);
    }

    /// <summary>
    /// The mesh point queries (random point, raycast) of the pathfinder that answers for this map, or null when it has none (no
    /// navigation data for the map, or a pathfinder without them): callers then keep their own behaviour.
    /// </summary>
    public IMeshPointQuery? MeshPoints => _services.PathfinderFor(Map.MapId) as IMeshPointQuery;

    /// <summary>A path on this map (no navigation data: the world's fallback pathfinder, by default a straight line).</summary>
    public PathResult FindPath(Vector3 start, Vector3 end, PathOptions? options = null)
        => _services.PathfinderFor(Map.MapId).FindPath(Map.MapId, start, end, options);

    /// <summary>
    /// vmangos <c>TerrainInfo::GetHeightStatic</c>: the floor under a point, choosing between the
    /// terrain surface and the nearest model floor below. Returns
    /// <see cref="TerrainTile.InvalidHeightValue"/> when neither is known.
    /// <list type="bullet">
    /// <item>The terrain counts only when it is below the probe point (z + 2).</item>
    /// <item>Models are searched downward from z + 2, at least down to the terrain.</item>
    /// <item>With both, the model floor wins when the point is already below the terrain, when
    /// the model floor is above the terrain, or when it is nearer to z.</item>
    /// </list>
    /// </summary>
    public float GetHeight(float x, float y, float z, bool useModels = true, float maxSearchDistance = DefaultHeightSearch)
    {
        if (!Invariant.Check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
                $"MapCollision.GetHeight called with non-finite coordinates ({x}, {y}, {z}) on map {Map.MapId}"))
        {
            return TerrainTile.InvalidHeightValue;
        }

        float probe = z + FloorProbeOffset;
        float mapHeight = TerrainTile.InvalidHeightValue;
        float terrain = Map.Terrain.GetHeight(x, y, z);
        if (terrain > TerrainTile.InvalidHeight && probe > terrain)
        {
            mapHeight = terrain;
        }

        float modelHeight = TerrainTile.InvalidHeightValue;
        if (useModels && _services.LineOfSight.Enabled)
        {
            float search = maxSearchDistance;
            if (mapHeight > TerrainTile.InvalidHeight && probe - mapHeight > search)
            {
                // Reach the terrain even when the point is high above it.
                search = probe - mapHeight + 1.0f;
            }

            if (_services.LineOfSight.GetModelHeight(Map.MapId, x, y, probe, search) is { } found)
            {
                modelHeight = found;
            }
        }

        if (modelHeight > TerrainTile.InvalidHeight)
        {
            if (mapHeight > TerrainTile.InvalidHeight)
            {
                bool preferModel = z < mapHeight || modelHeight > mapHeight
                    || MathF.Abs(mapHeight - z) > MathF.Abs(modelHeight - z);
                return preferModel ? modelHeight : mapHeight;
            }

            return modelHeight;
        }

        return mapHeight;
    }

    /// <summary>How far below the point a model floor still explains a position under the terrain surface.</summary>
    public const float UnderMapFloorSearch = 60.0f;

    /// <summary>
    /// Whether a point is under the map: below the terrain surface by more than <paramref name="minDepthYards"/> with no static model to
    /// explain it (a cave, a city or a cellar lies under the terrain on purpose). Same intent as vmangos <c>HandleMoverRelocation</c>
    /// (MovementHandler.cpp:1115-1131: a vmap-aware ground height compared with a margin), with these departures: vmangos asks
    /// <c>TerrainInfo::GetHeightStatic</c> (GridMap.cpp:815-870) for one blended floor, including its upward pass and its near-terrain
    /// pass; this query implements neither. It asks the three model questions below and compares the raw terrain height itself, and it
    /// has an Unknown answer that vmangos does not (no data is "no floor" there). There is deliberately no upward probe: model triangles
    /// are double-sided, so a unit a few yards under a floor it fell through would be excused by the underside of that floor.
    /// <list type="number">
    /// <item>No terrain data at the point: <see cref="UnderMapKind.Unknown"/>.</item>
    /// <item>The terrain is not more than the margin above the point: <see cref="UnderMapKind.Ok"/>.</item>
    /// <item>No usable model data for the map (open line of sight, or no tree): the terrain comparison decides.</item>
    /// <item>Model heights are off, or a tiled model tree whose tile here was never loaded: <see cref="UnderMapKind.Unknown"/>
    /// (models may exist and be unseen). A tile whose vmtile file does not exist counts as loaded and empty (vmangos
    /// <c>StaticMapTree::LoadMapTile</c>), so the terrain decides there.</item>
    /// <item>Inside a WMO group, or a model floor within <see cref="UnderMapFloorSearch"/> below <c>z + 2</c>, or a navigation-mesh
    /// floor in that same window (<see cref="TryGetMeshFloorUnder"/>): <see cref="UnderMapKind.Ok"/>.</item>
    /// <item>Otherwise <see cref="UnderMapKind.UnderMap"/> with depth = terrain height - z.</item>
    /// </list>
    /// Reading the coverage may read the map's vmtree from disk the first time (<see cref="VMapManager.GetTree"/>).
    /// </summary>
    public UnderMapVerdict ClassifyUnderMap(float x, float y, float z, float minDepthYards = 0f)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || TerrainTile.TileOf(x, y) is not { } tile)
        {
            return UnderMapVerdict.Unknown;
        }

        TerrainTile terrainTile = Map.Terrain.GetTile(tile.X, tile.Y);
        if (!terrainTile.HasData)
        {
            return UnderMapVerdict.Unknown;
        }

        float terrain = terrainTile.GetHeight(x, y);
        if (!float.IsFinite(terrain) || terrain <= TerrainTile.InvalidHeight)
        {
            return UnderMapVerdict.Unknown;
        }

        float depth = terrain - z;
        if (depth <= minDepthYards)
        {
            return UnderMapVerdict.Ok;
        }

        ILineOfSight models = _services.LineOfSight;
        if (!models.Enabled)
        {
            return UnderMapVerdict.Under(depth);
        }

        ModelCoverage coverage = models is IModelCoverage known ? known.CoverageAt(Map.MapId, tile.X, tile.Y) : ModelCoverage.Loaded;
        if (coverage == ModelCoverage.None)
        {
            return UnderMapVerdict.Under(depth);
        }

        if (coverage is ModelCoverage.TileNotLoaded or ModelCoverage.Unavailable)
        {
            return UnderMapVerdict.Unknown;
        }

        if (models.TryGetAreaInfo(Map.MapId, x, y, z, out _)
            || models.GetModelHeight(Map.MapId, x, y, z + FloorProbeOffset, UnderMapFloorSearch) is not null
            || TryGetMeshFloorUnder(x, y, z, UnderMapFloorSearch - FloorProbeOffset, out _))
        {
            return UnderMapVerdict.Ok;
        }

        return UnderMapVerdict.Under(depth);
    }

    /// <summary>
    /// The navigation-mesh floor under a point, searched the way the model floor is: from <c>z + 2</c> down to
    /// <paramref name="z"/> - <paramref name="maxDropYards"/>, never higher (a floor more than 2 yards over the point is a floor the
    /// unit is under, and must not excuse it). The mesh is built from the same terrain and model data (vmangos <c>MoveMapGen</c>), so
    /// a polygon there is a floor even where the model query misses it. False when the map has no mesh or its tile is not loaded.
    /// The one rule <see cref="ClassifyUnderMap"/> and the bot airwalk check share.
    /// </summary>
    public bool TryGetMeshFloorUnder(float x, float y, float z, float maxDropYards, out float height)
        => TryGetMeshFloor(x, y, z, MathF.Max(0f, maxDropYards), FloorProbeOffset, out height);

    /// <summary>
    /// Whether this map's navigation mesh has a polygon straight under or over the point with its surface between
    /// <paramref name="z"/> - <paramref name="below"/> and <paramref name="z"/> + <paramref name="above"/> (<see cref="IMeshFloorQuery"/>);
    /// false when the map's pathfinder has no mesh or the tile is not loaded. The surface a mesh route is walked on: the route's own
    /// legs run between polygon corners, whose heights are the coarse polygon's, not the detail surface's.
    /// </summary>
    public bool TryGetMeshFloor(float x, float y, float z, float below, float above, out float height)
    {
        height = 0f;
        return _services.PathfinderFor(Map.MapId) is IMeshFloorQuery mesh && mesh.TryGetMeshFloor(Map.MapId, x, y, z, below, above, out height);
    }

    /// <summary>
    /// vmangos <c>TerrainInfo::IsOutdoors</c> without WMOAreaTable.dbc: outside every WMO group,
    /// or inside a group flagged exterior.
    /// </summary>
    public bool IsOutdoors(float x, float y, float z)
        => !_services.LineOfSight.TryGetAreaInfo(Map.MapId, x, y, z, out ModelAreaInfo info) || info.IsOutdoors;

    public void Update(Map map, uint diffMs)
    {
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }

    private void OnGridCreated(MapGrid grid)
    {
        try
        {
            (int tx, int ty) = TerrainTile.TileOf(grid.Coord);
            ForEachLifecycle(l => l.OnTileLoaded(Map.MapId, tx, ty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Map.Logger.LogFault(
                "map-collision",
                ex,
                "MapCollision fault loading collision tile for grid {Grid} on map {MapId}",
                grid.Coord,
                Map.MapId);
        }
    }

    private void OnGridApproaching(Grid.GridCoord coord)
    {
        (int tx, int ty) = TerrainTile.TileOf(coord);
        if (_services.LineOfSight is ICollisionTilePrefetch los)
        {
            try
            {
                los.Prefetch(Map.MapId, tx, ty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Map.Logger.LogFault(
                    "map-collision",
                    ex,
                    "MapCollision fault prefetching LOS tile ({TileX}, {TileY}) on map {MapId}",
                    tx,
                    ty,
                    Map.MapId);
            }
        }

        if (_services.Pathfinder is ICollisionTilePrefetch path && !ReferenceEquals(path, _services.LineOfSight))
        {
            try
            {
                path.Prefetch(Map.MapId, tx, ty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Map.Logger.LogFault(
                    "map-collision",
                    ex,
                    "MapCollision fault prefetching pathfinder tile ({TileX}, {TileY}) on map {MapId}",
                    tx,
                    ty,
                    Map.MapId);
            }
        }
    }

    private void OnGridUnloaded(Grid.GridCoord coord)
    {
        try
        {
            (int tx, int ty) = TerrainTile.TileOf(coord);
            ForEachLifecycle(l => l.OnTileUnloaded(Map.MapId, tx, ty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Map.Logger.LogFault(
                "map-collision",
                ex,
                "MapCollision fault unloading collision tile for grid {Grid} on map {MapId}",
                coord,
                Map.MapId);
        }
    }

    private void ForEachLifecycle(Action<ICollisionTileLifecycle> action)
    {
        if (_services.LineOfSight is ICollisionTileLifecycle los)
        {
            try
            {
                action(los);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Map.Logger.LogFault(
                    "map-collision",
                    ex,
                    "MapCollision fault in LOS tile lifecycle on map {MapId}",
                    Map.MapId);
            }
        }

        if (_services.Pathfinder is ICollisionTileLifecycle path && !ReferenceEquals(path, _services.LineOfSight))
        {
            try
            {
                action(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Map.Logger.LogFault(
                    "map-collision",
                    ex,
                    "MapCollision fault in pathfinder tile lifecycle on map {MapId}",
                    Map.MapId);
            }
        }
    }
}
