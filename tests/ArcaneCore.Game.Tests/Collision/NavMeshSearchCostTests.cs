using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The bot path search on the real Elwynn/Westfall navmesh. The 2026-10-11 bot benchmark (200 bots, real terrain and content)
/// traced the bot feature's 300-400 ms ticks to <see cref="NavMeshQuery.FindCorridor"/>: per 60 s, about 4.9 GB of neighbour
/// iterators, 3.2 GB of node arrays and 2 GB of node-index entries. This measures a fixed, seeded set of queries (the bot search
/// budgets: 2 048 and 32 768 nodes) for wall time and world-thread allocation, and fingerprints every corridor so a change to the
/// search can be shown to return the same corridors.
/// </summary>
public sealed class NavMeshSearchCostTests(ITestOutputHelper output)
{
    private const uint EasternKingdoms = 0;

    internal static (NavMesh Mesh, List<(NavPolyRef Poly, Vector3 Pos)> Points) LoadElwynn(NavMeshPathfinder nav)
    {
        // Northshire to Sentinel Hill: the bot starting area of the live stress (terrain tiles around both).
        (int ax, int ay) = TerrainTile.TileOf(-8950f, -130f)!.Value;
        (int bx, int by) = TerrainTile.TileOf(-10630f, 1060f)!.Value;
        for (int x = Math.Min(ax, bx) - 1; x <= Math.Max(ax, bx) + 1; x++)
            for (int y = Math.Min(ay, by) - 1; y <= Math.Max(ay, by) + 1; y++)
                nav.LoadTile(EasternKingdoms, x, y);
        NavMesh mesh = nav.GetNavMesh(EasternKingdoms)!;
        var points = new List<(NavPolyRef, Vector3)>();
        for (int tx = 0; tx < 64; tx++)
            for (int ty = 0; ty < 64; ty++)
                if (mesh.GetTile(tx, ty) is { } tile)
                    for (int poly = 0; poly < tile.Polys.Length; poly++)
                        if (tile.Passes(poly, NavTerrain.Ground, 0))
                            points.Add((new NavPolyRef(tile, poly), tile.Center(poly)));
        return (mesh, points);
    }

    internal static List<(NavPolyRef Start, Vector3 StartPos, NavPolyRef End, Vector3 EndPos, int Budget)> Queries(
        List<(NavPolyRef Poly, Vector3 Pos)> points, int count)
    {
        var random = new Random(20261011);
        var queries = new List<(NavPolyRef, Vector3, NavPolyRef, Vector3, int)>(count);
        while (queries.Count < count)
        {
            (NavPolyRef a, Vector3 pa) = points[random.Next(points.Count)];
            (NavPolyRef b, Vector3 pb) = points[random.Next(points.Count)];
            float d = Vector3.Distance(pa, pb);
            if (d is < 50 or > 1500) continue;
            queries.Add((a, pa, b, pb, queries.Count % 2 == 0 ? PathOptions.DefaultMaxSearchNodes : 32_768));
        }

        return queries;
    }

    internal static string Fingerprint(List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> corridor, bool complete, StringBuilder into)
    {
        into.Append(complete ? 'C' : 'P');
        foreach ((NavPolyRef poly, Vector3 left, Vector3 right) in corridor)
            into.Append(poly.Tile.X).Append(',').Append(poly.Tile.Y).Append(':').Append(poly.Poly).Append('|')
                .Append(left.X.ToString("R")).Append(right.Z.ToString("R")).Append(';');
        into.Append('\n');
        return into.ToString();
    }

