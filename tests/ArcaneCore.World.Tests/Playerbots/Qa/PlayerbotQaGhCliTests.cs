using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A small <c>gh</c>: answers the commands the adapter runs from a list of issues, records every request and reads the body files while they exist.</summary>
internal sealed class FakeGh : IQaProcessRunner
{
    private readonly object _gate = new();
    private readonly List<QaProcessRequest> _requests = [];
    private readonly List<JsonObject> _issues = [];
    private int _next = 200;

    public QaProcessRequest[] Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    /// <summary>The body files as they were when gh was called: path, content.</summary>
    public List<(string Path, string Content, bool ExistedDuringCall)> BodyFiles { get; } = [];

    /// <summary>A result to answer every request with instead of acting.</summary>
    public Func<QaProcessRequest, QaProcessResult?>? Override { get; set; }

    /// <summary>How many calls ran <paramref name="verb"/>; <c>list</c> counts the issue-list calls of either form (<c>gh issue list</c>, <c>gh api .../issues?</c>).</summary>
    public int Count(string verb) => Requests.Count(r => r.Arguments.Count >= 2 && ((r.Arguments[0] == "issue" && r.Arguments[1] == verb) || (verb == "list" && IsApiList(r))));

    /// <summary>
    /// The most issues one call can print before gh runs past its timeout (null: no limit). Models the live failure of 2026-10-17: one <c>gh issue list</c> of 635 issues with
    /// their bodies took 20-40 s and was killed at 30 s on most passes.
    /// </summary>
    public int? MaxIssuesPerCall { get; set; }

    private static bool IsApiList(QaProcessRequest r) => r.Arguments[0] == "api" && r.Arguments[^1].Contains("/issues?", StringComparison.Ordinal);

    /// <summary>The bodies <c>gh issue edit</c> wrote, by issue number.</summary>
    public Dictionary<int, string> Edited { get; } = [];

    public string BodyOf(int number)
    {
        lock (_gate) return (string)_issues.Single(i => (int)i["number"]! == number)["body"]!;
    }

    public bool IsOpen(int number)
    {
        lock (_gate) return (string)_issues.Single(i => (int)i["number"]! == number)["state"]! == "OPEN";
    }

