using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// A bot's own announcement through the chat service (<see cref="PlayerbotChat.TryAnnounce"/> and its worker, a fake transport and a
/// manual clock; no network): what it takes to be admitted, that the topic's sentence is screened like any input and the line like any
/// output, that a model is used only while RequireOptIn is off, and what a flagged line costs (it is dropped, or replaced by the built-in
/// one) with no strike for anybody.
/// </summary>
public sealed class PlayerbotAnnounceTests
{
    private const string Key = "sk-announce-test";
    private const string Guild = "Quiet Owls";

    private static PlayerbotChatProviderOptions Model() => new()
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://api.example.test/v1", Model = "test-model",
        ApiKeyEnvironmentVariable = "TEST_ANNOUNCE_KEY", InputUsdPerMillionTokens = 0, OutputUsdPerMillionTokens = 0,
    };

    private sealed class Rig : IAsyncDisposable
    {
        public PlayerbotOptions Options { get; } = new();
        public FakeBotChatClient Client { get; } = new();
        public ManualTime Time { get; } = new();
        public ListLogger Log { get; } = new();
        public PlayerbotChat Chat { get; }

        public Rig(Action<PlayerbotChatOptions>? configure = null, bool model = true)
        {
            if (model) Options.Chat.Providers = [Model()];
            configure?.Invoke(Options.Chat);
            Options.Validate();
            Chat = new PlayerbotChat(Options, Client, new FakeBotChatEnvironment(new() { ["TEST_ANNOUNCE_KEY"] = Key }), Time, Log);
        }

        public PlayerbotChatSafetyOptions Safety => Options.Chat.Safety;

        public Task<BotChatReply?> AnswerAsync(BotChatAsk ask) => Chat.AnswerAsync(ask);

        /// <summary>Announce through the admission and the worker, and wait for the reply (or for the worker to give up: null).</summary>
        public async Task<BotChatReply?> AnnounceAsync(BotChatAsk ask)
        {
            BotChatStatus before = Chat.Status();
            Assert.Equal(BotChatAdmission.Queued, Chat.TryAnnounce(ask));
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (Chat.DrainReplies() is [var reply, ..]) return reply;
                if (Chat.Status().Dropped > before.Dropped) return null;
                await Task.Delay(10);
            }

            throw new TimeoutException("no reply and no drop");
        }

        public ValueTask DisposeAsync() => Chat.DisposeAsync();
    }

    private static BotChatAsk Announcement(string who = "Bob", string? sentence = null, string instruction = "Welcome Bob, who has just joined your guild Quiet Owls.")
        => new(ChatSamples.BotId, new ObjectGuid(0x7), ChatSamples.Persona(), new ObjectGuid(0x7), "Chatbot", ChatType.Guild,
            sentence ?? $"{who} joined the guild {Guild}.", FromMaster: false, InviteAllowed: false, SenderAccountId: 0,
            Topic: new BotChatTopic("guild.welcome", instruction, new Dictionary<string, string> { ["guild"] = Guild, ["who"] = who }, Guild));

    // --- admission ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnnouncementNeedsATopic()
    {
        await using var rig = new Rig();
        Assert.Throws<ArgumentException>(() => rig.Chat.TryAnnounce(ChatSamples.Ask("hello", channel: ChatType.Guild)));
        Assert.Throws<ArgumentNullException>(() => rig.Chat.TryAnnounce(null!));
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task ChatOff_TakesNoAnnouncement()
    {
        await using var rig = new Rig(chat => chat.Enabled = false);
        Assert.Equal(BotChatAdmission.Disabled, rig.Chat.TryAnnounce(Announcement()));
        Assert.Empty(rig.Client.Calls);
        Assert.Equal(0, rig.Chat.Status().Queued);
    }

    [Fact]
    public async Task AnAnnouncement_IsQueuedAndAnsweredByTheBuiltinTemplate_WithTheGuildItIsFor()
    {
        await using var rig = new Rig(model: false);
        BotChatReply reply = (await rig.AnnounceAsync(Announcement()))!;

        Assert.Equal((ChatType.Guild, Guild, "Builtin"), (reply.Channel, reply.Guild, reply.Provider));
        Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], line => line.Replace("{who}", "Bob").Replace("{guild}", Guild) == reply.Text);
        Assert.Null(reply.Command);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task WhenNoProviderHasBudgetLeft_TheAnnouncementIsRefused()
    {
        // The built-in provider is the only one; its hourly cap is the budget.
        await using var rig = new Rig(chat => chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Builtin, MaxRepliesPerHour = 1 }], model: false);
        Assert.NotNull(await rig.AnnounceAsync(Announcement()));
        Assert.Equal(BotChatAdmission.Budget, rig.Chat.TryAnnounce(Announcement()));
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAnnounce(Announcement()));
    }

    // --- screening ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFlaggedSentence_ReachesNoProvider_IsFlaggedWithoutAStrike_AndSaysNothing()
    {
        await using var rig = new Rig();
        Assert.Null(await rig.AnswerAsync(Announcement(sentence: "kys joined the guild.")));

        Assert.Empty(rig.Client.Calls);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal((BotChatFlagSource.Local, "Harassment"), (flag.Source, flag.Category));
        Assert.Equal("Chatbot", flag.Bot);

        // Nobody is cut off by it: the next clean announcement goes out, and no mute was queued.
        Assert.Empty(rig.Chat.DrainMutes());
        Assert.Equal("fake reply", (await rig.AnswerAsync(Announcement()))!.Text);
    }

    [Fact]
    public async Task WithScreeningOff_TheSameSentenceIsAnnounced()
    {
        // The control for the test above: the screening, not anything else, stops that sentence.
        await using var rig = new Rig(chat => chat.Safety.Enabled = false);
        Assert.Equal("fake reply", (await rig.AnswerAsync(Announcement(sentence: "kys joined the guild.")))!.Text);
        Assert.Empty(rig.Chat.Safety.Flags());
    }

    [Fact]
    public async Task AFlaggedBuiltinLine_IsDropped_BecauseItsNameWasFlagged()
    {
        await using var rig = new Rig(model: false);
        Assert.Null(await rig.AnswerAsync(Announcement(who: "kys now", sentence: "A new member joined the guild.")));

        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal((BotChatFlagSource.Output, "Harassment"), (flag.Source, flag.Category));
        Assert.Equal(1, rig.Chat.Status().Safety.OutputFlagged);
        Assert.Equal(0, rig.Chat.Status().Safety.Flagged); // the sentence was clean: no input flag, no strike
    }

    [Fact]
    public async Task AFlaggedModelLine_IsReplacedByTheBuiltinOne_OrDropped()
    {
        await using (var replace = new Rig())
        {
            replace.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"just kys\", \"intent\": \"none\"}"));
            BotChatReply reply = (await replace.AnswerAsync(Announcement()))!;
            Assert.Equal("Builtin", reply.Provider);
            Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], line => line.Replace("{who}", "Bob").Replace("{guild}", Guild) == reply.Text);
            Assert.Equal((BotChatFlagSource.Output, "just kys"), (replace.Chat.Safety.Flags()[0].Source, replace.Chat.Safety.Flags()[0].Excerpt));
        }

        await using var drop = new Rig(chat => chat.Safety.OnOutputFlagged = PlayerbotChatOutputAction.Drop);
        drop.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"just kys\", \"intent\": \"none\"}"));
        Assert.Null(await drop.AnswerAsync(Announcement()));
        Assert.Equal(1, drop.Chat.Status().Safety.OutputFlagged);
    }

    /// <summary>
    /// Output screening that runs out of time has not judged the line: the announcement path fails closed like the answer path
    /// (PlayerbotChat.cs answer screening, guild MOTD), so the model's line is never said and nothing is flagged for a
    /// personal-data guess made by a timeout.
    /// </summary>
    // A pattern timeout other than the default gives the rig a filter of its own: the test hook never reaches the shared default one.
    private static void OwnFilter(PlayerbotChatOptions chat) => chat.Safety.PatternTimeout = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task AModelLine_ThatTheOutputScreeningCouldNotJudge_IsNotSaid_AndLeavesNoFlag()
    {
        await using var rig = new Rig(OwnFilter);
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex slow = filter.AllPatterns.First();
        filter.TimeoutForTest = (pattern, text) => ReferenceEquals(pattern, slow) && text.Contains("fake", StringComparison.Ordinal);

        BotChatReply reply = (await rig.AnswerAsync(Announcement()))!;
        Assert.Equal("Builtin", reply.Provider);
        Assert.DoesNotContain("fake", reply.Text, StringComparison.Ordinal);
        Assert.True(rig.Chat.Status().Safety.TimedOut >= 1);

        // A personal-data pattern that timed out is no finding: no flag record with the model's text as its excerpt.
        await using var email = new Rig(OwnFilter);
        PlayerbotChatSafetyFilter emailFilter = email.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex pattern = emailFilter.AllPatterns.Last(p => p.ToString().Contains('@', StringComparison.Ordinal));
        emailFilter.TimeoutForTest = (p, text) => ReferenceEquals(p, pattern) && text.Contains("fake", StringComparison.Ordinal);
        BotChatReply other = (await email.AnswerAsync(Announcement()))!;
        Assert.Equal("Builtin", other.Provider);
        Assert.Empty(email.Chat.Safety.Flags());
    }

    [Fact]
    public async Task ABuiltinLine_ThatTheOutputScreeningCouldNotJudge_IsNotSaid()
    {
        await using var rig = new Rig(OwnFilter, model: false);
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex slow = filter.AllPatterns.First();
        filter.TimeoutForTest = (pattern, text) => ReferenceEquals(pattern, slow) && text.Contains("zed", StringComparison.Ordinal); // the screened forms are lower case

        Assert.Null(await rig.AnswerAsync(Announcement(who: "Zed", sentence: "A new member joined the guild.")));
        Assert.Empty(rig.Chat.Safety.Flags());
    }

    // --- models and RequireOptIn --------------------------------------------------------------------------------------

    [Fact]
    public async Task AModel_IsUsedWhileRequireOptInIsOff_WithTheTopicsPrompt()
    {
        await using var rig = new Rig();
        BotChatReply reply = (await rig.AnswerAsync(Announcement()))!;

        Assert.Equal("fake reply", reply.Text);
        Assert.Equal(Guild, reply.Guild);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Equal(Key, call.Key);
        Assert.Contains($"You are a member of the guild {Guild}", call.Prompt.DynamicSystem);
        Assert.Contains("Nobody spoke to you", call.Prompt.DynamicSystem);
        Assert.Equal("Welcome Bob, who has just joined your guild Quiet Owls.", Assert.Single(call.Prompt.Messages).Text);
    }

    [Fact]
    public async Task UnderRequireOptIn_NoModelIsAsked_AndTheBuiltinLineIsUsed()
    {
        await using var rig = new Rig(chat => chat.Safety.RequireOptIn = true);
        BotChatReply reply = (await rig.AnswerAsync(Announcement()))!;

        Assert.Equal("Builtin", reply.Provider);
        Assert.Empty(rig.Client.Calls);
        Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], line => line.Replace("{who}", "Bob").Replace("{guild}", Guild) == reply.Text);

        // Even a player who has opted in (their lines may use the model) does not change an announcement: nobody opted in to it.
        rig.Chat.TryAsk(ChatSamples.Ask("ai on", sender: 0x43, name: "Optin"));
        rig.Chat.DrainReplies();
        Assert.Equal("Builtin", (await rig.AnswerAsync(Announcement()))!.Provider);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task ATopicWhosePlaceholderHasNoValue_IsNotSaid()
    {
        // guild.welcome needs {who}: with no value for it no phrasing can be filled, and the built-in provider chooses silence.
        await using var rig = new Rig(model: false);
        BotChatAsk ask = Announcement() with { Topic = new BotChatTopic("guild.welcome", "Welcome.", new Dictionary<string, string> { ["guild"] = Guild }, Guild) };
        Assert.Null(await rig.AnswerAsync(ask));
    }
}
