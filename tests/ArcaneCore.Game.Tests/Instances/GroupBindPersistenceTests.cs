using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Instances;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Review finding 89: a permanent group bind is stored (vmangos <c>group_instance</c>: leader_guid, instance, permanent; written by
/// Group::BindToInstance, Group.cpp:2231-2250, removed by Group::UnbindInstance, :2264-2276, the leader change, :1710-1757, and with
/// the instance, MapPersistentStateMgr.cpp:756) and copied onto every member who enters the instance (vmangos DungeonMap::Add: "if the
/// group/leader is permanently bound to the instance, players also become permanently bound when they enter"). The groups themselves
/// are not stored yet, so a stored bind is given back to the next group its leader leads (vmangos ObjectMgr::LoadGroups attaches the
/// rows to the reloaded group of that leader, ObjectMgr.cpp:5463-5513).
/// </summary>
public sealed class GroupBindPersistenceTests
{
    private static InstanceBind? Bind(InstanceFixture f, Player player) => f.Manager.GetPlayerBind(player.Guid, Raid);

    [Fact]
    public void AKillThatLocksTheLeader_StoresThePermanentGroupBind_AndAMemberWhoWasOutsideIsLockedOnEntry()
    {
        using var f = new InstanceFixture();
        Player leader = f.AddPlayer(1), member = f.AddPlayer(2);
        f.RaidGroup(leader, member);
        Assert.True(f.EnterRaid(leader));
        Map raid = leader.Map!;
        uint id = raid.InstanceId;
        Assert.Empty(f.Persistence.GroupBinds); // a temporary group bind is not stored

        f.Manager.PermBindAllPlayers(raid, leader);

        Assert.Equal([(1u, id, true)], f.Persistence.GroupBinds);
        Assert.Null(Bind(f, member));
        Assert.True(f.EnterRaid(member));
        Assert.Same(raid, member.Map);
        Assert.Equal((id, true), (Bind(f, member)!.Value.Save.InstanceId, Bind(f, member)!.Value.Permanent));
    }

    [Fact]
    public void AfterARestart_TheStoredBindLocksTheLeadersNextGroup_AndItsMembersFollowIntoTheInstance()
    {
        using var f = new InstanceFixture(load: false);
        f.Manager.Load(new InstanceStoreSnapshot([new InstanceRecord(150, Raid, 0)], [], [], [])
        {
            GroupBinds = [new GroupInstanceBindRecord(1, 150, Permanent: true)],
        });
        Assert.True(f.Manager.IsSaveLive(Raid, 150)); // kept by the stored group bind alone

        Player leader = f.AddPlayer(1), member = f.AddPlayer(2);
        Group group = f.RaidGroup(leader, member);

        InstanceBind groupBind = Assert.IsType<InstanceBind>(f.Manager.GetGroupBind(group, Raid));
        Assert.Equal((150u, true), (groupBind.Save.InstanceId, groupBind.Permanent));
        Assert.True(f.EnterRaid(member));
        Assert.Equal(150u, member.Map!.InstanceId);
        Assert.Equal((150u, true), (Bind(f, member)!.Value.Save.InstanceId, Bind(f, member)!.Value.Permanent));
    }

    [Fact]
    public void AStoredBindIsNotGivenToAGroupLedBySomebodyElse()
    {
        using var f = new InstanceFixture(load: false);
        f.Manager.Load(new InstanceStoreSnapshot([new InstanceRecord(150, Raid, 0)], [], [], [])
        {
            GroupBinds = [new GroupInstanceBindRecord(1, 150, Permanent: true)],
        });
        Player other = f.AddPlayer(3), member = f.AddPlayer(2);

        Group group = f.RaidGroup(other, member);

        Assert.Null(f.Manager.GetGroupBind(group, Raid));
        Assert.True(f.EnterRaid(member));
        Assert.NotEqual(150u, member.Map!.InstanceId);
    }

    [Fact]
    public void AStoredBindOfAMissingInstance_IsDroppedAtLoad()
    {
        using var f = new InstanceFixture(load: false);
        f.Persistence.GroupBound(1, 999, permanent: true);

        f.Manager.Load(new InstanceStoreSnapshot([], [], [], []) { GroupBinds = [new GroupInstanceBindRecord(1, 999, Permanent: true)] });

        Assert.Empty(f.Persistence.GroupBinds);
    }

    [Fact]
    public void TheLeaderChange_DropsTheStoredBindOfTheOldLeader_AndTheNewLeadersLockBecomesTheGroups()
    {
        using var f = new InstanceFixture();
        Player leader = f.AddPlayer(1), member = f.AddPlayer(2);
        Group group = f.RaidGroup(leader, member);
        Assert.True(f.EnterRaid(leader));
        Assert.True(f.EnterRaid(member));
        uint id = leader.Map!.InstanceId;
        f.Manager.PermBindAllPlayers(leader.Map!, leader);
        Assert.Equal([(1u, id, true)], f.Persistence.GroupBinds);

        f.Social.Groups.SetLeader(leader, member.Guid);

        // vmangos Group::ChangeLeader: the permanent binds go, then the new leader's permanent binds become the group's.
        Assert.Equal([(2u, id, true)], f.Persistence.GroupBinds);
        Assert.True(f.Manager.GetGroupBind(group, Raid)!.Value.Permanent);
    }

    [Fact]
    public void Disbanding_DeletesTheStoredGroupBinds()
    {
        using var f = new InstanceFixture();
        Player leader = f.AddPlayer(1), member = f.AddPlayer(2);
        f.RaidGroup(leader, member);
        Assert.True(f.EnterRaid(leader));
        f.Manager.PermBindAllPlayers(leader.Map!, leader);
        Assert.NotEmpty(f.Persistence.GroupBinds);

        f.Social.Groups.Leave(member); // two members: the group disbands

        Assert.Null(f.Social.Groups.GetGroup(leader.Guid));
        Assert.Empty(f.Persistence.GroupBinds);
        Assert.True(Bind(f, leader)!.Value.Permanent); // the leader's own lock stays
    }
}
