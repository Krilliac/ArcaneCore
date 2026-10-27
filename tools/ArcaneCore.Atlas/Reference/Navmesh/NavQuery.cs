using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>A polygon of the DotRecast mesh, identified the way the server's polygons are (Detour tile coordinates and index).</summary>
/// <param name="Tile">Terrain tile (file name indices) the polygon's tile was read from, or null if it is no longer resident.</param>
/// <param name="DetourX">The header x of the polygon's tile.</param>
/// <param name="DetourY">The header y of the polygon's tile.</param>
/// <param name="Layer">The header layer of the polygon's tile.</param>
/// <param name="Poly">Index of the polygon in its tile.</param>
/// <param name="Flags">The polygon's <see cref="NavTerrain"/> flags as stored.</param>
/// <param name="Area">The area id (low six bits of the stored area-and-type byte).</param>
/// <param name="OffMesh">True for an off-mesh connection polygon.</param>
/// <param name="Center">The mean of the polygon's vertices, world coordinates (an off-mesh connection's midpoint).</param>
public sealed record NavPolyId(TileKey? Tile, int DetourX, int DetourY, int Layer, int Poly, ushort Flags, int Area, bool OffMesh, Vector3 Center)
{
    /// <summary>The key the server oracle comparisons use for a corridor polygon: <c>detourX,detourY,poly</c>.</summary>
    public string CorridorKey => $"{DetourX},{DetourY},{Poly}";

    /// <summary>JSON for command results.</summary>
    public JsonObject ToJson() => new()
    {
        ["tile"] = Tile is { } t ? new JsonArray(t.X, t.Y) : null,
        ["detour"] = new JsonArray(DetourX, DetourY, Layer),
        ["poly"] = Poly,
        ["flags"] = (int)Flags,
        ["flagNames"] = NavTerrainNames.Describe(Flags),
        ["area"] = Area,
        ["colorKey"] = NavTerrainNames.ColorKey(Flags),
        ["offMesh"] = OffMesh,
        ["center"] = new JsonArray(Center.X, Center.Y, Center.Z),
    };
}

/// <summary>
/// The polygon filter of a query: Detour's <c>dtQueryFilter::passFilter</c> (some include flag, no exclude flag), optionally refusing
/// off-mesh connection polygons the way the server's <c>NavMeshTile.Passes</c> does. The cost is the distance, as the server's search and
/// vmangos' default area costs (all 1.0) give.
/// </summary>
public sealed class NavQueryFilter(NavTerrain include, NavTerrain exclude, bool allowOffMesh) : IDtQueryFilter
{
    // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMeshQuery.cpp:72-77 (dtQueryFilter::passFilter: (poly->flags & includeFlags) != 0 &&
    // (poly->flags & excludeFlags) == 0) and src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshTile.cs:554-558 (the server also refuses off-mesh polygons).
    // vmangos never sets an area cost (grep setAreaCost over src/game and contrib/mmap/src finds none), so every area costs 1.0.

    /// <summary>The flags a polygon must have at least one of.</summary>
    public NavTerrain Include { get; } = include;

    /// <summary>The flags a polygon must have none of.</summary>
    public NavTerrain Exclude { get; } = exclude;

    /// <summary>Whether off-mesh connection polygons may be traversed (Detour does; the server's reader does not read them).</summary>
    public bool AllowOffMesh { get; } = allowOffMesh;

    /// <inheritdoc/>
    public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
        => (AllowOffMesh || poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            && (poly.flags & (ushort)Include) != 0 && (poly.flags & (ushort)Exclude) == 0;

    /// <inheritdoc/>
    public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly, long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        => RcVec3f.Distance(pa, pb);
}

/// <summary>How a query located an end point: the polygon, the nearest point on it and which search box found it.</summary>
public sealed class NavLocate
{
    /// <summary>The polygon.</summary>
    public required NavPolyId Poly { get; init; }

    /// <summary>The nearest point on the polygon, world coordinates.</summary>
    public required Vector3 Nearest { get; init; }