    private Task<QaProcessResult> ApiList(QaProcessRequest request)
    {
        string path = request.Arguments[^1];
        var query = path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        int perPage = int.Parse(query["per_page"], CultureInfo.InvariantCulture);
        int page = int.Parse(query["page"], CultureInfo.InvariantCulture);
        JsonObject[] slice = [.. _issues.Skip((page - 1) * perPage).Take(perPage)];
        if (MaxIssuesPerCall is { } max && slice.Length > max) return Task.FromResult(new QaProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        // The REST shape: lower-case state, state_reason, html_url.
        return Ok(new JsonArray([.. slice.Select(i => (JsonNode?)new JsonObject
        {
            ["number"] = (int)i["number"]!, ["state"] = ((string)i["state"]!).ToLowerInvariant(), ["html_url"] = (string)i["url"]!, ["body"] = (string)i["body"]!,
            ["state_reason"] = i["stateReason"] is { } reason ? ((string)reason!).ToLowerInvariant() : null,
        })]).ToJsonString());
    }

    /// <summary>Whether the search index misses every issue (a lagging index: it answers 0 hits).</summary>
    public bool SearchLags { get; set; }

    /// <summary>The query of a search call: the <c>q=</c> field.</summary>
    public static string QueryOf(QaProcessRequest r) => r.Arguments.Single(a => a.StartsWith("q=", StringComparison.Ordinal))[2..];

    private Task<QaProcessResult> ApiSearch(QaProcessRequest request)
    {
        string query = QueryOf(request);
        int open = query.IndexOf('"', StringComparison.Ordinal);
        int close = query.LastIndexOf('"');
        string phrase = query[(open + 1)..close].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        JsonObject[] hits = SearchLags ? [] : [.. _issues.Where(i => ((string)i["body"]!).Contains(phrase, StringComparison.Ordinal))];
        return Ok(new JsonObject
        {
            ["total_count"] = hits.Length,
            ["incomplete_results"] = false,
            ["items"] = new JsonArray([.. hits.Select(i => (JsonNode?)new JsonObject
            {
                ["number"] = (int)i["number"]!, ["state"] = ((string)i["state"]!).ToLowerInvariant(), ["html_url"] = (string)i["url"]!, ["body"] = (string)i["body"]!,
            })]),
        }.ToJsonString());
    }

    public void SetReason(int number, string reason)
    {
        lock (_gate) _issues.Single(i => (int)i["number"]! == number)["stateReason"] = reason;
    }

    public int Add(string body, bool open = true)
    {
        lock (_gate)
        {
            int number = _next++;
            _issues.Add(new JsonObject { ["number"] = number, ["state"] = open ? "OPEN" : "CLOSED", ["body"] = body, ["url"] = $"https://github.com/{QaData.Repo}/issues/{number}" });
            return number;
        }
    }

    public Task<QaProcessResult> RunAsync(QaProcessRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
            if (Override?.Invoke(request) is { } forced) return Task.FromResult(forced);
            if (request.Arguments.Contains("search/issues")) return ApiSearch(request);
            if (IsApiList(request)) return ApiList(request);
            string verb = request.Arguments[1];
            string? file = request.Arguments.Contains("--body-file") ? request.Arguments[request.Arguments.ToList().IndexOf("--body-file") + 1] : null;
            if (file is not null) BodyFiles.Add((file, File.Exists(file) ? File.ReadAllText(file) : string.Empty, File.Exists(file)));
            switch (verb)
            {
                case "list":
                    if (MaxIssuesPerCall is { } max && _issues.Count > max) return Task.FromResult(new QaProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
                    return Ok(new JsonArray([.. _issues.Select(i => (JsonNode?)JsonNode.Parse(i.ToJsonString()))]).ToJsonString());
                case "view":
                    {
                        int number = int.Parse(request.Arguments[2], CultureInfo.InvariantCulture);
                        JsonObject? issue = _issues.SingleOrDefault(i => (int)i["number"]! == number);
                        return issue is null ? Task.FromResult(new QaProcessResult(1, string.Empty, "GraphQL: Could not resolve to an Issue with the number " + number, false)) : Ok(issue.ToJsonString());
                    }

                case "create":
                    {
                        int number = Add(File.ReadAllText(file!));
                        return Ok($"https://github.com/{QaData.Repo}/issues/{number}\n");
                    }

                case "comment":
                    return Ok("https://github.com/x/y/issues/1#issuecomment-1\n");
                case "close":
                    {
                        int number = int.Parse(request.Arguments[2], CultureInfo.InvariantCulture);
                        _issues.Single(i => (int)i["number"]! == number)["state"] = "CLOSED";
                        return Ok(string.Empty);
                    }

                case "edit":
                    {
                        int number = int.Parse(request.Arguments[2], CultureInfo.InvariantCulture);
                        string body = File.ReadAllText(file!);
                        Edited[number] = body;
                        _issues.Single(i => (int)i["number"]! == number)["body"] = body;
                        return Ok($"https://github.com/{QaData.Repo}/issues/{number}\n");
                    }

                case "reopen":
                    {
                        int number = int.Parse(request.Arguments[2], CultureInfo.InvariantCulture);
                        _issues.Single(i => (int)i["number"]! == number)["state"] = "OPEN";
                        return Ok(string.Empty);
                    }

                default:
                    return Task.FromResult(new QaProcessResult(1, string.Empty, "unknown command", false));
            }
        }
    }

    private static Task<QaProcessResult> Ok(string stdout) => Task.FromResult(new QaProcessResult(0, stdout, string.Empty, false));
}

/// <summary>The <c>gh</c> transport of bot QA: arguments, environment, body files, dedup through the publisher, timeouts and the executable lookup.</summary>
public sealed class PlayerbotQaGhCliTests : IDisposable
{
    private const string A = "aaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccc";
    private const string Exe = "C:\\tools\\gh.exe";

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static PlayerbotQaGitHubOptions Options() => new() { Enabled = true, Repository = QaData.Repo, Publish = true, Transport = PlayerbotQaGitHubTransport.GhCli };

    private PlayerbotQaGhCliClient NewClient(FakeGh gh, string? token = null, TimeSpan? timeout = null, PlayerbotQaGitHubOptions? options = null)
        => new(gh, Exe, QaData.Repo, options ?? Options(), token, _directory, timeout);

    private sealed class Rig
    {
        public required FakeGh Gh { get; init; }

        public required FakeQaState State { get; init; }

        public required PlayerbotQaPublisher Publisher { get; init; }

        public required ListLogger Log { get; init; }

        public int Factories;

        public Task Run() => Publisher.RunAsync(State.All, CancellationToken.None);
    }

