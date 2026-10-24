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

/// <summary>
/// Optional: a pathfinder that can pick points on its navigation mesh and walk a ray along it (Detour
/// <c>findRandomPointAroundCircle</c> and <c>raycast</c>; vmangos <c>Map::GetWalkRandomPosition</c> (Map.cpp:3332) and
/// <c>Map::GetWalkHitPosition</c>, used by <c>FleeingMovementGenerator.cpp:108</c>). Used by creature wander and flee to choose destinations that are on the mesh instead
/// of a terrain-height point that may lie inside a building. Every query answers "unavailable" (false /
/// <see cref="MeshRaycastResult.Available"/> false) when the map has no mesh, the tile under the query is not loaded or the start is not
/// on the mesh, so the caller keeps its own behaviour; none throws. Positions are world coordinates; world thread.
/// </summary>
public interface IMeshPointQuery
{
    /// <summary>
    /// A random point on the mesh within <paramref name="radius"/> (horizontal) of <paramref name="center"/>, on a polygon reachable from
    /// the polygon under <paramref name="center"/> across loaded tiles. Deterministic for a given <paramref name="random"/> state; the
    /// generator is not touched when the answer is unavailable. False when unavailable.
    /// </summary>
    bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point);

    /// <summary>
    /// Walk the mesh surface from <paramref name="start"/> toward <paramref name="end"/> (horizontal) until the segment ends or leaves the
    /// mesh (a wall edge, a polygon the filter refuses, a tile that is not loaded).
    /// </summary>
    MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null);
}

/// <summary>
/// Optional: a pathfinder that can say whether its navigation mesh has a walkable surface straight under (or over) a point. The mesh is
/// built from the same terrain and model data by the reference generator (vmangos <c>MoveMapGen</c>, <c>TerrainBuilder::loadVMap</c>),
/// so a polygon there is a floor the world was built with even where the model query misses it (a gap in the extracted model, the
/// overhanging edge of a ledge polygon). "Unavailable" is false: no mesh, the tile under the point not loaded, nothing in the window.
/// World coordinates; world thread.
/// </summary>
public interface IMeshFloorQuery
{
    /// <summary>
    /// Whether a mesh polygon lies straight under or over (<paramref name="x"/>, <paramref name="y"/>) (within
    /// <see cref="MeshFloorHorizontalYards"/>) with its surface between <paramref name="z"/> - <paramref name="below"/> and
    /// <paramref name="z"/> + <paramref name="above"/>; <paramref name="height"/> is that surface. Never reads a tile.
    /// </summary>
    bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height);

    /// <summary>How far sideways from the point a polygon may lie and still count as "straight under it" (half a navmesh cell).</summary>
    const float MeshFloorHorizontalYards = 0.25f;
}

