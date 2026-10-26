using System.Net;
using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A publication state backed by a dictionary, applying updates the way the aggregator does.</summary>
internal sealed class FakeQaState : IQaPublicationState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, QaFindingView> _views = new(StringComparer.Ordinal);

    public FakeQaState(params QaFindingView[] views)
    {
        foreach (QaFindingView view in views) _views[view.Id] = view;
    }

    public QaFindingView? Get(string id)
    {
        lock (_gate) return _views.GetValueOrDefault(id);
    }

    public void Apply(string id, QaPublicationUpdate update)
    {
        lock (_gate)
        {
            if (!_views.TryGetValue(id, out QaFindingView? view)) return;
            _views[id] = view with
            {
                Publication = update.Publication,
                LastCommentUtc = update.LastCommentUtc ?? view.LastCommentUtc,
                OccurrencesReported = update.OccurrencesReported ?? view.OccurrencesReported,
            };
        }
    }

    public void Set(QaFindingView view)
    {
        lock (_gate) _views[view.Id] = view;
    }

    public QaFindingView[] All
    {
        get
        {
            lock (_gate) return [.. _views.Values];
        }
    }
}

/// <summary>
/// The publisher: a dry run writes <c>pending-issues/</c> and makes no network call; publishing finds by marker, comments on open issues, leaves
/// closed ones alone, honours the hourly caps and the comment interval, never duplicates under concurrency, and never lets the token out.
/// </summary>
public sealed class PlayerbotQaPublisherTests : IDisposable
{
    private const string A = "aaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccc";
    private const string TokenVariable = "ARCANECORE_GITHUB_TOKEN";

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private sealed class Rig
    {
        public required PlayerbotQaOptions Options { get; init; }

        public required FakeGitHub GitHub { get; init; }

        public required FakeQaState State { get; init; }

        public required ListLogger Log { get; init; }

        public required ManualTime Time { get; init; }

        public required PlayerbotQaPublisher Publisher { get; init; }

        public required string Directory { get; init; }

        public required List<TimeSpan> Delays { get; init; }

        public int ClientsCreated;

        public required List<TimeSpan> LookupDelays { get; init; }

        public Task Run() => Publisher.RunAsync(State.All, CancellationToken.None);

        public string Folder => Path.Combine(Directory, "pending-issues");
    }

    private Rig NewRig(Action<PlayerbotQaGitHubOptions>? configure = null, QaFindingView[]? findings = null, string? token = QaData.FakeToken, FakeGitHub? github = null,
        bool publish = true, PlayerbotQaRedactor? redactor = null, HttpMessageHandler? handler = null)
    {
        var options = new PlayerbotQaOptions
        {
            GitHub = { Enabled = true, Repository = QaData.Repo, Publish = publish, MinOccurrences = 3, MinDistinctBots = 2 },
        };
        configure?.Invoke(options.GitHub);
        github ??= new FakeGitHub();
        var time = new ManualTime(QaData.T0);
        var log = new ListLogger();
        var delays = new List<TimeSpan>();
        var state = new FakeQaState(findings ?? [QaData.View(A)]);
        Rig? rig = null;
        List<TimeSpan> lookupDelays = [];
        var publisher = new PlayerbotQaPublisher(options, _directory, state, redactor ?? new PlayerbotQaRedactor(), log,
            name => name == TokenVariable ? token : null,
            t =>
            {
                Interlocked.Increment(ref rig!.ClientsCreated);
                return new PlayerbotQaGitHubClient(handler ?? github.Http, QaData.Repo, t, options.GitHub, (span, _) =>
                {
                    delays.Add(span);
                    return Task.CompletedTask;
                }, null, () => time.GetUtcNow());
            },
            time, "test-build", null, (span, _) =>
            {
                lock (lookupDelays) lookupDelays.Add(span);
                return Task.CompletedTask;
            });
        rig = new Rig { Options = options, GitHub = github, State = state, Log = log, Time = time, Publisher = publisher, Directory = _directory, Delays = delays, LookupDelays = lookupDelays };
        return rig;
    }

