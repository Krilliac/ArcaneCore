using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision.VMaps;
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
/// the grid); an unload only when no instance of the map id still has a grid on the tile.
/// <para>Thread affinity: world thread.</para>
/// </summary>
[DefaultMapUpdater(Order = 1000)]
public sealed partial class MapCollision : IMapUpdater, IUnderMapClassifier
{
    /// <summary>vmangos <c>DEFAULT_HEIGHT_SEARCH</c>: how far below a point a floor is searched.</summary>
    public const float DefaultHeightSearch = 50.0f;

    /// <summary>
    /// How far above a point it is looked at: vmangos <c>WorldObject::IsWithinLOS</c> and <c>IsWithinLOSAtPosition</c> default
    /// <c>targetHeight</c> to 2 (Object.h:607-611). It equals the collision height of a unit without model data
    /// (<see cref="global::ArcaneCore.Game.Locomotion.LocomotionState.DefaultCollisionHeight"/>), so the area-centre check of spell targeting, which stands in for no
    /// object, raises both its ends by it.
    /// </summary>
    public const float DefaultEyeHeight = 2.0f;

    /// <summary>
    /// vmangos <c>WorldObject::GetLosCheckPosition</c> (Object.cpp:1777-1781): an object is looked at 1 yard above its position.
    /// A game object looks at the centre of its model bounds instead (GameObject.cpp:2385-2423), which needs the display bounds
    /// (<c>gameobject_display_info_addon</c>) or the game object models this codebase does not load, so it takes this fallback.
    /// </summary>
    public const float ObjectTargetHeight = 1.0f;

    /// <summary>vmangos <c>WorldObject::IsWithinLOSAtPosition</c> (Object.cpp:1770): an object that is not a unit looks from 1 yard above its position.</summary>
    public const float ObjectEyeHeight = 1.0f;

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

    /// <summary>The pathfinder that answers <see cref="FindPath"/> for this map (navigation data for it, else the world's fallback).</summary>
    public IPathfinder PathfinderForMap => _services.PathfinderFor(Map.MapId);

