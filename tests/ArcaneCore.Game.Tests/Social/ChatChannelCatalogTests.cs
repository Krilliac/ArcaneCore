using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The built-in channels from ChatChannels.dbc (vmangos GetChannelEntryFor, DBCStores.cpp:531-552, and Channel::Channel,
/// Channel.cpp:33-55): locale patterns, file order and the channel flags derived from the DBC flags.
/// </summary>
public sealed class ChatChannelCatalogTests
{
    private static ChatChannelRow Row(uint id, uint flags, params string[] patterns)
        => new(id, flags, 0, [.. patterns, .. Enumerable.Repeat(string.Empty, 8 - patterns.Length)]);

    [Theory]
    [InlineData(0x00003u, 0x18)] // General: GENERAL | NOT_LFG
    [InlineData(0x0003Bu, 0x3C)] // Trade: + TRADE + CITY
    [InlineData(0x10003u, 0x18)] // LocalDefense
    [InlineData(0x10004u, 0x18)] // WorldDefense
    [InlineData(0x00000u, 0x18)] // LookingForGroup in the 1.12.1 file
    [InlineData(0x20032u, 0x38)] // GuildRecruitment: CITY, the flags Channel::Join refuses to guilded players
    [InlineData(0x40039u, 0x74)] // the 2.x LookingForGroup row (LFG flag): GENERAL | TRADE | CITY | LFG
    public void ChannelFlags_FollowVmangosChannelConstructor(uint dbcFlags, int channelFlags)
        => Assert.Equal((ChannelFlags)channelFlags, ChatChannelCatalog.FlagsOf(dbcFlags));

    [Fact]
    public void TheTranscribedRows_AreThe1121ClientFile()
    {
        Assert.Equal(
            [(1u, 0x18), (2u, 0x3C), (22u, 0x18), (23u, 0x18), (24u, 0x18), (25u, 0x38)],
            ChatChannelCatalog.Builtin.Channels.Select(c => (c.Id, (int)(byte)c.Flags)));
        Assert.False(ChatChannelCatalog.Builtin.FromClientData);
        Assert.Equal(BuiltInChannels.LookingForGroupId, ChatChannelCatalog.Builtin.Find("LookingForGroup")!.Id);
    }

    [Fact]
    public void ACatalogFromTheDbc_MatchesEveryLocalesPattern_TheFirstRowInFileOrderWins()
    {
        ChatChannelCatalog catalog = ChatChannelCatalog.FromDbc(
        [
            Row(1, 0x3, "General - %s", "Allgemein - %s"),
            Row(2, 0x3B, "Trade - %s", "Handel - %s"),
            Row(99, 0x0, "Gen"), // a later, broader pattern never beats an earlier match
            Row(98, 0x0),        // no pattern at all: skipped
        ]);

        Assert.True(catalog.FromClientData);
        Assert.Equal(1u, catalog.Find("Allgemein - Wald von Elwynn")!.Id);
        Assert.Equal(1u, catalog.Find("General - Elwynn Forest")!.Id);
        Assert.Equal((2u, (ChannelFlags)0x3C), (catalog.Find("Handel - Stadt")!.Id, catalog.Find("Handel - Stadt")!.Flags));
        Assert.Equal(99u, catalog.Find("Gentlemen")!.Id);
        Assert.Null(catalog.Find("Mychannel"));
        Assert.DoesNotContain(catalog.Channels, c => c.Id == 98);
    }

    [Fact]
    public void JoiningALocalisedGeneralChannel_CreatesTheBuiltInChannel_WhenTheCatalogComesFromTheDbc()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "Allgemein - Wald von Elwynn", string.Empty);
        Assert.False(f.Context.Channels.Find(Team.Alliance, "Allgemein - Wald von Elwynn")!.IsConstant); // English rows only: a custom channel

        using var g = new SocialFixture();
        g.Context.ChannelCatalog = ChatChannelCatalog.FromDbc([Row(1, 0x3, "General - %s", "Allgemein - %s")]);
        Player b = g.AddPlayer(1);
        g.ClearAll();
        g.Context.Channels.Join(b, "Allgemein - Wald von Elwynn", string.Empty);

        Channel channel = g.Context.Channels.Find(Team.Alliance, "Allgemein - Wald von Elwynn")!;
        Assert.True(channel.IsConstant);
        Assert.Equal((BuiltInChannels.GeneralId, ChannelFlags.General | ChannelFlags.NotLfg), (channel.ChannelId, channel.Flags));
        var notify = new PacketReader(g.Single(b, WorldOpcode.SmsgChannelNotify));
        Assert.Equal((byte)ChatNotify.YouJoined, notify.ReadByte());
    }
}
