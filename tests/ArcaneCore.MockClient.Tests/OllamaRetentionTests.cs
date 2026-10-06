using System.Net;
using System.Text;
using System.Text.Json;
using ArcaneCore.MockClient.Playbots;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class OllamaRetentionTests
{
    private static PlaybotDecisionContext Context => new(7, 10, 60, false,
        [new("7:observe", PlaybotActionKind.Observe, 0), new("7:stop", PlaybotActionKind.StopAttack, 100)]);

    [Fact]
    public async Task WarmAndDecisionUseTheSameShortRetentionAndPreserveBounds()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", http: http);

        Assert.True(await selector.WarmAsync(default));
        PlaybotSelection decision = await selector.SelectAsync(Context, default);

        Assert.Equal("ollama", decision.Provider);
        Assert.Equal(2, handler.Bodies.Count);
        JsonElement warm = handler.Bodies[0];
        JsonElement select = handler.Bodies[1];
        Assert.Equal("30s", warm.GetProperty("keep_alive").GetString());
        Assert.Equal("30s", select.GetProperty("keep_alive").GetString());
        Assert.False(warm.GetProperty("think").GetBoolean());
        Assert.False(select.GetProperty("think").GetBoolean());
        Assert.Equal(2048, warm.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(2048, select.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(1, warm.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(96, select.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            Bodies.Add(document.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":\"{\\\"action_id\\\":\\\"7:stop\\\"}\"}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
