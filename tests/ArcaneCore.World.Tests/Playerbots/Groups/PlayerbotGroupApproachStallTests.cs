using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Characters.Creation;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Live issue #1470: level-60 dungeon groups for Wailing Caverns stalled on the hill above the cave. The approach point (15 yards flat from the
/// entrance, at the entrance's height) lay under the hill, the leader's walk to it was refused for good, and its holds and rests between two
/// attempts kept resetting the stuck stretch, so no bailout fired and the same goal formed a new group a few minutes after each one failed.
/// </summary>
public sealed class PlayerbotGroupApproachStallTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Mage = 8;
    private const uint Fireball = 133;

    /// <summary>The brute's spawn (GroupTestWorld: 70 yards north of the start), the meeting point of the brute quest's group.</summary>
    private static readonly Vector3 Brute = new(StartX, StartY + 70f, StartZ);

    private static uint NowMs(GroupTestWorld world) => world.World.Host.World.NowMs;

    /// <summary>A way exists only to a point within 3 yards (flat) of the given point: every other point lies under the hill.</summary>
    private sealed class ReachesOnly(Vector3 only) : IPathfinder
    {
        public bool Enabled => true;

        public bool Reaches(Vector3 end) => Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(only.X, only.Y)) <= 3f;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Reaches(end) ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
    }

    /// <summary>No way leads anywhere.</summary>
    private sealed class NoPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    /// <summary>Plans straight lines, except to a point within the radius of the center: no path there.</summary>
    private sealed class AwayFrom(Vector3 center, float radius) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(center.X, center.Y)) <= radius
                ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
    }

    [Fact]
    public async Task TheApproachPointOfANewGroup_IsReachableFromTheLeader_OrTheMeetingPointItself()
    {
        var mesh = new ReachesOnly(Brute);
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: mesh);
            return true;
        });
        await world.AddBotAsync("Hillwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Hillmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(group.Approach == group.Goal.Meeting || mesh.Reaches(group.Approach),
            $"approach {group.Approach} is neither reachable nor the meeting point {group.Goal.Meeting}");
    }

    [Fact]
    public async Task ALeaderWhoseWalkIsRefused_WhileItAlternatesWithHolds_GivesUpWithNoPathToApproachOnceThirtySecondsOfRefusalsAreCounted()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        var first = await world.AddBotAsync("Holdwar", Human, Warrior, 1, []);
        var second = await world.AddBotAsync("Holdmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Guid leaderId = group.LeaderBotId, laggardId = leaderId == first.Id ? second.Id : first.Id;
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());
            return true;
        });

        // Ten-second cycles: 5 s with the whole group at the leader (it tries to walk and is refused), then 5 s with a member 60 yards behind
        // (it holds for the member): the holds are not counted, so the bound (30 s of refusals) comes after about 60 s.
        uint started = await world.OnWorldAsync(() => NowMs(world));
        bool gaveUp = false;
        for (int cycle = 0; cycle < 10 && !gaveUp; cycle++)
        {
            await world.OnWorldAsync(() =>
            {
                Player leader = world.Player(leaderId);
                world.Player(laggardId).Relocate(leader.X, leader.Y, leader.Z, 0, NowMs(world));
                return true;
            });
            gaveUp = await world.RunUntilAsync(5_000, () => group.Reason is not null);
            if (gaveUp) break;
            await world.OnWorldAsync(() =>
            {
                Player leader = world.Player(leaderId);
                world.Player(laggardId).Relocate(leader.X, leader.Y - 60f, leader.Z, 0, NowMs(world));
                return true;
            });
            gaveUp = await world.RunUntilAsync(5_000, () => group.Reason is not null);
        }

        Assert.True(gaveUp, await world.TraceAsync());
        Assert.Equal("no-path-to-approach", group.Reason);
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - started), 45_000u, 85_000u);
    }

    /// <summary>Plans a straight line of the given type to every point <c>reaches</c> accepts, else no path; keeps the options of its queries.</summary>
    private sealed class Probe(Func<Vector3, bool> reaches, PathType type = PathType.Normal) : IPathfinder
    {
        public List<PathOptions?> Options { get; } = [];

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Options.Add(options);
            return reaches(end) ? PathResult.StraightLine(start, end, type) : PathResult.None(start);
        }
    }

    private static readonly PlayerbotGroupGoal QuestGoal = new(PlayerbotGroupGoalKind.Quest, 0, 1, 0, 0, Vector3.Zero, 0, Vector3.Zero, 2, 1, "test");

    private static readonly Vector3 FarLeader = new(100f, 0f, 0f);

    [Fact]
    public void ANonFirstBearing_IsChosen_WhenTheUsualPointAndTheFirstBearingsAreUnreachable()
    {
        // Reachable: only the points north or south of the meeting point, 30 yards out (the third bearing, 90 degrees either way).
        var probe = new Probe(end => MathF.Abs(end.X) < 2f && MathF.Abs(MathF.Abs(end.Y) - PlayerbotGroupCoordinator.QuestApproachYards) < 2f);

        Vector3 approach = PlayerbotGroupCoordinator.ReachableApproach(probe, QuestGoal, FarLeader);

        Assert.NotEqual(QuestGoal.Meeting, approach);
        Assert.InRange(MathF.Abs(approach.X), 0f, 2f);
        Assert.InRange(MathF.Abs(approach.Y), PlayerbotGroupCoordinator.QuestApproachYards - 2f, PlayerbotGroupCoordinator.QuestApproachYards + 2f);
        Assert.True(probe.Options.Count >= 3, "the usual point and the two nearer bearings were asked first");
    }

    [Fact]
    public void WhenNoBearingIsReachable_TheMeetingPointItselfIsTheApproach()
    {
        Assert.Equal(QuestGoal.Meeting, PlayerbotGroupCoordinator.ReachableApproach(new Probe(_ => false), QuestGoal, FarLeader));
    }

    [Fact]
    public void AStraightLineWithNoMeshBehindIt_IsNoProofOfAWay_AndTheMeetingPointIsTheApproach()
    {
        var probe = new Probe(_ => true, PathType.NotUsingPath);

        Assert.Equal(QuestGoal.Meeting, PlayerbotGroupCoordinator.ReachableApproach(probe, QuestGoal, FarLeader));
    }

    [Fact]
    public void ANavigationTileStillPending_GivesNoVerdict_TheUsualPointStands()
    {
        var probe = new Probe(_ => true, PathType.TilesPending);

        Vector3 approach = PlayerbotGroupCoordinator.ReachableApproach(probe, QuestGoal, FarLeader);

        Assert.Equal(PlayerbotGroupCoordinator.ApproachPoint(QuestGoal, FarLeader), approach);
    }

    [Fact]
    public void TheReachabilityProbe_AsksWithTheWalkersOwnOptions()
    {
        var probe = new Probe(_ => true);

        PlayerbotGroupCoordinator.ReachableApproach(probe, QuestGoal, FarLeader);

        PathOptions options = Assert.Single(probe.Options)!;
        Assert.Equal(PathMover.Player, options.Mover);
        Assert.True((options.ExcludeFlags & NavTerrain.SteepSlopes) != 0);
        Assert.True(options.LoadTiles);
        Assert.True(options.NonBlockingTiles);
    }

    [Fact]
    public void TheRefusedWalkStretch_CountsOnlyRefusedThinks_SoPausesBetweenThemAreLeftOut()
    {
        long refused = 0;
        // 29 s of refusals at 200 ms thinks; a long pause makes no call at all; the next refusals go on from 29 s.
        for (int i = 0; i < 145; i++) Assert.False(PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: false, 200));
        Assert.Equal(29_000, refused);
        for (int i = 0; i < 5; i++) Assert.False(PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: false, 200));
        Assert.Equal(PlayerbotGroupAI.RunStuckMs, refused);
        Assert.True(PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: false, 200));
    }

    [Fact]
    public void APlannedWalk_EndsTheRefusedStretch()
    {
        long refused = 0;
        for (int i = 0; i < 100; i++) PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: false, 200);
        Assert.False(PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: true, 200));
        Assert.Equal(0, refused);
        Assert.False(PlayerbotGroupAI.NoteStuckRefused(ref refused, planned: false, 200));
        Assert.Equal(200, refused);
    }

    [Fact]
    public async Task ALeaderInsideTheRingWhoseWalkIsRefused_ChoosesTheApproachPointAgainOnce()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
            options.Party.TeleportToLeader = false;
        });
        var first = await world.AddBotAsync("Ringwar", Human, Warrior, 1, []);
        var second = await world.AddBotAsync("Ringmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Guid leaderId = group.LeaderBotId, otherId = leaderId == first.Id ? second.Id : first.Id;
        Assert.NotEqual(group.Goal.Meeting, group.Approach);

        // Only the meeting point itself can be reached; the group stands 20 yards (flat) from it and 40 yards above its height: inside the ring, off its level.
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new ReachesOnly(group.Goal.Meeting));
            foreach (Guid id in new[] { leaderId, otherId })
                world.Player(id).Relocate(group.Goal.Meeting.X, group.Goal.Meeting.Y - 20f, group.Goal.Meeting.Z + 40f, 0, NowMs(world));
            return true;
        });

        Assert.True(await world.RunUntilAsync(30_000, () => group.ApproachRetried || group.State != PlayerbotGroupState.Travelling), await world.TraceAsync());
        Assert.True(group.ApproachRetried, await world.TraceAsync());
        Assert.Equal(group.Goal.Meeting, group.Approach);
    }

    [Fact]
    public async Task AGroupStallLine_CarriesTheGroupsState_NotTheLeadersStaleBrainSnapshot()
    {
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 30;
        }, services: logs.Register);
        await world.AddBotAsync("Diagwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Diagmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new AwayFrom(Brute, 25f));
            return true;
        });

        Assert.True(await world.RunUntilAsync(150_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        string[] stalls = [.. logs.Lines.Where(line => line.Contains("stalled", StringComparison.Ordinal) && line.Contains("goal=Group", StringComparison.Ordinal))];
        Assert.NotEmpty(stalls);
        Assert.All(stalls, line =>
        {
            Assert.Contains("diag: group ", line, StringComparison.Ordinal);
            Assert.Contains("state=engaging", line, StringComparison.Ordinal);
            Assert.Contains("approach=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("phase=", line, StringComparison.Ordinal);
        });
        Assert.Equal("stalled", group.Reason);
    }

    [Fact]
    public async Task AfterThreeGroupsFailedTheSameWayForOneGoal_NewGroupsChooseAnotherGoal()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupGoalKind, uint) key = default;
        for (int round = 0; round < 3; round++)
        {
            await world.AddBotAsync("Failwar" + (char)('a' + round), Human, Warrior, 1, []);
            await world.AddBotAsync("Failmage" + (char)('a' + round), Human, Mage, 1, [Fireball]);
            Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());
            PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
            key = group.Goal.Key;
            await world.OnWorldAsync(() =>
            {
                group.RunFault = "no-path-to-approach";
                return true;
            });
            Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
            Assert.Equal("no-path-to-approach", group.Reason);
        }

        var fresh = await world.AddBotAsync("Freshwar", Human, Warrior, 1, []);
        var freshMate = await world.AddBotAsync("Freshmage", Human, Mage, 1, [Fireball]);
        await world.RunAsync(90_000);

        Assert.All(world.Coordinator.Groups, g => Assert.NotEqual(key, g.Goal.Key));
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.IsSetAside(fresh.Id, key) && world.Coordinator.IsSetAside(freshMate.Id, key)), await world.TraceAsync());
    }
}
