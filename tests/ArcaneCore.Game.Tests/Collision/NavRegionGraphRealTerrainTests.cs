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

    // Issue #1481 samples, all from the live bot log world.debug-20261025-001.log: "goal unreachable on foot ... at X,Y,Z map 0
    // components=A/B" lines (A the bot's region component, B its goal's), here with A = 64458 and B = 1630 for every one of them. The zone is the
    // line's zone=; points are ordered north to south along each road. Zone 10 is Duskwood, 41 Deadwind Pass, 8 Swamp of Sorrows.
    private static readonly (string Label, Vector3 At)[] DuskwoodRoad =
    [
        ("zone 10 Duskwood, 12:24:30", new(-10429.2f, -1599.0f, 86.5f)),
        ("zone 41 Deadwind, 08:38:44", new(-10470.5f, -1701.1f, 84.2f)),
        ("zone 41 Deadwind, 02:02:38", new(-10446.9f, -1755.0f, 90.8f)),
        ("zone 41 Deadwind, 08:09:09", new(-10506.2f, -1747.1f, 89.2f)),
        ("zone 41 Deadwind, 10:21:24", new(-10372.1f, -1950.9f, 90.3f)),
    ];

    private static readonly (string Label, Vector3 At)[] SwampRoad =
    [
        ("zone 41 Deadwind, 21:11:31", new(-10497.9f, -1942.2f, 39.9f)),
        ("zone 41 Deadwind, 02:39:53", new(-10662.6f, -1958.9f, 41.4f)),
        ("zone 41 Deadwind, 18:52:26", new(-10652.8f, -2090.2f, 109.3f)),
        ("zone 41 Deadwind, 15:12:44", new(-10836.6f, -2214.5f, 117.7f)),
        ("zone 8 Swamp of Sorrows, 12:34:24", new(-10451.2f, -2418.8f, 70.2f)),
        ("zone 8 Swamp of Sorrows, 13:16:37", new(-10520.0f, -2564.3f, 22.8f)),
        ("zone 8 Swamp of Sorrows, 22:23:10", new(-10474.8f, -2665.4f, 23.5f)),
        ("zone 8 Swamp of Sorrows, 12:44:08", new(-10510.5f, -2802.8f, 21.9f)),
    ];

    /// <summary>
    /// Issue #1481 (a probe, not a fix): does the region graph of Eastern Kingdoms split Deadwind Pass (live component 64458) from the rest
    /// (1630) along the Deadwind-Duskwood and Deadwind-Swamp of Sorrows roads, and does it with the steep polygons allowed? The test prints
    /// each live bot position's region, component and its size (the ids need not equal the live server's: the graph is built here), then
    /// the route between neighbouring points, for the walker's graph (steep slopes excluded, as the bots plan) and the one with them
    /// allowed. It asserts only that the graph reads and that the points are on the mesh at all; read the output (a gate run) to decide
    /// whether the split is the navigation data or the builder.
    /// </summary>
    [RealTerrainFact]
    public void EasternKingdoms_DeadwindRoads_PrintTheirComponents_WithAndWithoutTheSteepSlopes_Issue1481()
    {
        var finder = new NavMeshPathfinder(Mmaps);
        (string Name, NavTerrain Exclude)[] modes = [("steep slopes excluded", NavTerrain.SteepSlopes), ("steep slopes allowed", NavTerrain.Empty)];
        (string Name, (string Label, Vector3 At)[] Points)[] roads = [("Deadwind-Duskwood road", DuskwoodRoad), ("Deadwind-Swamp of Sorrows road", SwampRoad)];
        foreach ((string name, NavTerrain exclude) in modes)
        {
            var watch = Stopwatch.StartNew();
            NavRegionGraph graph = NavRegionGraph.Build(Mmaps, 0, PathMover.Player.IncludeFlags, exclude);
            output.WriteLine($"== {name}: {graph.TileCount} tiles, {graph.PolygonCount} polygons, {graph.RegionCount} regions in {watch.ElapsedMilliseconds} ms");
            Assert.True(graph.TileCount > 400, $"only {graph.TileCount} tiles read");
            PathOptions options = Walking with { ExcludeFlags = exclude };
            int located = 0;
            foreach (var (road, points) in roads)
            {
                output.WriteLine($"-- {road}");
                int previous = -1;
                Vector3 previousAt = default;
                foreach ((string label, Vector3 at) in points)
                {
                    int region = -1;
                    if (TerrainTile.TileOf(at.X, at.Y) is { } tile && finder.LoadTile(graph.MapId, tile.X, tile.Y)) region = finder.RegionAt(graph, at, options);
                    if (region >= 0) located++;
                    output.WriteLine($"{label} {at}: region {region}, component {graph.ComponentOf(region)}, {graph.ComponentSize(region)} regions, {graph.ComponentPolygons(region)} polygons");
                    if (previous >= 0 && region >= 0)
                    {
                        NavRegionRouteStatus status = graph.FindRoute(previous, previousAt, region, at, 20_000, out List<Vector3> portals, out int settled);
                        output.WriteLine($"   from the point above: {status}, {portals.Count} portals, {settled} settled");
                    }

                    if (region >= 0)
                    {
                        previous = region;
                        previousAt = at;
                    }
                }
            }

            Assert.True(located > 0, $"none of the {DuskwoodRoad.Length + SwampRoad.Length} live bot positions lies on the mesh ({name})");
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
