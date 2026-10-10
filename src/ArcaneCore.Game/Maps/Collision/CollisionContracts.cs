using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// Static-model collision for every map of a world (vmangos <c>VMAP::IVMapManager</c>):
/// line of sight, the first model hit along a segment, model floor heights and the WMO group a
/// point is inside. Positions are world coordinates (x, y, z). Implementations are world-wide
/// (one instance serves every map, keyed by map id) and are called on the world thread.
/// <para>
/// The default (<see cref="OpenLineOfSight"/>) is used when no vmap data is configured: every
/// segment is clear, there is no model height and no WMO area. Callers must treat "no data" as
/// "open", never as "blocked" (docs/integration/vmap-los.md).
/// </para>
/// </summary>
public interface ILineOfSight
{
    /// <summary>Whether real collision data backs this service (false for the open default).</summary>
    bool Enabled { get; }

    /// <summary>
    /// vmangos <c>VMapManager2::isInLineOfSight</c>: true when no static model intersects the
    /// segment <paramref name="from"/> → <paramref name="to"/>. With <paramref name="ignoreM2"/>
    /// (the vmangos default for unit LOS) doodad (M2) models do not block.
    /// </summary>
    bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true);

    /// <summary>
    /// vmangos <c>VMapManager2::getObjectHitPos</c>: the first model hit on the segment. Returns
    /// false and <paramref name="hit"/> = <paramref name="to"/> when nothing is hit. On a hit the
    /// point is moved <paramref name="modifyDistance"/> along the segment (negative = back toward
    /// <paramref name="from"/>, never past it).
    /// </summary>
    bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit);

    /// <summary>
    /// vmangos <c>VMapManager2::getHeight</c>: the nearest model surface straight below
    /// (<paramref name="maxSearchDistance"/> &gt;= 0) or above (&lt; 0) the point, within the
    /// search distance; null when there is none (or no data).
    /// </summary>
    float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance);

    /// <summary>
    /// vmangos <c>VMapManager2::getAreaInfo</c>: the WMO group whose geometry encloses the point
    /// from below (the "indoor" test); false outside every WMO or without data.
    /// </summary>
    bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info);
}

/// <summary>The WMO group found by <see cref="ILineOfSight.TryGetAreaInfo"/> (vmangos <c>AreaInfo</c>).</summary>
/// <param name="MogpFlags">The WMO group's MOGP flags.</param>
/// <param name="AdtId">The spawn's ADT id.</param>
/// <param name="RootId">The WMO root id (WMOAreaTable key part).</param>
/// <param name="GroupId">The WMO group id (WMOAreaTable key part).</param>
/// <param name="GroundZ">The world height of the group floor under the point.</param>
public readonly record struct ModelAreaInfo(uint MogpFlags, int AdtId, int RootId, int GroupId, float GroundZ)
{
    /// <summary>
    /// MOGP flag 0x8000: the group is outdoors (vmangos GridMap.cpp:875-878 <c>IsOutdoorWMO</c>;
    /// mangos-classic GridMap.cpp:887-890). Without WMOAreaTable.dbc this is the whole test.
    /// </summary>
    public const uint MogpExterior = 0x8000;

    /// <summary>MOGP flag 0x2000: the group is interior.</summary>
    public const uint MogpInterior = 0x2000;

    /// <summary>Whether this group counts as outdoors (exterior flag set).</summary>
    public bool IsOutdoors => (MogpFlags & MogpExterior) != 0;
}

/// <summary>
/// Path queries for every map of a world (vmangos <c>PathFinder</c> over <c>MMAP::MMapManager</c>).
/// Positions are world coordinates; implementations are world-wide and called on the world
/// thread. The default (<see cref="StraightLinePathfinder"/>) answers a straight line, flagged
/// <see cref="PathType.NotUsingPath"/>, when no navigation data is configured.
/// </summary>
public interface IPathfinder
{
    /// <summary>Whether real navigation data backs this service (false for the straight-line default).</summary>
    bool Enabled { get; }

