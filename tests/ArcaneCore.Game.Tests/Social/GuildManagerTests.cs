using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>Guilds (vmangos GuildHandler / Guild rules).</summary>
public sealed class GuildManagerTests
{
    private static Guild Found(SocialFixture f, Player leader, string name = "Arcane")
    {
        if (!f.Context.Guilds.IsLoaded)
        {
            f.Context.Guilds.Load([]);
        }

        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Create(leader.Guid.Low, name, out Guild? guild));
        return guild!;
    }

    private static void Join(SocialFixture f, Player inviter, Player member)
    {
        f.Context.Guilds.Invite(inviter, member.Name);
        f.Context.Guilds.Accept(member);
    }

    /// <summary>The event and its strings joined with '|'.</summary>
    private static (GuildEvent Event, string Strings) ReadEvent(byte[] payload)
    {
        var reader = new PacketReader(payload);
        var guildEvent = (GuildEvent)reader.ReadByte();
        int count = reader.ReadByte();
        var strings = new List<string>();
        for (int i = 0; i < count; i++)
        {
            strings.Add(reader.ReadCString());
        }

        return (guildEvent, string.Join('|', strings));
    }

    private static (uint Command, string Text, uint Error) ReadResult(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), reader.ReadCString(), reader.ReadUInt32());
    }

    [Fact]
    public void Create_WaitsForTheStoredGuilds()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);

        Assert.Equal(GuildAdminResult.NotLoaded, f.Context.Guilds.Create(a.Guid.Low, "Arcane", out _));
    }

    [Fact]
    public void Create_FoundsAGuildWithDefaultRanks_AndSetsTheLeaderFields()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);

        Guild guild = Found(f, a);

        Assert.Equal(["Guild Master", "Officer", "Veteran", "Member", "Initiate"], guild.Ranks.Select(r => r.Name));
        Assert.Equal(Guild.DefaultMotd, guild.Motd);
        Assert.Equal((uint)guild.Id, a.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.Equal(0u, a.GetUInt32(UpdateFields.PlayerGuildrank));
        GuildData saved = f.Persistence.Guilds[guild.Id];
        Assert.Equal("Arcane", saved.Name);
        Assert.Equal(1, saved.LeaderId);
        Assert.Single(saved.Members);
    }

    [Fact]
    public void Create_Refusals()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Found(f, a);

        Assert.Equal(GuildAdminResult.NameExists, f.Context.Guilds.Create(b.Guid.Low, "ARCANE", out _));
        Assert.Equal(GuildAdminResult.AlreadyInGuild, f.Context.Guilds.Create(a.Guid.Low, "Other", out _));
        Assert.Equal(GuildAdminResult.NameInvalid, f.Context.Guilds.Create(b.Guid.Low, new string('x', 25), out _));
        Assert.Equal(GuildAdminResult.CharacterNotFound, f.Context.Guilds.Create(77, "Other", out _));
    }

    [Fact]
    public void InviteAndAccept_JoinsAtTheLowestRank_AndAnnouncesIt()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        f.ClearAll();

        f.Context.Guilds.Invite(a, "P2");

        var invite = new PacketReader(f.Single(b, WorldOpcode.SmsgGuildInvite));
        Assert.Equal("P1", invite.ReadCString());
        Assert.Equal("Arcane", invite.ReadCString());
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgGuildCommandResult)); // vmangos: no reply on success

        f.Context.Guilds.Accept(b);

        Assert.Equal(guild.LowestRank, guild.Find(2)!.Rank);
        Assert.Equal((uint)guild.LowestRank, b.GetUInt32(UpdateFields.PlayerGuildrank));
        Assert.Equal((GuildEvent.Joined, "P2"), ReadEvent(f.Single(a, WorldOpcode.SmsgGuildEvent)));
    }

    [Fact]
    public void Invite_Refusals()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player orc = f.AddPlayer(3, Race.Orc, x: 5000);
        Player c = f.AddPlayer(4);
        Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        f.Context.Guilds.Invite(a, "Nobody");
        f.Context.Guilds.Invite(a, "P3");
        f.Context.Guilds.Invite(a, "P2");
        f.Context.Guilds.Invite(b, "P4"); // initiates may not invite

        Assert.Equal(
            [(uint)GuildCommandError.PlayerNotFoundS, (uint)GuildCommandError.NotAllied, (uint)GuildCommandError.AlreadyInGuildS],
            f.Sent(a, WorldOpcode.SmsgGuildCommandResult).Select(p => ReadResult(p).Error));
        Assert.Equal((uint)GuildCommandError.Permissions, ReadResult(f.Single(b, WorldOpcode.SmsgGuildCommandResult)).Error);
        Assert.Empty(f.Sent(c, WorldOpcode.SmsgGuildInvite));
        Assert.Empty(f.Sent(orc, WorldOpcode.SmsgGuildInvite));
    }

    [Fact]
    public void Decline_TellsTheInviter()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Found(f, a);
        f.Context.Guilds.Invite(a, "P2");
        f.ClearAll();

        f.Context.Guilds.Decline(b);

        Assert.Equal("P2", new PacketReader(f.Single(a, WorldOpcode.SmsgGuildDecline)).ReadCString());
        Assert.Equal(0, f.Context.Guilds.InvitedTo(b));
    }

    [Fact]
    public void PromoteAndDemote_FollowTheRankRules()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Guild guild = Found(f, a);
        Join(f, a, b);
        Join(f, a, c);

        f.Context.Guilds.Promote(a, "P2");
        f.Context.Guilds.Promote(a, "P2");
        f.Context.Guilds.Promote(a, "P2");
        Assert.Equal(Guild.OfficerRank, guild.Find(2)!.Rank);
        f.ClearAll();

        f.Context.Guilds.Promote(a, "P2"); // an officer can't become a second guild master
        Assert.Equal((uint)GuildCommandError.RankTooHighS, ReadResult(f.Single(a, WorldOpcode.SmsgGuildCommandResult)).Error);

        f.ClearAll();
        f.Context.Guilds.Demote(a, "P3"); // already the lowest rank
        Assert.Equal((uint)GuildCommandError.RankTooLowS, ReadResult(f.Single(a, WorldOpcode.SmsgGuildCommandResult)).Error);

        f.ClearAll();
        f.Context.Guilds.Demote(b, "P1"); // officers can't demote the guild master
        Assert.Equal((uint)GuildCommandError.RankTooHighS, ReadResult(f.Single(b, WorldOpcode.SmsgGuildCommandResult)).Error);

        f.ClearAll();
        f.Context.Guilds.Demote(a, "P2");
        Assert.Equal(2, guild.Find(2)!.Rank);
        Assert.Equal((GuildEvent.Demotion, "P1|P2|Veteran"), ReadEvent(f.Single(c, WorldOpcode.SmsgGuildEvent)));
    }

    [Fact]
    public void GuildChat_ReachesListeners_ThatDoNotIgnoreTheSpeaker()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Found(f, a);
        Join(f, a, b);
        Join(f, a, c);
        f.Context.Friends.AddIgnore(c, "P2");
        f.ClearAll();

        Assert.True(f.Context.Guilds.BroadcastChat(b, officer: false, [9]));

        Assert.Single(f.Sent(a, WorldOpcode.SmsgMessagechat));
        Assert.Single(f.Sent(b, WorldOpcode.SmsgMessagechat));
        Assert.Empty(f.Sent(c, WorldOpcode.SmsgMessagechat));
    }

    [Fact]
    public void OfficerChat_NeedsTheOfficerRights()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player outsider = f.AddPlayer(3);
        Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        Assert.True(f.Context.Guilds.BroadcastChat(b, officer: true, [1])); // no speak right: dropped
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgMessagechat));

        Assert.True(f.Context.Guilds.BroadcastChat(a, officer: true, [2]));
        Assert.Single(f.Sent(a, WorldOpcode.SmsgMessagechat));
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgMessagechat));

        Assert.False(f.Context.Guilds.BroadcastChat(outsider, officer: false, [3]));
    }

    [Fact]
    public void Leave_ByTheLeaderOfAGuildWithMembers_IsRefused_ButMembersMayLeave()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        f.Context.Guilds.Leave(a);
        Assert.Equal((3u, string.Empty, (uint)GuildCommandError.Permissions), ReadResult(f.Single(a, WorldOpcode.SmsgGuildCommandResult)));

        f.Context.Guilds.Leave(b);
        Assert.Null(guild.Find(2));
        Assert.Equal(0u, b.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.Equal((3u, "Arcane", 0u), ReadResult(f.Single(b, WorldOpcode.SmsgGuildCommandResult)));
        Assert.Equal(GuildEvent.Left, ReadEvent(f.Single(a, WorldOpcode.SmsgGuildEvent)).Event);
    }

    [Fact]
    public void Remove_NeedsTheRightAndAHigherRank()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Guild guild = Found(f, a);
        Join(f, a, b);
        Join(f, a, c);
        f.ClearAll();

        f.Context.Guilds.Remove(b, "P3");
        Assert.Equal((uint)GuildCommandError.Permissions, ReadResult(f.Single(b, WorldOpcode.SmsgGuildCommandResult)).Error);

        f.Context.Guilds.Remove(a, "P3");
        Assert.Null(guild.Find(3));
        Assert.Equal((GuildEvent.Removed, "P3|P1"), ReadEvent(f.Single(b, WorldOpcode.SmsgGuildEvent)));
    }

    [Fact]
    public void Disband_ClearsEveryMember_AndDeletesTheGuild()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        f.Context.Guilds.Disband(b);
        Assert.NotNull(f.Context.Guilds.Get(guild.Id));

        f.Context.Guilds.Disband(a);

        Assert.Equal(GuildEvent.Disbanded, ReadEvent(f.Single(b, WorldOpcode.SmsgGuildEvent)).Event);
        Assert.Null(f.Context.Guilds.Get(guild.Id));
        Assert.Null(f.Context.Guilds.GetGuildOf(b));
        Assert.Equal(0u, b.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.Contains(guild.Id, f.Persistence.Deleted);
    }

    [Fact]
    public void SetMotd_NeedsTheRight_AndIsBroadcast()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        f.Context.Guilds.SetMotd(b, "nope");
        f.Context.Guilds.SetMotd(a, "Raid at eight");

        Assert.Equal("Raid at eight", guild.Motd);
        Assert.Equal((GuildEvent.Motd, "Raid at eight"), ReadEvent(f.Single(b, WorldOpcode.SmsgGuildEvent)));
        Assert.Equal("Raid at eight", f.Persistence.Guilds[guild.Id].Motd);
    }

    [Fact]
    public void Roster_ShowsOfficerNotes_OnlyToViewersWithTheRight()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Found(f, a);
        Join(f, a, b);
        f.Context.Guilds.SetOfficerNote(a, "P2", "secret");
        f.ClearAll();

        f.Context.Guilds.Roster(a);
        f.Context.Guilds.Roster(b);

        Assert.Contains("secret", System.Text.Encoding.UTF8.GetString(f.Single(a, WorldOpcode.SmsgGuildRoster)), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(f.Single(b, WorldOpcode.SmsgGuildRoster)), StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_Layout_FollowsVmangos()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(5);
        Guild guild = Found(f, a);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminInvite(5, "Arcane"));
        f.ClearAll();

        f.Context.Guilds.Roster(a);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgGuildRoster));
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(Guild.DefaultMotd, reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(5u, reader.ReadUInt32());
        Assert.Equal(GuildRights.All, reader.ReadUInt32());
        for (int i = 1; i < 5; i++)
        {
            reader.ReadUInt32();
        }

        int offline = 0;
        for (int i = 0; i < 2; i++)
        {
            reader.ReadUInt64();
            byte flags = reader.ReadByte();
            reader.ReadCString();
            reader.ReadUInt32();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt32();
            if (flags == 0)
            {
                offline++;
                reader.ReadSingle();
            }

            reader.ReadCString();
            reader.ReadCString();
        }

        Assert.Equal(1, offline);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(2, guild.MemberCount);
    }

    [Fact]
    public void Query_RepliesWithTenRankNamesAndTheEmblem()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Guild guild = Found(f, a);
        f.ClearAll();

        f.Context.Guilds.Query(a, (uint)guild.Id);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgGuildQueryResponse));
        Assert.Equal((uint)guild.Id, reader.ReadUInt32());
        Assert.Equal("Arcane", reader.ReadCString());
        var ranks = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            ranks.Add(reader.ReadCString());
        }

        Assert.Equal("Initiate", ranks[4]);
        Assert.Equal(string.Empty, ranks[9]);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(-1, reader.ReadInt32());
        }

        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Ranks_AddAndDelete_MovesMembersOfTheDeletedRank()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        f.Context.Guilds.AddRank(a, "Recruit");
        Join(f, a, b);
        Assert.Equal(5, guild.Find(2)!.Rank);

        f.Context.Guilds.DeleteRank(a);
        f.Context.Guilds.DeleteRank(a); // never below five ranks

        Assert.Equal(5, guild.Ranks.Count);
        Assert.Equal(4, guild.Find(2)!.Rank);
        Assert.Equal(4u, b.GetUInt32(UpdateFields.PlayerGuildrank));
    }

    [Fact]
    public void Load_DropsMissingCharacters_AndReplacesAMissingLeader()
    {
        using var f = new SocialFixture();
        f.AddOffline(2);
        f.AddOffline(3);
        var data = new GuildData(
            7, "Old", 1, "hi", string.Empty, 0, -1, -1, -1, -1, -1,
            [new(0, "GM", GuildRights.All), new(1, "O", GuildRights.All), new(2, "V", 0x43), new(3, "M", 0x43), new(4, "I", 0x43)],
            [new(1, 0, string.Empty, string.Empty, 1, 0, 0), new(2, 3, string.Empty, string.Empty, 1, 0, 0), new(3, 1, string.Empty, string.Empty, 1, 0, 0)]);
        var empty = data with { Id = 8, Name = "Gone", Members = [new(99, 0, string.Empty, string.Empty, 1, 0, 0)] };

        f.Context.Guilds.Load([data, empty]);

        Guild guild = f.Context.Guilds.Get(7)!;
        Assert.Equal(2, guild.MemberCount);
        Assert.Equal(3u, guild.LeaderId);
        Assert.Equal(Guild.GuildMasterRank, guild.Find(3)!.Rank);
        Assert.Null(f.Context.Guilds.Get(8));
        Assert.Contains(8, f.Persistence.Deleted);

        Player a = f.AddPlayer(9);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Create(a.Guid.Low, "New", out Guild? created));
        Assert.Equal(8, created!.Id); // ids continue after the stored ones (the deleted empty guild's id is free)
    }

    [Fact]
    public void LoginAndLogout_SendMotdAndSignOnOff()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Found(f, a);
        Join(f, a, b);
        f.ClearAll();

        f.Context.Guilds.OnLoggedIn(b);

        List<byte[]> toB = f.Sent(b, WorldOpcode.SmsgGuildEvent);
        Assert.Equal(GuildEvent.Motd, ReadEvent(toB[0]).Event);
        Assert.Equal(GuildEvent.SignedOn, ReadEvent(toB[1]).Event);
        Assert.Equal((GuildEvent.SignedOn, "P2"), ReadEvent(f.Single(a, WorldOpcode.SmsgGuildEvent)));

        f.ClearAll();
        f.Context.Guilds.OnLoggingOut(b);
        Assert.Equal(GuildEvent.SignedOff, ReadEvent(f.Single(a, WorldOpcode.SmsgGuildEvent)).Event);
    }

    [Fact]
    public void AdminCommands_InviteRankUninviteDelete()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.AddOffline(2);
        Guild guild = Found(f, a);

        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminInvite(2, "arcane"));
        Assert.Equal(GuildAdminResult.RankInvalid, f.Context.Guilds.AdminSetRank(2, 9));
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminSetRank(2, 1));
        Assert.Equal(1, guild.Find(2)!.Rank);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminUninvite(2));
        Assert.Equal(GuildAdminResult.NotInGuild, f.Context.Guilds.AdminUninvite(2));
        Assert.Equal(GuildAdminResult.GuildNotFound, f.Context.Guilds.Delete("Nope"));
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Delete("Arcane"));
        Assert.Null(f.Context.Guilds.GetGuildOf(a));
    }
}
