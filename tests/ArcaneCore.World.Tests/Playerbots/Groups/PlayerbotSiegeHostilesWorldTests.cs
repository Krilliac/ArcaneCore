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
/// What counts as a hostile near the route: civilians, units far above or off the route, and an advancing leader. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeHostilesWorldTests : PlayerbotSiegeWorldTestBase
{
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
