using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A wipe and regroup, a raid quest, the formation timeout, and bots with a scenario stay or defend post. Split from the former <c>PlayerbotGroupWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupWipeAndRaidWorldTests : PlayerbotGroupWorldTestBase
{
    [Fact]
    public async Task AWipe_TheSurvivorRetreats_ResurrectsTheDead_AndTheGroupRegroups()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        var tank = await world.AddBotAsync("Wipetank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Wipeprie", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        // The tank falls in the fight (the priest is on the ogre's threat list, as a healer that healed it): half the group is dead, the
        // group is wiping.
        Creature ogre = await world.OnWorldAsync(() =>
        {
            Player victim = world.Player(tank.Id);
            var creature = (Creature)victim.Combat.Victim!;
            creature.Combat.Threat.AddThreat(world.Player(healer.Id), 1f);
            victim.Map!.Combat.DealDamage(creature, victim, victim.Health, direct: false);
            Assert.False(victim.IsAlive);
            return creature;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => group.State == PlayerbotGroupState.Wiped), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(5_000, () => world.Coordinator.FindAI(healer.Id)!.Retreat.Active), await world.TraceAsync());

        // The ogre goes away for a while; the priest comes back and resurrects the tank (its party intake accepts a group member's
        // offer), and the group gathers again, one failure down.
        await world.OnWorldAsync(() =>
        {
            ogre.Map!.FindUpdater<CreatureMapSystem>()!.Despawn(ogre);
            return true;
        });
        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(tank.Id).IsAlive), await world.TraceAsync());
        Assert.Equal(tank.Name, world.Coordinator.FindAI(healer.Id)!.LastResurrection);
        Assert.True(await world.RunUntilAsync(60_000, () => group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling
            or PlayerbotGroupState.Engaging), await world.TraceAsync());
        Assert.Equal(1, group.Failures);
        Assert.Contains(world.Coordinator.Events, e => e.Contains("wiped (1/2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARaidQuest_ForSix_TheLeaderConvertsToARaid_AndSpreadsTheRolesOverTheSubgroups()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([RaidQuest]);
        var tank = await world.AddBotAsync("Raidtank", Human, Warrior, 20, [Taunt]);
        var healer = await world.AddBotAsync("Raidprie", Human, Priest, 20, [LesserHeal, Smite]);
        await world.AddBotAsync("Raidmage", Human, Mage, 20, [Fireball]);
        await world.AddBotAsync("Raidrogue", Human, Rogue, 20, []);
        await world.AddBotAsync("Raidlock", Human, Warlock, 20, []);
        await world.AddBotAsync("Raidhunt", Dwarf, Hunter, 20, []);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Raid, group.Goal.Kind);
        Assert.True(group.Raid);
        await world.OnWorldAsync(() =>
        {
            Group server = world.GroupManager.GetGroup(world.Player(tank.Id).Guid)!;
            Assert.True(server.IsRaid);
            Assert.Equal(6, server.MemberCount);
            Assert.Equal(group.Find(tank.Id)!.SubGroup, server.Find(world.Player(tank.Id).Guid)!.SubGroup);
            Assert.Equal(group.Find(healer.Id)!.SubGroup, server.Find(world.Player(healer.Id).Guid)!.SubGroup);
            Assert.NotEqual(server.Find(world.Player(tank.Id).Guid)!.SubGroup, server.Find(world.Player(healer.Id).Guid)!.SubGroup);
            return true;
        });
    }

    [Fact]
    public async Task WithoutPartners_TheGoalIsSetAsideAfterTheFormationTimeout_AndTheBotGoesOnAlone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Groups.FormationTimeoutSeconds = 30);
        var lone = await world.AddBotAsync("Loneone", Human, Mage, 10, [Fireball]);

        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == lone.Id)), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => world.Bots.FindBrain(lone.Id)!.GroupHeld.Contains((EliteQuest, OgreEntry))));
        Assert.Contains("group=waiting", (await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == lone.Id))).Group);

        Assert.True(await world.RunUntilAsync(40_000, () => world.Coordinator.IsSetAside(lone.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry))), await world.TraceAsync());
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == lone.Id);
        Assert.Empty(await world.OnWorldAsync(() => world.Bots.FindBrain(lone.Id)!.GroupHeld.ToArray()));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("found no partners", StringComparison.Ordinal));
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        // Set aside, it is not waited for again within the set-aside time.
        await world.RunAsync(10_000);
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == lone.Id);
    }

    [Fact]
    public async Task ABotWithAScenarioStay_IsNeverMatchedIntoAQuestGroup_AndWaitsForNone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Stytank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Styheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Stymage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];

        bool staying = false;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            if (!staying && world.HasQuest(healer.Id, EliteQuest))
            {
                Player p = world.Player(healer.Id);
                world.Bots.FindBrain(healer.Id)!.Stay = new PlayerbotStay(p.MapId, new System.Numerics.Vector3(p.X, p.Y, p.Z), 30f, 1, unchecked(world.World.Host.World.NowMs + 600_000));
                staying = true;
            }

            return ids.All(id => world.HasQuest(id, EliteQuest));
        }), await world.TraceAsync());

        await world.RunAsync(40_000);
        await world.OnWorldAsync(() =>
        {
            Assert.False(world.Coordinator.Drives(healer.Id), world.Trace());
            Assert.DoesNotContain(world.Coordinator.Groups, g => g.Members.Any(m => m.BotId == healer.Id));
            Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == healer.Id);
            return true;
        });
    }

    [Fact]
    public async Task ABotWithADefendPost_IsNeverMatchedIntoAQuestGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Deftank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Defheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Defmage", Human, Mage, 10, [Fireball]);
        Guid[] ids = [tank.Id, healer.Id, mage.Id];

        // The healer takes a post the moment it has the quest, before the coordinator can match the three: it is not free for a group.
        bool posted = false;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            if (!posted && world.HasQuest(healer.Id, EliteQuest))
            {
                Player p = world.Player(healer.Id);
                world.Bots.FindBrain(healer.Id)!.Defend = new PlayerbotDefendPost(p.MapId, new System.Numerics.Vector3(p.X, p.Y, p.Z), 30f, 1);
                posted = true;
            }

            return ids.All(id => world.HasQuest(id, EliteQuest));
        }), await world.TraceAsync());

        // Long enough for the three to be matched and the group to leave Forming, as in the test above.
        await world.RunAsync(40_000);
        await world.OnWorldAsync(() =>
        {
            Assert.False(world.Coordinator.Drives(healer.Id), world.Trace()); // on the world thread
            Assert.DoesNotContain(world.Coordinator.Groups, g => g.Members.Any(m => m.BotId == healer.Id));
            return true;
        });
    }
}
