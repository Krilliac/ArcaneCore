using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The navmesh's random-point-around-a-circle and surface raycast queries (<see cref="IMeshPointQuery"/>, Detour
/// <c>findRandomPointAroundCircle</c> / <c>raycast</c>) on synthetic tiles. The raycast is also checked against DotRecast, a test-only
/// independent Detour port, on seeded random mazes.
/// </summary>
public sealed class NavMeshPointQueryTests(ITestOutputHelper output)
{
    private const uint MapId = 0;

    /// <summary>10×10 cells of 2 yd over world [0, 20)² with a hole of 2×2 cells at world [8, 12)².</summary>
    private static CellTile Holed() => new(0, 0, 0, 0, 10, 10, Size: 2) { Walkable = (i, j) => !(i is 4 or 5 && j is 4 or 5) };

    /// <summary>4×4 cells of 5 yd over world [0, 20)²; a wall of missing cells at X 10–15, Y 0–15 (gap at the top).</summary>
    private static CellTile Walled() => new(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, j) => !(i == 2 && j < 3) };

    private static NavMeshPathfinder Load(NavMeshFixture fixture, params (int X, int Y, CellTile Tile)[] tiles)
    {
        fixture.WriteParams(MapId);
        foreach ((int x, int y, CellTile tile) in tiles)
        {
            fixture.WriteTile(MapId, x, y, tile);
        }

        var pathfinder = new NavMeshPathfinder(fixture.Directory);
        foreach ((int x, int y, _) in tiles)
        {
            Assert.True(pathfinder.LoadTile(MapId, x, y));
        }

        return pathfinder;
    }

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static bool InHole(Vector3 p) => p.X > 8.001f && p.X < 11.999f && p.Y > 8.001f && p.Y < 11.999f;

    private static bool OnMesh(NavMeshPathfinder nav, Vector3 world)
        => nav.GetNavMesh(MapId)!.TryFindNearestPoly(NavMeshFormat.ToRecast(world), new Vector3(0.05f, 1f, 0.05f), PathOptions.Default, out _, out _);

    [Fact]
    public void RandomPoints_AreOnTheMesh_WithinTheRadius_ReachableAndNeverInTheHole()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Holed()));
        var random = new Random(7);
        Vector3 center = new(15, 10, 0);
        var distinct = new HashSet<(int, int)>();

        for (int i = 0; i < 400; i++)
        {
            Assert.True(nav.TryGetRandomPointAround(MapId, center, 7f, random, null, out Vector3 p));
            Assert.True(Horizontal(p, center) <= 7.01f, $"{p} is {Horizontal(p, center)} yd from the centre");
            Assert.False(InHole(p), $"{p} is inside the hole");
            Assert.True(OnMesh(nav, p), $"{p} is off the mesh");
            Assert.Equal(PathType.Normal, nav.FindPath(MapId, center, p).Type);
            distinct.Add(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)));
        }

        Assert.True(distinct.Count > 40, $"only {distinct.Count} distinct 1 yd cells were drawn");
    }

    [Fact]
    public void RandomPoints_StayOnTheStartsSideOfAWall()
    {
        using var fixture = new NavMeshFixture();
        // Column i = 5 (world X 10–12) is missing for every row: the halves are not connected, but the circle spans both.
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 10, 10, Size: 2) { Walkable = (i, _) => i != 5 }));
        var random = new Random(11);

        for (int i = 0; i < 300; i++)
        {
            Assert.True(nav.TryGetRandomPointAround(MapId, new Vector3(8, 10, 0), 9f, random, null, out Vector3 p));
            Assert.True(p.X <= 10.001f, $"{p} is across the wall");
        }
    }

    [Fact]
    public void RandomPoints_AreDeterministicForASeed()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Holed()));
        List<Vector3> Draw(int seed)
        {
            var random = new Random(seed);
            var points = new List<Vector3>();
            for (int i = 0; i < 20; i++)
            {
                Assert.True(nav.TryGetRandomPointAround(MapId, new Vector3(15, 10, 0), 6f, random, null, out Vector3 p));
                points.Add(p);
            }

            return points;
        }

        Assert.Equal(Draw(5), Draw(5));
        Assert.NotEqual(Draw(5), Draw(6));
    }

    [Fact]
    public void RandomPoint_IsUnavailable_WithoutAMesh_OrALoadedTile_AndLeavesTheRandomUntouched()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Holed()));
        var random = new Random(3);

        Assert.False(nav.TryGetRandomPointAround(9, new Vector3(15, 10, 0), 5f, random, null, out _)); // no .mmap for map 9
        Assert.False(nav.TryGetRandomPointAround(MapId, new Vector3(900, 900, 0), 5f, random, null, out _)); // tile not loaded
        Assert.False(nav.TryGetRandomPointAround(MapId, new Vector3(15, 10, 0), 0f, random, null, out _));
        Assert.False(nav.TryGetRandomPointAround(MapId, new Vector3(float.NaN, 10, 0), 5f, random, null, out _));
        Assert.False(nav.TryGetRandomPointAround(MapId, new Vector3(15, 10, 90), 5f, random, null, out _)); // 90 yd above the mesh

        Assert.Equal(new Random(3).Next(), random.Next());
    }

    [Fact]
    public void RandomPoints_FollowTheExcludeFilter()
    {
        using var fixture = new NavMeshFixture();
        // Cells with i >= 5 are steep: a wander that excludes steep slopes never draws there although they lie inside the circle.
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 10, 10, Size: 2)
        {
            Flags = (i, _) => i >= 5 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
        }));
        var random = new Random(2);
        var options = new PathOptions { ExcludeFlags = NavTerrain.SteepSlopes };
        for (int i = 0; i < 200; i++)
        {
            Assert.True(nav.TryGetRandomPointAround(MapId, new Vector3(6, 10, 0), 8f, random, options, out Vector3 p));
            Assert.True(p.X <= 10.001f, $"{p} is on a steep cell");
        }
    }

    [Fact]
    public void Raycast_StopsAtTheObstacleEdge_AndPassesThroughTheGap()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));

        MeshRaycastResult blocked = nav.Raycast(MapId, new Vector3(7, 5, 0), new Vector3(17, 5, 0));
        Assert.True(blocked.Available);
        Assert.True(blocked.Hit);
        Assert.Equal(0.3f, blocked.Fraction, 3);
        Assert.True(Vector3.Distance(new Vector3(10, 5, 0), blocked.Point) < 0.01f, $"stopped at {blocked.Point}");

        MeshRaycastResult clear = nav.Raycast(MapId, new Vector3(7, 17, 0), new Vector3(17, 17, 0));
        Assert.True(clear.Available);
        Assert.False(clear.Hit);
        Assert.Equal(1f, clear.Fraction);
        Assert.True(Vector3.Distance(new Vector3(17, 17, 0), clear.Point) < 0.01f);

        MeshRaycastResult edge = nav.Raycast(MapId, new Vector3(7, 17, 0), new Vector3(40, 17, 0));
        Assert.True(edge.Hit, "the mesh ends at X 20");
        Assert.True(Vector3.Distance(new Vector3(20, 17, 0), edge.Point) < 0.01f, $"stopped at {edge.Point}");
    }

    [Fact]
    public void Raycast_FollowsTheExcludeFilter()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 10, 10, Size: 2)
        {
            Flags = (i, _) => i >= 5 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
        }));

        Assert.False(nav.Raycast(MapId, new Vector3(2, 5, 0), new Vector3(18, 5, 0)).Hit);
        MeshRaycastResult steep = nav.Raycast(MapId, new Vector3(2, 5, 0), new Vector3(18, 5, 0), new PathOptions { ExcludeFlags = NavTerrain.SteepSlopes });
        Assert.True(steep.Hit);
        Assert.True(Vector3.Distance(new Vector3(10, 5, 0), steep.Point) < 0.01f, $"stopped at {steep.Point}");
    }

    [Fact]
    public void Raycast_CrossesIntoTheNeighbourTile_AndStopsWhereItIsNotLoaded()
    {
        using var fixture = new NavMeshFixture();
        var west = new CellTile(0, 0, 0, 0, 4, 4, Size: 5) { LinkedSides = [2] };
        var east = new CellTile(0, 1, 20, 0, 4, 4, Size: 5) { LinkedSides = [6], Walkable = (i, j) => !(i == 0 && j > 0) };
        NavMeshPathfinder nav = Load(fixture, (31, 31, west), (30, 31, east));

        MeshRaycastResult across = nav.Raycast(MapId, new Vector3(2, 2, 0), new Vector3(38, 2, 0));
        Assert.True(across.Available);
        Assert.False(across.Hit);
        Assert.True(Vector3.Distance(new Vector3(38, 2, 0), across.Point) < 0.01f, $"stopped at {across.Point}");

        // Above the border portal the east tile has a wall (X 20–25, Y 5–20): the ray crosses the border and is stopped by it.
        MeshRaycastResult walled = nav.Raycast(MapId, new Vector3(17, 17, 0), new Vector3(27, 17, 0));
        Assert.True(walled.Hit);

        nav.UnloadTile(MapId, 30, 31);
        MeshRaycastResult unloaded = nav.Raycast(MapId, new Vector3(2, 2, 0), new Vector3(38, 2, 0));
        Assert.True(unloaded.Available);
        Assert.True(unloaded.Hit);
        Assert.True(unloaded.Point.X <= 20.01f, $"walked into an unloaded tile: {unloaded.Point}");
    }

    [Fact]
    public void Raycast_IsUnavailable_WithoutAMesh_OrALoadedTile_OrFromOffTheMesh()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Walled()));
        Vector3 end = new(17, 5, 0);

        Assert.False(nav.Raycast(9, new Vector3(7, 5, 0), end).Available);
        Assert.False(nav.Raycast(MapId, new Vector3(900, 900, 0), end).Available);
        Assert.False(nav.Raycast(MapId, new Vector3(7, 5, 90), end).Available);
        Assert.False(nav.Raycast(MapId, new Vector3(float.PositiveInfinity, 5, 0), end).Available);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(3f)]
    [InlineData(5f)]
    public void RandomPoints_OnLargePolygons_AreSpreadAcrossASmallDisc_NotPiledAtTheCentre(float radius)
    {
        using var fixture = new NavMeshFixture();
        // 4×4 cells of 12 yd: the whole disc lies on one or two polygons far larger than the wander radius.
        NavMeshPathfinder nav = Load(fixture, (31, 31, new CellTile(0, 0, 0, 0, 4, 4, Size: 12)));
        var random = new Random(21);
        Vector3 center = new(30, 30, 0);
        const int draws = 600;
        int fallbacks = 0;
        int[] quadrants = new int[4];
        float farthest = 0;
        float sum = 0;
        for (int i = 0; i < draws; i++)
        {
            Assert.True(nav.TryGetRandomPointAround(MapId, center, radius, random, null, out Vector3 p));
            float d = Horizontal(p, center);
            Assert.True(d <= radius + 0.01f, $"{p} is {d} yd from the centre, radius {radius}");
            fallbacks += d < 0.01f ? 1 : 0;
            farthest = MathF.Max(farthest, d);
            sum += d;
            quadrants[(p.X >= center.X ? 1 : 0) + (p.Y >= center.Y ? 2 : 0)]++;
        }

        output.WriteLine($"radius {radius}: {fallbacks} of {draws} fell back to the centre, farthest {farthest:F2}, mean {sum / draws:F2}, quadrants {string.Join("/", quadrants)}");
        Assert.True(fallbacks <= draws / 50, $"{fallbacks} of {draws} draws fell back to the centre");
        Assert.True(farthest > 0.9f * radius, $"farthest point only {farthest} yd out");
        Assert.InRange(sum / draws, 0.55f * radius, 0.78f * radius); // uniform over a disc: 2/3 of the radius
        Assert.All(quadrants, q => Assert.True(q >= draws / 6, $"quadrants {string.Join("/", quadrants)}"));
    }

    [Fact]
    public void ThePointQueries_BuildNoCollectionsPerCall_AndTheHelpersTheyUseAllocateNothing()
    {
        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = Load(fixture, (31, 31, Holed()));
        var random = new Random(8);
        Vector3 center = new(15, 10, 0);
        NavMeshTile tile = nav.GetNavMesh(MapId)!.GetTile(0, 0)!;
        Vector3 rc = NavMeshFormat.ToRecast(center);

        Action window = () =>
        {
            for (int i = 0; i < 200; i++)
            {
                nav.TryGetRandomPointAround(MapId, center, 6f, random, null, out _);
                nav.Raycast(MapId, new Vector3(2, 2, 0), new Vector3(18, 18, 0));
                tile.ClosestPointOnPoly(0, rc);
                nav.GetNavMesh(MapId)!.TryFindNearestPoly(rc, NavMeshPathfinder.NearExtents, PathOptions.Default, out _, out _);
            }
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, maxBytes: 0, "point queries");
    }

    [Fact]
    public void ContainsXz_OverThePolygonVertices_MatchesTheListOverload_ForEveryPolygon()
    {
        NavMeshTile tile = NavMeshTile.Parse(Holed().Build());
        var random = new Random(5);
        int inside = 0;
        for (int poly = 0; poly < tile.Polys.Length; poly++)
        {
            Vector3[] corners = [.. tile.Polys[poly].Vertices.Select(v => tile.Vertices[v])];
            for (int i = 0; i < 200; i++)
            {
                Vector3 p = new((float)(random.NextDouble() * 20), 0, (float)(random.NextDouble() * 20));
                tile.ClosestPointOnPoly(poly, p, out bool over);
                Assert.Equal(NavMeshTile.ContainsXz(corners, p), over);
                inside += over ? 1 : 0;
            }
        }

        Assert.InRange(inside, 50, 5000); // both answers occurred
    }

    [Fact]
    public void Raycast_AgreesWithDotRecast_OnRandomMazes()
    {
        const int cells = 6;
        const float size = 5;
        var rng = new Random(4321);
        int compared = 0;
        int hits = 0;
        var mismatches = new List<string>();
        for (int trial = 0; trial < 60; trial++)
        {
            bool[,] walkable = new bool[cells, cells];
            var open = new List<(int I, int J)>();
            for (int i = 0; i < cells; i++)
            {
                for (int j = 0; j < cells; j++)
                {
                    walkable[i, j] = rng.NextDouble() > 0.3;
                    if (walkable[i, j])
                    {
                        open.Add((i, j));
                    }
                }
            }

            if (open.Count < 4)
            {
                continue;
            }

            byte[] bytes = new CellTile(0, 0, 0, 0, cells, cells, Size: size) { Walkable = (i, j) => walkable[i, j] }.Build();
            var arc = new NavMesh(0, new NavMeshParams(Vector3.Zero, 533.3333f, 533.3333f, 1, 0));
            Assert.True(arc.AddTile(31, 31, NavMeshTile.Parse(bytes)));
            var dot = new DtNavMesh();
            Assert.True(dot.Init(new DtNavMeshParams
            {
                orig = RcVec3f.Zero, tileWidth = 533.3333f, tileHeight = 533.3333f, maxTiles = 1, maxPolys = 1 << 16,
            }, NavMeshFormat.MaxVertsPerPoly).Succeeded());
            Assert.True(dot.AddTile(new DtMeshDataReader().Read(new RcByteBuffer(bytes), 6, is32Bit: false), 0, 0, out _).Succeeded());
            var query = new DtNavMeshQuery(dot);
            var filter = new DtQueryDefaultFilter();
            filter.SetIncludeFlags((int)PathOptions.Default.EffectiveIncludeFlags);

            for (int ray = 0; ray < 12; ray++)
            {
                (int I, int J) cell = open[rng.Next(open.Count)];
                Vector3 start = NavMeshFormat.ToRecast(new Vector3(
                    (cell.I * size) + 0.3f + (float)(rng.NextDouble() * (size - 0.6f)), (cell.J * size) + 0.3f + (float)(rng.NextDouble() * (size - 0.6f)), 0));
                Vector3 end = NavMeshFormat.ToRecast(new Vector3((float)(rng.NextDouble() * cells * size), (float)(rng.NextDouble() * cells * size), 0));
                Assert.True(arc.TryFindNearestPoly(start, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef arcPoly, out Vector3 arcOn));
                query.FindNearestPoly(new RcVec3f(start.X, start.Y, start.Z), new RcVec3f(3, 5, 3), filter, out long dotRef, out RcVec3f dotOn, out _);
                Assert.NotEqual(0, dotRef);

                long[] path = new long[64];
                query.Raycast(dotRef, dotOn, new RcVec3f(end.X, end.Y, end.Z), filter, out float dotT, out _, path, out _, path.Length);
                NavMeshQuery.RayResult mine = NavMeshQuery.Raycast(arc, arcPoly, arcOn, end, PathOptions.Default);

                bool dotHit = dotT < 1f;
                float dotFraction = dotHit ? dotT : 1f;
                compared++;
                hits += dotHit ? 1 : 0;
                if (dotHit != mine.Hit || MathF.Abs(dotFraction - mine.Fraction) > 1e-3f)
                {
                    mismatches.Add($"trial {trial} ray {ray}: DotRecast {(dotHit ? "hit" : "clear")} t={dotFraction:F4}, mine {(mine.Hit ? "hit" : "clear")} t={mine.Fraction:F4}");
                }
            }
        }

        output.WriteLine($"{compared} rays compared with DotRecast, {hits} of them hit a wall, {mismatches.Count} differ");
        Assert.True(compared > 300 && hits > 50, "the maze rays did not exercise the wall hit");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(10)));
    }
}
