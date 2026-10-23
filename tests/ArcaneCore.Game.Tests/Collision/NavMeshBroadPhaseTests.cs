using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The two navmesh speed-ups must change no answer: the BV-tree assisted nearest-polygon lookup equals the plain scan of every
/// polygon box (kept as <see cref="NavMeshTile.TryFindNearestPolyLinear"/>), and the cached cross-tile portals equal what the
/// <see cref="NavMesh.TryOverlap"/> scan of the neighbour's border edges finds (the former per-expansion algorithm, restated here).
/// </summary>
public sealed class NavMeshBroadPhaseTests(ITestOutputHelper output)
{
    private const float QuantFactor = 2f;

    private static readonly NavMeshParams Params = new(Vector3.Zero, 533.3333f, 533.3333f, 1, 0);

    /// <summary>A bumpy 12 x 12 cell tile with holes and mixed flags, so polygon boxes differ in height and some are not walkable.</summary>
    private static CellTile Bumpy(int seed, int detourX = 0, int detourY = 0, float x0 = 0, float y0 = 0, int cells = 12, float size = 2, int[]? linked = null)
    {
        var rng = new Random(seed);
        bool[,] open = new bool[cells, cells];
        NavTerrain[,] flags = new NavTerrain[cells, cells];
        for (int i = 0; i < cells; i++)
        {
            for (int j = 0; j < cells; j++)
            {
                open[i, j] = rng.NextDouble() > 0.15;
                flags[i, j] = rng.Next(4) == 0 ? NavTerrain.Water : NavTerrain.Ground;
            }
        }

        double phase = rng.NextDouble() * 6;
        return new CellTile(detourX, detourY, x0, y0, cells, cells, size)
        {
            Walkable = (i, j) => open[i, j],
            Flags = (i, j) => flags[i, j],
            Height = (x, y) => (float)((3 * Math.Sin((x * 0.7) + phase)) + (2 * Math.Cos(y * 0.9)) + (0.3 * x)),
            LinkedSides = linked ?? [],
        };
    }

    /// <summary>
    /// <paramref name="tile"/> with a Detour-style BV tree appended (<c>createBVTree</c>: median split along the longest axis,
    /// escape indices). <paramref name="minShift"/> moves every leaf's low corner up by that many units (0: exact, 1: loose by a
    /// unit the query must absorb, large: a tree that would drop polygons).
    /// </summary>
    private static byte[] WithBvTree(byte[] tile, int minShift = 0, bool descending = false)
    {
        NavMeshTile plain = NavMeshTile.Parse(tile);
        var items = new List<(ushort[] Min, ushort[] Max, int Index)>();
        for (int i = 0; i < plain.Polys.Length; i++)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (ushort v in plain.Polys[i].Vertices)
            {
                lo = Vector3.Min(lo, plain.Vertices[v]);
                hi = Vector3.Max(hi, plain.Vertices[v]);
            }

            ushort[] min = new ushort[3], max = new ushort[3];
            for (int a = 0; a < 3; a++)
            {
                min[a] = (ushort)Math.Max(0, (int)MathF.Floor((Axis(lo, a) - Axis(plain.BoundLow, a)) * QuantFactor) + minShift);
                max[a] = (ushort)Math.Max(min[a], (int)MathF.Ceiling((Axis(hi, a) - Axis(plain.BoundLow, a)) * QuantFactor));
            }

            items.Add((min, max, i));
        }

        var nodes = new List<(ushort[] Min, ushort[] Max, int Index)>();
        Subdivide(items, 0, items.Count, nodes, descending);

        byte[] result = new byte[tile.Length + (nodes.Count * 16)];
        tile.CopyTo(result, 0);
        BitConverter.GetBytes(nodes.Count).CopyTo(result, 48);
        BitConverter.GetBytes(QuantFactor).CopyTo(result, 96);
        int at = tile.Length;
        foreach (var n in nodes)
        {
            foreach (ushort v in n.Min.Concat(n.Max))
            {
                BitConverter.GetBytes(v).CopyTo(result, at);
                at += 2;
            }

            BitConverter.GetBytes(n.Index).CopyTo(result, at);
            at += 4;
        }

