using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Builders and fakes shared by the bot QA tests.</summary>
internal static class QaData
{
    internal static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid Bot1 = new("3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e");
    internal static readonly Guid Bot2 = new("a9c4e2d1-7b36-4f08-8e12-5c6d7e8f9a0b");
    internal static readonly Guid Bot3 = new("0d5e6f70-8192-4a3b-b4c5-d6e7f8091a2b");

    internal const string Repo = "Krilliac/ArcaneCore";

    /// <summary>A fake token with the shape of a classic GitHub personal access token (ghp_ + 36 characters).</summary>
    internal const string FakeToken = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    /// <summary>The production template of the bot stall/death-loop warning: <c>Playerbot {BotId} ({Name}) {Stall}; it gives up that goal</c>.</summary>
    internal static string BotLine(Guid bot, string name, string report) => $"Playerbot {bot} ({name}) {report}; it gives up that goal";

    internal static QaSignal Parse(string line, Guid? botKey = null, DateTime? at = null)
    {
        Assert.True(PlayerbotQaSignalParser.TryParse(line, at ?? T0, botKey, out QaSignal? signal), "line was not parsed: " + line);
        return signal!;
    }

    internal static QaSignal Stall(Guid? bot, float x, float y, uint quest = 1656, uint target = 6747, uint map = 0, DateTime? at = null, string goal = "Quest")
        => new()
        {
            Kind = QaSignalKind.Stall, AtUtc = at ?? T0, BotKey = bot, Goal = goal, Target = target, Quest = quest, HasPosition = true, Map = map, X = x, Y = y, Z = 400f,
            Seconds = 120, Message = $"Playerbot <bot> stalled 120s: goal={goal} target={target} quest={quest} at {x:F1},{y:F1},400.0 map {map}",
        };

    internal static QaSignal Fault(Guid? bot, string text, string type = "InvalidOperationException", DateTime? at = null)
        => new()
        {
            Kind = QaSignalKind.ActionFault, AtUtc = at ?? T0, BotKey = bot, Goal = "action", ExceptionType = type, Detail = text,
            FaultCode = text.StartsWith("playerbot-", StringComparison.Ordinal) ? text.Split(' ', ':')[0] : string.Empty,
            Message = $"Playerbot <bot> action failed ({type}: {text})",
        };

    internal static QaSignal Relay(uint relay, DateTime? at = null)
        => new() { Kind = QaSignalKind.RelayScriptMissing, AtUtc = at ?? T0, Relay = relay, Message = $"relay script {relay} does not exist (dbscripts_on_relay); not started" };

    internal static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A finding view with the given ranking inputs.</summary>
    internal static QaFindingView View(string id, double severity = 30, long count = 3, DateTime? last = null, string subject = "quest 1656", int bots = 2,
        QaSignalKind kind = QaSignalKind.Stall, IReadOnlyList<QaSample>? samples = null, QaCause cause = QaCause.Unknown, string causeDetail = "detail")
        => new()
        {
            Id = id, Key = "k-" + id, Kind = kind, Subject = subject, Count = count, DistinctBots = bots, FirstSeenUtc = T0, LastSeenUtc = last ?? T0, Severity = severity,
            Samples = samples ?? [new QaSample(T0, true, 0, -5608.8f, -439.4f, 401.2f, [], "Playerbot <bot> stalled 120s: goal=Quest")], Cause = cause, CauseDetail = causeDetail,
        };
}

/// <summary>A hand-set time source.</summary>
internal sealed class ManualTime(DateTime startUtc) : TimeProvider
{
    private DateTime _now = startUtc;

    public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>Scripted content facts.</summary>
internal class FakeFacts : IQaContentFacts
{
    public bool Available { get; set; } = true;

    public Dictionary<uint, string> Titles { get; } = [];

    public Dictionary<uint, int> Levels { get; } = [];

    public Dictionary<uint, uint[]> Objectives { get; } = [];

    public Dictionary<(uint Map, uint Entry), int> Spawns { get; } = [];

    public Dictionary<uint, List<QaEnderFact>> Enders { get; } = [];

    public Dictionary<(uint Map, uint Entry), List<(float X, float Y)>> SpawnPoints { get; } = [];

    public Dictionary<uint, QaTemplateFacts> Templates { get; } = [];

    public HashSet<(uint Entry, uint Path)> Paths { get; } = [];

    public HashSet<string> Scripts { get; } = [];

    /// <summary>Entries whose ScriptName is implemented only on one map (a per-map script).</summary>
    public HashSet<(uint Map, uint Entry)> MapScripts { get; } = [];

    public string? QuestTitle(uint quest) => Titles.GetValueOrDefault(quest);

    public int? QuestLevel(uint quest) => Levels.TryGetValue(quest, out int level) ? level : null;

    public IReadOnlyList<uint> QuestObjectiveCreatures(uint quest) => Objectives.GetValueOrDefault(quest) ?? [];

