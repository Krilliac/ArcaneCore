using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
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
}
