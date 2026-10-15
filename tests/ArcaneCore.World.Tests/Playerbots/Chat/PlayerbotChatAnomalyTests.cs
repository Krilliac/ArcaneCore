using System.Net;
using System.Text;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// H1 hardening: <c>bot-chat/client-timeout</c> and <c>bot-chat/client-error</c>, reported by <see cref="HttpBotChatClient"/> (the wire call ended
/// in anything but Ok) and by <see cref="PlayerbotChat"/> (a provider failed and the next one answers). The ledger is process-wide and the
/// subject is the provider label, so each test uses a model name of its own and captures only that subject.
/// </summary>
public sealed class PlayerbotChatAnomalyTests
{
    private static readonly string ClientTimeout = PlayerbotAnomalies.Key("chat", "client-timeout");
    private static readonly string ClientError = PlayerbotAnomalies.Key("chat", "client-error");

    private static PlayerbotChatProviderOptions Provider(string model) => new()
    {
        Kind = PlayerbotChatProviderKind.Anthropic, Model = model, ApiKeyEnvironmentVariable = "TEST_ANOMALY_KEY", MaxRepliesPerHour = 120,
    };

    private static AnomalyCapture CaptureOf(string model)
        => AnomalyLedger.Shared.Capture(n => (n.Key == ClientTimeout || n.Key == ClientError) && n.Subject.Contains(model, StringComparison.Ordinal));

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private static readonly BotChatPrompt Prompt = new("STATIC", "DYNAMIC", [new BotChatTurn(true, "hi")]);

    private const string OkBody = """
        {"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"{\"reply\":\"Hello.\",\"intent\":\"none\"}"}],
         "stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":3}}
        """;

    [Fact]
    public async Task HttpClient_ACancelledRequest_FiresClientTimeout_OnceForTheCall()
    {
        const string model = "anomaly-http-timeout";
        using AnomalyCapture capture = CaptureOf(model);
        using var client = new HttpBotChatClient(new Handler(() => throw new TaskCanceledException()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        BotChatResult result = await client.CompleteAsync(Provider(model), "k", Prompt, cancelled.Token);

        Assert.Equal(BotChatOutcome.Timeout, result.Outcome);
        Assert.Equal(1, capture.Count(ClientTimeout));
        Assert.False(capture.Any(ClientError), capture.Describe());
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Equal("bot-chat", notice.Subsystem);
        Assert.Contains("ended with Timeout", notice.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "Unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "RateLimited")]
    [InlineData(HttpStatusCode.BadRequest, "Rejected")]
    public async Task HttpClient_AnErrorStatus_FiresClientError_WithTheOutcomeInTheDetail(HttpStatusCode status, string outcome)
    {
        string model = $"anomaly-http-{(int)status}";
        using AnomalyCapture capture = CaptureOf(model);
        using var client = new HttpBotChatClient(new Handler(() => new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));

        BotChatResult result = await client.CompleteAsync(Provider(model), "k", Prompt, CancellationToken.None);

        Assert.Equal(outcome, result.Outcome.ToString());
        Assert.Equal(1, capture.Count(ClientError));
        Assert.False(capture.Any(ClientTimeout), capture.Describe());
        Assert.Contains($"ended with {outcome}", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClient_AConnectionFailure_FiresClientError_AndAGoodAnswerIsSilent()
    {
        const string broken = "anomaly-http-network";
        using AnomalyCapture failing = CaptureOf(broken);
        using (var client = new HttpBotChatClient(new Handler(() => throw new HttpRequestException("refused"))))
            Assert.Equal(BotChatOutcome.NetworkError, (await client.CompleteAsync(Provider(broken), "k", Prompt, CancellationToken.None)).Outcome);
        Assert.Equal(1, failing.Count(ClientError));
        Assert.Contains("ended with NetworkError", failing.Notices.Single().Detail, StringComparison.Ordinal);

        const string fine = "anomaly-http-ok";
        using AnomalyCapture quiet = CaptureOf(fine);
        using (var client = new HttpBotChatClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") })))
            Assert.Equal(BotChatOutcome.Ok, (await client.CompleteAsync(Provider(fine), "k", Prompt, CancellationToken.None)).Outcome);
        Assert.False(quiet.Any(ClientTimeout) || quiet.Any(ClientError), quiet.Describe());
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeBotChatClient Client { get; } = new();
        public ManualTime Time { get; } = new();
        public PlayerbotChat Chat { get; }

        public Rig(string model, Action<PlayerbotChatOptions>? configure = null)
        {
            var options = new PlayerbotOptions();
            options.Chat.PerPlayerCooldownSeconds = 0;
            ChatSamples.NoScreeningDeadline(options.Chat);
            options.Chat.Providers = [Provider(model)];
            configure?.Invoke(options.Chat);
            options.Validate();
            Chat = new PlayerbotChat(options, Client, new FakeBotChatEnvironment(new() { ["TEST_ANOMALY_KEY"] = "sk-anomaly" }), Time, new ListLogger());
        }

        public async Task<BotChatReply> AskAsync(string text)
        {
            Assert.Equal(BotChatAdmission.Queued, Chat.TryAsk(ChatSamples.Ask(text)));
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (Chat.DrainReplies() is [var reply, ..]) return reply;
                await Task.Delay(10);
            }

            throw new TimeoutException("no reply");
        }

        public ValueTask DisposeAsync() => Chat.DisposeAsync();
    }

    [Fact]
    public async Task Service_AHungProvider_FiresClientTimeout_OnceAndTheBuiltinAnswers()
    {
        const string model = "anomaly-svc-timeout";
        using AnomalyCapture capture = CaptureOf(model);
        await using var rig = new Rig(model, chat => chat.TimeoutSeconds = 1);
        rig.Client.Then((_, token) => FakeBotChatClient.HangAsync(token));

        BotChatReply reply = await rig.AskAsync("hello");

        Assert.Equal("Builtin", reply.Provider);
        Assert.Equal(1, capture.Count(ClientTimeout));
        Assert.False(capture.Any(ClientError), capture.Describe());
        Assert.Contains("failed: timeout", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_AFailingProvider_FiresClientError_WithTheErrorClass_AndAHealthyOneIsSilent()
    {
        const string model = "anomaly-svc-error";
        using AnomalyCapture capture = CaptureOf(model);
        await using (var rig = new Rig(model))
        {
            rig.Client.ThenReturn(BotChatResult.Fail(BotChatOutcome.Unavailable));
            Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider);
        }

        Assert.Equal(1, capture.Count(ClientError));
        Assert.False(capture.Any(ClientTimeout), capture.Describe());
        Assert.Contains("failed: unavailable", capture.Notices.Single().Detail, StringComparison.Ordinal);

        const string fine = "anomaly-svc-fine";
        using AnomalyCapture quiet = CaptureOf(fine);
        await using var healthy = new Rig(fine);
        Assert.Equal("fake reply", (await healthy.AskAsync("hello")).Text);
        Assert.False(quiet.Any(ClientTimeout) || quiet.Any(ClientError), quiet.Describe());
    }
}
