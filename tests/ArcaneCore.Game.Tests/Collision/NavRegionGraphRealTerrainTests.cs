using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="NavRegionGraph"/> on the real Kalimdor and Eastern Kingdoms navigation meshes (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, skipped
/// without it): the places where every bot stalled in issues #191 and #194 are joined on foot to the quest enders they were walking to,
/// and the region search finds that way in milliseconds. Positions are the issues' stall points and the world database's spawns.
/// </summary>
public sealed class NavRegionGraphRealTerrainTests(ITestOutputHelper output)
{
    private static string Mmaps => Path.Combine(Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!, "mmaps");

    /// <summary>
    /// The most portals a found way across a continent may settle. Work, not wall-clock time, so a loaded machine cannot fail it; the
    /// searches took 0.4 to 16 ms (one 295 ms under load).
    /// </summary>
    private const int FoundWork = 5_000;

    private static readonly PathOptions Walking = new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes };

    // Grish Longrunner (creature 12576, spawn 21295), the ender of quest 5881, on The Barrens side of the Great Lift's top.
    private static readonly Vector3 Grish = new(-4602.11f, -1835.37f, 86.1941f);

    // Neeka Bloodscar (creature 5394, spawn 6874), the ender of quest 1418, at the top of the Kargath tower; a Kargath Grunt below on the floor.
    private static readonly Vector3 Neeka = new(-6672.25f, -2240.52f, 278.577f);
    private static readonly Vector3 KargathFloor = new(-6675.7f, -2173.8f, 244.2f);

    public static TheoryData<float, float, float> StonetalonStalls => new()
    {
        { 183.9f, 181.2f, 51.8f }, { 796.0f, 900.0f, 168.2f }, { 2079.2f, 987.7f, 220.7f },
        { 2705.1f, 1243.8f, 298.2f }, { 2424.1f, 1338.6f, 274.8f }, { 620.3f, 441.3f, 90.1f },
    };

    /// <summary>Issue #191: every Stonetalon stall point is joined on foot to Grish Longrunner, the way found settling at most <see cref="FoundWork"/> portals.</summary>
    [RealTerrainFact]
    public void Kalimdor_EveryStonetalonStallPoint_IsJoinedOnFoot_ToGrishLongrunner()
    {
        (NavRegionGraph graph, NavMeshPathfinder finder) = Build(1);
        foreach ((float x, float y, float z) in StonetalonStalls.Select(row => ((float)row[0], (float)row[1], (float)row[2])))
        {
            Vector3 stall = new(x, y, z);
            (NavRegionRouteStatus status, List<Vector3> portals, double ms, int settled) = Route(graph, finder, stall, Grish);
            output.WriteLine($"{stall}: {status}, {portals.Count} portals, {settled} settled, {ms:F2} ms");
            Assert.Equal(NavRegionRouteStatus.Found, status);
            Assert.NotEmpty(portals);
            Assert.True(settled <= FoundWork, $"the region search settled {settled} portals");
        }
    }

    /// <summary>
    /// Issue #194: the Swamp of Sorrows stall points are joined on foot to Kargath's floor. Neeka Bloodscar's own spot at the top of the
    /// tower is not, for a walker: the tower's upper flight of stairs is flagged steep, and only with steep polygons allowed is her
    /// region joined to the rest (which the bots' indoor climb takes, PlayerbotNavigation.TryClimbIndoors).
    /// </summary>
    [RealTerrainFact]
    public void EasternKingdoms_TheSwampStallPoints_AreJoinedOnFoot_ToKargath_AndNeekasTowerTop_OnlyOverItsSteepStairs()
    {
        (NavRegionGraph graph, NavMeshPathfinder finder) = Build(0);
        foreach (Vector3 stall in (Vector3[])[new(-9979.2f, -3137.6f, 113.9f), new(-9973.3f, -3152.3f, 109.1f), new(-9988.4f, -3070.9f, 54.1f)])
        {
            (NavRegionRouteStatus status, List<Vector3> portals, double ms, int settled) = Route(graph, finder, stall, KargathFloor);
            output.WriteLine($"{stall}: {status}, {portals.Count} portals, {settled} settled, {ms:F2} ms");
            Assert.Equal(NavRegionRouteStatus.Found, status);
            Assert.True(settled <= FoundWork, $"the region search settled {settled} portals");

            (status, portals, ms, settled) = Route(graph, finder, stall, Neeka);
            output.WriteLine($"{stall} to Neeka: {status}, the way ends {Vector3.Distance(portals[^1], Neeka):F0} yards from her, {settled} settled, {ms:F2} ms");
            Assert.NotEqual(NavRegionRouteStatus.Found, status);
            Assert.True(Vector3.Distance(portals[^1], Neeka) < 400, "the partial way does not lead to Kargath");
            Assert.Equal(NavRegionRouteStatus.Unreachable, status); // the whole joined part searched within the budget
            Assert.True(settled < 20_000, $"the region search settled {settled} portals");
        }

        NavRegionGraph climbing = NavRegionGraph.Build(Mmaps, 0, PathMover.Player.IncludeFlags, NavTerrain.Empty);
        var steep = Walking with { ExcludeFlags = NavTerrain.Empty };
        Assert.Equal(NavRegionRouteStatus.Found, climbing.FindRoute(finder.RegionAt(climbing, KargathFloor, steep), KargathFloor,
            finder.RegionAt(climbing, Neeka, steep), Neeka, 20_000, out _));
        Assert.Equal(1, graph.ReachableCount(finder.RegionAt(graph, Neeka, Walking), int.MaxValue));
    }

    // Timbermaw Hold, the tunnel between Felwood and Winterspring (map 1). The points are the ones the bots' stall, dead-end-pin and
    // goal-unreachable lines name (bot QA, issues #1478 and #1479; only x and y are known, no Z). The route array holds the ones on the
    // through route from the Felwood mouth to the Winterspring pin at 7020,-2140 (no separate exit point is known).
    private static readonly (float X, float Y)[] TimbermawRoute =
        [(6627f, -2024f), (6901f, -2020f), (7020f, -2090f), (7010f, -2140f), (7020f, -2140f)];

    // Named in the same lines but off the route: the gate run (5b596dd0d) put them in small components of their own on the walking graph.
    private static readonly (float X, float Y)[] TimbermawOffRoute = [(6957.6f, -2044.4f), (6940f, -2090f)];

    private const float ProbeTopZ = 500f, ProbeBottomZ = -100f, ProbeStepZ = 5f;

    private HashSet<int> ComponentsAt(NavRegionGraph graph, NavMeshPathfinder finder, PathOptions options, float x, float y, List<int> regions)
    {
        HashSet<int> components = [];
        (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
        Assert.True(finder.LoadTile(graph.MapId, tx, ty), $"no navigation tile at {x},{y}");
        for (float z = ProbeTopZ; z >= ProbeBottomZ; z -= ProbeStepZ)
        {
            int region = finder.RegionAt(graph, new Vector3(x, y, z), options);
            if (region >= 0 && components.Add(graph.ComponentOf(region))) regions.Add(region);
        }

        return components;
    }

    /// <summary>
    /// Issues #1478 and #1479: the Timbermaw Hold tunnel is not cut on the walking graph (steep slopes excluded). The through-route samples
    /// share one component (12764 in the 2026-10-25 gate run), so the pile-up is not a data cut. 6957.6,-2044.4 and 6940,-2090 are not in
    /// it: they sit in small components of their own (ledges or pockets only steep polygons may join), which is why a goal there is
    /// unreachable on foot. The graph with steep slopes allowed is printed for those two, not asserted (not known when written). Whether the
    /// route's regions are cut regions (<see cref="NavRegionGraph.JoinsLargeParts"/>) is printed too.
    /// </summary>
    [RealTerrainFact]
    public void Kalimdor_TimbermawHoldTunnel_ThroughRouteIsOneWalkingComponent_AndTheOffRoutePointsAreNotInIt()
    {
        NavMeshPathfinder finder = new(Mmaps);
        NavRegionGraph walking = NavRegionGraph.Build(Mmaps, 1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        HashSet<int>? shared = null;
        List<int> routeRegions = [];
        foreach ((float x, float y) in TimbermawRoute)
        {
            HashSet<int> components = ComponentsAt(walking, finder, Walking, x, y, routeRegions);
            output.WriteLine($"walking: route {x},{y}: components [{string.Join(", ", components.Order())}]");
            Assert.NotEmpty(components);
            if (shared is null) shared = components;
            else shared.IntersectWith(components);
        }

        Assert.True(shared is { Count: > 0 }, "no one walking component has a region at every through-route sample point: the tunnel is cut");
        int main = shared!.OrderByDescending(c => walking.ComponentPolygons(routeRegions.First(r => walking.ComponentOf(r) == c))).First();

        foreach (int region in routeRegions.Where(r => walking.ComponentOf(r) == main).Distinct())
            output.WriteLine($"walking: route region {region}: articulation {walking.IsArticulation(region)}, joins large parts {walking.JoinsLargeParts(region, 500)}");

        foreach ((float x, float y) in TimbermawOffRoute)
        {
            HashSet<int> components = ComponentsAt(walking, finder, Walking, x, y, []);
            output.WriteLine($"walking: off route {x},{y}: components [{string.Join(", ", components.Order())}] (main {main})");
            Assert.DoesNotContain(main, components);
        }

        NavRegionGraph steep = NavRegionGraph.Build(Mmaps, 1, PathMover.Player.IncludeFlags, NavTerrain.Empty);
        var steepAllowed = Walking with { ExcludeFlags = NavTerrain.Empty };
        List<int> steepRoute = [];
        HashSet<int> mainSteep = ComponentsAt(steep, finder, steepAllowed, TimbermawRoute[0].X, TimbermawRoute[0].Y, steepRoute);
        foreach ((float x, float y) in TimbermawOffRoute)
        {
            HashSet<int> components = ComponentsAt(steep, finder, steepAllowed, x, y, []);
            output.WriteLine($"steep allowed: off route {x},{y}: components [{string.Join(", ", components.Order())}]; joins the Felwood mouth's: {components.Overlaps(mainSteep)}");
        }
    }


    private (NavRegionGraph Graph, NavMeshPathfinder Finder) Build(uint map)
    {
        var watch = Stopwatch.StartNew();
        NavRegionGraph graph = NavRegionGraph.Build(Mmaps, map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        output.WriteLine($"map {map}: {graph.TileCount} tiles, {graph.PolygonCount} polygons, {graph.RegionCount} regions, {graph.LinkCount} links in {watch.ElapsedMilliseconds} ms");
        Assert.True(graph.TileCount > 400, $"only {graph.TileCount} tiles read");
        return (graph, new NavMeshPathfinder(Mmaps));
    }

    private static (NavRegionRouteStatus, List<Vector3>, double, int) Route(NavRegionGraph graph, NavMeshPathfinder finder, Vector3 from, Vector3 to)
    {
        int a = Locate(finder, graph, from), b = Locate(finder, graph, to);
        Assert.True(a >= 0, $"no region at {from}");
        Assert.True(b >= 0, $"no region at {to}");
        var watch = Stopwatch.StartNew();
        NavRegionRouteStatus status = graph.FindRoute(a, from, b, to, 20_000, out List<Vector3> portals, out int settled);
        return (status, portals, watch.Elapsed.TotalMilliseconds, settled);
    }

    private static int Locate(NavMeshPathfinder finder, NavRegionGraph graph, Vector3 at)
    {
        (int x, int y) = TerrainTile.TileOf(at.X, at.Y)!.Value;
        Assert.True(finder.LoadTile(graph.MapId, x, y), $"no navigation tile at {at}");
        return finder.RegionAt(graph, at, Walking);
    }
}
