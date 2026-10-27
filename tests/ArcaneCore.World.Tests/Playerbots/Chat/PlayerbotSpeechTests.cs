using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Bot speech (docs/areas/playbots.md, Bot speech): the central limiter (bot gap and cap, target gap, shared scope budgets that thin out
/// in crowded channels, dedupe, the bot-to-bot guard), TrySpeak admission, the reply channel mapping, the world channel names, the
/// built-in's small-talk filter and the options' defaults and checks. Pure and deterministic: time is passed in by hand.
/// </summary>
public sealed class PlayerbotSpeechTests
{
    private static readonly Guid BotA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BotB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly ObjectGuid GuidA = new(0xA);
    private static readonly ObjectGuid GuidB = new(0xB);
    private static readonly ObjectGuid Player = new(0x42);

    private static BotSpeech Line(string text, BotSpeechChannel channel = BotSpeechChannel.World, BotSpeechPriority priority = BotSpeechPriority.Normal)
        => new(channel, text, "test", priority) { ChannelName = channel == BotSpeechChannel.World ? "General - Elwynn Forest" : null };

    private static SpeechScope Scope(int cap = 3) => new("ch:1:general - elwynn forest", cap, SpeechScope.MinuteMs);

    // --- limiter ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Limiter_KeepsTheBotGap_ButNotForReplies()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotGapSeconds = 20, DedupeSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("lf healer"), Scope(10), options, 0));
        Assert.Equal(BotSpeechAdmission.Cooldown, limiter.TryTake(BotA, GuidA, Line("anyone?"), Scope(10), options, 10_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("sure") with { IsReply = true }, Scope(10), options, 10_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("anyone?"), Scope(10), options, 30_000));
    }

    [Fact]
    public void Limiter_CapsLinesPerMinutePerBot_EvenForHighPriority()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotGapSeconds = 0, BotLinesPerMinute = 2, DedupeSeconds = 0, TargetGapSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("1", BotSpeechChannel.Say, BotSpeechPriority.High), null, options, 0));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("2", BotSpeechChannel.Say, BotSpeechPriority.High), null, options, 1_000));
        Assert.Equal(BotSpeechAdmission.Cooldown, limiter.TryTake(BotA, GuidA, Line("3", BotSpeechChannel.Say, BotSpeechPriority.High), null, options, 2_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("3", BotSpeechChannel.Say, BotSpeechPriority.High), null, options, 61_000));
    }

    [Fact]
    public void Limiter_SharesTheChannelBudgetAcrossBots()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { DedupeSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("wts linen"), Scope(1), options, 0));
        Assert.Equal(BotSpeechAdmission.Budget, limiter.TryTake(BotB, GuidB, Line("lf group"), Scope(1), options, 1_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotB, GuidB, Line("lf group"), Scope(1), options, 61_000));
    }

    [Fact]
    public void Limiter_HighPriority_GetsOneLineOverTheScopeCap()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { DedupeSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("a"), Scope(1), options, 0));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotB, GuidB, Line("b", priority: BotSpeechPriority.High), Scope(1), options, 0));
        Assert.Equal(BotSpeechAdmission.Budget, limiter.TryTake(BotB, GuidB, Line("c", priority: BotSpeechPriority.High) with { IsReply = true }, Scope(1), options, 0));
    }

    [Fact]
    public void Limiter_ZeroCapScope_SaysNothing()
    {
        var limiter = new PlayerbotSpeechLimiter();
        Assert.Equal(BotSpeechAdmission.Budget, limiter.TryTake(BotA, GuidA, Line("x", priority: BotSpeechPriority.High), Scope(0), new PlayerbotSpeechOptions(), 0));
    }

    [Fact]
    public void Limiter_DropsTheSameLineInTheSamePlace_CaseAndSpaceInsensitive()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { DedupeSeconds = 120 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, Line("LFG deadmines"), Scope(10), options, 0));
        Assert.Equal(BotSpeechAdmission.Duplicate, limiter.TryTake(BotB, GuidB, Line("  lfg DEADMINES "), Scope(10), options, 30_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotB, GuidB, Line("lfg deadmines"), new SpeechScope("ch:1:trade - city", 10, SpeechScope.MinuteMs), options, 30_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotB, GuidB, Line("lfg deadmines"), Scope(10), options, 121_000));
    }

    [Fact]
    public void Limiter_KeepsTheTargetGap_UnlessHighPriority()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { TargetGapSeconds = 10, DedupeSeconds = 0 };
        BotSpeech whisper = BotSpeech.WhisperTo("Testplayer", Player, "hey", "test") with { IsReply = true };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, whisper, null, options, 0));
        Assert.Equal(BotSpeechAdmission.Target, limiter.TryTake(BotA, GuidA, whisper with { Text = "again" }, null, options, 5_000));
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, whisper with { Text = "again", Priority = BotSpeechPriority.High }, null, options, 5_000));
    }

    [Fact]
    public void Limiter_PeekTakesNothing()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions();
        for (int i = 0; i < 10; i++) Assert.Equal(BotSpeechAdmission.Queued, limiter.Peek(BotA, GuidA, Line("x"), options, 0));
        Assert.Equal(0, limiter.Taken);
    }

    [Fact]
    public void ChannelScope_StretchesTheMinuteInCrowdedChannels()
    {
        Assert.Equal(SpeechScope.MinuteMs, SpeechScope.Channel("k", 3, 0, 10).WindowMs);
        Assert.Equal(SpeechScope.MinuteMs, SpeechScope.Channel("k", 3, 10, 10).WindowMs);
        Assert.Equal(2 * SpeechScope.MinuteMs, SpeechScope.Channel("k", 3, 11, 10).WindowMs);
        Assert.Equal(5 * SpeechScope.MinuteMs, SpeechScope.Channel("k", 3, 50, 10).WindowMs);
        Assert.Equal(3, SpeechScope.Channel("k", 3, 50, 10).Cap);
    }

    // --- bot-to-bot ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void BotToBot_EndsAfterMaxTurns_AndRestartsAfterQuiet()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotToBotMaxTurns = 2, BotToBotQuietSeconds = 120, BotGapSeconds = 0, TargetGapSeconds = 0, DedupeSeconds = 0 };
        long now = 0;
        int turn = limiter.NextBotTurn(GuidB, GuidA, options, now);
        Assert.Equal(1, turn);
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, ToBot("hi", GuidB, turn), null, options, now));
        turn = limiter.NextBotTurn(GuidA, GuidB, options, now += 5_000);
        Assert.Equal(2, turn);
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotB, GuidB, ToBot("yo", GuidA, turn), null, options, now));
        Assert.Equal(0, limiter.NextBotTurn(GuidB, GuidA, options, now += 5_000));
        Assert.Equal(BotSpeechAdmission.BotToBot, limiter.TryTake(BotA, GuidA, ToBot("sup", GuidB, 3), null, options, now));
        Assert.Equal(1, limiter.NextBotTurn(GuidB, GuidA, options, now + 121_000));
    }

    [Fact]
    public void BotToBot_Off_AnswersNothing()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotToBot = false };
        Assert.Equal(0, limiter.NextBotTurn(GuidA, GuidB, options, 0));
        Assert.Equal(BotSpeechAdmission.BotToBot, limiter.TryTake(BotA, GuidA, ToBot("hi", GuidB, 1), null, options, 0));
    }

    [Fact]
    public void BotToBot_GlobalMinuteBudget_StopsEveryPair()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotToBotLinesPerMinute = 1, BotGapSeconds = 0, DedupeSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, ToBot("hi", GuidB, 1), null, options, 0));
        Assert.Equal(0, limiter.NextBotTurn(new ObjectGuid(0xC), new ObjectGuid(0xD), options, 1_000));
        Assert.Equal(1, limiter.NextBotTurn(new ObjectGuid(0xC), new ObjectGuid(0xD), options, 61_000));
    }

    [Fact]
    public void BotToBot_PairHourBudget_StopsThatPair()
    {
        var limiter = new PlayerbotSpeechLimiter();
        var options = new PlayerbotSpeechOptions { BotToBotPairLinesPerHour = 1, BotToBotQuietSeconds = 10, BotGapSeconds = 0, DedupeSeconds = 0 };
        Assert.Equal(BotSpeechAdmission.Queued, limiter.TryTake(BotA, GuidA, ToBot("hi", GuidB, 1), null, options, 0));
        Assert.Equal(0, limiter.NextBotTurn(GuidB, GuidA, options, 600_000));
        Assert.Equal(1, limiter.NextBotTurn(GuidB, GuidA, options, 3_600_000));
    }

    private static BotSpeech ToBot(string text, ObjectGuid target, int turn)
        => BotSpeech.WhisperTo("Other", target, text, "bot-reply") with { IsReply = true, ToBot = true, Turn = turn };

    // --- TrySpeak ----------------------------------------------------------------------------------------------------------

    private static PlayerbotChat NewChat(Action<PlayerbotChatOptions>? configure = null)
    {
        var options = new PlayerbotOptions();
        options.Chat.Enabled = true;
        ChatSamples.NoScreeningDeadline(options.Chat);
        configure?.Invoke(options.Chat);
        options.Validate();
        return new PlayerbotChat(options, new FakeBotChatClient(), new FakeBotChatEnvironment(new()), new ManualTime(), new ListLogger());
    }

    [Fact]
    public async Task TrySpeak_QueuesACleanLine_AndTheDrainCarriesTheEnvelope()
    {
        await using PlayerbotChat chat = NewChat();
        BotSpeech speech = BotSpeech.InChannel("LookingForGroup", "lfg deadmines, rogue 18", "lfg");
        Assert.Equal(BotSpeechAdmission.Queued, chat.TrySpeak(BotA, GuidA, speech));
        BotChatReply reply = Assert.Single(chat.DrainReplies());
        Assert.Equal("lfg deadmines, rogue 18", reply.Text);
        Assert.NotNull(reply.Speech);
        Assert.Equal(BotSpeechChannel.World, reply.Speech!.Channel);
        Assert.Equal("LookingForGroup", reply.Speech.ChannelName);
    }

    [Fact]
    public async Task TrySpeak_RefusesWhatCannotBeSaid()
    {
        await using PlayerbotChat chat = NewChat();
        Assert.Equal(BotSpeechAdmission.Empty, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.Say, "   ", "t")));
        Assert.Equal(BotSpeechAdmission.Empty, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.Whisper, "hi", "t")));
        Assert.Equal(BotSpeechAdmission.Empty, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.World, "hi", "t")));
        Assert.Equal(BotSpeechAdmission.Empty, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.Emote, string.Empty, "t")));
        Assert.Equal(BotSpeechAdmission.Queued, chat.TrySpeak(BotA, GuidA, BotSpeech.EmoteAt(ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes.TextEmotes.Wave, Player, "t")));
    }

    [Fact]
    public async Task TrySpeak_Disabled_WhenSpeechIsOff()
    {
        await using PlayerbotChat chat = NewChat(c => c.Speech.Enabled = false);
        Assert.Equal(BotSpeechAdmission.Disabled, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.Say, "hello", "t")));
    }

    [Fact]
    public async Task TrySpeak_QueueIsBounded()
    {
        await using PlayerbotChat chat = NewChat(c => c.Speech.MaxQueuedLines = 16);
        for (int i = 0; i < 16; i++)
            Assert.Equal(BotSpeechAdmission.Queued, chat.TrySpeak(Guid.NewGuid(), new ObjectGuid((ulong)(100 + i)), new BotSpeech(BotSpeechChannel.Say, "line " + i, "t")));
        Assert.Equal(BotSpeechAdmission.QueueFull, chat.TrySpeak(BotA, GuidA, new BotSpeech(BotSpeechChannel.Say, "one more", "t")));
    }

    // --- mapping -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ChatType.Whisper, (int)BotSpeechChannel.Whisper)]
    [InlineData(ChatType.Say, (int)BotSpeechChannel.Say)]
    [InlineData(ChatType.Yell, (int)BotSpeechChannel.Yell)]
    [InlineData(ChatType.Party, (int)BotSpeechChannel.Party)]
    [InlineData(ChatType.Raid, (int)BotSpeechChannel.Raid)]
    [InlineData(ChatType.RaidLeader, (int)BotSpeechChannel.Raid)]
    [InlineData(ChatType.RaidWarning, (int)BotSpeechChannel.Raid)]
    [InlineData(ChatType.Battleground, (int)BotSpeechChannel.Battleground)]
    [InlineData(ChatType.BattlegroundLeader, (int)BotSpeechChannel.Battleground)]
    [InlineData(ChatType.Guild, (int)BotSpeechChannel.Guild)]
    [InlineData(ChatType.Channel, (int)BotSpeechChannel.World)]
    public void Replies_GoBackOnTheLinesOwnChannel(ChatType heard, int expected)
        => Assert.Equal((BotSpeechChannel)expected, BotSpeech.ReplyChannelFor(heard));

    [Theory]
    [InlineData(ChatType.Officer)]
    [InlineData(ChatType.Emote)]
    [InlineData(ChatType.MonsterSay)]
    public void Replies_NeverGoToOtherChannels(ChatType heard) => Assert.Null(BotSpeech.ReplyChannelFor(heard));

    [Fact]
    public async Task Answers_EveryChannelByDefault_AndRaidUnderParty()
    {
        await using PlayerbotChat chat = NewChat();
        foreach (ChatType type in new[] { ChatType.Whisper, ChatType.Say, ChatType.Yell, ChatType.Party, ChatType.Raid, ChatType.Battleground, ChatType.Guild, ChatType.Channel })
            Assert.True(chat.Answers(type), type.ToString());
        Assert.False(chat.Answers(ChatType.Officer));

        await using PlayerbotChat partyOnly = NewChat(c => c.Channels = PlayerbotChatChannels.Party);
        Assert.True(partyOnly.Answers(ChatType.Raid));
        Assert.False(partyOnly.Answers(ChatType.Channel));
    }

    [Fact]
    public void LegacyReplies_AreSaidOnTheirChannel()
    {
        var reply = new BotChatReply(BotA, Player, "Testplayer", ChatType.Whisper, "hey", null, false, "Builtin");
        BotSpeech speech = Assert.IsType<BotSpeech>(PlayerbotPartyAI.SpeechOf(reply));
        Assert.Equal(BotSpeechChannel.Whisper, speech.Channel);
        Assert.Equal("Testplayer", speech.Target);
        Assert.True(speech.IsReply);

        var announcement = new BotChatReply(BotA, Player, "Testplayer", ChatType.Guild, "ding 20!", null, false, "Builtin", Guild: "Knights");
        BotSpeech guild = Assert.IsType<BotSpeech>(PlayerbotPartyAI.SpeechOf(announcement));
        Assert.Equal(BotSpeechChannel.Guild, guild.Channel);
        Assert.Equal("Knights", guild.Guild);
        Assert.False(guild.IsReply);

        Assert.Null(PlayerbotPartyAI.SpeechOf(reply with { Text = null }));
    }

    // --- world channels ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WorldChannels_OpenWorldZone()
        => Assert.Equal(["General - Elwynn Forest", "LocalDefense - Elwynn Forest", "LookingForGroup"],
            PlayerbotChannelMembership.Desired(PlayerbotWorldChannels.All, "Elwynn Forest", capital: false, openWorld: true));

    [Fact]
    public void WorldChannels_CapitalAddsTrade()
        => Assert.Equal(["General - Stormwind City", "Trade - City", "LocalDefense - Stormwind City", "LookingForGroup"],
            PlayerbotChannelMembership.Desired(PlayerbotWorldChannels.All, "Stormwind City", capital: true, openWorld: true));

    [Fact]
    public void WorldChannels_InstanceKeepsOnlyLookingForGroup()
        => Assert.Equal(["LookingForGroup"], PlayerbotChannelMembership.Desired(PlayerbotWorldChannels.All, "The Deadmines", false, openWorld: false));

    [Fact]
    public void WorldChannels_NoneWanted_IsEmpty()
        => Assert.Empty(PlayerbotChannelMembership.Desired(PlayerbotWorldChannels.None, "Elwynn Forest", true, true));

    [Fact]
    public void WorldChannels_JoinPacket_IsNameAndEmptyPassword()
        => Assert.Equal("General\0\0"u8.ToArray(), PlayerbotChannelMembership.JoinPacket("General"));

    // --- built-in small talk -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData((int)BuiltinChatIntent.Greeting, true)]
    [InlineData((int)BuiltinChatIntent.Doing, true)]
    [InlineData((int)BuiltinChatIntent.Where, true)]
    [InlineData((int)BuiltinChatIntent.Bye, true)]
    [InlineData((int)BuiltinChatIntent.Command, false)]
    [InlineData((int)BuiltinChatIntent.Help, false)]
    [InlineData((int)BuiltinChatIntent.Group, false)]
    [InlineData((int)BuiltinChatIntent.Unknown, false)]
    public void Builtin_AnswersBotsWithSmallTalkOnly(int intent, bool answered)
        => Assert.Equal(answered, PlayerbotBuiltinChat.SmallTalkAllowed(ChatSamples.Ask("x") with { SenderIsBot = true }, (BuiltinChatIntent)intent));

    [Theory]
    [InlineData((int)BuiltinChatIntent.Group, true)]
    [InlineData((int)BuiltinChatIntent.Greeting, true)]
    [InlineData((int)BuiltinChatIntent.Unknown, false)]
    [InlineData((int)BuiltinChatIntent.Where, false)]
    public void Builtin_AnswersUnaddressedGuildQuestions_OnlyWithSomethingReal(int intent, bool answered)
        => Assert.Equal(answered, PlayerbotBuiltinChat.SmallTalkAllowed(ChatSamples.Ask("x", channel: ChatType.Guild) with { Addressed = false }, (BuiltinChatIntent)intent));

    [Fact]
    public void Builtin_UnknownLineFromABot_GetsNoReply()
    {
        BotChatAsk ask = ChatSamples.Ask("the purple elephant sings", channel: ChatType.Say) with { SenderIsBot = true };
        Assert.Null(PlayerbotBuiltinChat.Answer(ask, null, true, new Random(1)).Text);
    }

    [Theory]
    [InlineData("anyone up for deadmines?", true)]
    [InlineData("Anyone know where the rogue trainer is", true)]
    [InlineData("lfg sm", true)]
    [InlineData("ding", false)]
    [InlineData("", false)]
    public void OpenQuestions(string text, bool open) => Assert.Equal(open, PlayerbotChatPrompts.IsOpenQuestion(text));

    // --- options -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultOn_AndValid()
    {
        var options = new PlayerbotChatOptions();
        Assert.Equal(PlayerbotChatChannels.All, options.Channels);
        Assert.True(options.Speech.Enabled);
        Assert.True(options.Speech.RequireRealListener);
        Assert.True(options.Speech.BotToBot);
        Assert.True(options.Speech.EmoteReplies);
        Assert.Equal(PlayerbotWorldChannels.All, options.Speech.WorldChannels);
        options.Speech.Validate();
    }

    [Fact]
    public void Options_OutOfRange_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSpeechOptions { BotLinesPerMinute = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSpeechOptions { BotToBotMaxTurns = 11 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSpeechOptions { WorldChannels = (PlayerbotWorldChannels)64 }.Validate());
    }
}