    private static QaFindingView Finding(string id, double severity = 30, long count = 3, int bots = 2, string subject = "quest 1656", string message = "Playerbot <bot> stalled 120s: goal=Quest")
        => QaData.View(id, severity, count, subject: subject, bots: bots, samples: [new QaSample(QaData.T0, true, 0, -5608.8f, -439.4f, 401.2f, [], message)]);

    // ----- dry run ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DryRun_WritesPendingIssueFiles_AndMakesNoHttpCall()
    {
        Rig rig = NewRig(publish: false);

        await rig.Run();

        string md = Path.Combine(rig.Folder, A + ".md");
        string json = Path.Combine(rig.Folder, A + ".json");
        Assert.True(File.Exists(md));
        Assert.True(File.Exists(json));
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(A), File.ReadAllText(md), StringComparison.Ordinal);
        JsonNode file = JsonNode.Parse(File.ReadAllText(json))!;
        Assert.Equal(A, (string)file["Fingerprint"]!);
        Assert.Equal(QaData.Repo, (string)file["Repository"]!);
        Assert.Equal(PlayerbotQaIssueComposer.MarkerOf(A), (string)file["Marker"]!);
        Assert.StartsWith("[bot-qa] ", (string)file["Title"]!, StringComparison.Ordinal);
        Assert.Empty(rig.GitHub.Http.Requests);
        Assert.Equal(0, rig.ClientsCreated); // not even a client (and so not a token read into one) exists in a dry run
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(QaPublicationState.DryRun, view.Publication.State);
        Assert.Equal(3, view.OccurrencesReported);
        Assert.Contains(rig.Log.At(LogLevel.Information), m => m.Contains("dry run", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(rig.Folder, "*.tmp"));
    }

