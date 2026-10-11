using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The live-w22-r1 regression in the fixture world (docs/areas/playbots.md, "Travel by the Deeprun Tram"): a Westfall bot 1,936 yards
/// (straight) from the Stormwind tram entrance, whose way there is longer than <see cref="PlayerbotOptions.MaxRouteYards"/> and ends on
/// the floor 4.8 yards under the trigger's centre, as the real navigation mesh answers it (incomplete, 2,314 yards,
/// <c>PlayerbotTramLiveRegressionTests</c>). Before, that answer was refused whole: the bot never moved and gave the trip up after a
/// minute with "tram: the trip failed" and no reason.
/// </summary>
public sealed class PlayerbotTramFarStartTests
{
    /// <summary>Wfbotak at the start of live-w22-r1 (Sentinel Hill).</summary>
    private static readonly Vector3 Westfall = new(-10193.776f, 1089.2113f, TramTestData.StormwindFloor);

    private static readonly Vector3 Entrance = new(-8346.46f, 514.031f, 96.5989f);

    private static readonly Vector3 IronforgeGoal = new(-4918.9f, -982.5f, 501.5f);

    private static PlayerbotOptions LiveOptions() => new()
    {
        Enabled = true, ThinkIntervalMs = 100, MaxActionsPerTick = 12, MaxPathPoints = 128, MaxRouteYards = 2000, MoveSpeed = 7,
    };

    [Fact]
    public async Task AWayLongerThanTheRouteBound_EndingUnderTheTriggersCentre_IsWalked()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new RoadUnderTheTrigger());
        await world.PlaceAsync(0, Westfall);
        PlayerbotOptions options = LiveOptions();
        float routeLength = await world.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, Entrance, options, out PlayerbotRoute? route) ? route!.Distance : -1f);
        Assert.InRange(routeLength, 1500f, options.MaxRouteYards); // the first part of the way, cut at the route bound

        var brain = new PlayerbotBrain(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        for (int step = 0; step < 700; step++) await world.TickAsync(() => { brain.Update(100); return true; }); // 70 s: past the 60 s progress bound
        (TramStage? stage, float walked) = await world.OnWorldAsync(() => (PlayerbotTram.TripOf(world.Player)?.Stage,
            Vector2.Distance(new(world.Player.X, world.Player.Y), new(Westfall.X, Westfall.Y))));
        Assert.Equal(TramStage.ToEntrance, stage);
        Assert.True(walked > 300f, $"walked only {walked:F0} yards in 70 s");
    }

    [Fact]
    public async Task ATripThatFindsNoWay_FailsWithItsReason()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new NoWay());
        await world.PlaceAsync(0, Westfall);
        var brain = new PlayerbotBrain(world.Session, LiveOptions());
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player, IronforgeGoal, TramTestData.ZoneAt)));
        string? stall = null;
        for (int step = 0; step < 900 && stall is null; step++)
            stall = await world.TickAsync(() =>
            {
                int before = brain.StallCount;
                brain.Update(100);
                return brain.StallCount != before ? brain.StallReport : null;
            });

        Assert.NotNull(stall);
        Assert.StartsWith("tram: no way to the Deeprun Tram - Stormwind Instance entrance from", stall);
    }

    /// <summary>
    /// The arrival rule's height bound (<see cref="PlayerbotNavigation.ProjectedEndYards"/>): a way longer than the route bound whose
    /// incomplete end lies within a yard of the destination flat but 10 yards under it (a search that stopped short below a level the bot
    /// cannot reach) is no arrival and is refused whole, by the partial planner and by an exact one (TryPlan); 4.75 yards under it (the
    /// tram entrance's floor) is one, for both.
    /// </summary>
    [Theory]
    [InlineData(4.75f, true, true)]
    [InlineData(4.75f, false, true)]
    [InlineData(10f, true, false)]
    [InlineData(10f, false, false)]
    public async Task AnIncompleteWayEndingUnderTheDestination_ReachesItOnlyWithinTheHeightBound(float below, bool partialPlanner, bool accepted)
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new RoadUnderTheTrigger(below, incompleteAlways: true));
        await world.PlaceAsync(0, Westfall);
        PlayerbotOptions options = LiveOptions();
        float length = await world.OnWorldAsync(() =>
            (partialPlanner
                ? PlayerbotNavigation.TryPlanToward(world.Player, Entrance, options, out PlayerbotRoute? route)
                : PlayerbotNavigation.TryPlan(world.Player, Entrance, options, out route)) ? route!.Distance : -1f);
        if (accepted) Assert.InRange(length, 1500f, options.MaxRouteYards);
        else Assert.Equal(-1f, length);
    }

    /// <summary>
    /// The mesh's answer to the entrance: a road bending 600 yards south (2,330 yards), ending <paramref name="below"/> yards under the
    /// trigger's centre; incomplete for a partial query (an exact one gets no path), or always (<paramref name="incompleteAlways"/>).
    /// </summary>
    private sealed class RoadUnderTheTrigger(float below = 4.75f, bool incompleteAlways = false) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Vector2.Distance(new(end.X, end.Y), new(Entrance.X, Entrance.Y)) > 1f) return PathResult.StraightLine(start, end, PathType.Normal);
            Vector3 floor = end with { Z = end.Z - below };
            Vector3 bend = Vector3.Lerp(start, floor, 0.5f) + new Vector3(0, -600f, 0);
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++) points.Add(Vector3.Lerp(start, bend, i / 20f));
            for (int i = 1; i <= 20; i++) points.Add(Vector3.Lerp(bend, floor, i / 20f));
            bool incomplete = incompleteAlways || (options?.AllowPartial ?? false);
            return new PathResult(incomplete ? PathType.Incomplete : PathType.NoPath, incomplete ? points : [start, end]);
        }
    }

    private sealed class NoWay : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }
}
