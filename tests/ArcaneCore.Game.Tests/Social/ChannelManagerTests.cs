using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>Chat channels (vmangos Channel / ChannelMgr / ChannelHandler rules).</summary>
public sealed class ChannelManagerTests
{
    private static (ChatNotify Type, string Channel) ReadNotify(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return ((ChatNotify)reader.ReadByte(), reader.ReadCString());
    }

    private static List<ChatNotify> Notifies(SocialFixture f, Player player)
        => [.. f.Sent(player, WorldOpcode.SmsgChannelNotify).Select(p => (ChatNotify)p[0])];

    [Fact]
    public void CustomChannel_FirstJoinerOwnsAndModerates_LaterJoinsAreAnnounced()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.ClearAll();

        f.Context.Channels.Join(a, "secret", string.Empty);

        List<byte[]> toA = f.Sent(a, WorldOpcode.SmsgChannelNotify);
        var joined = new PacketReader(toA[0]);
        Assert.Equal((byte)ChatNotify.YouJoined, joined.ReadByte());
        Assert.Equal("Secret", joined.ReadCString()); // normalized like a player name
        Assert.Equal((uint)ChannelFlags.Custom, joined.ReadUInt32());
        Assert.Equal(0u, joined.ReadUInt32());
        Assert.Equal([ChatNotify.YouJoined, ChatNotify.ModeChange, ChatNotify.ModeChange], toA.Select(p => (ChatNotify)p[0]));
        Channel channel = f.Context.Channels.Find(Team.Alliance, "SECRET")!;
        Assert.Equal(a.Guid, channel.Owner);
        Assert.Equal(ChannelMemberFlags.Owner | ChannelMemberFlags.Moderator, channel.FlagsOf(a.Guid));

        f.ClearAll();
        f.Context.Channels.Join(b, "Secret", string.Empty);