    [Fact]
    public async Task DryRun_DoesNotReadTheTokenVariable()
    {
        int reads = 0;
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo } };
        var publisher = new PlayerbotQaPublisher(options, _directory, new FakeQaState(QaData.View(A)), new PlayerbotQaRedactor(), new ListLogger(),
            _ =>
            {
                reads++;
                return QaData.FakeToken;
            }, _ => throw new InvalidOperationException("a dry run must not create a client"), new ManualTime(QaData.T0), "b");

        await publisher.RunAsync([QaData.View(A)], CancellationToken.None);

        Assert.Equal(0, reads);
        Assert.True(File.Exists(Path.Combine(_directory, "pending-issues", A + ".md")));
    }

    [Fact]
    public async Task DryRun_IsNotRewritten_UntilTheCountGrows()
    {
        Rig rig = NewRig(publish: false);
        await rig.Run();
        string md = Path.Combine(rig.Folder, A + ".md");
        File.WriteAllText(md, "edited by a person");

        await rig.Run();
        Assert.Equal("edited by a person", File.ReadAllText(md));

        rig.State.Set(rig.State.Get(A)! with { Count = 9 });
        await rig.Run();
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(A), File.ReadAllText(md), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRun_BodyIsRedacted()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);
        Rig rig = NewRig(publish: false, findings: [Finding(A, message: "Playerbot Ironwander at 203.0.113.9 mail a@b.example token=abcdef123456 " + QaData.FakeToken)], redactor: redactor);

        await rig.Run();

        string md = File.ReadAllText(Path.Combine(rig.Folder, A + ".md"));
        Assert.DoesNotContain("Ironwander", md, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", md, StringComparison.Ordinal);
        Assert.DoesNotContain("a@b.example", md, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", md, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.FakeToken, md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReporterDisabled_DoesNothing()
    {
        Rig rig = NewRig(o => o.Enabled = false);

        await rig.Run();

        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(rig.GitHub.Http.Requests);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task DryRun_UnwritableFolder_WarnsOnce_AndLeavesTheFindingPending()
    {
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo } };
        var log = new ListLogger();
        var state = new FakeQaState(QaData.View(A), QaData.View(B));
        var publisher = new PlayerbotQaPublisher(options, blocker, state, new PlayerbotQaRedactor(), log, _ => null, null, new ManualTime(QaData.T0), "b");

        await publisher.RunAsync(state.All, CancellationToken.None);

        Assert.Single(log.At(LogLevel.Warning));
        Assert.All(state.All, f => Assert.Equal(QaPublicationState.Pending, f.Publication.State));
    }

    // ----- thresholds and the token ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BelowTheThresholds_NothingIsReported_ButDataFindingsAreExemptFromTheBotCount()
    {
        Rig rig = NewRig(findings:
        [
            Finding(A, count: 2),
            Finding(B, bots: 1),
            QaData.View(C, kind: QaSignalKind.RelayScriptMissing, bots: 0, count: 3),
        ]);

        await rig.Run();

        Assert.Equal(1, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(B)!.Publication.State);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(C)!.Publication.State);
    }

    /// <summary>
    /// A stall of a homed bot outside its home is expected (<see cref="QaCause.OutsideHome"/>) and never becomes an issue, whatever its
    /// count; the report still lists it. The bot-QA issues of 2026-10-11 (#143 quest 1097, #144 quest 400 and 18 more shared set-asides)
    /// came almost entirely from such stalls.
    /// </summary>
    [Fact]
    public async Task OutsideHomeFindings_AreNeverReported()
    {
        Rig rig = NewRig(findings: [QaData.View(A, count: 50, bots: 20, cause: QaCause.OutsideHome), QaData.View(B, count: 50, bots: 20)]);

        await rig.Run();

        Assert.Equal(1, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(B)!.Publication.State);
    }

    /// <summary>A creature held at its invincibility threshold (PlayerbotQaSpellMonitor) is listed in the report only; any other bot anomaly is published.</summary>
    [Fact]
    public async Task HeldTargetAnomalies_AreReportOnly_ButOtherAnomaliesArePublished()
    {
        QaFindingView held = QaData.View(A, count: 50, bots: 1, kind: QaSignalKind.Anomaly) with
        {
            Goal = PlayerbotQaSpellMonitor.HeldTargetGoal, Reason = PlayerbotQaSpellMonitor.HeldTargetReason + "7668",
        };
        QaFindingView normal = QaData.View(B, count: 1, bots: 1, kind: QaSignalKind.Anomaly) with { Goal = "bot-travel", Reason = "stuck" };
        Rig rig = NewRig(findings: [held, normal]);

        await rig.Run();

        Assert.Equal(1, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(B)!.Publication.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Publish_WithoutAToken_MakesNoCall_AndWarnsOnce(string? token)
    {
        Rig rig = NewRig(token: token);

        await rig.Run();
        await rig.Run();

        Assert.Empty(rig.GitHub.Http.Requests);
        Assert.Equal(0, rig.ClientsCreated);
        Assert.Single(rig.Log.At(LogLevel.Warning));
        Assert.Contains(TokenVariable, rig.Log.At(LogLevel.Warning).Single(), StringComparison.Ordinal);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
    }

    // ----- publish: create, comment, closed ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_NoMatchingIssue_CreatesOne_WithMarkerTitleAndLabels()
    {
        Rig rig = NewRig();

        await rig.Run();

        Assert.Equal(1, rig.GitHub.Creates);
        SeenRequest create = rig.GitHub.Http.Requests.Single(r => r.Method == HttpMethod.Post);
        JsonNode payload = JsonNode.Parse(create.Body)!;
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(A), (string)payload["body"]!, StringComparison.Ordinal);
        Assert.StartsWith("[bot-qa] ", (string)payload["title"]!, StringComparison.Ordinal);
        Assert.Equal("bot-qa", (string)payload["labels"]![0]!);
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(QaPublicationState.Published, view.Publication.State);
        Assert.True(view.Publication.Issue > 0);
        Assert.Equal(3, view.OccurrencesReported);
    }

    [Fact]
    public async Task Publish_SecondPassWithNothingNew_MakesNoCall()
    {
        Rig rig = NewRig();
        await rig.Run();
        int before = rig.GitHub.Http.Requests.Length;

        await rig.Run();

        Assert.Equal(before, rig.GitHub.Http.Requests.Length);
        Assert.Equal(1, rig.GitHub.Creates);
    }

    [Fact]
    public async Task Publish_OpenIssueWithTheMarker_GetsAComment_AndNoSecondIssue()
    {
        var github = new FakeGitHub();
        int number = github.Add(PlayerbotQaIssueComposer.MarkerOf(A) + "\nolder report");
        Rig rig = NewRig(github: github);

        await rig.Run();

        Assert.Equal(0, github.Creates);
        Assert.Equal(1, github.Comments);
        Assert.Contains("3 new occurrences", github.CommentBodies.Single(), StringComparison.Ordinal);
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(number, view.Publication.Issue);
        Assert.Equal(QaPublicationState.Published, view.Publication.State);
        Assert.Equal(QaData.T0, view.LastCommentUtc);
        Assert.Equal(3, view.OccurrencesReported);
    }

    [Fact]
    public async Task Publish_ClosedIssueWithTheMarker_IsLeftAlone_AndRecordedAsKnownClosed()
    {
        var github = new FakeGitHub();
        int number = github.Add(PlayerbotQaIssueComposer.MarkerOf(A), open: false);
        Rig rig = NewRig(github: github);

        await rig.Run();

        Assert.Equal(0, github.Creates);
        Assert.Equal(0, github.Comments);
        Assert.Equal(0, github.Patches);
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(QaPublicationState.KnownClosed, view.Publication.State);
        Assert.Equal(number, view.Publication.Issue);
        Assert.Equal("closed", (string)github.Get(number)["state"]!);

        // Later passes do not even ask GitHub again.
        int calls = github.Http.Requests.Length;
        rig.State.Set(view with { Count = 50 });
        await rig.Run();
        Assert.Equal(calls, github.Http.Requests.Length);
    }

    [Fact]
    public async Task Publish_ClosedIssue_WithReopenClosed_IsReopened_ThenCommented()
    {
        var github = new FakeGitHub();
        int number = github.Add(PlayerbotQaIssueComposer.MarkerOf(A), open: false);
        Rig rig = NewRig(o => o.ReopenClosed = true, github: github);

        await rig.Run();

        Assert.Equal(1, github.Patches);
        Assert.Equal("open", (string)github.Get(number)["state"]!);
        Assert.Equal(1, github.Comments);
        Assert.Equal(0, github.Creates);
    }

    [Fact]
    public async Task Publish_IssueNumberKnownLocally_ButMarkerGone_KeepsTheNumber_AndNeverCreates()
    {
        var github = new FakeGitHub();
        int stale = github.Add("someone edited the marker away");
        Rig rig = NewRig(github: github, findings: [QaData.View(A) with { Publication = new QaPublication(QaPublicationState.Failed, stale, "earlier") }]);

        await rig.Run();

        Assert.Equal(0, github.Creates); // the same policy for a Failed and a Published finding: a number in the list is the finding's, whatever its body says
        Assert.Equal(stale, rig.State.Get(A)!.Publication.Issue);
    }

    [Fact]
    public async Task Publish_PullRequestCarryingTheMarker_DoesNotCountAsTheIssue()
    {
        var github = new FakeGitHub();
        github.Add(PlayerbotQaIssueComposer.MarkerOf(A), pullRequest: true);
        Rig rig = NewRig(github: github);

        await rig.Run();

        Assert.Equal(1, github.Creates);
    }

    // ----- caps and interval -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_HourlyIssueCap_LimitsCreates_UntilTheHourPasses()
    {
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 2, findings: [Finding(A, 50), Finding(B, 40), Finding(C, 30)]);

        await rig.Run();

        Assert.Equal(2, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(B)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(C)!.Publication.State); // the lowest severity waits
        Assert.Contains(rig.Log.At(LogLevel.Information), m => m.Contains("hourly issue cap", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromMinutes(59));
        await rig.Run();
        Assert.Equal(2, rig.GitHub.Creates);

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await rig.Run();
        Assert.Equal(3, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(C)!.Publication.State);
    }

    [Fact]
    public async Task Publish_ZeroIssueCap_CreatesNothing()
    {
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 0);

        await rig.Run();

        Assert.Equal(0, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task Publish_HourlyCommentCap_LimitsComments()
    {
        var github = new FakeGitHub();
        github.Add(PlayerbotQaIssueComposer.MarkerOf(A));
        github.Add(PlayerbotQaIssueComposer.MarkerOf(B));
        Rig rig = NewRig(o => o.MaxCommentsPerHour = 1, github: github, findings: [Finding(A, 50), Finding(B, 40)]);

        await rig.Run();

        Assert.Equal(1, github.Comments);
        Assert.Equal(QaData.T0, rig.State.Get(A)!.LastCommentUtc);
        Assert.Null(rig.State.Get(B)!.LastCommentUtc);
        Assert.Equal(0, rig.State.Get(B)!.OccurrencesReported); // not told yet, so it is retried
        Assert.Equal(QaPublicationState.Published, rig.State.Get(B)!.Publication.State);

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Run();
        Assert.Equal(2, github.Comments);
    }

    [Fact]
    public async Task Publish_CommentInterval_AllowsOneCommentPerFindingPerInterval()
    {
        var github = new FakeGitHub();
        github.Add(PlayerbotQaIssueComposer.MarkerOf(A));
        Rig rig = NewRig(o => o.CommentIntervalHours = 24, github: github);
        await rig.Run();
        Assert.Equal(1, github.Comments);

        rig.State.Set(rig.State.Get(A)! with { Count = 10 });
        rig.Time.Advance(TimeSpan.FromHours(23));
        await rig.Run();
        Assert.Equal(1, github.Comments);
        Assert.Equal(3, rig.State.Get(A)!.OccurrencesReported); // the new occurrences wait for the interval

        rig.Time.Advance(TimeSpan.FromHours(2));
        await rig.Run();
        Assert.Equal(2, github.Comments);
        Assert.Contains("7 new occurrences", github.CommentBodies[1], StringComparison.Ordinal);
        Assert.Equal(10, rig.State.Get(A)!.OccurrencesReported);
    }

    [Fact]
    public async Task Ledger_CountsTheLastHourOnly_AndSurvivesARestart()
    {
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 1, findings: [Finding(A, 50), Finding(B, 40)]);
        await rig.Run();
        Assert.Equal(1, rig.GitHub.Creates);

        // "Restart": a new publisher over a new GitHub and the same persisted ledger (through the real findings file).
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        Assert.True(store.Save(new QaStoreFile { Ledger = rig.Publisher.Ledger.Export() }));
        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;
        Rig again = NewRig(o => o.MaxIssuesPerHour = 1, findings: [Finding(B, 40)]);
        again.Publisher.Ledger.Restore(loaded.Ledger);

        await again.Run();

        Assert.Equal(0, again.GitHub.Creates); // the hour has not passed in the restored ledger
        Assert.Equal(QaPublicationState.Pending, again.State.Get(B)!.Publication.State);
        again.Time.Advance(TimeSpan.FromMinutes(61));
        await again.Run();
        Assert.Equal(1, again.GitHub.Creates);
    }

    // ----- failures ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateThatTimesOutAfterTheServerAcceptedIt_FailsOnce_AndTheNextPassAdoptsTheIssue()
    {
        var github = new FakeGitHub { TimeoutsAfterApply = 1 };
        Rig rig = NewRig(github: github);

        await rig.Run();

        Assert.Equal(1, github.Creates);
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);

        await rig.Run();

        Assert.Equal(1, github.Creates); // exactly one issue exists on GitHub
        QaFindingView view = rig.State.Get(A)!;
        Assert.Equal(QaPublicationState.Published, view.Publication.State);
        Assert.True(view.Publication.Issue > 0);
    }

    [Fact]
    public async Task TransientFailure_StopsThePass_AndLeavesEveryFindingFailedOrPending()
    {
        var github = new FakeGitHub { FailWith = HttpStatusCode.ServiceUnavailable };
        Rig rig = NewRig(github: github, findings: [Finding(A, 50), Finding(B, 40)]);

        await rig.Run();

        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(B)!.Publication.State); // the pass stopped; B was not tried
        Assert.Equal(PlayerbotQaGitHubClient.MaxAttempts, github.Http.Requests.Length);
        Assert.Equal(0, github.Creates);
    }

    [Fact]
    public async Task PermanentFailure_ContinuesWithTheNextFinding()
    {
        var github = new FakeGitHub { FailWith = HttpStatusCode.Forbidden };
        Rig rig = NewRig(github: github, findings: [Finding(A, 50), Finding(B, 40)]);

        await rig.Run();

        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(B)!.Publication.State);
    }

    [Fact]
    public async Task FailedFindings_AreRetriedAfterTheSuccessfulOnes_NotDropped()
    {
        var github = new FakeGitHub { FailWith = HttpStatusCode.Forbidden };
        Rig rig = NewRig(github: github, findings: [Finding(A, 50)]);
        await rig.Run();
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(A)!.Publication.State);

        github.FailWith = null;
        await rig.Run();

        Assert.Equal(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task Cancellation_PropagatesOutOfThePass()
    {
        Rig rig = NewRig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Publisher.RunAsync(rig.State.All, cts.Token));

        Assert.Equal(QaPublicationState.Pending, rig.State.Get(A)!.Publication.State);
    }

    [Fact]
    public async Task ConcurrentPasses_ForTheSameFinding_CreateExactlyOneIssue()
    {
        var github = new FakeGitHub(() => Task.Delay(150));
        Rig rig = NewRig(github: github);

        await Task.WhenAll(rig.Run(), rig.Run(), rig.Run());

        Assert.Equal(1, github.Creates);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
    }

    // ----- the token never leaves --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(QaData.FakeToken)]
    [InlineData("zz-plain-secret-without-any-known-shape-424242")]
    public async Task TheToken_NeverAppearsInLogs_State_FilesOrRequestBodies(string token)
    {
        var github = new FakeGitHub { FailWith = HttpStatusCode.Unauthorized, FailBody = "{\"message\":\"Bad credentials for " + token + "\",\"echo\":\"Bearer " + token + "\"}" };
        Rig rig = NewRig(token: token, github: github, findings: [Finding(A, message: "Playerbot <bot> stalled; leaked " + token)]);

        await rig.Run();
        github.FailWith = null;
        await rig.Run(); // now it succeeds and creates the issue whose evidence held the token

        foreach ((LogLevel _, string message) in rig.Log.Entries) Assert.DoesNotContain(token, message, StringComparison.Ordinal);
        foreach (QaFindingView view in rig.State.All) Assert.DoesNotContain(token, view.Publication.ToString(), StringComparison.Ordinal);
        foreach (SeenRequest request in github.Http.Requests)
        {
            Assert.DoesNotContain(token, request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(token, request.Uri, StringComparison.Ordinal);
            Assert.Equal("Bearer " + token, request.Headers["Authorization"]); // the header is the only place it travels
        }

        Assert.Equal(1, github.Creates);
    }

    [Fact]
    public async Task FailureReason_IsRedacted_AndBounded()
    {
        var github = new FakeGitHub { FailWith = HttpStatusCode.BadRequest, FailBody = "{\"message\":\"token=hunter2hunter2 from 198.51.100.7 " + new string('x', 5000) + "\"}" };
        Rig rig = NewRig(github: github);

        await rig.Run();

        string reason = rig.State.Get(A)!.Publication.Reason!;
        Assert.True(reason.Length <= 300);
        Assert.DoesNotContain("hunter2", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.7", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedException_FailsTheFinding_WithItsTypeOnly_AndTheNextFindingIsTried()
    {
        string leak = "secret detail " + QaData.FakeToken;
        var handler = new FakeHttp((_, _) => throw new InvalidOperationException(leak));
        Rig rig = NewRig(findings: [Finding(A, 50), Finding(B, 40)], handler: handler);

        await rig.Run();

        Assert.Equal("Failed(InvalidOperationException)", rig.State.Get(A)!.Publication.ToString());
        Assert.Equal(QaPublicationState.Failed, rig.State.Get(B)!.Publication.State);
        foreach ((LogLevel _, string message) in rig.Log.Entries) Assert.DoesNotContain("secret detail", message, StringComparison.Ordinal);
    }

    // ----- bounded calls ---------------------------------------------------------------------------------------------------------------------

    private static string IdOf(int i) => i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    private static int Lists(Rig rig)
        => rig.GitHub.Http.Requests.Count(r => r.Method == HttpMethod.Get && r.PathAndQuery.StartsWith($"/repos/{QaData.Repo}/issues?", StringComparison.Ordinal));

    private static int Searches(Rig rig) => rig.GitHub.Http.Requests.Count(r => r.PathAndQuery.StartsWith("/search/issues", StringComparison.Ordinal));

    [Fact]
    public async Task FiftyPendingFindings_WithAZeroIssueCap_MakeNoSearch_AndAtMostOneList()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 50).Select(i => Finding(IdOf(i)))];
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 0, findings);

        await rig.Run();
        await rig.Run();

        Assert.Equal(0, Searches(rig));
        Assert.True(Lists(rig) <= 1, "at most one list");
        Assert.Equal(0, rig.GitHub.Creates);
        Assert.Equal(0, rig.GitHub.Gets - Lists(rig));
        Assert.All(rig.State.All, f => Assert.Equal(QaPublicationState.Pending, f.Publication.State));
    }

    [Fact]
    public async Task AFullIssueCap_StopsLookingUpFindingsWithNoIssue_AfterTheLastCreate()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 30).Select(i => Finding(IdOf(i)))];
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 2, findings);

        await rig.Run();

        Assert.Equal(2, rig.GitHub.Creates);
        Assert.Equal(2, Searches(rig)); // one search per create; the 28 others were skipped before any lookup
        Assert.Equal(1, Lists(rig));
    }

    [Fact]
    public async Task APass_ListsOnce_SearchesAtMostFivePerPass_AndSpacesTheSearches()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 20).Select(i => Finding(IdOf(i)))];
        Rig rig = NewRig(o => o.MaxIssuesPerHour = 100, findings);

        await rig.Run();

        Assert.Equal(1, Lists(rig));
        Assert.Equal(PlayerbotQaPublisher.MaxLookupsPerPass, Searches(rig));
        Assert.Equal(PlayerbotQaPublisher.MaxLookupsPerPass, rig.GitHub.Creates);
        Assert.Equal(Enumerable.Repeat(PlayerbotQaPublisher.LookupSpacing, PlayerbotQaPublisher.MaxLookupsPerPass - 1), rig.LookupDelays);
        Assert.Equal(15, rig.State.All.Count(f => f.Publication.State == QaPublicationState.Pending)); // the rest wait for the next pass

        await rig.Run();

        Assert.Equal(2, Lists(rig)); // one more list for the second pass, not one per finding
        Assert.Equal(2 * PlayerbotQaPublisher.MaxLookupsPerPass, Searches(rig));
        Assert.Equal(2 * PlayerbotQaPublisher.MaxLookupsPerPass, rig.GitHub.Creates);
    }

    [Fact]
    public async Task LabelledIssues_AreFoundFromTheOneList_WithoutSearchOrPerIssueReads()
    {
        string[] ids = [A, B, C];
        QaFindingView[] findings = [.. ids.Select(id => Finding(id))];
        var github = new FakeGitHub();
        foreach (string id in ids) github.Add(PlayerbotQaIssueComposer.MarkerOf(id) + "\nbody");
        Rig rig = NewRig(findings: findings, github: github);

        await rig.Run();

        Assert.Equal(1, Lists(rig));
        Assert.Equal(0, Searches(rig));
        Assert.Equal(0, github.Creates);
        Assert.Equal(3, github.Comments);
        Assert.Equal(1, github.Gets); // the list only
    }

    [Fact]
    public async Task SharedSetAside_IsNotPublishedAsDuplicateIssue_WhenCorrespondingStallFindingIsTracked()
    {
        QaFindingView stall = QaData.View(A, count: 5, bots: 3) with
        {
            Kind = QaSignalKind.Stall,
            Goal = "Quest",
            Quest = 95,
            Target = 0,
        };
        QaFindingView setAside = QaData.View(B, count: 3, bots: 3) with
        {
            Kind = QaSignalKind.SharedSetAside,
            Goal = "Quest",
            Quest = 95,
            Target = 0,
            Severity = stall.Severity + 20, // even with higher severity weight, it yields to the stall finding
        };
        Rig rig = NewRig(findings: [stall, setAside]);

        await rig.Run();

        Assert.Equal(1, rig.GitHub.Creates);
        Assert.Equal(QaPublicationState.Published, rig.State.Get(A)!.Publication.State);
        Assert.Equal(QaPublicationState.Pending, rig.State.Get(B)!.Publication.State);
    }
}
