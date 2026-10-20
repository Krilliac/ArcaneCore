using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A siege on the scenario world (real handlers, the manual clock, open flat ground): two bot-led raids under one plan, led by bots with
/// nobody else involved. The way runs east from where the bots log in (<see cref="Origin"/>): waypoints at 10 and 20 yards, two guards at 62, the objective ogre
/// (<see cref="Objective"/>) at 70.
/// </summary>
public sealed class PlayerbotSiegeWorldTests
{
    private const uint GuardA = 990801;
    private const uint GuardB = 990802;

    /// <summary>The creature the sieges are after: a level 10 body that takes a raid a while.</summary>
    private const uint Objective = 990803;

    /// <summary>Where a fresh bot character stands (the graveyard it logs in at): the way and its creatures are laid out from here.</summary>
    private static readonly Vector3 Origin = new(-8913f, 550f, StartZ);

    private static (CreatureTemplate Template, CreatureSpawn Spawn) Guard(uint entry, float dx, float dy, uint health = 60, uint npcFlags = 0) => (
        new CreatureTemplate
        {
            Entry = entry, Name = "Siege Guard " + entry, Faction = 14, CreatureType = 7, MinLevel = 10, MaxLevel = 10, DisplayIds = [903], NpcFlags = npcFlags,
            MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = 1, MaxMeleeDamage = 1, MeleeBaseAttackTime = 2000, Rank = 0,
        },
        new CreatureSpawn
        {
            Guid = entry, Entry = entry, MapId = 0, X = Origin.X + dx, Y = Origin.Y + dy, Z = Origin.Z, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600,
        });

    private static Vector3 At(float dx) => new(Origin.X + dx, Origin.Y, Origin.Z);
    private static async Task<GroupTestWorld> StartWorld(bool guards, Action<PlayerbotOptions>? configure = null)
        => await GroupTestWorld.StartAsync([], configure,
            extraCreatures: guards ? [Guard(GuardA, 62f, 2f), Guard(GuardB, 63f, -2f), Guard(Objective, 70f, 0f, health: 120)] : [Guard(Objective, 70f, 0f, health: 120)]);

    /// <summary>Two warriors, two priests and two mages, each level 10, at the start; waypoints at 10 and 20 yards.</summary>
    private static async Task<(Guid Id, string Name)[]> Enlist(GroupTestWorld world, params float[] waypoints)
    {
        (Guid, string)[] bots =
        [
            await world.AddBotAsync("Sgatank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgbtank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgaheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgbheal", 1, 5, 10, []),
            await world.AddBotAsync("Sgamage", 1, 8, 10, []),
            await world.AddBotAsync("Sgbmage", 1, 8, 10, []),
        ];
        await world.RunAsync(2_000); // the bots are in the world and the coordinator has seen them
        await world.OnWorldAsync(() =>
        {
            foreach (float dx in waypoints) Assert.Null(world.Coordinator.AddSiegeWaypoint(0, At(dx)));
            return true;
        });
        return bots;
    }

    private static async Task<PlayerbotSiege> Start(GroupTestWorld world, (Guid Id, string Name)[] bots, int raids)
    {
        (PlayerbotSiege? siege, string? refusal) = await world.OnWorldAsync(() =>
        {
            var skipped = new List<(Guid, string)>();
            PlayerbotSiege? started = world.Coordinator.StartSiege([.. bots.Select(b => b.Id)], Objective, raids, skipped, out string? why);
            return (started, why ?? string.Join(',', skipped.Select(s => s.Item2)));
        });
        Assert.NotNull(siege);
        Assert.True(string.IsNullOrEmpty(refusal), refusal);
        Assert.True(await world.RunUntilAsync(60_000, () => siege.Raids.Count == raids
            && siege.Raids.All(r => r.Group is { State: PlayerbotGroupState.Engaging })), world.Trace());
        return siege;
    }

    private static Player Leader(GroupTestWorld world, PlayerbotSiegeRaid raid) => world.World.Host.World.FindOnlinePlayer(raid.Group!.LeaderGuid)!;

    [Fact]
    public async Task TwoRaids_FormUnderOneSiege_AsRaids_LedByATank_OutsideTheGroupSlots()
    {
        await using GroupTestWorld world = await StartWorld(guards: false, options => options.Groups.MaxGroups = 1);
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);

        PlayerbotSiege siege = await Start(world, bots, raids: 2);

