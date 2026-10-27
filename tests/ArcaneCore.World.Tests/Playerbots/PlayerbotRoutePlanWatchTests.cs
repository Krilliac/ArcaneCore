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

    /// <summary>
    /// Plans toward <paramref name="goal"/> (timestamped, from where the bot stands) <paramref name="times"/> times, one second apart; each
    /// is refused. The motion blacklist a suppression adds lasts <see cref="PlayerbotMotion.LoopBlacklistMs"/>, shorter than any suppression,
    /// but expires only by the clock of a bot that has moved: this bot stands still, so it is lifted here as the clock would lift it.
    /// </summary>
    private static async Task RefuseAsync(RiskTestWorld world, Vector3 goal, int times)
    {
        TileWait[] wait = [default];
        for (int i = 0; i < times; i++)
        {
            PlanOutcome outcome = await world.OnWorldAsync(() =>
            {
                PlayerbotMotion.Unblacklist(world.Player, goal);
                return PlayerbotNavigation.PlanToward(world.Player, goal, Options, ref wait[0], world.Host.World.NowMs, out _);
            });
            Assert.Equal(PlanOutcome.Refused, outcome);
            await world.Host.World.AdvanceClockAsync(1_000);
        }
    }

    /// <summary>
    /// Bot QA #1492: the watch suppresses a pair after <see cref="PlayerbotRoutePlanWatch.MaxConsecutiveRefusals"/> refusals while the
    /// anomaly check kept counting across the suppression, so the first refusal after it expired was the anomaly's fifth in a row and
    /// <c>bot-nav/goal-unreachable</c> fired on every suppression cycle. A suppression starts the anomaly's count over.
    /// </summary>
    [Fact]
    public async Task TimestampedPlanToward_RefusedAgainAfterTheSuppressionExpires_DoesNotReportGoalUnreachable()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, _) => PathResult.None(start));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        Vector3 goal = await world.OnWorldAsync(() => new Vector3(world.Player.X + 80f, world.Player.Y, world.Player.Z));

        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressMs + 1);
        int queriesBefore = mesh.Queries;
        await RefuseAsync(world, goal, 1);

        Assert.True(mesh.Queries > queriesBefore, "the suppression must have expired: the plan searches the mesh again");
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());
        Assert.False(cap.Any(Key("no-path-connected")), cap.Describe());
    }

    [Fact]
    public async Task Suppression_OfAPairSuppressedBefore_LastsLongerThanTheFirst()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, _) => PathResult.None(start));
        await OnMesh(world, mesh);
        Vector3 goal = await world.OnWorldAsync(() => new Vector3(world.Player.X + 80f, world.Player.Y, world.Player.Z));

        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressMs + 1);
        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);

        // The second suppression outlives the first one's span: the pair is still held without a search.
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressMs + 1);
        int queriesBefore = mesh.Queries;
        await RefuseAsync(world, goal, 1);
        Assert.Equal(queriesBefore, mesh.Queries);

        // ... and expires in its turn (twice the first span).
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressMs);
        await RefuseAsync(world, goal, 1);
        Assert.True(mesh.Queries > queriesBefore, "the escalated suppression must expire after twice the first span");
    }

    [Fact]
    public void SuppressionSpan_DoublesPerStrike_UpToTheCap()
    {
        Assert.Equal(PlayerbotRoutePlanWatch.SuppressMs, PlayerbotRoutePlanWatch.SuppressionSpanMs(1));
        Assert.Equal(PlayerbotRoutePlanWatch.SuppressMs * 2, PlayerbotRoutePlanWatch.SuppressionSpanMs(2));
        Assert.Equal(PlayerbotRoutePlanWatch.SuppressMs * 4, PlayerbotRoutePlanWatch.SuppressionSpanMs(3));
        Assert.Equal(PlayerbotRoutePlanWatch.MaxSuppressMs, PlayerbotRoutePlanWatch.SuppressionSpanMs(50));
    }

    /// <summary>A goal still refused once the watch has escalated its suppression is really unreachable: that is reported, once.</summary>
    [Fact]
    public async Task TimestampedPlanToward_StillRefusedAfterAnEscalatedSuppression_ReportsGoalUnreachableOnce()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        var mesh = new Canned((start, _) => PathResult.None(start));
        await OnMesh(world, mesh);
        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        Vector3 goal = await world.OnWorldAsync(() => new Vector3(world.Player.X + 80f, world.Player.Y, world.Player.Z));

        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressMs + 1);
        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);
        Assert.False(cap.Any(Key("goal-unreachable")), cap.Describe());

        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.SuppressionSpanMs(2) + 1);
        await RefuseAsync(world, goal, 1);
        Assert.Equal(1, cap.Count(Key("goal-unreachable")));

        await RefuseAsync(world, goal, PlayerbotRoutePlanWatch.MaxConsecutiveRefusals);
        await world.Host.World.AdvanceClockAsync(PlayerbotRoutePlanWatch.MaxSuppressMs + 1);
        await RefuseAsync(world, goal, 1);
        Assert.Equal(1, cap.Count(Key("goal-unreachable")));
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
