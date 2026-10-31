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
/// Starting a siege on the scenario world: enlisting bots, forming the two raids, lockstep, the skull marks, a dead leader and the end of the siege. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeStartWorldTests : PlayerbotSiegeWorldTestBase
{
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
}
