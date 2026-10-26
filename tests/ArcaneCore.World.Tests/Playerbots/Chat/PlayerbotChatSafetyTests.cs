using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.Kernel.Diagnostics;
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
            ChatSamples.NoScreeningDeadline(Options.Chat);
            if (model) Options.Chat.Providers = [Model()];
            configure?.Invoke(Options.Chat);
            Options.Validate();
            Chat = new PlayerbotChat(Options, Client, new FakeBotChatEnvironment(new() { ["TEST_SAFETY_KEY"] = Key, ["TEST_MODERATION_KEY"] = "mod-key" }), Time, Log);
        }

        public PlayerbotChatSafetyOptions Safety => Options.Chat.Safety;

        public Task<BotChatReply?> AnswerAsync(string text, ulong sender = 0x42, string name = "Testplayer", int account = 0)
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
        Assert.Equal((category, BotChatFlagSource.Local, "Testplayer", 0x42UL, 77, "Chatbot"), (flag.Category, flag.Source, flag.PlayerName, flag.PlayerGuid, flag.AccountId, flag.Bot));
        Assert.Contains(rig.Log.Lines, l => l.Contains($"flagged {category} from Testplayer", StringComparison.Ordinal));
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
        Assert.Contains(reply.Text!.Replace("Testplayer", "{player}", StringComparison.Ordinal), PlayerbotChatTemplates.Lines["safety.selfharm"]);
        Assert.Equal("SelfHarm", Assert.Single(rig.Chat.Safety.Flags()).Category);
        Assert.Equal("fake reply", (await rig.AnswerAsync("ok thanks"))!.Text); // not cut off
    }

    [Fact]
    public async Task PersonalData_IsKeptFromTheProvider_WithACaution_AndNoStrike()
    {
        await using var rig = new Rig(chat => chat.Safety.StrikesBeforeCutoff = 1);
        BotChatReply reply = (await rig.AnswerAsync("mail me at testplayer.w@example.com"))!;
        Assert.Empty(rig.Client.Calls);
        Assert.Contains(reply.Text!.Replace("Testplayer", "{player}", StringComparison.Ordinal), PlayerbotChatTemplates.Lines["safety.personal"]);
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
        Assert.Equal(1, rig.Chat.Safety.Player("Testplayer")!.Strikes);
        Assert.Equal("fake reply", (await rig.AnswerAsync("hello"))!.Text);

        await rig.AnswerAsync("kys");
        await rig.AnswerAsync("kys"); // the third within the window
        BotChatPlayerSafety standing = rig.Chat.Safety.Player("Testplayer")!;
        Assert.Equal(30 * 60, standing.CutoffSeconds);
        Assert.Contains(rig.Log.Lines, l => l.Contains("Testplayer (guid 66, account 0) is cut off from the model providers for 30 minutes after 3 strikes", StringComparison.Ordinal));

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
        Assert.True(rig.Chat.Safety.Pardon("testplayer"));
        Assert.False(rig.Chat.Safety.Pardon("testplayer"));
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
        Assert.Equal(new BotChatMute(501, 0x42, "Testplayer", 15), Assert.Single(rig.Chat.DrainMutes()));
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
        foreach (string leak in new[] { "example.com", "555", "7946", guid.ToString(), "1234567890", account.ToString(), "Testplayer" })
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
        Assert.True(rig.Chat.Safety.Player("Testplayer")!.CutoffSeconds > 0);
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
    public async Task ClassifyModeration_TurnsReasoningOffForALocalServer_AndKeepsAnExplicitChoice()
    {
        PlayerbotChatProviderOptions local = Model(PlayerbotChatModerationKind.Classify);
        local.Kind = PlayerbotChatProviderKind.OpenAICompatible;
        local.BaseUrl = "http://localhost:11434/v1";
        local.Model = "qwen3.5:4b";
        local.ApiKeyEnvironmentVariable = "";
        await using var rig = new Rig(chat => chat.Providers = [local]);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "SAFE"));
        await rig.AnswerAsync("hello");
        Assert.Equal("none", rig.Client.Calls.First().Provider.ReasoningEffort);

        local.ReasoningEffort = "low";
        await using var explicitRig = new Rig(chat => chat.Providers = [local]);
        explicitRig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "SAFE"));
        await explicitRig.AnswerAsync("hello");
        Assert.Equal("low", explicitRig.Client.Calls.First().Provider.ReasoningEffort);
    }

    [Fact]
    public async Task ClassifyModeration_SendsNoReasoningEffortToAHostedServer_WhenTheOptionIsEmpty()
    {
        await using var rig = new Rig(chat => chat.Providers = [Model(PlayerbotChatModerationKind.Classify)]);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "SAFE"));
        await rig.AnswerAsync("hello");
        Assert.Equal(string.Empty, rig.Client.Calls.First().Provider.ReasoningEffort);
    }

    [Fact]
    public async Task OmitNeverSendsReasoningEffort_NotEvenToTheLocalClassifier()
    {
        PlayerbotChatProviderOptions local = Model(PlayerbotChatModerationKind.Classify);
        local.BaseUrl = "http://localhost:11434/v1";
        local.ApiKeyEnvironmentVariable = "";
        local.ReasoningEffort = "omit";
        await using var rig = new Rig(chat => chat.Providers = [local]);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "SAFE"));
        await rig.AnswerAsync("hello");
        Assert.Equal(2, rig.Client.Calls.Count);
        foreach (var call in rig.Client.Calls)
            Assert.False(HttpBotChatClient.OpenAIBody(call.Provider, call.Prompt).ContainsKey("reasoning_effort"));
    }

    [Fact]
    public async Task AClassifierThatOnlyThinks_BacksTheProviderOffOnTheSecond_AndLogsTheHint()
    {
        await using var rig = new Rig(chat => chat.Providers = [Model(PlayerbotChatModerationKind.Classify)]);
        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.ReasoningOnly));
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello"))!.Provider);
        Assert.Contains(rig.Log.Lines, line => line.Contains("set ReasoningEffort=none for this provider"));
        Assert.Equal("reasoning-only", rig.Chat.Status().Providers[0].LastError);
        Assert.Equal(0, rig.Chat.Status().Providers[0].CooldownSeconds);

        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.ReasoningOnly));
        Assert.Equal("Builtin", (await rig.AnswerAsync("hello again"))!.Provider);
        Assert.True(rig.Chat.Status().Providers[0].CooldownSeconds > 0);

        Assert.Equal("Builtin", (await rig.AnswerAsync("and again"))!.Provider);
        Assert.Equal(2, rig.Client.Calls.Count); // cooling down: the model is not asked
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
            // Screen keeps its 100 ms per-pattern guard, so whether it reports TimedOut depends on how starved the CPU is (it
            // tripped on Node1 with seven suites running, load 29 on 14 cores) and is not asserted: tripping is the guard
            // working. Linear time is proven below on the same patterns with the guard removed, against a wall-clock bound.
            filter.Screen(input, PlayerbotChatSafetyCategories.All);
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

    [Fact]
    public async Task ThePatternTimeout_ReachesEveryPatternOfTheFilterInUse_ThePersonalDataOnesToo()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), new PlayerbotChatSafetyOptions().PatternTimeout);
        Assert.Equal(PlayerbotChatSafetyFilter.PatternTimeout, PlayerbotChatSafetyFilter.Default.MatchTimeout);

        await using var rig = new Rig(chat => chat.Safety.PatternTimeout = TimeSpan.FromMilliseconds(250));
        PlayerbotChatSafetyFilter custom = rig.Chat.Safety.Filter();
        Assert.Equal(TimeSpan.FromMilliseconds(250), custom.MatchTimeout);
        Assert.True(custom.AllPatterns.Count() > 3); // the shipped patterns and the three personal-data ones
        Assert.All(custom.AllPatterns, pattern => Assert.Equal(TimeSpan.FromMilliseconds(250), pattern.MatchTimeout));
        Assert.Same(custom, rig.Chat.Safety.Filter());

        // Live: the filter follows the option, and the default is the shared filter again.
        rig.Safety.PatternTimeout = System.Text.RegularExpressions.Regex.InfiniteMatchTimeout;
        Assert.All(rig.Chat.Safety.Filter().AllPatterns, pattern => Assert.Equal(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout, pattern.MatchTimeout));
        rig.Safety.PatternTimeout = PlayerbotChatSafetyFilter.PatternTimeout;
        Assert.Same(PlayerbotChatSafetyFilter.Default, rig.Chat.Safety.Filter());

        // Personal data is still screened and stripped through the filter in use.
        rig.Safety.PatternTimeout = TimeSpan.FromMilliseconds(250);
        Assert.Equal("mail me at [email]", rig.Chat.Safety.Filter().StripPersonalData("mail me at someone@example.com"));
        Assert.Equal(PlayerbotChatSafetyCategories.PersonalData, rig.Chat.Safety.ScreenInput("call 555-123-4567").Category);
    }

    [Fact]
    public async Task EveryFilterInUse_IsWarmed_EveryPatternOnce_TheDefaultOneAtStartAndAReloadedOne()
    {
        // At start: the chat's safety warms the filter it starts with (the shared default when the deadline is the default).
        await using (var plain = new Rig(chat => chat.Safety.PatternTimeout = PlayerbotChatSafetyFilter.PatternTimeout))
        {
            PlayerbotChatSafetyFilter shared = plain.Chat.Safety.Filter();
            Assert.Same(PlayerbotChatSafetyFilter.Default, shared);
            await shared.Warmed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(shared.AllPatterns.Count(), shared.WarmedPatterns);
        }

        // A filter built later (another deadline, an operator's file: what a reload does) is warmed when it is swapped in.
        string path = Path.Combine(Path.GetTempPath(), "arcane-safety-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "categories": { "Harassment": { "patterns": [ "\\bwarm(ed)? up\\b" ] } } }""");
        try
        {
            await using var rig = new Rig();
            PlayerbotChatSafetyFilter first = rig.Chat.Safety.Filter();
            Assert.True(first.WarmUpStarted); // started by the swap itself, not by the await below
            await first.Warmed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(first.AllPatterns.Count(), first.WarmedPatterns);

            rig.Safety.TermsFile = path;
            PlayerbotChatSafetyFilter reloaded = rig.Chat.Safety.Filter();
            Assert.NotSame(first, reloaded);
            Assert.True(reloaded.WarmUpStarted);
            await reloaded.Warmed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(reloaded.AllPatterns.Count(), reloaded.WarmedPatterns);
            Assert.Equal(first.AllPatterns.Count() + 1, reloaded.WarmedPatterns); // the operator's pattern too
            Assert.Same(reloaded.Warmed, reloaded.Warmed); // once per filter
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AScreeningOutOfTime_IsCounted_AndWarnedOncePerMinute_WithThePatternNeverTheLine()
    {
        var timeouts = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // By name: the counter may be published from its own static initializer, before the property is set.
            if (instrument.Meter.Name == "ArcaneCore.Playerbots.Chat" && instrument.Name == "arcanecore.playerbot.chat.screening_timeouts") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "kind") timeouts.Enqueue(tag.Value as string);
        });
        listener.Start();

        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter(); // this rig's own filter (no deadline), not the shared one
        Assert.NotSame(PlayerbotChatSafetyFilter.Default, filter);
        System.Text.RegularExpressions.Regex slow = filter.AllPatterns.First();
        filter.TimeoutForTest = (pattern, _) => ReferenceEquals(pattern, slow);
        static bool Warning(string line) => line.Contains("input screening ran out of time on ", StringComparison.Ordinal);

        BotChatReply reply = (await rig.AnswerAsync("hello there, meet me at the inn"))!;
        Assert.Equal("Builtin", reply.Provider); // not judged in time: kept from the model, no strike
        Assert.Empty(rig.Client.Calls);
        Assert.Empty(rig.Chat.Safety.Flags());
        string warning = Assert.Single(rig.Log.Lines, Warning);
        Assert.Matches(@"on [A-Za-z]+ pattern #\d+ \(1 so far, 0 more since the last warning\)$", warning);
        Assert.All(rig.Log.Lines, line => Assert.DoesNotContain("meet me", line));

        await rig.AnswerAsync("hello again");
        Assert.Single(rig.Log.Lines, Warning); // within the minute: counted, not logged
        rig.Time.Advance(PlayerbotChatSafety.TimeoutWarningInterval);
        await rig.AnswerAsync("and once more");
        Assert.Equal(2, rig.Log.Lines.Count(Warning));
        Assert.EndsWith("(3 so far, 1 more since the last warning)", rig.Log.Lines.Last(Warning));

        Assert.Equal(3, rig.Chat.Status().Safety.TimedOut);
        Assert.Contains(" timed-out=3 ", PlayerbotCommands.ChatStatusLines(rig.Chat.Status()).Last());
        Assert.True(timeouts.Count(kind => kind == "input") >= 3, $"input timeouts metered: {timeouts.Count(kind => kind == "input")}");

        // A personal-data pattern out of time is named as such (and the line is treated as personal data, as before).
        System.Text.RegularExpressions.Regex email = filter.AllPatterns.Last(pattern => pattern.ToString().Contains('@', StringComparison.Ordinal));
        filter.TimeoutForTest = (pattern, _) => ReferenceEquals(pattern, email);
        SafetyVerdict verdict = rig.Chat.Safety.ScreenInput("nothing personal here");
        Assert.Equal((PlayerbotChatSafetyCategories?)PlayerbotChatSafetyCategories.PersonalData, verdict.Category);
        Assert.Equal("personal data (email)", verdict.TimedOutOn);
        Assert.Equal(4, rig.Chat.Status().Safety.TimedOut);
    }

    /// <summary>The <c>kind</c> of every screening-timeout measurement while it lives (any test's: count with <c>&gt;=</c>).</summary>
    private sealed class TimeoutMeter : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();

        public TimeoutMeter()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "ArcaneCore.Playerbots.Chat" && instrument.Name == "arcanecore.playerbot.chat.screening_timeouts") l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (KeyValuePair<string, object?> tag in tags)
                    if (tag.Key == "kind") Kinds.Enqueue(tag.Value as string);
            });
            _listener.Start();
        }

        public System.Collections.Concurrent.ConcurrentQueue<string?> Kinds { get; } = new();

        public int Of(string kind) => Kinds.Count(k => k == kind);

        public void Dispose() => _listener.Dispose();
    }

    [Theory]
    [InlineData(PlayerbotChatOutputAction.BuiltinReply)]
    [InlineData(PlayerbotChatOutputAction.Drop)]
    public async Task AModelReplyWhoseScreeningRunsOutOfTime_IsNeverSaid_ItTakesTheBlockedReplyPath(PlayerbotChatOutputAction action)
    {
        using var meter = new TimeoutMeter();
        await using var rig = new Rig(chat => chat.Safety.OnOutputFlagged = action);
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex slow = filter.AllPatterns.First();
        // Only the model's reply ("fake reply") is out of time; the player's line is judged in time and goes to the model.
        filter.TimeoutForTest = (pattern, text) => ReferenceEquals(pattern, slow) && text.Contains("fake", StringComparison.Ordinal);

        BotChatReply? reply = await rig.AnswerAsync("hello there");

        Assert.Single(rig.Client.Calls); // the model was asked
        if (action == PlayerbotChatOutputAction.Drop) Assert.Null(reply);
        else
        {
            Assert.NotNull(reply);
            Assert.Equal("Builtin", reply.Provider);
            Assert.NotEqual("fake reply", reply.Text);
        }

        Assert.Empty(rig.Chat.Safety.Flags()); // nothing was found in it: no flag record, no strike
        Assert.Equal(1, rig.Chat.Status().Safety.TimedOut);
        Assert.Contains(rig.Log.Lines, line => line.Contains("output screening ran out of time on ", StringComparison.Ordinal));
        Assert.All(rig.Log.Lines, line => Assert.DoesNotContain("fake reply", line));
        Assert.True(meter.Of("output") >= 1, $"output timeouts metered: {meter.Of("output")}");
    }

    [Fact]
    public async Task APersonalDataPatternOutOfTimeOnAModelReply_BlocksTheReply_WithoutAFlagRecord()
    {
        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex email = filter.AllPatterns.Last(pattern => pattern.ToString().Contains('@', StringComparison.Ordinal));
        filter.TimeoutForTest = (pattern, text) => ReferenceEquals(pattern, email) && text.Contains("fake", StringComparison.Ordinal);

        BotChatReply reply = (await rig.AnswerAsync("hello there"))!;

        Assert.Single(rig.Client.Calls);
        Assert.Equal("Builtin", reply.Provider); // not said
        Assert.Empty(rig.Chat.Safety.Flags()); // a timeout is not a finding: no flag record, no excerpt of the model's text
        BotChatSafetyStatus status = rig.Chat.Status().Safety;
        Assert.Equal((0L, 0L, 1L), (status.Flagged, status.OutputFlagged, status.TimedOut));
        Assert.All(rig.Log.Lines, line => Assert.DoesNotContain("fake reply", line));
        Assert.Contains(rig.Log.Lines, line => line.Contains("output screening ran out of time on personal data (email)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APersonalDataPatternOutOfTimeOnAPlayersLine_IsStillFlaggedAsPersonalData_WithoutAStrike()
    {
        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex email = filter.AllPatterns.Last(pattern => pattern.ToString().Contains('@', StringComparison.Ordinal));
        filter.TimeoutForTest = (pattern, _) => ReferenceEquals(pattern, email);

        BotChatReply reply = (await rig.AnswerAsync("hello there"))!;

        Assert.Empty(rig.Client.Calls);
        Assert.Contains(PlayerbotChatTemplates.Lines["safety.personal"], line => line.Replace("{player}", "Testplayer", StringComparison.Ordinal) == reply.Text);
        BotChatFlag flag = Assert.Single(rig.Chat.Safety.Flags());
        Assert.Equal(("PersonalData", BotChatFlagSource.Local), (flag.Category, flag.Source));
        Assert.Equal("[removed]", flag.Excerpt); // the excerpt's strip ran out of time on the same pattern: fails closed
        Assert.Equal("Testplayer: no strikes, no cut-off.", PlayerbotCommands.ChatFlagLines(rig.Chat.Safety, "Testplayer").First());
        Assert.Equal(2, rig.Chat.Status().Safety.TimedOut); // the input screening and the excerpt's strip
    }

    [Fact]
    public async Task AWarmUpThatFails_StillCompletes_WarnsOnce_AndLinesAreScreenedCold()
    {
        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = PlayerbotChatSafetyFilter.Load(null, replaceDefaults: false, System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
        System.Text.RegularExpressions.Regex broken = filter.AllPatterns.First();
        filter.WarmUpHookForTest = pattern =>
        {
            if (ReferenceEquals(pattern, broken)) throw new InvalidOperationException("warm-up test failure kys");
        };

        await rig.Chat.Safety.Warm(filter).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(filter.Warmed.IsCompletedSuccessfully); // never faulted: a worker awaiting it goes on
        Assert.Equal(filter.AllPatterns.Count(), filter.WarmedPatterns); // the other patterns were still warmed
        Assert.Equal(nameof(InvalidOperationException), filter.WarmUpError);
        string warning = Assert.Single(rig.Log.Lines, line => line.Contains("warm-up failed", StringComparison.Ordinal));
        Assert.Contains("(InvalidOperationException)", warning);
        Assert.DoesNotContain("kys", warning);
        Assert.Equal(PlayerbotChatSafetyCategories.Harassment, filter.Screen("kys", PlayerbotChatSafetyCategories.All).Category); // screening works
    }

    [Fact]
    public async Task AWorker_ScreensNothing_UntilTheWarmUpOfTheFilterInUseIsDone()
    {
        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        var warmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filter.SetWarmedForTest(warmed.Task);

        Task<BotChatReply?> pending = rig.AnswerAsync("hello there");

        // AnswerAsync runs synchronously up to its first await: the pending warm-up. Nothing is screened or sent before it.
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, rig.Chat.Status().Safety.Screened);
        Assert.Empty(rig.Client.Calls);

        warmed.SetResult();
        Assert.Equal("fake reply", (await pending.WaitAsync(TimeSpan.FromSeconds(30)))!.Text);
        Assert.Equal(1, rig.Chat.Status().Safety.Screened);
    }

    [Fact]
    public async Task ATimeoutBeforeARealHit_IsStillCounted()
    {
        await using var rig = new Rig();
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex first = filter.AllPatterns.First(); // a category pattern, screened before personal data
        filter.TimeoutForTest = (pattern, _) => ReferenceEquals(pattern, first);

        SafetyVerdict verdict = rig.Chat.Safety.ScreenInput("mail me at someone@example.com");

        Assert.Equal((PlayerbotChatSafetyCategories?)PlayerbotChatSafetyCategories.PersonalData, verdict.Category); // a real hit
        Assert.True(verdict.TimedOut);
        Assert.Matches(@"^[A-Za-z]+ pattern #\d+$", verdict.TimedOutOn);
        Assert.Equal(1, rig.Chat.Status().Safety.TimedOut);
    }

    [Fact]
    public async Task APersonalDataStripOutOfTime_SendsRemoved_AndIsCountedAsStrip()
    {
        using var meter = new TimeoutMeter();
        // Personal data left out of the screening, so only the strip before sending runs the personal-data patterns.
        await using var rig = new Rig(chat => chat.Safety.Categories = PlayerbotChatSafetyCategories.All & ~PlayerbotChatSafetyCategories.PersonalData);
        PlayerbotChatSafetyFilter filter = rig.Chat.Safety.Filter();
        System.Text.RegularExpressions.Regex email = filter.AllPatterns.Last(pattern => pattern.ToString().Contains('@', StringComparison.Ordinal));
        filter.TimeoutForTest = (pattern, _) => ReferenceEquals(pattern, email);

        Assert.Equal("fake reply", (await rig.AnswerAsync("mail me at someone@example.com"))!.Text);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Contains(call.Prompt.Messages, message => message.Text.Contains("[removed]", StringComparison.Ordinal));
        Assert.DoesNotContain(call.Prompt.Messages, message => message.Text.Contains("someone@example.com", StringComparison.Ordinal));

        Assert.Equal(1, rig.Chat.Status().Safety.TimedOut);
        string warning = Assert.Single(rig.Log.Lines, line => line.Contains("strip screening ran out of time on ", StringComparison.Ordinal));
        Assert.Contains("personal data (email)", warning);
        Assert.All(rig.Log.Lines, line => Assert.DoesNotContain("someone@example.com", line));
        Assert.True(meter.Of("strip") >= 1, $"strip timeouts metered: {meter.Of("strip")}");
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
        Assert.StartsWith("Bot chat safety: screening=on screened=3 flagged=2 moderation-flagged=0 output-flagged=0 builtin-only=0 timed-out=0 cut-offs=1 cut-off-now=1", safety);
        string[] flags = [.. PlayerbotCommands.ChatFlagLines(rig.Chat.Safety, "testplayer")];
        Assert.Equal("Testplayer: strikes=0 cut-off=60m ai=default", flags[0]);
        Assert.Matches(@"^2026-10-08 12:00:00 Testplayer \(guid 66, account 12\) -> Chatbot: Harassment \(local filter\) ""kys, mail me at \[email\]""$", flags[1]);
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

    [Fact]
    public void SafetyFilter_RecordsFaultAndAnomaly_OnInvalidJsonAndRegexTimeout()
    {
        static long SiteCount() => FaultLedger.Shared.Snapshot()
            .Where(s => s.Site == "playerbot.chat-safety")
            .Sum(s => s.Count);

        string path = Path.Combine(Path.GetTempPath(), $"safety-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ broken json");
        try
        {
            long beforeFaults = SiteCount();
            using AnomalyCapture faultCapture = AnomalyLedger.Shared.Capture(
                n => n.Key == "bot-chat/safety-filter-fault" && n.Detail.Contains(path, StringComparison.Ordinal));
            PlayerbotChatSafetyFilter filter = PlayerbotChatSafetyFilter.Load(path, replaceDefaults: false);

            Assert.True(SiteCount() > beforeFaults);
            Assert.Equal(1, faultCapture.Count("bot-chat/safety-filter-fault"));

            long beforeTimeoutFaults = SiteCount();
            using AnomalyCapture timeoutCapture = AnomalyLedger.Shared.Capture(
                n => n.Key == "bot-chat/safety-regex-timeout" && n.Detail == "personal data (email)");
            filter.TimeoutForTest = (_, _) => true;
            Assert.Equal("[removed]", filter.StripPersonalData("hello@example.com", out string? timedOut));
            Assert.Equal("personal data (email)", timedOut);
            Assert.True(SiteCount() > beforeTimeoutFaults);
            Assert.Equal(1, timeoutCapture.Count("bot-chat/safety-regex-timeout"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
