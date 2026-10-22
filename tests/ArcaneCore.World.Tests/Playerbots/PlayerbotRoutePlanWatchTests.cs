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

            if (i < PlayerbotRoutePlanWatch.MaxUnproductivePlans || i * 6_000u < PlayerbotRoutePlanWatch.MinLoopSpanMs)
            {
                Assert.Equal(PlanOutcome.Route, outcome);
            }
            else
            {
                Assert.Equal(PlanOutcome.Refused, outcome);
            }

            await world.Host.World.AdvanceClockAsync(6_000);
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
}
