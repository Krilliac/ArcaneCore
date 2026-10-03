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
    /// <summary>MOGP flag 0x8: the group is exterior (vmangos <c>IsOutdoorWMO</c> without WMOAreaTable.dbc).</summary>
    public const uint MogpExterior = 0x8;

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

    /// <summary>The path was cut to <see cref="PathOptions.MaxPoints"/>.</summary>
    Short = 0x20,
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
    /// <summary>vmangos <c>MAX_POINT_PATH_LENGTH</c>.</summary>
    public const int DefaultMaxPoints = 74;

    /// <summary>Shared default instance.</summary>
    public static PathOptions Default { get; } = new();

    /// <summary>Polygons must have at least one of these flags (vmangos filter include flags).</summary>
    public NavTerrain IncludeFlags { get; init; } = NavTerrain.Ground | NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime;

    /// <summary>Polygons with any of these flags are avoided.</summary>
    public NavTerrain ExcludeFlags { get; init; } = NavTerrain.SteepSlopes;

    /// <summary>Most points in a result (start included); longer paths are cut and flagged <see cref="PathType.Short"/>.</summary>
    public int MaxPoints { get; init; } = DefaultMaxPoints;

    /// <summary>
    /// Whether an unreachable destination may answer the path to the closest reachable point
    /// (<see cref="PathType.Incomplete"/>) instead of <see cref="PathType.NoPath"/>.
    /// </summary>
    public bool AllowPartial { get; init; } = true;

    /// <summary>Upper bound on search work (polygons or grid cells expanded) before the query gives up.</summary>
    public int MaxSearchNodes { get; init; } = 4096;
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
