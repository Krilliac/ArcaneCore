using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The leg <see cref="PlayerbotNavigation.TryPlanToward"/> chooses towards a far goal on a repository-authored mesh
/// (<see cref="NavMeshFixture"/>, the layout of <c>NavRegionGraphTests</c>): four 40-yard tiles, the bot in <c>A</c> (world X 0–40,
/// Y 0–40), the goal in <c>B</c> east of it, whose western 30 yards are a hole; the way goes north through <c>C</c>, east through <c>D</c>
/// and south into <c>B</c>. Routes are bounded to 30 yards (<see cref="PlayerbotOptions.MaxRouteYards"/>) and 22 corners, so the goal is
/// beyond one route from everywhere in <c>A</c>, as Grish Longrunner and Kargath are beyond 2,000 yards from Stonetalon and the swamp.
/// </summary>
public sealed class PlayerbotRegionLegTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 Start = new(5, 5, 0), Goal = new(75, 5, 0);

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    /// <summary>
    /// Each leg goes to a portal of the region graph's way (north first, never into the hole) and the bot reaches the goal. Before, the
    /// next straight-line point 20 yards east of the hole's edge lay off the mesh: no route, and the bot stood at the edge for good.
    /// </summary>
    [Fact]
    public async Task AFarGoalBehindADeadEnd_IsReachedLegByLeg_RoundIt()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(Start);
        var ends = new List<Vector3>();
        for (int leg = 0; leg < 12 && (ends.Count == 0 || Vector3.Distance(ends[^1], Goal) > 0.5f); leg++)
        {
            PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
                PlayerbotNavigation.TryPlanToward(world.Player, Goal, Options, out PlayerbotRoute? planned) ? planned : null);
            Assert.True(route is not null, $"no route towards the goal after legs ending {string.Join(" ", ends)}");
            Assert.True(route!.Distance <= Options.MaxRouteYards + 0.01f, $"a {route.Distance:F1}-yard route");
            ends.Add(route.Points[^1]);
            Assert.False(ends[^1].X is > 40 and < 70 && ends[^1].Y < 40, $"a leg ends in the hole at {ends[^1]}");
            await world.PlaceAsync(ends[^1]);
        }

        Assert.True(Vector3.Distance(ends[^1], Goal) <= 0.5f, $"the legs end {string.Join(" ", ends)}");
        Assert.True(ends[0].Y - Start.Y > ends[0].X - Start.X, $"the first leg ends at {ends[0]}, not north towards C");
        Assert.Contains(ends, end => end.Y > 40f && end.X > 40f); // through D
    }

    /// <summary>
    /// The leg ends at the farthest point of the way within half a route (<see cref="PlayerbotNavigation.RegionLegYards"/> at most),
    /// measured along it: from <c>C</c> the portal into <c>D</c> is 28.3 yards on, the one into <c>B</c> 68.3 and the goal 102.3.
    /// </summary>
    [Theory]
    [InlineData(120f, 41f, 60f)] // half a route 60: the portal into D
    [InlineData(140f, 75f, 39f)] // 70: the portal into B
    [InlineData(220f, 75f, 5f)] // 110: the goal itself
    public async Task ALeg_EndsAtTheFarthestPointOfTheWay_WithinHalfARoute(float maxRouteYards, float x, float y)
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(new Vector3(20, 41, 0));
        var options = new PlayerbotOptions { MaxRouteYards = maxRouteYards, MaxPathPoints = 22 };
        PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, Goal, options, out PlayerbotRoute? planned) ? planned : null);

        Assert.NotNull(route);
        Vector3 end = route!.Points[^1];
        Assert.True(Vector2.Distance(new(end.X, end.Y), new(x, y)) < 0.5f, $"the leg ends at {end}");
    }

    /// <summary>
    /// A goal a few yards away, inside one route chunk, whose straight line crosses a wall. The only mesh connection is a door at the far
    /// end of the wall, so the polygon search spends its node budget on the open ground beside the wall and answers nothing that closes.
    /// The two sides are still one component of the region graph (bot QA #810: same component, a few dozen yards, every search refused).
    /// The plan must take the graph's door instead of refusing.
    /// </summary>
    [Fact]
    public async Task ANearGoalBehindAWall_UsesTheRegionGraph_InsteadOfRefusing()
    {
        const int cells = 70;
        const float size = 2f;
        int door = cells - 4;
        var blocked = new CellTile(0, 0, 0, 0, cells, cells, size)
        {
            LinkedSides = [2],
            Walkable = (i, j) => i < cells - 1 || j >= door,
        };
        var beyond = new CellTile(0, 1, cells * size, 0, 6, cells, size) { LinkedSides = [6] };
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, [(10, 10, blocked), (10, 11, beyond)]);
        Vector3 start = new(((cells - 2) * size) + 1f, 4f, 0f);
        Vector3 goal = new((cells * size) + 4f, 4f, 0f);
        await world.PlaceAsync(start);
        var options = new PlayerbotOptions { MaxRouteYards = 2000f, MaxPathPoints = 128 };
        PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, goal, options, out PlayerbotRoute? planned) ? planned : null);

        Assert.True(route is not null, "the region graph joins the two sides but the plan was refused");
        Vector3 end = route!.Points[^1];
        Assert.True(end.Y > start.Y + 30f, $"the route ends at {end}, against the wall instead of toward the door");
    }

    /// <summary>
    /// A far goal planned through a waiting wrapper: the region search is a miss, runs off the think (the plan is pending), and the leg is
    /// planned on a later think from the finished answer. The next leg between the same regions is a cache hit: no search.
    /// </summary>
    [Fact]
    public async Task ARegionRouteMiss_RunsOffTheThink_AndTheNextLegIsACacheHit()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(Start);
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        Assert.Equal(0, cache.Searches);

        var wait = new TileWait();
        uint now = 1_000;
        PlayerbotRoute? route = null;
        PlanOutcome outcome = await world.Host.OnWorldAsync(() => PlayerbotNavigation.PlanToward(world.Player, Goal, Options, ref wait, now, out route));
        Assert.Equal(PlanOutcome.Pending, outcome);
        Assert.Null(route);

        for (int tries = 0; tries < 400 && outcome == PlanOutcome.Pending; tries++)
        {
            await Task.Delay(25);
            now += 25;
            outcome = await world.Host.OnWorldAsync(() => PlayerbotNavigation.PlanToward(world.Player, Goal, Options, ref wait, now, out route));
        }

        Assert.Equal(PlanOutcome.Route, outcome);
        Assert.NotNull(route);
        Assert.Equal(1, cache.Searches);
        Assert.Equal(1, cache.Count);

        // The same pair again: a hit, answered on the first ask.
        Assert.Equal(PlanOutcome.Route, await world.Host.OnWorldAsync(() => PlayerbotNavigation.PlanToward(world.Player, Goal, Options, ref wait, now + 25, out route)));
        Assert.Equal(1, cache.Searches);
    }

    /// <summary>
    /// A region route that stays unanswered (here: marked in flight by a search that was lost) is not waited for for ever: past the caller's
    /// wait bound the ask is searched on the asking thread and answered.
    /// </summary>
    [Fact]
    public async Task AnUnansweredRegionRoute_IsSearchedOnTheAskingThread_PastTheWaitBound()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = world.Nav.RegionAt(graph, Start, locate), to = world.Nav.RegionAt(graph, Goal, locate);
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        cache.MarkAsked(from, to, 1_000); // the pool never answers
        var budget = new PlayerbotRegionRoutes.SyncSearchBudget();

        Assert.False(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to, Goal, mayDefer: true, out _, out _, wallMs: 1_000, maxWaitMs: 30_000, budget: budget));
        Assert.False(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to, Goal, mayDefer: true, out _, out _, wallMs: 30_999, maxWaitMs: 30_000, budget: budget));
        Assert.Equal(0, cache.Searches);
        Assert.True(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to, Goal, mayDefer: true, out NavRegionRouteStatus status, out _, wallMs: 31_001, maxWaitMs: 30_000, budget: budget));
        Assert.NotEqual(NavRegionRouteStatus.Exhausted, status);
        Assert.Equal(1, cache.Searches);
    }

    /// <summary>
    /// The searches the asking thread runs for lost work are bounded: one per window of <see cref="PlayerbotRegionRoutes.SyncSearchBudget.WindowMs"/>.
    /// A second lost pair waits for the next window.
    /// </summary>
    [Fact]
    public async Task TheSyncFallback_IsLimitedToOneSearchPerWindow()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = world.Nav.RegionAt(graph, Start, locate), to = world.Nav.RegionAt(graph, Goal, locate);
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        int to2 = to + 1000, to3 = to + 1001; // no such region: FindRoute range-checks only the from region, so the search settles the start's component, then answers Unreachable
        cache.MarkAsked(from, to2, 0);
        cache.MarkAsked(from, to3, 0);
        var budget = new PlayerbotRegionRoutes.SyncSearchBudget();

        Assert.True(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to2, Goal, mayDefer: true, out _, out _, wallMs: 40_000, maxWaitMs: 30_000, budget: budget));
        Assert.Equal(1, cache.Searches);
        Assert.False(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to3, Goal, mayDefer: true, out _, out _, wallMs: 40_010, maxWaitMs: 30_000, budget: budget));
        Assert.Equal(1, cache.Searches);
        Assert.True(PlayerbotRegionRoutes.TryRoute(graph, from, Start, to3, Goal, mayDefer: true, out _, out _, wallMs: 40_060, maxWaitMs: 30_000, budget: budget));
        Assert.Equal(2, cache.Searches);
    }

    /// <summary>
    /// The state of deferred asks is bounded and drains: many distinct pairs are queued, searched by the workers, and leave nothing asked
    /// or queued behind; a stale mark is cleared by the search, and the ask of a pair that is no longer cached is queued, not searched at once.
    /// </summary>
    [Fact]
    public async Task DeferredAsks_DrainToNothing_AndALostMarkIsClearedBySearching()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = world.Nav.RegionAt(graph, Start, locate);
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        var budget = new PlayerbotRegionRoutes.SyncSearchBudget();

        for (int i = 0; i < 300; i++)
            PlayerbotRegionRoutes.TryRoute(graph, from, Start, 1000 + i, Goal, mayDefer: true, out _, out _, wallMs: 0, budget: budget);
        // Only this graph's own counts: the queue and the worker count are process-wide and other tests' bots use them at the same time.
        for (int tries = 0; tries < 400 && cache.AskedCount > 0; tries++)
            await Task.Delay(25);

        Assert.Equal(0, cache.AskedCount);
        Assert.Equal(300, cache.Count);
        Assert.Equal(300, cache.Searches);

        cache.MarkAsked(from, 5000, 0);
        Assert.True(PlayerbotRegionRoutes.TryRoute(graph, from, Start, 5000, Goal, mayDefer: false, out _, out _));
        Assert.Equal(0, cache.AskedCount);

        // Evict pair 1000 (the oldest) by storing MaxEntries more pairs: bounded, and its next deferred ask is queued, not answered on this thread.
        for (int extra = 0; extra < PlayerbotRegionRoutes.MaxEntries; extra++) cache.Search(from, Start, 6000 + extra, Goal);
        Assert.True(cache.Count <= PlayerbotRegionRoutes.MaxEntries);
        Assert.False(cache.TryGet(from, 1000, Start, out _, out _));
        Assert.False(PlayerbotRegionRoutes.TryRoute(graph, from, Start, 1000, Goal, mayDefer: true, out _, out _, wallMs: 10_000_000, maxWaitMs: 30_000, budget: budget));
        for (int tries = 0; tries < 400 && cache.AskedCount > 0; tries++) await Task.Delay(25);
        Assert.Equal(0, cache.AskedCount);
        Assert.True(cache.TryGet(from, 1000, Start, out _, out _));
    }

    /// <summary>A rebuilt graph is a new instance: it starts with an empty cache, and the old graph's routes never answer for it.</summary>
    [Fact]
    public async Task ARebuiltGraph_DoesNotSeeTheOldGraphsRoutes_AndTheCacheIsBounded()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        NavRegionGraph first = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        NavRegionGraph rebuilt = NavRegionGraph.Build(_fixture.Directory, 0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        Assert.NotSame(first, rebuilt);
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        int from = world.Nav.RegionAt(first, Start, locate), to = world.Nav.RegionAt(first, Goal, locate);
        Assert.NotEqual(from, to);

        Assert.True(PlayerbotRegionRoutes.TryRoute(first, from, Start, to, Goal, mayDefer: false, out NavRegionRouteStatus status, out List<Vector3> portals));
        Assert.Equal(1, PlayerbotRegionRoutes.Of(first).Searches);
        Assert.True(PlayerbotRegionRoutes.TryRoute(first, from, Start, to, Goal, mayDefer: true, out NavRegionRouteStatus again, out List<Vector3> hit));
        Assert.Equal(1, PlayerbotRegionRoutes.Of(first).Searches);
        Assert.Equal(status, again);
        Assert.Equal(portals, hit);

        Assert.Equal(0, PlayerbotRegionRoutes.Of(rebuilt).Count);
        Assert.True(PlayerbotRegionRoutes.TryRoute(rebuilt, from, Start, to, Goal, mayDefer: false, out _, out _));
        Assert.Equal(1, PlayerbotRegionRoutes.Of(rebuilt).Searches);

        // Bounded: more pairs than MaxEntries keep MaxEntries.
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(rebuilt);
        for (int extra = 0; extra < PlayerbotRegionRoutes.MaxEntries + 20; extra++)
            cache.Search(from, Start, extra + 1000, Goal);
        Assert.True(cache.Count <= PlayerbotRegionRoutes.MaxEntries);
    }

    // The layout of NavRegionGraphTests: Detour tile (x, y) covers world Y from 40x and world X from 40y.
    private static readonly (int X, int Y, CellTile Tile)[] Layout =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
        (11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] }),
    ];
}
