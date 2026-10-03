using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The channel wire constants pinned to the references, and the vmangos-only channel names kept off
/// by default (retail has no "World" or "China" channel with special flags).
/// </summary>
public sealed class ChannelFlagConstantsTests
{
    [Fact]
    public void ChannelFlags_AreTheReferenceValues()
    {
        // vmangos Channel.h:89-96 = mangos-classic Channel.h.
        Assert.Equal(0x01, (byte)ChannelFlags.Custom);
        Assert.Equal(0x04, (byte)ChannelFlags.Trade);
        Assert.Equal(0x08, (byte)ChannelFlags.NotLfg);
        Assert.Equal(0x10, (byte)ChannelFlags.General);
        Assert.Equal(0x20, (byte)ChannelFlags.City);
        Assert.Equal(0x40, (byte)ChannelFlags.Lfg);
    }

    [Fact]
    public void MemberFlags_FollowTheTwoServerReferences()
    {
        // vmangos Channel.h:121-125 and mangos-classic Channel.h:121-123: MODERATOR 0x02, VOICED 0x04,
        // MUTED 0x08. wow_messages smsg_channel_list.wowm:14-22 lists MODERATOR 0x04, VOICED 0x08 and
        // MUTED 0x10 for the same versions; with no client capture to arbitrate, the server references win.
        Assert.Equal(0x01, (byte)ChannelMemberFlags.Owner);
        Assert.Equal(0x02, (byte)ChannelMemberFlags.Moderator);
        Assert.Equal(0x04, (byte)ChannelMemberFlags.Voiced);
        Assert.Equal(0x08, (byte)ChannelMemberFlags.Muted);
    }

    [Fact]
    public void BuiltInChannels_CarryTheDocumentedFlagsAndIds()
    {
        // vmangos Channel.h:74-81 ids and the flag table in the comment at :105-110.
        (uint Id, byte Flags)[] expected = [(1, 0x18), (2, 0x3C), (22, 0x18), (23, 0x18), (25, 0x38), (26, 0x50)];
        Assert.Equal(expected, BuiltInChannels.All.Select(c => (c.Id, (byte)c.Flags)));
    }

    private static (byte Flags, bool Announced) JoinTwice(SocialOptions options, string name)
    {
        using var f = new SocialFixture(options);
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, name, string.Empty);
        f.ClearAll();

        f.Context.Channels.Join(b, name, string.Empty);

        Channel channel = f.Context.Channels.JoinedBy(b).Single();
        bool announced = f.Sent(a, WorldOpcode.SmsgChannelNotify).Any(p => p[0] == (byte)ChatNotify.Joined);
        return ((byte)channel.Flags, announced);
    }

    [Theory]
    [InlineData("World")]
    [InlineData("China")]
    public void WorldAndChinaChannels_AreOrdinaryCustomChannels_ByDefault(string name)
    {
        (byte flags, bool announced) = JoinTwice(new SocialOptions(), name);

        Assert.Equal((byte)ChannelFlags.Custom, flags);
        Assert.True(announced);
    }

    [Fact]
    public void WithTheVmangosExtensions_WorldIsAGeneralChannelWithoutAnnouncements()
    {
        // vmangos Channel.cpp:63-66.
        (byte flags, bool announced) = JoinTwice(new SocialOptions { VmangosChannelExtensions = true }, "World");

        Assert.Equal((byte)ChannelFlags.General, flags);
        Assert.False(announced);
    }

    [Theory]
    [InlineData("China")]
    [InlineData("中国")]
    public void WithTheVmangosExtensions_ChinaIsACustomChannelWithoutAnnouncements(string name)
    {
        // vmangos Channel.cpp:67-71 (the second spelling is the UTF-8 Mandarin name).
        (byte flags, bool announced) = JoinTwice(new SocialOptions { VmangosChannelExtensions = true }, name);

        Assert.Equal((byte)ChannelFlags.Custom, flags);
        Assert.False(announced);
    }
}