    /// <summary>Whether a static model blocks the segment (no data: always in sight).</summary>
    public bool IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2, bool ignoreM2 = true)
    {
        if (!Invariant.Check(
                float.IsFinite(x1) && float.IsFinite(y1) && float.IsFinite(z1)
                && float.IsFinite(x2) && float.IsFinite(y2) && float.IsFinite(z2),
                $"MapCollision.IsInLineOfSight called with non-finite coordinates ({x1}, {y1}, {z1}) -> ({x2}, {y2}, {z2}) on map {Map.MapId}"))
        {
            return false;
        }

        return _services.LineOfSight.IsInLineOfSight(Map.MapId, new Vector3(x1, y1, z1), new Vector3(x2, y2, z2), ignoreM2);
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinLOSInMap</c> (Object.cpp:1754-1764): line of sight from <paramref name="source"/>'s eye
    /// (<see cref="EyeHeightOf"/>) to <paramref name="target"/>'s check position (<see cref="ObjectTargetHeight"/> above it), the
    /// segment <see cref="LineOfSightSegment"/> gives. The ends differ, so the question is not symmetric: ask it from the object
    /// vmangos asks it from. Objects in another map are never in sight; objects within each other's bounding radius always are.
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

        // IsWithinDist(obj, 0.0f) (Object.cpp:1759-1760): objects within each other's bounding radius (3D) are in sight without a ray.
        float dx = source.X - target.X;
        float dy = source.Y - target.Y;
        float dz = source.Z - target.Z;
        float touching = source.BoundingRadius + target.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) < touching * touching)
        {
            return true;
        }

        (Vector3 from, Vector3 to) = LineOfSightSegment(source, target);
        return IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z);
    }

    /// <summary>
    /// vmangos <c>WorldObject::IsWithinLOS</c> (Object.h:607-610): line of sight from <paramref name="source"/>'s eye
    /// (<see cref="EyeHeightOf"/>) to the point <paramref name="targetHeight"/> above (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>), by default <see cref="DefaultEyeHeight"/> as there.
    /// </summary>
    public bool IsWithinLineOfSight(WorldObject source, float x, float y, float z, float targetHeight = DefaultEyeHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        return IsInLineOfSight(source.X, source.Y, source.Z + EyeHeightOf(source), x, y, z + targetHeight);
    }

    /// <summary>
    /// The segment <see cref="IsWithinLineOfSight(WorldObject, WorldObject)"/> tests: from <paramref name="source"/>'s eye to
    /// <paramref name="target"/>'s check position.
    /// </summary>
    public static (Vector3 From, Vector3 To) LineOfSightSegment(WorldObject source, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return (new Vector3(source.X, source.Y, source.Z + EyeHeightOf(source)), new Vector3(target.X, target.Y, target.Z + ObjectTargetHeight));
    }

    /// <summary>
    /// How far above its position an object looks from (vmangos <c>IsWithinLOSAtPosition</c>, Object.cpp:1770): a unit's
    /// <see cref="Unit.CollisionHeight"/>, else <see cref="ObjectEyeHeight"/>.
    /// </summary>
    public static float EyeHeightOf(WorldObject viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer is Unit unit ? unit.CollisionHeight : ObjectEyeHeight;
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
        => GetHeight(x, y, z, FloorProbeOffset, useModels, maxSearchDistance);

    /// <summary>
    /// <see cref="GetHeight(float, float, float, bool, float)"/> probing from <c>z + </c><paramref name="probeOffset"/> instead of <c>z + 2</c>
    /// (the terrain counts only below the probe, models are searched down from it), while the choice between a model floor and the terrain
    /// still compares with the real <paramref name="z"/> (a probe set low by shifting z would make every terrain over the point
    /// "above it" and prefer the models under it).
    /// </summary>
    public float GetHeight(float x, float y, float z, float probeOffset, bool useModels = true, float maxSearchDistance = DefaultHeightSearch)
    {
        if (!Invariant.Check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
                $"MapCollision.GetHeight called with non-finite coordinates ({x}, {y}, {z}) on map {Map.MapId}"))
        {
            return TerrainTile.InvalidHeightValue;
        }

        float probe = z + probeOffset;
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
            || TryGetMeshFloor(x, y, z, UnderMapFloorSearch - FloorProbeOffset, UnderMapMeshChordRise, out _)
            || LevelWithANearbyFloor(x, y, z))
        {
            return UnderMapVerdict.Ok;
        }

        return UnderMapVerdict.Under(depth);
    }

    private const float UnderMapMeshChordRise = 6.0f;
    private const float NearbyFloorAbove = 5.0f;
    private const float NearbyFloorBelow = 5.0f;
    private const int NearbyFloorAxes = 4;
    private static readonly float[] NearbyFloorRings = [1f, 2f, 3f];

    /// <summary>
    /// Whether a point that missed the vertical column probe sits in a narrow 1–3 yard seam bridged by floor on opposite sides
    /// (e.g. between WMO floor pieces in Ironforge/Undercity), as opposed to off the edge of a room into the void.
    /// </summary>
    public bool LevelWithANearbyFloor(float x, float y, float z)
    {
        ILineOfSight models = _services.LineOfSight;
        bool checkModels = models is VMapManager;
        float probeZ = z + NearbyFloorAbove;
        float searchDepth = NearbyFloorAbove + NearbyFloorBelow;

        for (int i = 0; i < NearbyFloorAxes; i++)
        {
            float angle = i * (MathF.PI / NearbyFloorAxes);
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            if (HasFloorAlongRay(models, checkModels, x, y, z, probeZ, searchDepth, cos, sin)
                && HasFloorAlongRay(models, checkModels, x, y, z, probeZ, searchDepth, -cos, -sin))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasFloorAlongRay(ILineOfSight models, bool checkModels, float x, float y, float z, float probeZ, float searchDepth, float cos, float sin)
    {
        foreach (float radius in NearbyFloorRings)
        {
            float rx = x + (radius * cos);
            float ry = y + (radius * sin);
            if ((checkModels && (models.GetModelHeight(Map.MapId, rx, ry, probeZ, searchDepth) is not null
                    || models.TryGetAreaInfo(Map.MapId, rx, ry, probeZ, out _)))
                || TryGetMeshFloor(rx, ry, z, NearbyFloorBelow, NearbyFloorAbove, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Vector2[] AnyNearbyFloorDirections =
    [
        new(1f, 0f), new(0f, 1f), new(-1f, 0f), new(0f, -1f),
        new(0.7071f, 0.7071f), new(-0.7071f, 0.7071f), new(-0.7071f, -0.7071f), new(0.7071f, -0.7071f),
    ];

    /// <summary>
    /// Whether any model or navigation-mesh floor lies within 2.5–8 yards horizontally at the point's height (between 4 yards below and
    /// 3 yards above). Used by the report-only <c>move/under-map</c> observer to excuse city doodad/grating gaps in Ironforge and Undercity
    /// without altering <see cref="ClassifyUnderMap"/> or login-floor recovery.
    /// </summary>
    public bool LevelWithAnyNearbyFloor(float x, float y, float z)
    {
        ILineOfSight models = _services.LineOfSight;
        bool checkModels = models is VMapManager;
        const float above = 3.0f;
        const float below = 4.0f;
        foreach (float radius in (ReadOnlySpan<float>)[2.5f, 5.0f, 8.0f])
        {
            foreach (Vector2 dir in AnyNearbyFloorDirections)
            {
                float px = x + (dir.X * radius);
                float py = y + (dir.Y * radius);
                if (checkModels && models.GetModelHeight(Map.MapId, px, py, z + above, above + below) is { } floor && floor >= z - below)
                {
                    return true;
                }

                if (TryGetMeshFloor(px, py, z, below, above, out _))
                {
                    return true;
                }
            }
        }

        return false;
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
    /// The navigation-mesh surface at (<paramref name="to"/>.X, <paramref name="to"/>.Y) reached by walking the mesh from
    /// <paramref name="from"/> (<see cref="IMeshPointQuery.Raycast"/>: horizontal, polygon to adjacent polygon): the storey continuous
    /// with the one under <paramref name="from"/>, whatever lies above or below it. vmangos follows the surface similarly when it builds
    /// a unit's path (<c>PathFinder::findSmoothPath</c>: <c>moveAlongSurface</c> and <c>getPolyHeight</c> every 4 yards), where a
    /// straight path's corners alone give only chords through hills and under ramps; unlike <c>moveAlongSurface</c>, which slides along
    /// a wall, this walk stops at one (false), and the caller falls back to its own estimate. False when the map has no mesh, the tile
    /// under <paramref name="from"/> is not loaded, <paramref name="from"/> is not on (within 3 yards across, 5 up or down of) a polygon
    /// <paramref name="options"/> accepts, or the segment leaves the mesh (or the polygons the filter accepts) before it ends.
    /// </summary>
    public bool TryWalkMeshSurface(Vector3 from, Vector3 to, out float height, PathOptions? options = null)
    {
        height = 0f;
        if (MeshPoints is not { } mesh || !float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(from.Z)
            || !float.IsFinite(to.X) || !float.IsFinite(to.Y) || !float.IsFinite(to.Z))
        {
            return false;
        }

        MeshRaycastResult walk = mesh.Raycast(Map.MapId, from, to, options);
        if (!walk.Available || walk.Hit || !float.IsFinite(walk.Point.Z))
        {
            return false;
        }

        height = walk.Point.Z;
        return true;
    }

    /// <summary>
    /// vmangos <c>TerrainInfo::IsOutdoors</c> (GridMap.cpp:880-890) from the group the model query finds at the point itself: outside every
    /// WMO group, or inside a group whose MOGP flags say exterior. WMOAreaTable.dbc plays no part in this test (vmangos reads only the MOGP
    /// flag; <see cref="TerrainInfo.GetAreaFlag"/> is where the area table is read). The ground-over-a-building check of
    /// <see cref="TerrainInfo.TryGetWmoGroup"/> is not applied here; vmangos does apply it, and adding it would move every caller (rest, mounting, bot routing).
    /// </summary>
    public bool IsOutdoors(float x, float y, float z)
        => !_services.LineOfSight.TryGetAreaInfo(Map.MapId, x, y, z, out ModelAreaInfo info) || info.IsOutdoors;

    /// <summary>
    /// Load the model and navigation tiles under (<paramref name="x"/>, <paramref name="y"/>) for a question about a place no grid may
    /// cover (a bot choosing the landing beside a lift far from it), through the services that can (<see cref="ICollisionTileDemand"/>):
    /// each holds them among its bounded tiles loaded on demand, and a grid created there takes them over. Never through the grid hooks
    /// (<see cref="ICollisionTileLifecycle"/>), which would hold them as a grid's until a grid on the tile unloads. Nothing outside the map.
    /// </summary>
    public void LoadTilesOnDemand(float x, float y)
    {
        if (_services.LineOfSight is ICollisionTileDemand models)
        {
            models.LoadTileOnDemand(Map.MapId, x, y);
        }

        if (_services.Pathfinder is ICollisionTileDemand mesh && !ReferenceEquals(mesh, _services.LineOfSight))
        {
            mesh.LoadTileOnDemand(Map.MapId, x, y);
        }
    }

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
            FaultLedger.Shared.Record("collision.tile-load", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId} grid {grid.Coord}", ex.Message);
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
                FaultLedger.Shared.Record("collision.tile-prefetch", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId} tile ({tx}, {ty})", ex.Message);
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
                FaultLedger.Shared.Record("collision.tile-prefetch", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId} tile ({tx}, {ty})", ex.Message);
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
            // The services hold one tree and one navmesh per map id for every instance of it, and so does the terrain: vmangos unloads a
            // tile's vmaps and mmaps only once no grid of any instance references the tile (TerrainInfo::CleanUpGrids, GridMap.cpp:769-785).
            // The map's own terrain handler, subscribed in its constructor and so run before this one, has already dropped this grid's
            // reference: a count left over is another instance's grid on the same tile.
            if (Map.Terrain.RefCount(tx, ty) > 0)
            {
                return;
            }

            ForEachLifecycle(l => l.OnTileUnloaded(Map.MapId, tx, ty));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("collision.tile-unload", ex);
            AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId} grid {coord}", ex.Message);
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
                FaultLedger.Shared.Record("collision.tile-lifecycle", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId}", ex.Message);
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
                FaultLedger.Shared.Record("collision.tile-lifecycle", ex);
                AnomalyLedger.Shared.Report("collision", "tile-load-failed", $"map {Map.MapId}", ex.Message);
                Map.Logger.LogFault(
                    "map-collision",
                    ex,
                    "MapCollision fault in pathfinder tile lifecycle on map {MapId}",
                    Map.MapId);
            }
        }
    }
}