    /// <summary>The query point's horizontal position lies inside the polygon.</summary>
    public required bool OverPoly { get; init; }

    /// <summary>The server's "far from the mesh" test for this end (it turns a whole path into an incomplete one).</summary>
    public required bool Far { get; init; }

    /// <summary>Which search box found it: <c>near</c>, <c>far</c>, <c>wide</c> or <c>end-tolerance</c>.</summary>
    public required string Box { get; init; }

    /// <summary>The polygon reference in the mesh (valid only inside the <see cref="ReferenceNavMap.Read{T}"/> callback).</summary>
    internal long Ref { get; init; }

    /// <summary>The nearest point in Recast axes.</summary>
    internal RcVec3f Point { get; init; }
}

/// <summary>The answer of a reference path query.</summary>
/// <param name="Result">Type and corner points in the server's <see cref="PathResult"/> shape (world coordinates).</param>
/// <param name="Outcome">Why the type is what it is: <c>path</c>, <c>tile-not-loaded</c>, <c>locate-failed</c>, <c>search-failed</c>, <c>partial-refused</c> or <c>straight-path-failed</c>.</param>
/// <param name="Complete">The corridor reaches the end polygon.</param>
/// <param name="EndFar">The end point is far from its polygon (server rule).</param>
/// <param name="Start">How the start was located (null if the query stopped earlier).</param>
/// <param name="End">How the end was located (null if the query stopped earlier).</param>
/// <param name="Corridor">The polygon corridor DotRecast's <c>FindPath</c> returned.</param>
/// <param name="PointFlags">DotRecast's straight-path flags per point (1 start, 2 end, 4 off-mesh connection), aligned with the result points.</param>
/// <param name="Nodes">Nodes DotRecast's search allocated (the server stops at <see cref="PathOptions.DefaultMaxSearchNodes"/>).</param>
/// <param name="SearchStatus">DotRecast's status bits for the corridor search.</param>
public sealed record NavPathAnswer(
    PathResult Result,
    string Outcome,
    bool Complete,
    bool EndFar,
    NavLocate? Start,
    NavLocate? End,
    IReadOnlyList<NavPolyId> Corridor,
    IReadOnlyList<byte> PointFlags,
    int Nodes,
    uint SearchStatus);

/// <summary>Which search box a nearest-polygon query uses.</summary>
public enum NavSearchBox
{
    /// <summary>The server's ladder: near, then far, then wide.</summary>
    Ladder,

    /// <summary>The server's <see cref="NavMeshPathfinder.NearExtents"/> box.</summary>
    Near,

    /// <summary>The server's <see cref="NavMeshPathfinder.FarExtents"/> column.</summary>
    Far,

    /// <summary>The server's <see cref="NavMeshPathfinder.WideExtents"/> box.</summary>
    Wide,

    /// <summary>vmangos' end-point search: extents 5 x 10 x 5, refusing a polygon more than 3 above the point.</summary>
    EndTolerance,
}

/// <summary>
/// Path and nearest-polygon queries on the DotRecast mesh with the server's rules: the same search boxes, the same filter flags, the
/// same decision of what is a whole, partial or failed path. Pure functions over a <see cref="NavMapView"/>; call inside a
/// <see cref="ReferenceNavMap.Read{T}"/> callback.
/// </summary>
public static class NavQuery
{
    /// <summary>Size of DotRecast's corridor buffer (polygons). The server's corridor is bounded by its 2048-node search.</summary>
    public const int MaxCorridorPolys = 4096;

    private sealed class CountingQuery(DtNavMesh mesh) : DtNavMeshQuery(mesh)
    {
        public int NodeCount => m_nodePool.GetNodeCount();
    }

