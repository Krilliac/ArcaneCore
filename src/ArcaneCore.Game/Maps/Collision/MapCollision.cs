using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
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
public sealed class MapCollision : IMapUpdater
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
        }
    }

    public Map Map { get; }

    /// <summary>The world's line-of-sight service.</summary>
    public ILineOfSight LineOfSight => _services.LineOfSight;

    /// <summary>The world's pathfinder.</summary>
    public IPathfinder Pathfinder => _services.Pathfinder;

    /// <summary>Whether a static model blocks the segment (no data: always in sight).</summary>
    public bool IsInLineOfSight(float x1, float y1, float z1, float x2, float y2, float z2)
        => _services.LineOfSight.IsInLineOfSight(Map.MapId, new Vector3(x1, y1, z1), new Vector3(x2, y2, z2));

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
        (int tx, int ty) = TerrainTile.TileOf(grid.Coord);
        ForEachLifecycle(l => l.OnTileLoaded(Map.MapId, tx, ty));
    }

    private void OnGridUnloaded(Grid.GridCoord coord)
    {
        (int tx, int ty) = TerrainTile.TileOf(coord);
        ForEachLifecycle(l => l.OnTileUnloaded(Map.MapId, tx, ty));
    }

    private void ForEachLifecycle(Action<ICollisionTileLifecycle> action)
    {
        if (_services.LineOfSight is ICollisionTileLifecycle los)
        {
            action(los);
        }

        if (_services.Pathfinder is ICollisionTileLifecycle path && !ReferenceEquals(path, _services.LineOfSight))
        {
            action(path);
        }
    }
}
