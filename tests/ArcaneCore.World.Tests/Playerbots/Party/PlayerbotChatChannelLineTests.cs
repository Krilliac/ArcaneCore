using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// What a bot reads from its session for speech: world channel lines (CHAT_MSG_CHANNEL with the channel name), text emotes aimed at it,
/// and other bots' lines (the Bot source, only while bots hear bots); and the channel line it writes. Plus the emote-in-kind table.
/// </summary>
public sealed class PlayerbotChatChannelLineTests
{
    private static readonly ObjectGuid Bot = new(0x10);
    private static readonly ObjectGuid Master = new(0x20);
    private static readonly ObjectGuid Other = new(0x30);

    private static byte[] ChannelLine(string channel, ulong sender, string text)
    {
        var w = new PacketWriter(64);
        w.WriteByte((byte)ChatType.Channel);
        w.WriteUInt32((uint)Language.Common);
        w.WriteCString(channel);
        w.WriteUInt32(0); // rank
        w.WriteUInt64(sender);
        w.WriteUInt32((uint)text.Length + 1);
        w.WriteCString(text);
        w.WriteByte(0);
        return w.ToArray();
    }

    private static byte[] TextEmote(ulong emoter, uint emote, string target)
    {
        var w = new PacketWriter(32);
        w.WriteUInt64(emoter);
        w.WriteUInt32(emote);
        w.WriteUInt32(0);
        w.WriteUInt32((uint)target.Length + 1);
        w.WriteCString(target);
        return w.ToArray();
    }

    [Fact]
    public void ChannelLines_AreDecoded_WithTheirChannel()
    {
        Assert.True(PlayerbotChatCommands.TryRead(ChannelLine("General - Elwynn Forest", 0x30, "Chatbot where is goldshire?"), out PlayerbotChatLine line));
        Assert.Equal(ChatType.Channel, line.Type);
        Assert.Equal("General - Elwynn Forest", line.ChannelName);
        Assert.Equal(Other, line.Sender);
        Assert.Equal("Chatbot where is goldshire?", line.Text);
    }

    [Fact]
    public void ChannelLines_Truncated_AreRefused()
    {
        byte[] full = ChannelLine("Trade - City", 0x30, "wts linen");
        Assert.False(PlayerbotChatCommands.TryRead(full[..20], out _));
    }

    [Fact]
    public void ChannelMessage_RoundTripsTheClientLayout()
    {
        byte[] body = PlayerbotChatCommands.ChannelMessage(Language.Common, "LookingForGroup", "lfg rfc");
        var reader = new PacketReader(body);
        Assert.Equal((uint)ChatType.Channel, reader.ReadUInt32());
        Assert.Equal((uint)Language.Common, reader.ReadUInt32());
        Assert.Equal("LookingForGroup", reader.ReadCString());
        Assert.Equal("lfg rfc", reader.ReadCString());
    }

    [Fact]
    public void TextEmotes_AreDecoded_WithTheTargetName()
    {
        Assert.True(PlayerbotChatCommands.TryReadTextEmote(TextEmote(0x30, PlayerbotSocialEmotes.TextEmotes.Wave, "Chatbot"), out PlayerbotTextEmote emote));
        Assert.Equal(Other, emote.Emoter);
        Assert.Equal(PlayerbotSocialEmotes.TextEmotes.Wave, emote.TextEmote);
        Assert.Equal("Chatbot", emote.TargetName);
        Assert.True(PlayerbotChatCommands.TryReadTextEmote(TextEmote(0x30, PlayerbotSocialEmotes.TextEmotes.Dance, ""), out emote));
        Assert.Equal(string.Empty, emote.TargetName);
        Assert.False(PlayerbotChatCommands.TryReadTextEmote(new byte[8], out _));
    }

    [Fact]
    public void BotLines_AreHeard_OnlyWhileBotsHearBots()
    {
        var line = new PlayerbotChatLine(ChatType.Say, Language.Common, Other, "hey Chatbot");
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(line, Bot, Master, senderIsBot: true));
        Assert.Equal(PlayerbotChatSource.Bot, PlayerbotChatCommands.Classify(line, Bot, Master, senderIsBot: true, botsHeard: true));
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(line with { Sender = Bot }, Bot, Master, senderIsBot: true, botsHeard: true));
    }

    [Theory]
    [InlineData(PlayerbotSocialEmotes.TextEmotes.Wave)]
    [InlineData(PlayerbotSocialEmotes.TextEmotes.Bow)]
    [InlineData(PlayerbotSocialEmotes.TextEmotes.Hug)]
    [InlineData(PlayerbotSocialEmotes.TextEmotes.Thank)]
    [InlineData(PlayerbotSocialEmotes.TextEmotes.Salute)]
    public void Emotes_AreAnsweredInKind(uint emote)
    {
        var random = new Random(7);
        for (int i = 0; i < 20; i++) Assert.NotNull(PlayerbotSocialEmotes.ReplyTo(emote, random));
    }

    [Fact]
    public void Emotes_SaluteWithASalute_AndMostEmotesGetNothing()
    {
        Assert.Equal(PlayerbotSocialEmotes.TextEmotes.Salute, PlayerbotSocialEmotes.ReplyTo(PlayerbotSocialEmotes.TextEmotes.Salute, new Random(1)));
        Assert.Null(PlayerbotSocialEmotes.ReplyTo(PlayerbotSocialEmotes.TextEmotes.Agree, new Random(1)));
        Assert.Null(PlayerbotSocialEmotes.ReplyTo(9999, new Random(1)));
    }
}
