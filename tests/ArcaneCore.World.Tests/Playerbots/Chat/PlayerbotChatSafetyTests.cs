using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Bot chat safety with a fake transport and a manual clock (no network, no waits): a flagged line never reaches a provider, model
/// replies are screened, strikes cut a player off and decay, the automatic mute, the disclosure once per login, "ai off" / "ai on"
/// and RequireOptIn, what a request carries, the moderation step, and the local filter itself (variants, false positives, ReDoS).
/// Replies are asked of <see cref="PlayerbotChat.AnswerAsync"/> directly, so no worker or wall clock is involved.
/// </summary>
public sealed class PlayerbotChatSafetyTests
{
    private const string Key = "sk-safety-test";

    private static PlayerbotChatProviderOptions Model(PlayerbotChatModerationKind moderation = PlayerbotChatModerationKind.None) => new()
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://api.example.test/v1", Model = "test-model",
        ApiKeyEnvironmentVariable = "TEST_SAFETY_KEY", InputUsdPerMillionTokens = 0, OutputUsdPerMillionTokens = 0, Moderation = moderation,
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
            Options.Chat.PerPlayerCooldownSeconds = 0;
            if (model) Options.Chat.Providers = [Model()];
            configure?.Invoke(Options.Chat);
            Options.Validate();
            Chat = new PlayerbotChat(Options, Client, new FakeBotChatEnvironment(new() { ["TEST_SAFETY_KEY"] = Key, ["TEST_MODERATION_KEY"] = "mod-key" }), Time, Log);
        }

        public PlayerbotChatSafetyOptions Safety => Options.Chat.Safety;

        public Task<BotChatReply?> AnswerAsync(string text, ulong sender = 0x42, string name = "Nathan", int account = 0)
            => Chat.AnswerAsync(ChatSamples.Ask(text, sender: sender, name: name, account: account));

        public ValueTask DisposeAsync() => Chat.DisposeAsync();
    }

    // --- pre-send screening -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kys", "Harassment")]
    [InlineData("k y s loser", "Harassment")]
    [InlineData("K.Y.S", "Harassment")]
    [InlineData("go kill yourselfff", "Harassment")]
    [InlineData("k1ll y0urs3lf", "Harassment")]
    [InlineData("I know where you live", "Threats")]
    [InlineData("heil hitler", "Hate")]
    [InlineData("how do i make napalm", "IllegalGoods")]
    [InlineData("anyone selling cocaine", "IllegalGoods")]
    public async Task AFlaggedLine_NeverReachesTheProvider_AndGetsTheBuiltinBrushOff(string line, string category)
    {
        await using var rig = new Rig();
        BotChatReply reply = (await rig.AnswerAsync(line, account: 77))!;

        Assert.Empty(rig.Client.Calls);
        Assert.Equal("Builtin", reply.Provider);
        Assert.Contains(reply.Text, PlayerbotChatTemplates.Lines["abuse"]);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal((category, BotChatFlagSource.Local, "Nathan", 0x42UL, 77, "Chatbot"), (flag.Category, flag.Source, flag.PlayerName, flag.PlayerGuid, flag.AccountId, flag.Bot));
        Assert.Contains(rig.Log.Lines, l => l.Contains($"flagged {category} from Nathan", StringComparison.Ordinal));
        Assert.Equal(1, rig.Chat.Status().Safety.Flagged);
    }

    [Fact]
    public async Task WithScreeningOff_TheSameLineWouldReachTheProvider()
    {
        // The control for the test above: the screening, not anything else, keeps the line away from the provider.
        await using var rig = new Rig(chat => chat.Safety.Enabled = false);
        Assert.Equal("fake reply", (await rig.AnswerAsync("kys"))!.Text);
        Assert.Equal("kys", Assert.Single(rig.Client.Calls).Prompt.Messages[^1].Text);
        Assert.Empty(rig.Chat.Safety.Flags());
    }

    [Fact]
    public async Task OnFlaggedIgnore_AnswersNothing_AndAFlaggedLineIsNeverRemembered()
    {
        await using var rig = new Rig(chat => chat.Safety.OnFlagged = PlayerbotChatFlaggedAction.Ignore);
        Assert.Null(await rig.AnswerAsync("kill yourself"));
        Assert.Empty(rig.Client.Calls);

        // The next clean line goes out without the flagged one in its memory.
        Assert.Equal("fake reply", (await rig.AnswerAsync("where are you?"))!.Text);
        Assert.Equal(["where are you?"], Assert.Single(rig.Client.Calls).Prompt.Messages.Select(m => m.Text));
    }

    [Theory]
    [InlineData("let's kill the boars and die trying")]
    [InlineData("suicide pull the next pack, I'll res")]
    [InlineData("my hunter needs a better gun")]
    [InlineData("I'm naked lol, repair bill")]
    [InlineData("throw a bomb at the boss")]
    [InlineData("that kid in goldshire is annoying")]
    [InlineData("I have 1500 gold and 12 silver")]
    [InlineData("you are a noob")]
    public async Task OrdinaryGameTalk_IsNotFlagged(string line)
    {
        await using var rig = new Rig();
        Assert.Equal("fake reply", (await rig.AnswerAsync(line))!.Text);
        Assert.Empty(rig.Chat.Safety.Flags());
    }

    [Fact]
    public async Task SelfHarm_GetsASupportiveLine_AndNoStrike()
    {
        await using var rig = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 1);
        BotChatReply reply = (await rig.AnswerAsync("i want to kill myself"))!;
        Assert.Empty(rig.Client.Calls);
        Assert.Contains(reply.Text!.Replace("Nathan", "{player}", StringComparison.Ordinal), PlayerbotChatTemplates.Lines["safety.selfharm"]);
        Assert.Equal("SelfHarm", Assert.Single(rig.Chat.Safety.Flags()).Category);
        Assert.Equal("fake reply", (await rig.AnswerAsync("ok thanks"))!.Text); // not cut off
    }

    [Fact]
    public async Task PersonalData_IsKeptFromTheProvider_WithACaution_AndNoStrike()
    {
        await using var rig = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 1);
        BotChatReply reply = (await rig.AnswerAsync("mail me at nathan.w@example.com"))!;
        Assert.Empty(rig.Client.Calls);
        Assert.Contains(reply.Text!.Replace("Nathan", "{player}", StringComparison.Ordinal), PlayerbotChatTemplates.Lines["safety.personal"]);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal("PersonalData", flag.Category);
        Assert.Equal("mail me at [email]", flag.Excerpt); // the excerpt itself never holds the address
        await rig.AnswerAsync("call me 555-123-4567");
        Assert.Equal("PersonalData", rig.Chat.Safety.Flags()[0].Category);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);
    }

    [Fact]
    public async Task StoreExcerptOff_KeepsNoText()
    {
        await using var rig = new Rig(chat => chat.Safety.StoreExcerpt = false);
        await rig.AnswerAsync("kys you idiot");
        Assert.Null(Assert.Single(rig.Chat.Safety.Flags()).Excerpt);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("idiot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFlagRing_IsBounded()
    {
        await using var rig = new Rig(chat => { chat.Safety.FlagLogSize = 3; chat.Safety.StrikesBeforeCutoff = 100; });
        for (int i = 0; i < 5; i++) await rig.AnswerAsync("kys " + i);
        Assert.Equal(["kys 4", "kys 3", "kys 2"], rig.Chat.Safety.Flags().Select(f => f.Excerpt));
    }

    // --- output screening -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFlaggedModelReply_IsNotSaid_TheBuiltinAnswersInstead()
    {
        await using var rig = new Rig();
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"just kys\", \"intent\": \"none\"}"));
        BotChatReply reply = (await rig.AnswerAsync("what level are you?"))!;

        Assert.Equal("Builtin", reply.Provider);
        Assert.Matches(@"\b7\b", reply.Text);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal((BotChatFlagSource.Output, "Harassment", "just kys"), (flag.Source, flag.Category, flag.Excerpt));
        Assert.Equal(1, rig.Chat.Status().Safety.OutputFlagged);
        Assert.Equal(0, rig.Chat.Status().Safety.Flagged); // the player did nothing wrong: no strike
    }

    [Fact]
    public async Task AFlaggedModelReply_IsDropped_WhenSoConfigured()
    {
        await using var rig = new Rig(chat => chat.Safety.OnOutputFlagged = PlayerbotChatOutputAction.Drop);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"email me at bot@example.com\", \"intent\": \"none\"}"));
        Assert.Null(await rig.AnswerAsync("hi"));
        Assert.Equal("PersonalData", Assert.Single(rig.Chat.Safety.Flags()).Category);
    }

    [Fact]
    public async Task WithOutputScreeningOff_TheModelReplyIsSaid()
    {
        await using var rig = new Rig(chat => chat.Safety.ScreenOutput = false);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"just kys\", \"intent\": \"none\"}"));
        Assert.Equal("just kys", (await rig.AnswerAsync("hi"))!.Text);
    }

    // --- strikes, cut-off, decay, mute ------------------------------------------------------------------------------------

    [Fact]
    public async Task Strikes_CutAPlayerOff_TheyDecay_AndTheCutoffEnds()
    {
        await using var rig = new Rig(chat => { chat.Safety.StrikesBeforeCutoff = 3; chat.Safety.StrikeWindowMinutes = 60; chat.Safety.CutoffMinutes = 30; });
        await rig.AnswerAsync("kys");
        await rig.AnswerAsync("kys");
        rig.Time.Advance(TimeSpan.FromMinutes(61)); // both strikes fall away
        await rig.AnswerAsync("kys");
        Assert.Equal(1, rig.Chat.Safety.Player("Nathan")!.Strikes);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);

        await rig.AnswerAsync("kys");
        await rig.AnswerAsync("kys"); // the third within the window
        BotChatPlayerSafety standing = rig.Chat.Safety.Player("Nathan")!;
        Assert.Equal(30 * 60, standing.CutoffSeconds);
        Assert.Contains(rig.Log.Lines, l => l.Contains("Nathan (guid 66, account 0) is cut off from the model providers for 30 minutes after 3 strikes", StringComparison.Ordinal));

        int calls = rig.Client.Calls.Count;
        BotChatReply cut = (await rig.AnswerAsync("what level are you?"))!;
        Assert.Equal("Builtin", cut.Provider);
        Assert.Equal(calls, rig.Client.Calls.Count);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello", sender: 0x43, name: "Other"))!.Text); // only that player
        BotChatSafetyStatus status = rig.Chat.Status().Safety;
        Assert.Equal((1, 1L), (status.CutOffNow, status.Cutoffs));

        rig.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello again"))!.Text);
        Assert.Equal(0, rig.Chat.Status().Safety.CutOffNow);
    }

    [Fact]
    public async Task APardon_ClearsStrikesAndTheCutoff()
    {
        await using var rig = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 1);
        await rig.AnswerAsync("kys");
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello"))!.Provider);
        Assert.True(rig.Chat.Safety.Pardon("nathan"));
        Assert.False(rig.Chat.Safety.Pardon("nathan"));
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);
    }

    [Fact]
    public async Task AutoMute_IsQueuedForTheWorldThread_OnlyWhenEnabled()
    {
        await using (var off = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 2))
        {
            await off.AnswerAsync("kys", account: 501);
            await off.AnswerAsync("kys", account: 501);
            Assert.Empty(off.Chat.DrainMutes());
        }

        await using var rig = new Rig(chat => { chat.Safety.StrikesBeforeCutoff = 2; chat.Safety.AutoMute = true; chat.Safety.AutoMuteMinutes = 15; });
        await rig.AnswerAsync("kys", account: 501);
        Assert.Empty(rig.Chat.DrainMutes());
        await rig.AnswerAsync("kys", account: 501);
        Assert.Equal(new BotChatMute(501, 0x42, "Nathan", 15), Assert.Single(rig.Chat.DrainMutes()));
        Assert.Empty(rig.Chat.DrainMutes());
        Assert.Equal(1, rig.Chat.Status().Safety.AutoMutes);
    }

    // --- disclosure and choice --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDisclosure_IsShownOncePerLogin_OnlyWithAModelProvider()
    {
        await using var rig = new Rig();
        BotChatAsk ask = ChatSamples.Ask("hello");
        string notice = rig.Chat.TakeDisclosure(ask, login: 1)!;
        Assert.StartsWith(rig.Safety.DisclosureText, notice);
        Assert.Contains("\"ai off\"", notice);
        Assert.Null(rig.Chat.TakeDisclosure(ask, login: 1));
        Assert.Null(rig.Chat.TakeDisclosure(ChatSamples.Ask("hi", bot: Guid.NewGuid()), login: 1)); // another bot, same login
        Assert.NotNull(rig.Chat.TakeDisclosure(ask, login: 2)); // the next login
        Assert.NotNull(rig.Chat.TakeDisclosure(ChatSamples.Ask("hi", sender: 0x43), login: 2));

        rig.Safety.Disclosure = false;
        Assert.Null(rig.Chat.TakeDisclosure(ask, login: 3));

        await using var builtinOnly = new Rig(model: false);
        Assert.Null(builtinOnly.Chat.TakeDisclosure(ask, login: 1));
    }

    [Fact]
    public void LoginOf_IsStablePerObject_AndNewForTheNext()
    {
        object first = new(), second = new();
        Assert.Equal(PlayerbotChatSafety.LoginOf(first), PlayerbotChatSafety.LoginOf(first));
        Assert.NotEqual(PlayerbotChatSafety.LoginOf(first), PlayerbotChatSafety.LoginOf(second));
    }

    [Fact]
    public async Task AiOff_LimitsThePlayerToBuiltinReplies_AndAiOn_UndoesIt()
    {
        await using var rig = new Rig();
        Assert.Equal(BotChatAdmission.Choice, rig.Chat.TryAsk(ChatSamples.Ask("AI off!")));
        BotChatReply confirm = Assert.Single(rig.Chat.DrainReplies());
        Assert.Equal(("Safety", ChatType.Whisper), (confirm.Provider, confirm.Channel));
        Assert.Contains("built-in replies only", confirm.Text);

        Assert.Equal("Builtin", (await rig.AnswerAsync("what level are you?"))!.Provider);
        Assert.Empty(rig.Client.Calls);
        Assert.Null(rig.Chat.TakeDisclosure(ChatSamples.Ask("hi"), login: 9)); // opted out: no notice
        Assert.Equal(1, rig.Chat.Status().Safety.OptedOut);

        Assert.Equal(BotChatAdmission.Choice, rig.Chat.TryAsk(ChatSamples.Ask("ai on")));
        Assert.Contains("may now come from an AI service", Assert.Single(rig.Chat.DrainReplies()).Text);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);

        // Not a choice on a party line, and not with the commands off.
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("Chatbot ai off", channel: ChatType.Party)));
        rig.Safety.AllowOptOut = false;
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("ai off", sender: 0x50)));
    }

    [Fact]
    public async Task RequireOptIn_UsesTheModelsOnlyForPlayersWhoSaidAiOn()
    {
        await using var rig = new Rig(chat => chat.Safety.RequireOptIn = true);
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello"))!.Provider);
        Assert.Empty(rig.Client.Calls);
        string notice = rig.Chat.TakeDisclosure(ChatSamples.Ask("hello"), login: 1)!;
        Assert.Contains("\"ai on\"", notice);

        rig.Chat.TryAsk(ChatSamples.Ask("ai"));
        Assert.Contains("built-in replies only", Assert.Single(rig.Chat.DrainReplies()).Text);
        rig.Chat.TryAsk(ChatSamples.Ask("ai on"));
        rig.Chat.DrainReplies();
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello", sender: 0x43, name: "Other"))!.Provider);
        Assert.Equal(1, rig.Chat.Status().Safety.OptedIn);
    }

    // --- what is sent -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRequest_CarriesOnlyTheLineAndMemory_UnderTheCharacterName_WithoutPersonalData()
    {
        const ulong guid = 0x1234567890;
        const int account = 987_654;
        await using var rig = new Rig(chat =>
        {
            chat.MemoryExchanges = 1;
            chat.Safety.Categories = PlayerbotChatSafetyCategories.All & ~PlayerbotChatSafetyCategories.PersonalData; // let it reach the strip
        });
        await rig.AnswerAsync("my old number was +44 20 7946 0958", sender: guid, name: "Thrall", account: account);
        await rig.AnswerAsync("first", sender: guid, name: "Thrall", account: account);
        await rig.AnswerAsync("write to thrall.real@example.com or call (555) 123-4567", sender: guid, name: "Thrall", account: account);

        BotChatPrompt prompt = rig.Client.Calls.ToArray()[^1].Prompt;
        Assert.Equal(["first", "fake reply", "write to [email] or call [phone]"], prompt.Messages.Select(m => m.Text));
        string body = HttpBotChatClient.OpenAIBody(Model(), prompt).ToJsonString() + HttpBotChatClient.AnthropicBody(Model(), prompt).ToJsonString();
        Assert.Contains("speaking with Thrall by whisper", body);
        foreach (string leak in new[] { "example.com", "555", "7946", guid.ToString(), "1234567890", account.ToString(), "Nathan" })
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my old number", body); // two exchanges back: past MemoryExchanges
    }

    [Fact]
    public async Task WithStrippingOff_TheLineGoesAsTyped()
    {
        await using var rig = new Rig(chat =>
        {
            chat.Safety.StripPersonalData = false;
            chat.Safety.Categories = PlayerbotChatSafetyCategories.All & ~PlayerbotChatSafetyCategories.PersonalData;
        });
        await rig.AnswerAsync("write to me@example.com");
        Assert.Equal("write to me@example.com", Assert.Single(rig.Client.Calls).Prompt.Messages[^1].Text);
    }

    // --- moderation -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEndpointModerationFlag_KeepsTheLineFromEveryProvider_AndIsAStrike()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Model(PlayerbotChatModerationKind.Endpoint), Model()]; chat.Safety.StrikesBeforeCutoff = 1; });
        rig.Client.Moderation = _ => new BotChatModeration(BotChatModerationVerdict.Flagged, "harassment/threatening");
        BotChatReply reply = (await rig.AnswerAsync("something the local list does not know"))!;

        Assert.Empty(rig.Client.Calls);
        Assert.Equal(("something the local list does not know", (string?)Key), Assert.Single(rig.Client.Moderations));
        Assert.Contains(reply.Text, PlayerbotChatTemplates.Lines["abuse"]);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal((BotChatFlagSource.Moderation, "harassment/threatening"), (flag.Source, flag.Category));
        Assert.True(rig.Chat.Safety.Player("Nathan")!.CutoffSeconds > 0);
    }

    [Fact]
    public async Task AFailedModeration_SkipsThatProvider_AndACleanOneLetsItAnswer()
    {
        await using var rig = new Rig(chat =>
        {
            var moderated = Model(PlayerbotChatModerationKind.Endpoint);
            moderated.ModerationBaseUrl = "https://moderation.example.test/v1";
            moderated.ModerationApiKeyEnvironmentVariable = "TEST_MODERATION_KEY";
            chat.Providers = [moderated];
        });
        rig.Client.Moderation = _ => new BotChatModeration(BotChatModerationVerdict.Failed);
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello"))!.Provider);
        Assert.Empty(rig.Client.Calls);
        Assert.Equal("moderation-failed", rig.Chat.Status().Providers[0].LastError);

        rig.Client.Moderation = _ => new BotChatModeration(BotChatModerationVerdict.Clean);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello again"))!.Text);
        Assert.Equal("mod-key", rig.Client.Moderations.ToArray()[^1].Key);
    }

    [Fact]
    public async Task ClassifyModeration_AsksTheProviderFirst_AndUnsafeFlags()
    {
        await using var rig = new Rig(chat => chat.Providers = [Model(PlayerbotChatModerationKind.Classify)]);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "UNSAFE"));
        Assert.Equal("Builtin", (await rig.AnswerAsync("a line the list misses"))!.Provider);
        var classify = Assert.Single(rig.Client.Calls);
        Assert.Equal(PlayerbotChatPrompts.ClassifierSystem, classify.Prompt.StaticSystem);
        Assert.Equal(16, classify.Provider.MaxTokens);
        Assert.Equal("classified-unsafe", Assert.Single(rig.Chat.Safety.Flags()).Category);

        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "SAFE"));
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);
        Assert.Equal(3, rig.Client.Calls.Count);
    }

    [Fact]
    public async Task TheModerationStep_RunsOnlyWithScreeningOn()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Model(PlayerbotChatModerationKind.Endpoint)]; chat.Safety.Enabled = false; });
        rig.Client.Moderation = _ => new BotChatModeration(BotChatModerationVerdict.Flagged);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);
        Assert.Empty(rig.Client.Moderations);
    }

    [Fact]
    public async Task TheHttpModerationRequest_HasTheModerationsShape()
    {
        string body = string.Empty;
        Uri? uri = null;
        string? auth = null;
        var handler = new RecordingHandler(request =>
        {
            uri = request.RequestUri;
            auth = request.Headers.Authorization?.ToString();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return """{"id":"modr-1","results":[{"flagged":true,"categories":{"hate":false,"harassment":true},"category_scores":{}}]}""";
        });
        using var client = new HttpBotChatClient(handler);
        PlayerbotChatProviderOptions provider = Model(PlayerbotChatModerationKind.Endpoint);

        BotChatModeration verdict = await client.ModerateAsync(provider, "sk-mod", "some line", CancellationToken.None);

        Assert.Equal(new BotChatModeration(BotChatModerationVerdict.Flagged, "harassment"), verdict);
        Assert.Equal("https://api.example.test/v1/moderations", uri!.ToString());
        Assert.Equal("Bearer sk-mod", auth);
        using JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("omni-moderation-latest", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("some line", json.RootElement.GetProperty("input").GetString());
        Assert.Equal(BotChatModerationVerdict.Clean, HttpBotChatClient.ParseModeration("""{"results":[{"flagged":false}]}"""u8.ToArray()).Verdict);
        Assert.Equal(BotChatModerationVerdict.Failed, HttpBotChatClient.ParseModeration("""{"error":"x"}"""u8.ToArray()).Verdict);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    // --- the filter -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheShippedList_LoadsCompletely_EveryPatternLinear()
    {
        PlayerbotChatSafetyFilter filter = PlayerbotChatSafetyFilter.Default;
        Assert.Empty(filter.Problems);
        using Stream stream = typeof(PlayerbotChatSafetyFilter).Assembly.GetManifestResourceStream(PlayerbotChatSafetyFilter.ResourceName)!;
        using JsonDocument json = JsonDocument.Parse(stream);
        int patterns = json.RootElement.GetProperty("categories").EnumerateObject().Sum(c => c.Value.GetProperty("patterns").GetArrayLength());
        Assert.Equal(patterns, filter.Size.Patterns);
        Assert.True(filter.Size.Terms > 50);
    }

    [Fact]
    public void Screening_LongAndHostileInput_TakesLinearTime()
    {
        PlayerbotChatSafetyFilter filter = PlayerbotChatSafetyFilter.Default;
        string[] inputs =
        [
            new string('a', 1_000_000),
            string.Concat(Enumerable.Repeat("kid ", 250_000)),
            string.Concat(Enumerable.Repeat("12 yo ", 100_000)) + "!",
            string.Concat(Enumerable.Repeat("a.", 300_000)) + "@",
            string.Concat(Enumerable.Repeat("1 ", 300_000)),
            string.Concat(Enumerable.Repeat("i will find ", 80_000)),
        ];
        var clock = Stopwatch.StartNew();
        foreach (string input in inputs)
        {
            // Screen cuts a line at MaxScreenedLength, so every pattern (personal data too) also runs over the whole input, untimed.
            Assert.False(filter.Screen(input, PlayerbotChatSafetyCategories.All).TimedOut);
            foreach (System.Text.RegularExpressions.Regex pattern in filter.AllPatterns) Untimed(pattern).IsMatch(input);
        }

        // Even an operator's textbook catastrophic pattern runs in linear time (NonBacktracking).
        string path = Path.Combine(Path.GetTempPath(), "arcane-safety-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "categories": { "Harassment": { "patterns": [ "^(a+)+b$", "(x|x|xx)*y" ] } } }""");
        try
        {
            PlayerbotChatSafetyFilter hostile = PlayerbotChatSafetyFilter.Load(path, replaceDefaults: true);
            Assert.Empty(hostile.Problems);
            foreach (System.Text.RegularExpressions.Regex pattern in hostile.AllPatterns)
            {
                Assert.DoesNotMatch(Untimed(pattern), new string('a', 200_000));
                Assert.DoesNotMatch(Untimed(pattern), new string('x', 200_000));
            }
        }
        finally
        {
            File.Delete(path);
        }

        // Generous for a loaded machine; a backtracking blow-up on any of these takes minutes, not seconds.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"screening took {clock.Elapsed}");
    }

    /// <summary>The same pattern without its match timeout, so the test measures the engine, not the guard.</summary>
    private static System.Text.RegularExpressions.Regex Untimed(System.Text.RegularExpressions.Regex pattern)
    {
        Assert.True(pattern.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking), pattern.ToString());
        return new System.Text.RegularExpressions.Regex(pattern.ToString(), pattern.Options);
    }

    [Fact]
    public void AnOperatorFile_AddsTerms_SkipsPatternsThatCannotRunLinearly_AndCanReplaceTheShippedList()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-safety-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            { "categories": {
                "Harassment": { "terms": [ "gnomes smell", "zug*" ], "patterns": [ "(a+)\\1", "\\bbad (troll|orc) line\\b" ] },
                "Nonsense": { "terms": [ "x" ] } } }
            """);
        try
        {
            PlayerbotChatSafetyFilter added = PlayerbotChatSafetyFilter.Load(path, replaceDefaults: false);
            Assert.Equal(2, added.Problems.Count); // the back-reference and the unknown category
            Assert.Equal(PlayerbotChatSafetyCategories.Harassment, added.Screen("ugh, GNOMES SMELL", PlayerbotChatSafetyCategories.All).Category);
            Assert.Equal(PlayerbotChatSafetyCategories.Harassment, added.Screen("zugzug", PlayerbotChatSafetyCategories.All).Category);
            Assert.Equal(PlayerbotChatSafetyCategories.Harassment, added.Screen("a bad orc line", PlayerbotChatSafetyCategories.All).Category);
            Assert.Equal(PlayerbotChatSafetyCategories.Hate, added.Screen("heil hitler", PlayerbotChatSafetyCategories.All).Category);

            PlayerbotChatSafetyFilter replaced = PlayerbotChatSafetyFilter.Load(path, replaceDefaults: true);
            Assert.False(replaced.Screen("heil hitler", PlayerbotChatSafetyCategories.All).Flagged);
            Assert.True(replaced.Screen("zug", PlayerbotChatSafetyCategories.All).Flagged);

            PlayerbotChatSafetyFilter missing = PlayerbotChatSafetyFilter.Load(path + ".missing", replaceDefaults: true);
            Assert.Contains("not found", Assert.Single(missing.Problems));
            Assert.True(missing.Screen("heil hitler", PlayerbotChatSafetyCategories.All).Flagged); // the shipped list stays
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheTermsFile_IsLive_AndReReadWhenItChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-safety-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "categories": { "Harassment": { "terms": [ "first banned phrase" ] } } }""");
        try
        {
            await using var rig = new Rig();
            Assert.Equal("fake reply", (await rig.AnswerAsync("first banned phrase"))!.Text);
            rig.Safety.TermsFile = path; // what a reload does
            Assert.Equal("Builtin", (await rig.AnswerAsync("first banned phrase"))!.Provider);

            File.WriteAllText(path, """{ "categories": { "Harassment": { "terms": [ "second banned phrase" ] } } }""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            rig.Time.Advance(PlayerbotChatSafety.TermsFileCheck);
            Assert.Equal("Builtin", (await rig.AnswerAsync("second banned phrase"))!.Provider);
            Assert.Equal("fake reply", (await rig.AnswerAsync("first banned phrase"))!.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ACategoryLeftOut_IsNotScreened()
    {
        await using var rig = new Rig(chat => chat.Safety.Categories = PlayerbotChatSafetyCategories.Hate);
        Assert.Equal("fake reply", (await rig.AnswerAsync("kys"))!.Text);
        Assert.Equal("Builtin", (await rig.AnswerAsync("heil hitler"))!.Provider);
    }

    // --- status and options -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheStatusAndFlagLines_ShowTheCounts_AndNeverAnAddress()
    {
        await using var rig = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 2);
        await rig.AnswerAsync("kys", account: 12);
        await rig.AnswerAsync("kys, mail me at a@b.example.org", account: 12);
        await rig.AnswerAsync("hello", sender: 0x43, name: "Other");

        string safety = PlayerbotCommands.ChatStatusLines(rig.Chat.Status()).Last();
        Assert.StartsWith("Bot chat safety: screening=on screened=3 flagged=2 moderation-flagged=0 output-flagged=0 builtin-only=0 cut-offs=1 cut-off-now=1", safety);
        string[] flags = [.. PlayerbotCommands.ChatFlagLines(rig.Chat.Safety, "nathan")];
        Assert.Equal("Nathan: strikes=0 cut-off=60m ai=default", flags[0]);
        Assert.Matches(@"^2026-10-08 12:00:00 Nathan \(guid 66, account 12\) -> Chatbot: Harassment \(local filter\) ""kys, mail me at \[email\]""$", flags[1]);
        Assert.All(flags, line => Assert.DoesNotContain("b.example.org", line));
        Assert.Equal(["Other: no strikes, no cut-off.", "No flagged lines."], PlayerbotCommands.ChatFlagLines(rig.Chat.Safety, "Other"));
    }

    [Fact]
    public void TheSafetyOptions_AreValidated()
    {
        static void Bad(Action<PlayerbotChatSafetyOptions> change)
        {
            var options = new PlayerbotOptions();
            change(options.Chat.Safety);
            Assert.Throws<InvalidOperationException>(options.Validate);
        }

        Bad(s => s.StrikesBeforeCutoff = 0);
        Bad(s => s.StrikeWindowMinutes = 0);
        Bad(s => s.CutoffMinutes = 10_081);
        Bad(s => s.AutoMuteMinutes = 0);
        Bad(s => s.FlagLogSize = -1);
        Bad(s => s.DisclosureText = "two|parts");
        Bad(s => s.DisclosureText = new string('x', 201));
        Bad(s => s.Categories = (PlayerbotChatSafetyCategories)1024);
        Bad(s => s.OnFlagged = (PlayerbotChatFlaggedAction)9);
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.Anthropic, Moderation = PlayerbotChatModerationKind.Endpoint }] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.Anthropic, Moderation = PlayerbotChatModerationKind.Endpoint, ModerationBaseUrl = "http://example.com/v1" }] } }.Validate());
        new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.Anthropic, Moderation = PlayerbotChatModerationKind.Endpoint, ModerationBaseUrl = "https://api.openai.com/v1", ModerationApiKeyEnvironmentVariable = "OPENAI_API_KEY" }] } }.Validate();

        var defaults = new PlayerbotChatSafetyOptions();
        Assert.True(defaults.Enabled && defaults.ScreenOutput && defaults.Disclosure && defaults.AllowOptOut && defaults.StripPersonalData && defaults.StoreExcerpt);
        Assert.False(defaults.AutoMute || defaults.RequireOptIn || defaults.ReplaceDefaultTerms);
        Assert.Equal(PlayerbotChatSafetyCategories.All, defaults.Categories);
        Assert.Equal(PlayerbotChatModerationKind.None, new PlayerbotChatProviderOptions().Moderation);
    }
}