    /// <summary>
    /// A path from <paramref name="start"/> to <paramref name="end"/>. The result always starts
    /// at <paramref name="start"/>; it has at least two points unless <see cref="PathType.NoPath"/>.
    /// </summary>
    PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null);
}

/// <summary>
/// Optional: a pathfinder that knows whether it has navigation data for a map (vmangos
/// <c>MMapManager</c> holds a navmesh per map or none, PathFinder.cpp:86-90). Maps it lacks are
/// routed to <see cref="WorldCollision.Fallback"/>.
/// </summary>
public interface IMapAwarePathfinder : IPathfinder
{
    /// <summary>Whether a navmesh exists for <paramref name="mapId"/>.</summary>
    bool HasNavigationData(uint mapId);
}

/// <summary>vmangos <c>PathType</c> (PathFinder.h).</summary>
[Flags]
public enum PathType
{
    Blank = 0x00,

    /// <summary>A normal path was built.</summary>
    Normal = 0x01,

    /// <summary>A straight line from start to end was used.</summary>
    Shortcut = 0x02,

    /// <summary>The path ends at the reachable point closest to the destination.</summary>
    Incomplete = 0x04,

    /// <summary>No path: start or end is off the navigation data, or nothing connects them.</summary>
    NoPath = 0x08,

    /// <summary>No navigation data was used (the straight-line default, or a disabled map).</summary>
    NotUsingPath = 0x10,

    /// <summary>vmangos <c>PATHFIND_DEST_FORCED</c>: the destination was forced onto the path end.</summary>
    DestForced = 0x20,

    /// <summary>vmangos <c>PATHFIND_FLYPATH</c>.</summary>
    FlyPath = 0x40,

    /// <summary>vmangos <c>PATHFIND_UNDERWATER</c>.</summary>
    Underwater = 0x80,

    /// <summary>vmangos <c>PATHFIND_CASTER</c>.</summary>
    Caster = 0x100,

    /// <summary>
    /// ArcaneCore extension (not a vmangos value): the path was cut to <see cref="PathOptions.MaxPoints"/>.
    /// Kept clear of vmangos' bits (<c>PathFinder.h:45-57</c>) so numeric comparisons match.
    /// </summary>
    Short = 0x200,
}

/// <summary>Navigation mesh polygon flags written by the vmangos/cmangos mmap generator (<c>NavTerrain</c>).</summary>
[Flags]
public enum NavTerrain : ushort
{
    Empty = 0x00,
    Ground = 0x01,
    Magma = 0x02,
    Slime = 0x04,
    Water = 0x08,

    /// <summary>Slopes steeper than a player can climb.</summary>
    SteepSlopes = 0x10,
}

/// <summary>Query options for <see cref="IPathfinder.FindPath"/>.</summary>
public sealed record PathOptions
{
    /// <summary>vmangos <c>MAX_POINT_PATH_LENGTH</c> (PathFinder.h:39).</summary>
    public const int DefaultMaxPoints = 256;

    /// <summary>vmangos nav query node pool (<c>navMeshQuery->init(navMesh, 2048)</c>, MoveMap.cpp:350).</summary>
    public const int DefaultMaxSearchNodes = 2048;

    /// <summary>Shared default instance.</summary>
    public static PathOptions Default { get; } = new();

    /// <summary>
    /// The unit the path is for. When set, the include flags come from its capabilities
    /// (<see cref="PathMover.IncludeFlags"/>, vmangos <c>createFilter</c>) and <see cref="IncludeFlags"/> is ignored.
    /// </summary>
    public PathMover? Mover { get; init; }

    /// <summary>Polygons must have at least one of these flags when no <see cref="Mover"/> is given (a swimming creature).</summary>
    public NavTerrain IncludeFlags { get; init; } = NavTerrain.Ground | NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime;

    /// <summary>The include flags a query uses: the mover's when there is one, else <see cref="IncludeFlags"/>.</summary>
    public NavTerrain EffectiveIncludeFlags => Mover?.IncludeFlags ?? IncludeFlags;

