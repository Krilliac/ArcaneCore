using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Unit tests for <see cref="PlayerbotRoutePlanWatch"/> and its integration with <see cref="PlayerbotNavigation"/>:
/// unproductive replan loops are suppressed and blacklisted before <see cref="PlayerbotNavigationAnomalies.ReplanLoopPlans"/>,
/// repeated timestamped refusals are held in the negative cache, and policy/blacklist refusals do not count as path-search failures.
/// </summary>
public sealed class PlayerbotRoutePlanWatchTests
{
    private static readonly PlayerbotOptions Options = new() { MaxPathPoints = 128, MaxRouteYards = 2000 };

    private static string Key(string code) => PlayerbotAnomalies.Key("nav", code);

    private sealed class Canned(Func<Vector3, Vector3, PathResult> answer) : IPathfinder
    {
        public int Queries { get; private set; }
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (start == end) return PathResult.None(start);
            Queries++;
            return answer(start, end);
        }
    }

    private sealed class FlatLos(float z) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info) { info = default; return false; }
    }

    private static Task<bool> OnMesh(RiskTestWorld world, Canned mesh)
        => world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatLos(world.Player.Z), pathfinder: mesh);
            return true;
        });

    [Fact]
    public void MinLoopSpan_LiesBetweenTheDestinationsNotClosingClock_AndTheReplanLoopAnomaly()
    {
        Assert.True(PlayerbotWorldDestinations.SpawnNotClosingMs < PlayerbotRoutePlanWatch.MinLoopSpanMs);
        Assert.True(PlayerbotRoutePlanWatch.MinLoopSpanMs < PlayerbotNavigationAnomalies.ReplanLoopMinMs);
    }

    [Fact]
    public async Task TimestampedPlanToward_FromSameSpotWithoutProgress_SuppressesAndBlacklistsOnceTheLoopSpanPasses_BeforeReplanLoopFires()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, end) => new PathResult(PathType.Normal, [start, end]));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);

        Vector3 goal = default;
        TileWait[] wait = [default];
        for (int i = 0; i < PlayerbotNavigationAnomalies.ReplanLoopPlans + 2; i++)
        {
            PlanOutcome outcome = await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                if (i == 0) goal = new Vector3(player.X + 120f, player.Y, player.Z);
                return PlayerbotNavigation.PlanToward(player, goal, Options, ref wait[0], world.Host.World.NowMs, out _);
            });

            if (i < PlayerbotRoutePlanWatch.MaxUnproductivePlans)
            {
                Assert.Equal(PlanOutcome.Route, outcome);
            }
            else
            {
                Assert.Equal(PlanOutcome.Refused, outcome);
            }

            // Three plans span MinLoopSpanMs by the fourth.
            await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.MinLoopSpanMs / 3 + 1_000);
        }

        bool blacklisted = await world.OnWorldAsync(() => PlayerbotMotion.IsBlacklisted(world.Player, goal));
        Assert.True(blacklisted, "the suppressed goal must be blacklisted in PlayerbotMotion");
        Assert.False(cap.Any(Key("replan-loop")), cap.Describe());
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }

    [Fact]
    public async Task TimestampedPlanToward_MakingForwardProgress_IsNeverSuppressed()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, end) => new PathResult(PathType.Normal, [start, end]));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);

        Vector3 goal = default;
        TileWait[] wait = [default];
        for (int i = 0; i < 10; i++)
        {
            PlanOutcome outcome = await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                if (i == 0) goal = new Vector3(player.X + 200f, player.Y, player.Z);
                else player.Relocate(player.X + 8f, player.Y, player.Z, player.Orientation, world.Host.World.NowMs);
                return PlayerbotNavigation.PlanToward(player, goal, Options, ref wait[0], world.Host.World.NowMs, out _);
            });
            Assert.Equal(PlanOutcome.Route, outcome);
            await world.Host.World.AdvanceClockAsync(6_000);
        }

        Assert.False(cap.Any(Key("replan-loop")), cap.Describe());
    }

    [Fact]
    public async Task TimestampedPlanToward_RepeatedlyRefused_EntersNegativeCacheAfterThreeAttempts_AndExpiresAfterWindow()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, _) => PathResult.None(start));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);

        Vector3 goal = default;
        TileWait[] wait = [default];
        for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals + 3; i++)
        {
            PlanOutcome outcome = await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                if (i == 0) goal = new Vector3(player.X + 80f, player.Y, player.Z);
                return PlayerbotNavigation.PlanToward(player, goal, Options, ref wait[0], world.Host.World.NowMs, out _);
            });
            Assert.Equal(PlanOutcome.Refused, outcome);
            await world.Host.World.AdvanceClockAsync(1_000);
        }

        int queriesWhileSuppressed = mesh.Queries;
        PlanOutcome cachedOutcome = await world.OnWorldAsync(() =>
            PlayerbotNavigation.PlanToward(world.Player, goal, Options, ref wait[0], world.Host.World.NowMs, out _));
        Assert.Equal(PlanOutcome.Refused, cachedOutcome);
        Assert.Equal(queriesWhileSuppressed, mesh.Queries);
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
    }

    [Fact]
    public async Task BlacklistedDestinationRefusals_DoNotReportGoalUnreachableOrNoPathConnected()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, end) => new PathResult(PathType.Normal, [start, end]));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);

        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Vector3 goal = new(player.X + 50f, player.Y, player.Z);
            PlayerbotMotion.Blacklist(player, [goal], world.Host.World.NowMs);
            for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals + 2; i++)
            {
                Assert.False(PlayerbotNavigation.TryPlanToward(player, goal, Options, out _));
            }
            return true;
        });

        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
        Assert.False(cap.Any(Key("no-path-connected")), cap.Describe());
    }

    /// <summary>
    /// Seeds the watch with <see cref="PlayerbotRoutePlanWatch.MaxConsecutiveRefusals"/> definitive refusals toward a duty goal from one fixed
    /// spot (a respawn at the graveyard), 1 s apart. The origin is fixed rather than read from the bot, which may walk while the clock runs.
    /// </summary>
    private static async Task<(Vector3 Origin, Vector3 Goal)> SeedRefusalsAsync(RiskTestWorld world, PlayerbotRoutePlanWatch.Policy? policy)
    {
        Vector3 origin = await world.OnWorldAsync(() => PlayerbotMotion.CurrentPosition(world.Player));
        Vector3 goal = origin + new Vector3(80f, 0f, 0f);
        for (int i = 0; i < PlayerbotRoutePlanWatch.MaxConsecutiveRefusals; i++)
        {
            await world.OnWorldAsync(() =>
            {
                PlayerbotRoutePlanWatch.RecordOutcome(world.Player, origin, goal, planned: false, route: null, world.Host.World.NowMs, policy);
                return true;
            });
            await world.Host.World.AdvanceClockAsync(1_000);
        }

        return (origin, goal);
    }

    /// <summary>11 s: past the 10 s duty hold, far inside the 180 s default hold.</summary>
    private const uint PastDutyWindowMs = 11_000;

    private static Task<bool> Refused(RiskTestWorld world, Vector3 origin, Vector3 goal)
        => world.OnWorldAsync(() => PlayerbotRoutePlanWatch.ShouldRefuse(world.Player, origin, goal, world.Host.World.NowMs));

    [Fact]
    public async Task DutyPolicy_RefusedGoalFromTheGraveyard_IsPlannedAgainAfterTheShortWindow_NotShelvedFor180Seconds()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();

        (Vector3 origin, Vector3 goal) = await SeedRefusalsAsync(world, PlayerbotRoutePlanWatch.Policy.Duty);
        Assert.True(await Refused(world, origin, goal), "inside the short hold the duty goal is refused");
        Assert.False(await world.OnWorldAsync(() => PlayerbotMotion.IsBlacklisted(world.Player, goal)), "a duty goal is not blacklisted");

        await world.Host.World.AdvanceClockAsync(PastDutyWindowMs);
        Assert.False(await Refused(world, origin, goal), "past the short hold the duty goal is planned again");
        Assert.True(PlayerbotRoutePlanWatch.DutySuppressMs < PlayerbotRoutePlanWatch.SuppressMs);
    }

    [Fact]
    public async Task DefaultPolicy_RefusedGoal_StaysShelvedPastTheDutyWindow_AndIsBlacklisted()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();

        (Vector3 origin, Vector3 goal) = await SeedRefusalsAsync(world, null);
        Assert.True(await world.OnWorldAsync(() => PlayerbotMotion.IsBlacklisted(world.Player, goal)), "the default hold blacklists the goal");
        await world.Host.World.AdvanceClockAsync(PastDutyWindowMs);
        Assert.True(await Refused(world, origin, goal), "other callers keep the 180 s hold");
    }
}
