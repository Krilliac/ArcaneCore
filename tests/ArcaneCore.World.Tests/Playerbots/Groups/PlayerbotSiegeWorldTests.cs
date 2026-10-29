using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A siege on the scenario world (real handlers, the manual clock, open flat ground): two bot-led raids under one plan, led by bots with
/// nobody else involved. The way runs east from where the bots log in (<see cref="Origin"/>): waypoints at 10 and 20 yards, two guards at 62, the objective ogre
/// (<see cref="Objective"/>) at 70.
/// </summary>
public sealed class PlayerbotSiegeWorldTests(ITestOutputHelper output)
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

    private static PlayerbotStay StayAt(GroupTestWorld world, Vector3 at)
        => new(0, at, 30f, 1, unchecked(world.World.Host.World.NowMs + 600_000));

    [Fact]
    public async Task StartSiege_EnlistsBotsHoldingAScenarioStay_AndClearsTheirStays()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        await world.OnWorldAsync(() =>
        {
            foreach ((Guid id, _) in bots) world.Bots.FindBrain(id)!.Stay = StayAt(world, Origin);
            return true;
        });
        await world.RunAsync(2_000);

        (PlayerbotSiege? siege, string skipped) = await world.OnWorldAsync(() =>
        {
            var skippedBots = new List<(Guid, string)>();
            PlayerbotSiege? started = world.Coordinator.StartSiege([.. bots.Select(b => b.Id)], Objective, 2, skippedBots, out string? why);
            return (started, why ?? string.Join(',', skippedBots.Select(s => s.Item2)));
        });

        Assert.NotNull(siege);
        Assert.True(string.IsNullOrEmpty(skipped), skipped); // a stay is not a hold: every stayed bot was free and was enlisted
        Assert.Equal([3, 3], siege.Raids.Select(r => r.Group!.Members.Count).ToArray());
        await world.OnWorldAsync(() =>
        {
            foreach ((Guid id, _) in bots) Assert.Null(world.Bots.FindBrain(id)!.Stay);
            return true;
        });
    }

    [Fact]
    public async Task StartSiege_StillSkipsABotHoldingADefendPost_AsBusy()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 10f, 20f);
        await world.OnWorldAsync(() =>
        {
            Player p = world.Player(bots[5].Id);
            world.Bots.FindBrain(bots[5].Id)!.Defend = new PlayerbotDefendPost(p.MapId, new Vector3(p.X, p.Y, p.Z), 30f, 2);
            return true;
        });
        await world.RunAsync(2_000);

        List<(Guid BotId, string Reason)> skipped = await world.OnWorldAsync(() =>
        {
            var skippedBots = new List<(Guid BotId, string Reason)>();
            world.Coordinator.StartSiege([.. bots.Select(b => b.Id)], Objective, 1, skippedBots, out _);
            return skippedBots;
        });

        Assert.Equal([(bots[5].Id, "busy")], skipped);
    }

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

    [Fact]
    public async Task AStoppedSiege_RecordsNoGroupSuccess_WhereAWonOneDoes()
    {
        // Trust moves only on a success (or the first death in a wipe): the exact witness of GroupSucceeded for a pair.
        int successTrust = 500 + new PlayerbotSocialMemoryOptions().TrustPerGroupSuccess;

        await using GroupTestWorld stopped = await StartWorld(guards: true);
        (Guid Id, string Name)[] stoppedBots = await Enlist(stopped, 20f, 40f);
        await Start(stopped, stoppedBots, raids: 2);
        Assert.True(await stopped.OnWorldAsync(() => stopped.Coordinator.StopSiege("gm")));
        Assert.True(await stopped.RunUntilAsync(60_000, () => stopped.Coordinator.Groups.Count == 0), await stopped.TraceAsync());
        Assert.Equal("stopped: gm", stopped.Coordinator.SiegeOutcome);
        Assert.Contains(stoppedBots, b => stopped.Bots.Social.Memory.Snapshot(b.Id).Count > 0); // the raids formed, so the bots did meet
        Assert.All(stoppedBots, b => Assert.All(stopped.Bots.Social.Memory.Snapshot(b.Id), r => Assert.Equal((short)500, r.TrustPermille)));

        await using GroupTestWorld won = await StartWorld(guards: true);
        (Guid Id, string Name)[] wonBots = await Enlist(won, 20f, 40f);
        await Start(won, wonBots, raids: 2);
        Assert.True(await won.RunUntilAsync(300_000, () => won.Coordinator.Siege is null), await won.TraceAsync());
        Assert.Equal("won", won.Coordinator.SiegeOutcome);
        Assert.Contains(wonBots, b => won.Bots.Social.Memory.Snapshot(b.Id).Any(r => r.TrustPermille == successTrust));
    }

    private sealed class FrozenDeathClock(long seconds) : DeathClock
    {
        public override long UnixSeconds => seconds;
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
            // The death clock follows the manual world clock: a ghost bot would reclaim its body after the 30 s delay within this run.
            // The test is about a leaderless raid, so the death clock stands still and the leader stays dead.
            WorldRuntime runtime = world.World.Host.World;
            DeathHooks.Register(runtime, new DeathHooks(DeathHooks.For(runtime).Options, new FrozenDeathClock(DeathHooks.For(runtime).Clock.UnixSeconds)));
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
                .Invoke(system, [GridDefines.ComputeGridCoord(objective.X, objective.Y)]);
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
                typeof(PlayerbotGroupAI).GetProperty("_route", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(raid.Group!.Find(raid.Group.LeaderBotId)!.AI, null);
                wasFlagged = false;
                flaps++;
            }
            return !raid.RetreatLatched;
        });
        Assert.True(ended, $"the latch outlived every bound after {flaps} flaps, {unchecked(Now(world) - began)} ms" + Environment.NewLine + string.Join(" ", trail));
        Assert.Equal(1, raid.Retreats);
    }

    // --- push through a straggler fight -------------------------------------------------------------------------------------

    /// <summary>The scenario world with the objective 300 yards out: nothing hostile is within the pull range of a leader on the way (waypoints at 20, 40 and 60 yards).</summary>
    private static Task<GroupTestWorld> StartEmptyWay() => GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 300f, 0f, health: 120)]);

    /// <summary>The combat flag a far-away attacker leaves on the members: set again on every check of the clock, so the raid is in combat for as long as the test runs.</summary>
    private static void KeepInCombat(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        foreach (Player member in Living(world, raid)) member.Map!.Combat.SetInCombatState(member, 5_500);
    }

    [Fact]
    public async Task ARaidInCombatWithAFarAttackerAndNoHostileNearItsLeader_PushesThrough_AndAdvancesWithinTheBound()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await world.OnWorldAsync(() => { KeepInCombat(world, raid); return true; });
        float startX = await world.OnWorldAsync(() => Leader(world, raid).X);
        uint began = Now(world);

        bool pushed = false, detailed = false, leaderStep = false;
        var orders = new HashSet<PlayerbotSiegeOrder>();
        uint firstStepMs = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 90_000, () =>
        {
            KeepInCombat(world, raid);
            orders.Add(raid.Order);
            Player leader = Leader(world, raid);
            pushed |= raid.Push == PlayerbotSiegePush.Quiet && leader.Combat.IsInCombat && raid.LastFacts.Hostiles == 0;
            detailed |= raid.Detail.Contains("push=quiet", StringComparison.Ordinal);
            leaderStep |= raid.Group!.Find(raid.Group.LeaderBotId)?.AI?.Step.Contains(":push", StringComparison.Ordinal) == true;
            if (siege.Step >= 1 && firstStepMs == 0) firstStepMs = unchecked(Now(world) - began);
            return siege.Step >= 2;
        });

        Assert.True(done, $"the raid never pushed through (step {siege.Step}, order {raid.Order}, push {raid.Push}, facts {raid.LastFacts})" + Environment.NewLine + await world.TraceAsync());
        Assert.True(pushed && detailed && leaderStep, $"pushed {pushed} detailed {detailed} leader step {leaderStep}");
        Assert.InRange(firstStepMs, 1u, PlayerbotSiegeCommander.PushThroughQuietMs + 40_000);
        Assert.True(await world.OnWorldAsync(() => Leader(world, raid).X - startX > 30f), "the leader walked on");
        // No retreat began or was counted, and nobody was ever told to disengage.
        Assert.Equal(0, raid.Retreats);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Retreat, orders);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Rally, orders);
    }

    [Fact]
    public async Task OnTheFinalLeg_TheLegToTheLastWaypoint_ARaidInCombatWithNoHostileNear_DoesNotPushThrough()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        await world.OnWorldAsync(() => { siege.Step = siege.Waypoints.Count - 1; /* NB3: the leg to the last waypoint is the final leg */ KeepInCombat(world, raid); return true; });

        var pushes = new List<string>();
        uint began = Now(world);
        Assert.False(await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 20_000, () =>
        {
            KeepInCombat(world, raid);
            // The order of the decision before the fight (an advance) stands for the first tick of the commander.
            if (unchecked(Now(world) - began) > 1_000 && (raid.Push != PlayerbotSiegePush.None || raid.Order == PlayerbotSiegeOrder.Advance))
                pushes.Add($"{raid.Order}/{raid.Push} at {Now(world)}");
            return false;
        }));
        Assert.Empty(pushes);
        Assert.Equal(siege.Waypoints.Count - 1, siege.Step);
        Assert.Equal(0, raid.Retreats);
    }

    // --- the real paths: stalemate, two raids, lag ----------------------------------------------------------------------------

    private static float MaxLag(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        Player leader = Leader(world, raid);
        return Living(world, raid).Where(p => p.Guid != leader.Guid).Select(p => MathF.Sqrt(((p.X - leader.X) * (p.X - leader.X)) + ((p.Y - leader.Y) * (p.Y - leader.Y)))).DefaultIfEmpty(0f).Max();
    }

    [Fact]
    public async Task AStalemateWithARealHostileNearTheLeader_DisengagesTheMembers_AndTheRaidAdvancesTogether()
    {
        // Two guards 45 yards out that the raid cannot kill while they are topped up; the way: a waypoint at the start, then 150, 200 and 250 yards.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 4f, 150f, 200f, 250f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];

        bool pushed = false, hostileNear = false, engagedBefore = false, guardsDown = false;
        float lagAtStep = -1f;
        var engagedWhilePushing = new List<string>();
        var marksWhilePushing = new List<string>();
        var leaderFought = new List<string>();
        var wrongSince = new Dictionary<ObjectGuid, uint>();
        var trail = new List<string>();
        uint startedAt = Now(world), pushSince = 0;
        int stepSeen = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            // The guards cannot be killed until the push begins (then the raid, fighting back what attacks it, may finish them).
            if (!pushed) TopUpGuards(world, raid);
            // The healers cannot keep up with the guards over three minutes in this fixture: the raid is kept healthy, as a stalemate needs.
            foreach (Player member in Living(world, raid)) member.Health = member.MaxHealth;
            if (unchecked(Now(world) - startedAt) / 10_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - startedAt)}ms step={siege.Step} order={raid.Order} push={raid.Push} combat={raid.LastFacts.InCombat} hostiles={raid.LastFacts.Hostiles} inCombatMs={raid.LastFacts.MsInCombatAtStep} hp={raid.LastFacts.AverageHealthPct:F0} leaderx={Leader(world, raid).X - Origin.X:F1}");
            if (raid.Push == PlayerbotSiegePush.None && raid.LastFacts.InCombat)
                engagedBefore |= raid.Group!.Members.Any(m => !m.Real && m.AI?.TargetUnit is not null);
            if (raid.Push == PlayerbotSiegePush.Stalemate)
            {
                pushed = true;
                hostileNear |= raid.LastFacts.Hostiles > 0;
                if (pushSince == 0) pushSince = Now(world);
                // A second into the push the skull is off its unit and nobody is the raid's pull target (the marks are taken off when the push begins).
                if (unchecked(Now(world) - pushSince) >= 1_000 && (!Skull(world, raid).IsEmpty || !raid.PullTarget.IsEmpty))
                    marksWhilePushing.Add($"skull {Skull(world, raid)}, pull target {raid.PullTarget}, {unchecked(Now(world) - pushSince)} ms into the push");
                // The leader walks: away from its waypoint, more than a second into the push, it has no target (its attackers follow it).
                if (unchecked(Now(world) - pushSince) >= 1_000 && !raid.AtWaypoint && raid.Group!.Members.First(m => m.BotId == raid.Group.LeaderBotId).AI?.TargetUnit is { } leaderTarget)
                    leaderFought.Add($"the leader fights {leaderTarget.Guid} {unchecked(Now(world) - pushSince)} ms into the push, away from its waypoint");
                // Three seconds into the push (the marks are off, the targets dropped) a member fights only what attacks a member of the raid.
                if (unchecked(Now(world) - pushSince) >= 3_000)
                    foreach (var member in raid.Group!.Members.Where(m => !m.Real && m.Joined))
                    {
                        // A member's target may be one think old (the creature switched victims since): only a target that stays wrong for 2.5 s counts. Right is an attacker of any living member of the raid.
                        bool wrong = world.World.Host.World.FindOnlinePlayer(member.Guid) is { IsAlive: true } && member.AI?.TargetUnit is { } target
                            && !Living(world, raid).Any(p => p.Combat.Attackers.Any(a => ReferenceEquals(a, target)));
                        if (!wrong) wrongSince.Remove(member.Guid);
                        else if (!wrongSince.TryAdd(member.Guid, Now(world)) && unchecked(Now(world) - wrongSince[member.Guid]) >= 2_500)
                            engagedWhilePushing.Add($"{member.Name} fights {member.AI?.TargetUnit?.Guid} that has not attacked it for 2.5 s, {unchecked(Now(world) - pushSince)} ms into the push");
                    }

                // Six seconds into the push the guards are finished off (the raid is topped up in this fixture, and the test is about where the raid goes, not about a damage race).
                if (!guardsDown && unchecked(Now(world) - pushSince) >= 6_000)
                {
                    guardsDown = true;
                    Player tank = Leader(world, raid);
                    foreach (Creature guard in tank.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Where(c => c.Entry is GuardA or GuardB && c.IsAlive).ToArray())
                        tank.Map.Combat.DealDamage(tank, guard, guard.Health);
                }
            }
            else pushSince = 0;

            if (siege.Step != stepSeen)
            {
                stepSeen = siege.Step;
                if (stepSeen == 2) lagAtStep = MaxLag(world, raid);
            }

            return siege.Step >= 2;
        });

        string story = string.Join(Environment.NewLine, trail);
        Assert.True(done, $"the step never moved (step {siege.Step}, order {raid.Order}, push {raid.Push}, facts {raid.LastFacts})" + Environment.NewLine + story + Environment.NewLine + await world.TraceAsync());
        Assert.True(engagedBefore && pushed && hostileNear, $"engaged before {engagedBefore}, pushed {pushed}, hostile near the leader {hostileNear}" + Environment.NewLine + story);
        Assert.Empty(engagedWhilePushing);
        Assert.Empty(marksWhilePushing);
        Assert.Empty(leaderFought);
        Assert.Equal(0, raid.Retreats);
        // The step moved with every member within LagYards of the leader (a tick of walking as tolerance), and nobody is left behind afterwards.
        Assert.InRange(lagAtStep, 0f, PlayerbotSiegeCommander.LagYards + 6f);
        Assert.True(await world.RunUntilAsync(30_000, () => MaxLag(world, raid) <= PlayerbotSiegeCommander.LagYards + 6f), "a member was left behind: " + MaxLag(world, raid));
    }

    [Fact]
    public async Task TwoRaids_OnlyOneQualifies_NoPush_UntilBothDo_AndTheStepThenMovesTogether()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 0f, 85f, health: 600), Guard(Objective, 300f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid first = siege.Raids[0], second = siege.Raids[1];

        // The second raid stands by a guard it cannot kill; the first has nothing hostile within 55 yards.
        await world.OnWorldAsync(() =>
        {
            foreach (var member in second.Group!.Members.Where(m => !m.Real))
            {
                Player shifted = world.World.Host.World.FindOnlinePlayer(member.Guid)!;
                shifted.Relocate(Origin.X, Origin.Y + 75f, shifted.Z, 0, Now(world));
            }

            KeepInCombat(world, first);
            KeepInCombat(world, second);
            return true;
        });

        var violations = new List<string>();
        bool Invariant()
        {
            // A raid pushes only when the other is out of combat or pushes too.
            foreach ((PlayerbotSiegeRaid a, PlayerbotSiegeRaid b, string name) in new[] { (first, second, "first"), (second, first, "second") })
                if (a.PushingThrough && b.LastFacts.InCombat && PlayerbotSiegeCommander.PushOf(b.LastFacts) == PlayerbotSiegePush.None)
                    violations.Add($"{name} raid pushes ({a.Push}) at {Now(world)} while the other fights without qualifying: {b.LastFacts}");
            return false;
        }

        // Phase 1: the first raid qualifies by the quiet rule long before the end, and does not push.
        bool firstQualified = false;
        await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 30_000, () =>
        {
            TopUpGuards(world, second);
            KeepInCombat(world, first);
            KeepInCombat(world, second);
            firstQualified |= PlayerbotSiegeCommander.PushOf(first.LastFacts) == PlayerbotSiegePush.Quiet;
            return Invariant();
        });
        Assert.True(firstQualified, "the first raid never qualified for a push: " + first.LastFacts + Environment.NewLine + await world.TraceAsync());
        Assert.Empty(violations);
        Assert.Equal((PlayerbotSiegePush.None, PlayerbotSiegeOrder.Assault, 0), (first.Push, first.Order, siege.Step));
        Assert.True(second.LastFacts.Hostiles > 0 && second.LastFacts.InCombat, "the second raid fights the guard: " + second.LastFacts);

        // Phase 2: the guard dies, the second raid goes quiet and qualifies; the push starts for both and the step moves on together.
        await world.OnWorldAsync(() =>
        {
            Player killer = Leader(world, second);
            Creature guard = killer.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == GuardA);
            killer.Map.Combat.DealDamage(killer, guard, guard.Health);
            return true;
        });
        bool bothPushed = false;
        bool moved = await world.RunUntilAsync(150_000, () =>
        {
            KeepInCombat(world, first);
            KeepInCombat(world, second);
            bothPushed |= first.PushingThrough && second.PushingThrough;
            Invariant();
            return siege.Step >= 1;
        });
        Assert.True(moved, $"the step never moved (first {first.Order}/{first.Push} {first.LastFacts}; second {second.Order}/{second.Push} {second.LastFacts})" + Environment.NewLine + await world.TraceAsync());
        Assert.True(bothPushed, "the raids never pushed together");
        Assert.Empty(violations);
        Assert.Equal(0, first.Retreats + second.Retreats);
    }

    [Fact]
    public async Task AMemberHeldFarAway_BlocksTheStepOfAPushingRaid_UntilItCatchesUp()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false; // the member walks back, it is not summoned
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });

        // The raid qualifies, the leader walks to the first waypoint and waits there: the member is 120 yards away.
        bool granted = false;
        var stepped = new List<string>();
        await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + 40_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            granted |= raid.Push == PlayerbotSiegePush.Quiet;
            if (siege.Step != 0) stepped.Add($"step {siege.Step} at {Now(world)} with the member {MaxLag(world, raid):F0} yd from the leader");
            return false;
        });
        Assert.True(granted, "the push was never granted: " + raid.LastFacts);
        Assert.Empty(stepped);
        Assert.InRange(YardsFrom(Leader(world, raid), siege.Waypoints[0]), 0f, PlayerbotSiegeCommander.WaypointYards + 2f);
        Assert.True(raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards, "the member is far: " + raid.LastFacts);

        // Released, the member catches up (it walks), and the step moves on with it inside the bound.
        float lagAtStep = -1f;
        bool moved = await world.RunUntilAsync(120_000, () =>
        {
            KeepInCombat(world, raid);
            if (siege.Step >= 1 && lagAtStep < 0f) lagAtStep = MaxLag(world, raid);
            return siege.Step >= 1;
        });
        Assert.True(moved, $"the step never moved after the member was released: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.InRange(lagAtStep, 0f, PlayerbotSiegeCommander.LagYards + 6f);
    }

    /// <summary>The fixture of the stalemate tests: two guards 45 yards out that the raid cannot kill while they are topped up; the way: waypoints at the start, then 150, 200 and 250 yards.</summary>
    private static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> StalemateFixture(params float[] waypoints)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, waypoints.Length > 0 ? waypoints : [4f, 150f, 200f, 250f]);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        return (world, siege, siege.Raids[0]);
    }

    private static void TopUpMembers(GroupTestWorld world, PlayerbotSiegeRaid raid)
    {
        foreach (Player member in Living(world, raid)) member.Health = member.MaxHealth;
    }

    /// <summary>The runs of the guard stalemate test: 3, or the number in SIEGE_STALEMATE_RUNS (a loop to measure how often it fails).</summary>
    public static IEnumerable<object[]> StalemateRuns()
        => Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("SIEGE_STALEMATE_RUNS"), out int n) && n > 0 ? n : 3).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(StalemateRuns))]
    public async Task AStalemateWithGuardsTheRaidCannotKill_TheLeaderWalksOnAndTheStepMoves_OrTheRaidRetreatsAsDesigned_AndNoLeaderDies(int run)
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Guid leaderBot = raid.Group!.LeaderBotId;

        // Nothing is killed or dealt by the test: the guards stay at full health for the whole run, and nobody is topped up once the push has begun
        // (the members and the leader are left to the guards; two members may die, and the raid then retreats as designed).
        bool pushed = false;
        int stepAtPush = -1;
        uint pushSince = 0;
        float maxX = float.MinValue;
        var trail = new List<string>();
        uint began = Now(world);
        var problems = new List<string>();
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 200_000, () =>
        {
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} order={raid.Order} push={raid.Push} force={raid.PushInForce} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={Leader(world, raid).X - Origin.X:F1} lag={MaxLag(world, raid):F0} alive={Living(world, raid).Count} retreats={raid.Retreats}");
            TopUpGuards(world, raid);
            if (!pushed) TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Group.LeaderBotId != leaderBot) problems.Add($"the leader changed at {Now(world)}");
            if (!leader.IsAlive) problems.Add($"the leader died at {Now(world)} (push {raid.Push}, step {siege.Step})");
            maxX = Math.Max(maxX, leader.X - Origin.X);
            if (raid.Push == PlayerbotSiegePush.Stalemate)
            {
                if (!pushed) { pushed = true; stepAtPush = siege.Step; pushSince = Now(world); }
                if (unchecked(Now(world) - pushSince) >= 1_000 && !raid.AtWaypoint
                    && raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit is { } target)
                    problems.Add($"the leader fights {target.Guid} {unchecked(Now(world) - pushSince)} ms into the push, away from its waypoint");
            }

            return pushed && (siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0);
        });

        // The story (with the world trace) is built only when something fails: the trace is a live buffer.
        async Task<string> Story() => $"run {run}: step {siege.Step} (at the push {stepAtPush}), denied {raid.PushDeniedStep}, push {raid.Push}, retreats {raid.Retreats}, leader reached x+{maxX:F1}, {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        bool walkedOn = maxX > 60f && siege.Step > stepAtPush;
        bool retreated = raid.Retreats > 0;
        output.WriteLine($"STALEMATE-ROW run={run} outcome={(walkedOn ? "walked" : retreated ? "retreat" : "neither")} step={siege.Step}/{stepAtPush} maxX={maxX:F1} retreats={raid.Retreats} denied={raid.PushDeniedStep}");
        int rows = Interlocked.Increment(ref _stalemateRows);
        int retreatRows = retreated && !walkedOn ? Interlocked.Increment(ref _stalemateRetreatRows) : Volatile.Read(ref _stalemateRetreatRows);
        if (!pushed) Assert.Fail("the raid never pushed through: " + await Story());
        if (!done) Assert.Fail("the push neither moved the step nor ended in a retreat: " + await Story());
        Assert.Empty(problems);
        // Never a denial: the leader walks on (it passed 60 yards from the start and the step moved), or the raid lost members and retreated, as designed.
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
        if (!(walkedOn || retreated)) Assert.Fail(await Story());
        // The retreat is a designed outcome of a row, not of the test: if every row of the run retreated the leader never walked on and the test proves nothing about it.
        if (rows == StalemateRunCount() && retreatRows == rows) Assert.Fail($"every one of the {rows} rows retreated, none walked on: " + await Story());
    }

    private static int _stalemateRows, _stalemateRetreatRows;

    private static int StalemateRunCount() => StalemateRuns().Count();

    /// <summary>
    /// A guard that runs at the waiting leader from its spawn point is fought by the leader at its waypoint (the leader's AI takes it as its target and its melee hits it) and never evades on its way in. Once it has been hit it is
    /// put <see cref="ProbeKiteYards"/> from the waypoint, on the side the leader fought it on, and held there rooted for <see cref="ProbeHoldMs"/> with the leader on its threat list: a kiting attacker past the answer leash that
    /// stays within <see cref="PlayerbotGroupAI.AssistYards"/> of the leader for the whole hold (the test checks that, and that the step's answer is not spent), so the leash of the waiting leader's target selection is the one
    /// thing between the leader and it. For the whole hold the leader must not take it as its target again (from <see cref="ProbeDropByMs"/> on: a think or two), must not go past the leash, and must be back within
    /// <see cref="PlayerbotSiegeCommander.WaypointYards"/> of its waypoint by <see cref="ProbeHomeByMs"/> and stay there to the end of the hold; released, the guard comes back, and the step moves on without a denial or a refused waypoint.
    /// </summary>
    [Theory]
    [MemberData(nameof(StalemateRuns))]
    public async Task AnAttackerComingToTheWaitingLeader_IsFought_AndPastTheLeashIsNotFollowed_NoDenialNoRefusedWaypoint(int run)
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        // One member is kept where the guards are, 110 yards behind the waypoint: it stays in the fight (the raid stays in combat) and the lag hold of the waiting leader runs.
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        bool sent = false, refused = false, targeted = false, hit = false, evaded = false, onLeader = false, kited = false, released = false, back = false;
        // The hold's books: whether the probe stayed answerable but for the leash (an attacker of the leader within AssistYards, the step's answer unspent), and what the leader did about it.
        bool attackerLost = false, tooFar = false, spent = false, targetedPastLeash = false, strayedHome = false;
        float kiteDistance = -1f, farthestInHold = -1f;
        uint kitedAt = 0;
        Creature? probe = null;
        float worstOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            Player leader = Leader(world, raid);
            var leaderAi = raid.Group!.Members.First(m => m.BotId == leaderBot).AI;
            // Read before the top-up: the probe was at full health after the last one, so less now is damage dealt during this tick, by its victim's side.
            if (probe is not null && siege.Step == stepAtPush)
            {
                evaded |= probe.IsEvading;
                onLeader |= ReferenceEquals(probe.Combat.Victim, leader);
                targeted |= ReferenceEquals(leaderAi?.TargetUnit, probe);
                hit |= ReferenceEquals(leader.Combat.Victim, probe) && probe.Health < probe.MaxHealth;
            }

            TopUpGuards(world, raid);
            TopUpMembers(world, raid); // nobody dies: the only trigger is the guard sent at the leader
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) { stepAtPush = siege.Step; }
            if (stepAtPush < 0) return false;
            Hold();
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} sent={sent} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} lagSince={raid.LagSinceMs} off={worstOff:F1} "
                    + $"tgt={(leaderAi?.TargetUnit is null ? "-" : "set")} probe={(probe is null ? "-" : $"{Yards3d(leader, probe):F1}yd")} spent={raid.AnswerSpent}");
            // The members stay behind, fighting at the start (the lag hold of a pushing leader at its waypoint runs, about 110 yards); once the leader has waited 5 s a guard (the one farthest along x) turns on the leader, as the guard of a member that
            // died does. It runs at the leader from afar, and the leader must fight it at the waypoint.
            // Where the guards are by then is not this test's to wait for: a guard that chased the leader to the waypoint is still on it unless its soft leash fired during the chase, and since #1630 that is a coin toss (a chaser whose
            // spline is finalized, melee or ranged, refreshes its leash clock every 5 s, and so does every hit on it, so the clock is under 5 s old when the leader walks off; the chase to the waypoint takes about 15 s against the 12 s
            // of the leash; and an in-area member listed above the leader within 110% of its threat keeps the leader the victim, vmangos ThreatContainer::selectNextVictim). So every guard is sent back to its spawn point first, and the
            // probe runs at the leader from there, 100 yards out; it must arrive without evading, which the assertions below check.
            if (!sent && raid.AtWaypoint && raid.PushInForce != PlayerbotSiegePush.None && raid.LagSinceMs != 0 && unchecked(Now(world) - raid.LagSinceMs) > 5_000
                && leader.Map?.FindUpdater<CreatureMapSystem>() is { } system
                && system.Creatures.Where(g => g.Entry is GuardA or GuardB && g.IsAlive).OrderByDescending(g => MathF.Abs(g.X - leader.X)).FirstOrDefault() is { } guard)
            {
                ResetOtherGuardsToStraggler(system, raid, leader, guard, Now(world));
                ResetGuardToHome(system, guard, Now(world));
                system.AttackStart(guard, leader);
                guard.Combat.Threat.AddThreat(leader, 100_000f);
                probe = guard;
                sent = true;
            }

            if (sent && siege.Step == stepAtPush)
            {
                Vector3 waypoint = siege.Waypoints[stepAtPush];
                float off = YardsFrom(leader, waypoint);
                worstOff = Math.Max(worstOff, off);
                refused |= PlayerbotMotion.IsBlacklisted(leader, waypoint);
                // The leash: once the leader has fought the probe, the probe kites past the answer leash and is held there (rooted, so it neither walks back nor is pushed about) with the leader on its threat list. It goes to the side
                // of the waypoint the leader fought it on (the way it came): past the leash from the waypoint, yet within AssistYards of the leader wherever on its way home the leader is, since the leader fights within the leash
                // (so the probe is at most ProbeKiteYards plus its melee reach from the leader, and ProbeKiteYards from the waypoint itself). A rooted creature refreshes its leash clock at every leash check and runs no chase step,
                // so it neither soft-leashes nor evades for an unreachable victim while held.
                if (!kited && hit && probe is not null && leader.Map?.FindUpdater<CreatureMapSystem>() is { } creatures)
                {
                    kited = true;
                    kitedAt = Now(world);
                    creatures.SetAiImmobilized(probe, true, combatOnly: false);
                    float dx = probe.X - waypoint.X, dy = probe.Y - waypoint.Y, length = MathF.Sqrt((dx * dx) + (dy * dy));
                    (dx, dy) = length >= 1f ? (dx / length, dy / length) : (-1f, 0f);
                    probe.Relocate(waypoint.X + (ProbeKiteYards * dx), waypoint.Y + (ProbeKiteYards * dy), probe.Z, 0, Now(world));
                    kiteDistance = Yards3d(leader, probe);
                }

                if (kited && !released && probe is not null)
                {
                    uint sinceKite = unchecked(Now(world) - kitedAt);
                    if (!ReferenceEquals(probe.Combat.Victim, leader)) probe.Combat.Threat.AddThreat(leader, 100_000f);
                    // Answerable but for the leash: an attacker of the leader within AssistYards (3D, as SelectTarget measures), with the step's answer unspent (so the leash in force is SiegeAnswerLeashYards, not the disk).
                    attackerLost |= !leader.Combat.Attackers.Contains(probe);
                    tooFar |= Yards3d(leader, probe) > PlayerbotGroupAI.AssistYards;
                    spent |= raid.AnswerSpent;
                    // The leash itself: the leader drops the probe within a think or two and never takes it again while it is held; it is home by ProbeHomeByMs and stays home to the end of the hold.
                    if (sinceKite >= ProbeDropByMs) targetedPastLeash |= ReferenceEquals(leaderAi?.TargetUnit, probe);
                    back |= off <= PlayerbotSiegeCommander.WaypointYards;
                    if (sinceKite >= ProbeHomeByMs)
                    {
                        strayedHome |= off > PlayerbotSiegeCommander.WaypointYards;
                        farthestInHold = Math.Max(farthestInHold, off);
                    }

                    if (sinceKite >= ProbeHoldMs && leader.Map?.FindUpdater<CreatureMapSystem>() is { } holding)
                    {
                        released = true;
                        holding.SetAiImmobilized(probe, false, combatOnly: false);
                    }
                }
            }

            return siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0;
        });

        string story = $"run {run}: done={done} sent={sent} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} refused={refused} worstOff={worstOff:F1} retreats={raid.Retreats} "
            + $"probe: onLeader={onLeader} evaded={evaded} kiteDistance={kiteDistance:F1} attackerLost={attackerLost} tooFar={tooFar} spent={spent}; "
            + $"leader: targeted={targeted} hit={hit} kited={kited} released={released} targetedPastLeash={targetedPastLeash} back={back} strayedHome={strayedHome} farthestInHold={farthestInHold:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail);
        Assert.True(sent, "the probe never fired: " + story);
        Assert.True(done && siege.Step > stepAtPush, "the step did not move: " + story);
        // The probe came all the way: it turned on the leader and never evaded on its run in from its spawn point.
        Assert.True(onLeader, "the probe never had the leader as its victim: " + story);
        Assert.False(evaded, "the probe evaded before the step moved: " + story);
        // The leader answered it at the waypoint before the step moved: its AI took the probe as its target and its melee hit it.
        Assert.True(targeted, "the waiting leader never took the probe as its target: " + story);
        Assert.True(hit, "the waiting leader never hit the probe: " + story);
        Assert.True(kited, "the probe was never sent past the leash: " + story);
        Assert.True(released, "the hold did not run its course before the step moved: " + story);
        // The hold was a test of the leash and nothing else: the probe stayed an attacker of the leader within AssistYards, and the answer of the step was not spent.
        Assert.True(kiteDistance <= PlayerbotGroupAI.AssistYards - 2f, "the probe was not held within AssistYards of the leader: " + story);
        Assert.False(attackerLost, "the probe stopped being an attacker of the leader during the hold: " + story);
        Assert.False(tooFar, "the leader was more than AssistYards from the held probe: " + story);
        Assert.False(spent, "the answer of the step was spent during the hold, so the disk, not the leash, kept the leader: " + story);
        // The leash: the waiting leader does not answer an attacker beyond SiegeAnswerLeashYards of its waypoint, does not follow it past the leash, and is home for the rest of the hold.
        Assert.False(targetedPastLeash, "the leader took the probe as its target while it was held past the leash: " + story);
        Assert.True(worstOff <= PlayerbotSiegeCommander.SiegeAnswerLeashYards + 3f, "the leader followed the probe past the leash: " + story);
        Assert.True(back, "the leader did not return to its waypoint once the probe was past the leash: " + story);
        Assert.False(strayedHome, $"the leader was not at its waypoint from {ProbeHomeByMs} ms to {ProbeHoldMs} ms into the hold: " + story);
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
        Assert.False(refused, "the loop watch refused the waypoint: " + story);
    }

    /// <summary>How far from the waypoint the probe of the waiting-leader test is held once it has been fought: past <see cref="PlayerbotSiegeCommander.SiegeAnswerLeashYards"/> by more than the 3-yard slack of the bound plus a melee reach
    /// (a leader that chased it to melee stands clear of the bound), yet, on the side the leader fought on, within <see cref="PlayerbotGroupAI.AssistYards"/> of a leader standing at the waypoint (42 against 50), so the probe stays
    /// one of the answerable attackers of the leader for the whole hold and only the answer leash keeps the leader at its waypoint.</summary>
    private const float ProbeKiteYards = PlayerbotSiegeCommander.SiegeAnswerLeashYards + 12f;

    /// <summary>How long the probe is held past the leash: long enough for the walk home and a stretch standing there, well under the lag hold (<see cref="PlayerbotGroupCoordinator.LagHoldMs"/>) that moves the step on.</summary>
    private const uint ProbeHoldMs = 12_000;

    /// <summary>How long after the probe is put past the leash the leader may still have it as its target: a combat think or two (<see cref="PlayerbotOptions.CombatThinkIntervalMs"/>).</summary>
    private const uint ProbeDropByMs = 1_000;

    /// <summary>By when the leader must be back within <see cref="PlayerbotSiegeCommander.WaypointYards"/> of its waypoint: it fights within the leash, 30 yards at the most, which it walks back in under 5 s.</summary>
    private const uint ProbeHomeByMs = 7_000;

    private static float Yards3d(Unit a, Unit b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>One line of what the waiting leader and its attacker are doing (the failure story of the answer tests).</summary>
    private static string AnswerLine(GroupTestWorld world, PlayerbotSiegeRaid raid, Guid leaderBot, Player leader, Vector3 waypoint, Creature? attacker, uint began)
    {
        var ai = raid.Group!.Members.First(m => m.BotId == leaderBot).AI;
        return $"+{unchecked(Now(world) - began)}ms atWp={raid.AtWaypoint} push={raid.Push} answerStep={raid.AnsweringStep} answerAge={(raid.AnswerRequestedMs == 0 ? -1 : (long)unchecked(Now(world) - raid.AnswerRequestedMs))} off={FlatYards(leader, waypoint):F1} "
            + $"att={(attacker is null ? "-" : $"{FlatYards(attacker, waypoint):F1}off hp={attacker.Health} victimLeader={ReferenceEquals(attacker.Combat.Victim, leader)} dist={FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)):F1}")} step={ai?.Step} target={(ai?.TargetUnit is null ? "-" : "set")}";
    }

    private static Creature? NextAttacker(Player leader)
        => leader.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(g => g.Entry is GuardA or GuardB && g.IsAlive);

    private static float FlatYards(Unit unit, Vector3 point) => MathF.Sqrt(((unit.X - point.X) * (unit.X - point.X)) + ((unit.Y - point.Y) * (unit.Y - point.Y)));

    private sealed class WaitingSince { public uint Since; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerbotSiegeRaid, WaitingSince> Waiting = new();

    /// <summary>
    /// Any other guard that already followed the raid to the waypoint would be answered first (the leader fights the nearest attacker) and TopUpGuards keeps it at full health, while sending it into evade from the waypoint
    /// leaves it running home out of combat for 14 s (so killing the probe drops the raid out of combat and ends the push). Reset every other live guard to its spawn point in combat with the held straggler.
    /// </summary>
    private static void ResetOtherGuardsToStraggler(CreatureMapSystem system, PlayerbotSiegeRaid raid, Player leader, Creature probe, uint now)
    {
        Player? straggler = raid.Group?.Members.FirstOrDefault(m => !m.Real && m.BotId != raid.Group.LeaderBotId) is { } held ? leader.Map?.FindPlayer(held.Guid) : null;
        foreach (Creature other in system.Creatures.Where(g => g.Entry is GuardA or GuardB && g.IsAlive && !ReferenceEquals(g, probe)))
        {
            ResetGuardToHome(system, other, now);
            if (straggler is not null)
            {
                other.CalledAssistance = true;
                system.AttackStart(other, straggler);
                other.Combat.Threat.AddThreat(straggler, 100_000f);
            }
        }
    }

    /// <summary>Put a guard back at its spawn point out of combat, at once (no evade run home): its auras of the fight, its combat, threat and motion are dropped.</summary>
    private static void ResetGuardToHome(CreatureMapSystem system, Creature guard, uint now)
    {
        system.ResetAurasOnEvade(guard);
        system.StopCombatInPlace(guard);
        guard.Motion.Clear();
        system.StopMoving(guard);
        guard.Relocate(guard.Home.X, guard.Home.Y, guard.Home.Z, guard.Home.Orientation, now);
    }

    /// <summary>
    /// A ranged attacker on the waiting leader, as the tests make one: a guard 25 yards from the waypoint that cannot move (rooted, so it neither walks to the leader nor is pushed about by the test) and has the leader on its threat
    /// list. Returns it once the waiting leader's lag hold has run for 5 s, or null before.
    /// </summary>
    private static Creature? SendRangedAttacker(PlayerbotSiege siege, PlayerbotSiegeRaid raid, Player leader, int stepAtPush, uint now, float yards = 25f)
    {
        // The lag clock runs from the first read of the straggler, before the leader arrives: "waiting 5 s" is counted here, from the leader's arrival at the waypoint with its push in force.
        WaitingSince box = Waiting.GetOrCreateValue(raid);
        if (siege.Step != stepAtPush || !raid.AtWaypoint || raid.PushInForce == PlayerbotSiegePush.None) box.Since = 0;
        else if (box.Since == 0)
        {
            box.Since = Math.Max(1u, now);
            raid.LagSinceMs = 0; // the members were already behind (they fight the guards): the lag hold of these tests counts from the leader's arrival, so the clock starts afresh here
        }
        if (box.Since == 0 || raid.LagSinceMs == 0 || unchecked(now - box.Since) <= 5_000
            || leader.Map?.FindUpdater<CreatureMapSystem>() is not { } system || NextAttacker(leader) is not { } guard)
            return null;
        ResetOtherGuardsToStraggler(system, raid, leader, guard, now);
        Vector3 waypoint = siege.Waypoints[stepAtPush];
        system.SetAiImmobilized(guard, true, combatOnly: false);
        guard.Relocate(waypoint.X + yards, waypoint.Y, guard.Z, 0, now);
        guard.Combat.Threat.AddThreat(leader, 100_000f);
        return guard;
    }

    [Fact]
    public async Task AMeleeWaitingLeaderShotByAnAttackerOutOfReach_ClosesAndFightsIt_ThenReturnsToTheWaypoint_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        // One member is kept 110 yards behind the waypoint, in the fight: the lag hold of the waiting leader runs and nobody covers it.
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, lagBefore = 0, onLeaderSince = 0, lastLine = 0, engagedAt = 0;
        bool engaged = false, killed = false, back = false, refused = false, flipped = false, lagKept = true;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
                lagBefore = raid.LagSinceMs;
            }

            if (attacker is null) return false;
            if (unchecked(Now(world) - lastLine) >= 500) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began)); }
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush) leaderOff = Math.Max(leaderOff, off);
            // Reached out in the open: the leader is out of the disk and within a blow of the attacker, and has it as its target.
            if (!engaged && attacker.IsAlive && off > PlayerbotSiegeCommander.WaypointYards + 2f && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 6f
                && ReferenceEquals(raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit, attacker))
            {
                engaged = true;
                engagedAt = Now(world);
            }

            // The fight goes on for 4 s (the answer outlasts the freshness of the leader's answer), then the attacker dies.
            if (engaged && attacker.IsAlive && unchecked(Now(world) - engagedAt) > 4_000) leader.Map!.Combat.Kill(null, attacker);
            killed |= !attacker.IsAlive;
            if (attacker.IsAlive && ReferenceEquals(attacker.Combat.Victim, leader)) onLeaderSince = onLeaderSince == 0 ? Now(world) : onLeaderSince;
            else onLeaderSince = 0;
            refused |= siege.Step == stepAtPush && PlayerbotMotion.IsBlacklisted(leader, waypoint);
            // The raid still reads the leader at the waypoint while it answers out of the disk, and the lag hold keeps running.
            if (siege.Step == stepAtPush && !killed && onLeaderSince != 0 && unchecked(Now(world) - onLeaderSince) > 3_000 && off > PlayerbotSiegeCommander.WaypointYards + 2f)
            {
                flipped |= !raid.AtWaypoint;
                lagKept &= raid.LagSinceMs == lagBefore;
            }

            back |= killed && (off <= PlayerbotSiegeCommander.WaypointYards || siege.Step > stepAtPush);
            return back || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 80_000;
        });

        async Task<string> Story() => $"done={done} sent={attacker is not null} engaged={engaged} killed={killed} back={back} refused={refused} flipped={flipped} lagKept={lagKept} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!engaged) Assert.Fail("the melee leader never closed on the attacker that was shooting it: " + await Story());
        if (leaderOff <= PlayerbotSiegeCommander.WaypointYards + 2f) Assert.Fail("the leader never left the disk, the probe proves nothing: " + await Story());
        if (leaderOff > PlayerbotSiegeCommander.SiegeAnswerLeashYards + PlayerbotSiegeCommander.WaypointYards) Assert.Fail("the leader went past the leash: " + await Story());
        if (!killed) Assert.Fail("the attacker did not die: " + await Story());
        if (!back) Assert.Fail("the leader did not return to the waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (refused) Assert.Fail("the waypoint was blacklisted: " + await Story());
        if (flipped) Assert.Fail("the raid read the answering leader as away from its waypoint: " + await Story());
        if (!lagKept) Assert.Fail("the lag hold was reset by the answer: " + await Story());
    }

    [Fact]
    public async Task AMeleeWaitingLeaderChasingAnAttackerOutOfReach_WalksAChaseRoute_SoTheLoopWatchDoesNotCountItsEnds()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, engagedAt = 0;
        bool engaged = false, killed = false, chasing = false;
        var plain = new List<string>();
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f);
            // Every route the leader walks that ends at the attacker is a chase: the watch must not count it (the waypoint walk, the chase, the walk back read as A, B, A).
            if (attacker.IsAlive && PlayerbotMotion.RouteOf(leader) is { } route && route.Points.Count > 0
                && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) > 3f
                && Vector3.Distance(route.Points[^1], new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 4f)
            {
                chasing = true;
                if (!route.Chase) plain.Add($"t={Now(world) - sentAt} end=({route.Points[^1].X:F1},{route.Points[^1].Y:F1})");
            }

            if (!engaged && attacker.IsAlive && FlatYards(leader, new Vector3(attacker.X, attacker.Y, attacker.Z)) <= 6f)
            {
                engaged = true;
                engagedAt = Now(world);
            }

            if (engaged && attacker.IsAlive && unchecked(Now(world) - engagedAt) > 4_000) leader.Map!.Combat.Kill(null, attacker);
            killed |= !attacker.IsAlive;
            return killed || unchecked(Now(world) - sentAt) > 80_000;
        });

        string story = $"done={done} sent={attacker is not null} engaged={engaged} killed={killed} chasing={chasing}";
        if (attacker is null) Assert.Fail("the probe never fired: " + story);
        if (!chasing) Assert.Fail("the leader never walked a route to the attacker, the probe proves nothing: " + story);
        if (plain.Count > 0) Assert.Fail("the fight chase was planned as a plain route, so the loop watch counts its ends: " + story + " " + string.Join("; ", plain.Take(5)));
    }

    [Fact]
    public async Task AnAttackerThatKitesBeyondTheLeash_IsNotFollowedFurther_TheLeaderReturnsToTheWaypoint_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, kitedAt = 0, lastLine = 0;
        bool chased = false, kited = false, back = false, refused = false;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world)) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (unchecked(Now(world) - lastLine) >= 500) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began)); }
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush) leaderOff = Math.Max(leaderOff, off);
            // The attacker stays 25 yards out until the leader has left the disk to answer it, then it kites to 45 yards (beyond the 30-yard leash) and stays there.
            if (!kited && off > 8f && ReferenceEquals(raid.Group.Members.First(m => m.BotId == leaderBot).AI?.TargetUnit, attacker))
            {
                chased = true;
                kited = true;
                kitedAt = Now(world);
                attacker.Relocate(waypoint.X + 45f, waypoint.Y, attacker.Z, 0, Now(world));
            }

            refused |= siege.Step == stepAtPush && PlayerbotMotion.IsBlacklisted(leader, waypoint);
            back |= kited && unchecked(Now(world) - kitedAt) > 2_000 && (off <= PlayerbotSiegeCommander.WaypointYards || siege.Step > stepAtPush);
            return back || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 80_000 || !raid.PushingThrough;
        });

        async Task<string> Story() => $"done={done} sent={attacker is not null} chased={chased} kited={kited} back={back} refused={refused} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!chased) Assert.Fail("the leader never left the disk to answer the attacker: " + await Story());
        if (leaderOff > PlayerbotSiegeCommander.SiegeAnswerLeashYards + 3f) Assert.Fail("the leader followed the kiting attacker past the leash: " + await Story());
        if (!back) Assert.Fail("the leader did not return to the waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (refused) Assert.Fail("the waypoint was blacklisted: " + await Story());
    }

    [Fact]
    public async Task APushingLeaderWhoseWaypointIsBlacklisted_WalksOn_OnlyThatEntryIsLifted_NoDenial()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        int stepAtPush = -1;
        bool injected = false, lifted = false, decoyKept = true, walked = false, counterKept = false;
        uint injectedAt = 0;
        float xAtInject = 0f;
        var decoy = new Vector3(Origin.X + 300f, Origin.Y + 300f, Origin.Z);
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (unchecked(Now(world) - began) / 5_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} injected={injected} lifted={lifted} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} rej={PlayerbotMotion.ConsecutiveRejections(leader)}");
            if (!injected && raid.PushInForce != PlayerbotSiegePush.None && !raid.AtWaypoint && siege.Step == stepAtPush && YardsFrom(leader, waypoint) > 20f
                && PlayerbotMotion.TryGetState(leader, out PlayerbotMotion.MotionState? state))
            {
                // Lifting one place leaves the rejection count alone (a successful route start resets it, so this is checked on the call itself, not later).
                var probe = new Vector3(Origin.X + 400f, Origin.Y + 400f, Origin.Z);
                state.Blacklist.Add((probe, Now(world) + 600_000));
                state.ConsecutiveRejections = 2;
                state.LastRejectedDestination = probe;
                PlayerbotMotion.Unblacklist(leader, probe);
                counterKept = state.ConsecutiveRejections == 2 && !PlayerbotMotion.IsBlacklisted(leader, probe);
                state.ConsecutiveRejections = 0;
                state.LastRejectedDestination = default;
                // The push's own walk is refused (as an A, B, A pattern would), another place is refused too, and a rejection count is running.
                state.Blacklist.Add((waypoint, Now(world) + 600_000));
                state.Blacklist.Add((decoy, Now(world) + 600_000));
                state.ConsecutiveRejections = 2;
                state.LastRejectedDestination = decoy;
                injected = true;
                injectedAt = Now(world);
                xAtInject = leader.X;
            }

            if (injected)
            {
                lifted |= !PlayerbotMotion.IsBlacklisted(leader, waypoint);
                // ForgetLoops on every think would clear this entry as well.
                if (unchecked(Now(world) - injectedAt) < 20_000) decoyKept &= PlayerbotMotion.IsBlacklisted(leader, decoy);
                walked |= leader.X - xAtInject > 10f;
            }

            return injected && (siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0);
        });

        async Task<string> Story() => $"done={done} injected={injected} lifted={lifted} decoyKept={decoyKept} counterKept={counterKept} walked={walked} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (!injected) Assert.Fail("the probe never fired: " + await Story());
        if (!lifted) Assert.Fail("the push's own waypoint stayed refused: " + await Story());
        if (!walked) Assert.Fail("the leader did not walk on: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
        if (!counterKept) Assert.Fail("lifting one refused place reset the rejection count: " + await Story());
        if (!decoyKept) Assert.Fail("the other refused place was forgotten as well (ForgetLoops on every think): " + await Story());
        if (!(done && siege.Step > stepAtPush)) Assert.Fail("the step did not move: " + await Story());
    }

    [Fact]
    public async Task AStepThatAdvancesWhileTheLeaderAnswers_IsNotReadAtTheNextWaypoint_TheLeaderReachesItBeforeTheStepMovesOn()
    {
        // Waypoints at 4, 150, 188 and 238 yards: waypoint 2 is 38 yards past waypoint 1, outside the answer leash (the leader is not in its disk while it answers, so the furthest-waypoint credit has no waypoint-2 visit to give; a credit after the answer's bound is spent and the lag waived is the credit's own rule, which the test exempts),
        // and about 10 yards from where the leader stands when it answers an attacker 29 yards past waypoint 1.
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture(4f, 150f, 188f, 238f);
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1;
        Creature? attacker = null;
        uint sentAt = 0, lastLine = 0;
        bool readAtNext = false, sawDiskAtNext = false, skipped = false, advanced = false;
        float closestToNext = float.MaxValue;
        bool movedWhileOut = false;
        string stepChange = string.Empty;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 500_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world), 29f) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f); // it keeps shooting the leader
            if (unchecked(Now(world) - lastLine) >= 1_000) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, siege.Waypoints[stepAtPush], attacker, began) + $" step={siege.Step}"); }
            if (siege.Step == stepAtPush + 1)
            {
                float toNext = YardsFrom(leader, siege.Waypoints[stepAtPush + 1]);
                if (!advanced)
                {
                    // The lockstep moves the step only with the leader really in the old waypoint's disk (level-aware); a little slack for the leader's move within the tick.
                    Vector3 old = siege.Waypoints[stepAtPush];
                    float oldFlat = FlatYards(leader, old), oldLevel = Math.Abs(leader.Z - old.Z);
                    // The lockstep is meant here. A step moved by the credit after the answer's bound was spent (LagHoldMs out of the disk) and the lag was waived is the credit's rule (tested apart).
                    bool credited = world.Coordinator.Events.Any(e => e.Contains($"step {stepAtPush} -> {stepAtPush + 1}", StringComparison.Ordinal));
                    movedWhileOut = !credited && (oldFlat > PlayerbotSiegeCommander.WaypointYards + 1f || oldLevel > PlayerbotSiegeCommander.WaypointLevelYards);
                    stepChange = $"the step moved at +{unchecked(Now(world) - began)}ms with the leader {oldFlat:F1} yards (level {oldLevel:F1}) from the old waypoint and {toNext:F1} from the next, atWp={raid.AtWaypoint} real={raid.AtWaypointReal} answerStep={raid.AnsweringStep}";
                }

                advanced = true;
                closestToNext = Math.Min(closestToNext, toNext);
                // At the next waypoint while the leader is still out where it fought: it is not at it.
                if (toNext > PlayerbotSiegeCommander.WaypointYards + 2f) readAtNext |= raid.AtWaypoint;
                else sawDiskAtNext = true;
            }

            if (siege.Step > stepAtPush + 1 && !sawDiskAtNext) skipped = true;
            return sawDiskAtNext || siege.Step > stepAtPush + 1 || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 450_000;
        });

        async Task<string> Story() => $"{stepChange} done={done} advanced={advanced} readAtNext={readAtNext} sawDiskAtNext={sawDiskAtNext} skipped={skipped} closestToNext={closestToNext:F1} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!advanced) Assert.Fail("the step never moved on while the leader answered: " + await Story());
        if (movedWhileOut) Assert.Fail("the lockstep moved the step while the leader was out of the old waypoint's disk, answering: " + await Story());
        if (readAtNext) Assert.Fail("the raid read the leader at the next waypoint while it was still out where it fought: " + await Story());
        if (skipped) Assert.Fail("the step moved past the next waypoint before the leader reached it: " + await Story());
        if (!sawDiskAtNext) Assert.Fail("the leader never reached the next waypoint: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
    }

    [Fact]
    public async Task AKiterAtTheLeashEdge_SteppingInAndOutSeveralTimes_NeverResetsTheLagHold_NorBlacklistsTheWaypoint_NorTripsTheRejectionGuard()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1, maxRejections = 0;
        Creature? attacker = null;
        uint sentAt = 0, lagBefore = 0, lastLine = 0;
        bool lagReset = false, refused = false, chased = false, flipped = false, noPath = false;
        uint blacklistedSince = 0;
        float leaderOff = 0f;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 300_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            Vector3 waypoint = siege.Waypoints[stepAtPush];
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world), 28f) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
                lagBefore = raid.LagSinceMs;
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f);
            if (unchecked(Now(world) - lastLine) >= 1_000) { lastLine = Now(world); trail.Add(AnswerLine(world, raid, leaderBot, leader, waypoint, attacker, began) + $" rej={PlayerbotMotion.ConsecutiveRejections(leader)}"); }
            // The kiter: 3.5 s inside the leash (28 yards: the leader chases it out about 24 yards), then 4.5 s outside it (33 yards: the walk back takes longer than the 3 s a request stays fresh), three times over.
            uint t = unchecked(Now(world) - sentAt);
            bool inside = t < 24_000 && t % 8_000 < 3_500;
            attacker.Relocate(waypoint.X + (inside ? 28f : 33f), waypoint.Y, attacker.Z, 0, Now(world));
            float off = FlatYards(leader, waypoint);
            if (siege.Step == stepAtPush)
            {
                leaderOff = Math.Max(leaderOff, off);
                chased |= off > PlayerbotSiegeCommander.WaypointYards + 4f;
                lagReset |= raid.LagSinceMs != lagBefore;
                flipped |= raid.PushingThrough && !raid.AtWaypoint && off > PlayerbotSiegeCommander.WaypointYards;
                // The loop watch may refuse the waypoint for the moment between a walk back and the next think of the leader, which lifts it (LeadSiege); a refusal that stands, or a walk that finds no path, is the failure.
                if (PlayerbotMotion.IsBlacklisted(leader, waypoint)) { if (blacklistedSince == 0) blacklistedSince = Math.Max(1u, Now(world)); refused |= unchecked(Now(world) - blacklistedSince) > 2_000; }
                else blacklistedSince = 0;
                // The leader's own walk back to the waypoint (a chase of the kiter's place may be refused by the loop watch for a while, which only keeps the leader at the waypoint).
                string aiStep = raid.Group.Members.First(m => m.BotId == leaderBot).AI?.Step ?? string.Empty;
                noPath |= aiStep.StartsWith("lead:", StringComparison.Ordinal) && aiStep.EndsWith(":no-path", StringComparison.Ordinal);
                maxRejections = Math.Max(maxRejections, PlayerbotMotion.ConsecutiveRejections(leader));
            }

            return t > 25_000 || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || siege.Step > stepAtPush;
        });

        async Task<string> Story() => $"done={done} chased={chased} lagReset={lagReset} flipped={flipped} refused={refused} noPath={noPath} maxRejections={maxRejections} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats} leaderOff={leaderOff:F1} {raid.LastFacts}"
            + Environment.NewLine + string.Join(Environment.NewLine, trail) + Environment.NewLine + await world.TraceAsync();
        if (attacker is null) Assert.Fail("the probe never fired: " + await Story());
        if (!chased) Assert.Fail("the leader never left the disk to answer the kiter: " + await Story());
        if (siege.Step > stepAtPush) Assert.Fail("the step moved on during the kiting, the probe proves nothing: " + await Story());
        if (lagReset) Assert.Fail("the kiter reset the lag hold: " + await Story());
        if (flipped) Assert.Fail("the raid read the leader as away from its waypoint while it answered or walked back: " + await Story());
        if (refused) Assert.Fail("the waypoint stayed blacklisted: " + await Story());
        if (noPath) Assert.Fail("the leader's walk to the waypoint found no path: " + await Story());
        if (maxRejections >= PlayerbotMotion.MaxRejectionsBeforeLoop) Assert.Fail("the rejection guard was tripped: " + await Story());
        if (raid.PushDeniedStep == stepAtPush) Assert.Fail("the push was denied: " + await Story());
    }

    [Fact]
    public async Task QueuedLootWaits_WhileAPushingLeaderWalksOffItsWaypoint()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Guid leaderBot = raid.Group!.LeaderBotId;
        int stepAtPush = -1;
        bool queued = false, drained = false;
        int walkTicks = 0;
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 250_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            PlayerbotGroupAI ai = raid.Group.Members.First(m => m.BotId == leaderBot).AI!;
            bool walking = siege.Step == stepAtPush && raid.PushingThrough && !raid.AtWaypoint && YardsFrom(leader, siege.Waypoints[stepAtPush]) > 20f;
            if (!queued && walking)
            {
                // A corpse looted before the push began left its packets queued.
                ai.QueueLootAction(WorldOpcode.CmsgLootMoney, []);
                queued = true;
            }

            if (queued && walking)
            {
                walkTicks++;
                drained |= ai.QueuedLootActions == 0;
            }

            return queued && (!walking && walkTicks > 0 || siege.Step > stepAtPush);
        });

        async Task<string> Story() => $"done={done} queued={queued} drained={drained} walkTicks={walkTicks} step={siege.Step}/{stepAtPush} {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync();
        if (!queued) Assert.Fail("the probe never fired: " + await Story());
        if (walkTicks < 3) Assert.Fail("the leader was not seen walking long enough: " + await Story());
        if (drained) Assert.Fail("the queued loot was sent while the pushing leader was walking: " + await Story());
    }

    private const uint WeakFoodItem = 992101;
    private const uint WeakFoodSpell = 992001;

    [Fact]
    public async Task APushingLeaderOffItsWaypoint_HurtAndOutOfCombat_WalksOnInsteadOfResting_NoDenial()
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            services: c =>
            {
                if (c.LastOrDefault(d => d.ServiceType == typeof(ArcaneCore.Kernel.Items.IItemTemplateSource))?.ImplementationInstance is ArcaneCore.World.Tests.Items.InMemoryItemTemplateSource items)
                    items.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = WeakFoodItem, Name = "weak food", Class = 0, InventoryType = 0, Stackable = 20,
                        Spells = [new ArcaneCore.Kernel.Items.ItemSpell(WeakFoodSpell, 0, -1, 0, 0, 0, 0)] });
            },
            extraCreatures: [Guard(GuardA, 45f, 2f, health: 600), Guard(GuardB, 46f, -2f, health: 600), Guard(Objective, 600f, 0f, health: 120)]);
        await using GroupTestWorld _ = world;
        await world.OnWorldAsync(() =>
        {
            // A food whose regeneration is far too weak to clear the rest threshold in the 20 s a rest lasts.
            var spells = world.World.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
            spells.System.Store = new ArcaneCore.Game.Spells.SpellStore([.. spells.System.Store.All, new ArcaneCore.Game.Spells.SpellInfo
            {
                Id = WeakFoodSpell, Name = "playerbot consumable", Duration = new ArcaneCore.Game.Spells.SpellDuration(60_000, 0, 60_000),
                Attributes = ArcaneCore.Game.Spells.SpellAttributes.AllowWhileSitting, AuraInterruptFlags = ArcaneCore.Game.Spells.SpellAuraInterruptFlags.StandingCancels,
                Effects = [new ArcaneCore.Game.Spells.SpellEffectInfo { Effect = ArcaneCore.Game.Spells.SpellEffectName.ApplyAura, AuraType = ArcaneCore.Game.Spells.AuraType.ModRegen,
                    BasePoints = 0, BaseDice = 1, DieSides = 1, TargetA = ArcaneCore.Game.Spells.SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            return true;
        });
        (Guid Id, string Name)[] bots = await Enlist(world, 4f, 150f, 200f, 250f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        bool food = await world.OnWorldAsync(() => Leader(world, raid).Inventory.AddItem(WeakFoodItem, 10, out ArcaneCore.Game.Items.Item? _) == ArcaneCore.Game.Items.InventoryResult.Ok);
        Assert.True(food, "the food was not added");

        int stepAtPush = -1;
        bool hurt = false, sat = false, outOfCombat = false;
        var trail = new List<string>();
        uint began = Now(world);
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 200_000, () =>
        {
            TopUpGuards(world, raid);
            Player leader = Leader(world, raid);
            // The raid is kept in combat and healthy; the leader is left as it is once it is hurt.
            foreach (Player member in Living(world, raid).Where(p => p.Guid != leader.Guid || !hurt)) member.Health = member.MaxHealth;
            if (hurt) foreach (Player member in Living(world, raid).Where(p => p.Guid != leader.Guid)) member.Map!.Combat.SetInCombatState(member, 5_500); // the raid fights on, the leader does not
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            if (hurt && unchecked(Now(world) - began) / 1_000 >= trail.Count)
                trail.Add($"+{unchecked(Now(world) - began)}ms step={siege.Step} push={raid.Push} denied={raid.PushDeniedStep} atWp={raid.AtWaypoint} x={leader.X - Origin.X:F1} hp={100 * leader.Health / Math.Max(1, leader.MaxHealth)}% sit={leader.StandState == StandState.Sit} combat={leader.Combat.IsInCombat}");
            if (!hurt && !raid.AtWaypoint && leader.X - Origin.X > 80f && leader.Map?.FindUpdater<CreatureMapSystem>() is { } system)
            {
                // Mid-walk its attackers are gone (they evade) and it is left at 30 % health, out of combat, while the raid fights on.
                foreach (Creature guard in system.Creatures.Where(g => g.Entry is GuardA or GuardB && ReferenceEquals(g.Combat.Victim, leader))) system.EnterEvadeMode(guard);
                leader.Map.Combat.CombatStop(leader);
                leader.Health = leader.MaxHealth * 30 / 100;
                hurt = true;
            }

            if (hurt)
            {
                sat |= leader.StandState == StandState.Sit && !raid.AtWaypoint; // at the waypoint it waits and may rest
                outOfCombat |= !leader.Combat.IsInCombat;
            }

            return siege.Step > stepAtPush || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0;
        });

        string story = $"done={done} hurt={hurt} outOfCombat={outOfCombat} sat={sat} step={siege.Step}/{stepAtPush} denied={raid.PushDeniedStep} retreats={raid.Retreats}" + Environment.NewLine + string.Join(Environment.NewLine, trail);
        Assert.True(hurt, "the leader was never hurt mid-walk: " + story);
        Assert.False(sat, "the pushing leader sat down to rest away from its waypoint (it may rest at the waypoint, where it waits): " + story);
        Assert.True(done && siege.Step > stepAtPush, "the step did not move: " + story);
        Assert.NotEqual(stepAtPush, raid.PushDeniedStep);
    }

    [Fact]
    public async Task TheNoProgressBound_InTheWorld_WithdrawsTheStandingPush_AndGrantsNoOtherForTheStep()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture();
        await using GroupTestWorld _ = world;
        Assert.True(await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 60_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            return raid.Push == PlayerbotSiegePush.Stalemate;
        }), "the raid never pushed through: " + raid.LastFacts + Environment.NewLine + await world.TraceAsync());

        // The leader can no longer plan a way (the maps the bots may enter exclude this one): it stands, and the push gets it nowhere.
        int step = siege.Step;
        uint[] allowed = world.Options.AllowedMaps;
        world.Options.AllowedMaps = [1];
        float movedFrom = Leader(world, raid).X;
        bool withdrawn = await world.RunUntilAsync(PlayerbotSiegeCommander.PushStallMs + 10_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            return raid.PushDeniedStep == step && raid.Push == PlayerbotSiegePush.None;
        });
        Assert.True(withdrawn, $"the push was not withdrawn (step {siege.Step}, push {raid.Push}, denied {raid.PushDeniedStep}, best {raid.PushBestYards:F1}, leader moved {Leader(world, raid).X - movedFrom:F1})" + Environment.NewLine + await world.TraceAsync());
        Assert.Equal(PlayerbotSiegePush.None, raid.PushInForce);

        // Back at the baseline (Assault), and no push is granted again at this step, though the fight has lasted longer than StalemateMs.
        var regrants = new List<string>();
        bool assaulted = false;
        await world.RunUntilAsync(20_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            assaulted |= raid.Order == PlayerbotSiegeOrder.Assault;
            if (raid.Push != PlayerbotSiegePush.None || raid.PushInForce != PlayerbotSiegePush.None || siege.Step != step)
                regrants.Add($"at {Now(world)}: push {raid.Push}, in force {raid.PushInForce}, step {siege.Step}");
            return false;
        });
        Assert.True(assaulted, "the raid did not fall back to Assault: " + raid.LastFacts);
        Assert.Empty(regrants);
        Assert.True(raid.LastFacts.MsInCombatAtStep >= PlayerbotSiegeCommander.StalemateMs, "the fight has lasted longer than a stalemate: " + raid.LastFacts);
        Assert.Contains("push=denied", raid.Detail);
        world.Options.AllowedMaps = allowed;
    }

    [Fact]
    public async Task AMemberHeldFarAway_HoldsAPushingRaidForNinetySeconds_ThenTheStepMovesWithoutIt()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });

        // The member is held for the whole run: the step moves once the leader has waited LagHoldMs at the waypoint, not before.
        bool waived = false, noted = false;
        uint arrivedAt = 0, movedAt = 0, lagBegan = 0;
        float lagWhenMoved = -1f;
        bool moved = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (arrivedAt == 0 && raid.AtWaypoint && raid.PushingThrough) arrivedAt = Now(world);
            if (lagBegan == 0 && raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards) lagBegan = Now(world);
            waived |= raid.LastFacts.LagWaived;
            noted |= world.Coordinator.Events.Any(e => e.Contains("pushing on from waypoint 1"));
            if (siege.Step >= 1 && movedAt == 0)
            {
                movedAt = Now(world);
                lagWhenMoved = MaxLag(world, raid);
            }

            return siege.Step >= 1;
        });

        Assert.True(moved, $"the step never moved: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(arrivedAt != 0, "the pushing leader never waited at the waypoint");
        Assert.True(lagWhenMoved > PlayerbotSiegeCommander.LagYards, $"the member was within reach when the step moved: {lagWhenMoved:F0} yd");
        // The lag clock runs from the first read of the straggler (the leader need not have arrived), so the hold is counted from there.
        Assert.True(lagBegan != 0 && unchecked(movedAt - lagBegan) >= PlayerbotGroupCoordinator.LagHoldMs - 1_000, $"the step moved {unchecked(movedAt - lagBegan)} ms after the lag began (the hold is {PlayerbotGroupCoordinator.LagHoldMs} ms)");
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.True(noted, "no event note says the raid pushed on:" + Environment.NewLine + await world.TraceAsync());
        Assert.Contains(PlayerbotGroupCoordinator.PushOnLine, raid.Announcer.Sent);
    }

    // --- the furthest-waypoint credit and the route tether -------------------------------------------------------------------

    /// <summary>Moves the leader of <paramref name="raid"/> (and its members, so none lags) to <paramref name="x"/> yards east of the start, <paramref name="y"/> north of it.</summary>
    private static void Teleport(GroupTestWorld world, PlayerbotSiegeRaid raid, float x, float y = 0f)
    {
        foreach (var member in raid.Group!.Members.Where(m => !m.Real && m.Joined))
            if (world.World.Host.World.FindOnlinePlayer(member.Guid) is { } player)
                player.Relocate(Origin.X + x, Origin.Y + y, Origin.Z, 0, Now(world));
    }

    /// <summary>Four waypoints (15, 30, 45 and 60 yards), no guards, the objective at 70 yards or (<paramref name="objectiveFar"/>) 400, out of any pull range: the raids are teleported over the waypoints.</summary>
    private static async Task<(GroupTestWorld World, PlayerbotSiege Siege)> CreditFixture(int raids, bool objectiveFar = true)
    {
        GroupTestWorld world = objectiveFar
            ? await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 400f, 0f, health: 120)])
            : await StartWorld(guards: false);
        (Guid Id, string Name)[] bots = await Enlist(world, 15f, 30f, 45f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids);
        Assert.True(siege.Step <= 1, $"the siege is at step {siege.Step} already");
        return (world, siege);
    }

    /// <summary>The first step the siege moves to after now, or -1 within <paramref name="ms"/>.</summary>
    private static async Task<int> NextStep(GroupTestWorld world, PlayerbotSiege siege, uint ms)
    {
        int before = siege.Step, seen = -1;
        await world.RunUntilAsync(ms, () =>
        {
            if (siege.Step != before) seen = siege.Step;
            return seen >= 0;
        });
        return seen;
    }

    /// <summary>The distinct steps the siege goes through (the first is the step now) until it reaches <paramref name="target"/> or <paramref name="ms"/> have run.</summary>
    private static async Task<List<int>> StepsUntil(GroupTestWorld world, PlayerbotSiege siege, int target, uint ms)
    {
        var seen = new List<int> { siege.Step };
        await world.RunUntilAsync(ms, () =>
        {
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return siege.Step >= target;
        });
        return seen;
    }

    private static void AssertOneAtATime(List<int> seen, string story)
    {
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] == seen[i - 1] + 1, $"the step went {string.Join(" -> ", seen)}: {story}");
    }

    [Fact]
    public async Task TwoRaidsWhoseLeadersFoughtOnPastAWaypoint_NeverStandingAtIt_GoOnOneStepAtATime_UpToTheFurthestWaypointBothReached()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, 45f); // the third waypoint, the step is at the first or second
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 3, 20_000);
        string story = await world.TraceAsync();
        AssertOneAtATime(seen, story);
        Assert.True(seen.Count >= 2 && seen[1] == before + 1, $"the step went {string.Join(" -> ", seen)}");
        Assert.Equal(3, siege.Step); // every leader stood at the third waypoint: the step after it is the fourth
        Assert.Contains(world.Coordinator.Events, e => e.Contains("every raid has reached waypoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoRaidsReachingTheWaypointAtDifferentTimes_NeverTogether_StillMoveTheStepByOne()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        float x = siege.Waypoints[step].X - Origin.X;
        bool together = false;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[1], -80f);
            Teleport(world, siege.Raids[0], x);
            return true;
        });

        // The first raid stands at the waypoint (once is enough), then is dragged away; the second arrives after, with the first 60 yards off.
        Assert.True(await world.RunUntilAsync(5_000, () => siege.Raids[0].FurthestWaypoint >= step), "the first raid never stood at the waypoint");
        Assert.Equal(step, siege.Step);
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], -60f);
            return true;
        });
        await world.RunAsync(500);
        Assert.Equal(step, siege.Step);
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[1], x);
            return true;
        });

        List<int> seen = [siege.Step];
        await world.RunUntilAsync(3_000, () =>
        {
            together |= siege.Raids.All(r => r.AtWaypointReal);
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return false;
        });

        Assert.False(together, "both leaders stood at the waypoint together: the lockstep moved the step, not the credit");
        Assert.True(seen.SequenceEqual([step, step + 1]), $"the step went {string.Join(" -> ", seen)}" + Environment.NewLine + await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"every raid has reached waypoint {step + 1}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneRaidAtAWaypointAndTheOtherBeyondIt_TheStepGoesOnOneAtATime_UpToTheLowerRaidsWaypoint()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 30f); // the second waypoint
            Teleport(world, siege.Raids[1], 45f); // the third
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 2, 20_000);
        AssertOneAtATime(seen, await world.TraceAsync());
        Assert.True(seen.Count >= 2 && seen[1] == before + 1, $"the step went {string.Join(" -> ", seen)}");
        Assert.Equal(2, siege.Step); // the credit stops there: the lower raid has not stood at the third waypoint (it walks there, and the lockstep goes on)
    }

    [Fact]
    public async Task OneRaidFarAhead_WhileTheOtherNeverReachedAnything_MovesNoStep()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int before = siege.Step;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 45f);
            Teleport(world, siege.Raids[1], -40f); // behind the start
            return true;
        });

        Assert.Equal(-1, await NextStep(world, siege, 2_000));
        Assert.Equal(before, siege.Step);
        Assert.Equal(2, siege.Raids[0].FurthestWaypoint);
        Assert.Equal(-1, siege.Raids[1].FurthestWaypoint);
    }

    [Fact]
    public async Task AFurtherWaypointReached_WithAMemberBeyondLagYards_GivesNoCredit_UntilTheLagClears()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        int before = siege.Step;
        PlayerbotSiegeRaid lagging = siege.Raids[0];
        var held = lagging.Group!.Members.First(m => !m.Real && m.BotId != lagging.Group.LeaderBotId);
        await world.OnWorldAsync(() =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, 45f);
            return true;
        });

        // One member of the first raid stays 130 yards behind its leader: the leaders have both been at the third waypoint, and the step must not move.
        bool moved = false;
        await world.RunUntilAsync(4_000, () =>
        {
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(Origin.X - 80f, Origin.Y, Origin.Z, 0, Now(world));
            moved |= siege.Step != before;
            return moved;
        });
        Assert.False(moved, $"the step moved to {siege.Step} with a member {lagging.LastFacts.MaxLagYards:F0} yd behind: {lagging.LastFacts}");
        Assert.True(lagging.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards, "the member is far: " + lagging.LastFacts);
        Assert.True(siege.Raids.All(r => r.FurthestWaypoint >= 2), "both leaders have stood at the third waypoint");

        // The member catches up: the credit applies, by one step.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, lagging);
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(leader.X, leader.Y, leader.Z, 0, Now(world));
            return true;
        });
        Assert.Equal(before + 1, await NextStep(world, siege, 20_000));
    }

    [Fact]
    public async Task ALoneRaidThatFoughtItsWayToTheLastWaypoint_HeadsForTheObjective_AndWinsAsEver()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 1, objectiveFar: false);
        await using GroupTestWorld _ = world;
        await world.OnWorldAsync(() =>
        {
            Teleport(world, siege.Raids[0], 60f); // the last waypoint
            return true;
        });

        List<int> seen = await StepsUntil(world, siege, 3, 30_000); // the last waypoint: the step after it is the approach to the objective, which the raid fights from there
        AssertOneAtATime(seen, await world.TraceAsync());
        Assert.Equal(3, siege.Step);
        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Siege is null), await world.TraceAsync());
        Assert.Equal("won", world.Coordinator.SiegeOutcome);
    }

    [Fact]
    public async Task APushingRaidWhoseLagIsWaived_WithTheFurthestWaypointTwoBeyondTheStep_MovesByOneStep_AndSaysSo()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        bool planted = false, waived = false;
        var seen = new List<int> { siege.Step };
        bool moved = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            // The leader has fought past the next waypoint too, as far as the siege knows.
            if (!planted && raid.AtWaypoint && raid.PushingThrough)
            {
                raid.FurthestWaypoint = siege.Step + 2;
                planted = true;
            }

            waived |= raid.LastFacts.LagWaived;
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return seen.Count > 1;
        });
        Assert.True(moved && planted, $"the step never moved (planted {planted}): {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());

        // The waiver is spent by that one step: the member is still held, and nothing moves the step again.
        await world.RunUntilAsync(5_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (siege.Step != seen[^1]) seen.Add(siege.Step);
            return false;
        });
        Assert.True(seen.SequenceEqual([0, 1]), $"the step went {string.Join(" -> ", seen)}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.Contains(world.Coordinator.Events, e => e.Contains("pushing on from waypoint 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWaivedLag_IsSpentByTheStepItMoves_EvenWhenTheNextWaypointLiesInTheSameDisk()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 24f, 28f, 32f); // 4 yards apart: the leader stands in the next disk too
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        var changes = new List<(int Step, uint At)>();
        int last = siege.Step;
        bool two = await world.RunUntilAsync(PlayerbotSiegeCommander.PushThroughQuietMs + (2 * PlayerbotGroupCoordinator.LagHoldMs) + 60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            if (siege.Step != last)
            {
                last = siege.Step;
                changes.Add((last, Now(world)));
            }

            return changes.Count >= 2;
        });

        string story = string.Join(", ", changes.Select(c => $"step {c.Step} at {c.At}")) + $": {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync();
        Assert.True(two, "the step moved fewer than twice: " + story);
        // The first bump is the waived one (after LagHoldMs at the waypoint); the second costs a fresh LagHoldMs, not 400 ms and not the 5 s of a retry.
        Assert.Equal(changes[0].Step + 1, changes[1].Step);
        uint gap = unchecked(changes[1].At - changes[0].At);
        Assert.True(gap >= PlayerbotGroupCoordinator.LagHoldMs - 2_000, $"the second bump came {gap} ms after the first (a waiver is spent by the step it moves; the hold is {PlayerbotGroupCoordinator.LagHoldMs} ms): " + story);
    }

    [Fact]
    public async Task AMemberBeyondLagYardsForLagHoldMs_OutOfCombat_NoLongerHoldsTheRegroup_InTheWorld()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        bool regrouped = false;
        uint since = 0;
        bool released = await world.RunUntilAsync(PlayerbotGroupCoordinator.LagHoldMs + 60_000, () =>
        {
            world.World.Host.World.FindOnlinePlayer(held.Guid)!.Relocate(Origin.X, Origin.Y + 120f, Origin.Z, 0, Now(world));
            if (raid.LastFacts.MaxLagYards > PlayerbotSiegeCommander.LagYards)
            {
                if (since == 0) since = Now(world);
                regrouped |= raid.Order == PlayerbotSiegeOrder.Regroup;
            }

            return raid.LastFacts.LagWaived && raid.Order != PlayerbotSiegeOrder.Regroup;
        });

        Assert.True(regrouped, "the raid never regrouped for the straggler");
        Assert.True(released, $"the regroup was never lifted: {raid.Order} {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
        Assert.True(unchecked(Now(world) - since) >= PlayerbotGroupCoordinator.LagHoldMs - 2_000, "the lag was waived before LagHoldMs");
    }

    [Fact]
    public async Task APeerAwayAfterReachingTheWaypoint_WithAStragglerBeyondLagYards_StepsByCreditOnceTheLagIsWaived()
    {
        await using GroupTestWorld world = await StartEmptyWay();
        world.Options.Party.TeleportToLeader = false;
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 2);
        PlayerbotSiegeRaid raid = siege.Raids[0], peer = siege.Raids[1];
        var held = raid.Group!.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X, Origin.Y + 120f, far.Z, 0, Now(world));
        }

        await world.OnWorldAsync(() => { KeepInCombat(world, raid); Hold(); return true; });
        // Both raids stand at the waypoint; then the peer is kept away from it, as a defender post that keeps pulling it off would.
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            return peer.FurthestWaypoint >= 0 && raid.FurthestWaypoint >= 0 && siege.Step == 0 && raid.AtWaypoint && raid.PushingThrough;
        }), "the raids never stood at the first waypoint: " + await world.TraceAsync());
        int step = siege.Step;
        bool waived = false;
        await world.RunUntilAsync(PlayerbotGroupCoordinator.LagHoldMs + 40_000, () =>
        {
            KeepInCombat(world, raid);
            Hold();
            foreach (var member in peer.Group!.Members.Where(m => !m.Real && m.Joined))
                world.World.Host.World.FindOnlinePlayer(member.Guid)?.Relocate(Origin.X - 60f, Origin.Y, Origin.Z, 0, Now(world));
            waived |= raid.LastFacts.LagWaived;
            return siege.Step != step;
        });

        // The lag clock runs wherever the leader is: after LagHoldMs the lag is waived, and the credit (the peer stood at the waypoint once) moves the step by one and says the lag was waived.
        Assert.True(waived, "the lag was never waived: " + raid.LastFacts);
        Assert.Equal(step + 1, siege.Step);
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"every raid has reached waypoint {step + 1}", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("pushing on from waypoint", StringComparison.Ordinal));
    }

    // --- the per-step deadline ------------------------------------------------------------------------------------------------

    /// <summary>Keeps every raid 60 yards behind the start (where no waypoint is) until the step has lasted <see cref="PlayerbotGroupCoordinator.StepDeadlineMs"/> minus <paramref name="beforeMs"/>.</summary>
    private static Task<bool> HoldAwayUntilDeadline(GroupTestWorld world, PlayerbotSiege siege, uint beforeMs)
        => world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return unchecked(Now(world) - siege.StepSinceMs) >= PlayerbotGroupCoordinator.StepDeadlineMs - beforeMs;
        });

    [Fact]
    public async Task TwoRaidsHeldAwayFromTheWaypoint_ForStepDeadlineMs_MoveOnOneStep_AndSaySo()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        Assert.True(await HoldAwayUntilDeadline(world, siege, 20_000), "the run ended before the deadline: " + await world.TraceAsync());
        Assert.Equal(step, siege.Step); // 20 s short of the deadline: the step stays put
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("not taken", StringComparison.Ordinal));

        Assert.True(await world.RunUntilAsync(40_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        }), "the step never moved at the deadline: " + await world.TraceAsync());
        Assert.Equal(step + 1, siege.Step);
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"waypoint {step + 1} not taken in {PlayerbotGroupCoordinator.StepDeadlineMs / 1000}s, moving on to waypoint {step + 2}", StringComparison.Ordinal));
        Assert.All(siege.Raids, r => Assert.Contains(r.Announcer.Sent, line => line.Contains("not taken", StringComparison.Ordinal)));
        await world.RunAsync(1_000);
        Assert.Contains(siege.Raids, r => r.Detail.Contains("furthest=", StringComparison.Ordinal) && r.Detail.Contains("deadline=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStepWhoseRetreatLatchWasHeldPastTheDeadline_MovesOnlyAFreshDeadlineAfterTheLatchClears()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        PlayerbotSiegeRaid latched = siege.Raids[0];
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 120_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            latched.RetreatLatched = true;
            latched.RetreatSinceMs = Now(world);
            KeepInCombat(world, latched);
            return unchecked(Now(world) - siege.StepSinceMs) >= PlayerbotGroupCoordinator.StepDeadlineMs + 30_000;
        }), "the run ended early: " + await world.TraceAsync());
        Assert.Equal(step, siege.Step); // the latch held the step past the deadline

        // The latch clears (the raid is out of combat): the step does not move at once.
        uint cleared = 0;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            if (!latched.RetreatLatched) cleared = Now(world);
            return cleared != 0;
        }), "the latch never cleared: " + latched.LastFacts);
        await world.RunUntilAsync(30_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        });
        Assert.Equal(step, siege.Step);

        // And it moves after a fresh deadline.
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        }), "the step never moved after the latch cleared: " + await world.TraceAsync());
        Assert.True(unchecked(Now(world) - cleared) >= PlayerbotGroupCoordinator.StepDeadlineMs - 5_000, $"the step moved {unchecked(Now(world) - cleared)} ms after the latch cleared");
    }

    [Fact]
    public async Task APinnedRetreat_AdvancesRetreatSinceMs_AndReleasesAfterRetreatMaxMs_ResettingStepSinceMs()
    {
        await using GroupTestWorld world = await StartStrongGuards();
        (PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await IntoTheFight(world);
        uint stepSinceBefore = siege.StepSinceMs;
        await KillAsync(world, raid, "mage");
        Assert.True(await world.RunUntilAsync(30_000, () => raid.RetreatLatched && raid.Order == PlayerbotSiegeOrder.Retreat), await world.TraceAsync());
        Assert.NotEqual(0u, raid.RetreatSinceMs);
        uint retreatSince = raid.RetreatSinceMs;

        // Keep the raid pinned in combat far from the retreat point (60 yards out, while the retreat point is at 20 yards).
        uint maxRetreating = 0;
        bool seenOrderRally = false;
        bool released = await world.RunUntilAsync(PlayerbotSiegeCommander.RetreatMaxMs + 20_000, () =>
        {
            TopUpGuards(world, raid);
            KeepInCombat(world, raid);
            Teleport(world, raid, 60f);
            maxRetreating = Math.Max(maxRetreating, raid.LastFacts.MsRetreating);
            seenOrderRally |= raid.Order == PlayerbotSiegeOrder.Rally;
            return !raid.RetreatLatched;
        });

        Assert.True(released, $"the pinned retreat latch never released (MsRetreating={maxRetreating}, Order={raid.Order}): " + await world.TraceAsync());
        Assert.False(seenOrderRally, "the raid reached AtRally instead of expiring via RetreatMaxMs");
        Assert.True(maxRetreating >= PlayerbotSiegeCommander.RetreatMaxMs - 1_000, $"MsRetreating only reached {maxRetreating}");
        Assert.Equal(0u, raid.RetreatSinceMs);
        Assert.True(raid.WasInCombat, "the raid left combat instead of expiring via RetreatMaxMs");
        Assert.True(siege.StepSinceMs > stepSinceBefore && unchecked(siege.StepSinceMs - retreatSince) >= PlayerbotSiegeCommander.RetreatMaxMs - 1_000,
            $"StepSinceMs ({siege.StepSinceMs}) was not reset when the retreat latch released after {retreatSince}");
    }

    [Fact]
    public async Task TheDeadline_IsPerStep_AndMovesByOne()
    {
        (GroupTestWorld world, PlayerbotSiege siege) = await CreditFixture(raids: 2);
        await using GroupTestWorld _ = world;
        int step = siege.Step;
        Assert.True(await HoldAwayUntilDeadline(world, siege, 2_000), "the run ended before the deadline: " + await world.TraceAsync());
        await world.RunUntilAsync(10_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step;
        });
        Assert.Equal(step + 1, siege.Step);

        // The clock started afresh with the new step: nothing moves it again for a whole deadline, and then it moves by one.
        Assert.True(await HoldAwayUntilDeadline(world, siege, 20_000), "the run ended before the second deadline: " + await world.TraceAsync());
        Assert.Equal(step + 1, siege.Step);
        await world.RunUntilAsync(40_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            return siege.Step != step + 1;
        });
        Assert.Equal(step + 2, siege.Step);
    }

    [Fact]
    public void ARaidFlippingBetweenUnledAndLed_OnlyPausesTheDeadline_ByTheTimeItWasUnled()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], default, 1_000);
        uint now = 1_000, unledTotal = 0, moved = 0;
        // Every 10 s the raid loses its leader for 1 s (a death and a prompt promotion); the ticks are 500 ms apart.
        while (moved == 0 && now < 1_000 + (2 * PlayerbotGroupCoordinator.StepDeadlineMs))
        {
            now += 500;
            bool unled = (now / 500) % 20 == 0 || (now / 500) % 20 == 1; // 1 s in every 10 s
            if (unled) unledTotal += 500;
            siege.TrackLeaders(unled, now);
            if (!unled && PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, now)) moved = now;
        }

        Assert.NotEqual(0u, moved);
        uint expected = 1_000 + PlayerbotGroupCoordinator.StepDeadlineMs + unledTotal;
        Assert.InRange(moved, expected - 1_000, expected + 2_000);
    }

    [Fact]
    public void TheAnnouncer_Force_SendsTheLineWithinTheGap_WhereOfferDoesNot()
    {
        var announcer = new PlayerbotSiegeAnnouncer();
        Assert.True(announcer.Offer("first", 10_000));
        Assert.False(announcer.Offer("second", 11_000)); // within the gap
        announcer.Force("forced", 11_500);
        Assert.Equal(new[] { "first", "forced" }, announcer.Sent);
    }

    [Fact]
    public void TheDeadline_WaitsForARetreatLatch_AndForAnUnledRaid()
    {
        var siege = new PlayerbotSiege(1, 0, 1, [new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], default, 1_000);
        uint due = 1_000 + PlayerbotGroupCoordinator.StepDeadlineMs;
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due - 1));
        Assert.True(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due));
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 1, false, due)); // a raid without a leader
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, true, due)); // a retreat latch
        siege.Step = 2; // past the last waypoint: nothing to move on to
        Assert.False(PlayerbotGroupCoordinator.StepDeadlineDue(siege, 0, false, due + 1_000_000));
    }

    [Fact]
    public async Task ARaidUnledPastTheDeadline_ThenLedAgain_DoesNotMoveTheStepAtOnce_ButAfterTheRemainingLedTime()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);
        PlayerbotSiegeRaid other = siege.Raids[0];
        uint stepSince = 0, unledSince = 0, began = Now(world);

        // Unled for longer than the deadline: the step stays.
        await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, Now(world)); // no promotion
            Teleport(world, other, -60f);
            if (stepSince == 0 && siege.UnledSinceMs != 0) { stepSince = siege.StepSinceMs; unledSince = siege.UnledSinceMs; }
            return unchecked(Now(world) - began) >= PlayerbotGroupCoordinator.StepDeadlineMs + 40_000;
        });
        Assert.NotEqual(0u, unledSince);
        Assert.Equal(0, siege.Step);

        // Promotion runs: the raid is led again. The step does not move at once, nor within 30 s.
        uint ledAt = 0;
        bool movedEarly = false;
        await world.RunUntilAsync(120_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) if (raid.Group is not null && (raid != fallen || siege.UnledSinceMs == 0)) Teleport(world, raid, -60f);
            if (ledAt == 0 && siege.UnledSinceMs == 0) ledAt = Now(world);
            movedEarly |= siege.Step != 0;
            return ledAt != 0 && unchecked(Now(world) - ledAt) >= 30_000;
        });
        Assert.NotEqual(0u, ledAt);
        Assert.False(movedEarly, "the step moved within 30 s of the leader's return");

        // It moves after what remained of the deadline when the raid lost its leader.
        uint movedAt = 0;
        Assert.True(await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 60_000, () =>
        {
            foreach (PlayerbotSiegeRaid raid in siege.Raids) Teleport(world, raid, -60f);
            if (siege.Step != 0 && movedAt == 0) movedAt = Now(world);
            return movedAt != 0;
        }), "the step never moved: " + await world.TraceAsync());
        uint expected = ledAt + PlayerbotGroupCoordinator.StepDeadlineMs - unchecked(unledSince - stepSince);
        Assert.InRange(movedAt, expected - 3_000, expected + 4_000);
    }

    [Fact]
    public async Task TheDeadline_DoesNotMoveTheStep_WhileARaidIsWithoutALeader()
    {
        await using GroupTestWorld world = await StartWorld(guards: false);
        (PlayerbotSiege siege, PlayerbotSiegeRaid fallen) = await LeaderlessRaid(world);
        bool moved = false;
        await world.RunUntilAsync(PlayerbotGroupCoordinator.StepDeadlineMs + 40_000, () =>
        {
            fallen.PromoteTriedMs = Math.Max(1u, Now(world));
            moved |= siege.Step != 0;
            return moved;
        });
        Assert.False(moved, "the step moved with a raid without a leader");
    }

    // --- what the scan counts, and the marching leader ----------------------------------------------------------------------------

    /// <summary>One raid, one waypoint 100 yards east (the way covers x 0..100), the given creatures near it and the objective far away; the creatures stand where they were put.</summary>
    private static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> ScanFixture(
        Func<List<(CreatureTemplate Template, CreatureSpawn Spawn)>> creatures, float waypointZ = 0f)
    {
        List<(CreatureTemplate, CreatureSpawn)> extra = creatures();
        extra.Add(Guard(Objective, 400f, 0f, health: 120));
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [.. extra]);
        (Guid Id, string Name)[] bots = await Enlist(world);
        await world.OnWorldAsync(() =>
        {
            Assert.Null(world.Coordinator.AddSiegeWaypoint(0, new Vector3(Origin.X + 100f, Origin.Y, Origin.Z + waypointZ)));
            return true;
        });
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        await world.OnWorldAsync(() =>
        {
            CreatureMapSystem system = Leader(world, siege.Raids[0]).Map!.FindUpdater<CreatureMapSystem>()!;
            foreach (Creature creature in system.Creatures.Where(c => c.Entry != Objective)) system.SetAiImmobilized(creature, true, combatOnly: false);
            return true;
        });
        return (world, siege, siege.Raids[0]);
    }

    private static Creature Spawned(GroupTestWorld world, PlayerbotSiegeRaid raid, uint entry)
        => Leader(world, raid).Map!.FindUpdater<CreatureMapSystem>()!.Creatures.First(c => c.Entry == entry);

    [Fact]
    public async Task ASiegeLeaderWithNoPathToItsPullTarget_ShunsIt_AndTheSkullMovesOn()
    {
        // An unreachable hostile within 40 yards (30) and a reachable one at 45.
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await ScanFixture(() => [Guard(GuardA, 30f, 0f, health: 600), Guard(GuardB, 45f, 0f, health: 600)]);
        await using GroupTestWorld _ = world;
        ObjectGuid unreachable = default, next = default;
        await world.OnWorldAsync(() =>
        {
            unreachable = Spawned(world, raid, GuardA).Guid;
            next = Spawned(world, raid, GuardB).Guid;
            world.Options.AllowedMaps = [1]; // walking is refused: the leader finds no way to the guard it is sent at
            return true;
        });

        bool skulledFirst = false, skullMoved = false;
        await world.RunUntilAsync(30_000, () =>
        {
            ObjectGuid skull = Skull(world, raid);
            skulledFirst |= skull == unreachable;
            skullMoved |= skulledFirst && skull == next;
            return skullMoved;
        });

        Assert.True(skulledFirst, "the nearest hostile was never skulled: " + raid.LastFacts);
        Assert.True(await world.OnWorldAsync(() => raid.Group!.IsShunned(unreachable, Now(world))), "the unreachable target was not given up");
        Assert.True(skullMoved, $"the skull never moved to the next hostile: {raid.LastFacts}" + Environment.NewLine + await world.TraceAsync());
    }

    [Fact]
    public async Task TheObjectiveWithNoPathToIt_IsNeverShunned_StaysInHostiles_AndRemainsAValidPullTarget()
    {
        // The objective 30 yards ahead, walking refused: every plan to it fails.
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(Objective, 30f, 0f, health: 1_000_000)]);
        await using GroupTestWorld _ = world;
        (Guid Id, string Name)[] bots = await Enlist(world, 100f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        ObjectGuid objective = default;
        await world.OnWorldAsync(() =>
        {
            Creature sylvanas = Spawned(world, raid, Objective);
            objective = sylvanas.Guid;
            sylvanas.Map!.FindUpdater<CreatureMapSystem>()!.SetAiImmobilized(sylvanas, true, combatOnly: false);
            world.Options.AllowedMaps = [1];
            return true;
        });

        var leaderMember = raid.Group!.Members.First(m => m.BotId == raid.Group.LeaderBotId);
        bool noPath = false, shunned = false, hostileGone = false, pulledThenDropped = false, pulled = false;
        await world.RunUntilAsync(30_000, () =>
        {
            string step = leaderMember.AI?.Step ?? string.Empty;
            noPath |= step == $"fight:{Objective}:no-path";
            if (!noPath) return false;
            shunned |= raid.Group.IsShunned(objective, Now(world));
            hostileGone |= raid.LastFacts.Hostiles < 1;
            if (raid.PullTarget == objective) pulled = true;
            else if (pulled) pulledThenDropped = true;
            return false; // the run lasts its whole time
        });

        Assert.True(noPath, "the leader never failed to plan a way to the objective: " + await world.TraceAsync());
        Assert.False(shunned, "the objective was shunned");
        Assert.False(hostileGone, "the objective dropped out of the hostiles: " + raid.LastFacts);
        Assert.False(pulledThenDropped, "the pull target left the objective");
    }

    [Fact]
    public async Task ACivilianNotFightingTheRaid_IsNotInHostiles_OneThatDoesIs()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await ScanFixture(() =>
        {
            (CreatureTemplate template, CreatureSpawn spawn) = Guard(GuardA, 30f, 0f, health: 600);
            return [(template with { Civilian = true }, spawn)];
        });
        await using GroupTestWorld _ = world;
        int most = 0;
        await world.RunUntilAsync(3_000, () => { most = Math.Max(most, raid.LastFacts.Hostiles); return false; });
        Assert.Equal(0, most);

        await world.OnWorldAsync(() =>
        {
            Spawned(world, raid, GuardA).Combat.Threat.AddThreat(Leader(world, raid), 100_000f); // it attacks the raid
            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => raid.LastFacts.Hostiles >= 1), "a civilian that fights the raid was not counted: " + raid.LastFacts);
    }

    [Fact]
    public async Task AUnit15YardsAboveTheLeader_IsNotInHostiles_UnlessItAttacksTheRaid()
    {
        // The way climbs 30 yards to its waypoint, so the unit is on the route's levels but 15 yards above the leader.
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await ScanFixture(() =>
        {
            (CreatureTemplate template, CreatureSpawn spawn) = Guard(GuardA, 30f, 0f, health: 600);
            return [(template, spawn with { Z = spawn.Z + 15f })];
        }, waypointZ: 30f);
        await using GroupTestWorld _ = world;
        Assert.True(await world.OnWorldAsync(() => Spawned(world, raid, GuardA).Z - Leader(world, raid).Z > PlayerbotSiegeCommander.WaypointLevelYards), "the unit is not above the leader");
        int most = 0;
        await world.RunUntilAsync(3_000, () => { most = Math.Max(most, raid.LastFacts.Hostiles); return false; });
        Assert.Equal(0, most);

        await world.OnWorldAsync(() =>
        {
            Spawned(world, raid, GuardA).Combat.Threat.AddThreat(Leader(world, raid), 100_000f);
            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => raid.LastFacts.Hostiles >= 1), "a unit above the leader that attacks the raid was not counted: " + raid.LastFacts);
    }

    [Fact]
    public async Task AnAdvancingLeader_DoesNotTurnToPeelForAMember_AndReachesTheWaypoint()
    {
        // A guard (not rooted) beside a member 20 yards behind the leader attacks that member.
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null, extraCreatures: [Guard(GuardA, 30f, 60f, health: 600), Guard(Objective, 400f, 0f, health: 120)]);
        await using GroupTestWorld _ = world;
        (Guid Id, string Name)[] bots = await Enlist(world, 60f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        PlayerbotSiegeRaid raid = siege.Raids[0];
        var leaderMember = raid.Group!.Members.First(m => m.BotId == raid.Group.LeaderBotId);
        var victim = raid.Group.Members.First(m => !m.Real && m.BotId != raid.Group.LeaderBotId);
        Creature? guard = null;
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, raid);
            Player member = world.World.Host.World.FindOnlinePlayer(victim.Guid)!;
            guard = Spawned(world, raid, GuardA);
            member.Relocate(leader.X - 20f, leader.Y, leader.Z, 0, Now(world));
            guard.Relocate(member.X - 2f, member.Y, member.Z, 0, Now(world));
            guard.Combat.Threat.AddThreat(member, 100_000f);
            return true;
        });

        Assert.True(await world.RunUntilAsync(10_000, () => world.World.Host.World.FindOnlinePlayer(victim.Guid)!.Combat.Attackers.Contains(guard!)), "the guard never attacked the member: " + await world.TraceAsync());

        // Under a marching order the tank leader peels for no member; the member itself answers its attacker.
        MethodInfo select = typeof(PlayerbotGroupAI).GetMethod("SelectTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (Unit? ofLeader, Unit? ofMember) = await world.OnWorldAsync(() =>
        {
            raid.Order = PlayerbotSiegeOrder.Advance;
            raid.Push = PlayerbotSiegePush.None;
            Player leader = Leader(world, raid);
            Player member = world.World.Host.World.FindOnlinePlayer(victim.Guid)!;
            Unit? forLeader = (Unit?)select.Invoke(leaderMember.AI, [leader, raid.Group, leaderMember, leader, true]);
            Unit? forMember = (Unit?)select.Invoke(victim.AI, [member, raid.Group, victim, leader, false]);
            return (forLeader, forMember);
        });
        Assert.Null(ofLeader);
        Assert.Same(guard, ofMember);

        // And once the raid has dealt with the guard the leader reaches the waypoint. A creature that is hit keeps its leash fresh (vmangos
        // SetInCombatWithAggressor), so the guard now fights on until it dies instead of leashing away early; the bots it wounded are topped up
        // here, because a hurt raid waits for a heal this fixture does not provide.
        Assert.True(await world.RunUntilAsync(240_000, () =>
        {
            foreach (var member in raid.Group!.Members)
            {
                if (!member.Real && world.World.Host.World.FindOnlinePlayer(member.Guid) is { } bot)
                {
                    bot.Health = bot.MaxHealth;
                }
            }

            return siege.Step >= 1;
        }), "the leader never reached the waypoint: " + raid.LastFacts + Environment.NewLine + await world.TraceAsync());
    }

    /// <summary>
    /// The old answer fixture: waypoint 2 lies 12 yards past waypoint 1, so a leader answering an attacker 29 yards past waypoint 1 walks through waypoint 2's disk, and back. The step moves by one at a time, and not while the leader is out answering.
    /// </summary>
    [Fact]
    public async Task ALeaderAnsweringThroughTheNextWaypointsDisk_MovesTheStepByOneAtMost_AndNotWhileItIsOutAnswering()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await StalemateFixture(4f, 150f, 162f, 212f);
        await using GroupTestWorld _ = world;
        world.Options.Party.TeleportToLeader = false;
        Guid leaderBot = raid.Group!.LeaderBotId;
        var held = raid.Group.Members.First(m => !m.Real && m.BotId != leaderBot);
        void Hold()
        {
            Player far = world.World.Host.World.FindOnlinePlayer(held.Guid)!;
            far.Relocate(Origin.X + 38f, Origin.Y, far.Z, 0, Now(world));
        }

        int stepAtPush = -1, prevStep = -1;
        bool prevAnswering = false, passedNext = false;
        Creature? attacker = null;
        uint sentAt = 0;
        var problems = new List<string>();
        bool done = await world.RunUntilAsync(PlayerbotSiegeCommander.StalemateMs + 500_000, () =>
        {
            TopUpGuards(world, raid);
            TopUpMembers(world, raid);
            Player leader = Leader(world, raid);
            if (raid.Push == PlayerbotSiegePush.Stalemate && stepAtPush < 0) stepAtPush = siege.Step;
            if (stepAtPush < 0) return false;
            Hold();
            if (attacker is null && SendRangedAttacker(siege, raid, leader, stepAtPush, Now(world), 29f) is { } sent)
            {
                attacker = sent;
                sentAt = Now(world);
            }

            if (attacker is null) return false;
            if (attacker.IsAlive && !ReferenceEquals(attacker.Combat.Victim, leader)) attacker.Combat.Threat.AddThreat(leader, 100_000f);
            passedNext |= raid.FurthestWaypoint >= stepAtPush + 1;
            if (prevStep >= 0 && siege.Step != prevStep)
            {
                float off = FlatYards(leader, siege.Waypoints[prevStep]);
                if (siege.Step != prevStep + 1) problems.Add($"the step went {prevStep} -> {siege.Step} at {Now(world)}");
                if (prevAnswering && !raid.AnswerSpent && off > PlayerbotSiegeCommander.WaypointYards + 1f)
                    problems.Add($"the step went {prevStep} -> {siege.Step} at {Now(world)} while the leader was out answering ({off:F1} yd from the waypoint)");
            }

            prevStep = siege.Step;
            prevAnswering = raid.AnsweringStep >= 0;
            return siege.Step > stepAtPush + 1 || raid.PushDeniedStep == stepAtPush || raid.Retreats > 0 || unchecked(Now(world) - sentAt) > 450_000;
        });

        string story = $"done={done} step={siege.Step}/{stepAtPush} furthest={raid.FurthestWaypoint} {raid.LastFacts}";
        Assert.True(attacker is not null, "the probe never fired: " + story);
        Assert.True(passedNext, "the leader never stood in the next waypoint's disk: " + story);
        Assert.Empty(problems);
        Assert.True(siege.Step > stepAtPush, "the step never moved on: " + story);
    }

    /// <summary>One raid, waypoints at 20 and 40, a guard A far to the north of the way, a guard B far to the south; the objective far away.</summary>
    private static async Task<(GroupTestWorld World, PlayerbotSiege Siege, PlayerbotSiegeRaid Raid)> TetherFixture()
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], null,
            extraCreatures: [Guard(GuardA, 30f, 50f, health: 600), Guard(GuardB, 30f, -50f, health: 600), Guard(Objective, 400f, 0f, health: 120)]);
        (Guid Id, string Name)[] bots = await Enlist(world, 20f, 40f);
        PlayerbotSiege siege = await Start(world, bots, raids: 1);
        // The guards stand where they were put: neither walks to the raid.
        await world.OnWorldAsync(() =>
        {
            Player leader = Leader(world, siege.Raids[0]);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            foreach (Creature guard in system.Creatures.Where(g => g.Entry is GuardA or GuardB)) system.SetAiImmobilized(guard, true, combatOnly: false);
            return true;
        });
        return (world, siege, siege.Raids[0]);
    }

    [Fact]
    public async Task AHostile50YardsOffTheRoute_IsNotCounted_WhileOneThatFightsTheRaidIs()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await TetherFixture();
        await using GroupTestWorld _ = world;
        int mostCounted = 0;
        await world.OnWorldAsync(() =>
        {
            // Guard B attacks the leader (it cannot move, but it has the leader on its threat list); guard A has nothing to do with the raid.
            Player leader = Leader(world, raid);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            system.Creatures.First(g => g.Entry == GuardB).Combat.Threat.AddThreat(leader, 100_000f);
            Teleport(world, raid, 30f); // within the 55-yard scan of both guards
            return true;
        });

        bool aCounted = false;
        await world.RunUntilAsync(3_000, () =>
        {
            mostCounted = Math.Max(mostCounted, raid.LastFacts.Hostiles);
            aCounted |= raid.LastFacts.Hostiles >= 2;
            return false;
        });

        Assert.True(mostCounted >= 1, "the guard that fights the raid was never counted: " + raid.LastFacts + Environment.NewLine + await world.TraceAsync());
        Assert.False(aCounted, "the guard 50 yards off the route was counted: " + raid.LastFacts);
    }

    [Fact]
    public async Task ALeaderMoreThan40YardsOffTheRoute_OutOfCombat_AdvancesInsteadOfPullingAHostileNearTheRoute()
    {
        (GroupTestWorld world, PlayerbotSiege siege, PlayerbotSiegeRaid raid) = await TetherFixture();
        await using GroupTestWorld _ = world;
        await world.OnWorldAsync(() =>
        {
            // Guard A stands on the route and in reach of a leader 60 yards north of it.
            Player leader = Leader(world, raid);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            Creature guard = system.Creatures.First(g => g.Entry == GuardA);
            guard.Relocate(Origin.X + 10f, Origin.Y + 25f, Origin.Z, 0, Now(world));
            Teleport(world, raid, 10f, 60f);
            return true;
        });

        var orders = new List<PlayerbotSiegeOrder>();
        await world.RunUntilAsync(1_500, () =>
        {
            if (raid.LastFacts.Hostiles > 0 && !raid.LastFacts.InCombat) orders.Add(raid.Order);
            return false;
        });

        Assert.NotEmpty(orders);
        Assert.DoesNotContain(PlayerbotSiegeOrder.Pull, orders);
        Assert.Contains(PlayerbotSiegeOrder.Advance, orders);
    }
}
