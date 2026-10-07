using System.Net.Http.Json;
using System.Text.Json;

namespace ArcaneCore.World.Playerbots;

/// <summary>Optional local-only planner. It selects among supplied closed candidates and cannot create actions.</summary>
internal sealed class PlayerbotLocalPlanner : IAsyncDisposable
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11435/api/chat");
    private const string KeepAlive = "30s";
    private const int MaxCandidates = 16;
    private const int MaxResponseBytes = 4 * 1024;
    private readonly PlayerbotOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _inference = new(1, 1);
    private readonly CancellationTokenSource _dispose = new();
    private int _disposed;

    internal PlayerbotLocalPlanner(PlayerbotOptions options, HttpMessageHandler? handler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = Endpoint;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal async Task<string?> SelectAsync(PlayerbotPlannerFacts facts,
        IReadOnlyList<PlayerbotPlanCandidate> candidates, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested
            || !_options.AllowLocalLlm || !_options.Enabled
            || candidates.Count == 0 || candidates.Count > MaxCandidates
            || !ValidateFacts(facts) || candidates.Any(candidate => !ValidateCandidate(candidate)))
            return null;
        if (candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            return null;
        try
        {
            if (!await _inference.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        }
        catch (OperationCanceledException) { return null; }

        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
            deadline.CancelAfter(Math.Clamp(_options.LocalLlmTimeoutMs, 100, 10_000));
            var factsPayload = new
            {
                level = facts.Level, health = facts.Health, maximum_health = facts.MaxHealth,
                in_combat = facts.InCombat, map_id = facts.MapId, known_spells = facts.KnownSpells,
                food_count = facts.FoodCount,
                candidates = candidates.Select(candidate => new { id = candidate.Id, goal = (int)candidate.Goal,
                    entry = candidate.Entry, quest_id = candidate.QuestId }).ToArray(),
            };
            var body = new
            {
                model = _options.LocalLlmModel,
                stream = false,
                think = false,
                keep_alive = KeepAlive,
                messages = new[]
                {
                    new { role = "system", content = "Select only one supplied candidate id. Return the required JSON object." },
                    new { role = "user", content = JsonSerializer.Serialize(factsPayload) },
                },
                format = new
                {
                    type = "object",
                    properties = new { id = new { type = "string", @enum = candidates.Select(candidate => candidate.Id).ToArray() } },
                    required = new[] { "id" }, additionalProperties = false,
                },
                options = new { num_ctx = Math.Clamp(_options.LocalLlmContextSize, 512, 2048), num_predict = 96, temperature = 0 },
            };

            using HttpRequestMessage request = new(HttpMethod.Post, string.Empty) { Content = JsonContent.Create(body) };
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] buffer = new byte[MaxResponseBytes + 1];
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(used), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            if (used > MaxResponseBytes) return null;
            using JsonDocument envelope = JsonDocument.Parse(buffer.AsMemory(0, used));
            if (envelope.RootElement.ValueKind != JsonValueKind.Object
                || !envelope.RootElement.TryGetProperty("done", out JsonElement done) || done.ValueKind != JsonValueKind.True
                || !envelope.RootElement.TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.String)
                return null;
            using JsonDocument choice = JsonDocument.Parse(content.GetString()!);
            if (choice.RootElement.ValueKind != JsonValueKind.Object
                || choice.RootElement.EnumerateObject().Count() != 1
                || !choice.RootElement.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String)
                return null;
            string? selected = id.GetString();
            return candidates.Any(candidate => candidate.Id == selected) ? selected : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
        finally { _inference.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispose.Cancel();
        bool acquired = false;
        try { acquired = await _inference.WaitAsync(TimeSpan.FromSeconds(11)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        finally { if (acquired) _inference.Release(); }
        if (!acquired)
        {
            _http.Dispose();
            return;
        }
        _http.Dispose();
        _dispose.Dispose();
        _inference.Dispose();
    }

    private static bool ValidateFacts(PlayerbotPlannerFacts facts)
        => facts.Level > 0 && facts.MaxHealth > 0 && facts.Health <= facts.MaxHealth;

    private static bool ValidateCandidate(PlayerbotPlanCandidate candidate)
        => Enum.IsDefined(candidate.Goal)
            && candidate.Id.Length is > 0 and <= 32
            && candidate.Id.All(IsCandidateCharacter);

    private static bool IsCandidateCharacter(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
}
