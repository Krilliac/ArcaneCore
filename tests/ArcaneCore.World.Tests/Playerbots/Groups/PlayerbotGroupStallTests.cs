using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
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
    private const uint Fireball = 133, LesserHeal = 2050, PowerWordFortitude = 1243, Smite = 585;

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
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == healer.Id)), await world.TraceAsync());
        await world.AddBotAsync("Stallmage", Human, Mage, 1, [Fireball]);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(healer.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);

        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(150_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
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

    /// <summary>
    /// Once the group has formed (still gathering or already travelling), puts every bot on the side of the meeting point opposite the
    /// approach point, <paramref name="flatYards"/> from it and <paramref name="rise"/> above its height. The approach point is fixed at
    /// formation, on the side the leader stood on then, so from here the leader never walks to it.
    /// </summary>
    private static async Task<PlayerbotGroupCoordinator.BotGroup> FormThenMoveToTheFarSideAsync(GroupTestWorld world, (Guid Id, string Name)[] bots, float flatYards, float rise)
    {
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            Vector3 meeting = group.Goal.Meeting;
            Vector3 away = Vector3.Normalize(new Vector3(meeting.X - group.Approach.X, meeting.Y - group.Approach.Y, 0));
            Vector3 spot = meeting + (away * flatYards) + new Vector3(0, 0, rise);
            foreach ((Guid id, _) in bots)
                world.Player(id).Relocate(spot.X, spot.Y, spot.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        return group;
    }

    private static float Flat(Player player, Vector3 point) => MathF.Sqrt(((player.X - point.X) * (player.X - point.X)) + ((player.Y - point.Y) * (player.Y - point.Y)));

    [Fact]
    public async Task ALeaderOnTheFarSideOfTheObjective_WithinTheRing_StartsEngaging_WithoutComingNearTheCreature()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        var warrior = await world.AddBotAsync("Farwar", Human, Warrior, 1, []);
        var mage = await world.AddBotAsync("Farmage", Human, Mage, 1, [Fireball]);
        // 33 yards flat from the brute on its far side: outside the 30-yard approach, inside the ring (30 + the slack of 5).
        PlayerbotGroupCoordinator.BotGroup group = await FormThenMoveToTheFarSideAsync(world, [warrior, mage], 33f, 0f);

        Assert.True(await world.RunUntilAsync(5_000, () => group.State is not (PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Engaging, group.State);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            float flat = Flat(leader, group.Goal.Meeting);
            Assert.InRange(flat, 28f, 34f);
            Assert.False(leader.Combat.IsInCombat, world.Trace()); // on the world thread
            return true;
        });
    }

    [Fact]
    public async Task ALeaderWithinTheRingFlat_ButOnAnotherFloor_StaysTravelling()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        var warrior = await world.AddBotAsync("Highwar", Human, Warrior, 1, []);
        var mage = await world.AddBotAsync("Highmage", Human, Mage, 1, [Fireball]);
        PlayerbotGroupCoordinator.BotGroup group = await FormThenMoveToTheFarSideAsync(world, [warrior, mage], 33f, 30f);

        await world.RunAsync(1_500);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            Assert.True(Flat(leader, group.Goal.Meeting) <= PlayerbotGroupCoordinator.QuestApproachYards + PlayerbotGroupCoordinator.ApproachSlackYards, "flat distance is inside the ring");
            Assert.True(leader.Z - group.Goal.Meeting.Z > PlayerbotGroupCoordinator.ApproachHeightYards, "the leader is above the height bound");
            return true;
        });
    }

    [Fact]
    public async Task ALeaderOnTheFarSideOfAnEntrance_WithinTheRing_StartsEntering_BeforeItWalksIn()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 30, dungeon: true);
        var tank = await world.AddBotAsync("Ringtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Ringprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Ringmage", Human, Mage, 12, [Fireball], EntranceArea);
        // 18 yards flat from the entrance on its far side: outside the 15-yard approach, inside the ring (15 + 5).
        PlayerbotGroupCoordinator.BotGroup group = await FormThenMoveToTheFarSideAsync(world, [tank, healer, mage], 18f, 0f);

        Assert.True(await world.RunUntilAsync(5_000, () => group.State is not (PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling)), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Entering, group.State);
        await world.OnWorldAsync(() =>
        {
            // Not through the door yet: it was the ring that moved the group on, not the walk to the approach point past the entrance.
            Player leader = world.Player(group.LeaderBotId);
            Assert.Equal(0u, leader.MapId);
            Assert.InRange(Flat(leader, group.Goal.Meeting), 14f, 19f);
            return true;
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

    [Fact]
    public void TheWatch_WithSeveralUnreachableTargets_EveryReTargetPicksAnother_ButTheGroupStillDissolvesOnTheSecondStrike()
    {
        var watch = new PlayerbotGroupStallWatch();
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 7), 0, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 7), 30_000, 30_000));
        // The leader gave creature 7 up and took creature 8 a second later (the re-target's own doing): the strike stands.
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 31_000, 30_000));
        Assert.Equal(1, watch.Strikes);
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 59_999, 30_000));
        Assert.Equal(PlayerbotGroupStallVerdict.Dissolve, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 60_000, 30_000));
        Assert.Equal(2, watch.Strikes);
        Assert.Equal(60_000u, watch.IdleMs);
    }

    [Fact]
    public void TheWatch_ANewTargetLongAfterTheReTarget_OrWithMovement_StillStartsOver()
    {
        var watch = new PlayerbotGroupStallWatch();
        watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 7), 0, 30_000);
        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 7), 30_000, 30_000));
        // Past the grace the new target is the group's own doing.
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 30_000 + PlayerbotGroupStallWatch.RetargetGraceMs + 1, 30_000));
        Assert.Equal(0, watch.Strikes);

        Assert.Equal(PlayerbotGroupStallVerdict.Retarget, watch.Observe(PlayerbotGroupState.Engaging, Pair(target: 8), 100_000, 30_000));
        // Moving right after the re-target is progress whatever the target.
        Assert.Equal(PlayerbotGroupStallVerdict.None, watch.Observe(PlayerbotGroupState.Engaging, Pair(ax: 20, target: 9), 101_000, 30_000));
        Assert.Equal(0, watch.Strikes);
    }

    [Fact]
    public async Task AGroupWithARealPlayerInIt_IsNeverStalledOrDissolvedByTheWatch()
    {
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 30;
        }, services: logs.Register);
        await world.AddBotAsync("Realwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Realmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Engaging)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        await world.OnWorldAsync(() =>
        {
            group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.NewGuid(), default, "Realone", PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, real: true));
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new AwayFrom(Brute, 25f));
            return true;
        });

        // Well past two stall bounds (30 s each): the same group without the real member dissolves by 150 s (the test above).
        await world.RunAsync(200_000);
        Assert.Contains(world.Coordinator.Groups, g => ReferenceEquals(g, group));
        Assert.NotEqual("stalled", group.Reason);
        Assert.DoesNotContain(world.Coordinator.Events, line => line.Contains($"group {group.Id} stalled", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("goal=Group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStallOutsideEngaging_ReportsTargetZero_InTheShapeBotQaParses()
    {
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 30;
        }, services: logs.Register);
        // No way leads anywhere: the group forms and stands before it ever engages.
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());
            return true;
        });
        await world.AddBotAsync("Zerowar", Human, Warrior, 1, []);
        await world.AddBotAsync("Zeromage", Human, Mage, 1, [Fireball]);

        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.StallCount > 0), await world.TraceAsync());
        Assert.NotEqual(PlayerbotGroupState.Engaging, world.Coordinator.Groups.Single().State);
        QaSignal[] stalls = [.. logs.Lines
            .Where(line => line.StartsWith("Warning " + typeof(ManagedPlayerbotFeature).FullName + ": ", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])
            .Select(message => PlayerbotQaSignalParser.TryParse(message, DateTime.UtcNow, null, out QaSignal? signal) ? signal : null)
            .OfType<QaSignal>()
            .Where(signal => signal.Kind == QaSignalKind.Stall && signal.Goal == "Group")];
        Assert.NotEmpty(stalls);
        Assert.All(stalls, signal => Assert.Equal(0u, signal.Target));
    }

    /// <summary>
    /// A run that finished (a dungeon's boss fell) and walks out: a bot stuck inside makes no progress, but the leaving state has its own
    /// bound (<c>BringOut</c> teleports the stragglers to the entrance). The stall watch must not dissolve the group as "stalled" first:
    /// that released the bots inside and recorded a successful run as failed.
    /// </summary>
    [Fact]
    public async Task AFinishedRunLeaving_WithAMemberStuckInside_IsBroughtOut_NotDissolvedAsStalled()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options => options.StallSeconds = 30, dungeon: true);
        var tank = await world.AddBotAsync("Leavetank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Leaveprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Leavemage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == 36)), await world.TraceAsync());

        // The run is over and nobody can walk out: no way leads anywhere, so every member stands inside.
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());
            group.Succeeded = true;
            group.Reason = null;
            group.State = PlayerbotGroupState.Leaving;
            group.StateSinceMs = world.World.Host.World.NowMs;
            return true;
        });

        // Past two stall bounds, before the leaving state's own bound: the group is still there, still a success.
        await world.RunAsync(2 * 30_000 + 20_000);
        Assert.Contains(world.Coordinator.Groups, g => ReferenceEquals(g, group));
        Assert.True(group.Succeeded);
        Assert.NotEqual("stalled", group.Reason);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
        Assert.Equal(0, world.Coordinator.StallCount);

        // The leaving state's bound brings the stragglers out and the run is recorded as done.
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepTimeoutMs, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, line => line.Contains("could not walk out: brought to the entrance", StringComparison.Ordinal));
        Assert.True(group.Succeeded);
        Assert.Equal("completed", group.Reason);
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
        await world.OnWorldAsync(() =>
        {
            Assert.All(bots, b => Assert.Equal(0u, world.Player(b.Id).MapId));
            return true;
        });
    }

    [Fact]
    public async Task GatheringAndRegroupingWaitStates_DoNotReportGroupStallsWhileLeaderHoldsForMembers()
    {
        var logs = new LogCapture();
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 30;
        }, services: logs.Register);
        var leaderBot = await world.AddBotAsync("Gatherwar", Human, Warrior, 1, []);
        var farBot = await world.AddBotAsync("Gathermage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        // Keep the group in Gathering with a member far away and no path, so the leader holds at the meeting point past two StallSeconds bounds.
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new NoPath());
            world.Player(farBot.Id).Relocate(-9200f, 300f, 70f, 0f, world.World.Host.World.NowMs);
            group.State = PlayerbotGroupState.Gathering;
            group.StateSinceMs = world.World.Host.World.NowMs;
            return true;
        });

        await world.RunAsync(2 * 30_000 + 20_000);
        Assert.Equal(PlayerbotGroupState.Gathering, group.State);
        Assert.Equal(0, world.Coordinator.StallCount);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("goal=Group", StringComparison.Ordinal));

        // Now place the group in Regrouping (e.g. after a wipe while a dead member has not yet recovered) past two StallSeconds bounds.
        await world.OnWorldAsync(() =>
        {
            Player far = world.Player(farBot.Id);
            far.Health = 0;
            far.Combat.DeathState = ArcaneCore.Game.Combat.DeathState.Corpse;
            group.Find(farBot.Id)!.AI = null;
            group.State = PlayerbotGroupState.Regrouping;
            group.StateSinceMs = world.World.Host.World.NowMs;
            return true;
        });

        await world.RunAsync(2 * 30_000 + 20_000);
        Assert.Equal(PlayerbotGroupState.Regrouping, group.State);
        Assert.Equal(0, world.Coordinator.StallCount);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("goal=Group", StringComparison.Ordinal));
    }

    /// <summary>No path leads anywhere.</summary>
    private sealed class NoPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
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
