using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ArcaneCore.MockClient.Playbots;

/// <summary>Optional local selector. It cannot construct packets or introduce an action.</summary>
internal sealed class OllamaPlaybotSelector : IPlaybotSelector, IDisposable
{
    private const string ModelKeepAlive = "30s";
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly TimeSpan _deadline;
    private readonly bool _ownsHttp;

    internal OllamaPlaybotSelector(Uri endpoint, string model, TimeSpan? deadline = null, HttpClient? http = null)
    {
        ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(char.IsControl))
            throw new ArgumentException("A local model name of 1..128 characters is required.", nameof(model));
        _model = model;
        _deadline = deadline ?? TimeSpan.FromSeconds(5);
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        _http.BaseAddress = endpoint;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/"
            || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out IPAddress? ip)
            || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) || endpoint.Port < 1)
            throw new ArgumentException("The model endpoint must be a literal loopback HTTP origin.", nameof(endpoint));
    }

    // Loading is explicit, bounded and separate from short decisions. No pull/download API is used.
    internal async Task<bool> WarmAsync(CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "api/generate")
            {
                Content = JsonContent.Create(new { model = _model, prompt = "", stream = false, think = false, keep_alive = ModelKeepAlive,
                    options = new { num_ctx = 2048, num_predict = 1 } })
            };
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<PlaybotSelection> SelectAsync(PlaybotDecisionContext context, CancellationToken cancellationToken)
    {
        PlaybotCandidate fallback = DeterministicPlaybotSelector.Choose(context);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_deadline);
        try
        {
            // No names, chat, credentials, coordinates, DBC data or raw server logs leave the client.
            var facts = new { revision = context.Revision, health = context.Health, maximumHealth = context.MaximumHealth,
                inCombat = context.InCombat, actions = context.Candidates.Select(c => new { id = c.Id, kind = c.Kind.ToString(), priority = c.Priority }) };
            var body = new { model = _model, stream = false, think = false, keep_alive = ModelKeepAlive,
                prompt = "Select one supplied action ID for a bounded game playtest. Prefer survival and high priority. Return only JSON. Facts: "
                    + JsonSerializer.Serialize(facts),
                format = new { type = "object", properties = new { action_id = new { type = "string", @enum = context.Candidates.Select(c => c.Id).ToArray() } },
                    required = new[] { "action_id" }, additionalProperties = false },
                options = new { num_ctx = 2048, num_predict = 96, temperature = 0 } };
            using HttpRequestMessage request = new(HttpMethod.Post, "api/generate") { Content = JsonContent.Create(body) };
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new(fallback, "deterministic", "http-status");
            await using Stream stream = await response.Content.ReadAsStreamAsync(bound.Token).ConfigureAwait(false);
            byte[] buffer = new byte[16 * 1024 + 1];
            int used = 0;
            while (used < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(used), bound.Token).ConfigureAwait(false);
                if (count == 0) break;
                used += count;
            }
            if (used > 16 * 1024) return new(fallback, "deterministic", "response-budget");
            using JsonDocument envelope = JsonDocument.Parse(buffer.AsMemory(0, used));
            if (!envelope.RootElement.TryGetProperty("response", out JsonElement output) || output.ValueKind != JsonValueKind.String)
                return new(fallback, "deterministic", "invalid-envelope");
            using JsonDocument choice = JsonDocument.Parse(output.GetString()!);
            JsonElement root = choice.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("action_id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                return new(fallback, "deterministic", "invalid-choice");
            PlaybotCandidate? selected = context.Candidates.SingleOrDefault(c => c.Id == id.GetString());
            return selected is null ? new(fallback, "deterministic", "unknown-action") : new(selected, "ollama");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(fallback, "deterministic", error is OperationCanceledException ? "decision-timeout" : "provider-error");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
