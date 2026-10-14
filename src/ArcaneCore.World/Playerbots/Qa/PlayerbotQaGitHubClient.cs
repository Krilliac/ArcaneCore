using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>An issue found or made on GitHub. <paramref name="StateReason"/> is GitHub's closing reason (<c>completed</c>, <c>not_planned</c>); null when open or not reported.</summary>
internal sealed record QaIssueRef(int Number, bool Open, string Url, string? StateReason = null)
{
    /// <summary>The issue was closed as not planned (a won't-fix): a regression never reopens it.</summary>
    public bool NotPlanned => string.Equals(StateReason, "not_planned", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A GitHub call failed for good (after the retries). The message carries the status and a redacted, truncated body, never the token.</summary>
internal sealed class PlayerbotQaPublishException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Whether a later pass may succeed (a rate limit, a server error, a timeout) rather than a refusal (permissions, a bad request).</summary>
    public bool Transient { get; } = transient;
}

/// <summary>
/// The few GitHub REST calls bot QA needs, over an injectable <see cref="HttpMessageHandler"/> (the test seam). Every request carries the
/// Authorization, Accept, X-GitHub-Api-Version and User-Agent headers itself (never on the shared client), redirects are not followed (so the token can
/// never be sent elsewhere), a pagination link to another host is refused, response bodies are capped, and each call has a timeout. A 429, a 403 with
/// Retry-After or an exhausted x-ratelimit-remaining waits as GitHub says (at most 15 minutes), a 5xx or a timeout of a read or PATCH backs off 1, 2, 4 seconds (a POST is never resent after a 5xx, a timeout or a connection error, since GitHub may have applied it: the call throws a transient failure and the next pass's marker search settles it), and after
/// <see cref="MaxAttempts"/> attempts the call throws <see cref="PlayerbotQaPublishException"/>. One instance serves one publish pass: it holds the
/// token only for that long. Not thread-safe across passes; calls of one pass are sequential.
/// </summary>
internal sealed partial class PlayerbotQaGitHubClient : IQaIssueTransport
{
    internal const string ApiVersion = "2022-11-28";
    internal const string UserAgent = "ArcaneCore-bot-qa";
    internal const string BaseAddress = "https://api.github.com/";
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _token;
    private readonly PlayerbotQaGitHubOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    public PlayerbotQaGitHubClient(HttpMessageHandler? handler, string repository, string token, PlayerbotQaGitHubOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PlayerbotQaGitHubOptions.IsValidRepository(repository)) throw new ArgumentException("not an owner/repo", nameof(repository));
        ArgumentException.ThrowIfNullOrEmpty(token);
        _repository = repository;
        _token = token;
        _options = options;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseAddress);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 120));
    }

    public void Dispose() => _http.Dispose();

    public bool SupportsSearch => true;

    /// <summary>
    /// Read the label-filtered issue list (state all, no pull requests) once: up to <see cref="PlayerbotQaGitHubOptions.MaxListPages"/> pages of 100, indexed by marker and number.
    /// The index is complete when the last page had no next link.
    /// </summary>
    public async Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        string label = _options.Labels.Length > 0 ? _options.Labels[0] : "bot-qa";
        string? next = $"repos/{_repository}/issues?labels={Uri.EscapeDataString(label)}&state=all&per_page=100&page=1";
        var pages = new List<string>();
        int limit = Math.Max(1, _options.MaxListPages);
        for (int page = 0; page < limit && next is not null; page++)
        {
            Reply reply = await SendAsync(HttpMethod.Get, next, null, false, cancellationToken).ConfigureAwait(false);
            pages.Add(reply.Body);
            next = NextLink(reply.Headers);
        }

        var index = new QaIssueIndex(complete: next is null);
        foreach (string body in pages) AddAll(index, body);
        return index;
    }

    /// <summary>The list is authoritative, but an issue whose label was removed by hand is only found by the search (whose index lags and whose rate limit is small).</summary>
    public async Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken)
    {
        string marker = PlayerbotQaIssueComposer.MarkerOf(id);
        string query = $"repo:{_repository} \"arcanecore-bot-qa:{id}\" in:body is:issue";
        Reply search = await SendAsync(HttpMethod.Get, $"search/issues?q={Uri.EscapeDataString(query)}&per_page=30", null, false, cancellationToken).ConfigureAwait(false);
        return MatchIn(search.Body, marker, array: false);
    }

    /// <summary>One-shot lookup of <paramref name="id"/>: the list, then the search. A publish pass does not use it (it lists once and searches within a budget).</summary>
    public async Task<QaIssueRef?> FindByMarkerAsync(string id, CancellationToken cancellationToken)
    {
        QaIssueIndex index = await ListIndexAsync(cancellationToken).ConfigureAwait(false);
        return index.ByMarker(id) ?? await SearchByMarkerAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One issue by number; null when it does not exist.</summary>
    public async Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken)
    {
        Reply reply = await SendAsync(HttpMethod.Get, $"repos/{_repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}", null, true, cancellationToken).ConfigureAwait(false);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        using JsonDocument doc = Parse(reply.Body);
        QaIssueRef? issue = ToRef(doc.RootElement);
        return issue is null ? null : (issue, doc.RootElement.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty);
    }

    public async Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["title"] = title,
            ["body"] = body,
            ["labels"] = new JsonArray([.. labels.Select(label => (JsonNode?)JsonValue.Create(label))]),
        };
        Reply reply = await SendAsync(HttpMethod.Post, $"repos/{_repository}/issues", payload.ToJsonString(), false, cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = Parse(reply.Body);
        return ToRef(doc.RootElement) ?? throw new PlayerbotQaPublishException("GitHub created an issue but the reply had no issue number", transient: false);
    }

    public async Task CommentAsync(int number, string body, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Post, $"repos/{_repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments",
            new JsonObject { ["body"] = body }.ToJsonString(), false, cancellationToken).ConfigureAwait(false);

    public async Task ReopenAsync(int number, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Patch, $"repos/{_repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}",
            new JsonObject { ["state"] = "open" }.ToJsonString(), false, cancellationToken).ConfigureAwait(false);

    public async Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Patch, $"repos/{_repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}",
            new JsonObject { ["state"] = "closed", ["state_reason"] = "not_planned" }.ToJsonString(), false, cancellationToken).ConfigureAwait(false);

    private sealed record Reply(HttpStatusCode Status, string Body, HttpResponseHeaders Headers);

    private async Task<Reply> SendAsync(HttpMethod method, string pathAndQuery, string? json, bool allowNotFound, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            TimeSpan wait;
            try
            {
                using var request = new HttpRequestMessage(method, pathAndQuery);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                string body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
                {
                    return new Reply(response.StatusCode, body, response.Headers);
                }

                // A POST that drew a 5xx may already have been applied; resending would duplicate the issue or comment. Only a rate-limit refusal is safe to resend.
                if (method == HttpMethod.Post && (int)response.StatusCode >= 500) throw Failure(method, pathAndQuery, response.StatusCode, body, transient: true);
                TimeSpan? retry = RetryDelay(response, attempt);
                if (retry is null) throw Failure(method, pathAndQuery, response.StatusCode, body, transient: false);
                if (attempt >= MaxAttempts) throw Failure(method, pathAndQuery, response.StatusCode, body, transient: true);
                wait = retry.Value;
            }
            catch (HttpRequestException ex)
            {
                if (method == HttpMethod.Post || attempt >= MaxAttempts) throw new PlayerbotQaPublishException($"GitHub {method} {PathOf(pathAndQuery)} failed: {ex.GetType().Name}", transient: true, ex);
                wait = Backoff(attempt);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (method == HttpMethod.Post || attempt >= MaxAttempts) throw new PlayerbotQaPublishException($"GitHub {method} {PathOf(pathAndQuery)} timed out", transient: true, ex);
                wait = Backoff(attempt);
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan? RetryDelay(HttpResponseMessage response, int attempt)
    {
        int status = (int)response.StatusCode;
        if (status >= 500) return Backoff(attempt);
        if (status is not (429 or 403)) return null;
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            TimeSpan span = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - _now() : TimeSpan.FromSeconds(60));
            return Cap(span);
        }

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? remaining) && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string>? reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long epoch))
        {
            return Cap(DateTimeOffset.FromUnixTimeSeconds(Math.Min(epoch, 253_402_300_799L)) - _now());
        }

        return status == 429 ? TimeSpan.FromSeconds(60) : null;
    }

    private static TimeSpan Cap(TimeSpan span) => span < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : span > MaxWait ? MaxWait : span;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(1 << Math.Min(attempt - 1, 4));

    private PlayerbotQaPublishException Failure(HttpMethod method, string pathAndQuery, HttpStatusCode status, string body, bool transient)
    {
        string text = PlayerbotQaRedactor.RedactSecrets(body, _token).Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > 300) text = text[..300] + "...";
        return new PlayerbotQaPublishException($"GitHub {method} {PathOf(pathAndQuery)} failed with {(int)status}: {text}", transient);
    }

    private static string PathOf(string pathAndQuery)
    {
        int q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? pathAndQuery : pathAndQuery[..q];
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new PlayerbotQaPublishException("GitHub reply is larger than the 16 MiB limit", transient: false);
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlayerbotQaPublishException("GitHub reply is not JSON", transient: false, ex);
        }
    }

    private static void AddAll(QaIssueIndex index, string body)
    {
        using JsonDocument doc = Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _)) continue;
            if (ToRef(item) is not { } issue) continue;
            index.Add(issue, item.TryGetProperty("body", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty);
        }
    }

    private static QaIssueRef? MatchIn(string body, string marker, bool array)
    {
        using JsonDocument doc = Parse(body);
        JsonElement items = doc.RootElement;
        if (!array)
        {
            if (items.ValueKind != JsonValueKind.Object || !items.TryGetProperty("items", out items)) return null;
        }

        if (items.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _)) continue;
            if (item.TryGetProperty("body", out JsonElement text) && text.ValueKind == JsonValueKind.String
                && text.GetString()!.Contains(marker, StringComparison.Ordinal) && ToRef(item) is { } issue)
            {
                return issue;
            }
        }

        return null;
    }

    private static QaIssueRef? ToRef(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("number", out JsonElement number) || !number.TryGetInt32(out int n) || n <= 0) return null;
        bool open = item.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String && state.GetString() == "open";
        string url = item.TryGetProperty("html_url", out JsonElement html) && html.ValueKind == JsonValueKind.String ? html.GetString() ?? string.Empty : string.Empty;
        string? reason = item.TryGetProperty("state_reason", out JsonElement why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
        return new QaIssueRef(n, open, url, reason);
    }

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""next""", RegexOptions.CultureInvariant)]
    private static partial Regex NextPattern();

    private static string? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out IEnumerable<string>? values)) return null;
        foreach (string value in values)
        {
            Match m = NextPattern().Match(value);
            if (!m.Success) continue;
            if (!Uri.TryCreate(m.Groups["url"].Value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, new Uri(BaseAddress).Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlayerbotQaPublishException("GitHub pagination link points to another host; refused", transient: false);
            }

            return uri.PathAndQuery.TrimStart('/');
        }

        return null;
    }
}
