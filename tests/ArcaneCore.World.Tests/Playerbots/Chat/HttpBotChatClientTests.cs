using System.Net;
using System.Text;
using System.Text.Json;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>The wire shapes of the two model APIs, against a fake HTTP handler (no network).</summary>
public sealed class HttpBotChatClientTests
{
    private static readonly BotChatPrompt Prompt = new("STATIC RULES", "DYNAMIC FACTS",
        [new BotChatTurn(true, "hi"), new BotChatTurn(false, "hello"), new BotChatTurn(true, "where are you?")]);

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, Body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Anthropic_SendsTheMessagesApiShape_AndReadsTheReplyAndUsage()
    {
        var handler = new FakeHandler((_, _) => Json("""
            {"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"{\"reply\":\"In Goldshire.\",\"intent\":\"none\"}"}],
             "stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":14,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}
            """));
        using var client = new HttpBotChatClient(handler);
        var provider = new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, Headers = { ["X-Title"] = "ArcaneCore" } };

        BotChatResult result = await client.CompleteAsync(provider, "sk-ant-test", Prompt, CancellationToken.None);

        Assert.Equal(BotChatOutcome.Ok, result.Outcome);
        Assert.Equal("{\"reply\":\"In Goldshire.\",\"intent\":\"none\"}", result.Text);
        Assert.Equal((120, 14), (result.InputTokens, result.OutputTokens));
        HttpRequestMessage request = handler.Request!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        Assert.Equal("ArcaneCore", Assert.Single(request.Headers.GetValues("X-Title")));
        Assert.Null(request.Headers.Authorization);

