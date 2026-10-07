using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Groups brought back from storage at start (vmangos ObjectMgr::LoadGroups, Group::LoadGroupFromDB / LoadMemberFromDB,
/// ObjectMgr.cpp:5360-5460) and the snapshot the persistence feature writes (Group::SaveToDB and the group_member rows).
/// </summary>
public sealed class GroupRestoreTests
{
    private static readonly ulong[] NoIcons = new ulong[8];

    private static GroupRecord Party(uint id, int leader, params int[] members)
        => new(id, leader, (byte)LootMethod.GroupLoot, leader, Group.DefaultLootThreshold, false, NoIcons,
            [.. members.Select(m => new GroupMemberRecord(m, 0, false))]);

    [Fact]
    public void AStoredParty_IsRestored_WithItsLeaderLootAndIcons_AndASnapshotOfItIsTheSameRecord()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        f.AddOffline(2);
        f.AddOffline(3);
        ulong[] icons = [0, 0, 0xF130_0000_0000_0042, 0, 0, 0, 0, 0];
        var stored = new GroupRecord(12, 2, (byte)LootMethod.MasterLoot, 3, 4, false, icons,
            [new GroupMemberRecord(1, 0, false), new GroupMemberRecord(2, 0, false), new GroupMemberRecord(3, 0, false)]);

        GroupRestoreResult result = f.Context.Groups.Restore([stored]);

        Assert.Equal([12u], result.Restored);
        Assert.Empty(result.Dropped);
        Group group = f.Context.Groups.GetGroup(ObjectGuid.Player(1))!;
        Assert.Same(group, f.Context.Groups.GetGroup(ObjectGuid.Player(3)));
        Assert.True(group.IsCreated);
        Assert.Equal((12u, ObjectGuid.Player(2), "P2"), (group.Id, group.LeaderGuid, group.LeaderName));
        Assert.Equal((LootMethod.MasterLoot, ObjectGuid.Player(3), (byte)4), (group.LootMethod, group.LooterGuid, group.LootThreshold));
        Assert.Equal(new ObjectGuid(0xF130_0000_0000_0042), group.TargetIcons[2]);
        Assert.Equal(["P1", "P2", "P3"], group.Members.Select(m => m.Name));
        Assert.True(stored.SameAs(GroupManager.Snapshot(group)));
    }

    [Fact]
    public void ARaid_KeepsItsSubgroupsAndAssistants()
    {
        using var f = new SocialFixture();
        for (uint i = 1; i <= 7; i++)
        {
            f.AddOffline(i);
        }

        GroupMemberRecord[] members = [.. Enumerable.Range(1, 7).Select(i => new GroupMemberRecord(i, (byte)(i <= 5 ? 0 : 3), i == 6))];
        f.Context.Groups.Restore([new GroupRecord(4, 1, 3, 1, 2, true, NoIcons, members)]);

        Group raid = f.Context.Groups.GetGroup(ObjectGuid.Player(6))!;
        Assert.True(raid.IsRaid);
        Assert.Equal(5, raid.SubGroupCount(0));
        Assert.Equal(2, raid.SubGroupCount(3));
        Assert.True(raid.IsAssistant(ObjectGuid.Player(6)));
        Assert.False(raid.IsAssistant(ObjectGuid.Player(7)));
    }

    [Fact]
    public void MissingCharacters_AreSkipped_AGroupLeftWithOneMemberOrWithoutItsLeaderIsDropped()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        f.AddOffline(2);
        f.AddOffline(3);

        GroupRestoreResult result = f.Context.Groups.Restore(
        [
            Party(1, 1, 1, 2, 99),   // 99 was deleted: skipped, the group stays with two
            Party(2, 3, 3, 98),      // 98 was deleted: one member left, dropped (vmangos: fewer than two disbands)
            Party(3, 97, 97, 1, 2),  // its leader 97 was deleted: dropped (vmangos: "group leader not exist")
        ]);

        Assert.Equal([1u], result.Restored);
        Assert.Equal([2u, 3u], result.Dropped);
        Assert.Equal([ObjectGuid.Player(1), ObjectGuid.Player(2)], f.Context.Groups.GetGroup(ObjectGuid.Player(1))!.Members.Select(m => m.Guid));
        Assert.Null(f.Context.Groups.GetGroup(ObjectGuid.Player(3)));
    }

    [Fact]
    public void NewGroups_TakeIdsAboveTheHighestStoredOne()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        f.AddOffline(2);
        Player a = f.AddPlayer(5);
        Player b = f.AddPlayer(6);
        f.Context.Groups.Restore([Party(40, 1, 1, 2)]);

        f.Context.Groups.Invite(a, b.Name);
        f.Context.Groups.Accept(b);

        Assert.Equal(41u, f.Context.Groups.GetGroup(a.Guid)!.Id);
    }

    [Fact]
    public void ARestoredMember_LoggingIn_GetsTheGroupList_AndTheLeaderFlag()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        f.AddOffline(2);
        Assert.Equal([7u], f.Context.Groups.Restore([Party(7, 1, 1, 2)]).Restored);
        Player leader = f.AddPlayer(1);   // the leader comes back online after the restart
        f.ClearAll();

        f.Context.Groups.OnLoggedIn(leader);

        var list = new PacketReader(f.Single(leader, WorldOpcode.SmsgGroupList));
        Assert.Equal((byte)GroupType.Normal, list.ReadByte());
        list.ReadByte();
        Assert.Equal(1u, list.ReadUInt32());   // one other member
        Assert.Equal("P2", list.ReadCString());
        Assert.True((leader.Flags & PlayerFlags.GroupLeader) != 0);
    }

    [Fact]
    public void AStoredLeaderWhoIsNotAMember_AndAForeignLooter_FallBackToTheFirstMember()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        f.AddOffline(2);
        f.AddOffline(3);
        f.Context.Groups.Restore([new GroupRecord(9, 3, (byte)LootMethod.MasterLoot, 3, 9, false, NoIcons,
            [new GroupMemberRecord(1, 0, false), new GroupMemberRecord(2, 0, false)])]);

        Group group = f.Context.Groups.GetGroup(ObjectGuid.Player(1))!;
        Assert.Equal(ObjectGuid.Player(1), group.LeaderGuid);
        Assert.Equal(ObjectGuid.Player(1), group.LooterGuid);
        Assert.Equal(Group.DefaultLootThreshold, group.LootThreshold); // 9 is above ITEM_QUALITY_ARTIFACT
    }
}