    /// <summary>
    /// The path from <paramref name="start"/> to <paramref name="end"/> (world coordinates) on the DotRecast mesh, deciding the result
    /// type exactly as <c>NavMeshPathfinder.FindPath</c> does for a non-flying mover without tile loading.
    /// </summary>
    public static NavPathAnswer FindPath(NavMapView view, Vector3 start, Vector3 end, PathOptions options, bool allowOffMesh = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);
        DtNavMesh mesh = view.Mesh;
        RcVec3f startRc = NavGeometry.ToRecast(start);
        RcVec3f endRc = NavGeometry.ToRecast(end);

        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshPathfinder.cs:1101-1110 and vmangos@0e3ff01 src/game/Maps/PathFinder.cpp:99-105, 695-706 (HaveTiles:
        // calcTileLoc then getTileAt(tx, ty, 0)): an end on a tile that is not loaded is a straight line typed Normal | NotUsingPath.
        if (!HaveTileAt(mesh, startRc) || !HaveTileAt(mesh, endRc))
        {
            return Straight(start, end, PathType.Normal | PathType.NotUsingPath, "tile-not-loaded", null, null);
        }

        var filter = new NavQueryFilter(options.EffectiveIncludeFlags, options.ExcludeFlags, allowOffMesh);
        var query = new CountingQuery(mesh);
        NavLocate? startLocated = Locate(view, query, filter, startRc, 0f, false);
        NavLocate? endLocated = Locate(view, query, filter, endRc, options.EndTolerance, options.WideEndSearch);
        if (startLocated is null || endLocated is null)
        {
            // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshPathfinder.cs:1126-1131 and vmangos@0e3ff01 src/game/Maps/PathFinder.cpp:191-198: an end not on the usable mesh is NoPath.
            return Straight(start, end, PathType.NoPath, "locate-failed", startLocated, endLocated);
        }

        long[] corridor = new long[MaxCorridorPolys];
        DtStatus search = query.FindPath(startLocated.Ref, endLocated.Ref, startLocated.Point, endLocated.Point, filter, corridor, out int polyCount, corridor.Length);
        if (search.Failed() || polyCount == 0)
        {
            return Straight(start, end, PathType.NoPath, "search-failed", startLocated, endLocated, search.Value, query.NodeCount);
        }

        bool complete = corridor[polyCount - 1] == endLocated.Ref;
        if (!complete && !options.AllowPartial)
        {
            return Straight(start, end, PathType.NoPath, "partial-refused", startLocated, endLocated, search.Value, query.NodeCount);
        }

        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshQuery.cs:332-376 (FindWorldPath): the goal of an incomplete corridor is the
        // closest point of its last polygon to the end; the corners are Detour's straight path with options 0 (vmangos@0e3ff01 src/game/Maps/PathFinder.cpp:406-415
        // passes no options); the first point is the caller's start, and a one-corner path gets the goal appended.
        RcVec3f goal = endLocated.Point;
        if (!complete)
        {
            query.ClosestPointOnPoly(corridor[polyCount - 1], endRc, out goal, out _);
        }

        int limit = Math.Max(2, options.MaxPoints) + 1;
        var straight = new DtStraightPath[limit];
        DtStatus straightStatus = query.FindStraightPath(startLocated.Point, goal, corridor, polyCount, straight, out int cornerCount, limit, 0);
        if (straightStatus.Failed() || cornerCount == 0)
        {
            return Straight(start, end, PathType.NoPath, "straight-path-failed", startLocated, endLocated, search.Value, query.NodeCount);
        }

        var points = new List<Vector3>(cornerCount + 1) { start };
        var flags = new List<byte>(cornerCount + 1) { straight[0].flags };
        for (int i = 1; i < cornerCount; i++)
        {
            points.Add(ToVector(straight[i].pos));
            flags.Add(straight[i].flags);
        }

        if (points.Count == 1)
        {
            points.Add(ToVector(goal));
            flags.Add(DtStraightPathFlags.DT_STRAIGHTPATH_END);
        }

        PathType type = complete && !endLocated.Far ? PathType.Normal : PathType.Incomplete;
        int max = Math.Max(2, options.MaxPoints);
        if (points.Count > max)
        {
            points.RemoveRange(max, points.Count - max);
            flags.RemoveRange(max, flags.Count - max);
            type |= PathType.Short;
        }

        var corridorIds = new List<NavPolyId>(polyCount);
        for (int i = 0; i < polyCount; i++)
        {
            corridorIds.Add(Describe(view, corridor[i]));
        }

        return new NavPathAnswer(new PathResult(type, points), "path", complete, endLocated.Far, startLocated, endLocated, corridorIds, flags, query.NodeCount, search.Value);
    }

