using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// The chat service with a fake transport (no network): admission (cooldown, caps, queue), provider order and failover, the timeout,
/// the built-in fallback, memory, the spend cap, and that a key never reaches a log line or the status.
/// </summary>
public sealed class PlayerbotChatServiceTests
{
    private const string Secret = "sk-test-SECRET-0123456789";

    private static PlayerbotChatProviderOptions Anthropic(int perHour = 120) => new()
    {
        Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_ANTHROPIC_KEY", MaxRepliesPerHour = perHour,
    };

    private static PlayerbotChatProviderOptions Local(int perHour = 120) => new()
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "http://localhost:11434/v1", Model = "qwen3:0.6b",
        ApiKeyEnvironmentVariable = "", MaxRepliesPerHour = perHour, InputUsdPerMillionTokens = 0, OutputUsdPerMillionTokens = 0,
    };

    private sealed class Rig : IAsyncDisposable
    {
        public PlayerbotOptions Options { get; } = new();
        public FakeBotChatClient Client { get; } = new();
        public FakeBotChatEnvironment Environment { get; } = new(new() { ["TEST_ANTHROPIC_KEY"] = Secret });
        public ManualTime Time { get; } = new();
        public ListLogger Log { get; } = new();
        public PlayerbotChat Chat { get; }

        public Rig(Action<PlayerbotChatOptions>? configure = null)
        {
            Options.Chat.PerPlayerCooldownSeconds = 0;
            configure?.Invoke(Options.Chat);
            Options.Validate();
            Chat = new PlayerbotChat(Options, Client, Environment, Time, Log);
        }

        /// <summary>Ask and wait for the worker's reply (or for it to give up: null).</summary>
        public async Task<BotChatReply?> AskAsync(BotChatAsk ask)
        {
            BotChatStatus before = Chat.Status();
            Assert.Equal(BotChatAdmission.Queued, Chat.TryAsk(ask));
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

    [Fact]
    public async Task ByDefault_OnlyTheBuiltinProviderAnswers_WithNoNetwork()
    {
        await using var rig = new Rig();
        Assert.True(rig.Chat.IsActive);
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("what level are you?")))!;
        Assert.Equal("Builtin", reply.Provider);
        Assert.Matches(@"\b7\b", reply.Text);
        Assert.Empty(rig.Client.Calls);
        Assert.Equal(PlayerbotChatProviderKind.Builtin, Assert.Single(rig.Chat.Status().Providers).Kind);
    }

    [Fact]
    public async Task Disabled_TakesNoLine()
    {
        await using var rig = new Rig(chat => chat.Enabled = false);
        Assert.False(rig.Chat.IsActive);
        Assert.Equal(BotChatAdmission.Disabled, rig.Chat.TryAsk(ChatSamples.Ask("hello")));
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task AChannelLeftOut_IsNotAnswered()
    {
        await using var rig = new Rig(chat => chat.Channels = PlayerbotChatChannels.Whisper);
        Assert.Equal(BotChatAdmission.Channel, rig.Chat.TryAsk(ChatSamples.Ask("Chatbot hi", channel: ChatType.Say)));
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("hi")));
    }

    [Fact]
    public async Task AModelProvider_AnswersFirst_WithTheKeyFromTheEnvironment_AndTheCachedPrompt()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic()]);
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("hello there")))!;
        Assert.Equal("fake reply", reply.Text);
        Assert.Equal("Anthropic claude-haiku-4-5", reply.Provider);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Equal(Secret, call.Key);
        Assert.Equal(PlayerbotChatPrompts.StaticSystem, call.Prompt.StaticSystem);
        Assert.Contains("Chatbot, a level 7 male human warrior", call.Prompt.DynamicSystem);
        Assert.Contains("Elwynn Forest (Northshire Valley)", call.Prompt.DynamicSystem);
        Assert.Contains("Wolves Across the Border", call.Prompt.DynamicSystem);
        Assert.Equal("hello there", Assert.Single(call.Prompt.Messages).Text);
    }

    [Fact]
    public async Task AProviderWithoutItsKey_IsSkipped_AndAKeylessLocalProviderNeedsNone()
    {
        await using var rig = new Rig(chat => chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_MISSING_KEY" }, Local()]);
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("hello")))!;
        Assert.Equal("OpenAICompatible qwen3:0.6b", reply.Provider);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Null(call.Key);
        BotChatProviderStatus missing = rig.Chat.Status().Providers[0];
        Assert.False(missing.KeyPresent);
        Assert.Equal("TEST_MISSING_KEY", missing.KeyVariable);
        Assert.Null(rig.Chat.Status().Providers[1].KeyVariable);
    }

    [Fact]
    public async Task ARateLimitedProvider_FailsOverToTheNext_AndCoolsDown()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic(), Local()]);
        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.RateLimited, TimeSpan.FromSeconds(30)));
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("hello")))!;
        Assert.Equal("OpenAICompatible qwen3:0.6b", reply.Provider);
        BotChatProviderStatus first = rig.Chat.Status().Providers[0];
        Assert.Equal("rate-limited", first.LastError);
        Assert.Equal(30, first.CooldownSeconds);

        // While it cools down the next provider answers without asking it.
        Assert.Equal("OpenAICompatible qwen3:0.6b", (await rig.AskAsync(ChatSamples.Ask("hello again")))!.Provider);
        Assert.Equal(3, rig.Client.Calls.Count);
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("Anthropic claude-haiku-4-5", (await rig.AskAsync(ChatSamples.Ask("and again")))!.Provider);
    }

    [Fact]
    public async Task WhenEveryModelProviderFails_TheBuiltinOneAnswers()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic(), Local()]);
        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.Unavailable));
        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.NetworkError));
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("where are you?")))!;
        Assert.Equal("Builtin", reply.Provider);
        Assert.Contains("Elwynn Forest", reply.Text);
        BotChatStatus status = rig.Chat.Status();
        Assert.Equal("unavailable", status.Providers[0].LastError);
        Assert.Equal("network", status.Providers[1].LastError);
        Assert.Equal(1, status.Providers[2].Replies);
    }

    [Fact]
    public async Task AHungRequest_TimesOut_AndTheNextProviderAnswers()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Anthropic()]; chat.TimeoutSeconds = 1; });
        rig.Client.Then((_, token) => FakeBotChatClient.HangAsync(token));
        var started = DateTime.UtcNow;
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("hello")))!;
        Assert.Equal("Builtin", reply.Provider);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(15));
        Assert.Equal("timeout", rig.Chat.Status().Providers[0].LastError);
    }

    [Fact]
    public async Task AnUnreadableOrEmptyAnswer_FailsOver()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic()]);
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"broken"));
        BotChatReply reply = (await rig.AskAsync(ChatSamples.Ask("hello")))!;
        Assert.Equal("Builtin", reply.Provider);
        Assert.Equal("empty-reply", rig.Chat.Status().Providers[0].LastError);
    }

    [Fact]
    public async Task ThePerPlayerCooldown_HoldsAcrossBots_AndExpires()
    {
        await using var rig = new Rig(chat => chat.PerPlayerCooldownSeconds = 10);
        Assert.NotNull(await rig.AskAsync(ChatSamples.Ask("hello")));
        Assert.Equal(BotChatAdmission.Cooldown, rig.Chat.TryAsk(ChatSamples.Ask("hello", bot: Guid.NewGuid())));
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("hello", sender: 0x43)));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("hello")));
    }

    [Fact]
    public async Task TheHourlyCap_PassesToTheNextProvider_AndAtTheBuiltinCapNothingIsTaken()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic(perHour: 1), new PlayerbotChatProviderOptions { MaxRepliesPerHour = 1 }]);
        Assert.Equal("Anthropic claude-haiku-4-5", (await rig.AskAsync(ChatSamples.Ask("one")))!.Provider);
        Assert.Equal("Builtin", (await rig.AskAsync(ChatSamples.Ask("two")))!.Provider);
        Assert.Equal(BotChatAdmission.Budget, rig.Chat.TryAsk(ChatSamples.Ask("three")));
        BotChatStatus status = rig.Chat.Status();
        Assert.Equal(1, status.Providers[0].RepliesThisHour);
        Assert.Equal(1, status.Providers[1].RepliesThisHour);
        rig.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Anthropic claude-haiku-4-5", (await rig.AskAsync(ChatSamples.Ask("four")))!.Provider);
    }

    [Fact]
    public async Task TheDailySpendCap_StopsThePricedProviders_UntilTheNextUtcDay()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Anthropic(), Local()]; chat.MaxDailySpendUsd = 0.01; });
        // Claude Haiku 4.5 at $1/$5 per million: 5000 in + 1000 out = $0.01.
        rig.Client.ThenReturn(new BotChatResult(BotChatOutcome.Ok, "{\"reply\":\"pricey\",\"intent\":\"none\"}", 5000, 1000));
        Assert.Equal("Anthropic claude-haiku-4-5", (await rig.AskAsync(ChatSamples.Ask("one")))!.Provider);
        Assert.Equal(0.01, rig.Chat.Status().SpentTodayUsd, 6);
        Assert.Equal("OpenAICompatible qwen3:0.6b", (await rig.AskAsync(ChatSamples.Ask("two")))!.Provider); // a free local model is not capped
        rig.Time.Advance(TimeSpan.FromHours(12));
        Assert.Equal("Anthropic claude-haiku-4-5", (await rig.AskAsync(ChatSamples.Ask("three")))!.Provider);
    }

    [Fact]
    public async Task TheConversation_IsRemembered_ForTheSamePlayerOnly()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Anthropic()]; chat.MemoryExchanges = 1; });
        await rig.AskAsync(ChatSamples.Ask("first"));
        await rig.AskAsync(ChatSamples.Ask("second"));
        await rig.AskAsync(ChatSamples.Ask("third"));
        await rig.AskAsync(ChatSamples.Ask("stranger", sender: 0x99));
        var calls = rig.Client.Calls.ToArray();
        Assert.Equal(["second", "fake reply", "third"], calls[2].Prompt.Messages.Select(m => m.Text));
        Assert.Equal([true, false, true], calls[2].Prompt.Messages.Select(m => m.FromPlayer));
        Assert.Equal("stranger", Assert.Single(calls[3].Prompt.Messages).Text);
    }

    [Fact]
    public async Task AModelsOrder_IsKeptOnlyForTheMaster()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic()]);
        rig.Client.Default = (_, _) => Task.FromResult(new BotChatResult(BotChatOutcome.Ok, "Sure! {\"reply\": \"On my way.\", \"intent\": \"follow\"}"));
        BotChatReply master = (await rig.AskAsync(ChatSamples.Ask("could you tag along", fromMaster: true)))!;
        Assert.Equal(PlayerbotPartyCommand.Follow, master.Command);
        Assert.Equal("On my way.", master.Text);
        Assert.False(master.AcknowledgeCommand);
        BotChatReply other = (await rig.AskAsync(ChatSamples.Ask("could you tag along", sender: 0x55)))!;
        Assert.Null(other.Command);
    }

    [Fact]
    public async Task TheKey_NeverAppearsInALogLineOrTheStatus()
    {
        await using var rig = new Rig(chat => chat.Providers = [Anthropic(), Local()]);
        rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.Unauthorized));
        rig.Client.Then((_, _) => throw new InvalidOperationException("transport failed for key " + Secret));
        rig.Chat.LogConfiguration();
        Assert.Contains(rig.Log.Lines, line => line.Contains("key TEST_ANTHROPIC_KEY present", StringComparison.Ordinal));
        await rig.AskAsync(ChatSamples.Ask("hello"));
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        await rig.AskAsync(ChatSamples.Ask("hello again"));

        Assert.NotEmpty(rig.Log.Lines);
        Assert.All(rig.Log.Lines, line => Assert.DoesNotContain(Secret, line));
        string[] status = [.. PlayerbotCommands.ChatStatusLines(rig.Chat.Status())];
        Assert.All(status, line => Assert.DoesNotContain(Secret, line));
        Assert.Contains(status, line => line.Contains("key=TEST_ANTHROPIC_KEY:present", StringComparison.Ordinal));
        Assert.Contains(status, line => line.Contains("last-error=unauthorized", StringComparison.Ordinal));
        Assert.Contains(status, line => line.StartsWith("Bot chat: enabled=yes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFullQueue_TakesNoMore()
    {
        await using var rig = new Rig(chat => { chat.Providers = [Anthropic()]; chat.MaxQueuedRequests = 1; });
        var gate = new TaskCompletionSource();
        rig.Client.Default = async (_, token) => { await gate.Task.WaitAsync(token); return new BotChatResult(BotChatOutcome.Ok, "fine"); };
        // The two workers each take one line and hold it; the third waits in the queue, so the fourth finds it full.
        for (int i = 1; i <= PlayerbotChat.Workers; i++)
        {
            Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("busy", sender: (ulong)i)));
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (rig.Client.Calls.Count < i && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.Equal(i, rig.Client.Calls.Count);
        }

        Assert.Equal(BotChatAdmission.Queued, rig.Chat.TryAsk(ChatSamples.Ask("waits", sender: 10)));
        Assert.Equal(BotChatAdmission.QueueFull, rig.Chat.TryAsk(ChatSamples.Ask("full", sender: 11)));
        Assert.Equal(1, rig.Chat.Status().Queued);
        gate.SetResult();
    }

    [Fact]
    public void TheOptions_AreValidated()
    {
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.OpenAICompatible, Model = "m" }] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "http://example.com/v1", Model = "m", ApiKeyEnvironmentVariable = "OPENAI_API_KEY" }] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new(), Anthropic()] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { Providers = [new() { Kind = PlayerbotChatProviderKind.Anthropic, Headers = { ["x-api-key"] = "no" } }] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOptions { Chat = { TimeoutSeconds = 0 } }.Validate());
        new PlayerbotOptions { Chat = { Providers = [Local(), Anthropic(), new()] } }.Validate();
        Assert.Equal(PlayerbotChatProviderKind.Builtin, new PlayerbotChatOptions().EffectiveProviders().Single().Kind);
        Assert.True(new PlayerbotChatOptions().Enabled);
    }
}
