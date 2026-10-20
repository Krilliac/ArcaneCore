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
using Microsoft.Extensions.DependencyInjection;
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

    private static (CreatureTemplate Template, CreatureSpawn Spawn) Guard(uint entry, float dx, float dy, uint health = 60, uint npcFlags = 0, uint faction = 14, byte creatureType = 7) => (
        new CreatureTemplate
        {
            Entry = entry, Name = "Siege Guard " + entry, Faction = faction, CreatureType = creatureType, MinLevel = 10, MaxLevel = 10, DisplayIds = [903], NpcFlags = npcFlags,
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
            && siege.Raids.All(r => r.Group is { State: PlayerbotGroupState.Engaging })), await world.TraceAsync());
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

        Assert.True(await world.RunUntilAsync(120_000, Sample), await world.TraceAsync());
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
        }), await world.TraceAsync() + Environment.NewLine + string.Join(Environment.NewLine, raid.Announcer.Sent));

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

        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            Player next = Leader(world, raid);
            Assert.NotEqual(oldGuid, next.Guid);
            Assert.True(next.IsAlive);
            Assert.True(world.GroupManager.GetGroup(next.Guid)!.IsLeader(next.Guid));
            return true;
        });

        Assert.True(await world.RunUntilAsync(60_000, () => siege.Step >= 1), await world.TraceAsync());
    }

    [Fact]
    public async Task TheSiegeEndsWhenTheObjectiveDies_WithTheRaidsAnnouncingIt()
    {
        await using GroupTestWorld world = await StartWorld(guards: true);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);

        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Siege is null), await world.TraceAsync());
        Assert.True(world.Coordinator.SiegeOutcome == "won", world.Coordinator.SiegeOutcome + Environment.NewLine + await world.TraceAsync());
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

        Assert.True(stalled, await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(60_000, () => siege.ObjectiveSeen), await world.TraceAsync());

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
        }), await world.TraceAsync());
    }

    // --- the retreat: disengage, rally, one per engagement ------------------------------------------------------------------

    private static uint Now(GroupTestWorld world) => world.World.Host.World.NowMs;

    private static ObjectGuid Skull(GroupTestWorld world, PlayerbotSiegeRaid raid)
        => world.GroupManager.GetGroup(raid.Group!.LeaderGuid) is { } server ? server.TargetIcons[7] : default;

    private static List<Player> Living(GroupTestWorld world, PlayerbotSiegeRaid raid)
        => [.. raid.Group!.Members.Where(m => !m.Real && m.Joined).Select(m => world.World.Host.World.FindOnlinePlayer(m.Guid)).OfType<Player>().Where(p => p.IsAlive)];

    private static float YardsFrom(Player player, Vector3 point) => MathF.Sqrt(((player.X - point.X) * (player.X - point.X)) + ((player.Y - point.Y) * (player.Y - point.Y)));

    /// <summary>Kill every living member of <paramref name="raid"/> whose name contains <paramref name="part"/> (the bots are named after their class).</summary>
    private static Task<bool> KillAsync(GroupTestWorld world, PlayerbotSiegeRaid raid, string part) => world.OnWorldAsync(() =>
    {
        foreach (var member in raid.Group!.Members.Where(m => m.Name.Contains(part, StringComparison.Ordinal)))
            if (world.World.Host.World.FindOnlinePlayer(member.Guid) is { IsAlive: true } player)
            {
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
            }

        return true;
    });

    /// <summary>The scenario world with guards a raid cannot kill during a test (600 health at 62 and 63 yards), the objective far behind them (150 yards, out of the fight).</summary>
    private static async Task<GroupTestWorld> StartStrongGuards()
        => await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(GuardA, 62f, 2f, health: 600), Guard(GuardB, 63f, -2f, health: 600), Guard(Objective, 150f, 0f, health: 120)]);

    /// <summary>Where a retreat of <paramref name="siege"/> goes: the waypoint behind the step (the raid pulls from the second waypoint, so this is 40 yards out).</summary>
    private static Vector3 RetreatPoint(PlayerbotSiege siege) => siege.Step > 0 ? siege.Waypoints[siege.Step - 1] : siege.Rally;

    /// <summary>One raid of six, waypoints at 20 and 40 yards, run until it fights the guards with its leader more than 12 yards from its retreat point.</summary>
    private static async Task<(PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> IntoTheFight(GroupTestWorld world)
    {
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(240_000, () => siege.Step >= 1 && raid.WasInCombat && YardsFrom(Leader(world, raid), RetreatPoint(siege)) > 12f), await world.TraceAsync());
        return (siege, raid);
    }

    /// <summary>Keep the two strong guards at full health (a test that needs the fight to outlast the rally hold).</summary>
    private static void TopUpGuards(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        if (Leader(world, raid).Map?.FindUpdater<CreatureMapSystem>() is not { } system) return;
        foreach (Creature guard in system.Creatures)
            if (guard.Entry is GuardA or GuardB && guard.IsAlive) guard.Health = guard.MaxHealth;
    }

    [Fact]
    public async Task ARetreatUnderAttack_WalksToThePreviousWaypoint_ThenFightsBackThere()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");

        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);
        ObjectGuid skull = Skull(world, raid);
        uint since = Now(world);
        Vector3 point = RetreatPoint(siege);
        int step = siege.Step;
        float lastYards = YardsFrom(Leader(world, raid), point);
        var violations = new List<string>();
        var trail = new List<string>();

        // The disengage: nobody fights or casts, the leader walks to the first waypoint, nothing is re-marked.
        bool rallied = await world.RunUntilAsync(60_000, () =>
        {
            Player leader = Leader(world, raid);
            if (raid.Order == PlayerbotSiegeOrder.Retreat)
            {
                if (unchecked(Now(world) - since) >= 1_500)
                    foreach (var member in raid.Group!.Members)
                        if (world.World.Host.World.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && (member.AI?.TargetUnit is not null || player.Combat.Victim is not null))
                            violations.Add($"{member.Name} fights during the retreat (ai target {member.AI?.TargetUnit?.Guid}, victim {player.Combat.Victim?.Guid}) at +{unchecked(Now(world) - since)}ms");
                if (YardsFrom(leader, point) > lastYards + 1f) violations.Add($"the leader moved away from the retreat point: {lastYards:F1} to {YardsFrom(leader, point):F1} yd");
                if (Skull(world, raid) != skull) violations.Add("the skull changed during the retreat");
            }

            lastYards = YardsFrom(leader, point);
            if (unchecked(Now(world) - since) / 2_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - since)}ms order={raid.Order} leader {lastYards:F1} yd from the point, step {raid.Group!.Find(raid.Group.LeaderBotId)?.AI?.Step}, hp {leader.HealthPercent:F0}%, loop-blacklisted {ArcaneCore.World.Playerbots.PlayerbotMotion.IsBlacklisted(leader, point)}");
            return raid.Order == PlayerbotSiegeOrder.Rally;
        });
        Assert.True(rallied, string.Join(Environment.NewLine, trail) + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.Empty(violations);
        Assert.InRange(YardsFrom(Leader(world, raid), point), 0f, PlayerbotSiegeCommander.RallyYards + 1.5f);
        Assert.Equal(1, raid.Retreats);

        // At the rally point a member answers what attacks it.
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group!.Members.Any(m =>
            m.AI?.TargetUnit is { } target && world.World.Host.World.FindOnlinePlayer(m.Guid) is { } player && player.Combat.Attackers.Any(a => ReferenceEquals(a, target)))),
            await world.TraceAsync());

        // The latch ends by quiet or by the bound, and that was one retreat.
        Assert.True(await world.RunUntilAsync(90_000, () => !raid.RetreatLatched), await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);
        Assert.Equal(step, siege.Step);
    }

    [Fact]
    public async Task ARaidChasedToItsRallyPoint_FightsOnAfterRallyHoldMs_AndDoesNotRetreatAgainUntilANewEngagement()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());

        // The guards cannot die, so what followed the raid fights on past the hold.
        var stray = new List<string>();
        Vector3 point = RetreatPoint(siege);
        Assert.True(await world.RunUntilAsync(60_000, () => { TopUpGuards(world, raid); return raid.Order == PlayerbotSiegeOrder.Rally; }), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(PlayerbotSiegeCommander.RallyHoldMs + 30_000, () =>
        {
            TopUpGuards(world, raid);
            if (raid.Order == PlayerbotSiegeOrder.Rally && YardsFrom(Leader(world, raid), point) > PlayerbotSiegeCommander.RallyYards + 1.5f)
                stray.Add($"the leader left the rally point: {YardsFrom(Leader(world, raid), point):F1} yd");
            return !raid.RetreatLatched;
        }), await world.TraceAsync());
        Assert.Empty(stray);
        Assert.True(raid.WasInCombat, "the latch ended because the fight ended, not by the bound" + Environment.NewLine + await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);

        // Still fighting, still a lost fight by the numbers: no second retreat and no rally in this engagement.
        var orders = new List<PlayerbotSiegeOrder>();
        bool fought = true;
        await world.RunUntilAsync(10_000, () => { TopUpGuards(world, raid); orders.Add(raid.Order); fought &= raid.WasInCombat; return false; });
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Rally, orders);
        Assert.Equal(1, raid.Retreats);
        Assert.True(fought, "the raid left combat in the window: no engagement was kept" + Environment.NewLine + await world.TraceAsync());
        Assert.True(raid.RetreatedThisEngagement);
        Assert.Contains(raid.Order, new[] { PlayerbotSiegeOrder.Assault, PlayerbotSiegeOrder.AssignTank });
    }

    [Fact]
    public async Task ARetreatThatLosesItsLeader_IsCountedOnce()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (_, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());
        Guid oldLeader = raid.Group!.LeaderBotId;

        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            raid.PromoteTriedMs = Math.Max(1u, Now(world));
            return true;
        });

        // The raid stays unled for a few commander ticks (the promotion is held off), then the next tank takes over.
        int most = raid.Retreats;
        var states = new List<string>();
        string last = string.Empty;
        void Sample()
        {
            most = Math.Max(most, raid.Retreats);
            string now = $"{Now(world)}ms order={raid.Order} latched={raid.RetreatLatched} retreats={raid.Retreats} thisEngagement={raid.RetreatedThisEngagement} combat={raid.WasInCombat} leader={raid.Group!.LeaderBotId == oldLeader}";
            if (now[(now.IndexOf(' ', StringComparison.Ordinal))..] != last) states.Add(now);
            last = now[(now.IndexOf(' ', StringComparison.Ordinal))..];
        }

        await world.RunUntilAsync(2_000, () => { raid.PromoteTriedMs = Math.Max(1u, Now(world)); Sample(); return false; });
        Assert.Equal(oldLeader, raid.Group.LeaderBotId);

        // After the promotion the retreat goes on (the latch survived the unled ticks) and was begun only once.
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            Sample();
            return raid.Group.LeaderBotId != oldLeader && raid.Order is PlayerbotSiegeOrder.Retreat or PlayerbotSiegeOrder.Rally;
        }), await world.TraceAsync());
        await world.RunUntilAsync(5_000, () => { Sample(); return false; });
        Assert.True(most == 1, string.Join(Environment.NewLine, states));
        Assert.Equal(1, raid.Retreats);
    }

    [Fact]
    public async Task DeathsBeforeTheEngagement_AreNotLost_DeathsDuringItAre()
    {
        // Regression guard: the engagement snapshot already worked on the old code; this pins it.
        await using GroupTestWorld world = await StartStrongGuards();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await KillAsync(world, raid, "heal");

        Assert.True(await world.RunUntilAsync(240_000, () => raid.WasInCombat), await world.TraceAsync());
        var orders = new List<PlayerbotSiegeOrder>();
        await world.RunUntilAsync(3_000, () => { orders.Add(raid.Order); return false; });
        Assert.Equal(2, raid.DeadAtEngagement.Count);
        Assert.Equal(0, raid.LastFacts.DeadSinceEngagement);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);

        await KillAsync(world, raid, "Sgamage");
        await world.RunUntilAsync(3_000, () => { orders.Add(raid.Order); return false; });
        Assert.Equal(1, raid.LastFacts.DeadSinceEngagement);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);
        Assert.Equal(0, raid.Retreats);

        await KillAsync(world, raid, "Sgbmage");
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);
    }

    [Fact]
    public async Task AnUnledRaid_StillTracksItsEngagement()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(240_000, () => raid.WasInCombat && Leader(world, raid).X - Origin.X > 45f), await world.TraceAsync());

        // The leader falls and nobody takes over (the promotion is kept off for the rest of the test).
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            raid.PromoteTriedMs = Math.Max(1u, Now(world));
            return true;
        });
        Assert.True(await world.RunUntilAsync(10_000, () => { raid.PromoteTriedMs = Math.Max(1u, Now(world)); return !Leader(world, raid).IsAlive; }), await world.TraceAsync());
        await world.RunUntilAsync(3_000, () => { raid.PromoteTriedMs = Math.Max(1u, Now(world)); return false; });
        Assert.True(raid.WasInCombat, "the members still fight while the raid is unled");

        // The fight ends while the raid is unled: the raid is out of combat at the next ticks.
        await world.OnWorldAsync(() =>
        {
            Player attacker = Living(world, raid)[0];
            foreach (Creature guard in attacker.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Where(c => c.Entry is GuardA or GuardB or Objective && c.IsAlive).ToArray())
                attacker.Map.Combat.DealDamage(attacker, guard, guard.Health, direct: true, meleeDamage: true);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () =>
        {
            raid.PromoteTriedMs = Math.Max(1u, Now(world));
            return !raid.WasInCombat;
        }), "WasInCombat froze while the raid was unled" + Environment.NewLine + await world.TraceAsync());
        Assert.All(Living(world, raid), p => Assert.False(p.Combat.IsInCombat));
    }

    [Fact]
    public async Task ACommittedRaid_SaysTheCommitLineOnce_ThenItsPlanLines()
    {
        // Regression guard: this already held on the old code.
        await using GroupTestWorld world = await StartStrongGuards();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        raid.Retreats = PlayerbotSiegeCommander.MaxRetreats;
        Assert.True(await world.RunUntilAsync(240_000, () => raid.WasInCombat && Leader(world, raid).X - Origin.X > 45f), await world.TraceAsync());
        await KillAsync(world, raid, "mage");

        var orders = new List<PlayerbotSiegeOrder>();
        Assert.True(await world.RunUntilAsync(30_000, () => { orders.Add(raid.Order); return raid.CommitAnnounced; }), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(40_000, () =>
        {
            orders.Add(raid.Order);
            int at = raid.Announcer.Sent.IndexOf(PlayerbotSiegeCommander.CommitLine);
            return at >= 0 && raid.Announcer.Sent.Count > at + 1;
        }), string.Join(Environment.NewLine, raid.Announcer.Sent));

        Assert.Equal(1, raid.Announcer.Sent.Count(line => line == PlayerbotSiegeCommander.CommitLine));
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Rally, orders);
        Assert.Contains(raid.Order, new[] { PlayerbotSiegeOrder.Assault, PlayerbotSiegeOrder.AssignTank });
        Assert.Equal(PlayerbotSiegeCommander.MaxRetreats, raid.Retreats);
    }

    [Theory]
    [InlineData(12u)]
    [InlineData(7u)]
    public async Task AFriendlyOrNeutralNpcWithNpcFlags_IsNeitherAHostileNorSkulled(uint faction)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 62f, 2f, npcFlags: 3, faction: faction), Guard(Objective, 70f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];

        ObjectGuid npc = await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            Creature creature = leader.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == GuardA);
            Assert.False(leader.Map.Combat.Hooks.IsHostileTo(creature, leader), $"faction {faction} must not be hostile to the raid for this test to mean anything");
            return creature.Guid;
        });

        var seen = new List<string>();
        Assert.True(await world.RunUntilAsync(300_000, () =>
        {
            if (raid.LastFacts.Hostiles > 1) seen.Add($"{raid.LastFacts.Hostiles} hostiles counted (only the objective is hostile)");
            if (Skull(world, raid) == npc) seen.Add("the npc is skulled");
            if (raid.PullTarget == npc) seen.Add("the npc is the pull target");
            return world.Coordinator.Siege is null;
        }), await world.TraceAsync());
        Assert.Empty(seen);
        Assert.Equal("won", world.Coordinator.SiegeOutcome);
    }

    [Fact]
    public async Task ACritter_IsExcluded_UnlessItIsTheObjective()
    {
        // (a) A hostile critter (creature type 8) beside the objective is neither counted nor skulled.
        await using (GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 62f, 2f, creatureType: 8), Guard(Objective, 70f, 0f, health: 120)]))
        {
            (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
            PlayerbotSiege siege = await Start(world, bots, raids: 1);
            PlayerbotSiegeRaid raid = siege.Raids[0];
            ObjectGuid critter = await world.OnWorldAsync(() => Leader(world, raid).Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == GuardA).Guid);
            var seen = new List<string>();
            Assert.True(await world.RunUntilAsync(300_000, () =>
            {
                if (raid.LastFacts.Hostiles > 1) seen.Add($"{raid.LastFacts.Hostiles} hostiles counted (only the objective counts)");
                if (Skull(world, raid) == critter) seen.Add("the critter is skulled");
                if (raid.PullTarget == critter) seen.Add("the critter is the pull target");
                return world.Coordinator.Siege is null;
            }), await world.TraceAsync());
            Assert.Empty(seen);
        }

        // (b) A critter that is the objective is skulled and its death wins the siege.
        await using (GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 70f, 0f, health: 120, creatureType: 8)]))
        {
            (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
            PlayerbotSiege siege = await Start(world, bots, raids: 1);
            PlayerbotSiegeRaid raid = siege.Raids[0];
            bool skulled = false;
            Assert.True(await world.RunUntilAsync(300_000, () =>
            {
                Creature? objective = Leader(world, raid).Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Entry == Objective && c.IsAlive);
                if (objective is not null && Skull(world, raid) == objective.Guid) skulled = true;
                return world.Coordinator.Siege is null;
            }), await world.TraceAsync());
            Assert.True(skulled, "the objective critter was never skulled");
            Assert.Equal("won", world.Coordinator.SiegeOutcome);
        }
    }

    [Fact]
    public async Task ARetreatingLeaderWithNoPath_RalliesWhereItStands_AndTheMembersFightBack()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");

        // Walking is refused on this map from the retreat on (the planner answers no path), so the leader cannot leave the fight.
        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order is PlayerbotSiegeOrder.Retreat or PlayerbotSiegeOrder.Rally), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Rally), await world.TraceAsync());
        Assert.True(raid.RetreatNoPath);
        Assert.True(YardsFrom(Leader(world, raid), RetreatPoint(siege)) > PlayerbotSiegeCommander.RallyYards, "the leader did not move: it rallies in place");
        Assert.Equal(1, raid.Retreats);

        // The members answer their attackers, and the bound ends the latch (the guards cannot be killed meanwhile).
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group!.Members.Any(m =>
            m.AI?.TargetUnit is { } target && world.World.Host.World.FindOnlinePlayer(m.Guid) is { } player && player.Combat.Attackers.Any(a => ReferenceEquals(a, target)))),
            await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(PlayerbotSiegeCommander.RallyHoldMs + 30_000, () => { TopUpGuards(world, raid); return !raid.RetreatLatched; }), await world.TraceAsync());
        Assert.False(raid.RetreatNoPath);
    }

    [Fact]
    public async Task ASiegeWhoseOnlyLeaderIsOffTheSiegeMap_IsNotWon_ByAGridLoadedAtTheObjectivesPositionThere()
    {
        // The Deadmines world logs the bots in elsewhere than the open one: the objective stands 30 yards east of their start.
        Vector3 start = new(-8939.45f, -132.49f, 83.58f);
        (CreatureTemplate template, CreatureSpawn spawn) = Guard(Objective, 0f, 0f, health: 120);
        spawn = spawn with { X = start.X + 30f, Y = start.Y };
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null, dungeon: true, extraCreatures: [(template, spawn)]);
        (Guid, string)[] bots =
        [
            await world.AddBotAsync("Sgatank", 1, 1, 10, [GroupTestWorld.Taunt]),
            await world.AddBotAsync("Sgbtank", 1, 1, 10, [GroupTestWorld.Taunt]),
        ];
        await world.RunAsync(2_000);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Coordinator.AddSiegeWaypoint(0, start with { X = start.X + 10f }));
            return true;
        });
        PlayerbotSiege siege = await Start(world, [.. bots.Select(b => (b.Item1, b.Item2))], raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(60_000, () => siege.ObjectiveSeen), await world.TraceAsync());

        // The leader stands on another map (the Deadmines), on the objective's own X/Y: the grid there is loaded and no such creature lives there.
        // (The raid dissolves when its leader leaves the map, so the commander's tick never gets that far; the judgement of the objective itself must
        // refuse such a witness too, and is called here as the tick would call it.)
        Player witness = await world.OnWorldAsync(() => Leader(world, raid));
        Assert.True(await world.OnWorldAsync(() =>
        {
            Vector3 at = siege.ObjectivePosition;
            return world.World.Services.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports.TeleportTo(witness, 36, at.X, at.Y, at.Z, 0f);
        }), "teleport refused");
        Assert.True(await world.RunUntilAsync(20_000, () => witness.MapId == 36), await world.TraceAsync());
        bool won = await world.OnWorldAsync(() =>
        {
            Assert.True(witness.Map!.FindUpdater<CreatureMapSystem>()!.IsGridLoaded(siege.ObjectivePosition.X, siege.ObjectivePosition.Y),
                "the grid at the objective's X/Y on the other map must be loaded for this test to mean anything");
            Assert.True(siege.ObjectiveSeen);
            typeof(PlayerbotGroupCoordinator).GetMethod("WatchObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(world.Coordinator, [siege, witness, world.World.Host.World.NowMs]);
            return siege.Won;
        });

        Assert.False(won, "a witness on another map judged the objective dead");
        Assert.False(siege.ObjectiveUnseen);
    }

    [Fact]
    public async Task ANoPathRally_RetriesTheWalk_AndRetreatsAgainOnceThePathIsBack()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        uint[] allowed = world.Options.AllowedMaps;

        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Rally && raid.RetreatNoPath), await world.TraceAsync());
        Vector3 point = RetreatPoint(siege);
        Assert.True(YardsFrom(Leader(world, raid), point) > PlayerbotSiegeCommander.RallyYards);

        // The path comes back: within a few retries the flag clears and the raid retreats again, the leader walking to its point.
        world.Options.AllowedMaps = allowed;
        Assert.True(await world.RunUntilAsync(15_000, () => !raid.RetreatNoPath), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(15_000, () => raid.Order == PlayerbotSiegeOrder.Retreat || raid.Order == PlayerbotSiegeOrder.Rally && !raid.RetreatNoPath), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(60_000, () => YardsFrom(Leader(world, raid), point) <= PlayerbotSiegeCommander.RallyYards + 1.5f), await world.TraceAsync());
        Assert.Equal(1, raid.Retreats);
    }

    [Fact]
    public async Task APromotion_DuringANoPathRally_RestartsTheClockAndClearsTheFlag()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (_, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Order == PlayerbotSiegeOrder.Rally && raid.RetreatNoPath && raid.RallySinceMs != 0), await world.TraceAsync());
        (uint oldSince, Guid oldLeader) = (raid.RallySinceMs, raid.Group!.LeaderBotId);

        // The maps stay refused: the new leader has no path either, so only the promotion itself can restart the clock.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(1_500, () => raid.RallySinceMs != oldSince), "the new leader inherited the clock of the no-path rally" + Environment.NewLine + await world.TraceAsync());
        Assert.True(raid.RallyClockRestarted);
    }

    [Fact]
    public async Task APromotion_AtARealRetreatPoint_KeepsTheTimeAtThePoint()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (_, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(60_000, () => { TopUpGuards(world, raid); return raid.Order == PlayerbotSiegeOrder.Rally && !raid.RetreatNoPath && raid.RallySinceMs != 0; }), await world.TraceAsync());
        (uint since, Guid oldLeader) = (raid.RallySinceMs, raid.Group!.LeaderBotId);

        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            leader.Health = 0;
            leader.Map!.Combat.KillPlayer(leader);
            return true;
        });
        Assert.True(await world.RunUntilAsync(30_000, () => raid.Group.LeaderBotId != oldLeader), await world.TraceAsync());
        var changed = new List<uint>();
        await world.RunUntilAsync(2_000, () => { TopUpGuards(world, raid); if (raid.RallySinceMs != since) changed.Add(raid.RallySinceMs); return false; });
        Assert.Empty(changed);
        Assert.False(raid.RallyClockRestarted);
    }

    [Fact]
    public async Task AFlappingPath_DoesNotKeepTheRallyBoundFromEver_BeingReached()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        await KillAsync(world, raid, "mage");
        uint[] allowed = world.Options.AllowedMaps;
        world.Options.AllowedMaps = [1];
        Assert.True(await world.RunUntilAsync(30_000, () => raid.RetreatLatched), await world.TraceAsync());

        // The path flaps: it is back when a retry comes (the flag clears), and refused again at once (the next walk finds none).
        Vector3 point = RetreatPoint(siege);
        int flaps = 0;
        uint refusedAt = Now(world);
        var trail = new List<string>();
        bool wasFlagged = false;
        uint began = Now(world);
        bool ended = await world.RunUntilAsync(2 * PlayerbotSiegeCommander.RallyHoldMs + 3_000 + 20_000, () =>
        {
            TopUpGuards(world, raid);
            if (unchecked(Now(world) - began) % 2000 < 50) trail.Add($"[{unchecked(Now(world) - began)} {raid.Order} noPath={raid.RetreatNoPath} since={raid.RallySinceMs} restarted={raid.RallyClockRestarted} leader={Leader(world, raid).IsAlive}]");
            if (raid.RetreatNoPath)
            {
                // Refused for a while, so the leader rallies (and a retry finds the path back only after that).
                if (unchecked(Now(world) - refusedAt) >= 3_500) { world.Options.AllowedMaps = allowed; PlayerbotMotion.ForgetLoops(Leader(world, raid)); wasFlagged = true; }
            }
            else if (wasFlagged)
            {
                // The route the retry planned is dropped (a stale route, a lift ride that ended): the next walk plans again and finds none.
                world.Options.AllowedMaps = [1];
                refusedAt = Now(world);
                Player dragged = Leader(world, raid);
                dragged.Relocate(point.X + 25f, point.Y, point.Z, 0, Now(world)); // pursuit keeps the leader off the point
                typeof(PlayerbotGroupAI).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(raid.Group!.Find(raid.Group.LeaderBotId)!.AI, null);
                wasFlagged = false;
                flaps++;
            }
            return !raid.RetreatLatched;
        });
        Assert.True(ended, $"the latch outlived every bound after {flaps} flaps, {unchecked(Now(world) - began)} ms" + Environment.NewLine + string.Join(" ", trail));
        Assert.Equal(1, raid.Retreats);
    }
}
