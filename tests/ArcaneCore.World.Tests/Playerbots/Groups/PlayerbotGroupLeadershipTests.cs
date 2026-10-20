using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>A bot-led group whose leader died passes the lead to its best living member with CMSG_GROUP_SET_LEADER.</summary>
public sealed class PlayerbotGroupLeadershipTests
{
    private static PlayerbotSuccessorCandidate C(byte level, PlayerbotGroupRole role, bool alive = true) => new(Guid.NewGuid(), role, level, alive);

    [Fact]
    public void ASuccessorIsAlive_AndATankBeatsAHealerBeatsDamage()
    {
        PlayerbotSuccessorCandidate damage = C(60, PlayerbotGroupRole.Damage), healer = C(50, PlayerbotGroupRole.Healer), tank = C(40, PlayerbotGroupRole.Tank);
        Assert.Equal(tank.BotId, PlayerbotGroupLeadership.BestSuccessor([damage, healer, tank]));
        Assert.Equal(healer.BotId, PlayerbotGroupLeadership.BestSuccessor([damage, healer, tank with { Alive = false }]));
        Assert.Equal(damage.BotId, PlayerbotGroupLeadership.BestSuccessor([damage, healer with { Alive = false }, tank with { Alive = false }]));
    }

    [Fact]
    public void WithinARole_TheHigherLevelWins_AndNobodyAliveMeansNoSuccessor()
    {
        PlayerbotSuccessorCandidate low = C(40, PlayerbotGroupRole.Damage), high = C(55, PlayerbotGroupRole.Damage);
        Assert.Equal(high.BotId, PlayerbotGroupLeadership.BestSuccessor([low, high]));
        Assert.Null(PlayerbotGroupLeadership.BestSuccessor([low with { Alive = false }, high with { Alive = false }]));
        Assert.Null(PlayerbotGroupLeadership.BestSuccessor([]));
    }

    [Fact]
    public async Task ADeadLeader_PassesTheLeadToTheLivingTank_WithTheClientPacket()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        var leader = await world.AddBotAsync("Leadold", 1, 1, 10, [Taunt]);
        var tank = await world.AddBotAsync("Leadtank", 1, 1, 10, [Taunt]);
        var mage = await world.AddBotAsync("Leadmage", 1, 8, 10, []);
        await world.RunAsync(2_000); // the coordinator has seen the bots
        var sent = new ConcurrentQueue<WorldOpcode>();
        await world.OnWorldAsync(() =>
        {
            world.Session(leader.Id).ManagedDispatchObserver = opcode => sent.Enqueue(opcode);
            return true;
        });

        PlayerbotGroupCoordinator.BotGroup group = await world.OnWorldAsync(() =>
        {
            Player l = world.Player(leader.Id), t = world.Player(tank.Id), m = world.Player(mage.Id);
            world.GroupManager.Invite(l, t.Name);
            world.GroupManager.Accept(t);
            world.GroupManager.Invite(l, m.Name);
            world.GroupManager.Accept(m);
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, 0, l.MapId, l.MapId, Vector3.Zero, 0, Vector3.Zero, 3, 1, "test");
            var built = new PlayerbotGroupCoordinator.BotGroup(99, goal, leader.Id, l.Guid, world.World.Host.World.NowMs) { State = PlayerbotGroupState.Engaging };
            built.Members.Add(new PlayerbotGroupCoordinator.Member(leader.Id, l.Guid, l.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, false) { Joined = true });
            built.Members.Add(new PlayerbotGroupCoordinator.Member(mage.Id, m.Guid, m.Name, PlayerbotGroupRole.Damage, PlayerbotRole.RangeDps, 0, false) { Joined = true });
            built.Members.Add(new PlayerbotGroupCoordinator.Member(tank.Id, t.Guid, t.Name, PlayerbotGroupRole.Tank, PlayerbotRole.Tank, 0, false) { Joined = true });
            l.Health = 0;
            l.Map!.Combat.KillPlayer(l);
            return built;
        });

        Assert.True(await world.OnWorldAsync(() => world.Coordinator.PromoteLeader(group)));

        await world.OnWorldAsync(() =>
        {
            Assert.Equal(tank.Id, group.LeaderBotId);
            Assert.Equal(world.Player(tank.Id).Guid, group.LeaderGuid);
            Assert.True(world.GroupManager.GetGroup(world.Player(tank.Id).Guid)!.IsLeader(world.Player(tank.Id).Guid));
            return true;
        });
        Assert.Contains(WorldOpcode.CmsgGroupSetLeader, sent);
    }
}