/// <summary>
/// The answer of <see cref="IMeshPointQuery.Raycast"/>. <see cref="Fraction"/> is the share of the horizontal segment walked (1: it
/// reached <c>end</c>), measured from the start's position on the mesh; <see cref="Point"/> is where it stopped, on the mesh surface.
/// </summary>
/// <param name="Available">False when no answer could be given (no mesh, tile not loaded, start off the mesh); the rest is meaningless.</param>
/// <param name="Hit">True when the ray stopped at the mesh boundary before reaching its end.</param>
public readonly record struct MeshRaycastResult(bool Available, bool Hit, float Fraction, Vector3 Point)
{
    /// <summary>No answer.</summary>
    public static MeshRaycastResult Unavailable(Vector3 end) => new(false, false, 1f, end);
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

    /// <summary>
    /// ArcaneCore extension (not a vmangos value): the search needed a navigation tile whose background read has not finished
    /// (<see cref="PathOptions.NonBlockingTiles"/>). No path was decided: the result holds only the start and is neither a path nor
    /// a refusal; the caller asks again on a later think.
    /// </summary>
    TilesPending = 0x400,
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

    /// <summary>Cooperatively stops a private navigation query; the default leaves existing callers uncancelled.</summary>
    public CancellationToken CancellationToken { get; init; } = System.Threading.CancellationToken.None;

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
    /// Polygons with any of these flags are avoided. vmangos excludes nothing by default (PathInfo::createFilter,
    /// PathFinder.cpp:657-677); random, confused, fear and flee movement exclude <see cref="NavTerrain.SteepSlopes"/> from their path
    /// search (RandomMovementGenerator.cpp:52 through MoveSplineInit.h:129-130, ConfusedMovementGenerator.cpp:79,
    /// FearMovementGenerator.cpp:38, FleeingMovementGenerator.cpp:56), and so do those creature legs here
    /// (<c>CreatureMapSystem.PathOptionsFor</c>, with <see cref="ExcludeOnlyInSearch"/>).
    /// </summary>
    public NavTerrain ExcludeFlags { get; init; } = NavTerrain.Empty;

    /// <summary>
    /// Whether <see cref="ExcludeFlags"/> only keeps the search out of its polygons, the start and end polygons being located by the
    /// include flags alone: vmangos <c>getPolyByLocation</c> builds its own filter from the include flags (PathFinder.cpp:145-157) and
    /// Detour <c>findPath</c> applies the query filter only to the polygons it enters (DetourNavMeshQuery.cpp:1037), so a creature
    /// standing on a steep slope still paths off it. Off by default: the endpoints of the other queries (the bots') keep off the
    /// excluded polygons too.
    /// </summary>
    public bool ExcludeOnlyInSearch { get; init; }

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
    /// Wall-time bound on one A* search in milliseconds (0 = none). A search past it stops as one that ran out of nodes does: it is
    /// capped and answers the corridor to the node closest to the goal (<see cref="PathSearchCounters.DeadlineHits"/> counts it). A
    /// relative duration, so it can sit in a shared options instance. Checked every 32 expansions.
    /// </summary>
    public double MaxSearchMs { get; init; }

    /// <summary>
    /// Whether the query may read the navigation tiles it needs that no grid holds: the start's and the end's, and each tile the
    /// search crosses into. vmangos loads navmesh tiles only with the map's grids (round the players), so a search towards a far
    /// destination stops at the last loaded tile and answers the point nearest the goal there; with this set the search follows
    /// the mesh to the destination. Bounded (<see cref="MMaps.NavMeshPathfinder.MaxOnDemandTiles"/>). Off by default: creature
    /// movement keeps vmangos' answers (a straight line to an unloaded tile).
    /// </summary>
    public bool LoadTiles { get; init; }

    /// <summary>
    /// With <see cref="LoadTiles"/>: never read a tile file on the calling thread. A tile that is not resident is read on the thread
    /// pool (one read per tile however often it is asked for, a few at a time) and installed by a later query on the calling thread, which
    /// owns the navmesh; meanwhile a query that cannot be decided without it answers <see cref="PathType.TilesPending"/>. A query that
    /// finds a whole path without the tile is answered as usual. Off by default; the bots' planning turns it on (a tile read under
    /// disk contention took 5.6 s of one world tick, live 2026-10-12).
    /// </summary>
    public bool NonBlockingTiles { get; init; }

    /// <summary>vmangos <c>BuildPolyPath</c> "farFromPoly" distance (PathFinder.cpp:209): an endpoint farther than this from its polygon makes the path incomplete.</summary>
    public const float VmangosEndTolerance = 7.0f;

    /// <summary>
    /// 0 (default): the start and the destination are located within <see cref="MMaps.NavMeshPathfinder.NearExtents"/> (3 yd), then
    /// <see cref="MMaps.NavMeshPathfinder.FarExtents"/>, and the path is <see cref="PathType.Incomplete"/> as soon as the destination is 0.1 yd
    /// off its polygon (the start is not graded). Above 0 (vmangos, <see cref="VmangosEndTolerance"/>): both are located as vmangos
    /// <c>getPolyByLocation</c> locates them (PathFinder.cpp:188-189; FindWalkPoly, :128-142: within 5 yd horizontally and 10 vertically, a
    /// polygon more than 3 above the point refused, else <see cref="PathType.NoPath"/>) and either is only "far" past this distance
    /// (farFromPoly, :209-235: the path is incomplete, a flier's the straight line), so a destination a few yards off the mesh whose polygon
    /// the corridor reaches is a whole path ending at the nearest mesh point, and so is a path from a start a few yards off it. vmangos'
    /// swimming answers (BuildUnderwaterPath, :167-177 and :213-218; the IsSwimmable shortcut, :199-201; a swimmable end, :234) are not
    /// modelled here; a charge takes its own (<c>ForcedMovement.ChargeSwims</c>).
    /// </summary>
    public float EndTolerance { get; init; }

    /// <summary>
    /// With <see cref="EndTolerance"/> 0: when no polygon this query may use lies in the <see cref="MMaps.NavMeshPathfinder.NearExtents"/> or
    /// <see cref="MMaps.NavMeshPathfinder.FarExtents"/> column of the destination, look for one in cmangos' wider
    /// <see cref="MMaps.NavMeshPathfinder.WideExtents"/> box (<c>FarPolySearchBound</c>, MotionGenerators/PathFinder.h:59, read by
    /// <c>PathFinder::getPolyByLocation</c>) and answer the path to its nearest point as <see cref="PathType.Incomplete"/>
    /// (<c>BuildPolyPath</c> farFromPoly: <c>closestPointOnPoly</c>, <c>PATHFIND_INCOMPLETE</c>). A walker chasing a target that swims
    /// off a shore then runs to the water's edge instead of standing where it was. Off by default (the endpoint search of every other
    /// query is unchanged); creature chase and follow turn it on.
    /// </summary>
    public bool WideEndSearch { get; init; }
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

    /// <summary>
    /// The last grid covering the tile was unloaded (release the tile): <see cref="MapCollision"/> calls it once no instance of the
    /// map id has a grid there, since one service serves them all.
    /// </summary>
    void OnTileUnloaded(uint mapId, int tileX, int tileY);
}