        using JsonDocument body = JsonDocument.Parse(handler.Body);
        JsonElement root = body.RootElement;
        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal(150, root.GetProperty("max_tokens").GetInt32());
        JsonElement system = root.GetProperty("system");
        Assert.Equal(2, system.GetArrayLength());
        Assert.Equal("STATIC RULES", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("DYNAMIC FACTS", system[1].GetProperty("text").GetString());
        Assert.False(system[1].TryGetProperty("cache_control", out _));
        Assert.Equal(["user", "assistant", "user"], root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("where are you?", root.GetProperty("messages")[2].GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task Anthropic_ARefusal_AndTheErrorStatuses_AreClassified()
    {
        var provider = new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic };
        using (var refused = new HttpBotChatClient(new FakeHandler((_, _) => Json("""{"content":[],"stop_reason":"refusal","usage":{"input_tokens":5,"output_tokens":0}}"""))))
            Assert.Equal(BotChatOutcome.Refused, (await refused.CompleteAsync(provider, "k", Prompt, CancellationToken.None)).Outcome);

        var limited = Json("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
        using (var client = new HttpBotChatClient(new FakeHandler((_, _) => limited)))
        {
            BotChatResult result = await client.CompleteAsync(provider, "k", Prompt, CancellationToken.None);
            Assert.Equal(BotChatOutcome.RateLimited, result.Outcome);
            Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
        }

        foreach ((HttpStatusCode status, BotChatOutcome outcome) in new[]
        {
            ((HttpStatusCode)529, BotChatOutcome.Unavailable), (HttpStatusCode.InternalServerError, BotChatOutcome.Unavailable),
            (HttpStatusCode.Unauthorized, BotChatOutcome.Unauthorized), (HttpStatusCode.BadRequest, BotChatOutcome.Rejected),
        })
        {
            using var client = new HttpBotChatClient(new FakeHandler((_, _) => Json("{}", status)));
            Assert.Equal(outcome, (await client.CompleteAsync(provider, "k", Prompt, CancellationToken.None)).Outcome);
        }
    }

    [Fact]
    public async Task OpenAICompatible_SendsTheChatCompletionsShape_WithABearerKey()
    {
        var handler = new FakeHandler((_, _) => Json("""
            {"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Hey there."},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":90,"completion_tokens":4,"total_tokens":94}}
            """));
        using var client = new HttpBotChatClient(handler);
        var provider = new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://openrouter.ai/api/v1/", Model = "openai/gpt-4o-mini",
            ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY", MaxTokens = 120,
            Headers = { ["HTTP-Referer"] = "https://github.com/Krilliac/ArcaneCore", ["X-Title"] = "ArcaneCore" },
        };

        BotChatResult result = await client.CompleteAsync(provider, "sk-or-test", Prompt, CancellationToken.None);

        Assert.Equal(BotChatOutcome.Ok, result.Outcome);
        Assert.Equal("Hey there.", result.Text);
        Assert.Equal((90, 4), (result.InputTokens, result.OutputTokens));
        HttpRequestMessage request = handler.Request!;
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-or-test", request.Headers.Authorization.Parameter);
        Assert.Equal("https://github.com/Krilliac/ArcaneCore", Assert.Single(request.Headers.GetValues("HTTP-Referer")));
        Assert.False(request.Headers.Contains("x-api-key"));

        using JsonDocument body = JsonDocument.Parse(handler.Body);
        JsonElement root = body.RootElement;
        Assert.Equal("openai/gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal(120, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        JsonElement[] messages = [.. root.GetProperty("messages").EnumerateArray()];
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("STATIC RULES\n\nDYNAMIC FACTS", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAICompatible_Keyless_SendsNoAuthorization_AndTheNewerLimitFieldWhenAsked()
    {
        var handler = new FakeHandler((_, _) => Json("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        using var client = new HttpBotChatClient(handler);
        var provider = new PlayerbotChatProviderOptions
        {
            Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "http://localhost:11434/v1", Model = "qwen3:0.6b",
            ApiKeyEnvironmentVariable = "", TokenLimitParameter = "max_completion_tokens",
        };

        BotChatResult result = await client.CompleteAsync(provider, null, Prompt, CancellationToken.None);

        Assert.Equal(BotChatOutcome.Ok, result.Outcome);
        Assert.Equal((0, 0), (result.InputTokens, result.OutputTokens)); // a local server may report no usage
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Null(handler.Request.Headers.Authorization);
        using JsonDocument body = JsonDocument.Parse(handler.Body);
        Assert.Equal(150, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task OpenAICompatible_AContentFilter_AnEmptyChoice_AndAnOversizedBody_AreNotReplies()
    {
        var provider = new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "http://127.0.0.1:1234/v1", Model = "m" };
        using (var filtered = new HttpBotChatClient(new FakeHandler((_, _) => Json("""{"choices":[{"message":{"content":""},"finish_reason":"content_filter"}]}"""))))
            Assert.Equal(BotChatOutcome.Refused, (await filtered.CompleteAsync(provider, null, Prompt, CancellationToken.None)).Outcome);
        using (var empty = new HttpBotChatClient(new FakeHandler((_, _) => Json("""{"choices":[]}"""))))
            Assert.Equal(BotChatOutcome.BadResponse, (await empty.CompleteAsync(provider, null, Prompt, CancellationToken.None)).Outcome);
        string huge = "{\"choices\":[{\"message\":{\"content\":\"" + new string('a', HttpBotChatClient.MaxResponseBytes) + "\"}}]}";
        using (var oversized = new HttpBotChatClient(new FakeHandler((_, _) => Json(huge))))
            Assert.Equal(BotChatOutcome.BadResponse, (await oversized.CompleteAsync(provider, null, Prompt, CancellationToken.None)).Outcome);
        using (var garbage = new HttpBotChatClient(new FakeHandler((_, _) => Json("not json"))))
            Assert.Equal(BotChatOutcome.BadResponse, (await garbage.CompleteAsync(provider, null, Prompt, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ACancelledRequest_IsATimeout_AndAConnectionFailure_IsANetworkError()
    {
        var provider = new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic };
        using (var slow = new HttpBotChatClient(new FakeHandler((_, _) => throw new TaskCanceledException())))
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            Assert.Equal(BotChatOutcome.Timeout, (await slow.CompleteAsync(provider, "k", Prompt, cancelled.Token)).Outcome);
        }

        using var broken = new HttpBotChatClient(new FakeHandler((_, _) => throw new HttpRequestException("refused")));
        Assert.Equal(BotChatOutcome.NetworkError, (await broken.CompleteAsync(provider, "k", Prompt, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("{\"reply\": \"Sure.\", \"intent\": \"stay\"}", "Sure.", "Stay")]
    [InlineData("Plain words, no JSON.", "Plain words, no JSON.", null)]
    [InlineData("{\"reply\": \"broken", null, null)]
    [InlineData("{\"reply\": \"line one\\nline two |cff0000ffred|r\"}", "line one line two /cff0000ffred/r", null)]
    [InlineData("{\"reply\": \"\", \"intent\": \"follow\"}", null, "Follow")]
    public void AModelAnswer_IsReadAndMadeFitForChat(string answer, string? reply, string? command)
    {
        (string? text, ArcaneCore.World.Playerbots.Party.PlayerbotPartyCommand? order) = PlayerbotChatPrompts.ReadAnswer(answer);
        Assert.Equal(reply, text);
        Assert.Equal(command, order?.ToString());
    }

    [Fact]
    public void ALongAnswer_IsCutAtAWord_WithinTheChatLimit()
    {
        string text = PlayerbotChatPrompts.Sanitize(string.Join(' ', Enumerable.Repeat("word", 200)))!;
        Assert.True(text.Length <= PlayerbotChatPrompts.MaxReplyLength);
        Assert.EndsWith("word", text);
    }
}
