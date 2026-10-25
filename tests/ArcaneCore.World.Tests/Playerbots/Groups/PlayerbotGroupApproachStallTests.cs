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
    public async Task ALeaderWhoseWalkIsRefused_WhileItAlternatesWithHolds_GivesUpWithNoPathToApproachWithinAboutThirtyFiveSeconds()
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

        // Ten-second cycles: 3 s with the whole group at the leader (it tries to walk and is refused), then 7 s with a member 60 yards behind
        // (it holds for the member): every pause is longer than the gap that used to start a new stretch.
        uint started = await world.OnWorldAsync(() => NowMs(world));
        bool gaveUp = false;
        for (int cycle = 0; cycle < 6 && !gaveUp; cycle++)
        {
            await world.OnWorldAsync(() =>
            {
                Player leader = world.Player(leaderId);
                world.Player(laggardId).Relocate(leader.X, leader.Y, leader.Z, 0, NowMs(world));
                return true;
            });
            gaveUp = await world.RunUntilAsync(3_000, () => group.Reason is not null);
            if (gaveUp) break;
            await world.OnWorldAsync(() =>
            {
                Player leader = world.Player(leaderId);
                world.Player(laggardId).Relocate(leader.X, leader.Y - 60f, leader.Z, 0, NowMs(world));
                return true;
            });
            gaveUp = await world.RunUntilAsync(7_000, () => group.Reason is not null);
        }

        Assert.True(gaveUp, await world.TraceAsync());
        Assert.Equal("no-path-to-approach", group.Reason);
        Assert.InRange(unchecked(await world.OnWorldAsync(() => NowMs(world)) - started), 25_000u, 45_000u);
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
