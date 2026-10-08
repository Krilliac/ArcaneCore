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

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Bots group up for content one bot cannot do (<see cref="PlayerbotGroupCoordinator"/>), end to end on the real world handlers and the
/// manual clock: the bots accept their quests from the giver like players, the coordinator matches them, the leader's invitations and
/// the members' acceptances are ordinary client packets, and the group does the content and disbands.
/// </summary>
public sealed class PlayerbotGroupWorldTests
{
    private const byte Human = 1, Dwarf = 3;
    private const byte Warrior = 1, Hunter = 3, Rogue = 4, Priest = 5, Mage = 8, Warlock = 9;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task AnEliteQuest_TooHardAlone_ThreeBotsGroupWithATankAndAHealer_KillIt_AndDisband()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var sent = new ConcurrentQueue<(string Bot, WorldOpcode Opcode)>();
        var tank = await world.AddBotAsync("Grouptank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Groupheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Groupmage", Human, Mage, 10, [Fireball]);
        foreach ((Guid id, string name) in new[] { tank, healer, mage })
            await world.OnWorldAsync(() => world.Session(id).ManagedDispatchObserver = opcode => sent.Enqueue((name, opcode)));

        // The brains take the elite quest from the giver beside them; the coordinator holds its objective for a group.
        Assert.True(await world.RunUntilAsync(60_000, () => new[] { tank, healer, mage }.All(b => world.HasQuest(b.Id, EliteQuest))), world.Trace());
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Quest, group.Goal.Kind);
        Assert.Equal(OgreEntry, group.Goal.ObjectiveEntry);
        Assert.Equal(3, group.Goal.Size);
        Assert.Equal(tank.Id, group.LeaderBotId); // the tank leads
        Assert.Equal(PlayerbotGroupRole.Tank, group.Find(tank.Id)!.Role);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);
        Assert.Equal(PlayerbotGroupRole.Damage, group.Find(mage.Id)!.Role);

        // The group is an ordinary server group, made through the client's packets: the leader invited, each member accepted.
        await world.OnWorldAsync(() =>
        {
            Group server = world.GroupManager.GetGroup(world.Player(tank.Id).Guid)!;
            Assert.True(server.IsLeader(world.Player(tank.Id).Guid));
            Assert.Equal(3, server.MemberCount);
            Assert.Equal(LootMethod.RoundRobin, server.LootMethod);
            return true;
        });
        Assert.Equal(2, sent.Count(s => s.Bot == tank.Name && s.Opcode == WorldOpcode.CmsgGroupInvite));
        Assert.Contains(sent, s => s.Bot == healer.Name && s.Opcode == WorldOpcode.CmsgGroupAccept);
        Assert.Contains(sent, s => s.Bot == mage.Name && s.Opcode == WorldOpcode.CmsgGroupAccept);
        Assert.Contains(sent, s => s.Bot == tank.Name && s.Opcode == WorldOpcode.CmsgLootMethod);
        Assert.Contains("group=", (await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.Name == tank.Name))).Group);

        // Together they kill the ogre: every member gets the credit, the group disbands and the brains take over again.
        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        await world.OnWorldAsync(() =>
        {
            foreach ((Guid id, string _) in new[] { tank, healer, mage })
            {
                Player player = world.Player(id);
                Assert.Null(world.GroupManager.GetGroup(player.Guid));
                Assert.False(world.Coordinator.Drives(id));
                Assert.Equal(QuestStatus.Complete, world.World.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!.Quests.Get(EliteQuest)!.Status);
            }

            return true;
        });
        Assert.Contains(sent, s => s.Bot == healer.Name && s.Opcode == WorldOpcode.CmsgGroupDisband);
    }

    [Fact]
    public async Task AQuestObjective_TheRiskEstimatePassesOverAlone_IsDoneByTwoBots()
    {
        // Tolerance 0.25: the brute (150 health, 0.5 damage a second) is too much for one level 1 bot by the estimate, fine for two.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        var warrior = await world.AddBotAsync("Riskwar", Human, Warrior, 1, []);
        var mage = await world.AddBotAsync("Riskmage", Human, Mage, 1, [Fireball]);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal("risk", group.Goal.Source);
        Assert.Equal(BruteEntry, group.Goal.ObjectiveEntry);
        Assert.Equal(2, group.Goal.Size);

        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.ObjectiveDone(world.Player(warrior.Id), BruteEntry)
            && world.Coordinator.ObjectiveDone(world.Player(mage.Id), BruteEntry)));
    }

    [Fact]
    public async Task TheHealer_HealsAWoundedPartyMember_InTheFight()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Healtank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Healprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Healmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), world.Trace());

        (uint wounded, uint manaBefore) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(tank.Id);
            player.Health = player.MaxHealth * 30 / 100;
            return (player.Health, SpellSystem.GetPower(world.Player(healer.Id), PowerType.Mana));
        });

        Assert.True(await world.RunUntilAsync(15_000, () => world.Player(tank.Id).Health >= wounded + 40), world.Trace());
        Assert.True(await world.OnWorldAsync(() => SpellSystem.GetPower(world.Player(healer.Id), PowerType.Mana) < manaBefore), "the priest spent no mana");
    }

    [Fact]
    public async Task TheTank_TauntsACreatureThatTurnedOnTheHealer()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Taunttank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Tauntprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Tauntmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), world.Trace());

        Creature ogre = await world.OnWorldAsync(() =>
        {
            var target = (Creature)world.Player(tank.Id).Combat.Victim!;
            target.Combat.Threat.AddThreat(world.Player(healer.Id), 100_000f); // the ogre turns on the priest
            return target;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => ReferenceEquals(ogre.Combat.Victim, world.Player(healer.Id))), world.Trace());

        // The tank answers with its Taunt (the class rotation's NeedsTaunt for the group's tank) and gets the ogre back.
        Assert.True(await world.RunUntilAsync(10_000, () => world.World.Services.GetRequiredService<SpellFeature>().System
            .GetActiveCooldowns(world.Player(tank.Id)).Any(c => c.SpellId == Taunt)), world.Trace());
        Assert.True(await world.RunUntilAsync(5_000, () => ReferenceEquals(ogre.Combat.Victim, world.Player(tank.Id)) || !ogre.IsAlive), world.Trace());
    }

    [Fact]
    public async Task ADungeonQuest_TheGroupEntersThroughTheTriggerIntoOneInstanceBoundToIt_KillsTheBoss_AndWalksOut()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true);
        var tank = await world.AddBotAsync("Delvetank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Delveprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Delvemage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, group.Goal.Kind);
        Assert.Equal(36u, group.Goal.MapId);
        Assert.Equal(World.Playerbots.Scenarios.DungeonEntryScenario.EntranceTrigger, group.Goal.EntranceTrigger);

        // Every member walks into the entrance (no scripted CMSG_AREATRIGGER): one instance, bound to the group.
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == 36)), world.Trace());
        await world.OnWorldAsync(() =>
        {
            uint instance = world.Player(tank.Id).Map!.InstanceId;
            Assert.NotEqual(0u, instance);
            Assert.All(bots, b => Assert.Equal(instance, world.Player(b.Id).Map!.InstanceId));
            Group server = world.GroupManager.GetGroup(world.Player(tank.Id).Guid)!;
            Assert.Equal(instance, world.World.Services.GetRequiredService<World.Instances.InstanceFeature>().Instances.GetGroupBind(server, 36)?.Save.InstanceId);
            return true;
        });

        // The boss falls, the group walks out through the exit trigger and disbands outside.
        Assert.True(await world.RunUntilAsync(360_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        await world.OnWorldAsync(() =>
        {
            Assert.All(bots, b => Assert.Equal(0u, world.Player(b.Id).MapId));
            Assert.All(bots, b => Assert.True(world.Coordinator.ObjectiveDone(world.Player(b.Id), BossEntry)));
            Assert.All(bots, b => Assert.Null(world.GroupManager.GetGroup(world.Player(b.Id).Guid)));
            return true;
        });
    }

    [Fact]
    public async Task AWipe_TheSurvivorRetreats_ResurrectsTheDead_AndTheGroupRegroups()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        var tank = await world.AddBotAsync("Wipetank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Wipeprie", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), world.Trace());
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
        Assert.True(await world.RunUntilAsync(5_000, () => group.State == PlayerbotGroupState.Wiped), world.Trace());
        Assert.True(await world.RunUntilAsync(5_000, () => world.Coordinator.FindAI(healer.Id)!.Retreat.Active), world.Trace());

        // The ogre goes away for a while; the priest comes back and resurrects the tank (its party intake accepts a group member's
        // offer), and the group gathers again, one failure down.
        await world.OnWorldAsync(() =>
        {
            ogre.Map!.FindUpdater<CreatureMapSystem>()!.Despawn(ogre);
            return true;
        });
        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(tank.Id).IsAlive), world.Trace());
        Assert.Equal(tank.Name, world.Coordinator.FindAI(healer.Id)!.LastResurrection);
        Assert.True(await world.RunUntilAsync(60_000, () => group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling
            or PlayerbotGroupState.Engaging), world.Trace());
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

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), world.Trace());
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

        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == lone.Id)), world.Trace());
        Assert.True(await world.OnWorldAsync(() => world.Bots.FindBrain(lone.Id)!.GroupHeld.Contains((EliteQuest, OgreEntry))));
        Assert.Contains("group=waiting", (await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == lone.Id))).Group);

        Assert.True(await world.RunUntilAsync(40_000, () => world.Coordinator.IsSetAside(lone.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry))), world.Trace());
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == lone.Id);
        Assert.Empty(await world.OnWorldAsync(() => world.Bots.FindBrain(lone.Id)!.GroupHeld.ToArray()));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("found no partners", StringComparison.Ordinal));
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        // Set aside, it is not waited for again within the set-aside time.
        await world.RunAsync(10_000);
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == lone.Id);
    }
}
