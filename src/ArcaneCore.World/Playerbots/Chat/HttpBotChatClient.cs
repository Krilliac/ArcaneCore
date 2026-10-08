using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The HTTP transport of the model providers, with no SDK dependency: one <c>POST</c> per reply.
/// <para>
/// <b>Anthropic</b> (<c>POST {BaseUrl}/v1/messages</c>, headers <c>x-api-key</c> and <c>anthropic-version: 2023-06-01</c>): the
/// system prompt is two text blocks, the static rules first with <c>cache_control: ephemeral</c> (prompt caching; a prefix under
/// the model's minimum cacheable length, 4096 tokens on Claude Haiku 4.5, is simply not cached), then the bot's facts. The reply is
/// the text blocks of <c>content</c>; <c>stop_reason: refusal</c> is a refusal.
/// </para>
/// <para>
/// <b>OpenAICompatible</b> (<c>POST {BaseUrl}/chat/completions</c>, <c>Authorization: Bearer</c> when a key is set): one system
/// message (rules then facts, so providers that cache prefixes can), the reply is <c>choices[0].message.content</c>;
/// <c>finish_reason: content_filter</c> is a refusal.
/// </para>
/// Responses above 64 KiB are not read. The key is put on the request only and never appears in a result or an exception message.
/// </summary>
public sealed class HttpBotChatClient : IBotChatClient, IDisposable
{
    public const string AnthropicVersion = "2023-06-01";
    internal const int MaxResponseBytes = 64 * 1024;
    private readonly HttpClient _http;

    public HttpBotChatClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // the caller's token carries the deadline
    }

    public async Task<BotChatResult> CompleteAsync(PlayerbotChatProviderOptions provider, string? apiKey, BotChatPrompt prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(prompt);
        if (provider.Endpoint is not { } endpoint) return BotChatResult.Fail(BotChatOutcome.Rejected);
        bool anthropic = provider.Kind == PlayerbotChatProviderKind.Anthropic;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent((anthropic ? AnthropicBody(provider, prompt) : OpenAIBody(provider, prompt)).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach ((string name, string value) in provider.Headers ?? []) request.Headers.TryAddWithoutValidation(name, value);
        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (apiKey is not null) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        else if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return BotChatResult.Fail(Classify(response.StatusCode), RetryAfter(response));
            byte[]? body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            if (body is null) return BotChatResult.Fail(BotChatOutcome.BadResponse);
            return anthropic ? ParseAnthropic(body) : ParseOpenAI(body);
        }
        catch (OperationCanceledException)
        {
            return BotChatResult.Fail(BotChatOutcome.Timeout);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return BotChatResult.Fail(BotChatOutcome.NetworkError);
        }
    }

    /// <summary>The Messages API body (Anthropic): model, max_tokens, the two-block system prompt with the cache breakpoint, the messages.</summary>
    internal static JsonObject AnthropicBody(PlayerbotChatProviderOptions provider, BotChatPrompt prompt)
    {
        var system = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = prompt.StaticSystem, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } },
            new JsonObject { ["type"] = "text", ["text"] = prompt.DynamicSystem },
        };
        var messages = new JsonArray();
        foreach (BotChatTurn turn in prompt.Messages)
            messages.Add(new JsonObject { ["role"] = turn.FromPlayer ? "user" : "assistant", ["content"] = turn.Text });
        return new JsonObject
        {
            ["model"] = provider.EffectiveModel,
            ["max_tokens"] = provider.MaxTokens,
            ["system"] = system,
            ["messages"] = messages,
        };
    }

    /// <summary>The chat completions body (OpenAI-compatible): model, the reply limit field, one system message, the messages.</summary>
    internal static JsonObject OpenAIBody(PlayerbotChatProviderOptions provider, BotChatPrompt prompt)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = prompt.StaticSystem + "\n\n" + prompt.DynamicSystem } };
        foreach (BotChatTurn turn in prompt.Messages)
            messages.Add(new JsonObject { ["role"] = turn.FromPlayer ? "user" : "assistant", ["content"] = turn.Text });
        return new JsonObject
        {
            ["model"] = provider.EffectiveModel,
            [provider.TokenLimitParameter] = provider.MaxTokens,
            ["messages"] = messages,
            ["stream"] = false,
        };
    }

    internal static BotChatOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        429 => BotChatOutcome.RateLimited,
        401 or 403 => BotChatOutcome.Unauthorized,
        408 => BotChatOutcome.Timeout,
        >= 500 => BotChatOutcome.Unavailable,
        _ => BotChatOutcome.Rejected,
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MaxResponseBytes + 1];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }

        return used > MaxResponseBytes ? null : buffer[..used];
    }

    internal static BotChatResult ParseAnthropic(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BotChatResult.Fail(BotChatOutcome.BadResponse);
            (int input, int output, int write, int read) = (0, 0, 0, 0);
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Int(usage, "input_tokens");
                output = Int(usage, "output_tokens");
                write = Int(usage, "cache_creation_input_tokens");
                read = Int(usage, "cache_read_input_tokens");
            }

            if (root.TryGetProperty("stop_reason", out JsonElement stop) && stop.ValueKind == JsonValueKind.String && stop.GetString() == "refusal")
                return new BotChatResult(BotChatOutcome.Refused, null, input, output, write, read);
            var text = new StringBuilder();
            if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "text" && block.TryGetProperty("text", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                        text.Append(value.GetString());
                }
            }

            return text.Length == 0
                ? new BotChatResult(BotChatOutcome.BadResponse, null, input, output, write, read)
                : new BotChatResult(BotChatOutcome.Ok, text.ToString(), input, output, write, read);
        }
        catch (JsonException)
        {
            return BotChatResult.Fail(BotChatOutcome.BadResponse);
        }
    }

    internal static BotChatResult ParseOpenAI(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BotChatResult.Fail(BotChatOutcome.BadResponse);
            (int input, int output) = (0, 0);
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Int(usage, "prompt_tokens");
                output = Int(usage, "completion_tokens");
            }

            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return new BotChatResult(BotChatOutcome.BadResponse, null, input, output);
            JsonElement first = choices[0];
            if (first.ValueKind != JsonValueKind.Object) return BotChatResult.Fail(BotChatOutcome.BadResponse);
            if (first.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "content_filter")
                return new BotChatResult(BotChatOutcome.Refused, null, input, output);
            if (!first.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
                return new BotChatResult(BotChatOutcome.BadResponse, null, input, output);
            // Cached prompt tokens are part of prompt_tokens here; the estimate prices them as plain input (an upper bound).
            return new BotChatResult(BotChatOutcome.Ok, content.GetString(), input, output);
        }
        catch (JsonException)
        {
            return BotChatResult.Fail(BotChatOutcome.BadResponse);
        }
    }

    private static int Int(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0 ? number : 0;

    public void Dispose() => _http.Dispose();
}
