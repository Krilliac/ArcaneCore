using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The targeting of a bot-led group that is not a siege raid: a leader that can plan no way to its target gives it up for a minute (as a
/// siege leader does), a member remembers what it could not reach, a fight that goes nowhere is given up, and the leader's pull choice
/// (the objective on its level, the obstacles that would really reach its way) follows the shared filter. Live: 697 travel-timeouts, 488
/// gather-timeouts, 75 stalls and 25 timeouts of the group, quest and run bots.
/// </summary>
public sealed class PlayerbotGroupTargetingTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Mage = 8;
    private const uint Fireball = 133;

    /// <summary>The brute's spawn (GroupTestWorld: 70 yards north of the start).</summary>
    private static readonly Vector3 Brute = new(StartX, StartY + 70f, StartZ);

    private static PlayerbotThreat Threat(Vector3 at, float radius) => new(at, radius, IgnoresHeight: false, Radii: 0f);

    // --- the leader's choice -----------------------------------------------------------------------------------------

    [Fact]
    public void AnObstacleIsAThreatWhoseReachCoversTheRoute_OnTheLeadersLevel_AndNotACivilian()
    {
        var leader = new Vector3(0, 0, 0);
        var goal = new Vector3(100, 0, 0);

        // 12 yards ahead, 3 off the route, reach 8: it would aggro the walk.
        Assert.True(PlayerbotGroupAI.CountsAsObstacle(Threat(new Vector3(12, 3, 0), 8f), civilian: false, leader, goal));
        // The same unit is a civilian: left alone.
        Assert.False(PlayerbotGroupAI.CountsAsObstacle(Threat(new Vector3(12, 3, 0), 8f), civilian: true, leader, goal));
        // On another level of the leader (15 yards up): not a threat to this walk.
        Assert.False(PlayerbotGroupAI.CountsAsObstacle(Threat(new Vector3(12, 3, 15), 8f), civilian: false, leader, goal));
        // Within 20 yards of the leader but 15 yards off a route its reach of 5 does not touch.
        Assert.False(PlayerbotGroupAI.CountsAsObstacle(Threat(new Vector3(10, 15, 0), 5f), civilian: false, leader, goal));
        // Beyond the clearing distance of the leader, on the route and reaching it: the leader walks on first.
        Assert.False(PlayerbotGroupAI.CountsAsObstacle(Threat(new Vector3(40, 0, 0), 8f), civilian: false, leader, goal));
    }

    [Fact]
    public void TheObjectiveOnTheLeadersLevelIsPreferred_ThenTheNearest()
    {
        // A nearer one on another level does not beat a farther one on the leader's level.
        Assert.True(PlayerbotGroupAI.Better(candidateOtherLevel: false, candidateYards: 40f, haveBest: true, bestOtherLevel: true, bestYards: 10f));
        Assert.False(PlayerbotGroupAI.Better(candidateOtherLevel: true, candidateYards: 10f, haveBest: true, bestOtherLevel: false, bestYards: 40f));
        // On one level the nearer wins; with none yet, any candidate does (an objective on another level is better than none).
        Assert.True(PlayerbotGroupAI.Better(false, 10f, haveBest: true, bestOtherLevel: false, bestYards: 40f));
        Assert.False(PlayerbotGroupAI.Better(false, 40f, haveBest: true, bestOtherLevel: false, bestYards: 10f));
        Assert.True(PlayerbotGroupAI.Better(true, 99f, haveBest: false, bestOtherLevel: false, bestYards: 0f));
        Assert.True(PlayerbotGroupAI.Better(true, 10f, haveBest: true, bestOtherLevel: true, bestYards: 40f));
    }

    // --- the fight watch ---------------------------------------------------------------------------------------------

    private static readonly ObjectGuid Foe = new(0x1100_0000_0000_0001ul), OtherFoe = new(0x1100_0000_0000_0002ul);

    [Fact]
    public void AFightThatLosesNoHealthAndClosesNoDistance_IsGivenUpAfterTwentySeconds()
    {
        var watch = new PlayerbotFightWatch();
        Assert.False(watch.Observe(Foe, 100, 30f, 1_000));
        Assert.False(watch.Observe(Foe, 100, 30f, 1_000 + PlayerbotFightWatch.StallMs - 1));
        Assert.True(watch.Observe(Foe, 100, 30f, 1_000 + PlayerbotFightWatch.StallMs));
        Assert.Equal(20_000u, PlayerbotFightWatch.StallMs);
    }

    [Fact]
    public void AFightThatDropsTheTargetsHealth_OrClosesTheDistance_StartsTheWindowAgain()
    {
        var watch = new PlayerbotFightWatch();
        Assert.False(watch.Observe(Foe, 100, 30f, 0));
        // Health dropping is progress ...
        Assert.False(watch.Observe(Foe, 90, 30f, 15_000));
        Assert.False(watch.Observe(Foe, 90, 30f, 34_999));
        // ... so is closing in by more than a yard ...
        Assert.False(watch.Observe(Foe, 90, 20f, 30_000));
        Assert.False(watch.Observe(Foe, 90, 20f, 49_999));
        Assert.True(watch.Observe(Foe, 90, 20f, 50_000));
        // ... and a target that heals back is not progress.
        watch.Reset();
        Assert.False(watch.Observe(Foe, 50, 5f, 100_000));
        Assert.False(watch.Observe(Foe, 80, 5f, 110_000));
        Assert.True(watch.Observe(Foe, 80, 5f, 120_000));
    }

    [Fact]
    public void ANewTarget_RestartsTheWatch_AndTheClockWraps()
    {
        var watch = new PlayerbotFightWatch();
        Assert.False(watch.Observe(Foe, 100, 30f, 1_000));
        Assert.False(watch.Observe(OtherFoe, 100, 30f, 15_000));
        Assert.False(watch.Observe(OtherFoe, 100, 30f, 34_999));
        Assert.True(watch.Observe(OtherFoe, 100, 30f, 35_000));

        // The world's millisecond clock is a uint: a window that spans the wrap is still 20 seconds.
        watch.Reset();
        uint start = uint.MaxValue - 5_000;
        Assert.False(watch.Observe(Foe, 100, 30f, start));
        Assert.False(watch.Observe(Foe, 100, 30f, unchecked(start + 19_999)));
        Assert.True(watch.Observe(Foe, 100, 30f, unchecked(start + 20_000)));
    }

    // --- a leader that can plan no way to its target ------------------------------------------------------------------

    private static async Task<(PlayerbotGroupCoordinator.BotGroup Group, PlayerbotGroupAI Leader, ObjectGuid BruteGuid)> EngageThenBlockTheBruteAsync(GroupTestWorld world)
    {
        await world.AddBotAsync("Shunwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Shunmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        PlayerbotGroupAI leader = world.Coordinator.FindAI(group.LeaderBotId)!;
        ObjectGuid brute = await world.OnWorldAsync(() =>
        {
            // From here no way leads near the brute: its spawn stands on a ledge the mesh does not reach.
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new AwayFrom(Brute, 25f));
            return PlayerbotGroupCoordinator.FindLivingOnMap(world.Player(group.LeaderBotId), BruteEntry)!.Guid;
        });
        return (group, leader, brute);
    }

    [Fact]
    public async Task ALeaderWithNoPathToItsTarget_ShunsItForAMinute_AndMovesOn()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupAI leader, ObjectGuid brute) = await EngageThenBlockTheBruteAsync(world);

        Assert.True(await world.RunUntilAsync(15_000, () => group.IsShunned(brute, world.World.Host.World.NowMs)), await world.TraceAsync());

        // The shun is the siege's minute (not a stall watch's half hour) ...
        Assert.Equal(60_000u, PlayerbotGroupAI.NoPathShunMs);
        Assert.True(await world.OnWorldAsync(() => group.IsShunned(brute, unchecked(world.World.Host.World.NowMs + 50_000))), "the shun is still in force 50 s on");

        // ... and the leader moves on: it fights nothing, and does not choose the brute again at every think.
        var fights = new List<string>();
        await world.RunUntilAsync(5_000, () =>
        {
            if (leader.TargetUnit is not null || leader.Step.StartsWith("fight:", StringComparison.Ordinal)) fights.Add(leader.Step);
            return false;
        });
        Assert.Empty(fights);
    }

    [Fact]
    public async Task ALeaderThatDropsAFightForNoPath_KeepsTheGuidOfThatTarget()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        (_, PlayerbotGroupAI leader, ObjectGuid brute) = await EngageThenBlockTheBruteAsync(world);

        // On the think that dropped the fight (no unit is targeted, the step says why) the group's stall watch can still name what to shun.
        ObjectGuid kept = default;
        bool dropped = false;
        Assert.True(await world.RunUntilAsync(15_000, () =>
        {
            if (leader.TargetUnit is not null || leader.Step != $"fight:{BruteEntry}:no-path") return false;
            kept = leader.LastTargetGuid;
            dropped = true;
            return true;
        }), await world.TraceAsync());
        Assert.True(dropped);
        Assert.Equal(brute, kept);
        Assert.Equal(BruteEntry, await world.OnWorldAsync(() => leader.LastTargetEntry));
    }

    // --- a member that could not reach a target -------------------------------------------------------------------------

    /// <summary>No way leads anywhere.</summary>
    private sealed class NoPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
    }

    [Fact]
    public async Task AMemberThatCouldNotReachATarget_DoesNotChooseItAgainAtEveryThink()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        var first = await world.AddBotAsync("Pullwar", Human, Warrior, 1, []);
        var second = await world.AddBotAsync("Assistwar", Human, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Guid leaderId = group.LeaderBotId, memberId = leaderId == first.Id ? second.Id : first.Id;

        // The leader reaches the brute and fights it.
        Assert.True(await world.RunUntilAsync(90_000, () => world.Player(leaderId).Combat.Victim is Creature { Entry: BruteEntry }), await world.TraceAsync());

        // Then the member stands 40 yards off with no way to the brute: it assists the leader's victim, finds no path, and ...
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(leaderId);
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());
            world.Player(memberId).Relocate(leader.X, leader.Y - 40f, leader.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

        PlayerbotGroupAI member = world.Coordinator.FindAI(memberId)!;
        int noPath = 0;
        await world.RunUntilAsync(5_000, () =>
        {
            if (member.Step.StartsWith("fight:", StringComparison.Ordinal) && member.Step.EndsWith(":no-path", StringComparison.Ordinal)) noPath++;
            return false;
        });

        // ... does not try it again at each of the 25 thinks of those five seconds.
        Assert.True(noPath >= 1, "the member never tried the brute: the test did not set the case up");
        Assert.True(noPath <= 2, $"the member tried the unreachable brute {noPath} times in 5 s");
    }

    /// <summary>Plans straight lines, except to a point within <paramref name="radius"/> of <paramref name="center"/>: no path there.</summary>
    private sealed class AwayFrom(Vector3 center, float radius) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(center.X, center.Y)) <= radius
                ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
    }
}
