using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA cluster C3 (the same stall point across bots and goals), the part the region graph explains: a bot on a fragment of mesh no walk
/// leaves (the ridge pocket above the Valley of Trials, the sliver inside Ironforge; real-terrain checks in
/// <c>PlayerbotMeshFragmentTerrainTests</c>), and a goal in a part of the map no walk reaches (Chief Hawkwind's platform from the Durotar
/// ridge). NOT addressed, and still open: the Explore stalls at the ridge (-1.0,-4188.3), Coldridge (-6494.1,324.9) and the Barrens
/// (-1173,-3285) pins, which are on large connected regions where the probes found plans and walks working.
/// <para>
/// The layout is that of <see cref="PlayerbotRegionLegTests"/> (four 40-yard tiles, the bot's <c>A</c> at world X 0-40, Y 0-40) with a
/// pocket: <c>A</c>'s western cells are walkable (the fragment, one region), a gap of unwalkable cells follows, and the rest of <c>A</c>
/// joins its neighbours (the large component, four regions).
/// </para>
/// </summary>
public sealed class PlayerbotMeshFragmentTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 InThePocket = new(5, 5, 0), OnTheLargePart = new(30, 5, 0), Goal = new(75, 5, 0);

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    // Pocket: cells 0-1 of rows 0-5 (X 0-10, Y 0-30: touching no tile border); large part: cells from firstLargeCell (X 5 * firstLargeCell on).
    private static (int X, int Y, CellTile Tile)[] Fragment(int firstLargeCell) =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2], Walkable = (i, j) => i <= 1 && j <= 5 || i >= firstLargeCell }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6] }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
        (11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] }),
    ];

    // The dead-end layout of the region-leg tests (tile B's western 30 yards a hole) without tile D, and with two more tiles north of C
    // so the bot's component is four regions: the goal's tile B is joined to nothing the bot can walk to.
    private static readonly (int X, int Y, CellTile Tile)[] Unjoined =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];
    private static Task<PlayerbotRoute?> PlanAsync(SyntheticNavWorld world, Vector3 goal)
        => world.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(world.Player, goal, Options, out PlayerbotRoute? planned, nowMs: world.Host.World.NowMs) ? planned : null);

    /// <summary>Retried like a bot thinking again: a tile still being read answers "pending" for the first thinks.</summary>
    private static async Task<PlayerbotRoute?> PlanSettledAsync(SyntheticNavWorld world, Vector3 goal)
    {
        PlayerbotRoute? route = null;
        for (int think = 0; think < 40 && route is null; think++)
        {
            route = await PlanAsync(world, goal);
            if (route is null) await world.Host.World.AdvanceClockAsync(50);
        }

        return route;
    }

    private static Task<bool> OnFragmentAsync(SyntheticNavWorld world) => world.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(world.Player));

    /// <summary>
    /// A bot in the pocket gets, for a far goal, the one straight hop across the gap to the large part (the stepped terrain leg, not a
    /// mesh route) instead of the few yards of the pocket; from where it lands it plans as any bot does. Before, it was given the pocket's
    /// own few yards and stood at its edge for good.
    /// </summary>
    [Fact]
    public async Task ABotInThePocket_HopsToTheLargePart_AndPlansFromThere()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Fragment(4), new FloorLineOfSight());
        await world.PlaceAsync(InThePocket);
        Assert.True(await OnFragmentAsync(world));
        PlayerbotRoute? hop = await PlanSettledAsync(world, Goal);
        Assert.NotNull(hop);
        Assert.False(hop!.Navigated, "the hop is a stepped terrain leg");
        Vector3 end = hop.Points[^1];
        Assert.True(end.X >= 17f, $"the hop ends at {end}, not on the large part");
        Assert.True(Vector2.Distance(new(InThePocket.X, InThePocket.Y), new(end.X, end.Y)) <= PlayerbotNavigation.FragmentHopYards + 0.5f);

        await world.PlaceAsync(end);
        Assert.False(await OnFragmentAsync(world));
        PlayerbotRoute? next = await PlanSettledAsync(world, Goal);
        Assert.NotNull(next);
        Assert.True(next!.Navigated);
    }

    /// <summary>The large part farther than the hop reaches: no hop (a refusal or the pocket's own yards), and the bot is a fragment bot for the stall path (the brain's island escape).</summary>
    [Fact]
    public async Task ABotInAPocketTooFarFromTheLargePart_IsNotHopped_ButIsAFragment()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Fragment(7), new FloorLineOfSight());
        await world.PlaceAsync(InThePocket);
        Assert.True(await OnFragmentAsync(world));
        for (int think = 0; think < 10; think++)
        {
            PlayerbotRoute? route = await PlanAsync(world, Goal);
            Assert.True(route is null || route.Points[^1].X < 15f, $"a route ends at {route?.Points[^1]}");
            await world.Host.World.AdvanceClockAsync(50);
        }
    }

    /// <summary>A bot on the large part is no fragment bot: plans as before, no hop.</summary>
    [Fact]
    public async Task ABotOnTheLargePart_PlansAsBefore()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Fragment(4), new FloorLineOfSight());
        await world.PlaceAsync(OnTheLargePart);
        Assert.False(await OnFragmentAsync(world));
        PlayerbotRoute? route = await PlanSettledAsync(world, Goal);
        Assert.NotNull(route);
        Assert.True(route!.Navigated);
    }

    [Fact]
    public async Task TheComponents_OfThePocketAndTheLargePart_AreCounted()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Fragment(4), new FloorLineOfSight());
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true };
        int pocket = world.Nav.RegionAt(graph, InThePocket, locate), large = world.Nav.RegionAt(graph, OnTheLargePart, locate);
        Assert.True(pocket >= 0 && large >= 0);
        Assert.Equal(1, graph.ComponentSize(pocket));
        Assert.True(graph.ComponentSize(large) > PlayerbotNavigation.FragmentRegions, $"{graph.ComponentSize(large)} regions");
        Assert.NotEqual(graph.ComponentOf(pocket), graph.ComponentOf(large));
        Assert.Equal(-1, graph.ComponentOf(-1));
        Assert.Equal(0, graph.ComponentSize(graph.RegionCount));
    }

    /// <summary>A goal in a part of the map no walk reaches is told so from the region graph alone (no path search); a reachable one is not.</summary>
    [Fact]
    public async Task AGoalNoWalkReaches_IsToldUnreachable_WithNoPathSearch()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Unjoined);
        await world.PlaceAsync(InThePocket);
        Vector3 north = new(20, 70, 0);
        bool unreachable = false, reachable = true;
        for (int think = 0; think < 40 && !unreachable; think++)
        {
            (unreachable, reachable, long searches) = await world.Host.OnWorldAsync(() =>
            {
                long before = PathSearchCounters.Current.Searches;
                bool far = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, Goal, out _, out _), near = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, north, out _, out _);
                return (far, near, PathSearchCounters.Current.Searches - before);
            });
            Assert.Equal(0, searches);
            if (!unreachable) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(unreachable);
        Assert.False(reachable);
    }

    // The dead-end layout with tile B's western 30 yards walkable as steep slopes only: the slope-excluded graph still splits the goal off,
    // the all-terrain graph joins it (stairs).
    private static readonly (int X, int Y, CellTile Tile)[] SteepStairs =
    [
        Unjoined[0],
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5)
        {
            LinkedSides = [0, 6],
            Flags = (i, _) => i < 6 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
        }),
        Unjoined[2], Unjoined[3], Unjoined[4],
    ];

    /// <summary>The same goal indoors (a WMO group) reached over steep stairs: the climb may reach it, so it is not skipped (a sealed room is: <c>PlayerbotSharedPinsTests</c>).</summary>
    [Fact]
    public async Task AnIndoorGoalOnlySteepStairsReach_IsNotSkipped()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, SteepStairs, new IndoorsWhere(at => at.X > 40));
        await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.Empty)!;
        await world.PlaceAsync(InThePocket);
        bool split = false;
        for (int think = 0; think < 80 && !split; think++)
        {
            split = await world.Host.OnWorldAsync(() =>
            {
                PlayerbotNavigation.IsUnreachableOnFoot(world.Player, Goal, out int from, out int to);
                return from >= 0 && to >= 0 && from != to;
            });
            if (!split) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(split, "the slope-excluded graph splits the stairs off (control)");
        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, Goal, out _, out _)));
    }

    /// <summary>
    /// A caller that plans a leg toward a goal no walk reaches (any caller but the world destinations, which skip it) gets the leg it
    /// always got: the skip is a query of its own, not a refusal in the planner.
    /// </summary>
    [Fact]
    public async Task APlanTowardAnUnreachableGoal_IsUnchanged()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Unjoined);
        await world.PlaceAsync(InThePocket);
        PlayerbotRoute? route = await PlanSettledAsync(world, Goal);
        Assert.NotNull(route);
        Assert.True(route!.Navigated);
        Assert.False(await OnFragmentAsync(world));
    }

    private static async Task<bool> UnreachableAsync(SyntheticNavWorld world, Vector3 goal)
        => await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, goal, out _, out _));

    /// <summary>
    /// Only a bot on a large walkable part may call a goal unreachable: the same outdoor goal, no walk joining it, is told unreachable from
    /// the large part (the control: it answers true once the graph and the tiles are ready) and never from a fragment, so PlanToward's
    /// escapes (the hop, the bind-point stall path) run for the fragment bot instead of the goal being dropped.
    /// </summary>
    [Fact]
    public async Task AFragmentBot_IsNeverToldAnOutdoorGoalIsUnreachable()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Fragment(4), new FloorLineOfSight());
        await world.PlaceAsync(OnTheLargePart);
        bool control = false;
        for (int think = 0; think < 40 && !control; think++)
        {
            control = await UnreachableAsync(world, InThePocket); // the pocket is joined to nothing the large part walks to
            if (!control) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(control, "from the large part the pocket is unreachable (control)");
        await world.PlaceAsync(InThePocket);
        Assert.True(await OnFragmentAsync(world));
        for (int think = 0; think < 10; think++)
        {
            Assert.False(await UnreachableAsync(world, Goal), "a fragment bot is not told its goal is unreachable");
            await world.Host.World.AdvanceClockAsync(50);
        }
    }

    /// <summary>A bot on a floor of a building (indoors) is not told an outdoor goal is unreachable: its own part says nothing about the map.</summary>
    [Fact]
    public async Task AnIndoorBot_IsNeverToldAnOutdoorGoalIsUnreachable()
    {
        bool botIndoors = false;
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Unjoined,
            new IndoorsWhere(at => Volatile.Read(ref botIndoors) && at.X < 40));
        await world.PlaceAsync(InThePocket);
        bool control = false;
        for (int think = 0; think < 40 && !control; think++)
        {
            control = await UnreachableAsync(world, Goal);
            if (!control) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(control, "outdoors, the same bot and goal are unreachable (control)");
        Volatile.Write(ref botIndoors, true);
        for (int think = 0; think < 10; think++)
        {
            Assert.False(await UnreachableAsync(world, Goal));
            await world.Host.World.AdvanceClockAsync(50);
        }
    }

    /// <summary>A plaza of a few regions but many polygons is a real place, not a fragment (<see cref="PlayerbotNavigation.FragmentPolygons"/>).</summary>
    [Fact]
    public async Task APlazaOfFewRegionsAndManyPolygons_IsNotAFragment()
    {
        (int X, int Y, CellTile Tile)[] plaza = [(10, 10, new CellTile(0, 0, 0, 0, 30, 30, Size: 1.3f))];
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, plaza, new FloorLineOfSight());
        Vector3 middle = new(20, 20, 0);
        await world.PlaceAsync(middle);
        NavRegionGraph graph = (await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!)!;
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true };
        int region = -1;
        for (int think = 0; think < 40 && region < 0; think++)
        {
            region = world.Nav.RegionAt(graph, middle, locate);
            if (region < 0) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(region >= 0);
        Assert.True(graph.ComponentSize(region) <= PlayerbotNavigation.FragmentRegions, $"{graph.ComponentSize(region)} regions");
        Assert.True(graph.ComponentPolygons(region) > PlayerbotNavigation.FragmentPolygons, $"{graph.ComponentPolygons(region)} polygons");
        Assert.False(await OnFragmentAsync(world));
    }

    /// <summary>A set-aside for an unreachable goal suspends the quest but is no step towards abandoning it (<see cref="PlayerbotSuspensions.MaxReturnSetAsides"/>).</summary>
    [Fact]
    public void AnUnreachableGoalSetAside_DoesNotFeedTheAbandonCounter()
    {
        var suspensions = new PlayerbotSuspensions();
        for (int i = 0; i < 2 * PlayerbotSuspensions.MaxReturnSetAsides; i++) suspensions.SuspendQuest(77, 1000, countsAsSetAside: false);
        Assert.True(suspensions.IsQuestSuspended(77, 2000));
        Assert.Equal(0, suspensions.QuestSetAsideCount(77));
        suspensions.SuspendQuest(77, 1000);
        Assert.Equal(1, suspensions.QuestSetAsideCount(77)); // an ordinary set-aside still counts
    }
}

/// <summary>A line-of-sight stand-in with no models in the way and a model floor at height 0 everywhere (the synthetic mesh sits at 0), so stepped terrain legs can be tested.</summary>
internal sealed class FloorLineOfSight : ILineOfSight
{
    public bool Enabled => true;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        return false;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 0f;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = default;
        return false;
    }
}
