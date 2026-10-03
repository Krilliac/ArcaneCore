using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>Parties and raids (vmangos GroupHandler / Group rules).</summary>
public sealed class GroupManagerTests
{
    private static (uint Op, string Name, uint Result) ReadResult(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), reader.ReadCString(), reader.ReadUInt32());
    }

    private static Group MakeParty(SocialFixture f, Player leader, params Player[] members)
    {
        foreach (Player member in members)
        {
            f.Context.Groups.Invite(leader, member.Name);
            f.Context.Groups.Accept(member);
        }

        return f.Context.Groups.GetGroup(leader.Guid)!;
    }

    [Fact]
    public void InviteAndAccept_CreatesTheGroup_LeaderFirst()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.ClearAll();

        f.Context.Groups.Invite(a, "P2");

        var invite = new PacketReader(f.Single(b, WorldOpcode.SmsgGroupInvite));
        Assert.Equal("P1", invite.ReadCString());
        Assert.Equal((0u, "P2", (uint)PartyResult.Ok), ReadResult(f.Single(a, WorldOpcode.SmsgPartyCommandResult)));
        Assert.Null(f.Context.Groups.GetGroup(a.Guid));

        f.Context.Groups.Accept(b);

        Group group = f.Context.Groups.GetGroup(a.Guid)!;
        Assert.Same(group, f.Context.Groups.GetGroup(b.Guid));
        Assert.Equal([a.Guid, b.Guid], group.Members.Select(m => m.Guid));
        Assert.Equal(a.Guid, group.LeaderGuid);
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);
        Assert.True((a.Flags & PlayerFlags.GroupLeader) != 0);
        Assert.False((b.Flags & PlayerFlags.GroupLeader) != 0);
        Assert.Empty(group.Invitees);
        Assert.NotEmpty(f.Sent(b, WorldOpcode.SmsgGroupList));
    }

    [Fact]
    public void GroupList_Layout_FollowsVmangos()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.SendUpdate(f.Context.Groups.GetGroup(a.Guid)!);

        var reader = new PacketReader(f.Single(b, WorldOpcode.SmsgGroupList));
        Assert.Equal((byte)GroupType.Normal, reader.ReadByte());
        Assert.Equal(0, reader.ReadByte()); // subgroup 0, not assistant
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal("P1", reader.ReadCString());
        Assert.Equal(a.Guid.Value, reader.ReadUInt64());
        Assert.Equal((byte)GroupMemberStatus.Online, reader.ReadByte() & (byte)GroupMemberStatus.Online);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(a.Guid.Value, reader.ReadUInt64());
        Assert.Equal((byte)LootMethod.GroupLoot, reader.ReadByte());
        Assert.Equal(0ul, reader.ReadUInt64());
        Assert.Equal(Group.DefaultLootThreshold, reader.ReadByte());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Invite_Refusals()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player orc = f.AddPlayer(2, Race.Orc, x: 5000);
        Player c = f.AddPlayer(3);
        f.Context.Friends.AddIgnore(c, "P1");
        f.ClearAll();

        f.Context.Groups.Invite(a, "Nobody");
        f.Context.Groups.Invite(a, "P2");
        f.Context.Groups.Invite(a, "P3");

        Assert.Equal(
            [(uint)PartyResult.BadPlayerName, (uint)PartyResult.WrongFaction, (uint)PartyResult.IgnoringYou],
            f.Sent(a, WorldOpcode.SmsgPartyCommandResult).Select(p => ReadResult(p).Result));
        Assert.Empty(f.Sent(orc, WorldOpcode.SmsgGroupInvite));
        Assert.Empty(f.Sent(c, WorldOpcode.SmsgGroupInvite));
    }

    [Fact]
    public void Invite_OppositeFaction_InGmMode_IsAllowed()
    {
        using var f = new SocialFixture();
        Player gm = f.AddPlayer(1, security: AccountSecurity.GameMaster);
        Player orc = f.AddPlayer(2, Race.Orc, x: 5000);
        gm.Flags |= PlayerFlags.Gm; // vmangos IsGameMaster(): GM mode is on
        f.ClearAll();

        f.Context.Groups.Invite(gm, "P2");

        Assert.Single(f.Sent(orc, WorldOpcode.SmsgGroupInvite));
    }

    [Fact]
    public void Invite_AlreadyGrouped_AndNotLeader()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.Invite(c, "P2");
        f.Context.Groups.Invite(b, "P3");

        Assert.Equal((uint)PartyResult.AlreadyInGroup, ReadResult(f.Single(c, WorldOpcode.SmsgPartyCommandResult)).Result);
        Assert.Equal((uint)PartyResult.NotLeader, ReadResult(f.Single(b, WorldOpcode.SmsgPartyCommandResult)).Result);
    }

    [Fact]
    public void Decline_TellsTheLeader_AndDropsTheUncreatedGroup()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Groups.Invite(a, "P2");
        f.ClearAll();

        f.Context.Groups.Decline(b);

        Assert.Equal("P2", new PacketReader(f.Single(a, WorldOpcode.SmsgGroupDecline)).ReadCString());
        Assert.Null(f.Context.Groups.GetInvite(a.Guid));
        Assert.Null(f.Context.Groups.GetInvite(b.Guid));
    }

    [Fact]
    public void Leave_FromTwoMemberGroup_DisbandsIt()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.Leave(b);

        Assert.Null(f.Context.Groups.GetGroup(a.Guid));
        Assert.Null(f.Context.Groups.GetGroup(b.Guid));
        Assert.Equal(14, f.Single(a, WorldOpcode.SmsgGroupList).Length);
        Assert.Equal(14, f.Single(b, WorldOpcode.SmsgGroupList).Length);
        Assert.Equal((2u, "P2", 0u), ReadResult(f.Single(b, WorldOpcode.SmsgPartyCommandResult)));
        Assert.False((a.Flags & PlayerFlags.GroupLeader) != 0);
    }

    [Fact]
    public void LeaderLeaving_HandsLeadershipToAnOnlineMember()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Group group = MakeParty(f, a, b, c);
        f.ClearAll();

        f.Context.Groups.Leave(a);

        Assert.Equal(b.Guid, group.LeaderGuid);
        Assert.Equal("P2", new PacketReader(f.Single(c, WorldOpcode.SmsgGroupSetLeader)).ReadCString());
        Assert.True((b.Flags & PlayerFlags.GroupLeader) != 0);
        Assert.Equal(2, group.MemberCount);
    }

    [Fact]
    public void Uninvite_ByLeader_KicksTheMember()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Group group = MakeParty(f, a, b, c);
        f.ClearAll();

        f.Context.Groups.UninviteByName(b, "P3");
        Assert.Equal((uint)PartyResult.NotLeader, ReadResult(f.Single(b, WorldOpcode.SmsgPartyCommandResult)).Result);

        f.Context.Groups.UninviteByName(a, "P3");
        Assert.Single(f.Sent(c, WorldOpcode.SmsgGroupUninvite));
        Assert.False(group.IsMember(c.Guid));
    }

    [Fact]
    public void Party_IsFullAtFive_UntilConvertedToRaid()
    {
        using var f = new SocialFixture();
        Player leader = f.AddPlayer(1);
        Player[] members = [.. Enumerable.Range(2, 4).Select(i => f.AddPlayer((uint)i))];
        Group group = MakeParty(f, leader, members);
        Player sixth = f.AddPlayer(6);
        f.ClearAll();

        f.Context.Groups.Invite(leader, "P6");
        Assert.Equal((uint)PartyResult.GroupFull, ReadResult(f.Single(leader, WorldOpcode.SmsgPartyCommandResult)).Result);

        f.Context.Groups.ConvertToRaid(leader);
        f.Context.Groups.Invite(leader, "P6");
        f.Context.Groups.Accept(sixth);

        Assert.True(group.IsRaid);
        Assert.Equal(1, group.Find(sixth.Guid)!.SubGroup);
        Assert.Equal(5, group.SubGroupCount(0));
    }

    [Fact]
    public void PartyChat_GoesToTheSpeakersSubgroup_RaidChatToEveryone()
    {
        using var f = new SocialFixture();
        Player leader = f.AddPlayer(1);
        Player[] members = [.. Enumerable.Range(2, 4).Select(i => f.AddPlayer((uint)i))];
        Group group = MakeParty(f, leader, members);
        f.Context.Groups.ConvertToRaid(leader);
        Player sixth = f.AddPlayer(6);
        f.Context.Groups.Invite(leader, "P6");
        f.Context.Groups.Accept(sixth);
        f.ClearAll();

        Assert.True(f.Context.Groups.BroadcastChat(leader, ChatType.Party, [1, 2, 3]));
        Assert.Single(f.Sent(members[0], WorldOpcode.SmsgMessagechat));
        Assert.Empty(f.Sent(sixth, WorldOpcode.SmsgMessagechat));

        Assert.True(f.Context.Groups.BroadcastChat(sixth, ChatType.Raid, [4]));
        Assert.Single(f.Sent(sixth, WorldOpcode.SmsgMessagechat));
        Assert.Equal(2, f.Sent(leader, WorldOpcode.SmsgMessagechat).Count);

        Assert.False(f.Context.Groups.BroadcastChat(sixth, ChatType.RaidWarning, [5]));
        Assert.True(f.Context.Groups.BroadcastChat(leader, ChatType.RaidWarning, [5]));
        Assert.Equal(2, f.Sent(sixth, WorldOpcode.SmsgMessagechat).Count);
        Assert.Equal(6, group.MemberCount);
    }

    [Fact]
    public void RaidChat_InAParty_IsRefused()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        MakeParty(f, a, b);
        f.ClearAll();

        Assert.False(f.Context.Groups.BroadcastChat(a, ChatType.Raid, [1]));
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgMessagechat));
    }

    [Fact]
    public void ReadyCheck_FromLeader_ReachesEveryone_AndAnswersReachTheLeader()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.ReadyCheck(b, null);
        Assert.Empty(f.Sent(a, WorldOpcode.MsgRaidReadyCheck));

        f.Context.Groups.ReadyCheck(a, null);
        Assert.Empty(f.Single(b, WorldOpcode.MsgRaidReadyCheck));

        f.Context.Groups.ReadyCheck(b, 1);
        List<byte[]> toLeader = f.Sent(a, WorldOpcode.MsgRaidReadyCheck);
        var reader = new PacketReader(toLeader[^1]);
        Assert.Equal(b.Guid.Value, reader.ReadUInt64());
        Assert.Equal(1, reader.ReadByte());
    }

    [Fact]
    public void LootMethod_MasterLoot_NeedsAMemberLooter()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Group group = MakeParty(f, a, b);

        f.Context.Groups.SetLootMethod(a, (uint)LootMethod.MasterLoot, ObjectGuid.Player(99), 2);
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);

        f.Context.Groups.SetLootMethod(a, (uint)LootMethod.MasterLoot, b.Guid, 3);
        Assert.Equal(LootMethod.MasterLoot, group.LootMethod);
        Assert.Equal(b.Guid, group.LooterGuid);
        Assert.Equal(3, group.LootThreshold);

        f.Context.Groups.SetLootMethod(b, (uint)LootMethod.FreeForAll, ObjectGuid.Empty, 2);
        Assert.Equal(LootMethod.MasterLoot, group.LootMethod);
    }

    [Fact]
    public void SetLeader_MovesTheLeaderFlag()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Group group = MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.SetLeader(a, b.Guid);

        Assert.Equal(b.Guid, group.LeaderGuid);
        Assert.True((b.Flags & PlayerFlags.GroupLeader) != 0);
        Assert.False((a.Flags & PlayerFlags.GroupLeader) != 0);
        Assert.Equal("P2", new PacketReader(f.Single(a, WorldOpcode.SmsgGroupSetLeader)).ReadCString());
    }

    [Fact]
    public void TargetIcons_MoveBetweenTargets_AndListOnRequest()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Group group = MakeParty(f, a, b);
        var mob = new ObjectGuid(0xF130000000000001);

        f.Context.Groups.TargetIcon(a, 0, mob);
        f.Context.Groups.TargetIcon(a, 3, mob);
        f.ClearAll();
        f.Context.Groups.TargetIcon(b, 0xFF, ObjectGuid.Empty);

        Assert.True(group.TargetIcons[0].IsEmpty);
        Assert.Equal(mob, group.TargetIcons[3]);
        var reader = new PacketReader(f.Single(b, WorldOpcode.MsgRaidTargetUpdate));
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(3, reader.ReadByte());
        Assert.Equal(mob.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void OutOfRangeStats_GoOnlyToMatesThatCannotSeeTheMember()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2, x: 5000, y: 5000);
        MakeParty(f, a, b);
        b.VisibleObjects.Remove(a.Guid);
        a.VisibleObjects.Remove(b.Guid);
        f.Context.Groups.UpdateOutOfRangeStats();
        f.ClearAll();

        f.Context.Groups.UpdateOutOfRangeStats();
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgPartyMemberStats)); // nothing changed

        a.Health = a.Health - 1;
        f.Context.Groups.UpdateOutOfRangeStats();

        var reader = new PacketReader(f.Single(b, WorldOpcode.SmsgPartyMemberStats));
        Assert.Equal(a.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)GroupUpdateFlags.CurrentHp, reader.ReadUInt32());
        Assert.Equal((ushort)a.Health, reader.ReadUInt16());
        Assert.Equal(0, reader.Remaining);

        b.VisibleObjects.Add(a.Guid);
        f.ClearAll();
        a.Health = a.Health - 1;
        f.Context.Groups.UpdateOutOfRangeStats();
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgPartyMemberStats));
    }

    [Fact]
    public void RequestMemberStats_OfAStranger_IsOffline()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.ClearAll();

        f.Context.Groups.RequestMemberStats(a, b.Guid);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgPartyMemberStatsFull));
        Assert.Equal(b.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)GroupUpdateFlags.Status, reader.ReadUInt32());
        Assert.Equal((byte)GroupMemberStatus.Offline, reader.ReadByte());
    }

    [Fact]
    public void LoggingOut_KeepsMembership_AndShowsTheMemberOffline()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Group group = MakeParty(f, a, b);
        f.ClearAll();

        f.Context.Groups.OnLoggingOut(b);

        Assert.True(group.IsMember(b.Guid));
        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgGroupList));
        reader.ReadByte();
        reader.ReadByte();
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal("P2", reader.ReadCString());
        reader.ReadUInt64();
        Assert.Equal((byte)GroupMemberStatus.Offline, reader.ReadByte());
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgGroupList));
    }
}