        var announce = new PacketReader(f.Single(a, WorldOpcode.SmsgChannelNotify));
        Assert.Equal((byte)ChatNotify.Joined, announce.ReadByte());
        Assert.Equal("Secret", announce.ReadCString());
        Assert.Equal(b.Guid.Value, announce.ReadUInt64());
        Assert.Equal([ChatNotify.YouJoined], Notifies(f, b));
    }

    [Fact]
    public void BuiltInChannel_HasDbcFlags_NoOwner_AndNoAnnouncements()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.ClearAll();

        f.Context.Channels.Join(a, "General - Elwynn Forest", string.Empty);
        f.Context.Channels.Join(b, "General - Elwynn Forest", string.Empty);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgChannelNotify));
        Assert.Equal((byte)ChatNotify.YouJoined, reader.ReadByte());
        Assert.Equal("General - Elwynn Forest", reader.ReadCString());
        Assert.Equal(0x18u, reader.ReadUInt32());
        Channel channel = f.Context.Channels.Find(Team.Alliance, "General - Elwynn Forest")!;
        Assert.True(channel.IsConstant);
        Assert.Equal(BuiltInChannels.GeneralId, channel.ChannelId);
        Assert.True(channel.Owner.IsEmpty);

        f.Context.Channels.Join(a, "General - Elwynn Forest", string.Empty); // silent re-join
        Assert.Single(f.Sent(a, WorldOpcode.SmsgChannelNotify));
    }

    [Theory]
    [InlineData("Trade - City", 0x3C)]
    [InlineData("LocalDefense - Elwynn Forest", 0x18)]
    [InlineData("LookingForGroup", 0x50)]
    public void BuiltInChannelFlags(string name, int flags)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        f.Context.Channels.Join(a, name, string.Empty);

        Assert.Equal((ChannelFlags)flags, f.Context.Channels.Find(Team.Alliance, name)!.Flags);
    }

    [Fact]
    public void Join_InvalidName_WrongPassword_AndGuildRecruitmentForGuildedPlayers()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Channels.SetPassword(a, "Club", "pw");
        f.Context.Guilds.Load([]);
        f.Context.Guilds.Create(b.Guid.Low, "Arcane", out Guild? _);
        f.ClearAll();

        f.Context.Channels.Join(b, "1abc", string.Empty);
        f.Context.Channels.Join(b, "Club", "bad");
        f.Context.Channels.Join(b, "GuildRecruitment - City", string.Empty);

        Assert.Equal([ChatNotify.InvalidName, ChatNotify.WrongPassword], Notifies(f, b));
        Assert.False(f.Context.Channels.Find(Team.Alliance, "GuildRecruitment - City")!.IsOn(b.Guid));
        Assert.Empty(f.Context.Channels.JoinedBy(b));

        f.Context.Channels.Join(b, "Club", "pw");
        Assert.True(f.Context.Channels.Find(Team.Alliance, "Club")!.IsOn(b.Guid));
    }

    [Fact]
    public void Say_ReachesMembers_ExceptThoseIgnoringTheSpeaker()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        Player outsider = f.AddPlayer(4);
        foreach (Player p in new[] { a, b, c })
        {
            f.Context.Channels.Join(p, "Club", string.Empty);
        }

        f.Context.Friends.AddIgnore(c, "P2");
        f.ClearAll();

        f.Context.Channels.Say(b, "club", "hello", Language.Common);

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgMessagechat));
        Assert.Equal((byte)ChatType.Channel, reader.ReadByte());
        Assert.Equal((uint)Language.Common, reader.ReadUInt32());
        Assert.Equal("Club", reader.ReadCString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(b.Guid.Value, reader.ReadUInt64());
        Assert.Equal(6u, reader.ReadUInt32());
        Assert.Equal("hello", reader.ReadCString());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);
        Assert.Single(f.Sent(b, WorldOpcode.SmsgMessagechat));
        Assert.Empty(f.Sent(c, WorldOpcode.SmsgMessagechat));

        f.Context.Channels.Say(outsider, "Club", "hi", Language.Common);
        Assert.Equal([ChatNotify.NotMember], Notifies(f, outsider));
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgMessagechat).Skip(1));
    }

    [Fact]
    public void Mute_And_Moderation_StopSpeech()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Channels.Join(b, "Club", string.Empty);

        f.Context.Channels.SetMode(a, "Club", "P2", moderator: false, set: true);
        f.ClearAll();
        f.Context.Channels.Say(b, "Club", "x", Language.Common);
        Assert.Equal([ChatNotify.Muted], Notifies(f, b));

        f.Context.Channels.SetMode(a, "Club", "P2", moderator: false, set: false);
        f.Context.Channels.ToggleModerate(a, "Club");
        f.ClearAll();
        f.Context.Channels.Say(b, "Club", "x", Language.Common);
        Assert.Equal([ChatNotify.NotModerator], Notifies(f, b));

        f.Context.Channels.Say(a, "Club", "x", Language.Common);
        Assert.Single(f.Sent(b, WorldOpcode.SmsgMessagechat));
    }

    [Fact]
    public void WorldDefense_IsMuted_ForAnUnrankedPlayer_WhileHonorIsEnabled()
    {
        using var f = new SocialFixture();
        ArcaneCore.Game.Honor.HonorHooks.Register(f.World, new ArcaneCore.Game.Honor.HonorHooks(new ArcaneCore.Game.Honor.HonorOptions(), ArcaneCore.Game.Honor.HonorClock.System) { InternalRank = _ => 0 });
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "WorldDefense", string.Empty);
        f.ClearAll();

        f.Context.Channels.Say(a, "WorldDefense", "inc", Language.Common);

        Assert.Equal([ChatNotify.Muted], Notifies(f, a));
    }

    [Fact]
    public void KickAndBan_NeedAModerator_AndBanKeepsThePlayerOut()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        foreach (Player p in new[] { a, b, c })
        {
            f.Context.Channels.Join(p, "Club", string.Empty);
        }

        f.ClearAll();
        f.Context.Channels.KickOrBan(b, "Club", "P3", ban: false);
        Assert.Equal([ChatNotify.NotModerator], Notifies(f, b));

        f.Context.Channels.KickOrBan(a, "Club", "P3", ban: true);
        Channel channel = f.Context.Channels.Find(Team.Alliance, "Club")!;
        Assert.False(channel.IsOn(c.Guid));
        Assert.True(channel.IsBanned(c.Guid));
        Assert.Contains(ChatNotify.PlayerBanned, Notifies(f, c));
        Assert.Empty(f.Context.Channels.JoinedBy(c));

        f.ClearAll();
        f.Context.Channels.Join(c, "Club", string.Empty);
        Assert.Equal([ChatNotify.Banned], Notifies(f, c));

        f.Context.Channels.Unban(a, "Club", "P3");
        f.Context.Channels.Join(c, "Club", string.Empty);
        Assert.True(channel.IsOn(c.Guid));
    }

    [Fact]
    public void OwnerLeaving_PassesOwnership_AndTheLastLeaveDeletesTheChannel()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Channels.Join(b, "Club", string.Empty);
        Channel channel = f.Context.Channels.Find(Team.Alliance, "Club")!;
        f.ClearAll();

        f.Context.Channels.Leave(a, "Club");

        Assert.Equal(b.Guid, channel.Owner);
        Assert.Equal([ChatNotify.YouLeft], Notifies(f, a));
        Assert.Contains(ChatNotify.OwnerChanged, Notifies(f, b));

        f.Context.Channels.Leave(b, "Club");
        Assert.Null(f.Context.Channels.Find(Team.Alliance, "Club"));
    }

    [Fact]
    public void LeaveAll_IsSilent_ForTheLeavingPlayer()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Channels.Join(b, "Club", string.Empty);
        f.Context.Channels.Join(a, "General - Elwynn Forest", string.Empty);
        f.ClearAll();

        f.Context.Channels.LeaveAll(a);

        Assert.Empty(f.Sent(a, WorldOpcode.SmsgChannelNotify));
        Assert.Contains(ChatNotify.Left, Notifies(f, b));
        Assert.Empty(f.Context.Channels.JoinedBy(a));
        Assert.NotNull(f.Context.Channels.Find(Team.Alliance, "General - Elwynn Forest")); // built-ins stay
    }

    [Fact]
    public void Factions_HaveSeparateChannels_UnlessTwoSideChannelsAreAllowed()
    {
        using var f = new SocialFixture();
        Player human = f.AddPlayer(1);
        Player orc = f.AddPlayer(2, Race.Orc, x: 5000);
        f.Context.Channels.Join(human, "Club", string.Empty);
        f.Context.Channels.Join(orc, "Club", string.Empty);

        Assert.NotSame(f.Context.Channels.Find(Team.Alliance, "Club"), f.Context.Channels.Find(Team.Horde, "Club"));

        using var shared = new SocialFixture(new Game.Social.SocialOptions { AllowTwoSideChannel = true });
        Player h2 = shared.AddPlayer(1);
        Player o2 = shared.AddPlayer(2, Race.Orc, x: 5000);
        shared.Context.Channels.Join(h2, "Club", string.Empty);
        shared.Context.Channels.Join(o2, "Club", string.Empty);
        Assert.Equal(2, shared.Context.Channels.Find(Team.Horde, "Club")!.MemberCount);
    }

    [Fact]
    public void List_ShowsVisibleMembersWithFlags()
    {
        using var f = new SocialFixture();
        f.World.Options.GmLevelInWhoList = AccountSecurity.Player;
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player gm = f.AddPlayer(3, security: AccountSecurity.GameMaster);
        foreach (Player p in new[] { a, b, gm })
        {
            f.Context.Channels.Join(p, "Club", string.Empty);
        }

        f.ClearAll();
        f.Context.Channels.List(b, "Club");

        var reader = new PacketReader(f.Single(b, WorldOpcode.SmsgChannelList));
        Assert.Equal("Club", reader.ReadCString());
        Assert.Equal((byte)ChannelFlags.Custom, reader.ReadByte());
        Assert.Equal(2u, reader.ReadUInt32()); // the game master is hidden from players
        Assert.Equal(a.Guid.Value, reader.ReadUInt64());
        Assert.Equal((byte)(ChannelMemberFlags.Owner | ChannelMemberFlags.Moderator), reader.ReadByte());
        Assert.Equal(b.Guid.Value, reader.ReadUInt64());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);

        f.Context.Channels.List(b, "Nope");
        Assert.Equal((ChatNotify.NotMember, "Nope"), ReadNotify(f.Single(b, WorldOpcode.SmsgChannelNotify)));
    }

    [Fact]
    public void Invite_TellsTheTarget_UnlessIgnored()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Player c = f.AddPlayer(3);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Friends.AddIgnore(c, "P1");
        f.ClearAll();

        f.Context.Channels.Invite(a, "Club", "P2");
        f.Context.Channels.Invite(a, "Club", "P3");

        Assert.Equal([ChatNotify.Invite], Notifies(f, b));
        Assert.Empty(f.Sent(c, WorldOpcode.SmsgChannelNotify));
        Assert.Equal([ChatNotify.PlayerInvited, ChatNotify.PlayerInvited], Notifies(f, a));
    }

    [Fact]
    public void Owner_IsReportedByName()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.ClearAll();

        f.Context.Channels.SendOwner(a, "Club");

        var reader = new PacketReader(f.Single(a, WorldOpcode.SmsgChannelNotify));
        Assert.Equal((byte)ChatNotify.ChannelOwner, reader.ReadByte());
        Assert.Equal("Club", reader.ReadCString());
        Assert.Equal("P1", reader.ReadCString());
    }
}
