using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="NavRegionGraph"/> on repository-authored meshes (<see cref="NavMeshFixture"/>): regions are the parts of a tile its own
/// links join, regions of adjacent tiles meet at portals where the tiles' border edges meet, and the region search finds the way round a
/// dead end that the straight line runs into.
/// <para>
/// The layout of four 40-yard tiles: <c>A</c> (world X 0–40, Y 0–40), <c>B</c> east of it (X 40–80), <c>C</c> north of <c>A</c> (Y 40–80)
/// and <c>D</c> north of <c>B</c>. <c>B</c> is walkable only in its eastern 10 yards, so nothing joins <c>A</c> to <c>B</c> directly: the
/// way from <c>A</c> to <c>B</c> goes north through <c>C</c>, east through <c>D</c> and south into <c>B</c>.
/// </para>
/// </summary>
public sealed class NavRegionGraphTests : IDisposable
{
    private const uint MapId = 0;
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // Detour tile (x, y): Recast x is world Y, Recast z is world X. Sides: 0 world +Y, 2 world +X, 4 world -Y, 6 world -X.
    private static readonly CellTile A = new(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] };
    private static readonly CellTile B = new(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 };
    private static readonly CellTile C = new(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] };
    private static readonly CellTile D = new(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] };

    private NavMeshPathfinder Write(params (int X, int Y, CellTile Tile)[] tiles)
    {
        _fixture.WriteParams(MapId);
        foreach ((int x, int y, CellTile tile) in tiles)
        {
            _fixture.WriteTile(MapId, x, y, tile);
        }

        var pathfinder = new NavMeshPathfinder(_fixture.Directory);
        foreach ((int x, int y, _) in tiles)
        {
            Assert.True(pathfinder.LoadTile(MapId, x, y));
        }

        return pathfinder;
    }

    private NavMeshPathfinder WriteLayout() => Write((10, 10, A), (10, 11, B), (11, 10, C), (11, 11, D));

    private static readonly PathOptions Walking = new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes };

    private static NavRegionGraph Graph(string directory) => NavRegionGraph.Build(directory, MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);

    [Fact]
    public void EachTile_IsOneRegion_AndTheirBordersMeetAtPortals_BothWays()
    {
        NavMeshPathfinder nav = WriteLayout();
        NavRegionGraph graph = Graph(_fixture.Directory);

        Assert.Equal(4, graph.TileCount);
        Assert.Equal(4, graph.RegionCount);
        Assert.Equal((64 * 3) + 16, graph.PolygonCount);
        // A–C, C–D and D–B meet (one portal each, the borders are shorter than MaxPortalYards); A–B do not: B's west is a hole.
        Assert.Equal(6, graph.LinkCount);
        int a = nav.RegionAt(graph, new Vector3(5, 5, 0), Walking), b = nav.RegionAt(graph, new Vector3(75, 5, 0), Walking);
        int c = nav.RegionAt(graph, new Vector3(5, 75, 0), Walking), d = nav.RegionAt(graph, new Vector3(75, 75, 0), Walking);
        Assert.Equal(4, new[] { a, b, c, d }.Distinct().Count());
        Assert.All(new[] { a, b, c, d }, region => Assert.InRange(region, 0, 3));
        Assert.Equal(-1, nav.RegionAt(graph, new Vector3(50, 5, 0), Walking)); // in B's hole, 10 yards from any polygon
    }

    [Fact]
    public void TheWayFromAToB_GoesRoundTheDeadEnd_ThroughCAndD()
    {
        NavMeshPathfinder nav = WriteLayout();
        NavRegionGraph graph = Graph(_fixture.Directory);
        Vector3 from = new(5, 5, 0), to = new(75, 5, 0);

        NavRegionRouteStatus status = graph.FindRoute(nav.RegionAt(graph, from, Walking), from, nav.RegionAt(graph, to, Walking), to, 100, out List<Vector3> portals);

        Assert.Equal(NavRegionRouteStatus.Found, status);
        Assert.Equal(3, portals.Count);
        // Each portal lies on the middle of the shared border, PortalInsetYards inside the region it enters.
        Near(new Vector3(20, 40 + NavRegionGraph.PortalInsetYards, 0), portals[0]); // A to C: border Y 40, X 0-40
        Near(new Vector3(40 + NavRegionGraph.PortalInsetYards, 60, 0), portals[1]); // C to D: border X 40, Y 40-80
        Near(new Vector3(75, 40 - NavRegionGraph.PortalInsetYards, 0), portals[2]); // D to B: border Y 40, X 70-80 (B's walkable part)
    }

    [Fact]
    public void OneRegion_HasNoPortals_AndAnUnjoinedGoal_IsUnreachable_WithTheWayToThePortalNearestIt()
    {
        // Without D, B is joined to nothing.
        NavMeshPathfinder nav = Write((10, 10, A), (10, 11, B), (11, 10, C));
        NavRegionGraph graph = Graph(_fixture.Directory);
        Vector3 from = new(5, 5, 0), near = new(35, 35, 0), to = new(75, 5, 0);
        int a = nav.RegionAt(graph, from, Walking);

        Assert.Equal(NavRegionRouteStatus.Found, graph.FindRoute(a, from, nav.RegionAt(graph, near, Walking), near, 100, out List<Vector3> none));
        Assert.Empty(none);

        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, from, nav.RegionAt(graph, to, Walking), to, 100, out List<Vector3> partial));
        // A's only portal, into C, is nearer to the goal than the start (65.7 against 70 yards): the way leads there. C's portal back
        // into A is nearer still (64.7), but only returns to where the search began.
        Near(new Vector3(20, 40 + NavRegionGraph.PortalInsetYards, 0), Assert.Single(partial));
        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, from, -1, to, 100, out _));
    }

    /// <summary>
    /// With <c>nearestFromAnywhere</c> the way to the portal nearest the goal is given even for a start nearer the goal than any portal
    /// (the default overload answers an empty way there), and it is the same way from a far start.
    /// </summary>
    [Fact]
    public void NearestFromAnywhere_GivesTheNearestPortal_ForAStartNearerTheGoalThanAnyPortal()
    {
        NavMeshPathfinder nav = Write((10, 10, A), (10, 11, B), (11, 10, C));
        NavRegionGraph graph = Graph(_fixture.Directory);
        Vector3 nearStart = new(37, 5, 0), farStart = new(2, 5, 0), to = new(75, 5, 0);
        int a = nav.RegionAt(graph, nearStart, Walking), goalRegion = nav.RegionAt(graph, to, Walking);

        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, nearStart, goalRegion, to, 100, out List<Vector3> byDefault));
        Assert.Empty(byDefault);

        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, nearStart, goalRegion, to, 100, nearestFromAnywhere: true, out List<Vector3> nearWay, out _));
        Near(new Vector3(20, 40 + NavRegionGraph.PortalInsetYards, 0), Assert.Single(nearWay));

        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, farStart, goalRegion, to, 100, nearestFromAnywhere: true, out List<Vector3> farWay, out _));
        Assert.Equal(nearWay, farWay);
        // The default overload from the far start is the same as the new one: the flag only lifts the start's threshold.
        Assert.Equal(NavRegionRouteStatus.Unreachable, graph.FindRoute(a, farStart, goalRegion, to, 100, out List<Vector3> farDefault));
        Assert.Equal(farWay, farDefault);
    }

    [Fact]
    public void TheSearchStops_AtItsNodeBudget()
    {
        NavMeshPathfinder nav = WriteLayout();
        NavRegionGraph graph = Graph(_fixture.Directory);
        Vector3 from = new(5, 5, 0), to = new(75, 5, 0);

        NavRegionRouteStatus status = graph.FindRoute(nav.RegionAt(graph, from, Walking), from, nav.RegionAt(graph, to, Walking), to, 1, out List<Vector3> portals);

        Assert.Equal(NavRegionRouteStatus.Exhausted, status);
        Assert.Single(portals);
    }

    [Fact]
    public void SteepPolygons_SplitARegion_WhenExcluded()
    {
        // A tile with a steep row across it (cells j == 4): two regions for a walker, one when steep slopes are allowed.
        var split = new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { Flags = (_, j) => j == 4 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground };
        Write((10, 10, split));

        NavRegionGraph walking = Graph(_fixture.Directory);
        NavRegionGraph climbing = NavRegionGraph.Build(_fixture.Directory, MapId, PathMover.Player.IncludeFlags, NavTerrain.Empty);

        Assert.Equal(2, walking.RegionCount);
        Assert.Equal(56, walking.PolygonCount);
        Assert.Equal(1, climbing.RegionCount);
        Assert.Equal(64, climbing.PolygonCount);
    }

    [Fact]
    public async Task ThePathfinder_BuildsTheGraphOnce_AndHasNoneForAMapWithoutANavmesh()
    {
        NavMeshPathfinder nav = WriteLayout();

        Task<NavRegionGraph?>? build = nav.RegionGraphTask(MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        Assert.NotNull(build);
        NavRegionGraph? built = await build;

        Assert.NotNull(built);
        Assert.Same(built, nav.RegionGraph(MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes));
        Assert.Same(build, nav.RegionGraphTask(MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)); // built once
        Assert.Null(nav.RegionGraphTask(MapId + 1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)); // no navmesh for map 1
        Assert.Null(nav.RegionGraph(MapId + 1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes));
    }

    private sealed class LineLog : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static long SkippedTileFaults()
        => ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot().Where(f => f.Site == "mmaps.regiongraph").Sum(f => f.Count);

    [Fact]
    public void ACorruptTile_IsListedAsSkipped_WithItsCoordinatesAndFile_AndTheRestStillBuild()
    {
        WriteLayout();
        string corrupt = _fixture.PathOf("0001213.mmtile");
        File.WriteAllBytes(corrupt, [1, 2, 3, 4, 5]);

        NavRegionGraph graph = Graph(_fixture.Directory);

        Assert.Equal(4, graph.TileCount);
        SkippedNavTile skipped = Assert.Single(graph.SkippedTiles);
        Assert.Equal(12, skipped.TileX);
        Assert.Equal(13, skipped.TileY);
        Assert.Equal(corrupt, skipped.File);
        Assert.NotNull(skipped.Error);
    }

    [Fact]
    public async Task ThePathfinder_LogsEachSkippedTileOnce_AsAWarning_AndCountsItInTheFaultLedger()
    {
        var log = new LineLog();
        _fixture.WriteParams(MapId);
        _fixture.WriteTile(MapId, 10, 10, A);
        File.WriteAllBytes(_fixture.PathOf("0001213.mmtile"), [9, 9, 9]);
        var nav = new NavMeshPathfinder(_fixture.Directory, log);
        Assert.True(nav.LoadTile(MapId, 10, 10));
        long before = SkippedTileFaults();

        NavRegionGraph? graph = await nav.RegionGraphTask(MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;

        Assert.NotNull(graph);
        var warnings = log.Lines.Where(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning).ToList();
        (_, string message) = Assert.Single(warnings);
        Assert.Contains("(12, 13)", message);
        Assert.Contains("0001213.mmtile", message);
        Assert.True(SkippedTileFaults() >= before + 1);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.01f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
}
