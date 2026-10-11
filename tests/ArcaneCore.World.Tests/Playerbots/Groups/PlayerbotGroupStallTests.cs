using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Characters.Creation;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Groups formed by the risk estimate's group signal that sat in <c>engaging</c> with both members standing still (live replays
/// 2026-10-11 on the real terrain, Westfall farm, quest 9 "The Killing Fields": a paladin healer leading a warlock stood 15-22 yards
/// from a Harvest Watcher for 300+ s, never pulling it). Nothing reported it: the brains' stall watch does not run while a group drives
/// the bots. The leader must pull, and a group that makes no progress anyway re-targets, then dissolves, with a stall line bot QA parses.
/// </summary>
public sealed class PlayerbotGroupStallTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint Fireball = 133, LesserHeal = 2050, PowerWordFortitude = 1243;

    /// <summary>The brute's spawn (GroupTestWorld: 70 yards north of the start).</summary>
    private static readonly Vector3 Brute = new(StartX, StartY + 70f, StartZ);

    [Fact]
    public async Task ARiskGroup_LedByAHealerWithoutAnAttackSpell_StillPullsAndKillsTheObjective()
    {
        // A healer keeps caster range (PlayerbotPriestRotation / PlayerbotPaladinRotation.PreferredRange in a group's healer role) and,
        // without an attack spell it can cast (none learned, out of mana, a paladin's heals only), stood at range from the objective for
        // good as the leader; nobody else pulls (the members assist the leader's victim). The brain goes into melee after
        // RangedIdleLimitMs of holding at range without a fight (PlayerbotBrain.Update); the group AI must too.
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f, services: logs.Register);
        var healer = await world.AddBotAsync("Stallprie", Human, Priest, 1, [LesserHeal, PowerWordFortitude]);
        // The priest waits first, so it leads the pair (no tank: the bot that waited longest leads).
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == healer.Id)), world.Trace());
        await world.AddBotAsync("Stallmage", Human, Mage, 1, [Fireball]);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(healer.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);

        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        // A healthy group is never reported stalled.
        Assert.DoesNotContain(logs.Lines, line => line.Contains("goal=Group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARiskGroup_WhoseObjectiveCannotBeReached_ReTargets_ThenDissolves_AndReportsAStallBotQaParses()
    {
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 30;
        }, services: logs.Register);
        var warrior = await world.AddBotAsync("Stuckwar", Human, Warrior, 1, []);
        var mage = await world.AddBotAsync("Stuckmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal("risk", group.Goal.Source);

        // From here no way leads near the brute (it stands on a ledge the mesh does not reach): the leader can neither chase it nor walk
        // to its spawn, and the members follow the leader that stands.
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new AwayFrom(Brute, 25f));
            return true;
        });

        // Two stall bounds (30 s each) and a little: the group gives the brute up as a target first, then gives up and dissolves.
        Assert.True(await world.RunUntilAsync(150_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Equal("stalled", group.Reason);
        Assert.Contains(world.Coordinator.Events, line => line.Contains($"group {group.Id} stalled", StringComparison.Ordinal)
            && line.Contains("re-targets", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Events, line => line.Contains($"group {group.Id} gave up", StringComparison.Ordinal)
            && line.EndsWith(": stalled", StringComparison.Ordinal));
        Assert.True(world.Coordinator.IsSetAside(warrior.Id, group.Goal.Key));
        Assert.True(world.Coordinator.IsSetAside(mage.Id, group.Goal.Key));

        // The stall is a warning of the managed bot feature in the shape bot QA already parses (PlayerbotQaSignalParser), for the leader.
        QaSignal[] stalls = [.. logs.Lines
            .Where(line => line.StartsWith("Warning " + typeof(ManagedPlayerbotFeature).FullName + ": ", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .Select(message => PlayerbotQaSignalParser.TryParse(message, DateTime.UtcNow, null, out QaSignal? signal) ? signal : null)
            .OfType<QaSignal>()
            .Where(signal => signal.Kind == QaSignalKind.Stall && signal.Goal == "Group")];
        Assert.NotEmpty(stalls);
        Assert.All(stalls, signal =>
        {
            Assert.Equal(BruteEntry, signal.Target);
            Assert.Equal(BruteQuest, signal.Quest);
            Assert.Equal(0u, signal.Map);
            Assert.True(signal.Seconds >= 30, $"stalled {signal.Seconds}s");
        });
    }

    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a"), B = new("00000000-0000-0000-0000-00000000000b");

    private static PlayerbotGroupStallMember[] Pair(float ax = 0, bool busy = false, ulong target = 0)
        => [new(A, 0, new Vector3(ax, 0, 0), busy, target), new(B, 0, new Vector3(2, 0, 0), false, 0)];

    [Fact]
    public void TheWatch_StandingStill_ReTargetsAfterOneBound_DissolvesAfterTheNext()
    {
        var watch = new PlayerbotGroupStallWatch();
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 1_000, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 30_999, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 31_000, 30_000));
        Assert.Equal(30_000u, watch.IdleMs);
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 60_999, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Dissolve, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 61_000, 30_000));
        Assert.Equal(60_000u, watch.IdleMs);
    }

    [Fact]
    public void TheWatch_AHealthyGroup_MovingFightingOrResting_NeverStalls()
    {
        var watch = new PlayerbotGroupStallWatch();
        uint now = 0;
        for (int i = 0; i < 400; i++, now += 1_000)
        {
            // Walks 1 yard a second (10 yards in 10 s, the progress distance), then stands in a fight, then rests.
            PlayerbotGroupStallMember[] members = i < 100 ? Pair(ax: i) : i < 250 ? Pair(ax: 100, busy: true, target: 7) : Pair(ax: 100, busy: true);
            Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, members, now, 30_000));
        }
    }

    [Fact]
    public void TheWatch_DroppingAndRetakingTheSameTarget_IsNoProgress_ANewTargetIs_AndSoIsANewState()
    {
        var watch = new PlayerbotGroupStallWatch();
        watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 7), 0, 30_000);
        // The leader cannot reach creature 7: it drops it and takes it again every think.
        for (uint t = 1_000; t < 30_000; t += 1_000)
            Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: t % 2_000 == 0 ? 7ul : 0ul), t, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Engaging, Pair(), 30_000, 30_000));

        // Another creature is progress: the watch starts over.
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 40_000, 30_000));
        Assert.Equal(0, watch.Strikes);
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 69_999, 30_000));
        // So is a new state of the group.
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Leaving, Pair(target: 8), 70_000, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Leaving, Pair(target: 8), 99_999, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Leaving, Pair(target: 8), 100_000, 30_000));
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
