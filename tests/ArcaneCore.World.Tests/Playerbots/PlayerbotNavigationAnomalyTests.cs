using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The outcome checks of navigation planning (<see cref="PlayerbotNavigationAnomalies"/>): a route that ends short of the destination it
/// was asked for or begins away from the bot, a goal replanned over and over from one spot, a goal refused again and again. Each key is
/// proved both ways: the failing plan reports it, the correct plan does not.
/// </summary>
public sealed class PlayerbotNavigationAnomalyTests
{
    private static readonly PlayerbotOptions Options = new() { MaxPathPoints = 128, MaxRouteYards = 2000 };

    private static string Key(string code) => PlayerbotAnomalies.Key("nav", code);

    /// <summary>The mesh answers whatever <c>answer</c> says; a query from a point to itself (the on-mesh probe) is answered "no path".</summary>
    private sealed class Canned(Func<Vector3, Vector3, PathResult> answer) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => start == end ? PathResult.None(start) : answer(start, end);
    }

    private sealed class FlatLos(float z) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private static async Task<bool> OnMesh(RiskTestWorld world, Canned mesh)
        => await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatLos(world.Player.Z), pathfinder: mesh);
            return true;
        });

    private static Vector3 Ahead(Player player, float yards) => new(player.X + yards, player.Y, player.Z);

    // --- nav/path-ends-short ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExactRoute_ThatEndsFarShortOfItsDestination_IsReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end - new Vector3(40f, 0, 0)])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.True(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 100f), Options, out PlayerbotRoute? route) && route is not null);
            Assert.True(cap.Any(Key("path-ends-short")), cap.Describe());
            return true;
        });
    }

    /// <summary>
    /// Bot QA #784 (live 2026-10-17, main 2b009ad2): the mesh locates a destination off the walkable mesh (a cliff foot, a ledge below a
    /// road) on a polygon whose nearest point is 12 yards away on another level, and answers the exact query <see cref="PathType.Incomplete"/>
    /// to there. That is no route to the destination: the bot walked to the wrong level, stood beside it out of reach and planned the same
    /// route again (probe on the real Elwynn mesh: from -9247.0, 448.4, 89.7 to -9235.9, 450.5, 84.4 the route ends at -9229.5, 439.9, 93.2).
    /// </summary>
    [Fact]
    public async Task AnExactPlan_WhoseMeshEndLiesFarFromTheDestination_IsRefused()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Incomplete,
            [start, end + new Vector3(6f, -10f, 9f)])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.False(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 40f), Options, out PlayerbotRoute? route), "an exact plan accepted a route ending 11.7 yd from its destination");
            Assert.Null(route);
            Assert.False(cap.Any(Key("path-ends-short")), cap.Describe());
            return true;
        });
    }

    /// <summary>A destination just off the mesh (an NPC on a crate, a spawn point a step above the floor) still has its exact route: the end is within reach.</summary>
    [Fact]
    public async Task AnExactPlan_WhoseMeshEndLiesWithinReachOfTheDestination_IsKept()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Incomplete,
            [start, end + new Vector3(-2.5f, 2.5f, -1f)])));
        await world.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 40f), Options, out PlayerbotRoute? route) && route is not null);
            return true;
        });
    }

    [Fact]
    public async Task AnExactRoute_ThatReachesItsDestination_IsNotReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.True(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 100f), Options, out PlayerbotRoute? route) && route is not null);
            Assert.False(cap.Any(Key("path-ends-short")), cap.Describe());
            Assert.False(cap.Any(Key("route-start-off")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AnExactRoute_CutAtThePointBound_IsNotReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal | PathType.Short, [start, end - new Vector3(60f, 0, 0)])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.True(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 100f), Options, out PlayerbotRoute? route) && route is not null);
            Assert.False(cap.Any(Key("path-ends-short")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task APartialPlan_ClosingOnAFarGoal_IsNotReportedAsEndingShort()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Incomplete, [start, start + new Vector3(60f, 0, 0)])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.True(PlayerbotNavigation.TryPlanToward(world.Player, Ahead(world.Player, 100f), Options, out PlayerbotRoute? route) && route is not null);
            Assert.False(cap.Any(Key("path-ends-short")), cap.Describe());
            return true;
        });
    }

    // --- nav/route-start-off ------------------------------------------------------------------------------------

    [Fact]
    public async Task ARoute_ThatBeginsFarFromTheBot_IsReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start + new Vector3(0, 50f, 0), end])));
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Assert.True(PlayerbotNavigation.TryPlan(world.Player, Ahead(world.Player, 100f), Options, out PlayerbotRoute? route) && route is not null);
            Assert.True(cap.Any(Key("route-start-off")), cap.Describe());
            return true;
        });
    }

    // --- nav/replan-loop ----------------------------------------------------------------------------------------

    /// <summary>Plans toward one goal <paramref name="times"/> times, <paramref name="gapMs"/> apart on the world clock, the bot moving <paramref name="stepYards"/> between plans.</summary>
    private static async Task PlanRepeatedly(RiskTestWorld world, int times, uint gapMs, float stepYards, bool rooted = false)
    {
        for (int i = 0; i < times; i++)
        {
            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                if (i > 0 && stepYards != 0f) player.Relocate(player.X, player.Y + stepYards, player.Z, player.Orientation, 0);
                if (rooted)
                {
                    var movement = player.Movement;
                    movement.Flags |= MovementFlags.Root;
                    player.ApplyMovement(movement, 0);
                }

                PlayerbotNavigation.TryPlanToward(player, new Vector3(player.X + 100f, player.Y - stepYards * i, player.Z), Options, out _);
                return true;
            });
            await world.Host.World.AdvanceClockAsync(gapMs);
        }
    }

    [Fact]
    public async Task ReplanningOneGoalFromOneSpot_ForHalfAMinute_IsReportedAsALoop()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end])));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await PlanRepeatedly(world, PlayerbotNavigationAnomalies.ReplanLoopPlans, 5_000, stepYards: 0f);
        Assert.True(cap.Any(Key("replan-loop")), cap.Describe());
        Assert.Equal(1, cap.Count(Key("replan-loop"))); // one report for the episode
    }

    [Fact]
    public async Task ReplanningOneGoal_WithinSeconds_IsNoLoop()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end])));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await PlanRepeatedly(world, PlayerbotNavigationAnomalies.ReplanLoopPlans + 4, 500, stepYards: 0f);
        Assert.False(cap.Any(Key("replan-loop")), cap.Describe());
    }

    [Fact]
    public async Task ReplanningOneGoal_WhileTheBotWalksOn_IsNoLoop()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end])));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        // Ten yards further each time: a long trip plans each leg from a new spot toward the same goal.
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Vector3 goal = Ahead(player, 400f);
            for (int i = 0; i < 12; i++)
            {
                PlayerbotNavigation.TryPlanToward(player, goal, Options, out _);
                player.Relocate(player.X + 10f, player.Y, player.Z, player.Orientation, 0);
            }

            return true;
        });
        await world.Host.World.AdvanceClockAsync(60_000);
        await world.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(world.Player, Ahead(world.Player, 400f), Options, out _));
        Assert.False(cap.Any(Key("replan-loop")), cap.Describe());
    }

    [Fact]
    public async Task ReplanningFromOneSpot_WhileRooted_IsNoLoop()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => new PathResult(PathType.Normal, [start, end])));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await PlanRepeatedly(world, PlayerbotNavigationAnomalies.ReplanLoopPlans, 5_000, stepYards: 0f, rooted: true);
        Assert.False(cap.Any(Key("replan-loop")), cap.Describe());
    }

    // --- nav/goal-unreachable -----------------------------------------------------------------------------------

    [Fact]
    public async Task AGoalRefusedAgainAndAgain_IsReportedOnce()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => PathResult.None(start)));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await PlanRepeatedly(world, PlayerbotNavigationAnomalies.RepeatedRefusals + 3, 1_000, stepYards: 0f);
        Assert.Equal(1, cap.Count(Key("goal-unreachable")));
    }

    [Fact]
    public async Task AGoalRefusedFewerTimesInARow_IsNotReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => PathResult.None(start)));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await PlanRepeatedly(world, PlayerbotNavigationAnomalies.RepeatedRefusals - 1, 1_000, stepYards: 0f);
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }

    [Fact]
    public async Task ARefusalRun_BrokenByASuccess_StartsOver()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        bool reachable = false;
        await OnMesh(world, new Canned((start, end) => reachable ? new PathResult(PathType.Normal, [start, end]) : PathResult.None(start)));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Vector3 goal = Ahead(player, 100f);
            for (int round = 0; round < 3; round++)
            {
                reachable = false;
                for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals - 1; i++) PlayerbotNavigation.TryPlanToward(player, goal, Options, out _);
                reachable = true;
                Assert.True(PlayerbotNavigation.TryPlanToward(player, goal, Options, out _));
            }

            return true;
        });
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }

    [Fact]
    public async Task APlanWithNoVerdict_PendingTiles_IsNeverCounted()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Vector3 goal = Ahead(player, 100f);
            for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals * 3; i++)
                PlayerbotNavigationAnomalies.ObservePlan(player, goal, new Vector3(player.X, player.Y, player.Z), routed: false, inconclusive: true);
            return true;
        });
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }

    [Fact]
    public async Task ABotInAChaosProbe_ReportsNothing()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await OnMesh(world, new Canned((start, end) => PathResult.None(start)));
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        world.Session.IsInChaosProbe = true;
        try
        {
            await PlanRepeatedly(world, PlayerbotNavigationAnomalies.RepeatedRefusals + 3, 1_000, stepYards: 0f);
        }
        finally { world.Session.IsInChaosProbe = false; }

        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }
}