        return result;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static void Subdivide(List<(ushort[] Min, ushort[] Max, int Index)> items, int from, int to, List<(ushort[] Min, ushort[] Max, int Index)> nodes, bool descending)
    {
        int slot = nodes.Count;
        nodes.Add(default);
        ushort[] min = [.. items.Skip(from).Take(to - from).Aggregate(new[] { 65535, 65535, 65535 }, (m, it) => [Math.Min(m[0], it.Min[0]), Math.Min(m[1], it.Min[1]), Math.Min(m[2], it.Min[2])]).Select(v => (ushort)v)];
        ushort[] max = [.. items.Skip(from).Take(to - from).Aggregate(new[] { 0, 0, 0 }, (m, it) => [Math.Max(m[0], it.Max[0]), Math.Max(m[1], it.Max[1]), Math.Max(m[2], it.Max[2])]).Select(v => (ushort)v)];
        if (to - from == 1)
        {
            nodes[slot] = (items[from].Min, items[from].Max, items[from].Index);
            return;
        }

        int longest = 0;
        for (int a = 1; a < 3; a++)
        {
            if (max[a] - min[a] > max[longest] - min[longest])
            {
                longest = a;
            }
        }

        items.Sort(from, to - from, Comparer<(ushort[] Min, ushort[] Max, int Index)>.Create((l, r) => descending ? r.Min[longest].CompareTo(l.Min[longest]) : l.Min[longest].CompareTo(r.Min[longest])));
        int split = from + ((to - from) / 2);
        Subdivide(items, from, split, nodes, descending);
        Subdivide(items, split, to, nodes, descending);
        nodes[slot] = (min, max, -(nodes.Count - slot));
    }

    private static void AssertSameNearest(NavMeshTile tile, Vector3 center, Vector3 extents, NavTerrain include, NavTerrain exclude, ref int found, ref int total)
    {
        bool a = tile.TryFindNearestPoly(center, extents, include, exclude, out int pa, out Vector3 na, out float da);
        bool b = tile.TryFindNearestPolyLinear(center, extents, include, exclude, out int pb, out Vector3 nb, out float db);
        total++;
        found += b ? 1 : 0;
        Assert.True(a == b && pa == pb && BitConverter.SingleToInt32Bits(da) == BitConverter.SingleToInt32Bits(db) && na.Equals(nb),
            $"tile ({tile.X},{tile.Y}) centre {center} extents {extents}: BV {a}/{pa}/{da}/{na} linear {b}/{pb}/{db}/{nb}");
    }

    private static Vector3 RandomIn(Random rng, Vector3 low, Vector3 high, float margin)
        => new(
            low.X - margin + (float)(rng.NextDouble() * (high.X - low.X + (2 * margin))),
            low.Y - margin + (float)(rng.NextDouble() * (high.Y - low.Y + (2 * margin))),
            low.Z - margin + (float)(rng.NextDouble() * (high.Z - low.Z + (2 * margin))));