    /// <summary>
    /// The polygon nearest to <paramref name="at"/> (world coordinates) within a search box. <see cref="NavSearchBox.Ladder"/> and the
    /// single boxes use the server's extents; a custom <paramref name="extents"/> (Recast axes: x = world Y, y = up, z = world X) replaces them.
    /// </summary>
    /// <returns>The polygon, or null when no polygon passing the filter lies in the box; <paramref name="tried"/> lists the boxes searched.</returns>
    public static NavLocate? Nearest(NavMapView view, Vector3 at, NavQueryFilter filter, NavSearchBox box, Vector3? extents, out IReadOnlyList<string> tried)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(filter);
        var query = new DtNavMeshQuery(view.Mesh);
        RcVec3f point = NavGeometry.ToRecast(at);
        var boxes = new List<string>();
        tried = boxes;
        if (extents is { } custom)
        {
            boxes.Add("custom");
            return TryBox(view, query, filter, point, new RcVec3f(custom.X, custom.Y, custom.Z), "custom", far: false);
        }

        switch (box)
        {
            case NavSearchBox.Near:
                boxes.Add("near");
                return TryBox(view, query, filter, point, Rc(NavMeshPathfinder.NearExtents), "near", far: false, nearRule: true);
            case NavSearchBox.Far:
                boxes.Add("far");
                return TryBox(view, query, filter, point, Rc(NavMeshPathfinder.FarExtents), "far", far: true);
            case NavSearchBox.Wide:
                boxes.Add("wide");
                return TryBox(view, query, filter, point, Rc(NavMeshPathfinder.WideExtents), "wide", far: true);
            case NavSearchBox.EndTolerance:
                boxes.Add("end-tolerance");
                return LocateWithTolerance(view, query, filter, point, PathOptions.VmangosEndTolerance);
            default:
                boxes.Add("near");
                NavLocate? found = TryBox(view, query, filter, point, Rc(NavMeshPathfinder.NearExtents), "near", far: false, nearRule: true);
                if (found is not null)
                {
                    return found;
                }

                boxes.Add("far");
                found = TryBox(view, query, filter, point, Rc(NavMeshPathfinder.FarExtents), "far", far: true);
                if (found is not null)
                {
                    return found;
                }

                boxes.Add("wide");
                return TryBox(view, query, filter, point, Rc(NavMeshPathfinder.WideExtents), "wide", far: true);
        }
    }

    /// <summary>
    /// Whether the Detour tile under a Recast position is in the mesh (vmangos <c>PathInfo::HaveTiles</c>). Positions that cannot be turned
    /// into a tile index (non-finite or enormous) have no tile.
    /// </summary>
    public static bool HaveTileAt(DtNavMesh mesh, RcVec3f recast)
    {
        // CalcTileLoc casts a float to int, which is unspecified for NaN and values past the int range; refuse them first.
        if (!float.IsFinite(recast.X) || !float.IsFinite(recast.Z) || MathF.Abs(recast.X) > 1.0e7f || MathF.Abs(recast.Z) > 1.0e7f)
        {
            return false;
        }

        mesh.CalcTileLoc(recast, out int tx, out int ty);
        return mesh.GetTileRefAt(tx, ty, 0) != 0;
    }

    private static NavLocate? Locate(NavMapView view, DtNavMeshQuery query, NavQueryFilter filter, RcVec3f point, float tolerance, bool wide)
    {
        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshPathfinder.cs:1171-1205 (TryLocate) and vmangos@0e3ff01 src/game/Maps/PathFinder.cpp:128-141 (FindWalkPoly) / :209 (farFromPoly).
        if (tolerance > 0)
        {
            return LocateWithTolerance(view, query, filter, point, tolerance);
        }

        NavLocate? found = TryBox(view, query, filter, point, Rc(NavMeshPathfinder.NearExtents), "near", far: false, nearRule: true);
        if (found is not null)
        {
            return found;
        }

        found = TryBox(view, query, filter, point, Rc(NavMeshPathfinder.FarExtents), "far", far: true);
        if (found is not null || !wide)
        {
            return found;
        }

        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshPathfinder.cs:1201-1204 (cmangos FarPolySearchBound, mangos-classic src/game/MotionGenerators/PathFinder.h:59).
        return TryBox(view, query, filter, point, Rc(NavMeshPathfinder.WideExtents), "wide", far: true);
    }

