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
/// Retreats that lose a leader, engagement death counting, the commit line, and which creatures count as hostiles. Split from the former <c>PlayerbotSiegeWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotSiegeWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotSiegeEngagementWorldTests : PlayerbotSiegeWorldTestBase
{
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
}