    private Rig NewRig(Action<PlayerbotQaGitHubOptions>? configure = null, QaFindingView[]? findings = null, FakeGh? gh = null, string? token = null)
    {
        PlayerbotQaGitHubOptions git = Options();
        git.MinOccurrences = 3;
        git.MinDistinctBots = 2;
        configure?.Invoke(git);
        var options = new PlayerbotQaOptions { GitHub = git };
        gh ??= new FakeGh();
        var state = new FakeQaState(findings ?? [QaData.View(A)]);
        var log = new ListLogger();
        var time = new ManualTime(QaData.T0);
        Rig? rig = null;
        var publisher = new PlayerbotQaPublisher(options, _directory, state, new PlayerbotQaRedactor(), log, name => name == "ARCANECORE_GITHUB_TOKEN" ? token : null,
            _ => throw new InvalidOperationException("the REST transport must not be used"), time, "test-build",
            t =>
            {
                Interlocked.Increment(ref rig!.Factories);
                return new PlayerbotQaGhCliClient(gh, Exe, QaData.Repo, git, t, _directory);
            }, (_, _) => Task.CompletedTask);
        rig = new Rig { Gh = gh, State = state, Publisher = publisher, Log = log };
        return rig;
    }

    private static QaFindingView Finding(string id, long count = 3) => QaData.View(id, count: count);

    // ----- the adapter -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_RunsOneGhApiCallPerPage_WithTheDocumentedArguments_AndIndexesOpenAndClosed()
    {
        var gh = new FakeGh();
        int open = gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx");
        int closed = gh.Add(PlayerbotQaIssueComposer.MarkerOf(B) + "\nx", open: false);
        using PlayerbotQaGhCliClient client = NewClient(gh);

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.Equal(Exe, request.FileName);
        Assert.Equal(["api", "--method", "GET", $"repos/{QaData.Repo}/issues?labels=bot-qa&state=all&per_page=100&page=1"], request.Arguments);
        Assert.True(index.ByMarker(A)!.Open);
        Assert.Equal(open, index.ByMarker(A)!.Number);
        Assert.False(index.ByMarker(B)!.Open);
        Assert.Equal(closed, index.ByMarker(B)!.Number);
        Assert.Null(index.ByMarker(C));
        Assert.True(index.Complete);
    }

    [Fact]
    public async Task List_CarriesGitHubsStateReason_SoANotPlannedIssueIsRecognised()
    {
        var gh = new FakeGh();
        int notPlanned = gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx", open: false);
        gh.SetReason(notPlanned, "NOT_PLANNED");
        gh.Add(PlayerbotQaIssueComposer.MarkerOf(B) + "\nx", open: false);
        using PlayerbotQaGhCliClient client = NewClient(gh);

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        Assert.True(index.ByMarker(A)!.NotPlanned);
        Assert.False(index.ByMarker(B)!.NotPlanned);
    }

    [Fact]
    public async Task List_ThatFillsTheLimit_IsIncomplete()
    {
        var gh = new FakeGh();
        for (int i = 0; i < 100; i++) gh.Add("x");
        using PlayerbotQaGhCliClient client = NewClient(gh, options: new PlayerbotQaGitHubOptions { Enabled = true, Repository = QaData.Repo, MaxListPages = 1 });

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        Assert.False(index.Complete);
    }

    [Fact]
    public async Task Create_PassesTheBodyInATempFile_NotOnTheCommandLine_AndDeletesIt()
    {
        var gh = new FakeGh();
        using PlayerbotQaGhCliClient client = NewClient(gh);
        string body = PlayerbotQaIssueComposer.MarkerOf(A) + "\nsecret-looking multi-line body with \"quotes\" & $(subshell)";

        QaIssueRef created = await client.CreateIssueAsync("[bot-qa] stall: quest 1656 \"x\" & y", body, ["bot-qa", "area:bots"], CancellationToken.None);

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.Equal(200, created.Number);
        Assert.Equal("issue", request.Arguments[0]);
        Assert.Equal("create", request.Arguments[1]);
        Assert.Equal("[bot-qa] stall: quest 1656 \"x\" & y", request.Arguments[request.Arguments.ToList().IndexOf("--title") + 1]); // one argv entry, no shell quoting
        Assert.Equal(["--label", "bot-qa", "--label", "area:bots"], request.Arguments.SkipWhile(a => a != "--label").ToArray());
        Assert.DoesNotContain(request.Arguments, a => a.Contains("secret-looking", StringComparison.Ordinal));
        (string path, string content, bool existed) = Assert.Single(gh.BodyFiles);
        Assert.True(existed);
        Assert.Equal(body, content);
        Assert.False(File.Exists(path)); // deleted after the call
        Assert.Equal(_directory, request.WorkingDirectory);
    }