    private static NavLocate? LocateWithTolerance(NavMapView view, DtNavMeshQuery query, NavQueryFilter filter, RcVec3f point, float tolerance)
    {
        // vmangos FindWalkPoly: extents {5, 10, 5}; a polygon whose nearest point is more than 3 above the query point is refused.
        NavLocate? found = TryBox(view, query, filter, point, new RcVec3f(5, 10, 5), "end-tolerance", far: false);
        if (found is null || found.Point.Y > point.Y + 3.0f)
        {
            return null;
        }

        return WithFar(found, RcVec3f.DistanceSquared(found.Point, point) > tolerance * tolerance);
    }

    private static NavLocate WithFar(NavLocate found, bool far) => new()
    {
        Poly = found.Poly,
        Nearest = found.Nearest,
        OverPoly = found.OverPoly,
        Far = far,
        Box = found.Box,
        Ref = found.Ref,
        Point = found.Point,
    };

    private static NavLocate? TryBox(NavMapView view, DtNavMeshQuery query, NavQueryFilter filter, RcVec3f point, RcVec3f halfExtents, string name, bool far, bool nearRule = false)
    {
        DtStatus status = query.FindNearestPoly(point, halfExtents, filter, out long polyRef, out RcVec3f nearest, out bool over);
        if (status.Failed() || polyRef == 0)
        {
            return null;
        }

        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshPathfinder.cs:1189-1192 (near box): far when the nearest point is off the query point horizontally (0.1 yd) or more than
        // the box's height away.
        if (nearRule)
        {
            float dx = nearest.X - point.X;
            float dz = nearest.Z - point.Z;
            far = ((dx * dx) + (dz * dz)) > 0.01f || MathF.Abs(nearest.Y - point.Y) > NavMeshPathfinder.NearExtents.Y;
        }

        return new NavLocate
        {
            Poly = Describe(view, polyRef),
            Nearest = ToVector(nearest),
            OverPoly = over,
            Far = far,
            Box = name,
            Ref = polyRef,
            Point = nearest,
        };
    }

    /// <summary>Identifies the polygon behind a reference of the view's mesh.</summary>
    internal static NavPolyId Describe(NavMapView view, long polyRef)
    {
        if (view.Mesh.GetTileAndPolyByRef(polyRef, out DtMeshTile tile, out DtPoly poly).Failed())
        {
            throw new InvalidOperationException($"DotRecast returned the polygon reference 0x{polyRef:x}, which is not in the mesh.");
        }

        DtMeshHeader h = tile.data.header;
        WorldPoint center = NavGeometry.PolyCenter(tile.data.verts, poly.verts, poly.vertCount);
        return new NavPolyId(view.TerrainTileOf(tile), h.x, h.y, h.layer, poly.index, (ushort)poly.flags, poly.GetArea(),
            poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION, new Vector3((float)center.X, (float)center.Y, (float)center.Z));
    }

    private static NavPathAnswer Straight(Vector3 start, Vector3 end, PathType type, string outcome, NavLocate? s, NavLocate? e, uint status = 0, int nodes = 0)
        => new(PathResult.StraightLine(start, end, type), outcome, false, false, s, e, [], [], nodes, status);

    private static RcVec3f Rc(Vector3 v) => new(v.X, v.Y, v.Z);

    // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:50 (ToWorld(rc) = (Z, X, Y)).
    private static Vector3 ToVector(RcVec3f rc) => NavMeshFormat.ToWorld(new Vector3(rc.X, rc.Y, rc.Z));
}
