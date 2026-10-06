using System.Net;
using System.Text;
using System.Text.Json;
using ArcaneCore.MockClient.Playbots;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class PlaybotSelectorTests
{
    private static PlaybotDecisionContext Context => new(7, 10, 60, false,
        [new("7:observe", PlaybotActionKind.Observe, 0), new("7:stop", PlaybotActionKind.StopAttack, 100)]);

    [Theory]
    [InlineData("http://localhost:11435/")]
    [InlineData("http://8.8.8.8:11435/")]
    [InlineData("http://127.0.0.1:11435/api/")]
    [InlineData("http://user:password@127.0.0.1:11435/")]
    [InlineData("http://127.0.0.1:11435/?secret=x")]
    [InlineData("https://127.0.0.1:11435/")]
    public void NonLiteralOrNonOriginEndpointsAreRejected(string endpoint)
        => Assert.Throws<ArgumentException>(() => OllamaPlaybotSelector.ValidateEndpoint(new Uri(endpoint)));

    [Theory]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://[::1]:11435/")]
    public void ExplicitLoopbackPortsAreAccepted(string endpoint)
        => OllamaPlaybotSelector.ValidateEndpoint(new Uri(endpoint));

    [Fact]
    public async Task SelectorAcceptsOnlyCurrentCandidateAndSendsBoundedNonThinkingSchema()
    {
        var handler = new ResponseHandler("{\"action_id\":\"7:stop\"}");
        using var http = new HttpClient(handler);
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", http: http);
        PlaybotSelection selection = await selector.SelectAsync(Context, default);
        Assert.Equal("ollama", selection.Provider);
        Assert.Equal("7:stop", selection.Candidate.Id);
        using JsonDocument body = JsonDocument.Parse(handler.Request!);
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(2048, body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(96, body.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Contains("7:stop", body.RootElement.GetProperty("format").GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Target", handler.Request!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"action_id\":\"6:stop\"}")]
    [InlineData("{\"action_id\":\".kill\"}")]
    [InlineData("{\"action_id\":\"7:stop\",\"command\":\".kill\"}")]
    [InlineData("{\"action_id\":\"7:stop\",\"action_id\":\"7:observe\"}")]
    [InlineData("not json")]
    public async Task InvalidStaleOrExtraFieldsFallBackWithoutIntroducingAnAction(string choice)
    {
        using var http = new HttpClient(new ResponseHandler(choice));
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", http: http);
        PlaybotSelection selection = await selector.SelectAsync(Context, default);
        Assert.Equal("deterministic", selection.Provider);
        Assert.Equal("7:stop", selection.Candidate.Id);
        Assert.NotNull(selection.FallbackReason);
    }

    [Fact]
    public async Task SlowProviderFallsBackButParentCancellationPropagates()
    {
        using var http = new HttpClient(new SlowHandler());
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b",
            TimeSpan.FromMilliseconds(20), http);
        PlaybotSelection result = await selector.SelectAsync(Context, default);
        Assert.Equal("decision-timeout", result.FallbackReason);
        using var parent = new CancellationTokenSource();
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selector.SelectAsync(Context, parent.Token));
    }

    [Fact]
    public async Task HugeResponseIsRejectedBeforeParsing()
    {
        using var http = new HttpClient(new ResponseHandler(new string('x', 20000)));
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", http: http);
        Assert.Equal("response-budget", (await selector.SelectAsync(Context, default)).FallbackReason);
    }

    private sealed class ResponseHandler(string choice) : HttpMessageHandler
    {
        internal string? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response = choice }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