        await world.OnWorldAsync(() =>
        {
            Assert.Equal(2, world.Coordinator.Groups.Count(g => g.Siege is not null));
            Assert.Equal([3, 3], siege.Raids.Select(r => r.Group!.Members.Count).ToArray());
            foreach (PlayerbotSiegeRaid raid in siege.Raids)
            {
                Group server = world.GroupManager.GetGroup(Leader(world, raid).Guid)!;
                Assert.True(server.IsRaid);
                Assert.True(server.IsLeader(raid.Group!.LeaderGuid));
                Assert.Equal(PlayerbotGroupRole.Tank, raid.Group.Find(raid.Group.LeaderBotId)!.Role);
                Assert.Equal(PlayerbotGroupGoalKind.Siege, raid.Group.Goal.Kind);
            }

            Assert.Equal(2, siege.Waypoints.Count);
            return true;
        });
    }

    [Fact]
    public async Task TheRaidsAdvanceInLockstep_NoLeaderPastTheWaypointTheSiegeIsHeadingFor()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);

        // The second raid starts 25 yards behind the first: the first reaches the waypoint and must wait for it.
        await world.OnWorldAsync(() =>
        {
            foreach (var member in siege.Raids[1].Group!.Members)
                { Player behind = world.World.Host.World.FindOnlinePlayer(member.Guid)!; behind.Relocate(behind.X - 25f, behind.Y, behind.Z, 0, world.World.Host.World.NowMs); }
            return true;
        });

        var overshoot = new List<string>();
        var firstArrival = new Dictionary<int, (float First, float Second)>();
        int lastStep = 0;
        bool Sample()
        {
            float[] x = [.. siege.Raids.Select(r => Leader(world, r).X - Origin.X)];
            if (siege.Step < siege.Waypoints.Count)
            {
                float limit = siege.Waypoints[siege.Step].X - Origin.X + 7f;
                for (int i = 0; i < x.Length; i++)
                    if (x[i] > limit) overshoot.Add($"step {siege.Step}: raid {i + 1} at x+{x[i]:F1} (limit {limit:F1})");
            }

            if (siege.Step != lastStep)
            {
                firstArrival[lastStep] = (x[0], x[1]);
                lastStep = siege.Step;
            }

            return siege.Step >= siege.Waypoints.Count;
        }

        Assert.True(await world.RunUntilAsync(120_000, Sample), world.Trace());
        Assert.Empty(overshoot);
        // When the step moved on from the first waypoint both leaders stood at it.
        Assert.InRange(firstArrival[0].First, 10f - 7f, 10f + 7f);
        Assert.InRange(firstArrival[0].Second, 10f - 7f, 10f + 7f);
        Assert.Equal(2, lastStep);
    }

    [Fact]
    public async Task TheLeaderMarksSkullAndCross_ThroughTheClientPacket_AndWarnsTheRaid_NoOftenerThanEveryThreeSeconds()
    {
        await using GroupTestWorld world = await StartWorld(guards: true);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);

        var leaderOpcodes = new ConcurrentQueue<WorldOpcode>();
        var warnings = new ConcurrentQueue<uint>();
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await world.OnWorldAsync(() =>
        {
            world.Session(raid.Group!.LeaderBotId).ManagedDispatchObserver = opcode => leaderOpcodes.Enqueue(opcode);
            Guid member = raid.Group.Members.First(m => m.BotId != raid.Group.LeaderBotId).BotId;
            world.Session(member).ManagedPacketObserver = (opcode, payload) =>
            {
                // SMSG_MESSAGECHAT starts with the chat type: a member of the raid is told the leader's warning by the server.
                if (opcode == WorldOpcode.SmsgMessagechat && payload.Length > 0 && payload[0] == (byte)ChatType.RaidWarning)
                    warnings.Enqueue(world.World.Host.World.NowMs);
            };
            return true;
        });

        bool skull = false, cross = false;
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            if (world.GroupManager.GetGroup(Leader(world, raid).Guid) is { } server)
            {
                skull |= !server.TargetIcons[7].IsEmpty;
                cross |= !server.TargetIcons[6].IsEmpty;
            }

            return skull && cross && warnings.Count >= 3;
        }), world.Trace() + Environment.NewLine + string.Join(Environment.NewLine, raid.Announcer.Sent));

        Assert.Contains(WorldOpcode.MsgRaidTargetUpdate, leaderOpcodes);
        Assert.Contains(WorldOpcode.CmsgMessagechat, leaderOpcodes);
        uint[] times = [.. warnings];
        for (int i = 1; i < times.Length; i++) Assert.True(times[i] - times[i - 1] >= PlayerbotSiegeAnnouncer.MinGapMs, $"warnings {times[i - 1]} and {times[i]} ms apart");
        string[] sent = [.. raid.Announcer.Sent];
        for (int i = 1; i < sent.Length; i++) Assert.NotEqual(sent[i - 1], sent[i]);
        Assert.True(sent.Length >= 3);
    }

    [Fact]
    public async Task ADeadLeader_PassesTheRaidToTheLivingTank_AndTheSiegeGoesOn()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        (Guid oldLeader, ObjectGuid oldGuid) = (raid.Group!.LeaderBotId, raid.Group.LeaderGuid);

        // Both warriors are in the one raid: when the leader falls, the other tank is the best successor.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            return true;
        });

        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), world.Trace());
        await world.OnWorldAsync(() =>
        {
            Player next = Leader(world, raid);
            Assert.NotEqual(oldGuid, next.Guid);
            Assert.True(next.IsAlive);
            Assert.True(world.GroupManager.GetGroup(next.Guid)!.IsLeader(next.Guid));
            return true;
        });

        Assert.True(await world.RunUntilAsync(60_000, () => siege.Step >= 1), world.Trace());
    }

    [Fact]
    public async Task TheSiegeEndsWhenTheObjectiveDies_WithTheRaidsAnnouncingIt()
    {
        await using GroupTestWorld world = await StartWorld(guards: true);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);

        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Siege is null), world.Trace());
        Assert.True(world.Coordinator.SiegeOutcome == "won", world.Coordinator.SiegeOutcome + Environment.NewLine + world.Trace());
        Assert.Contains(siege.Raids, r => r.Announcer.Sent.Any(line => line.Contains("Siege complete", StringComparison.Ordinal)));
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Groups.Count(g => g.Siege is not null) == 0));
    }

    /// <summary>One raid of two is left without a living leader (nobody promotes a new one): the other raid must keep waiting for it.</summary>
    private static async Task<(PlayerbotSiege Siege, PlayerbotSiegeRaid Fallen)> LeaderlessRaid(GroupTestWorld world)
    {
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid fallen = siege.Raids[1];
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, fallen);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            fallen.PromoteTriedMs = Math.Max(1u, world.World.Host.World.NowMs); // no promotion before the run below keeps it off
            return true;
        });
        return (siege, fallen);
    }

    [Fact]
    public async Task ARaidWhoseLeaderIsDead_StillHoldsTheOtherRaidAtItsWaypoint()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);

        // The fallen raid's promotion is kept off for the whole run, so it stays without a leader while the first raid walks to the waypoint.
        int highestStep = 0;
        await world.RunUntilAsync(30_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, world.World.Host.World.NowMs);
            highestStep = Math.Max(highestStep, siege.Step);
            return false;
        });

        Assert.Equal(0, highestStep);
        await world.OnWorldAsync(() =>
        {
            Assert.False(Leader(world, fallen).IsAlive);
            float x = Leader(world, siege.Raids[0]).X - Origin.X;
            Assert.InRange(x, 10f - 7f, 10f + 7f); // it got there, and waited
            return true;
        });
    }

    [Fact]
    public async Task ASiegeWithoutProgressFor120Seconds_IsReportedStalled_InTheStatus()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);

        bool stalled = await world.RunUntilAsync(PlayerbotSiegeCommander.StallMs + 60_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, world.World.Host.World.NowMs);
            return world.Coordinator.SiegeStatus(null)[0].Contains("stalled", StringComparison.Ordinal);
        });

        Assert.True(stalled, world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("no progress", StringComparison.Ordinal));
        Assert.Equal(0, siege.Step);
        Assert.NotNull(world.Coordinator.Siege);
    }

    [Fact]
    public async Task AnObjectiveWhoseGridUnloads_IsUnseen_NotDead_AndTheSiegeGoesOn()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        Assert.True(await world.RunUntilAsync(60_000, () => siege.ObjectiveSeen), world.Trace());

        await world.OnWorldAsync(() =>
        {
            CreatureMapSystem system = Leader(world, siege.Raids[0]).Map!.FindUpdater<CreatureMapSystem>()!;
            Creature objective = system.Creatures.First(c => c.Entry == Objective && c.IsAlive);
            Assert.True(system.IsGridLoaded(objective.X, objective.Y));
            typeof(CreatureMapSystem).GetMethod("UnloadGrid", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(system, [CreatureMapSystem.ComputeGrid(objective.X, objective.Y)]);
            Assert.False(system.IsGridLoaded(objective.X, objective.Y));
            Assert.DoesNotContain(system.Creatures, c => c.Entry == Objective && c.IsAlive);
            return true;
        });

        await world.RunAsync(8_000);

        await world.OnWorldAsync(() =>
        {
            Assert.Same(siege, world.Coordinator.Siege);
            Assert.False(siege.Won);
            Assert.True(siege.ObjectiveUnseen);
            Assert.Null(world.Coordinator.SiegeOutcome);
            Assert.Contains("objective unseen", world.Coordinator.SiegeStatus(null)[0], StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public async Task AHostileCreatureWithGossipAndQuestFlags_IsPulledAndMarked_LikeAnyOther()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 62f, 2f, npcFlags: 3), Guard(Objective, 70f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];

        bool skulled = false;
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Player leader = Leader(world, raid);
            Creature? flagged = leader.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.FirstOrDefault(c => c.Entry == GuardA);
            if (flagged is not null && world.GroupManager.GetGroup(leader.Guid) is { } server && server.TargetIcons[7] == flagged.Guid) skulled = true;
            return skulled;
        }), world.Trace());
    }
}