/// <summary>
/// Optional: a collision service that can read and parse a tile ahead of <see cref="ICollisionTileLifecycle.OnTileLoaded"/> on the
/// thread pool, so that the grid creation itself does not wait for the files (<c>World:Maps:GridPrefetchDistance</c>,
/// docs/reference/configuration.md). Called on the world thread when a grid that does not exist yet comes within reach of an
/// object; it changes nothing but when the files are read.
/// </summary>
public interface ICollisionTilePrefetch
{
    /// <summary>Start reading the tile's data in the background (no effect when it is loaded or already on its way).</summary>
    void Prefetch(uint mapId, int tileX, int tileY);
}

/// <summary>
/// Optional: a collision service that can load a tile for a question about a place no grid may cover (a bot choosing the landing beside
/// a lift far from it). The service holds such tiles among a bounded number per map and releases the one asked for longest ago to make
/// room; a grid created on one takes it over and releases it with itself. The grid hooks are not for this:
/// <see cref="ICollisionTileLifecycle.OnTileLoaded"/> makes the tile the grid's, held outside that bound until a grid on it unloads.
/// vmangos has no such call: any terrain lookup loads the tile's vmap and mmap with its <c>GridMap</c> (<c>TerrainInfo::GetGrid</c>,
/// GridMap.cpp:1132-1144 and 1146-1194), and the clean-up unloads them once a minute while no grid references the tile
/// (<c>TerrainInfo::CleanUpGrids</c>, GridMap.cpp:759-790). World coordinates; world thread.
/// </summary>
public interface ICollisionTileDemand
{
    /// <summary>
    /// Load the tile under (<paramref name="x"/>, <paramref name="y"/>) unless it is loaded, and hold it on demand; a tile held on demand
    /// counts as asked for now, and one a grid holds is left to the grid. False when no data is loaded there.
    /// </summary>
    bool LoadTileOnDemand(uint mapId, float x, float y);
}
