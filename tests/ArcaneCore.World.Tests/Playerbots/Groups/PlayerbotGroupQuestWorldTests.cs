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
/// Quest objectives, healing, taunting, dungeon quests and shared quests. Split from the former <c>PlayerbotGroupWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupQuestWorldTests : PlayerbotGroupWorldTestBase
{
    [Fact]
    public async Task AQuestObjective_TheRiskEstimatePassesOverAlone_IsDoneByTwoBots()
    {
        // Tolerance 0.25: the brute (150 health, 0.5 damage a second) is too much for one level 1 bot by the estimate, fine for two.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        var warrior = await world.AddBotAsync("Riskwar", Human, Warrior, 1, []);
        var mage = await world.AddBotAsync("Riskmage", Human, Mage, 1, [Fireball]);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal("risk", group.Goal.Source);
        Assert.Equal(BruteEntry, group.Goal.ObjectiveEntry);
        Assert.Equal(2, group.Goal.Size);

        Assert.True(await world.RunUntilAsync(300_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), await world.TraceAsync());

        (uint wounded, uint manaBefore) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(tank.Id);
            player.Health = player.MaxHealth * 30 / 100;
            return (player.Health, SpellSystem.GetPower(world.Player(healer.Id), PowerType.Mana));
        });

        Assert.True(await world.RunUntilAsync(15_000, () => world.Player(tank.Id).Health >= wounded + 40), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => SpellSystem.GetPower(world.Player(healer.Id), PowerType.Mana) < manaBefore), "the priest spent no mana");
    }

    [Fact]
    public async Task TheTank_TauntsACreatureThatTurnedOnTheHealer()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var tank = await world.AddBotAsync("Taunttank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Tauntprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Tauntmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Player(tank.Id).Combat.Victim is Creature { Entry: OgreEntry }), await world.TraceAsync());

        Creature ogre = await world.OnWorldAsync(() =>
        {
            var target = (Creature)world.Player(tank.Id).Combat.Victim!;
            target.Combat.Threat.AddThreat(world.Player(healer.Id), 100_000f); // the ogre turns on the priest
            return target;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => ReferenceEquals(ogre.Combat.Victim, world.Player(healer.Id))), await world.TraceAsync());

        // The tank answers with its Taunt (the class rotation's NeedsTaunt for the group's tank) and gets the ogre back.
        Assert.True(await world.RunUntilAsync(10_000, () => world.World.Services.GetRequiredService<SpellFeature>().System
            .GetActiveCooldowns(world.Player(tank.Id)).Any(c => c.SpellId == Taunt)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(5_000, () => ReferenceEquals(ogre.Combat.Victim, world.Player(tank.Id)) || !ogre.IsAlive), await world.TraceAsync());
    }

    [Fact]
    public async Task ADungeonQuest_TheGroupEntersThroughTheTriggerIntoOneInstanceBoundToIt_KillsTheBoss_AndWalksOut()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], dungeon: true);
        var tank = await world.AddBotAsync("Delvetank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Delveprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Delvemage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, group.Goal.Kind);
        Assert.Equal(36u, group.Goal.MapId);
        Assert.Equal(World.Playerbots.Scenarios.DungeonEntryScenario.EntranceTrigger, group.Goal.EntranceTrigger);

        // Every member walks into the entrance (no scripted CMSG_AREATRIGGER): one instance, bound to the group.
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == 36)), await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(360_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        await world.OnWorldAsync(() =>
        {
            Assert.All(bots, b => Assert.Equal(0u, world.Player(b.Id).MapId));
            Assert.All(bots, b => Assert.True(world.Coordinator.ObjectiveDone(world.Player(b.Id), BossEntry)));
            Assert.All(bots, b => Assert.Null(world.GroupManager.GetGroup(world.Player(b.Id).Guid)));
            return true;
        });
    }

    /// <summary>
    /// A dungeon quest that asks only for an item (the boss's trophy, a quest drop through a reference loot table) is group content too:
    /// the coordinator maps the item to the creature that drops it inside The Deadmines, the group goes in, kills the boss, each member
    /// loots its own copy of the quest drop (whoever holds the corpse), and the group walks out once everyone has it. Before, only
    /// creature objectives made a goal, and the bots never grouped for it.
    /// </summary>
    [Fact]
    public async Task ADungeonQuestForAnItemOnly_IsGroupContent_AtTheCreatureThatDropsIt_AndEveryMemberLootsIt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonItemQuest], dungeon: true);
        var tank = await world.AddBotAsync("Trophytank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Trophyprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Trophymage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, group.Goal.Kind);
        Assert.Equal((36u, BossEntry, BossTrophy, DungeonItemQuest), (group.Goal.MapId, group.Goal.ObjectiveEntry, group.Goal.ObjectiveItem, group.Goal.QuestId));

        Assert.True(await world.RunUntilAsync(480_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Equal(1, world.Coordinator.Totals.Completed);
        await world.OnWorldAsync(() =>
        {
            Assert.All(bots, b => Assert.Equal(0u, world.Player(b.Id).MapId));
            Assert.All(bots, b => Assert.Equal(1u, world.Player(b.Id).Inventory.GetItemCount(BossTrophy)));
            return true;
        });
    }

    /// <summary>
    /// The quest drop is looted even when the members' loop watch has just given up the place the boss died on. A healer walking
    /// between the leader and the fight, then to the corpse, makes the A, B, A pattern PlayerbotMotion gives up, and the places it
    /// refuses for a minute include the corpse: the walk to it was refused on three thinks in a row, the member's three loot tries
    /// were spent, it never looted, and the group gave up as objective-missing (the flaky CI failure of the test above, in about one
    /// CI lane in forty). Here the give-up is made at the kill for both followers, so it does not depend on how the fight moved them.
    /// </summary>
    [Fact]
    public async Task ADungeonQuestDrop_IsLooted_WhenALoopGaveUpTheCorpseSpot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonItemQuest], dungeon: true);
        var tank = await world.AddBotAsync("Spottank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Spotprie", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mage = await world.AddBotAsync("Spotmage", Human, Mage, 12, [Fireball], EntranceArea);
        (Guid Id, string Name)[] bots = [tank, healer, mage];
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());

        bool gaveUp = false;
        Assert.True(await world.RunUntilAsync(480_000, () =>
        {
            if (!gaveUp && world.Player(tank.Id).Map is { } map)
            {
                foreach (ObjectGuid guid in world.Player(tank.Id).VisibleObjects)
                {
                    if (map.FindObject(guid) is not Creature { Entry: BossEntry, IsAlive: false } corpse) continue;
                    var at = new System.Numerics.Vector3(corpse.X, corpse.Y, corpse.Z);
                    foreach ((Guid id, _) in new[] { healer, mage })
                    {
                        PlayerbotMotion.Blacklist(world.Player(id), [at], world.World.Host.World.NowMs);
                    }

                    gaveUp = true;
                }
            }

            return world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());
        Assert.True(gaveUp, "the boss was never seen dead");
        Assert.True(world.Coordinator.Totals.Completed == 1, await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            Assert.All(bots, b => Assert.Equal(1u, world.Player(b.Id).Inventory.GetItemCount(BossTrophy)));
            return true;
        });
    }

    /// <summary>
    /// Grouped for a goal, the members share its quest the way players do: one member dropped the elite quest after the group formed; a
    /// member who has it pushes it to the party once everyone is within the share distance (CMSG_PUSHQUESTTOPARTY, vmangos
    /// HandlePushQuestToParty), the member without it accepts it from the sharer (CMSG_QUESTGIVER_ACCEPT_QUEST with the sharer's guid),
    /// and is credited with the kill like the others.
    /// </summary>
    [Fact]
    public async Task GroupedForAGoal_AMemberWithoutTheQuest_IsSharedIt_AndCredited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var sent = new ConcurrentQueue<(string Bot, WorldOpcode Opcode)>();
        var tank = await world.AddBotAsync("Sharetank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Shareheal", Human, Priest, 10, [LesserHeal, Smite, Resurrection]);
        var mage = await world.AddBotAsync("Sharemage", Human, Mage, 10, [Fireball]);
        foreach ((Guid id, string name) in new[] { tank, healer, mage })
            await world.OnWorldAsync(() => world.Session(id).ManagedDispatchObserver = opcode => sent.Enqueue((name, opcode)));
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            Player player = world.Player(mage.Id);
            ArcaneCore.Game.Npc.QuestNpcServices quests = world.World.Services.GetRequiredService<QuestNpcFeature>().Services;
            int slot = quests.StateOf(player)!.Quests.FindSlot(EliteQuest);
            Assert.True(quests.AbandonQuest(player, (byte)slot));
            // Checked in the same world callback: the world thread handles the bots' packets between callbacks even on the manual clock,
            // and a member standing by may already have pushed the quest again and the mage accepted it.
            Assert.Equal(QuestStatus.None, quests.StateOf(player)!.Quests.GetStatus(EliteQuest));
            return true;
        });

        Assert.True(await world.RunUntilAsync(60_000, () => world.World.Services.GetRequiredService<QuestNpcFeature>().Services
            .StateOf(world.Player(mage.Id))!.Quests.GetStatus(EliteQuest) == QuestStatus.Incomplete), await world.TraceAsync());
        Assert.Contains(sent, s => s.Opcode == WorldOpcode.CmsgPushquesttoparty && s.Bot != mage.Name);
        Assert.Contains(sent, s => s.Opcode == WorldOpcode.CmsgQuestgiverAcceptQuest && s.Bot == mage.Name);
        Assert.Contains(world.Coordinator.Events, e => e.Contains($"shares quest {EliteQuest} with {mage.Name}", StringComparison.Ordinal));

        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            Assert.True(world.Coordinator.ObjectiveDone(world.Player(mage.Id), OgreEntry));
            Assert.Equal(QuestStatus.Complete, world.World.Services.GetRequiredService<QuestNpcFeature>().Services
                .StateOf(world.Player(mage.Id))!.Quests.GetStatus(EliteQuest));
            return true;
        });
    }
}