    /// <summary>
    /// Polygons with any of these flags are avoided. vmangos excludes nothing by default
    /// (PathFinder.cpp:657-675); fear, flee, confused and random movement exclude
    /// <see cref="NavTerrain.SteepSlopes"/> (FearMovementGenerator.cpp:38, RandomMovementGenerator.cpp:52).
    /// </summary>
    public NavTerrain ExcludeFlags { get; init; } = NavTerrain.Empty;

    /// <summary>Most points in a result (start included); longer paths are cut and flagged <see cref="PathType.Short"/>.</summary>
    public int MaxPoints { get; init; } = DefaultMaxPoints;

    /// <summary>
    /// Whether an unreachable destination may answer the path to the closest reachable point
    /// (<see cref="PathType.Incomplete"/>) instead of <see cref="PathType.NoPath"/>.
    /// </summary>
    public bool AllowPartial { get; init; } = true;

    /// <summary>Upper bound on search work (polygons or grid cells expanded) before the query gives up.</summary>
    public int MaxSearchNodes { get; init; } = DefaultMaxSearchNodes;

    /// <summary>
    /// Whether the query may read the navigation tiles it needs that no grid holds: the start's and the end's, and each tile the
    /// search crosses into. vmangos loads navmesh tiles only with the map's grids (round the players), so a search towards a far
    /// destination stops at the last loaded tile and answers the point nearest the goal there; with this set the search follows
    /// the mesh to the destination. Bounded (<see cref="MMaps.NavMeshPathfinder.MaxOnDemandTiles"/>). Off by default: creature
    /// movement keeps vmangos' answers (a straight line to an unloaded tile).
    /// </summary>
    public bool LoadTiles { get; init; }
}

/// <summary>A path answer: its classification and its corner points (world coordinates).</summary>
public sealed record PathResult(PathType Type, IReadOnlyList<Vector3> Points)
{
    /// <summary>Whether a usable path came back (anything but <see cref="PathType.NoPath"/> with points).</summary>
    public bool HasPath => (Type & PathType.NoPath) == 0 && Points.Count >= 2;

    /// <summary>The last point of the path (the destination, or the closest reachable point).</summary>
    public Vector3 End => Points.Count > 0 ? Points[^1] : default;

    /// <summary>The path's length along its corners.</summary>
    public float Length
    {
        get
        {
            float total = 0;
            for (int i = 1; i < Points.Count; i++)
            {
                total += Vector3.Distance(Points[i - 1], Points[i]);
            }

            return total;
        }
    }

    /// <summary>A two-point straight line (vmangos <c>PathFinder::BuildShortcut</c>).</summary>
    public static PathResult StraightLine(Vector3 start, Vector3 end, PathType type) => new(type, [start, end]);

    /// <summary>No path; the result holds only the start.</summary>
    public static PathResult None(Vector3 start) => new(PathType.NoPath, [start]);
}

/// <summary>
/// Optional: a collision service that wants to follow the map grids' life (vmangos loads vmap
/// and mmap tiles in <c>TerrainInfo::LoadMapAndVMap</c> and drops them with the grid). Tile
/// indices are the terrain tile indices (<see cref="Terrain.TerrainTile.TileOf(float, float)"/>).
/// </summary>
public interface ICollisionTileLifecycle
{
    /// <summary>A grid covering the tile was created (load what the tile needs).</summary>
    void OnTileLoaded(uint mapId, int tileX, int tileY);

    /// <summary>The grid covering the tile was unloaded (release the tile).</summary>
    void OnTileUnloaded(uint mapId, int tileX, int tileY);
}

/// <summary>
/// Optional: a collision service that can read and parse a tile ahead of <see cref="ICollisionTileLifecycle.OnTileLoaded"/> on the
/// thread pool, so that the grid creation itself does not wait for the files (docs/integration/tick-scaling-20261009.md). Called on
/// the world thread when a grid that does not exist yet comes within reach of an object; it changes nothing but when the files
/// are read.
/// </summary>
public interface ICollisionTilePrefetch
{
    /// <summary>Start reading the tile's data in the background (no effect when it is loaded or already on its way).</summary>
    void Prefetch(uint mapId, int tileX, int tileY);
}