    [Fact]
    public async Task Create_DeletesTheBodyFile_EvenWhenGhFails()
    {
        var gh = new FakeGh { Override = _ => new QaProcessResult(1, string.Empty, "HTTP 422: Validation Failed", false) };
        using PlayerbotQaGhCliClient client = NewClient(gh);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", "body", ["bot-qa"], CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Comment_UsesABodyFile_AndTheIssueNumber()
    {
        var gh = new FakeGh();
        using PlayerbotQaGhCliClient client = NewClient(gh);

        await client.CommentAsync(123, "more occurrences", CancellationToken.None);

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.Equal(["issue", "comment", "123", "--repo", QaData.Repo, "--body-file"], request.Arguments.Take(6).ToArray());
        Assert.Equal("more occurrences", Assert.Single(gh.BodyFiles).Content);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Timeout_IsATransientFailure_AndTheCreateIsNotRepeated()
    {
        var gh = new FakeGh { Override = _ => new QaProcessResult(-1, string.Empty, string.Empty, true) };
        using PlayerbotQaGhCliClient client = NewClient(gh);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", "body", ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Single(gh.Requests);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Environment_RemovesBothTokenVariables_AndNeverPromptsOrReadsStdin()
    {
        var gh = new FakeGh();
        using PlayerbotQaGhCliClient client = NewClient(gh);

        await client.ListIndexAsync(CancellationToken.None);

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.True(request.Environment.ContainsKey("GITHUB_TOKEN"));
        Assert.Null(request.Environment["GITHUB_TOKEN"]);
        Assert.True(request.Environment.ContainsKey("GH_TOKEN"));
        Assert.Null(request.Environment["GH_TOKEN"]);
        Assert.Equal("1", request.Environment["GH_PROMPT_DISABLED"]);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task Environment_PassesTheTokenAsGhToken_OnlyWhenOneWasHandedIn_AndNeverInAnError()
    {
        var gh = new FakeGh { Override = _ => new QaProcessResult(1, string.Empty, "bad credentials " + QaData.FakeToken, false) };
        using PlayerbotQaGhCliClient client = NewClient(gh, token: QaData.FakeToken);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.ListIndexAsync(CancellationToken.None));

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.Equal(QaData.FakeToken, request.Environment["GH_TOKEN"]);
        Assert.Null(request.Environment["GITHUB_TOKEN"]);
        Assert.DoesNotContain(QaData.FakeToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_OfAMissingIssue_IsNull_ButOtherFailuresThrow()
    {
        var gh = new FakeGh();
        using PlayerbotQaGhCliClient client = NewClient(gh);
        Assert.Null(await client.GetIssueAsync(999, CancellationToken.None));

        gh.Override = _ => new QaProcessResult(4, string.Empty, "authentication required", false);
        await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.GetIssueAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task Create_WithNoUrlInTheOutput_IsTransient_BecauseTheIssueMayExist()
    {
        var gh = new FakeGh { Override = _ => new QaProcessResult(0, "done\n", string.Empty, false) };
        using PlayerbotQaGhCliClient client = NewClient(gh);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", "b", ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
    }

    // ----- through the publisher --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publisher_ListsOnce_ForManyFindings_AndCreatesTheMissingOnes()
    {
        var gh = new FakeGh();
        gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx");
        Rig rig = NewRig(findings: [Finding(A), Finding(B), Finding(C)], gh: gh);

        await rig.Run();

        Assert.Equal(1, gh.Count("list"));
        Assert.Equal(2, gh.Count("create"));
        Assert.Equal(1, gh.Count("comment")); // A exists and is open
        Assert.Equal(0, gh.Count("view"));
        Assert.All(rig.State.All, f => Assert.Equal(QaPublicationState.Published, f.Publication.State));
        Assert.All(gh.Requests, r => Assert.Equal(Exe, r.FileName));
    }

    [Fact]
    public async Task Publisher_CommentsOnAnOpenIssue_UsingTheSameMarkerAndCaps()
    {
        var gh = new FakeGh();
        gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx");
        Rig rig = NewRig(findings: [Finding(A)], gh: gh);

        await rig.Run();
        await rig.Run();

        Assert.Equal(0, gh.Count("create"));
        Assert.Equal(1, gh.Count("comment")); // the second pass has nothing new
        Assert.Equal(1, gh.Count("edit")); // the issue predates the live status block: it is inserted once
        Assert.Equal(2, gh.BodyFiles.Count);
        Assert.All(gh.BodyFiles, file => Assert.NotEmpty(file.Content));
    }

    [Fact]
    public async Task Publisher_DoesNotReopenAClosedIssue_AndRecordsItAsKnownClosed()
    {
        var gh = new FakeGh();
        int closed = gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx", open: false);
        Rig rig = NewRig(findings: [Finding(A)], gh: gh);

        await rig.Run();
        await rig.Run();

        Assert.Equal(0, gh.Count("create"));
        Assert.Equal(0, gh.Count("comment"));
        Assert.Equal(0, gh.Count("reopen"));
        Assert.Equal(1, gh.Count("list")); // the second pass does not even list: the finding is known closed
        Assert.Equal(QaPublicationState.KnownClosed, rig.State.Get(A)!.Publication.State);
        Assert.Equal(closed, rig.State.Get(A)!.Publication.Issue);
    }

    [Fact]
    public async Task Publisher_ReopensAClosedIssue_WhenReopenClosedIsOn()
    {
        var gh = new FakeGh();
        gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx", open: false);
        Rig rig = NewRig(o => o.ReopenClosed = true, [Finding(A)], gh);

        await rig.Run();

        Assert.Equal(1, gh.Count("reopen"));
        Assert.Equal(1, gh.Count("comment"));
        Assert.Equal(0, gh.Count("create"));
    }

    [Fact]
    public async Task Publisher_TimeoutOnCreate_FailsTheFinding_StopsThePass_AndDoesNotRepeatTheCreate()
    {
        var gh = new FakeGh();
        gh.Override = request => request.Arguments[1] == "create" ? new QaProcessResult(-1, string.Empty, string.Empty, true) : null;
        Rig rig = NewRig(findings: [Finding(A), Finding(B)], gh: gh);

        await rig.Run();

        Assert.Equal(1, gh.Count("create"));
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(B)!.Publication.State); // the pass stopped
        Assert.Contains("timed out", rig.State.Get(A)!.Publication.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publisher_ZeroIssueCap_MakesNoGhCallAtAll()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 50).Select(i => Finding(i.ToString("x16", CultureInfo.InvariantCulture)))];
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 0, findings);

        await rig.Run();

        Assert.Empty(rig.Gh.Requests);
    }

    [Fact]
    public async Task Publisher_WithAListCutShort_AndASearchThatFails_DoesNotCreate_BecauseItCannotKnowTheIssueIsAbsent()
    {
        var gh = new FakeGh { Override = r => r.Arguments.Contains("search/issues") ? new QaProcessResult(1, string.Empty, "gh: HTTP 500 server error", false) : null };
        for (int i = 0; i < 100; i++) gh.Add("unrelated");
        Rig rig = NewRig(o => o.MaxListPages = 1, [Finding(A)], gh);

        await rig.Run();

        Assert.Equal(0, gh.Count("create"));
        Assert.Equal(1, SearchCount(gh));
        Assert.NotEqual(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
    }

    private static int SearchCount(FakeGh gh) => gh.Requests.Count(r => r.Arguments.Contains("search/issues"));

    // ----- search (the list is capped at MaxListPages; the 1001st bot-qa issue is beyond it) --------------------------------------------------

    [Fact]
    public async Task Publisher_AMarkerBeyondTheListedPages_IsFoundBySearch_AndNothingIsCreated()
    {
        var gh = new FakeGh();
        for (int i = 0; i < 1000; i++) gh.Add("unrelated " + i.ToString(CultureInfo.InvariantCulture));
        int target = gh.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nx");
        Rig rig = NewRig(o => o.MaxListPages = 10, [Finding(A)], gh);

        await rig.Run();

        Assert.Equal(10, gh.Count("list"));
        Assert.Equal(1, SearchCount(gh));
        Assert.Equal(0, gh.Count("create"));
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(QaPublicationState.Published, view.Publication.State);
        Assert.Equal(target, view.Publication.Issue);
    }

    [Fact]
    public async Task Publisher_ASearchWithNoHit_CreatesTheIssue_AndDoesNotSearchItAgainOnTheNextPass()
    {
        var gh = new FakeGh();
        for (int i = 0; i < 100; i++) gh.Add("unrelated");
        Rig rig = NewRig(o => o.MaxListPages = 1, [Finding(A)], gh);

        await rig.Run();
        await rig.Run();

        Assert.Equal(1, gh.Count("create"));
        Assert.Equal(1, SearchCount(gh));
        Assert.Equal(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task Publisher_ARateLimitedSearch_KeepsTheFindingPending_AndCreatesNothing()
    {
        var gh = new FakeGh { Override = r => r.Arguments.Contains("search/issues") ? new QaProcessResult(1, string.Empty, "gh: API rate limit exceeded for user ID 1. (HTTP 403)", false) : null };
        for (int i = 0; i < 100; i++) gh.Add("unrelated");
        Rig rig = NewRig(o => o.MaxListPages = 1, [Finding(A), Finding(B)], gh);

        await rig.Run();

        Assert.Equal(0, gh.Count("create"));
        Assert.Equal(1, SearchCount(gh)); // a transient failure stops the pass
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);
        Assert.Equal(0, rig.State.Get(A)!.Publication.Issue);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(B)!.Publication.State);
    }

    [Fact]
    public async Task Search_AfterARateLimit_DoesNotCallGhAgain_AndIsTransient()
    {
        var gh = new FakeGh { Override = r => new QaProcessResult(1, string.Empty, "You have exceeded a secondary rate limit", false) };
        using PlayerbotQaGhCliClient client = NewClient(gh);

        var first = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.SearchByMarkerAsync(A, CancellationToken.None));
        var second = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.SearchByMarkerAsync(B, CancellationToken.None));

        Assert.True(first.Transient);
        Assert.True(second.Transient);
        Assert.Equal(1, SearchCount(gh));
    }

    [Fact]
    public async Task Search_ATimeoutOrAnIncompleteResult_IsTransient_NeverNull()
    {
        var gh = new FakeGh { Override = _ => new QaProcessResult(-1, string.Empty, string.Empty, true) };
        using PlayerbotQaGhCliClient client = NewClient(gh);
        var timedOut = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.SearchByMarkerAsync(A, CancellationToken.None));
        Assert.True(timedOut.Transient);
        Assert.Equal(TimeSpan.FromSeconds(30), gh.Requests[0].Timeout);

        gh.Override = _ => new QaProcessResult(0, """{"total_count":0,"incomplete_results":true,"items":[]}""", string.Empty, false);
        var incomplete = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.SearchByMarkerAsync(A, CancellationToken.None));
        Assert.True(incomplete.Transient);

        gh.Override = _ => new QaProcessResult(0, """{"total_count":0,"incomplete_results":false,"items":[]}""", string.Empty, false);
        Assert.Null(await client.SearchByMarkerAsync(A, CancellationToken.None));
    }

