using ArcaneCore.Game;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The master's chat commands (mangoszero ChatShortcutActions; vmangos partybot table, Chat.cpp:94-116): parsing, who may give them,
/// the chat packets the bot reads and writes, the stranger reply limit, and the loot roll packets.
/// </summary>
public sealed class PlayerbotChatCommandTests
{
    private static readonly ObjectGuid Bot = ObjectGuid.Player(10);
    private static readonly ObjectGuid Master = ObjectGuid.Player(20);
    private static readonly ObjectGuid Stranger = ObjectGuid.Player(30);

    [Theory]
    [InlineData("follow", PlayerbotPartyCommand.Follow)]
    [InlineData("FOLLOW", PlayerbotPartyCommand.Follow)]
    [InlineData("  Stay ", PlayerbotPartyCommand.Stay)]
    [InlineData("attack", PlayerbotPartyCommand.Attack)]
    [InlineData("stop", PlayerbotPartyCommand.Stop)]
    [InlineData("Passive", PlayerbotPartyCommand.Stop)]
    [InlineData("come", PlayerbotPartyCommand.Come)]
    [InlineData("status", PlayerbotPartyCommand.Status)]
    [InlineData("LeAvE", PlayerbotPartyCommand.Leave)]
    public void Commands_AreOneWord_AnyCase(string text, PlayerbotPartyCommand expected)
    {
        Assert.True(PlayerbotChatCommands.TryParse(text, out PlayerbotPartyCommand command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dance")]
    [InlineData("follow me")]
    [InlineData("please stay")]
    [InlineData("followfollowfollow")]
    [InlineData(".follow")]
    public void UnknownCommandsAndTalk_AreNoCommands(string? text)
        => Assert.False(PlayerbotChatCommands.TryParse(text, out _));

    [Theory]
    [InlineData(ChatType.Whisper)]
    [InlineData(ChatType.Party)]
    [InlineData(ChatType.Raid)]
    [InlineData(ChatType.RaidLeader)]
    public void TheMastersWhisperOrGroupLine_IsACommand(ChatType type)
        => Assert.Equal(PlayerbotChatSource.Master, PlayerbotChatCommands.Classify(Line(type, Master), Bot, Master, senderIsBot: false));

    [Fact]
    public void AStrangersWhisper_GetsThePoliteAnswer_AndTheirPartyLineIsIgnored()
    {
        Assert.Equal(PlayerbotChatSource.Stranger, PlayerbotChatCommands.Classify(Line(ChatType.Whisper, Stranger), Bot, Master, false));
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(Line(ChatType.Party, Stranger), Bot, Master, false));
    }

    [Fact]
    public void WithoutAMaster_EveryWhisperIsAStrangers()
        => Assert.Equal(PlayerbotChatSource.Stranger, PlayerbotChatCommands.Classify(Line(ChatType.Whisper, Master), Bot, ObjectGuid.Empty, false));

    [Theory]
    [InlineData(ChatType.Say)]
    [InlineData(ChatType.Yell)]
    [InlineData(ChatType.Guild)]
    [InlineData(ChatType.WhisperInform)]
    [InlineData(ChatType.Afk)]
    public void TheMastersOtherLines_AreNoCommands(ChatType type)
        => Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(Line(type, Master), Bot, Master, false));

    [Fact]
    public void TheBotsOwnLines_AndOtherBotsLines_AreIgnored()
    {
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(Line(ChatType.Whisper, Bot), Bot, Master, false));
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(Line(ChatType.Whisper, Stranger), Bot, Master, senderIsBot: true));
        Assert.Equal(PlayerbotChatSource.Ignore, PlayerbotChatCommands.Classify(Line(ChatType.Whisper, ObjectGuid.Empty), Bot, Master, false));
    }

    [Theory]
    [InlineData(ChatType.Whisper)]
    [InlineData(ChatType.Party)]
    [InlineData(ChatType.Say)]
    public void TheServersChatLines_AreDecoded(ChatType type)
    {
        byte[] packet = ChatPackets.BuildMessage(type, Language.Common, Master, "Follow", ChatTag.None);
        Assert.True(PlayerbotChatCommands.TryRead(packet, out PlayerbotChatLine line));
        Assert.Equal(new PlayerbotChatLine(type, Language.Common, Master, "Follow"), line);
    }

    [Fact]
    public void TruncatedOrChannelLines_AreNotDecoded()
    {
        byte[] packet = ChatPackets.BuildMessage(ChatType.Whisper, Language.Common, Master, "follow", ChatTag.None);
        Assert.False(PlayerbotChatCommands.TryRead(packet[..(packet.Length - 4)], out _));
        Assert.False(PlayerbotChatCommands.TryRead([(byte)ChatType.Channel, .. packet[1..]], out _));
        Assert.False(PlayerbotChatCommands.TryRead([], out _));
    }

    [Fact]
    public void TheBotsWhisper_IsAClientChatPacket()
    {
        byte[] payload = PlayerbotChatCommands.Whisper(Language.Orcish, "Masterone", "Following.");
        var reader = new PacketReader(payload);
        Assert.Equal((uint)ChatType.Whisper, reader.ReadUInt32());
        Assert.Equal((uint)Language.Orcish, reader.ReadUInt32());
        Assert.Equal("Masterone", reader.ReadCString());
        Assert.Equal("Following.", reader.ReadCString());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void StrangerReplies_AreOncePerSenderPerMinute_AndCappedInAll()
    {
        var limiter = new PlayerbotReplyLimiter();
        Assert.True(limiter.TryTake(Stranger, 0));
        Assert.False(limiter.TryTake(Stranger, PlayerbotReplyLimiter.WindowMs - 1));
        Assert.True(limiter.TryTake(Stranger, PlayerbotReplyLimiter.WindowMs));

        var crowd = new PlayerbotReplyLimiter();
        for (uint i = 0; i < PlayerbotReplyLimiter.MaxPerWindow; i++) Assert.True(crowd.TryTake(ObjectGuid.Player(100 + i), 1_000));
        Assert.False(crowd.TryTake(ObjectGuid.Player(999), 2_000));
        Assert.True(crowd.TryTake(ObjectGuid.Player(999), 1_000 + PlayerbotReplyLimiter.WindowMs));
    }

    [Theory]
    [InlineData(PlayerbotLootRoll.Pass, RollVote.Pass)]
    [InlineData(PlayerbotLootRoll.Greed, RollVote.Greed)]
    public void LootRolls_AreAnsweredWithTheConfiguredVote_InTheServersOwnFormat(PlayerbotLootRoll choice, RollVote expected)
    {
        var corpse = ObjectGuid.WithEntry(HighGuid.Unit, 990001, 990001);
        Assert.True(PlayerbotLootRolls.TryRead(GroupLootPackets.StartRoll(corpse, 3, 2589, 60_000), out PlayerbotLootRolls.StartRoll roll));
        Assert.Equal(new PlayerbotLootRolls.StartRoll(corpse, 3, 2589), roll);

        byte[] vote = PlayerbotLootRolls.Vote(roll, PlayerbotLootRolls.VoteFor(choice));
        Assert.True(GroupLootPackets.TryParseLootRoll(vote, out ObjectGuid source, out uint slot, out RollVote parsed));
        Assert.Equal((corpse, 3u, expected), (source, slot, parsed));
        Assert.False(PlayerbotLootRolls.TryRead(new byte[11], out _));
    }

    private static PlayerbotChatLine Line(ChatType type, ObjectGuid sender) => new(type, Language.Common, sender, "follow");
}
