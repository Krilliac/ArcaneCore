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
/// A stopped siege, a dead-leader raid, a stalled siege, a vanished objective, gossip-flagged hostiles and the first retreat cases. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeStopAndRetreatWorldTests : PlayerbotSiegeWorldTestBase
{
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
}