    [Fact]
    public async Task Search_RemembersTheIssuesItCreated_WhileTheSearchIndexLags()
    {
        var gh = new FakeGh { SearchLags = true };
        using PlayerbotQaGhCliClient client = NewClient(gh);
        QaIssueRef created = await client.CreateIssueAsync("t", PlayerbotQaIssueComposer.MarkerOf(A) + "\nbody", ["bot-qa"], CancellationToken.None);

        QaIssueRef? found = await client.SearchByMarkerAsync(A, CancellationToken.None);
        QaIssueRef? other = await client.SearchByMarkerAsync(B, CancellationToken.None);

        Assert.Equal(created.Number, found!.Number);
        Assert.Null(other);
        Assert.Equal(1, SearchCount(gh)); // A was answered from memory; only B called gh
    }

    [Fact]
    public async Task Search_PassesTheQueryAsOneArgvEntry_WithQuotesAndBackslashesEscaped()
    {
        var gh = new FakeGh();
        using PlayerbotQaGhCliClient client = NewClient(gh);
        const string id = "we ird\"id\\x & $(rm -rf)";

        await client.SearchByMarkerAsync(id, CancellationToken.None);

        QaProcessRequest request = Assert.Single(gh.Requests);
        Assert.Equal(["api", "--method", "GET", "search/issues", "-f"], request.Arguments.Take(5).ToArray());
        Assert.Equal($"q=repo:{QaData.Repo} is:issue in:body \"arcanecore-bot-qa:we ird\\\"id\\\\x & $(rm -rf)\"", request.Arguments[5]);
        Assert.Equal(["-f", "per_page=30"], request.Arguments.Skip(6).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
    }

    [Fact]
    public async Task Publisher_DryRun_MakesNoGhCall_AndDoesNotEvenResolveGh()
    {
        Rig rig = NewRig(o => o.Publish = false);

        await rig.Run();

        Assert.Empty(rig.Gh.Requests);
        Assert.Equal(0, rig.Factories);
        Assert.True(File.Exists(Path.Combine(_directory, "pending-issues", A + ".md")));
    }

    [Fact]
    public async Task Publisher_NeedsNoTokenVariable_ForGh_ButWarnsWhenGhIsMissing()
    {
        var options = new PlayerbotQaOptions { GitHub = Options() };
        var state = new FakeQaState(Finding(A));
        var log = new ListLogger();
        var publisher = new PlayerbotQaPublisher(options, _directory, state, new PlayerbotQaRedactor(), log, _ => null, null, new ManualTime(QaData.T0), "b", _ => null);

        await publisher.RunAsync(state.All, CancellationToken.None);
        await publisher.RunAsync(state.All, CancellationToken.None);

        Assert.Single(log.At(Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Contains("gh", log.At(Microsoft.Extensions.Logging.LogLevel.Warning).Single(), StringComparison.Ordinal);
        Assert.Equal(QaPublicationState.Pending, state.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task Publisher_GhPassToken_RequiresTheVariable_AndHandsItToTheChild()
    {
        Rig missing = NewRig(o => o.GhPassToken = true, token: null);
        await missing.Run();
        Assert.Empty(missing.Gh.Requests);

        Rig given = NewRig(o => o.GhPassToken = true, token: QaData.FakeToken);
        await given.Run();
        Assert.All(given.Gh.Requests, r => Assert.Equal(QaData.FakeToken, r.Environment["GH_TOKEN"]));
    }

    // ----- the executable ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveExecutable_FindsABareNameOnPath_AddsExeOnWindows_AndNeverSearchesTheCurrentDirectory()
    {
        string sep = Path.PathSeparator.ToString();
        string tools = Path.Combine(Path.GetTempPath(), "tools");
        string other = Path.Combine(Path.GetTempPath(), "other");
        string win = Path.Combine(other, "gh.exe");
        string nix = Path.Combine(other, "gh");

        Assert.Equal(win, PlayerbotQaGhCliClient.ResolveExecutable("gh", _ => tools + sep + other, p => p == win, windows: true));
        Assert.Equal(nix, PlayerbotQaGhCliClient.ResolveExecutable("gh", _ => tools + sep + other, p => p == nix, windows: false));
        Assert.Null(PlayerbotQaGhCliClient.ResolveExecutable("gh", _ => tools, p => p == "gh.exe" || p == "gh", windows: true)); // only the current directory has it
        Assert.Null(PlayerbotQaGhCliClient.ResolveExecutable("gh", _ => "relative" + sep + tools, p => p.StartsWith("relative", StringComparison.Ordinal), windows: false));
    }

    [Fact]
    public void ResolveExecutable_AFullPath_MustExist_AndARelativePathWithADirectoryIsRefused()
    {
        string full = Path.Combine(Path.GetTempPath(), "gh-custom.exe");

        Assert.Equal(full, PlayerbotQaGhCliClient.ResolveExecutable(full, _ => null, p => p == full, windows: true));
        Assert.Null(PlayerbotQaGhCliClient.ResolveExecutable(full, _ => null, _ => false, windows: true));
        Assert.Null(PlayerbotQaGhCliClient.ResolveExecutable("bin/gh", _ => null, _ => true, windows: false));
    }

    // ----- the real runner --------------------------------------------------------------------------------------------------------------------

    private static string? Dotnet() => PlayerbotQaGhCliClient.ResolveExecutable("dotnet", Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    [Fact]
    public async Task RealRunner_RunsAnArgumentList_AndCapturesTheOutput()
    {
        if (Dotnet() is not { } dotnet) return;

        QaProcessResult result = await new QaProcessRunner().RunAsync(new QaProcessRequest(dotnet, ["--version"], Path.GetTempPath(), new Dictionary<string, string?>(), TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEmpty(result.StdOut.Trim());
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task RealRunner_KillsAChildAtTheTimeout()
    {
        if (Dotnet() is not { } dotnet) return;

        QaProcessResult result = await new QaProcessRunner().RunAsync(new QaProcessRequest(dotnet, ["--info"], Path.GetTempPath(), new Dictionary<string, string?>(), TimeSpan.FromMilliseconds(1)), CancellationToken.None);

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RealRunner_AMissingExecutable_IsAPermanentFailure()
    {
        string missing = Path.Combine(_directory, "no-such-gh");

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => new QaProcessRunner().RunAsync(
            new QaProcessRequest(missing, [], Path.GetTempPath(), new Dictionary<string, string?>(), TimeSpan.FromSeconds(5)), CancellationToken.None));

        Assert.False(ex.Transient);
    }

    // ----- the issue list at live size (2026-10-17: "gh issue list timed out after 30 s" on most passes) -----------------------------------------

    [Fact]
    public async Task List_Of635Issues_DoesNotRideOnOneCall_SoItNoLongerTimesOut()
    {
        // Live: 635 bot-qa issues, about 1.9 MB of bodies; one `gh issue list --limit 1000` took 20-40 s and was killed at 30 s, so the pass failed before publishing anything.
        var gh = new FakeGh { MaxIssuesPerCall = 100 };
        for (int i = 0; i < 635; i++) gh.Add(PlayerbotQaIssueComposer.MarkerOf(i.ToString("x16", CultureInfo.InvariantCulture)) + "\nbody", open: i % 3 != 0);
        using PlayerbotQaGhCliClient client = NewClient(gh);

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        Assert.Equal(635, index.Count);
        Assert.True(index.Complete);
        Assert.Equal(7, gh.Count("list")); // one call per page of 100, each under its own timeout
        Assert.All(gh.Requests, r => Assert.Equal(TimeSpan.FromSeconds(30), r.Timeout));
        Assert.Equal(["page=1", "page=2", "page=3", "page=4", "page=5", "page=6", "page=7"], gh.Requests.Select(r => r.Arguments[^1].Split('&')[^1]).ToArray());
        Assert.False(index.ByMarker(0.ToString("x16", CultureInfo.InvariantCulture))!.Open);
        Assert.True(index.ByMarker(634.ToString("x16", CultureInfo.InvariantCulture))!.Open);
    }

    [Fact]
    public async Task List_OfAnExactMultipleOfAPage_ReadsOneEmptyPage_AndIsComplete()
    {
        var gh = new FakeGh();
        for (int i = 0; i < 200; i++) gh.Add("x");
        using PlayerbotQaGhCliClient client = NewClient(gh);

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        Assert.Equal(200, index.Count);
        Assert.True(index.Complete);
        Assert.Equal(3, gh.Count("list"));
    }

    [Fact]
    public async Task List_SkipsPullRequests_AndKeepsEachIssuesBody()
    {
        var gh = new FakeGh
        {
            Override = r => r.Arguments[0] == "api"
                ? new QaProcessResult(0, """[{"number":7,"state":"open","html_url":"https://github.com/o/r/pull/7","body":"<!-- arcanecore-bot-qa:aaaaaaaaaaaaaaaa -->","pull_request":{}},{"number":8,"state":"closed","state_reason":"not_planned","html_url":"https://github.com/o/r/issues/8","body":"<!-- arcanecore-bot-qa:bbbbbbbbbbbbbbbb -->\nrest"}]""", string.Empty, false)
                : null,
        };
        using PlayerbotQaGhCliClient client = NewClient(gh);

        QaIssueIndex index = await client.ListIndexAsync(CancellationToken.None);

        Assert.Null(index.ByNumber(7));
        Assert.Null(index.ByMarker(A));
        Assert.True(index.ByMarker(B)!.NotPlanned);
        Assert.Equal("<!-- arcanecore-bot-qa:bbbbbbbbbbbbbbbb -->\nrest", index.BodyOf(8));
    }

    [Fact]
    public async Task EditBody_UsesABodyFile_AndCloseAsCompleted_SaysCompleted()
    {
        var gh = new FakeGh();
        int number = gh.Add("old");
        using PlayerbotQaGhCliClient client = NewClient(gh);

        await client.EditBodyAsync(number, "new body", CancellationToken.None);
        await client.CloseAsCompletedAsync(number, CancellationToken.None);

        Assert.Equal(["issue", "edit", number.ToString(CultureInfo.InvariantCulture), "--repo", QaData.Repo, "--body-file"], gh.Requests[0].Arguments.Take(6).ToArray());
        Assert.Equal("new body", gh.BodyOf(number));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(["issue", "close", number.ToString(CultureInfo.InvariantCulture), "--repo", QaData.Repo, "--reason", "completed"], gh.Requests[1].Arguments);
        Assert.False(gh.IsOpen(number));
    }

    // ----- options ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultToRest_BindGhCliFromConfiguration_AndValidateTheGhPath()
    {
        Assert.Equal(PlayerbotQaGitHubTransport.Rest, new PlayerbotQaGitHubOptions().Transport);
        Assert.Equal("gh", new PlayerbotQaGitHubOptions().GhPath);
        Assert.False(new PlayerbotQaGitHubOptions().GhPassToken);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Qa:GitHub:Transport"] = "GhCli",
            ["World:Playerbots:Qa:GitHub:GhPath"] = "C:\\Program Files\\GitHub CLI\\gh.exe",
        }).Build();
        var options = new PlayerbotQaOptions();
        PlayerbotQaOptions.ApplyConfiguration(options, configuration);

        Assert.Equal(PlayerbotQaGitHubTransport.GhCli, options.GitHub.Transport);
        Assert.Equal("C:\\Program Files\\GitHub CLI\\gh.exe", options.GitHub.GhPath);
        Assert.Empty(options.Validate());
        options.GitHub.GhPath = " ";
        Assert.Contains(options.Validate(), p => p.Contains("GhPath", StringComparison.Ordinal));
        options.GitHub.GhPath = "gh";
        options.GitHub.Transport = (PlayerbotQaGitHubTransport)7;
        Assert.Contains(options.Validate(), p => p.Contains("Transport", StringComparison.Ordinal));
    }
}