    [RealTerrainFact]
    public void MeasureBotSearchesOnElwynnAndWestfall()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!, "mmaps"));
        (NavMesh mesh, List<(NavPolyRef Poly, Vector3 Pos)> points) = LoadElwynn(nav);
        Assert.True(points.Count > 1000, $"only {points.Count} polygons loaded");
        var queries = Queries(points, 300);
        var options = new Dictionary<int, PathOptions>
        {
            [PathOptions.DefaultMaxSearchNodes] = new() { MaxSearchNodes = PathOptions.DefaultMaxSearchNodes, AllowPartial = true },
            [32_768] = new() { MaxSearchNodes = 32_768, AllowPartial = true },
        };

        // Warm up (JIT, tile border-edge caches), then measure two passes.
        foreach (var q in queries.Take(20)) NavMeshQuery.FindCorridor(mesh, q.Start, q.StartPos, q.End, q.EndPos, options[q.Budget]);
        var text = new StringBuilder();
        long allocated = 0;
        double worstMs = 0;
        var wall = Stopwatch.StartNew();
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var q in queries)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                (var corridor, bool complete) = NavMeshQuery.FindCorridor(mesh, q.Start, q.StartPos, q.End, q.EndPos, options[q.Budget]);
                worstMs = Math.Max(worstMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if (pass == 0) Fingerprint(corridor, complete, text);
            }
        }

        wall.Stop();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        output.WriteLine($"{queries.Count} queries x2 over {points.Count} polygons: {wall.Elapsed.TotalMilliseconds / (2 * queries.Count):F2} ms/query mean, "
            + $"worst {worstMs:F1} ms, {allocated / 1024.0 / (2 * queries.Count):F1} KiB allocated/query; corridor fingerprint {hash}");
        // Before the reused search state: 3 824 KiB a query on this set (same fingerprint F06FE607385E10D1). Now only the returned
        // corridor list (and a rare growth of the per-thread buffers) is allocated.
        Assert.True(allocated / (2 * queries.Count) < 64 * 1024, $"{allocated / (2 * queries.Count)} bytes allocated per query");
    }

    /// <summary>
    /// The search on reused state returns exactly the corridors of the former one (fresh List/Dictionary/HashSet/PriorityQueue per
    /// query, kept below as the oracle), for 300 seeded queries at both bot budgets.
    /// </summary>
    [RealTerrainFact]
    public void ReusedSearchStateReturnsTheCorridorsOfAFreshSearch()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!, "mmaps"));
        (NavMesh mesh, List<(NavPolyRef Poly, Vector3 Pos)> points) = LoadElwynn(nav);
        foreach (var q in Queries(points, 300))
        {
            var options = new PathOptions { MaxSearchNodes = q.Budget, AllowPartial = true };
            (var corridor, bool complete) = NavMeshQuery.FindCorridor(mesh, q.Start, q.StartPos, q.End, q.EndPos, options);
            (var expected, bool expectedComplete) = FreshSearch(mesh, q.Start, q.StartPos, q.End, q.EndPos, options);
            Assert.Equal(expectedComplete, complete);
            Assert.Equal(expected, corridor);
        }
    }

    private readonly record struct Node(NavPolyRef Poly, int Parent, Vector3 Position, float Cost, float Total, Vector3 Left, Vector3 Right);

    /// <summary>NavMeshQuery.FindCorridor as it was before 2026-10-11 (fresh collections per query), line for line.</summary>
    private static (List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)> Corridor, bool Complete) FreshSearch(
        NavMesh mesh, NavPolyRef start, Vector3 startPos, NavPolyRef end, Vector3 endPos, PathOptions options)
    {
        const float HeuristicScale = 0.999f;
        var nodes = new List<Node> { new(start, -1, startPos, 0, Vector3.Distance(startPos, endPos) * HeuristicScale, default, default) };
        var index = new Dictionary<(NavPolyRef, byte), int> { [(start, 0)] = 0 };
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, float>();
        var neighbours = new List<NavNeighbour>();
        open.Enqueue(0, nodes[0].Total);
        int best = 0;
        float bestHeuristic = nodes[0].Total;
        int goal = start == end ? 0 : -1;
        while (goal < 0 && open.TryDequeue(out int current, out float priority))
        {
            if (closed.Contains(current) || priority > nodes[current].Total) continue;
            closed.Add(current);
            Node node = nodes[current];
            if (node.Poly == end) { goal = current; break; }
            NavPolyRef? parent = node.Parent >= 0 ? nodes[node.Parent].Poly : null;
            mesh.CollectNeighbours(node.Poly, neighbours);
            foreach ((NavPolyRef next, Vector3 left, Vector3 right, byte crossSide) in neighbours)
            {
                if (next == parent || !next.Tile.Passes(next.Poly, options.EffectiveIncludeFlags, options.ExcludeFlags)) continue;
                bool known = index.TryGetValue((next, crossSide), out int existing);
                if (!known && nodes.Count >= options.MaxSearchNodes) continue;
                Vector3 position = known ? nodes[existing].Position : (left + right) * 0.5f;
                float cost = node.Cost + Vector3.Distance(node.Position, position);
                float heuristic;
                if (next == end) { cost += Vector3.Distance(position, endPos); heuristic = 0; }
                else heuristic = Vector3.Distance(position, endPos) * HeuristicScale;
                float total = cost + heuristic;
                if (known)
                {
                    if (total >= nodes[existing].Total) continue;
                    closed.Remove(existing);
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

                if (heuristic < bestHeuristic) { bestHeuristic = heuristic; best = existing; }
            }
        }

        bool complete = goal >= 0;
        var corridor = new List<(NavPolyRef, Vector3, Vector3)>();
        for (int i = complete ? goal : best; i >= 0 && corridor.Count < nodes.Count; i = nodes[i].Parent)
            corridor.Add((nodes[i].Poly, nodes[i].Left, nodes[i].Right));
        corridor.Reverse();
        return (corridor, complete);
    }
}