    public IReadOnlyList<uint> QuestEnderEntries(uint quest) => [.. (Enders.GetValueOrDefault(quest) ?? []).Select(e => e.Entry).Distinct()];

    public IReadOnlyList<QaEnderFact> QuestEnders(uint quest, uint entry) => [.. (Enders.GetValueOrDefault(quest) ?? []).Where(e => e.Entry == entry)];

    public int SpawnCount(uint map, uint entry) => Spawns.GetValueOrDefault((map, entry));

    public int SpawnCountAnyMap(uint entry) => Spawns.Where(pair => pair.Key.Entry == entry).Sum(pair => pair.Value);

    public IReadOnlyList<(float X, float Y)> SpawnPositions(uint map, uint entry) => SpawnPoints.GetValueOrDefault((map, entry)) ?? [];

    public QaTemplateFacts? Template(uint entry) => Templates.TryGetValue(entry, out QaTemplateFacts t) ? t : null;

    public bool HasWaypointPath(uint entry, uint pathId) => Paths.Contains((entry, pathId));

    public virtual bool HasScript(uint entry, uint map)
        => !Templates.TryGetValue(entry, out QaTemplateFacts t) || t.ScriptName.Length == 0
            || Scripts.Contains(t.ScriptName) || MapScripts.Contains((map, entry));
}

/// <summary>An <see cref="ILogger"/> that records every call.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public (LogLevel Level, string Message)[] Entries => [.. _entries];

    public IEnumerable<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>A request as the fake saw it, copied at send time (the client disposes the original).</summary>
internal sealed record SeenRequest(HttpMethod Method, string Uri, string PathAndQuery, Dictionary<string, string> Headers, string Body);

/// <summary>An <see cref="HttpMessageHandler"/> that records requests and answers through a delegate.</summary>
internal sealed class FakeHttp(Func<SeenRequest, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<SeenRequest> _requests = new();
    private int _calls;

    public SeenRequest[] Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers["content:" + header.Key] = string.Join(",", header.Value);
        }

        var seen = new SeenRequest(request.Method, request.RequestUri!.ToString(), request.RequestUri.PathAndQuery, headers, body);
        _requests.Enqueue(seen);
        int call = Interlocked.Increment(ref _calls);
        return await respond(seen, call).ConfigureAwait(false);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A small GitHub: issues with bodies, labels, open/closed state and pull requests, behind the REST calls bot QA makes.</summary>
internal sealed class FakeGitHub
{
    private readonly object _gate = new();
    private readonly List<JsonObject> _issues = [];
    private readonly Func<Task>? _beforeCreate;
    private int _next = 100;

    public FakeGitHub(Func<Task>? beforeCreate = null)
    {
        _beforeCreate = beforeCreate;
        Http = new FakeHttp(Handle);
    }

    public FakeHttp Http { get; }

    /// <summary>A status to answer every request with instead of acting (null: act).</summary>
    public HttpStatusCode? FailWith { get; set; }

    public string FailBody { get; set; } = "{\"message\":\"boom\"}";

    /// <summary>A status to answer every reopen or close (a PATCH of the state) with instead of acting (null: act); other calls are served.</summary>
    public HttpStatusCode? FailStateChangesWith { get; set; }

    /// <summary>How many of the next creates and comments are applied by the server and then answered with a timeout (the reply is lost), like a slow GitHub.</summary>
    public int TimeoutsAfterApply { get; set; }

    /// <summary>How many of the next creates and comments are applied by the server and then answered with a 502 (the reply is lost).</summary>
    public int BadGatewaysAfterApply { get; set; }

    private bool LoseReply(out HttpResponseMessage? reply)
    {
        reply = null;
        if (TimeoutsAfterApply > 0)
        {
            TimeoutsAfterApply--;
            throw new TaskCanceledException("the request timed out");
        }

        if (BadGatewaysAfterApply > 0)
        {
            BadGatewaysAfterApply--;
            reply = FakeHttp.Json(HttpStatusCode.BadGateway, "{\"message\":\"bad gateway\"}");
            return true;
        }

        return false;
    }

    public int Creates => Count(HttpMethod.Post, "/issues");

    public int Comments => Count(HttpMethod.Post, "/comments");

    /// <summary>The PATCHes that changed an issue's state (reopen, close); a body edit is counted by <see cref="BodyEdits"/>.</summary>
    public int Patches => Http.Requests.Count(r => r.Method == HttpMethod.Patch && JsonNode.Parse(r.Body)?["state"] is not null);

    /// <summary>The PATCHes that rewrote an issue's body (the live status block).</summary>
    public int BodyEdits => Http.Requests.Count(r => r.Method == HttpMethod.Patch && JsonNode.Parse(r.Body)?["body"] is not null);

    public int Gets => Http.Requests.Count(r => r.Method == HttpMethod.Get);

