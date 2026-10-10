using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.MMaps;

/// <summary>A polygon of a loaded tile.</summary>
public readonly record struct NavPolyRef(NavMeshTile Tile, int Poly);

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

    public NavMeshTile? GetTile(int x, int y) => _tiles.GetValueOrDefault((x, y));

    /// <summary>
    /// While set (one query with <see cref="PathOptions.LoadTiles"/>), asked for a Detour tile a search crosses into that is not
    /// loaded; it loads the tile when it can (<see cref="NavMeshPathfinder"/>) and answers it, or null.
    /// </summary>
    internal Func<int, int, NavMeshTile?>? TileLoader { get; set; }

    /// <summary>The centre of Detour tile (<paramref name="x"/>, <paramref name="y"/>) in Recast space (y 0).</summary>
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
        => _byTerrainTile.Remove((tileX, tileY), out (int X, int Y) key) && _tiles.Remove(key);

    /// <summary>The nearest passable polygon within <paramref name="extents"/> of a Recast position (Detour <c>findNearestPoly</c>).</summary>
    public bool TryFindNearestPoly(Vector3 center, Vector3 extents, PathOptions options, out NavPolyRef poly, out Vector3 nearest)
    {
        poly = default;
        nearest = center;
        float best = float.MaxValue;
        foreach (NavMeshTile tile in _tiles.Values)
        {
            if (tile.Overlaps(center, extents)
                && tile.TryFindNearestPoly(center, extents, options.EffectiveIncludeFlags, options.ExcludeFlags, out int index, out Vector3 point, out float d)
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
    /// </summary>
    internal IEnumerable<(NavPolyRef To, Vector3 Left, Vector3 Right, byte CrossSide)> Neighbours(NavPolyRef from)
    {
        NavMeshTile tile = from.Tile;
        NavPoly p = tile.Polys[from.Poly];
        Vector3 center = tile.Center(from.Poly);
        for (int e = 0; e < p.VertexCount; e++)
        {
            ushort nei = p.Neighbours[e];
            Vector3 a = tile.Vertices[p.Vertices[e]];
            Vector3 b = tile.Vertices[p.Vertices[(e + 1) % p.VertexCount]];
            if (nei == 0)
            {
                continue;
            }

            if ((nei & NavMeshFormat.ExternalLink) == 0)
            {
                int index = nei - 1;
                if (index < tile.Polys.Length && index != from.Poly)
                {
                    (Vector3 l, Vector3 r) = Orient(center, a, b);
                    yield return (new NavPolyRef(tile, index), l, r, 0);
                }

                continue;
            }

            int side = nei & 0xff;
            if (NavMeshFormat.SideOffset(side) is not { } offset
                || (GetTile(tile.X + offset.Dx, tile.Y + offset.Dy) ?? TileLoader?.Invoke(tile.X + offset.Dx, tile.Y + offset.Dy)) is not { } other)
            {
                continue;
            }

            bool alongZ = side is 0 or 4; // the border is a line of constant x; edges run along z
            float climb = MathF.Max(MathF.Max(tile.WalkableClimb, other.WalkableClimb), 0.5f);
            foreach (BorderEdge edge in other.GetBorderEdges((side + 4) & 7))
            {
                if (TryOverlap(a, b, edge.A, edge.B, alongZ, climb, out Vector3 pa, out Vector3 pb))
                {
                    (Vector3 l, Vector3 r) = Orient(center, pa, pb);
                    yield return (new NavPolyRef(other, edge.Poly), l, r, (byte)(side >> 1));
                }
            }
        }
    }

    /// <summary>2D cross product in the x/z plane (positive: <paramref name="b"/> is counter-clockwise of <paramref name="a"/>).</summary>
    internal static float Cross(Vector3 apex, Vector3 a, Vector3 b)
        => ((a.X - apex.X) * (b.Z - apex.Z)) - ((a.Z - apex.Z) * (b.X - apex.X));

    /// <summary>Order a portal so that, seen from <paramref name="inside"/>, the right point is clockwise of the left.</summary>
    private static (Vector3 Left, Vector3 Right) Orient(Vector3 inside, Vector3 a, Vector3 b)
        => Cross(inside, a, b) < 0 ? (a, b) : (b, a);

    private static bool TryOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool alongZ, float climb, out Vector3 pa, out Vector3 pb)
    {
        pa = pb = default;
        float fixedA = alongZ ? a.X : a.Z;
        float fixedC = alongZ ? c.X : c.Z;
        if (MathF.Abs(fixedA - fixedC) > 0.1f || MathF.Abs((alongZ ? b.X : b.Z) - fixedA) > 0.1f)
        {
            return false;
        }

        float a0 = alongZ ? a.Z : a.X, a1 = alongZ ? b.Z : b.X, c0 = alongZ ? c.Z : c.X, c1 = alongZ ? d.Z : d.X;
        float low = MathF.Max(MathF.Min(a0, a1), MathF.Min(c0, c1));
        float high = MathF.Min(MathF.Max(a0, a1), MathF.Max(c0, c1));
        if (high - low < 0.01f)
        {
            return false;
        }

        pa = PointAt(a, b, alongZ, low);
        pb = PointAt(a, b, alongZ, high);
        float mid = (low + high) * 0.5f;
        return MathF.Abs(PointAt(a, b, alongZ, mid).Y - PointAt(c, d, alongZ, mid).Y) <= climb;
    }

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
        var nodes = new List<Node> { new(start, -1, startPos, 0, Vector3.Distance(startPos, endPos) * HeuristicScale, default, default) };
        var index = new Dictionary<(NavPolyRef, byte), int> { [(start, 0)] = 0 };
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(0, nodes[0].Total);
        int best = 0;
        float bestHeuristic = nodes[0].Total;
        int goal = start == end ? 0 : -1;

        while (goal < 0 && open.TryDequeue(out int current, out float priority))
        {
            // A node re-queued at a lower total leaves its older, dearer entry behind.
            if (closed.Contains(current) || priority > nodes[current].Total)
            {
                continue;
            }

            closed.Add(current);
            Node node = nodes[current];
            if (node.Poly == end)
            {
                goal = current;
                break;
            }

            NavPolyRef? parent = node.Parent >= 0 ? nodes[node.Parent].Poly : null;
            foreach ((NavPolyRef next, Vector3 left, Vector3 right, byte crossSide) in mesh.Neighbours(node.Poly))
            {
                if (next == parent || !next.Tile.Passes(next.Poly, options.EffectiveIncludeFlags, options.ExcludeFlags))
                {
                    continue;
                }

                bool known = index.TryGetValue((next, crossSide), out int existing);
                if (!known && nodes.Count >= options.MaxSearchNodes)
                {
                    continue;
                }

                // Detour fixes a node's position at the portal it was first reached through.
                Vector3 position = known ? nodes[existing].Position : (left + right) * 0.5f;
                float cost = node.Cost + Vector3.Distance(node.Position, position);
                float heuristic;
                if (next == end)
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
                    if (total >= nodes[existing].Total)
                    {
                        continue;
                    }

                    closed.Remove(existing); // Detour reopens a closed node a cheaper route reaches.
                    nodes[existing] = new Node(next, current, position, cost, total, left, right);
                    open.Enqueue(existing, total);
                }
                else
                {
                    existing = nodes.Count;
                    index[(next, crossSide)] = existing;
                    nodes.Add(new Node(next, current, position, cost, total, left, right));
                    open.Enqueue(existing, total);
                }

                if (heuristic < bestHeuristic)
                {
                    bestHeuristic = heuristic;
                    best = existing;
                }
            }
        }

        bool complete = goal >= 0;
        var corridor = new List<(NavPolyRef, Vector3, Vector3)>();
        // A reopened node can leave a stale parent chain; never walk more links than there are nodes.
        for (int i = complete ? goal : best; i >= 0 && corridor.Count < nodes.Count; i = nodes[i].Parent)
        {
            corridor.Add((nodes[i].Poly, nodes[i].Left, nodes[i].Right));
        }

        corridor.Reverse();
        return (corridor, complete);
    }

    /// <summary>
    /// Funnel string-pulling (the "simple stupid funnel algorithm"): the corners where the
    /// straight path between the corridor's portals must turn, from start to end inclusive.
    /// </summary>
    public static List<Vector3> StringPull(Vector3 start, IReadOnlyList<(Vector3 Left, Vector3 Right)> portals, Vector3 end)
    {
        ArgumentNullException.ThrowIfNull(portals);
        var all = new List<(Vector3 Left, Vector3 Right)>(portals.Count + 1);
        all.AddRange(portals);
        all.Add((end, end));
        var points = new List<Vector3> { start };
        Vector3 apex = start, left = start, right = start;
        int leftIndex = -1, rightIndex = -1;
        for (int i = 0; i < all.Count; i++)
        {
            (Vector3 nl, Vector3 nr) = all[i];

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

    private static void AddCorner(List<Vector3> points, Vector3 point)
    {
        if (Vector3.DistanceSquared(points[^1], point) > 1e-6f)
        {
            points.Add(point);
        }
    }

    private readonly record struct Node(NavPolyRef Poly, int Parent, Vector3 Position, float Cost, float Total, Vector3 Left, Vector3 Right);
}
