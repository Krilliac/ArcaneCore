using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>The battleground raid and the parked original group (vmangos AddOrSetPlayerToCorrectBgGroup, m_originalGroup).</summary>
public sealed class GroupBattlegroundRaidTests
{
    [Fact]
    public void FirstParticipant_CreatesAndLeadsTheRaid_TeammatesJoinIt()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        GroupManager groups = f.Context.Groups;

        Group raid = groups.AddToBattlegroundRaid(a, null)!;
        Assert.Same(raid, groups.AddToBattlegroundRaid(b, raid));

        Assert.True(raid.IsBattlegroundGroup);
        Assert.True(raid.IsRaid);
        Assert.Equal(a.Guid, raid.LeaderGuid);
        Assert.Equal([a.Guid, b.Guid], raid.Members.Select(m => m.Guid));
        Assert.Empty(groups.Groups); // never stored
    }

    [Fact]
    public void OriginalGroup_IsParked_StillStored_AndRestoredOnLeave()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        GroupManager groups = f.Context.Groups;
        groups.Invite(a, b.Name);
        groups.Accept(b);
        Group party = groups.GetGroup(a.Guid)!;

        Group raid = groups.AddToBattlegroundRaid(a, null)!;
        Assert.Same(raid, groups.GetGroup(a.Guid));
        Assert.Same(party, groups.GetOriginalGroup(a.Guid));
        Assert.True(party.IsMember(a.Guid));
        Assert.Equal([party], groups.Groups);

        // The party's updates skip the member who is in the battleground raid.
        f.ClearAll();
        groups.SendUpdate(party);
        Assert.Empty(f.Sent(a, ArcaneCore.Protocol.WorldOpcode.SmsgGroupList));

        groups.RemoveFromBattlegroundRaid(a.Guid);
        Assert.Same(party, groups.GetGroup(a.Guid));
        Assert.Null(groups.GetOriginalGroup(a.Guid));
        Assert.Equal(0, raid.MemberCount);
    }

    [Fact]
    public void BattlegroundRaid_CannotBeLeft_AndPassesLeadOnRemoval()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        GroupManager groups = f.Context.Groups;
        Group raid = groups.AddToBattlegroundRaid(a, null)!;
        groups.AddToBattlegroundRaid(b, raid);

        groups.Leave(b);
        Assert.Same(raid, groups.GetGroup(b.Guid));

        groups.RemoveFromBattlegroundRaid(a.Guid);
        Assert.Null(groups.GetGroup(a.Guid));
        Assert.Equal(b.Guid, raid.LeaderGuid);
        Assert.Equal(1, raid.MemberCount); // no disband below two
    }

    [Fact]
    public void PartyDisbandedWhileParked_LeavesThePlayerUngroupedAfterward()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        GroupManager groups = f.Context.Groups;
        groups.Invite(a, b.Name);
        groups.Accept(b);
        groups.AddToBattlegroundRaid(a, null);

        groups.Leave(b); // two members: the party disbands
        Assert.Null(groups.GetOriginalGroup(a.Guid));
        Assert.True(groups.GetGroup(a.Guid)!.IsBattlegroundGroup);

        groups.RemoveFromBattlegroundRaid(a.Guid);
        Assert.Null(groups.GetGroup(a.Guid));
        Assert.Empty(groups.Groups);
    }

    [Fact]
    public void Raid_UsesGroupLoot_LikeVmangosGroupCreate()
    {
        using var f = new SocialFixture();
        Group raid = f.Context.Groups.AddToBattlegroundRaid(f.AddPlayer(1), null)!;
        Assert.Equal(LootMethod.GroupLoot, raid.LootMethod);
        Assert.Equal(Group.DefaultLootThreshold, raid.LootThreshold);
    }

    [Fact]
    public void PartyLeader_TakesTheRaidLead_WhenJoining()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        GroupManager groups = f.Context.Groups;
        groups.Invite(b, c.Name);
        groups.Accept(c);
        Group raid = groups.AddToBattlegroundRaid(a, null)!;

        groups.AddToBattlegroundRaid(c, raid);
        Assert.Equal(a.Guid, raid.LeaderGuid);
        groups.AddToBattlegroundRaid(b, raid);
        Assert.Equal(b.Guid, raid.LeaderGuid);
    }
}
