using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>A polygon of a loaded tile.</summary>
public readonly record struct NavPolyRef(NavMeshTile Tile, int Poly);

/// <summary>A polygon reachable across an edge, with the portal (left/right seen from inside the source) and Detour's cross side.</summary>
internal readonly record struct NavNeighbour(NavPolyRef To, Vector3 Left, Vector3 Right, byte CrossSide);

/// <summary>
/// The navmesh of one map: its parameters and loaded tiles, keyed by Detour tile coordinates.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class NavMesh(uint mapId, NavMeshParams parameters)
{
    private readonly Dictionary<(int X, int Y), NavMeshTile> _tiles = [];
    private readonly Dictionary<(int X, int Y), (int X, int Y)> _byTerrainTile = [];

    public uint MapId { get; } = mapId;

    public NavMeshParams Parameters { get; } = parameters;

    public int TileCount => _tiles.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NavMeshTile? GetTile(int x, int y) => _tiles.GetValueOrDefault((x, y));

    /// <summary>
    /// While set (one query with <see cref="PathOptions.LoadTiles"/>), asked for a Detour tile a search crosses into that is not
    /// loaded; it loads the tile when it can (<see cref="NavMeshPathfinder"/>) and answers it, or null.
    /// </summary>
    internal Func<int, int, NavMeshTile?>? TileLoader
    {
        get => _tileLoader;
        set
        {
            _tileLoaderThread = value is null ? 0 : Environment.CurrentManagedThreadId;
            _tileLoader = value;
        }
    }

    private Func<int, int, NavMeshTile?>? _tileLoader;
    private int _tileLoaderThread;

    /// <summary>
    /// <see cref="TileLoader"/> for the thread that set it, else null: a shared search (<see cref="NavMeshPathfinder.FindPathShared"/>) on
    /// another thread crossing into an unloaded tile treats it as unloaded, as its own options ask, and never runs the owner's loader.
    /// </summary>
    internal Func<int, int, NavMeshTile?>? TileLoaderForCurrentThread
        => _tileLoader is { } loader && _tileLoaderThread == Environment.CurrentManagedThreadId ? loader : null;

    [ThreadStatic] private static List<(int X, int Y)>? t_missedTiles;

    /// <summary>The most distinct missed tiles one recorded search keeps.</summary>
    internal const int MaxMissedTiles = 64;

    /// <summary>
    /// Sets the list this thread's searches add each Detour tile they wanted and found unloaded to (null: record nothing). Only
    /// <see cref="NavMeshPathfinder"/>'s shared search with a missed-tile list sets it, around one query. Returns the list that was set.
    /// </summary>
    internal static List<(int X, int Y)>? MissedTileSink
    {
        get => t_missedTiles;
        set => t_missedTiles = value;
    }

    private static void NoteMissedTile(int x, int y)
    {
        if (t_missedTiles is not { } list || list.Count >= MaxMissedTiles) return;
        foreach ((int X, int Y) known in list)
            if (known.X == x && known.Y == y) return;
        list.Add((x, y));
    }

    /// <summary>Test seam: the clock of the search wall-time bound (<see cref="PathOptions.MaxSearchMs"/>), in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    internal static Func<long> DeadlineClock { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

    /// <summary>The centre of Detour tile (<paramref name="x"/>, <paramref name="y"/>) in Recast space (y 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector3 TileCenter(int x, int y)
        => new(Parameters.Origin.X + ((x + 0.5f) * Parameters.TileWidth), 0, Parameters.Origin.Z + ((y + 0.5f) * Parameters.TileHeight));

    /// <summary>
    /// Whether the Detour tile under a Recast position is loaded (vmangos <c>PathInfo::HaveTiles</c>:
    /// <c>calcTileLoc</c> then <c>getTileAt</c>, PathFinder.cpp:695-706).
    /// </summary>
    public bool HaveTileAt(Vector3 recast)
    {
        float tx = MathF.Floor((recast.X - Parameters.Origin.X) / Parameters.TileWidth);
        float ty = MathF.Floor((recast.Z - Parameters.Origin.Z) / Parameters.TileHeight);
        return tx is > int.MinValue and < int.MaxValue && ty is > int.MinValue and < int.MaxValue && GetTile((int)tx, (int)ty) is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTerrainTileLoaded(int tileX, int tileY) => _byTerrainTile.ContainsKey((tileX, tileY));

    /// <summary>Add a tile read for a terrain tile; false when its Detour slot is taken (Detour <c>addTile</c> refuses too).</summary>
    public bool AddTile(int tileX, int tileY, NavMeshTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (_byTerrainTile.ContainsKey((tileX, tileY)) || !_tiles.TryAdd((tile.X, tile.Y), tile))
        {
            return false;
        }

        _byTerrainTile[(tileX, tileY)] = (tile.X, tile.Y);
        return true;
    }

    public bool RemoveTile(int tileX, int tileY)
    {
        if (!_byTerrainTile.Remove((tileX, tileY), out (int X, int Y) key) || !_tiles.Remove(key, out NavMeshTile? removed))
        {
            return false;
        }

        // The neighbours' cross-tile portal tables name the removed tile; drop them so it can be collected.
        foreach (int side in (ReadOnlySpan<int>)[0, 2, 4, 6])
        {
            if (NavMeshFormat.SideOffset(side) is { } offset)
            {
                GetTile(removed.X + offset.Dx, removed.Y + offset.Dy)?.DropCrossPortalsTo(removed);
            }
        }

        return true;
    }

    /// <summary>The nearest passable polygon within <paramref name="extents"/> of a Recast position (Detour <c>findNearestPoly</c>).</summary>
    public bool TryFindNearestPoly(Vector3 center, Vector3 extents, PathOptions options, out NavPolyRef poly, out Vector3 nearest)
        => TryFindNearestPoly(center, extents, options.EffectiveIncludeFlags, options.ExcludeFlags, out poly, out nearest);

    /// <summary>The nearest polygon within <paramref name="extents"/> of a Recast position that passes the given include and exclude flags.</summary>
    public bool TryFindNearestPoly(Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, out NavPolyRef poly, out Vector3 nearest)
    {
        poly = default;
        nearest = center;
        float best = float.MaxValue;
        foreach ((_, NavMeshTile tile) in _tiles)
        {
            if (tile.Overlaps(center, extents)
                && tile.TryFindNearestPoly(center, extents, include, exclude, out int index, out Vector3 point, out float d)
                && d < best)
            {
                best = d;
                poly = new NavPolyRef(tile, index);
                nearest = point;
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>
    /// The polygons reachable across each edge of <paramref name="from"/> with the portal segment
    /// (left/right as seen from inside <paramref name="from"/>): internal neighbours share the edge;
    /// border edges join the adjacent tile's edges on the opposite side where they overlap.
    /// <c>CrossSide</c> is Detour's <c>link.side &gt;&gt; 1</c> for a tile-border link and 0 inside the tile.
    /// <para>
    /// Appends to <paramref name="into"/> (cleared first) in the order a lazy enumeration would yield: the A* search expands tens
    /// of thousands of polygons per bot query, and an iterator per expansion was the largest allocation of the live bot tick
    /// (docs/integration/tick-perf-20261011.md).
    /// </para>
    /// </summary>
    internal void CollectNeighbours(NavPolyRef from, List<NavNeighbour> into)
    {
        into.Clear();
        NavMeshTile tile = from.Tile;
        int fromPoly = from.Poly;
        NavPoly p = tile.Polys[fromPoly];
        Vector3 center = tile.Center(fromPoly);
        Vector3[] vertices = tile.Vertices;
        int polyCount = tile.Polys.Length;
        int vertCount = p.VertexCount;
        for (int e = 0; e < vertCount; e++)
        {
            ushort nei = p.Neighbour(e);
            if (nei == 0)
            {
                continue; // a wall
            }

            if ((nei & NavMeshFormat.ExternalLink) == 0)
            {
                int index = nei - 1;
                if (index < polyCount && index != fromPoly)
                {
                    // The edge's vertices are read only for an internal neighbour (a wall or a tile-border link does not use them).
                    int nextEdge = e + 1 < vertCount ? e + 1 : 0;
                    Vector3 a = vertices[p.Vertex(e)];
                    Vector3 b = vertices[p.Vertex(nextEdge)];
                    (Vector3 l, Vector3 r) = Orient(center, a, b);
                    into.Add(new NavNeighbour(new NavPolyRef(tile, index), l, r, 0));
                }

                continue;
            }

            int side = nei & 0xff;
            if (NavMeshFormat.SideOffset(side) is not { } offset)
            {
                continue;
            }

            int otherX = tile.X + offset.Dx, otherY = tile.Y + offset.Dy;
            if ((GetTile(otherX, otherY) ?? TileLoaderForCurrentThread?.Invoke(otherX, otherY)) is not { } other)
            {
                NoteMissedTile(otherX, otherY);
                continue;
            }

            // The overlap of this edge with the neighbour's border edges is fixed by the two tiles' geometry; it is worked out once per
            // edge (NavMeshTile.GetCrossPortals) rather than by scanning every border edge of the neighbour per expansion.
            // Detour's connectExtLinks (DetourNavMesh.cpp:416-428) prepends each connecting polygon to poly->firstLink, so a border
            // edge's external links are walked in reverse candidate order.
            CrossPortal[] portals = tile.GetCrossPortals(fromPoly, e, other);
            for (int i = portals.Length - 1; i >= 0; i--)
            {
                ref readonly CrossPortal portal = ref portals[i];
                (Vector3 l, Vector3 r) = Orient(center, portal.A, portal.B);
                into.Add(new NavNeighbour(new NavPolyRef(other, portal.Poly), l, r, (byte)(side >> 1)));
            }
        }
    }

    /// <summary>
    /// The polygon across edge <paramref name="edge"/> of <paramref name="from"/> at the point <paramref name="crossing"/> on that edge,
    /// when the edge is not a wall and the polygon passes the filter. A tile-border edge joins a polygon of the adjacent tile only if that
    /// tile is loaded (nothing is read here). False: a wall, a refused polygon, an unloaded tile, or no polygon opposite the crossing.
    /// </summary>
    internal bool TryCrossEdge(NavPolyRef from, int edge, Vector3 crossing, PathOptions options, out NavPolyRef next)
    {
        next = default;
        NavMeshTile tile = from.Tile;
        NavPoly p = tile.Polys[from.Poly];
        ushort nei = p.Neighbour(edge);
        if (nei == 0)
        {
            return false;
        }

        NavTerrain include = options.EffectiveIncludeFlags;
        NavTerrain exclude = options.ExcludeFlags;
        if ((nei & NavMeshFormat.ExternalLink) == 0)
        {
            int index = nei - 1;
            if (index >= tile.Polys.Length || index == from.Poly || !tile.Passes(index, include, exclude))
            {
                return false;
            }

            next = new NavPolyRef(tile, index);
            return true;
        }

        int side = nei & 0xff;
        if (NavMeshFormat.SideOffset(side) is not { } offset || GetTile(tile.X + offset.Dx, tile.Y + offset.Dy) is not { } other)
        {
            return false;
        }

        bool alongZ = side is 0 or 4;
        float along = alongZ ? crossing.Z : crossing.X;
        CrossPortal[] portals = tile.GetCrossPortals(from.Poly, edge, other);
        for (int i = 0; i < portals.Length; i++)
        {
            ref readonly CrossPortal portal = ref portals[i];
            if (other.Passes(portal.Poly, include, exclude))
            {
                float low = MathF.Min(alongZ ? portal.A.Z : portal.A.X, alongZ ? portal.B.Z : portal.B.X) - 0.05f;
                float high = MathF.Max(alongZ ? portal.A.Z : portal.A.X, alongZ ? portal.B.Z : portal.B.X) + 0.05f;
                if (along >= low && along <= high)
                {
                    next = new NavPolyRef(other, portal.Poly);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>2D cross product in the x/z plane (positive: <paramref name="b"/> is counter-clockwise of <paramref name="a"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Cross(Vector3 apex, Vector3 a, Vector3 b)
        => ((a.X - apex.X) * (b.Z - apex.Z)) - ((a.Z - apex.Z) * (b.X - apex.X));

    /// <summary>Order a portal so that, seen from <paramref name="inside"/>, the right point is clockwise of the left.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector3 Left, Vector3 Right) Orient(Vector3 inside, Vector3 a, Vector3 b)
        => Cross(inside, a, b) < 0 ? (a, b) : (b, a);

    // ref: vmangos@0e3ff01/dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:31-112, 300-351 (calcSlabEndPoints, overlapSlabs, findConnectingPolys)
    internal static bool TryOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool alongZ, float climb, out Vector3 pa, out Vector3 pb)
    {
        pa = pb = default;
        float fixedA = alongZ ? a.X : a.Z;
        float fixedC = alongZ ? c.X : c.Z;
        if (MathF.Abs(fixedA - fixedC) > 0.01f || MathF.Abs((alongZ ? b.X : b.Z) - fixedA) > 0.01f)
        {
            return false;
        }

        float a0 = alongZ ? a.Z : a.X, a1 = alongZ ? b.Z : b.X;
        float c0 = alongZ ? c.Z : c.X, c1 = alongZ ? d.Z : d.X;
        float amin0, amin1, amax0, amax1;
        if (a0 < a1)
        {
            amin0 = a0;
            amin1 = a.Y;
            amax0 = a1;
            amax1 = b.Y;
        }
        else
        {
            amin0 = a1;
            amin1 = b.Y;
            amax0 = a0;
            amax1 = a.Y;
        }

        float bmin0, bmin1, bmax0, bmax1;
        if (c0 < c1)
        {
            bmin0 = c0;
            bmin1 = c.Y;
            bmax0 = c1;
            bmax1 = d.Y;
        }
        else
        {
            bmin0 = c1;
            bmin1 = d.Y;
            bmax0 = c0;
            bmax1 = c.Y;
        }

        const float px = 0.01f;
        float minx = MathF.Max(amin0 + px, bmin0 + px);
        float maxx = MathF.Min(amax0 - px, bmax0 - px);
        if (minx > maxx)
        {
            return false;
        }

        float ad = (amax1 - amin1) / (amax0 - amin0);
        float ak = amin1 - (ad * amin0);
        float bd = (bmax1 - bmin1) / (bmax0 - bmin0);
        float bk = bmin1 - (bd * bmin0);
        float aminy = (ad * minx) + ak;
        float amaxy = (ad * maxx) + ak;
        float bminy = (bd * minx) + bk;
        float bmaxy = (bd * maxx) + bk;
        float dmin = bminy - aminy;
        float dmax = bmaxy - amaxy;
        float thr = (climb * 2f) * (climb * 2f);
        if ((dmin * dmax) >= 0f && (dmin * dmin) > thr && (dmax * dmax) > thr)
        {
            return false;
        }

        float low = MathF.Max(amin0, bmin0);
        float high = MathF.Min(amax0, bmax0);
        pa = PointAt(a, b, alongZ, low);
        pb = PointAt(a, b, alongZ, high);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 PointAt(Vector3 a, Vector3 b, bool alongZ, float coordinate)
    {
        float a0 = alongZ ? a.Z : a.X;
        float a1 = alongZ ? b.Z : b.X;
        float t = MathF.Abs(a1 - a0) < 1e-6f ? 0 : (coordinate - a0) / (a1 - a0);
        return Vector3.Lerp(a, b, Math.Clamp(t, 0, 1));
    }
}

/// <summary>
/// Path queries over a <see cref="NavMesh"/>: A* over polygons (Detour <c>findPath</c> semantics:
/// costs between portal midpoints, include/exclude flag filter, node budget, partial result at
/// the node closest to the goal) and funnel string-pulling into corner points (Detour
/// <c>findStraightPath</c>). Written from the algorithm descriptions, not translated from Detour's
/// source; <see cref="FindCorridor"/>'s node rules are checked against Detour's behaviour.
/// All positions are Recast space.
/// </summary>
public static class NavMeshQuery
{
    /// <summary>The heuristic scale of Detour's <c>findPath</c> (<c>H_SCALE</c>).</summary>
    private const float HeuristicScale = 0.999f;

    /// <summary>
    /// Expansions per pooled node (<see cref="PathOptions.MaxSearchNodes"/>) after which a search stops. Detour's loop runs until its open
    /// list is empty, and a closed node is reopened whenever a cheaper route reaches it (portal-midpoint costs are not consistent), so
    /// the node pool alone does not bound the work of one search. A search that needs more than this many expansions per node is a
    /// reopen storm: it answers like a capped search (the node closest to the goal).
    /// </summary>
    public const int MaxExpansionsPerNode = 4;

    /// <summary>
    /// A* from <paramref name="start"/> to <paramref name="end"/>; returns the polygon corridor and whether it reaches the goal.
    /// <para>
    /// The search keeps the node semantics of the Detour <c>findPath</c> vmangos runs
    /// (dep/recastnavigation/Detour/Source/DetourNavMeshQuery.cpp, <c>dtNavMeshQuery::findPath</c>),
    /// which decide which of several near-equal corridors is returned: a node is a polygon plus the
    /// tile side it was entered across (<c>getNode(ref, crossSide)</c>), its position is the
    /// midpoint of the portal it was first reached through and is never moved, the goal node's
    /// heuristic is zero, the parent polygon is not expanded again, and a closed node is reopened
    /// when a cheaper route reaches it. The DotRecast oracle (tests, DotRecastOracleTests) agreed
    /// on 53 of 256 complete corridors before these rules and 254 of 256 after.
    /// </para>
    /// </summary>
    public static (List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> Corridor, bool Complete) FindCorridor(
        NavMesh mesh, NavPolyRef start, Vector3 startPos, NavPolyRef end, Vector3 endPos, PathOptions options)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        SearchScratch scratch = AcquireScratch(options);
        scratch.Begin();
        try
        {
            bool complete = Search(mesh, start, startPos, end, endPos, options, scratch);
            return ([.. scratch.Corridor], complete);
        }
        finally
        {
            scratch.End();
        }
    }

    /// <summary>
    /// Runs <see cref="FindCorridor"/> and <see cref="StringPull"/> inside the per-thread <see cref="SearchScratch"/> buffers and
    /// answers the resulting world-space waypoints (or <c>null</c> when the corridor is incomplete and partial paths are not allowed),
    /// avoiding per-query corridor, LINQ portal-slice, and Recast corner list allocations.
    /// </summary>
    internal static List<Vector3>? FindWorldPath(
        NavMesh mesh,
        Vector3 worldStart,
        NavPolyRef start,
        Vector3 startPos,
        NavPolyRef end,
        Vector3 endPos,
        Vector3 endRc,
        PathOptions options,
        out bool complete)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        SearchScratch scratch = AcquireScratch(options);
        scratch.Begin();
        try
        {
            complete = Search(mesh, start, startPos, end, endPos, options, scratch);
            if (!complete && !options.AllowPartial)
            {
                return null;
            }

            List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor = scratch.Corridor;
            Vector3 goal = complete ? endPos : corridor[^1].Poly.Tile.ClosestPointOnPoly(corridor[^1].Poly.Poly, endRc);
            List<Vector3> corners = scratch.Corners;
            StringPullCorridor(startPos, corridor, goal, corners);

            var points = new List<Vector3>(Math.Max(2, corners.Count)) { worldStart };
            for (int i = 1; i < corners.Count; i++)
            {
                points.Add(NavMeshFormat.ToWorld(corners[i]));
            }

            if (points.Count == 1)
            {
                points.Add(NavMeshFormat.ToWorld(goal));
            }

            return points;
        }
        finally
        {
            scratch.End();
        }
    }

    private static SearchScratch AcquireScratch(PathOptions options)
    {
        // The node list, index, closed flags, open queue and neighbour buffer are reused across queries on this thread (a bot query
        // grows them to up to 32 768 nodes); a nested query (a tile loader that paths) gets fresh ones.
        // A search with a large node pool (a far bot query) grows its index to tens of thousands of entries, and clearing a dictionary
        // clears its whole bucket array: small searches (creatures, 2048 nodes) kept paying for that on every query (Dictionary.Clear
        // was 2.5% of the live world thread, Oct 17). Small and large searches keep separate scratch.
        bool large = options.MaxSearchNodes > SmallSearchNodes;
        SearchScratch? cachedScratch = large ? t_largeScratch : t_scratch;
        SearchScratch scratch = cachedScratch is { InUse: false } cached ? cached : new SearchScratch();
        if (large)
        {
            t_largeScratch ??= scratch;
        }
        else
        {
            t_scratch ??= scratch;
        }

        return scratch;
    }

    [ThreadStatic]
    private static SearchScratch? t_scratch;

    [ThreadStatic]
    private static SearchScratch? t_largeScratch;

    /// <summary>Searches with at most this many nodes share the small per-thread scratch; larger ones use their own.</summary>
    internal const int SmallSearchNodes = 4096;

    /// <summary>
    /// Reusable A* state; <see cref="Begin"/> clears it (capacity is kept).
    /// <para>
    /// Nodes and closed flags are plain arrays read by reference and the index is keyed by a <c>long</c> (tile search id, polygon, cross
    /// side): live runs a Debug (unoptimized) build, where every <c>List&lt;Node&gt;</c> indexer copied a 64-byte node and every
    /// <c>(NavPolyRef, byte)</c> key hashed and compared through generated record-struct calls (Oct 17 sample: the search was 13% of
    /// the world thread, bot and creature A* together).
    /// </para>
    /// </summary>
    private sealed class SearchScratch
    {
        public Node[] Nodes = new Node[64];
        public bool[] Closed = new bool[64];
        public long[] Keys = new long[64];
        public int[] NextInBucket = new int[64];
        private int[] _buckets = new int[256];
        private int[] _bucketStamps = new int[256];
        private int _stamp;
        public int Count;
        public readonly PriorityQueue<int, float> Open = new();
        public readonly List<NavNeighbour> Neighbours = [];
        public readonly List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> Corridor = [];
        public readonly List<Vector3> Corners = [];
        public bool InUse;

        public void Begin()
        {
            InUse = true;
            Count = 0;
            AdvanceStamp();
            Open.Clear();
            Neighbours.Clear();
            Corridor.Clear();
            Corners.Clear();
        }

        public void End()
        {
            InUse = false;
            // Do not pin a far tile's polygons (NavPolyRef holds the tile) until the next query.
            Array.Clear(Nodes, 0, Count);
            Count = 0;
            Neighbours.Clear();
            Corridor.Clear();
            Corners.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AdvanceStamp()
        {
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_bucketStamps);
                _stamp = 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BucketOf(long key, int mask)
            => (int)(((ulong)key * 11400714819323198485UL) >> 32) & mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(long key, out int index)
        {
            int bucket = BucketOf(key, _buckets.Length - 1);
            if (_bucketStamps[bucket] == _stamp)
            {
                for (int i = _buckets[bucket]; i >= 0; i = NextInBucket[i])
                {
                    if (Keys[i] == key)
                    {
                        index = i;
                        return true;
                    }
                }
            }

            index = -1;
            return false;
        }

        /// <summary>Appends a node (open, not closed), indexes it under <paramref name="key"/>, and answers its index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Add(long key, in Node node)
        {
            int index = Count;
            if (index == Nodes.Length)
            {
                int newCap = index * 2;
                Array.Resize(ref Nodes, newCap);
                Array.Resize(ref Closed, newCap);
                Array.Resize(ref Keys, newCap);
                Array.Resize(ref NextInBucket, newCap);
            }

            if (index * 2 >= _buckets.Length)
            {
                GrowBuckets(index);
            }

            Nodes[index] = node;
            Closed[index] = false;
            Keys[index] = key;
            int bucket = BucketOf(key, _buckets.Length - 1);
            NextInBucket[index] = _bucketStamps[bucket] == _stamp ? _buckets[bucket] : -1;
            _buckets[bucket] = index;
            _bucketStamps[bucket] = _stamp;
            Count = index + 1;
            return index;
        }

        private void GrowBuckets(int count)
        {
            int newLen = _buckets.Length * 2;
            _buckets = new int[newLen];
            _bucketStamps = new int[newLen];
            if (++_stamp == int.MaxValue)
            {
                _stamp = 1;
            }

            int mask = newLen - 1;
            for (int i = 0; i < count; i++)
            {
                int bucket = BucketOf(Keys[i], mask);
                NextInBucket[i] = _bucketStamps[bucket] == _stamp ? _buckets[bucket] : -1;
                _buckets[bucket] = i;
                _bucketStamps[bucket] = _stamp;
            }
        }
    }

    /// <summary>The index key of a polygon reached across <paramref name="crossSide"/>: unique per tile object, like the former (NavPolyRef, byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long NodeKey(NavMeshTile tile, int poly, byte crossSide)
        => ((long)tile.SearchId << 32) | ((long)(uint)poly << 8) | crossSide;

    private static bool Search(
        NavMesh mesh, NavPolyRef start, Vector3 startPos, NavPolyRef end, Vector3 endPos, PathOptions options, SearchScratch scratch)
    {
        PriorityQueue<int, float> open = scratch.Open;
        List<NavNeighbour> neighbours = scratch.Neighbours;
        NavMeshTile endTile = end.Tile;
        int endPoly = end.Poly;
        int maxNodes = options.MaxSearchNodes;
        NavTerrain include = options.EffectiveIncludeFlags;
        NavTerrain exclude = options.ExcludeFlags;
        CancellationToken cancel = options.CancellationToken;
        bool canCancel = cancel.CanBeCanceled;
        scratch.Add(
            NodeKey(start.Tile, start.Poly, 0),
            new Node(start, -1, startPos, 0, Vector3.Distance(startPos, endPos) * HeuristicScale, default, default));
        open.Enqueue(0, scratch.Nodes[0].Total);
        int best = 0;
        float bestHeuristic = scratch.Nodes[0].Total;
        int goal = ReferenceEquals(start.Tile, endTile) && start.Poly == endPoly ? 0 : -1;
        int expansions = 0;
        bool capped = false;
        bool deadlined = false;
        Func<long> clock = NavMesh.DeadlineClock;
        long deadline = options.MaxSearchMs > 0
            ? clock() + (long)(options.MaxSearchMs * System.Diagnostics.Stopwatch.Frequency / 1000.0)
            : long.MaxValue;

        int maxExpansions = (int)Math.Min(int.MaxValue, (long)MaxExpansionsPerNode * Math.Max(1, maxNodes));
        while (goal < 0 && open.TryDequeue(out int current, out float priority))
        {
            if (expansions >= maxExpansions)
            {
                // The node pool bounds how many nodes exist, not how often a reopened one is expanded again: stop and answer the
                // node closest to the goal, as a search that ran out of nodes does.
                capped = true;
                break;
            }

            if (canCancel && (expansions & 31) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }

            if (deadline != long.MaxValue && (expansions & 31) == 0 && clock() >= deadline)
            {
                // Out of time rather than out of nodes: answer the node closest to the goal, as a capped search does.
                capped = true;
                deadlined = true;
                break;
            }

            // A node re-queued at a lower total leaves its older, dearer entry behind.
            if (scratch.Closed[current] || priority > scratch.Nodes[current].Total)
            {
                continue;
            }

            scratch.Closed[current] = true;
            expansions++;
            // Extract scalar fields before neighbour additions, since scratch.Nodes may grow and relocate.
            ref readonly Node currentNode = ref scratch.Nodes[current];
            NavPolyRef nodePolyRef = currentNode.Poly;
            NavMeshTile nodeTile = nodePolyRef.Tile;
            int nodePoly = nodePolyRef.Poly;
            if (ReferenceEquals(nodeTile, endTile) && nodePoly == endPoly)
            {
                goal = current;
                break;
            }

            int nodeParent = currentNode.Parent;
            Vector3 nodePos = currentNode.Position;
            float nodeCost = currentNode.Cost;
            NavMeshTile? parentTile = nodeParent >= 0 ? scratch.Nodes[nodeParent].Poly.Tile : null;
            int parentPoly = nodeParent >= 0 ? scratch.Nodes[nodeParent].Poly.Poly : -1;
            mesh.CollectNeighbours(nodePolyRef, neighbours);
            ReadOnlySpan<NavNeighbour> around = CollectionsMarshal.AsSpan(neighbours);
            for (int n = 0; n < around.Length; n++)
            {
                ref readonly NavNeighbour neighbour = ref around[n];
                NavMeshTile nextTile = neighbour.To.Tile;
                int nextPoly = neighbour.To.Poly;
                if ((ReferenceEquals(nextTile, parentTile) && nextPoly == parentPoly) || !nextTile.Passes(nextPoly, include, exclude))
                {
                    continue;
                }

                long key = NodeKey(nextTile, nextPoly, neighbour.CrossSide);
                bool known = scratch.TryGetIndex(key, out int existing);
                if (!known && scratch.Count >= maxNodes)
                {
                    capped = true;
                    continue;
                }

                // Detour fixes a node's position at the portal it was first reached through.
                Vector3 position = known ? scratch.Nodes[existing].Position : (neighbour.Left + neighbour.Right) * 0.5f;
                float cost = nodeCost + Vector3.Distance(nodePos, position);
                bool isGoal = ReferenceEquals(nextTile, endTile) && nextPoly == endPoly;
                if (known && !isGoal && cost >= scratch.Nodes[existing].Cost)
                {
                    continue;
                }

                float heuristic;
                if (isGoal)
                {
                    cost += Vector3.Distance(position, endPos);
                    heuristic = 0;
                }
                else
                {
                    heuristic = Vector3.Distance(position, endPos) * HeuristicScale;
                }

                float total = cost + heuristic;
                if (known)
                {
                    ref Node existingNode = ref scratch.Nodes[existing];
                    if (total >= existingNode.Total)
                    {
                        continue;
                    }

                    scratch.Closed[existing] = false; // Detour reopens a closed node a cheaper route reaches.
                    existingNode = new Node(neighbour.To, current, position, cost, total, neighbour.Left, neighbour.Right);
                    open.Enqueue(existing, total);
                }
                else
                {
                    existing = scratch.Add(key, new Node(neighbour.To, current, position, cost, total, neighbour.Left, neighbour.Right));
                    open.Enqueue(existing, total);
                }

                if (heuristic < bestHeuristic)
                {
                    bestHeuristic = heuristic;
                    best = existing;
                }
            }
        }

        cancel.ThrowIfCancellationRequested();
        bool complete = goal >= 0;
        int count = scratch.Count;
        PathSearchCounters.RecordSearch(count, expansions, capped);
        if (deadlined) PathSearchCounters.RecordDeadline();
        List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor = scratch.Corridor;
        corridor.Clear();
        Node[] nodes = scratch.Nodes;
        // A reopened node can leave a stale parent chain; never walk more links than there are nodes.
        for (int i = complete ? goal : best; i >= 0 && corridor.Count < count; i = nodes[i].Parent)
        {
            ref readonly Node n = ref nodes[i];
            corridor.Add((n.Poly, n.Left, n.Right));
        }

        corridor.Reverse();
        return complete;
    }

    /// <summary>The most polygons a random-point query gathers (a wander circle of a few yards touches a handful).</summary>
    public const int MaxRandomPointPolys = 256;

    /// <summary>The most rejected samples (a sampled point outside the circle) before the query answers the start point itself.</summary>
    private const int MaxRandomPointAttempts = 16;

    /// <summary>The most polygons a raycast crosses before it stops where it is.</summary>
    public const int MaxRaycastPolys = 512;

    [ThreadStatic]
    private static PointScratch? t_points;

    /// <summary>Reusable state of <see cref="TryRandomPointAround"/>; capacity is kept between queries.</summary>
    private sealed class PointScratch
    {
        public readonly List<NavPolyRef> Polys = [];
        public readonly HashSet<NavPolyRef> Seen = [];
        public readonly List<NavNeighbour> Neighbours = [];
        public bool InUse;
    }


    /// <summary>
    /// A random point on the mesh within <paramref name="radius"/> (horizontal) of <paramref name="center"/>, on a polygon reachable
    /// from <paramref name="start"/> (the contract of Detour <c>findRandomPointAroundCircle</c>, not its algorithm): the polygons
    /// reachable from the start across edges that touch the circle are gathered (loaded tiles only, at most 256), a point is drawn
    /// uniformly over the disc, and it counts when one of the gathered polygons contains it (the one nearest in height to
    /// <paramref name="center"/> when several do). A point over a hole, or over a polygon not connected within the circle, is drawn
    /// again (up to 16 times), after which the answer is the start position itself. Drawing over the disc, not over the polygons,
    /// keeps a small circle on large polygons spread across the disc. Exactly two values are drawn from <paramref name="random"/> per
    /// attempt, so a seed fixes the result. Uses per-thread scratch lists, so no collection is built per call. Recast space; returns
    /// false only for an unusable radius (nothing is drawn).
    /// </summary>
    public static bool TryRandomPointAround(NavMesh mesh, NavPolyRef start, Vector3 center, float radius, PathOptions options, Random random, out Vector3 point)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        point = center;
        if (!float.IsFinite(radius) || radius <= 0)
        {
            return false;
        }

        PointScratch scratch = t_points is { InUse: false } cached ? cached : new PointScratch();
        t_points ??= scratch;
        scratch.InUse = true;
        try
        {
            return RandomPointCore(mesh, start, center, radius, options, random, scratch, out point);
        }
        finally
        {
            scratch.InUse = false;
            scratch.Polys.Clear();
            scratch.Seen.Clear();
            scratch.Neighbours.Clear();
        }
    }

    private static bool RandomPointCore(NavMesh mesh, NavPolyRef start, Vector3 center, float radius, PathOptions options, Random random, PointScratch scratch, out Vector3 point)
    {
        List<NavPolyRef> polys = scratch.Polys;
        float radiusSquared = radius * radius;
        NavTerrain include = options.EffectiveIncludeFlags;
        NavTerrain exclude = options.ExcludeFlags;
        polys.Add(start);
        scratch.Seen.Add(start);
        for (int head = 0; head < polys.Count && polys.Count < MaxRandomPointPolys; head++)
        {
            mesh.CollectNeighbours(polys[head], scratch.Neighbours);
            ReadOnlySpan<NavNeighbour> neighbours = CollectionsMarshal.AsSpan(scratch.Neighbours);
            for (int n = 0; n < neighbours.Length; n++)
            {
                if (polys.Count >= MaxRandomPointPolys)
                {
                    break;
                }

                ref readonly NavNeighbour next = ref neighbours[n];
                if (!next.To.Tile.Passes(next.To.Poly, include, exclude)
                    || DistanceSegmentSquaredXz(center, next.Left, next.Right) > radiusSquared
                    || !scratch.Seen.Add(next.To))
                {
                    continue;
                }

                polys.Add(next.To);
            }
        }

        ReadOnlySpan<NavPolyRef> polySpan = CollectionsMarshal.AsSpan(polys);
        for (int attempt = 0; attempt < MaxRandomPointAttempts; attempt++)
        {
            double angle = random.NextDouble() * 2 * Math.PI;
            double distance = radius * Math.Sqrt(random.NextDouble());
            Vector3 candidate = new(center.X + (float)(distance * Math.Cos(angle)), center.Y, center.Z + (float)(distance * Math.Sin(angle)));
            float bestGap = float.MaxValue;
            float bestHeight = 0;
            for (int i = 0; i < polySpan.Length; i++)
            {
                NavPolyRef poly = polySpan[i];
                if (poly.Tile.TryHeightAtXz(poly.Poly, candidate, out float height) && MathF.Abs(height - center.Y) < bestGap)
                {
                    bestGap = MathF.Abs(height - center.Y);
                    bestHeight = height;
                }
            }

            if (bestGap < float.MaxValue)
            {
                point = candidate with { Y = bestHeight };
                return true;
            }
        }

        point = start.Tile.ClosestPointOnPoly(start.Poly, center);
        return true;
    }


    /// <summary>Squared horizontal distance from <paramref name="p"/> to the segment a–b.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DistanceSegmentSquaredXz(Vector3 p, Vector3 a, Vector3 b)
    {
        float t = NavMeshTile.ClosestT(a, b, p);
        return NavMeshTile.DistanceXzSquared(Vector3.Lerp(a, b, t), p);
    }

    /// <summary>The result of <c>Raycast</c> in Recast space.</summary>
    public readonly record struct RayResult(bool Hit, float Fraction, Vector3 Point);

    /// <summary>
    /// Walk the mesh surface from <paramref name="start"/> (a point on polygon <paramref name="startPoly"/>) toward <paramref name="end"/>,
    /// horizontally (Detour <c>raycast</c>): the segment leaves each polygon through the edge it crosses, and the walk goes on into the
    /// polygon beyond that edge (<c>NavMesh.TryCrossEdge</c>) until the end is inside the current polygon (fraction 1) or the edge
    /// is a wall, a polygon the filter refuses or a tile that is not loaded (a hit, at the edge). The point is on the mesh surface.
    /// Builds no collection per call (a test measures the allocation). Recast space.
    /// </summary>
    public static RayResult Raycast(NavMesh mesh, NavPolyRef startPoly, Vector3 start, Vector3 end, PathOptions options)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);
        float dx = end.X - start.X;
        float dz = end.Z - start.Z;
        if ((dx * dx) + (dz * dz) < 1e-10f)
        {
            return new RayResult(false, 1f, start);
        }

        NavPolyRef current = startPoly;
        float reached = 0f;
        for (int step = 0; step < MaxRaycastPolys; step++)
        {
            if (!ExitEdge(current, start, dx, dz, out float exitT, out int edge))
            {
                // Only a numerical edge case on a later polygon (the start is on the mesh): stop at the last edge crossed.
                return Stop(current, start, dx, dz, reached);
            }

            if (edge < 0)
            {
                return new RayResult(false, 1f, current.Tile.ClosestPointOnPoly(current.Poly, end));
            }

            reached = Math.Max(reached, exitT);
            Vector3 crossing = new(start.X + (dx * exitT), 0, start.Z + (dz * exitT));
            if (!mesh.TryCrossEdge(current, edge, crossing, options, out NavPolyRef next))
            {
                return Stop(current, start, dx, dz, reached);
            }

            current = next;
        }

        return Stop(current, start, dx, dz, reached);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RayResult Stop(NavPolyRef poly, Vector3 start, float dx, float dz, float t)
    {
        Vector3 at = new(start.X + (dx * t), start.Y, start.Z + (dz * t));
        return new RayResult(true, t, poly.Tile.ClosestPointOnPoly(poly.Poly, at));
    }

    /// <summary>
    /// Detour <c>dtIntersectSegmentPoly2D</c>: where the horizontal ray start + t·(dx, dz), t in [0, 1], leaves the polygon.
    /// <paramref name="edge"/> is the edge it leaves through, or -1 when the end is inside. False when the start is outside the polygon.
    /// </summary>
    private static bool ExitEdge(NavPolyRef poly, Vector3 start, float dx, float dz, out float exitT, out int edge)
    {
        NavMeshTile tile = poly.Tile;
        NavPoly p = tile.Polys[poly.Poly];
        Vector3[] vertices = tile.Vertices;
        int vertCount = p.VertexCount;
        float tMin = 0f;
        float tMax = 1f;
        edge = -1;
        exitT = 1f;
        for (int i = 0, j = vertCount - 1; i < vertCount; j = i++)
        {
            Vector3 vj = vertices[p.Vertex(j)];
            Vector3 vi = vertices[p.Vertex(i)];
            float ex = vi.X - vj.X;
            float ez = vi.Z - vj.Z;
            float n = (ez * (start.X - vj.X)) - (ex * (start.Z - vj.Z));
            float d = (dz * ex) - (dx * ez);
            if (MathF.Abs(d) < 1e-8f)
            {
                if (n < 0)
                {
                    return false;
                }

                continue;
            }

            float t = n / d;
            if (d < 0)
            {
                if (t > tMin)
                {
                    tMin = t;
                }

                if (tMin > tMax)
                {
                    return false;
                }
            }
            else
            {
                if (t < tMax)
                {
                    tMax = t;
                    edge = j;
                }

                if (tMax < tMin)
                {
                    return false;
                }
            }
        }

        exitT = tMax;
        return true;
    }

    /// <summary>
    /// Funnel string-pulling (the "simple stupid funnel algorithm"): the corners where the
    /// straight path between the corridor's portals must turn, from start to end inclusive.
    /// </summary>
    public static List<Vector3> StringPull(Vector3 start, IReadOnlyList<(Vector3 Left, Vector3 Right)> portals, Vector3 end)
    {
        ArgumentNullException.ThrowIfNull(portals);
        int portalCount = portals.Count;
        var points = new List<Vector3>(portalCount + 2) { start };
        Vector3 apex = start, left = start, right = start;
        int leftIndex = -1, rightIndex = -1;
        for (int i = 0; i <= portalCount; i++)
        {
            (Vector3 nl, Vector3 nr) = i < portalCount ? portals[i] : (end, end);

            // Right side: tighten when the new right point moves inwards (counter-clockwise).
            if (NavMesh.Cross(apex, right, nr) >= 0)
            {
                if (apex == right || NavMesh.Cross(apex, left, nr) < 0)
                {
                    right = nr;
                    rightIndex = i;
                }
                else
                {
                    AddCorner(points, left);
                    apex = right = left;
                    rightIndex = leftIndex;
                    i = leftIndex;
                    continue;
                }
            }

            // Left side: tighten when the new left point moves inwards (clockwise).
            if (NavMesh.Cross(apex, left, nl) <= 0)
            {
                if (apex == left || NavMesh.Cross(apex, right, nl) > 0)
                {
                    left = nl;
                    leftIndex = i;
                }
                else
                {
                    AddCorner(points, right);
                    apex = left = right;
                    leftIndex = rightIndex;
                    i = rightIndex;
                    continue;
                }
            }
        }

        AddCorner(points, end);
        return points;
    }

    private static void StringPullCorridor(
        Vector3 start,
        List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor,
        Vector3 end,
        List<Vector3> points)
    {
        points.Clear();
        points.Add(start);
        Vector3 apex = start, left = start, right = start;
        int leftIndex = -1, rightIndex = -1;
        ReadOnlySpan<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridorSpan = CollectionsMarshal.AsSpan(corridor);
        int portalCount = Math.Max(0, corridorSpan.Length - 1);
        for (int i = 0; i <= portalCount; i++)
        {
            Vector3 nl;
            Vector3 nr;
            if (i < portalCount)
            {
                ref readonly (NavPolyRef Poly, Vector3 Left, Vector3 Right) entry = ref corridorSpan[i + 1];
                nl = entry.Left;
                nr = entry.Right;
            }
            else
            {
                nl = end;
                nr = end;
            }

            // Right side: tighten when the new right point moves inwards (counter-clockwise).
            if (NavMesh.Cross(apex, right, nr) >= 0)
            {
                if (apex == right || NavMesh.Cross(apex, left, nr) < 0)
                {
                    right = nr;
                    rightIndex = i;
                }
                else
                {
                    AddCorner(points, left);
                    apex = right = left;
                    rightIndex = leftIndex;
                    i = leftIndex;
                    continue;
                }
            }

            // Left side: tighten when the new left point moves inwards (clockwise).
            if (NavMesh.Cross(apex, left, nl) <= 0)
            {
                if (apex == left || NavMesh.Cross(apex, right, nl) > 0)
                {
                    left = nl;
                    leftIndex = i;
                }
                else
                {
                    AddCorner(points, right);
                    apex = left = right;
                    leftIndex = rightIndex;
                    i = rightIndex;
                    continue;
                }
            }
        }

        AddCorner(points, end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddCorner(List<Vector3> points, Vector3 point)
    {
        if (Vector3.DistanceSquared(points[^1], point) > 1e-6f)
        {
            points.Add(point);
        }
    }

    private readonly record struct Node(NavPolyRef Poly, int Parent, Vector3 Position, float Cost, float Total, Vector3 Left, Vector3 Right);
}
