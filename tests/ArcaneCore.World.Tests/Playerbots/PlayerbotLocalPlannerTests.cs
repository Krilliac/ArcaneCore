using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotLocalPlannerTests
{
    [Fact]
    public async Task DisabledPlannerDoesNotCallHttp()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException());
        await using var planner = new PlayerbotLocalPlanner(new PlayerbotOptions { Enabled = true, AllowLocalLlm = false }, handler);
        Assert.Null(await planner.SelectAsync(Facts(), Candidates()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RequestIsBoundedAndReturnsOnlyKnownCandidate()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Json("{\"done\":true,\"message\":{\"content\":\"{\\\"id\\\":\\\"combat\\\"}\"}}")));
        await using var planner = new PlayerbotLocalPlanner(Options(), handler);
        Assert.Equal("combat", await planner.SelectAsync(Facts(), Candidates()));
        string body = handler.Requests.Single();
        Assert.Contains("127.0.0.1", handler.Urls.Single());
        using JsonDocument request = JsonDocument.Parse(body);
        Assert.Equal("R4C3R/qwen3-0.6b-heretic:q4_k_m", request.RootElement.GetProperty("model").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(1024, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(96, request.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(2, request.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", request.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.DoesNotContain("account", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("character", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownOrOversizedResponseFailsClosed()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Json("{\"done\":true,\"message\":{\"content\":\"{\\\"id\\\":\\\"other\\\"}\"}}")));
        await using var planner = new PlayerbotLocalPlanner(Options(), handler);
        Assert.Null(await planner.SelectAsync(Facts(), Candidates()));

        handler.Response = _ => Task.FromResult(Json("{\"done\":true,\"message\":{\"content\":\"" + new string('x', 5000) + "\"}}"));
        Assert.Null(await planner.SelectAsync(Facts(), Candidates()));
    }

    [Fact]
    public async Task BusyPlannerFailsClosed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async _ => { await gate.Task; return Json("{\"done\":true,\"message\":{\"content\":\"{\\\"id\\\":\\\"combat\\\"}\"}}" ); });
        await using var planner = new PlayerbotLocalPlanner(Options(), handler);
        Task<string?> first = planner.SelectAsync(Facts(), Candidates());
        await WaitForAsync(() => handler.Requests.Count == 1);
        Assert.Null(await planner.SelectAsync(Facts(), Candidates()));
        gate.SetResult();
        Assert.Equal("combat", await first);
    }

    private static PlayerbotOptions Options() => new()
    {
        Enabled = true, AllowLocalLlm = true, LocalLlmModel = "R4C3R/qwen3-0.6b-heretic:q4_k_m",
        LocalLlmContextSize = 1024, LocalLlmTimeoutMs = 5000,
    };

    private static PlayerbotPlannerFacts Facts() => new(10, 80, 100, false, 0, 12, 3);
    private static IReadOnlyList<PlayerbotPlanCandidate> Candidates() =>
        [new("rest", PlayerbotGoalKind.Rest, 117, 0), new("combat", PlayerbotGoalKind.Combat, 123, 0)];

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Response { get; set; } = response;
        public List<string> Requests { get; } = [];
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_gate) { Requests.Add(body); Urls.Add(request.RequestUri!.ToString()); }
            return await Response(request);
        }
    }
}