    private static Vector3 RandomExtents(Random rng) => rng.Next(4) switch
    {
        0 => new Vector3(0.05f, 1f, 0.05f),
        1 => new Vector3(2f, 4f, 2f),
        2 => new Vector3(5f, 10f, 5f),
        _ => new Vector3((float)(rng.NextDouble() * 15), (float)(rng.NextDouble() * 20), (float)(rng.NextDouble() * 15)),
    };

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(40, false)]
    public void NearestPoly_WithBvTree_EqualsTheLinearScan_OnSyntheticTiles(int minShift, bool treeExpectedToBeUsed)
    {
        int found = 0;
        int total = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            NavMeshTile tile = NavMeshTile.Parse(WithBvTree(Bumpy(seed).Build(), minShift));
            Assert.Equal(treeExpectedToBeUsed, tile.HasBvTree);
            var rng = new Random(seed * 77);
            for (int q = 0; q < 1500; q++)
            {
                NavTerrain include = rng.Next(3) == 0 ? NavTerrain.Water : NavTerrain.Ground | NavTerrain.Water;
                NavTerrain exclude = rng.Next(5) == 0 ? NavTerrain.Water : 0;
                AssertSameNearest(tile, RandomIn(rng, tile.BoundLow, tile.BoundHigh, 6), RandomExtents(rng), include, exclude, ref found, ref total);
            }
        }

        Assert.InRange(found, total / 4, total); // the tree-assisted query returned polygons, not only misses
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearestPoly_EqualDistances_GoToTheLowestPolygonIndex_WithTheBvTree(bool treeVisitsHighIndicesFirst)
    {
        // A flat open grid queried exactly on interior cell corners (a vertex of four polygons, at the plane's height): the point is
        // a vertex of all four, so each is at distance 0 (the one containing it by the crossing test, and the other three by their
        // closest point) and the four tie for real. The tree is built so a tie can be met in either index order.
        var flat = new CellTile(0, 0, 0, 0, 8, 8, 2);
        NavMeshTile tile = NavMeshTile.Parse(WithBvTree(flat.Build(), descending: treeVisitsHighIndicesFirst));
        Assert.True(tile.HasBvTree);
        int checkedCorners = 0;
        for (int i = 1; i < 8; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                Vector3 corner = NavMeshFormat.ToRecast(new Vector3(i * 2, j * 2, 0));
                int[] touching = [.. Enumerable.Range(0, tile.Polys.Length).Where(p => tile.Polys[p].Vertices.Any(v => tile.Vertices[v] == corner))];
                Assert.Equal(4, touching.Length);
                foreach (Vector3 extents in new[] { new Vector3(0.05f, 1f, 0.05f), new Vector3(3f, 4f, 3f) })
                {
                    Assert.True(tile.TryFindNearestPoly(corner, extents, NavTerrain.Ground, 0, out int poly, out Vector3 nearest, out float d));
                    Assert.Equal(0f, d); // all four really are equally near
                    Assert.Equal(corner, nearest);
                    Assert.Equal(touching.Min(), poly);
                    tile.TryFindNearestPolyLinear(corner, extents, NavTerrain.Ground, 0, out int linear, out _, out _);
                    Assert.Equal(linear, poly);
                    checkedCorners++;
                }
            }
        }

        Assert.Equal(2 * 7 * 7, checkedCorners);
    }

    [Fact]
    public void PointQueries_OnATileWithABvTree_AllocateNothing()
    {
        NavMeshTile tile = NavMeshTile.Parse(WithBvTree((Bumpy(6) with { Height = (_, _) => 0 }).Build()));
        Assert.True(tile.HasBvTree);
        var mesh = new NavMesh(0, Params);
        Assert.True(mesh.AddTile(0, 0, tile));
        var random = new Random(8);
        Vector3 at = tile.Center(tile.Polys.Length / 2);
        Assert.True(mesh.TryFindNearestPoly(at, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef start, out Vector3 onMesh));
        Vector3[] probes = [at, tile.Center(1), tile.Center(tile.Polys.Length - 1), at + new Vector3(0.7f, 0.2f, -0.4f)];

        Action window = () =>
        {
            for (int i = 0; i < 200; i++)
            {
                foreach (Vector3 p in probes)
                {
                    mesh.TryFindNearestPoly(p, NavMeshPathfinder.NearExtents, PathOptions.Default, out _, out _);
                    tile.TryFindNearestPoly(p, NavMeshPathfinder.FarExtents, NavTerrain.Ground, 0, out _, out _, out _);
                }

                NavMeshQuery.TryRandomPointAround(mesh, start, onMesh, 6f, PathOptions.Default, random, out _);
            }
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, maxBytes: 0, "point queries on a BV-tree tile");
    }

    [Fact]
    public void ATileWhoseBvTreeIsRejected_IsCounted()
    {
        long before = PathSearchCounters.BvTreesRejected;
        NavMeshTile.Parse(Bumpy(3).Build()); // no BV nodes
        NavMeshTile.Parse(WithBvTree(Bumpy(3).Build(), minShift: 40)); // leaves too tight to trust
        NavMeshTile good = NavMeshTile.Parse(WithBvTree(Bumpy(3).Build()));
        Assert.True(good.HasBvTree);
        Assert.True(PathSearchCounters.BvTreesRejected >= before + 2); // other tests parse tiles too
    }

    [Fact]
    public void NearestPoly_WithoutBvNodes_UsesTheLinearScan()
    {
        NavMeshTile tile = NavMeshTile.Parse(Bumpy(3).Build());
        Assert.False(tile.HasBvTree);
        int found = 0, total = 0;
        var rng = new Random(9);
        for (int q = 0; q < 300; q++)
        {
            AssertSameNearest(tile, RandomIn(rng, tile.BoundLow, tile.BoundHigh, 3), RandomExtents(rng), NavTerrain.Ground, 0, ref found, ref total);
        }

        Assert.True(found > 0);
    }

    [Fact]
    public void NearestPoly_NonFiniteQuery_FallsBackToTheLinearScan()
    {
        NavMeshTile tile = NavMeshTile.Parse(WithBvTree(Bumpy(2).Build()));
        int found = 0, total = 0;
        foreach (Vector3 extents in new[] { new Vector3(float.PositiveInfinity), new Vector3(float.NaN, 1, 1), new Vector3(1e30f) })
        {
            AssertSameNearest(tile, tile.Center(5), extents, NavTerrain.Ground, 0, ref found, ref total);
        }
    }

    [Fact]
    public void BvTree_MalformedNodes_AreNotUsed()
    {
        byte[] good = WithBvTree(Bumpy(4).Build());
        Assert.True(NavMeshTile.Parse(good).HasBvTree);

        // The root's escape distance (the first node's index field, at tile end minus nodes*16 + 12) no longer spans the tree.
        int nodeCount = BitConverter.ToInt32(good, 48);
        int rootIndexAt = good.Length - (nodeCount * 16) + 12;
        byte[] shortEscape = (byte[])good.Clone();
        BitConverter.GetBytes(-(nodeCount - 1)).CopyTo(shortEscape, rootIndexAt);
        Assert.False(NavMeshTile.Parse(shortEscape).HasBvTree);

        byte[] hugeEscape = (byte[])good.Clone();
        BitConverter.GetBytes(int.MinValue).CopyTo(hugeEscape, rootIndexAt);
        Assert.False(NavMeshTile.Parse(hugeEscape).HasBvTree);

        // A leaf naming a polygon twice (the last node is a leaf).
        byte[] duplicate = (byte[])good.Clone();
        BitConverter.GetBytes(0).CopyTo(duplicate, good.Length - 4);
        BitConverter.GetBytes(0).CopyTo(duplicate, good.Length - 4 - 16);
        Assert.False(NavMeshTile.Parse(duplicate).HasBvTree);
    }

    [RealTerrainFact]
    public void NearestPoly_WithBvTree_EqualsTheLinearScan_OnRealTiles()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!, "mmaps"));
        (NavMesh mesh, _) = NavMeshSearchCostTests.LoadElwynn(nav);
        int tiles = 0, withTree = 0, found = 0, total = 0;
        long treeBytes = 0, polys = 0;
        for (int tx = 0; tx < 64; tx++)
        {
            for (int ty = 0; ty < 64; ty++)
            {
                if (mesh.GetTile(tx, ty) is not { } tile)
                {
                    continue;
                }

                tiles++;
                withTree += tile.HasBvTree ? 1 : 0;
                treeBytes += tile.BvTreeBytes;
                polys += tile.Polys.Length;
                var rng = new Random((tx * 64) + ty);
                for (int q = 0; q < 1500; q++)
                {
                    // Mostly on the mesh (the bot queries), some far off it, over both flag filters.
                    Vector3 c = q % 4 == 3 ? RandomIn(rng, tile.BoundLow, tile.BoundHigh, 60) : tile.Center(rng.Next(tile.Polys.Length)) + new Vector3((float)rng.NextDouble() - 0.5f, (float)(rng.NextDouble() * 6) - 3, (float)rng.NextDouble() - 0.5f);
                    Vector3 extents = q % 3 == 0 ? NavMeshPathfinder.NearExtents : q % 3 == 1 ? NavMeshPathfinder.FarExtents : RandomExtents(rng);
                    AssertSameNearest(tile, c, extents, q % 5 == 0 ? NavTerrain.Water : NavTerrain.Ground | NavTerrain.Water, q % 7 == 0 ? NavTerrain.SteepSlopes : 0, ref found, ref total);
                }
            }
        }

        output.WriteLine($"{tiles} tiles, {withTree} use their BV tree; {polys} polygons, BV tree {treeBytes / 1024.0 / 1024.0:F2} MiB ({(double)treeBytes / Math.Max(1, tiles) / 1024.0:F0} KiB/tile, {(double)treeBytes / Math.Max(1, polys):F1} B/poly); {total} queries, {found} found a polygon");
        // Informational: the cost of 3000 near-extents lookups on one tile, tree against scan (Debug build).
        NavMeshTile timed = FirstTile(mesh);
        var probe = new Random(5);
        Vector3[] centers = [.. Enumerable.Range(0, 3000).Select(_ => timed.Center(probe.Next(timed.Polys.Length)))];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (Vector3 c in centers)
        {
            timed.TryFindNearestPoly(c, NavMeshPathfinder.NearExtents, NavTerrain.Ground, 0, out _, out _, out _);
        }

        double treeMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        foreach (Vector3 c in centers)
        {
            timed.TryFindNearestPolyLinear(c, NavMeshPathfinder.NearExtents, NavTerrain.Ground, 0, out _, out _, out _);
        }

        output.WriteLine($"3000 lookups on a {timed.Polys.Length}-polygon tile: tree {treeMs:F1} ms, linear scan {clock.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(tiles >= 4);
        Assert.Equal(tiles, withTree);
        Assert.True(found > total / 2);
    }

    /// <summary>The former per-expansion algorithm: scan the neighbour's opposite border edges with <see cref="NavMesh.TryOverlap"/>.</summary>
    private static List<(int Poly, Vector3 A, Vector3 B)> ScanPortals(NavMeshTile tile, int poly, int edge, NavMeshTile other)
    {
        NavPoly p = tile.Polys[poly];
        int side = p.Neighbours[edge] & 0xff;
        Vector3 a = tile.Vertices[p.Vertices[edge]];
        Vector3 b = tile.Vertices[p.Vertices[(edge + 1) % p.VertexCount]];
        bool alongZ = side is 0 or 4;
        float climb = MathF.Max(MathF.Max(tile.WalkableClimb, other.WalkableClimb), 0.5f);
        var result = new List<(int, Vector3, Vector3)>();
        int lastPoly = -1;
        foreach (BorderEdge candidate in other.GetBorderEdges((side + 4) & 7))
        {
            if (candidate.Poly != lastPoly
                && NavMesh.TryOverlap(a, b, candidate.A, candidate.B, alongZ, climb, out Vector3 pa, out Vector3 pb))
            {
                result.Add((candidate.Poly, pa, pb));
                lastPoly = candidate.Poly;
            }
        }

        return result;
    }

    private static int AssertPortalsEqualScan(NavMesh mesh, NavMeshTile tile)
    {
        int edgesChecked = 0;
        for (int poly = 0; poly < tile.Polys.Length; poly++)
        {
            NavPoly p = tile.Polys[poly];
            for (int e = 0; e < p.VertexCount; e++)
            {
                if ((p.Neighbours[e] & NavMeshFormat.ExternalLink) == 0
                    || NavMeshFormat.SideOffset(p.Neighbours[e] & 0xff) is not { } offset
                    || mesh.GetTile(tile.X + offset.Dx, tile.Y + offset.Dy) is not { } other)
                {
                    continue;
                }

                var expected = ScanPortals(tile, poly, e, other);
                CrossPortal[] actual = tile.GetCrossPortals(poly, e, other);
                Assert.Equal(expected.Count, actual.Length);
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert.Equal(expected[i].Poly, actual[i].Poly);
                    Assert.Equal(expected[i].A, actual[i].A);
                    Assert.Equal(expected[i].B, actual[i].B);
                }

                Assert.Same(actual, tile.GetCrossPortals(poly, e, other)); // second call: from the table
                edgesChecked++;
            }
        }

        return edgesChecked;
    }

    [RealTerrainFact]
    public void CrossPortals_EqualTheOverlapScan_ForEveryBorderEdgeOfLoadedRealTiles()
    {
        var nav = new NavMeshPathfinder(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!, "mmaps"));
        (NavMesh mesh, _) = NavMeshSearchCostTests.LoadElwynn(nav);
        int tiles = 0, edges = 0;
        long bytes = 0;
        for (int tx = 0; tx < 64; tx++)
        {
            for (int ty = 0; ty < 64; ty++)
            {
                if (mesh.GetTile(tx, ty) is { } tile)
                {
                    tiles++;
                    edges += AssertPortalsEqualScan(mesh, tile);
                    bytes += tile.CrossPortalBytes;
                }
            }
        }

        output.WriteLine($"{tiles} tiles, {edges} border edges with a loaded neighbour compared; portal tables {bytes / 1024.0:F0} KiB in total ({bytes / 1024.0 / Math.Max(1, tiles):F1} KiB/tile)");
        Assert.True(edges > 100, $"only {edges} border edges compared");
    }

    private static NavMeshTile FirstTile(NavMesh mesh)
    {
        for (int x = 0; x < 64; x++)
        {
            for (int y = 0; y < 64; y++)
            {
                if (mesh.GetTile(x, y) is { } t)
                {
                    return t;
                }
            }
        }

        throw new InvalidOperationException("no tiles");
    }

    private static (NavMesh Mesh, NavMeshTile A, NavMeshTile B) TwoTiles(NavMeshTile? b = null)
    {
        var mesh = new NavMesh(0, Params);
        NavMeshTile a = NavMeshTile.Parse((Bumpy(11, 0, 0, 0, 0, 5, 2, [0]) with { Height = (_, _) => 0 }).Build());
        // Recast +x is world Y: tile (1, 0) starts where tile (0, 0) ends (world Y 10), with cells of another size, so edges overlap in pieces.
        b ??= NavMeshTile.Parse((Bumpy(12, 1, 0, 0, 10, 4, 2.5f, [4]) with { Height = (_, _) => 0.2f }).Build());
        Assert.True(mesh.AddTile(0, 0, a));
        Assert.True(mesh.AddTile(1, 0, b));
        return (mesh, a, b);
    }

    [Fact]
    public void CrossPortals_EqualTheOverlapScan_OnSyntheticNeighbours()
    {
        (NavMesh mesh, NavMeshTile a, NavMeshTile b) = TwoTiles();
        int fromA = AssertPortalsEqualScan(mesh, a);
        int fromB = AssertPortalsEqualScan(mesh, b);
        Assert.True(fromA > 0 && fromB > 0);
        Assert.True(a.CrossPortalBytes > 0);
    }

    [Fact]
    public void CrossPortals_FollowANeighbourThatIsUnloadedAndReplaced()
    {
        (NavMesh mesh, NavMeshTile a, NavMeshTile b) = TwoTiles();
        AssertPortalsEqualScan(mesh, a);
        long before = a.CrossPortalBytes;
        Assert.True(mesh.RemoveTile(1, 0));
        Assert.True(a.CrossPortalBytes < before); // the removed tile's tables are dropped

        // A different tile in the same slot: the stale tables must not be served.
        NavMeshTile replacement = NavMeshTile.Parse((Bumpy(13, 1, 0, 0, 10, 5, 3, [4]) with { Height = (_, _) => 0 }).Build());
        Assert.True(mesh.AddTile(1, 0, replacement));
        Assert.True(AssertPortalsEqualScan(mesh, a) > 0);
        Assert.NotSame(b, replacement);
    }

    [Fact]
    public void CollectNeighbours_AcrossATileBorder_EqualsTheFormerScan()
    {
        (NavMesh mesh, NavMeshTile a, NavMeshTile b) = TwoTiles();
        var buffer = new List<NavNeighbour>();
        int crossings = 0;
        foreach (NavMeshTile tile in new[] { a, b })
        {
            for (int poly = 0; poly < tile.Polys.Length; poly++)
            {
                mesh.CollectNeighbours(new NavPolyRef(tile, poly), buffer);
                var expected = new List<NavNeighbour>();
                NavPoly p = tile.Polys[poly];
                Vector3 center = tile.Center(poly);
                for (int e = 0; e < p.VertexCount; e++)
                {
                    ushort nei = p.Neighbours[e];
                    if (nei == 0)
                    {
                        continue;
                    }

                    Vector3 va = tile.Vertices[p.Vertices[e]];
                    Vector3 vb = tile.Vertices[p.Vertices[(e + 1) % p.VertexCount]];
                    if ((nei & NavMeshFormat.ExternalLink) == 0)
                    {
                        int index = nei - 1;
                        if (index < tile.Polys.Length && index != poly)
                        {
                            (Vector3 l, Vector3 r) = NavMeshQuery_Orient(center, va, vb);
                            expected.Add(new NavNeighbour(new NavPolyRef(tile, index), l, r, 0));
                        }

                        continue;
                    }

                    int side = nei & 0xff;
                    NavMeshTile other = mesh.GetTile(tile.X + NavMeshFormat.SideOffset(side)!.Value.Dx, tile.Y + NavMeshFormat.SideOffset(side)!.Value.Dy)!;
                    var portals = ScanPortals(tile, poly, e, other);
                    for (int i = portals.Count - 1; i >= 0; i--)
                    {
                        (int toPoly, Vector3 pa, Vector3 pb) = portals[i];
                        (Vector3 l, Vector3 r) = NavMeshQuery_Orient(center, pa, pb);
                        expected.Add(new NavNeighbour(new NavPolyRef(other, toPoly), l, r, (byte)(side >> 1)));
                        crossings++;
                    }
                }

                Assert.Equal(expected, buffer);
            }
        }

        Assert.True(crossings > 10);
    }

    [Fact]
    public void TryOverlap_MatchesDetourOverlapSlabs_ForSlopedAndNarrowEdges()
    {
        const float Climb = 1.6f;

        // Sloped border edges whose vertical gap at the overlap midpoint is 1.8 yd (> 1 * Climb) but at the lower endpoint is 0.4 yd (<= 2 * Climb):
        // Detour's overlapSlabs connects them because (dmin * dmin) <= (climb * 2)^2.
        Vector3 a0 = new(10f, 0.0f, 0f);
        Vector3 a1 = new(10f, 2.0f, 10f);
        Vector3 b0 = new(10f, 0.4f, 0f);
        Vector3 b1 = new(10f, 5.2f, 10f);
        Assert.True(NavMesh.TryOverlap(a0, a1, b0, b1, alongZ: true, Climb, out Vector3 pa, out Vector3 pb));
        Assert.Equal(0f, pa.Z, 4);
        Assert.Equal(10f, pb.Z, 4);

        // Horizontal overlap of 0.015 yd (< 2 * px = 0.02f): Detour's px = 0.01f endpoint shrink rejects the narrow touch.
        Vector3 narrowA0 = new(10f, 0f, 0f);
        Vector3 narrowA1 = new(10f, 0f, 5.015f);
        Vector3 narrowB0 = new(10f, 0f, 5.000f);
        Vector3 narrowB1 = new(10f, 0f, 10.0f);
        Assert.False(NavMesh.TryOverlap(narrowA0, narrowA1, narrowB0, narrowB1, alongZ: true, Climb, out _, out _));

        // Perpendicular distance from tile border > 0.01f is rejected.
        Vector3 farB0 = new(10.02f, 0f, 0f);
        Vector3 farB1 = new(10.02f, 0f, 10f);
        Assert.False(NavMesh.TryOverlap(a0, a1, farB0, farB1, alongZ: true, Climb, out _, out _));
    }

    [Fact]
    public void GetCrossPortals_DeduplicatesTargetPolygon_WhenNeighbourHasMultipleCollinearBorderEdges()
    {
        // Matches 0013430.mmtile poly 9373 across from 0013330.mmtile polys 438/441: a degenerate border polygon whose vertices
        // lie on Recast x = 4 (side 4) and whose multiple edges carry DT_EXT_LINK | 4 must produce at most one CrossPortal from tile A.
        NavMeshTile a = NavMeshTile.Parse((new CellTile(0, 0, 0, 0, 1, 1, 4) { LinkedSides = [0] }).Build());
        byte[] rawB = (new CellTile(1, 0, 0, 4, 1, 1, 4) { LinkedSides = [4] }).Build();
        // Move vertex 2 of poly 0 in rawB onto the Recast x = 4 border at (4, 0, 2) and mark edge 1 as DT_EXT_LINK | 4 in addition to edge 0,
        // so both edge 0 ((4,0,0)->(4,0,4)) and edge 1 ((4,0,4)->(4,0,2)) lie on the x = 4 border and overlap tile A's edge 2 (side 0).
        const int VertsOffset = 100;
        const int PolysOffset = 100 + (4 * 12);
        BitConverter.GetBytes(4f).CopyTo(rawB, VertsOffset + (2 * 12) + 0);
        BitConverter.GetBytes(0f).CopyTo(rawB, VertsOffset + (2 * 12) + 4);
        BitConverter.GetBytes(2f).CopyTo(rawB, VertsOffset + (2 * 12) + 8);
        BitConverter.GetBytes((ushort)(NavMeshFormat.ExternalLink | 4)).CopyTo(rawB, PolysOffset + 16 + (0 * 2));
        BitConverter.GetBytes((ushort)(NavMeshFormat.ExternalLink | 4)).CopyTo(rawB, PolysOffset + 16 + (1 * 2));
        NavMeshTile b = NavMeshTile.Parse(rawB);

        CrossPortal[] portals = a.GetCrossPortals(0, 2, b);
        Assert.Single(portals);
        Assert.Equal(0, portals[0].Poly);
    }

    [Fact]
    public void ClosestPointOnPoly_ProjectsOntoDetailBoundaryEdges_WhenOutsidePolygon()
    {
        // A 1x1 cell polygon over [0, 4] x [0, 4] whose coarse vertices sit at Y = 0, while its detail mesh adds a peaked boundary
        // vertex at (2, 1.5, 0) on the Z = 0 edge: querying outside the polygon at (2, 0, -1) must project onto the detail boundary
        // at Y = 1.5, matching Detour's closestPointOnDetailEdges<true>.
        byte[] raw = new CellTile(0, 0, 0, 0, 1, 1, 4).Build();
        // CellTile(1,1) layout: 100 B header + 4 verts (48 B) + 1 poly (32 B) + 4 links (64 B) = 244 B before detailMeshes (12 B) + detailTris (8 B).
        const int DetailMeshesOffset = 100 + (4 * 12) + (1 * 32) + (4 * 16);
        byte[] withDetailVert = new byte[raw.Length + 12];
        Array.Copy(raw, 0, withDetailVert, 0, DetailMeshesOffset + 12);
        BitConverter.GetBytes(1).CopyTo(withDetailVert, 40); // DetailVertCount = 1
        BitConverter.GetBytes(2).CopyTo(withDetailVert, 44); // DetailTriCount = 2
        withDetailVert[DetailMeshesOffset + 8] = 1; // vertCount = 1
        withDetailVert[DetailMeshesOffset + 9] = 2; // triCount = 2
        int dvOffset = DetailMeshesOffset + 12;
        BitConverter.GetBytes(2f).CopyTo(withDetailVert, dvOffset);
        BitConverter.GetBytes(1.5f).CopyTo(withDetailVert, dvOffset + 4);
        BitConverter.GetBytes(0f).CopyTo(withDetailVert, dvOffset + 8);
        int dtOffset = dvOffset + 12;
        withDetailVert[dtOffset + 0] = 3;
        withDetailVert[dtOffset + 1] = 4;
        withDetailVert[dtOffset + 2] = 0;
        withDetailVert[dtOffset + 3] = 0b0001_01; // edges (v3->v4) and (v4->v0) are boundary edges
        withDetailVert[dtOffset + 4] = 0;
        withDetailVert[dtOffset + 5] = 4;
        withDetailVert[dtOffset + 6] = 2;
        withDetailVert[dtOffset + 7] = 0;

        NavMeshTile tile = NavMeshTile.Parse(withDetailVert);
        Vector3 outside = new(2f, 0f, -1f);
        Vector3 closest = tile.ClosestPointOnPoly(0, outside, out bool posOverPoly);
        Assert.False(posOverPoly);
        Assert.Equal(2f, closest.X, 3);
        Assert.Equal(1.5f, closest.Y, 3);
        Assert.Equal(0f, closest.Z, 3);
    }

    private static (Vector3 Left, Vector3 Right) NavMeshQuery_Orient(Vector3 inside, Vector3 a, Vector3 b)
        => NavMesh.Cross(inside, a, b) < 0 ? (a, b) : (b, a);
}