    private int Count(HttpMethod method, string suffix)
        => Http.Requests.Count(r => r.Method == method && r.PathAndQuery.Split('?')[0].EndsWith(suffix, StringComparison.Ordinal));

    public int Add(string body, bool open = true, bool pullRequest = false, string label = "bot-qa")
    {
        lock (_gate)
        {
            int number = _next++;
            var issue = new JsonObject
            {
                ["number"] = number, ["state"] = open ? "open" : "closed", ["html_url"] = $"https://github.com/{QaData.Repo}/issues/{number}", ["body"] = body,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = label }),
            };
            if (pullRequest) issue["pull_request"] = new JsonObject();
            _issues.Add(issue);
            return number;
        }
    }

    /// <summary>Replace issue <paramref name="number"/>'s labels with <paramref name="label"/>, as a person editing it would.</summary>
    public void Relabel(int number, string label)
    {
        lock (_gate) Get(number)["labels"] = new JsonArray(new JsonObject { ["name"] = label });
    }

    public JsonObject Get(int number)
    {
        lock (_gate) return _issues.Single(i => (int)i["number"]! == number);
    }

    public string[] CommentBodies => [.. Http.Requests.Where(r => r.Method == HttpMethod.Post && r.PathAndQuery.EndsWith("/comments", StringComparison.Ordinal))
        .Select(r => (string)JsonNode.Parse(r.Body)!["body"]!)];

    private async Task<HttpResponseMessage> Handle(SeenRequest request, int call)
    {
        if (FailWith is { } status) return FakeHttp.Json(status, FailBody);
        string path = request.PathAndQuery.Split('?')[0];
        string prefix = $"/repos/{QaData.Repo}/issues";
        if (request.Method == HttpMethod.Get && path == prefix)
        {
            // Like GitHub, a labels= filter lists only the issues that carry the label (an issue whose label was removed is reachable by number only).
            string? label = request.PathAndQuery.Contains('?', StringComparison.Ordinal)
                ? request.PathAndQuery.Split('?')[1].Split('&').Where(p => p.StartsWith("labels=", StringComparison.Ordinal)).Select(p => Uri.UnescapeDataString(p["labels=".Length..])).FirstOrDefault()
                : null;
            lock (_gate)
            {
                return FakeHttp.Json(HttpStatusCode.OK, new JsonArray([.. _issues
                    .Where(i => label is null || i["labels"]!.AsArray().Any(l => (string?)l?["name"] == label))
                    .Select(i => (JsonNode?)JsonNode.Parse(i.ToJsonString()))]).ToJsonString());
            }
        }

        if (request.Method == HttpMethod.Get && path == "/search/issues")
        {
            lock (_gate) return FakeHttp.Json(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray([.. _issues.Select(i => (JsonNode?)JsonNode.Parse(i.ToJsonString()))]) }.ToJsonString());
        }

        if (request.Method == HttpMethod.Post && path == prefix)
        {
            if (_beforeCreate is not null) await _beforeCreate().ConfigureAwait(false);
            JsonNode payload = JsonNode.Parse(request.Body)!;
            int number = Add((string)payload["body"]!, label: (string)payload["labels"]![0]!);
            if (LoseReply(out HttpResponseMessage? lost)) return lost!;
            return FakeHttp.Json(HttpStatusCode.Created, Get(number).ToJsonString());
        }

        if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            string[] rest = path[(prefix.Length + 1)..].Split('/');
            int number = int.Parse(rest[0], System.Globalization.CultureInfo.InvariantCulture);
            JsonObject? issue;
            lock (_gate) issue = _issues.SingleOrDefault(i => (int)i["number"]! == number);
            if (issue is null) return FakeHttp.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
            if (request.Method == HttpMethod.Get && rest.Length == 1) return FakeHttp.Json(HttpStatusCode.OK, issue.ToJsonString());
            if (request.Method == HttpMethod.Post && rest.Length == 2 && rest[1] == "comments")
            {
                return LoseReply(out HttpResponseMessage? lost) ? lost! : FakeHttp.Json(HttpStatusCode.Created, "{\"id\":1}");
            }
            if (request.Method == HttpMethod.Patch && rest.Length == 1)
            {
                JsonNode patch = JsonNode.Parse(request.Body)!;
                if (patch["state"] is not null && FailStateChangesWith is { } refuse) return FakeHttp.Json(refuse, FailBody);
                lock (_gate)
                {
                    if (patch["state"] is { } state) issue["state"] = (string)state!;
                    if (patch["state_reason"] is { } reason) issue["state_reason"] = (string)reason!;
                    if (patch["body"] is { } body) issue["body"] = (string)body!;
                }

                return FakeHttp.Json(HttpStatusCode.OK, issue.ToJsonString());
            }
        }

        return FakeHttp.Json(HttpStatusCode.NotFound, "{\"message\":\"unexpected " + request.Method + " " + path + "\"}");
    }
}
