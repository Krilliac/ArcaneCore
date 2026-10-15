using System.Net;
using System.Text;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// H1 hardening: <c>bot-chat/client-timeout</c> and <c>bot-chat/client-error</c>. A failed provider call is reported once, by
/// <see cref="PlayerbotChat"/> (which knows the provider's place in the failover order); <see cref="HttpBotChatClient"/> reports nothing
/// itself. The tests drive the service over the real HTTP client with a fake handler, so one failing call is the whole chain. The ledger is
/// process-wide and the subject is the provider label, so each test uses a model name of its own.
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
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond());
        }
    }

    private static readonly BotChatPrompt Prompt = new("STATIC", "DYNAMIC", [new BotChatTurn(true, "hi")]);

    private const string OkBody = """
        {"id":"m","type":"message","role":"assistant","content":[{"type":"text","text":"{\"reply\":\"Hello.\",\"intent\":\"none\"}"}],
         "stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":3}}
        """;

    private sealed class Rig : IAsyncDisposable
    {
        public PlayerbotChat Chat { get; }

        public Rig(string model, IBotChatClient client, Action<PlayerbotChatOptions>? configure = null)
        {
            var options = new PlayerbotOptions();
            options.Chat.PerPlayerCooldownSeconds = 0;
            ChatSamples.NoScreeningDeadline(options.Chat);
            options.Chat.Providers = [Provider(model)];
            configure?.Invoke(options.Chat);
            options.Validate();
            Chat = new PlayerbotChat(options, client, new FakeBotChatEnvironment(new() { ["TEST_ANOMALY_KEY"] = "sk-anomaly" }), new ManualTime(), new ListLogger());
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

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limited")]
    [InlineData(HttpStatusCode.BadRequest, "rejected")]
    public async Task OneFailingHttpCall_IsReportedOnce_AsClientError_WithTheErrorClass(HttpStatusCode status, string errorClass)
    {
        string model = $"anomaly-one-{(int)status}";
        using AnomalyCapture capture = CaptureOf(model);
        var handler = new Handler(() => Status(status));
        using var client = new HttpBotChatClient(handler);
        await using var rig = new Rig(model, client);

        Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider); // the failure fell through to the next provider
        Assert.Equal(1, handler.Calls);

        Assert.Equal(1, capture.Count(ClientError));
        Assert.False(capture.Any(ClientTimeout), capture.Describe());
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains($"failed: {errorClass}", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneTimedOutHttpCall_IsReportedOnce_AsClientTimeout()
    {
        const string model = "anomaly-one-timeout";
        using AnomalyCapture capture = CaptureOf(model);
        var handler = new Handler(() => throw new TaskCanceledException());
        using var client = new HttpBotChatClient(handler);
        await using var rig = new Rig(model, client);

        Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider);
        Assert.Equal(1, handler.Calls);

        Assert.Equal(1, capture.Count(ClientTimeout));
        Assert.False(capture.Any(ClientError), capture.Describe());
        Assert.Contains("failed: timeout", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneConnectionFailure_IsReportedOnce_AndAGoodAnswerIsSilent()
    {
        const string broken = "anomaly-one-network";
        using AnomalyCapture failing = CaptureOf(broken);
        using (var client = new HttpBotChatClient(new Handler(() => throw new HttpRequestException("refused"))))
        {
            await using var rig = new Rig(broken, client);
            Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider);
        }

        Assert.Equal(1, failing.Count(ClientError));
        Assert.Contains("failed: network", failing.Notices.Single().Detail, StringComparison.Ordinal);

        const string fine = "anomaly-one-ok";
        using AnomalyCapture quiet = CaptureOf(fine);
        using (var client = new HttpBotChatClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") })))
        {
            await using var rig = new Rig(fine, client);
            Assert.Equal("Hello.", (await rig.AskAsync("hello")).Text);
        }

        Assert.False(quiet.Any(ClientTimeout) || quiet.Any(ClientError), quiet.Describe());
    }

    [Fact]
    public async Task TheHttpClientAlone_ReportsNothing_TheServiceIsTheSingleSource()
    {
        const string model = "anomaly-client-alone";
        using AnomalyCapture capture = CaptureOf(model);
        using var client = new HttpBotChatClient(new Handler(() => Status(HttpStatusCode.InternalServerError)));

        BotChatResult result = await client.CompleteAsync(Provider(model), "k", Prompt, CancellationToken.None);

        Assert.Equal(BotChatOutcome.Unavailable, result.Outcome);
        Assert.False(capture.Any(ClientError) || capture.Any(ClientTimeout), capture.Describe());
    }

    [Fact]
    public async Task Service_AHungProvider_FiresClientTimeout_Once()
    {
        const string model = "anomaly-svc-timeout";
        using AnomalyCapture capture = CaptureOf(model);
        var fake = new FakeBotChatClient();
        fake.Then((_, token) => FakeBotChatClient.HangAsync(token));
        await using var rig = new Rig(model, fake, chat => chat.TimeoutSeconds = 1);

        Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider);

        Assert.Equal(1, capture.Count(ClientTimeout));
        Assert.False(capture.Any(ClientError), capture.Describe());
    }

    [Fact]
    public async Task Service_AProviderThatReturnsNoReply_FiresClientError_Once()
    {
        const string model = "anomaly-svc-empty";
        using AnomalyCapture capture = CaptureOf(model);
        var fake = new FakeBotChatClient();
        fake.ThenReturn(BotChatResult.Fail(BotChatOutcome.Unavailable));
        await using var rig = new Rig(model, fake);

        Assert.Equal("Builtin", (await rig.AskAsync("hello")).Provider);

        Assert.Equal(1, capture.Count(ClientError));
        Assert.Contains("failed: unavailable", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }
}
