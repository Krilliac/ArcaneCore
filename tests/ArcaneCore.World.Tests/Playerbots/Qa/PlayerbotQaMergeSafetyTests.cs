using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The realism probe needs a copy of a live findings file (<c>ARCANECORE_QA_REALISM_FILE</c>); without it the test is reported as Skipped, never as a silent pass.</summary>
public sealed class QaRealismFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_QA_REALISM_FILE";

    public QaRealismFactAttribute()
    {
        string? path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Skip = $"{Variable} is not set to a copy of a findings.json.";
    }
}

/// <summary>An in-memory GitHub as the publisher sees it: issues with bodies, every create, comment and close recorded, closes that can be made to fail.</summary>
internal sealed class ScriptedTransport : IQaIssueTransport
{
    private readonly Dictionary<int, (bool Open, string Body)> _issues = [];
    private int _next = 500;

    public List<string> Calls { get; } = [];

    public int CloseFailures { get; set; }

    /// <summary>Comments that fail with a transient error before one succeeds.</summary>
    public int CommentFailures { get; set; }

    /// <summary>Issue-list calls that fail with a transient error before one succeeds.</summary>
    public int ListFailures { get; set; }

    public bool SupportsSearch { get; set; }

    /// <summary>Issues that exist but are missing from the list (the list was cut short): only <see cref="GetIssueAsync"/> sees them.</summary>
    public HashSet<int> Unlisted { get; } = [];

    public int Add(string body, bool open = true)
    {
        _issues[_next] = (open, body);
        return _next++;
    }

    public bool IsOpen(int number) => _issues[number].Open;

    public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        if (ListFailures > 0)
        {
            ListFailures--;
            throw new PlayerbotQaPublishException("list failed", transient: true);
        }

        var index = new QaIssueIndex(complete: Unlisted.Count == 0);
        foreach ((int number, (bool open, string body)) in _issues.Where(p => !Unlisted.Contains(p.Key))) index.Add(new QaIssueRef(number, open, $"https://github.com/x/y/issues/{number}"), body);
        return Task.FromResult(index);
    }

    public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken)
        => Task.FromResult<(QaIssueRef, string)?>(_issues.TryGetValue(number, out (bool Open, string Body) issue) ? (new QaIssueRef(number, issue.Open, $"https://github.com/x/y/issues/{number}"), issue.Body) : null);

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

    public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        int number = Add(body);
        Calls.Add("create " + number);
        return Task.FromResult(new QaIssueRef(number, true, $"https://github.com/x/y/issues/{number}"));
    }

    public Task CommentAsync(int number, string body, CancellationToken cancellationToken)
    {
        if (CommentFailures > 0)
        {
            CommentFailures--;
            throw new PlayerbotQaPublishException("comment failed", transient: true);
        }

        Calls.Add($"comment {number}{(body.StartsWith("Duplicate of", StringComparison.Ordinal) ? " duplicate" : string.Empty)}");
        return Task.CompletedTask;
    }

    public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken)
    {
        Calls.Add("close " + number);
        if (CloseFailures > 0)
        {
            CloseFailures--;
            throw new PlayerbotQaPublishException("close failed", transient: true);
        }

        _issues[number] = (false, _issues[number].Body);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Merging findings must never lose an issue: the id of a merged-away finding is recorded, an outcome the publisher still reports for it reaches the
/// survivor, a finding the publisher works on is not aged out, and the issue of an absorbed published finding is closed as a duplicate of the survivor's.
/// </summary>
public sealed partial class PlayerbotQaMergeSafetyTests(ITestOutputHelper output) : IDisposable
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static PlayerbotQaAggregator New(int maxFindings = 100, TimeSpan? stale = null) => new(maxFindings, 5, Salt, stale);

    private static string Ingest(PlayerbotQaAggregator aggregator, QaSignal signal)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    private static QaSignal Loop(float x, float y, uint map = 0, Guid? bot = null, params uint[] attackers)
        => new()
        {
            Kind = QaSignalKind.DeathLoop, AtUtc = QaData.T0, BotKey = bot ?? Guid.NewGuid(), HasPosition = true, Map = map, X = x, Y = y, Z = 50f, Attackers = attackers,
            Message = "Playerbot <bot> death loop",
        };

    private static QaPublication Published(int issue) => new(QaPublicationState.Published, issue);

    private static QaPublication Closed(int issue) => new(QaPublicationState.KnownClosed, issue);

    /// <summary>Two death-loop findings of one area that share no attacker yet, and the signal that bridges them. Returns their ids (a: attacker 531, b: attacker 202).</summary>
    private static (string A, string B, QaSignal Bridge) TwoLoops(PlayerbotQaAggregator aggregator, int countA = 1, int countB = 1)
    {
        string a = "", b = "";
        for (int i = 0; i < countA; i++) a = Ingest(aggregator, Loop(-10600f, 300f, 0, null, 531));
        for (int i = 0; i < countB; i++) b = Ingest(aggregator, Loop(-10650f, 250f, 0, null, 202));
        return (a, b, Loop(-10600f, 250f, 0, null, 202, 531));
    }

    [Fact]
    public void TwoPublishedFindings_Merge_TheLowerIssueSurvives_AndTheOtherIsRecordedForClosing()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        aggregator.Apply(a, new QaPublicationUpdate(Published(40)));
        aggregator.Apply(b, new QaPublicationUpdate(Published(12)));

        string survivor = Ingest(aggregator, bridge);

        Assert.Equal(b, survivor); // #12 < #40
        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(Published(12), f.Publication);
        Assert.Null(aggregator.Get(a));
        QaAbsorbed absorbed = Assert.Single(aggregator.PendingDuplicates());
        Assert.Equal(new QaAbsorbed(a, b, 40, false), absorbed);
        Assert.Contains(aggregator.Export().Absorbed!, x => x.Id == a && x.Survivor == b && x.Issue == 40);
    }

    [Fact]
    public void AnOpenIssueSurvivesAClosedOne_EvenWithTheHigherNumber()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        aggregator.Apply(a, new QaPublicationUpdate(Closed(5)));
        aggregator.Apply(b, new QaPublicationUpdate(Published(90)));

        Assert.Equal(b, Ingest(aggregator, bridge));

        Assert.Equal(Published(90), Assert.Single(aggregator.Snapshot()).Publication);
        Assert.Empty(aggregator.PendingDuplicates()); // a person closed #5 already: nothing to close
        Assert.Contains(aggregator.Export().Absorbed!, x => x.Id == a && x.Survivor == b && x.Issue == 5 && x.Handled);
    }

    [Fact]
    public void ApplyAfterAMerge_ReachesTheSurvivor_OrIsRecordedAsADuplicate()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 1, countB: 3);
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge)); // a is absorbed while the publisher works on it
        Assert.Null(aggregator.Get(a));

        aggregator.Apply(a, new QaPublicationUpdate(Published(77), QaData.T0, 1));

        QaFindingView survivor = aggregator.Get(b)!;
        Assert.Equal(Published(77), survivor.Publication);
        Assert.Empty(aggregator.PendingDuplicates());
        aggregator.Unpin(a);

        // The survivor already has an issue of its own: the issue the publisher just opened is a duplicate to close.
        PlayerbotQaAggregator other = New();
        (string c, string d, QaSignal bridge2) = TwoLoops(other, countA: 1, countB: 3);
        other.Apply(d, new QaPublicationUpdate(Published(8)));
        other.Pin(c);
        Ingest(other, bridge2);
        other.Apply(c, new QaPublicationUpdate(Published(9)));
        Assert.Equal(new QaAbsorbed(c, d, 9, false), Assert.Single(other.PendingDuplicates()));
    }

    [Fact]
    public void AFindingBeingPublished_IsNotAgedOutOrEvicted()
    {
        PlayerbotQaAggregator aggregator = New(maxFindings: 1, stale: TimeSpan.FromHours(24));
        string id = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 1));
        aggregator.Pin(id);

        Assert.Equal(0, aggregator.AgeOut(QaData.T0.AddHours(48)));
        Assert.NotNull(aggregator.Get(id));
        Assert.Null(aggregator.Ingest(QaData.Stall(Guid.NewGuid(), 0, 0, quest: 2), NoQaContentFacts.Instance, QaData.T0)); // the cap is full and the only finding is in flight
        Assert.NotNull(aggregator.Get(id));

        aggregator.Unpin(id);
        Assert.Equal(1, aggregator.AgeOut(QaData.T0.AddHours(48)));
    }

    /// <summary>A transport whose issue creation waits for the test, so a merge can happen while the publisher awaits GitHub.</summary>
    private sealed class BlockingCreateTransport : IQaIssueTransport
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public bool SupportsSearch => false;

        public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken) => Task.FromResult(new QaIssueIndex(complete: true));

        public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken) => Task.FromResult<(QaIssueRef, string)?>(null);

        public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

        public async Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new QaIssueRef(100, true, "https://github.com/x/y/issues/100");
        }

        public Task CommentAsync(int number, string body, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private PlayerbotQaPublisher Publisher(PlayerbotQaAggregator aggregator, PlayerbotQaOptions options, Func<IQaIssueTransport> make, ListLogger? log = null, ManualTime? time = null)
        => new(options, _directory, aggregator, new PlayerbotQaRedactor(), log ?? new ListLogger(), name => name == "ARCANECORE_GITHUB_TOKEN" ? QaData.FakeToken : null,
            _ => make(), time ?? new ManualTime(QaData.T0), "test-build");

    private static PlayerbotQaOptions Options(Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 3, MinDistinctBots = 2, CloseAbsorbedDuplicates = true } };
        configure?.Invoke(options.GitHub);
        return options;
    }

    [Fact]
    public async Task AMergeWhileTheIssueIsBeingCreated_DoesNotLoseTheIssueNumber()
    {
        PlayerbotQaAggregator aggregator = New();
        // a: three occurrences of two bots (eligible); b: four of one bot (not eligible, but busier, so it survives the merge).
        string a = "";
        for (int i = 0; i < 3; i++) a = Ingest(aggregator, Loop(-10600f, 300f, 0, i == 0 ? QaData.Bot1 : QaData.Bot2, 531));
        string b = "";
        for (int i = 0; i < 4; i++) b = Ingest(aggregator, Loop(-10650f, 250f, 0, QaData.Bot3, 202));
        var transport = new BlockingCreateTransport();
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => transport);

        Task publishing = publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(b, Ingest(aggregator, Loop(-10600f, 250f, 0, null, 202, 531))); // while GitHub is being asked, a is merged into b
        Assert.Null(aggregator.Get(a));
        transport.Release();
        await publishing.WaitAsync(TimeSpan.FromSeconds(10));

        QaFindingView survivor = Assert.Single(aggregator.Snapshot());
        Assert.Equal(b, survivor.Id);
        Assert.Equal(Published(100), survivor.Publication); // the number came back through the alias, not into the void
        Assert.Empty(aggregator.PendingDuplicates());
    }

    [Fact]
    public async Task TheIssueOfAnAbsorbedPublishedFinding_IsCommentedAndClosedAsADuplicate_Once()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        var github = new FakeGitHub();
        int issueA = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a");
        int issueB = github.Add(PlayerbotQaIssueComposer.MarkerOf(b) + "\n\nbody b");
        aggregator.Apply(a, new QaPublicationUpdate(Published(issueA), null, 1));
        aggregator.Apply(b, new QaPublicationUpdate(Published(issueB), null, 1));
        Ingest(aggregator, bridge);
        var log = new ListLogger();

        PlayerbotQaOptions options = Options();
        PlayerbotQaPublisher publisher = Publisher(aggregator, options, () => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, QaData.FakeToken, options.GitHub, (_, _) => Task.CompletedTask, null, () => DateTimeOffset.UnixEpoch), log);
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        QaFindingView survivor = Assert.Single(aggregator.Snapshot());
        int kept = survivor.Publication.Issue;
        int lost = kept == issueA ? issueB : issueA;
        Assert.Equal(Math.Min(issueA, issueB), kept);
        Assert.Equal("closed", (string)github.Get(lost)["state"]!);
        Assert.Equal("open", (string)github.Get(kept)["state"]!);
        string comment = Assert.Single(github.CommentBodies, c => c.Contains("Duplicate of", StringComparison.Ordinal));
        Assert.Contains($"#{kept}", comment, StringComparison.Ordinal);
        Assert.DoesNotMatch(GuidPattern(), comment);
        Assert.Contains(github.Http.Requests, r => r.Method == HttpMethod.Patch && r.Body.Contains("not_planned", StringComparison.Ordinal));
        Assert.Empty(aggregator.PendingDuplicates());
        Assert.Contains(log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains($"closed issue #{lost} as a duplicate of #{kept}", StringComparison.Ordinal));

        int comments = github.Comments;
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.Equal(comments, github.Comments); // handled once
    }

    [Fact]
    public async Task WithCloseAbsorbedDuplicatesOff_TheAbsorbedIssueIsLeftAlone_AndTheMappingStaysRecorded()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        var github = new FakeGitHub();
        int issueA = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a");
        int issueB = github.Add(PlayerbotQaIssueComposer.MarkerOf(b) + "\n\nbody b");
        aggregator.Apply(a, new QaPublicationUpdate(Published(issueA), null, 1));
        aggregator.Apply(b, new QaPublicationUpdate(Published(issueB), null, 1));
        Ingest(aggregator, bridge);

        PlayerbotQaOptions options = Options(g => g.CloseAbsorbedDuplicates = false);
        PlayerbotQaPublisher publisher = Publisher(aggregator, options, () => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, QaData.FakeToken, options.GitHub, (_, _) => Task.CompletedTask, null, () => DateTimeOffset.UnixEpoch));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.Equal("open", (string)github.Get(issueA)["state"]!);
        Assert.Equal("open", (string)github.Get(issueB)["state"]!);
        Assert.DoesNotContain(github.CommentBodies, c => c.Contains("Duplicate of", StringComparison.Ordinal));
        Assert.Single(aggregator.PendingDuplicates());
        Assert.Single(aggregator.Export().Absorbed!);
    }

    [Fact]
    public void TheMergeIsPersisted_AndAnAbsorbedIdIsNeverReusedForAnotherProblem()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        aggregator.Apply(a, new QaPublicationUpdate(Published(3)));
        aggregator.Apply(b, new QaPublicationUpdate(Published(4)));
        Ingest(aggregator, bridge);
        QaAggregatorState saved = aggregator.Export();

        string json = JsonSerializer.Serialize(new QaStoreFile { SaltBase64 = Convert.ToBase64String(Salt), Findings = saved.Findings, Bots = saved.Bots, Absorbed = saved.Absorbed! }, QaStoreJsonContext.Default.QaStoreFile);
        QaStoreFile loaded = JsonSerializer.Deserialize(json, QaStoreJsonContext.Default.QaStoreFile)!;
        PlayerbotQaAggregator restored = New();
        Assert.Equal(0, restored.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots, loaded.Absorbed)));
        Assert.Equal(new QaAbsorbed(b, a, 4, false), Assert.Single(restored.PendingDuplicates()));

        // The survivor is gone (reset by hand); a loop in the absorbed finding's own cell must not take the absorbed id back.
        restored.Reset(a);
        string fresh = Ingest(restored, Loop(-10650f, 250f, 0, null, 202));
        Assert.NotEqual(b, fresh);
        Assert.NotEqual(a, fresh);
    }

    [Fact]
    public void ADeathLoopFinding_CollectsAtMostEightAttackers_AndAnUnrecordedOneIsNotItsIdentity()
    {
        PlayerbotQaAggregator aggregator = New();
        uint[] first = [1, 2, 3, 4, 5, 6, 7, 8];
        string id = Ingest(aggregator, Loop(100f, 100f, 0, null, first));
        Assert.Equal(id, Ingest(aggregator, Loop(110f, 100f, 0, null, 8, 9, 10))); // shares 8: this loop, but 9 and 10 do not fit
        Assert.Equal(first, aggregator.Get(id)!.Attackers);

        string other = Ingest(aggregator, Loop(120f, 100f, 0, null, 9)); // 9 was not recorded, so it is a loop of its own
        Assert.NotEqual(id, other);
        Assert.Equal(2, aggregator.Snapshot().Count);
    }

    [Fact]
    public void QuestAndCombatStalls_NeverMergeAcrossMaps()
    {
        PlayerbotQaAggregator aggregator = New();
        string q0 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 10, 10, quest: 1656, target: 6747, map: 0));
        string q1 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 10, 10, quest: 1656, target: 6747, map: 1));
        Assert.NotEqual(q0, q1);
        Assert.Equal(q0, Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 3000, -800, quest: 1656, target: 6747, map: 0))); // same map, anywhere on it

        string c0 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -9000, 400, quest: 0, target: 68, map: 0, goal: "Combat")); // a city guard
        string c1 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 1600, -4400, quest: 0, target: 68, map: 1, goal: "Combat"));
        Assert.NotEqual(c0, c1);
        Assert.Equal(4, aggregator.Snapshot().Count);
        Assert.All(aggregator.Snapshot(), f => Assert.Equal(f.Anchor!.Map, f.Map));
    }

    /// <summary>Two published loop findings (a: three occurrences, b: four) merged by a bridging signal, with their issues in the scripted GitHub. The survivor has the lower issue number.</summary>
    private static (string Survivor, string Absorbed, int SurvivorIssue, int AbsorbedIssue) PublishedPair(PlayerbotQaAggregator aggregator, ScriptedTransport github, bool survivorOpen = true)
    {
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 3, countB: 4);
        int issueB = github.Add(PlayerbotQaIssueComposer.MarkerOf(b) + "\n\nbody b", survivorOpen);
        int issueA = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a");
        aggregator.Apply(b, new QaPublicationUpdate(Published(issueB), null, 4));
        aggregator.Apply(a, new QaPublicationUpdate(Published(issueA), null, 3));
        Assert.Equal(b, Ingest(aggregator, bridge));
        return (b, a, issueB, issueA);
    }

    [Fact]
    public async Task AnIssueHandedOverToTheSurvivor_IsFoundByItsNumber_AndCommentedNotRecreated()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 3, countB: 4);
        var github = new ScriptedTransport();
        int issue = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a"); // the issue carries the ABSORBED finding's marker
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge)); // a is merged into b while its issue is being created
        aggregator.Apply(a, new QaPublicationUpdate(Published(issue), QaData.T0, 3));
        aggregator.Unpin(a);
        Assert.Equal(Published(issue), aggregator.Get(b)!.Publication);

        var time = new ManualTime(QaData.T0);
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => github, time: time);
        time.Advance(TimeSpan.FromHours(25)); // a comment is due
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        time.Advance(TimeSpan.FromHours(25));
        Ingest(aggregator, Loop(-10600f, 250f, 0, null, 202, 531));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.DoesNotContain(github.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal([$"comment {issue}", $"comment {issue}"], github.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHandedOverIssue_AfterATransientFailure_IsStillTheSurvivorsIssue_AndNeverFiledTwice(bool commentFails)
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 3, countB: 4);
        var github = new ScriptedTransport();
        int issue = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a"); // the issue carries the ABSORBED finding's marker
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge));
        aggregator.Apply(a, new QaPublicationUpdate(Published(issue), QaData.T0, 3));
        aggregator.Unpin(a);

        var time = new ManualTime(QaData.T0);
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => github, time: time);
        if (commentFails) github.CommentFailures = 1;
        else github.ListFailures = 1;

        time.Advance(TimeSpan.FromHours(25));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        QaPublication afterFailure = aggregator.Get(b)!.Publication;
        Assert.Equal(QaPublicationState.Failed, afterFailure.State);
        Assert.Equal(issue, afterFailure.Issue); // the failed attempt keeps the number

        time.Advance(TimeSpan.FromHours(25));
        Ingest(aggregator, Loop(-10600f, 250f, 0, null, 202, 531));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        time.Advance(TimeSpan.FromHours(25));
        Ingest(aggregator, Loop(-10600f, 250f, 0, null, 202, 531));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.DoesNotContain(github.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal(Published(issue).State, aggregator.Get(b)!.Publication.State);
        Assert.Equal(issue, aggregator.Get(b)!.Publication.Issue);
        Assert.True(github.IsOpen(issue));
    }

    /// <summary>A survivor b that holds issue #N on the ABSORBED finding's marker (handed over mid-create), plus its transport and clock.</summary>
    private static (string Survivor, int Issue) HandedOver(PlayerbotQaAggregator aggregator, ScriptedTransport github)
    {
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 3, countB: 4);
        int issue = github.Add(PlayerbotQaIssueComposer.MarkerOf(a) + "\n\nbody a");
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge));
        aggregator.Apply(a, new QaPublicationUpdate(Published(issue), QaData.T0, 3));
        aggregator.Unpin(a);
        return (b, issue);
    }

    [Fact]
    public async Task AFailedSurvivorOfAHandedOverIssue_IsNotAgedOut_TheAliasIsKept_AndNothingIsFiledTwice()
    {
        PlayerbotQaAggregator aggregator = New(stale: TimeSpan.FromHours(24));
        var github = new ScriptedTransport { CommentFailures = 1 };
        (string b, int issue) = HandedOver(aggregator, github);
        var time = new ManualTime(QaData.T0);
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => github, time: time);

        time.Advance(TimeSpan.FromHours(25));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.Equal(QaPublicationState.Failed, aggregator.Get(b)!.Publication.State);

        time.Advance(TimeSpan.FromHours(48)); // GitHub was unusable for longer than the stale limit
        Assert.Equal(0, aggregator.AgeOut(time.GetUtcNow().UtcDateTime));
        Assert.NotNull(aggregator.Get(b));
        Assert.Equal(issue, aggregator.Get(b)!.Publication.Issue);
        Assert.Contains(aggregator.Export().Absorbed!, x => x.Survivor == b);

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.DoesNotContain(github.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandedOverIssue_BeyondACutShortList_IsStillTheSurvivorsIssue()
    {
        PlayerbotQaAggregator aggregator = New();
        var github = new ScriptedTransport { SupportsSearch = true };
        (string b, int issue) = HandedOver(aggregator, github);
        github.Unlisted.Add(issue); // only a direct GET sees it, and its body has the absorbed finding's marker
        var time = new ManualTime(QaData.T0);
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => github, time: time);

        time.Advance(TimeSpan.FromHours(25));
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.DoesNotContain(github.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal([$"comment {issue}"], github.Calls);
        Assert.Equal(issue, aggregator.Get(b)!.Publication.Issue);
    }

    [Fact]
    public void AFailedSurvivorHoldingAnIssue_KeepsItsOwnReportedCount_WhenAnAbsorbedFindingIsMergedIn()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 3, countB: 3);
        aggregator.Apply(a, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, 40, "x"), null, 3));
        aggregator.Apply(b, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, 12, "x"), null, 1));

        Assert.Equal(b, Ingest(aggregator, bridge)); // #12 < #40

        Assert.Equal(1, aggregator.Get(b)!.OccurrencesReported);
    }

    [Fact]
    public void AbsorbedFindingWithAFailedPublication_KeepsItsIssueTracked_AndClosableAsADuplicate()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 1, countB: 1);
        aggregator.Apply(a, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, 40, "comment failed")));
        aggregator.Apply(b, new QaPublicationUpdate(Published(12)));

        Assert.Equal(b, Ingest(aggregator, bridge));

        Assert.Equal(new QaAbsorbed(a, b, 40, false), Assert.Single(aggregator.PendingDuplicates()));
    }

    [Fact]
    public void APendingFinding_MergedWithAFailedOneHoldingAnIssue_EndsHoldingThatIssue_AndNothingIsToClose()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 1, countB: 4); // b is the busiest, and Pending
        aggregator.Apply(a, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, 40, "comment failed")));

        string survivor = Ingest(aggregator, bridge);

        Assert.True(survivor == a || survivor == b);
        QaFindingView only = Assert.Single(aggregator.Snapshot());
        Assert.Equal(40, only.Publication.Issue);
        Assert.Empty(aggregator.PendingDuplicates());
    }

    [Fact]
    public void AFailedSurvivor_ThatHoldsAnIssue_IsNotOverwrittenByAnInFlightOutcome_ThatIssueIsRecordedForClosing()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 1, countB: 1);
        aggregator.Apply(b, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, 12, "list failed")));
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge)); // a (Pending, in flight) is merged into b, which holds #12 although it failed

        aggregator.Apply(a, new QaPublicationUpdate(Published(77)));
        aggregator.Unpin(a);

        Assert.Equal(12, aggregator.Get(b)!.Publication.Issue);
        Assert.Equal(new QaAbsorbed(a, b, 77, false), Assert.Single(aggregator.PendingDuplicates()));
    }

    [Fact]
    public void ARepointedAlias_IsNotMarkedCommented_ForTheNewSurvivor()
    {
        PlayerbotQaAggregator aggregator = New();
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator);
        string c = Ingest(aggregator, Loop(-10700f, 200f, 0, null, 777));
        aggregator.Apply(a, new QaPublicationUpdate(Published(40)));
        aggregator.Apply(b, new QaPublicationUpdate(Published(12)));
        aggregator.Apply(c, new QaPublicationUpdate(Published(5)));
        Assert.Equal(b, Ingest(aggregator, bridge));
        aggregator.MarkDuplicateCommented(a);
        Assert.True(aggregator.PendingDuplicates().Single(x => x.Id == a).Commented);

        Assert.Equal(c, Ingest(aggregator, Loop(-10650f, 225f, 0, null, 777, 202))); // b is merged into c (#5 < #12)

        QaAbsorbed repointed = aggregator.PendingDuplicates().Single(x => x.Id == a);
        Assert.Equal(c, repointed.Survivor);
        Assert.False(repointed.Commented); // the comment named #12, not #5
    }

    [Fact]
    public void AStoreWrittenBeforeCommentedExisted_ReadsCommentedAsFalse()
    {
        const string json = """{"FormatVersion":1,"SaltBase64":"AQID","Absorbed":[{"Id":"aaaaaaaaaaaaaaaa","Survivor":"bbbbbbbbbbbbbbbb","Issue":40,"Handled":false}]}""";

        QaStoreFile file = JsonSerializer.Deserialize(json, QaStoreJsonContext.Default.QaStoreFile)!;

        QaAbsorbed entry = Assert.Single(file.Absorbed);
        Assert.Equal(40, entry.Issue);
        Assert.False(entry.Handled);
        Assert.False(entry.Commented);
    }

    [Fact]
    public async Task AFailedClose_IsRetried_WithoutCommentingAgain()
    {
        PlayerbotQaAggregator aggregator = New();
        var github = new ScriptedTransport { CloseFailures = 1 };
        (_, _, int kept, int lost) = PublishedPair(aggregator, github);
        PlayerbotQaPublisher publisher = Publisher(aggregator, Options(), () => github);

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.Equal([$"comment {kept}", $"comment {lost} duplicate", $"close {lost}"], github.Calls); // the survivor's own comment, then the duplicate's; the close failed
        Assert.True(aggregator.PendingDuplicates().Single().Commented);

        github.Calls.Clear();
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.Equal([$"close {lost}"], github.Calls); // retried, and not commented again
        Assert.Empty(aggregator.PendingDuplicates());
        Assert.False(github.IsOpen(lost));
        Assert.True(github.IsOpen(kept));
    }

    [Fact]
    public async Task AnOpenIssue_IsNeverClosedInFavourOfAClosedOrUnknownOne()
    {
        // The survivor's issue was closed on GitHub since the merge: the absorbed (open) issue stays open and the entry stays pending.
        PlayerbotQaAggregator aggregator = New();
        var github = new ScriptedTransport();
        (_, _, int kept, int lost) = PublishedPair(aggregator, github, survivorOpen: false);
        var log = new ListLogger();
        await Publisher(aggregator, Options(), () => github, log).RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.DoesNotContain(github.Calls, c => c.Contains("duplicate", StringComparison.Ordinal) || c.StartsWith("close", StringComparison.Ordinal));
        Assert.True(github.IsOpen(lost));
        Assert.Single(aggregator.PendingDuplicates());
        Assert.Contains(log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains($"#{lost} is a duplicate of #{kept}", StringComparison.Ordinal));

        // The absorbed issue is not in the issue list at all: unknown, so untouched.
        PlayerbotQaAggregator other = New();
        var empty = new ScriptedTransport();
        (_, string absorbed, int survivorIssue, _) = PublishedPair(other, empty);
        other.Apply(absorbed, new QaPublicationUpdate(Published(9999))); // the id is merged away: recorded as the absorbed issue, which GitHub's list does not have
        QaAbsorbed entry = Assert.Single(other.PendingDuplicates());
        Assert.NotEqual(survivorIssue, entry.Issue);
        empty.Calls.Clear();
        await Publisher(other, Options(), () => empty).RunAsync(other.Snapshot(), CancellationToken.None);
        Assert.DoesNotContain(empty.Calls, c => c.Contains("duplicate", StringComparison.Ordinal) || c.StartsWith("close", StringComparison.Ordinal));
        Assert.Single(other.PendingDuplicates());
    }

    [Fact]
    public void TheSurvivorOfAnInFlightAlias_IsProtected_AndADeadSurvivorDoesNotStrandTheEntry()
    {
        PlayerbotQaAggregator aggregator = New(stale: TimeSpan.FromHours(24));
        (string a, string b, QaSignal bridge) = TwoLoops(aggregator, countA: 1, countB: 3);
        aggregator.Pin(a);
        Assert.Equal(b, Ingest(aggregator, bridge));
        Assert.Equal(0, aggregator.AgeOut(QaData.T0.AddDays(5))); // b is the survivor of the finding being published: it stays
        Assert.NotNull(aggregator.Get(b));

        aggregator.Reset(b); // the survivor is gone by hand while a is still in flight
        aggregator.Apply(a, new QaPublicationUpdate(Published(31)));
        Assert.Empty(aggregator.PendingDuplicates());
        Assert.Empty(aggregator.Export().Absorbed!);
        aggregator.Unpin(a);

        // Without an in-flight publish a dead survivor's alias is pruned by the next pass.
        PlayerbotQaAggregator again = New(stale: TimeSpan.FromHours(24));
        (_, string d, QaSignal bridge2) = TwoLoops(again, countA: 1, countB: 3);
        Ingest(again, bridge2);
        Assert.Single(again.Export().Absorbed!);
        again.Reset(d);
        again.AgeOut(QaData.T0);
        Assert.Empty(again.Export().Absorbed!);
    }

    [Fact]
    public void RestoredDeathLoopAttackers_AreCappedAtEight()
    {
        PlayerbotQaAggregator maker = New();
        QaFindingView view = Fragment(maker, Loop(100f, 100f, 0, null, [1, 2, 3, 4, 5, 6, 7, 8]));
        QaSample sample = view.Samples![0] with { Attackers = [.. Enumerable.Range(1, 20).Select(i => (uint)i)] };
        PlayerbotQaAggregator target = New();
        target.Restore(new QaAggregatorState([view with { Samples = [sample] }], new Dictionary<string, ulong[]>()));
        Assert.Equal(QaFinding.MaxLoopAttackers, Assert.Single(target.Snapshot()).Attackers.Count);
    }

    [Fact]
    public async Task GhCliTransport_RunsTheCommentAndCloseCommandsItShouldRun()
    {
        var gh = new FakeGh();
        int number = gh.Add("body");
        PlayerbotQaGitHubOptions options = new() { Enabled = true, Repository = QaData.Repo, Publish = true, Transport = PlayerbotQaGitHubTransport.GhCli };
        using var client = new PlayerbotQaGhCliClient(gh, "gh", QaData.Repo, options, null, _directory);
        string n = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await client.CommentAsync(number, PlayerbotQaIssueComposer.DuplicateComment(7), CancellationToken.None);
        await client.CloseAsDuplicateAsync(number, CancellationToken.None);

        Assert.Equal(["issue", "comment", n, "--repo", QaData.Repo, "--body-file"], gh.Requests[0].Arguments.Take(6));
        Assert.Equal(["issue", "close", n, "--repo", QaData.Repo, "--reason", "not planned"], gh.Requests[1].Arguments);
        Assert.Contains("Duplicate of #7", gh.BodyFiles[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingAbsorbedDuplicates_IsOffByDefault()
    {
        Assert.False(new PlayerbotQaOptions().GitHub.CloseAbsorbedDuplicates);
    }

    [Fact]
    public void TheStartUpLog_ListsEveryIssueItWouldClose_ByNumberOnly()
    {
        PlayerbotQaAggregator aggregator = New();
        var github = new ScriptedTransport();
        (_, _, int kept, int lost) = PublishedPair(aggregator, github);
        QaAggregatorState state = aggregator.Export();
        new PlayerbotQaStore(_directory, new ListLogger()).Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String(Salt), Findings = state.Findings, Bots = state.Bots, Absorbed = state.Absorbed! });

        var log = new ListLogger();
        var options = new PlayerbotQaOptions { Directory = _directory };
        using Microsoft.Extensions.DependencyInjection.ServiceProvider services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Options.IOptions<PlayerbotQaOptions>>(Microsoft.Extensions.Options.Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(64))
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<PlayerbotQaFeature>>(new ListLogger<PlayerbotQaFeature>(log))
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services) { Time = new ManualTime(QaData.T0) };
        feature.Attach(null!);

        Assert.Contains(log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains($"issue #{lost} -> duplicate of #{kept}", StringComparison.Ordinal) && m.Contains("left alone", StringComparison.Ordinal));
        Assert.Single(feature.Aggregator!.PendingDuplicates());
    }

    private sealed class ListLogger<T>(ListLogger inner) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }


    private static QaFindingView Fragment(PlayerbotQaAggregator maker, QaSignal signal, QaPublication? publication = null, bool oldFormat = true)
    {
        PlayerbotQaAggregator one = New();
        string id = Ingest(one, signal);
        if (publication is not null) one.Apply(id, new QaPublicationUpdate(publication));
        QaFindingView view = one.Get(id)!;
        return oldFormat ? view with { Anchor = null, Attackers = [], DeathSource = string.Empty } : view;
    }

    [Fact]
    public void Restore_AtTheCap_KeepsEveryPublishedFinding_AndMergesBeforeCounting()
    {
        PlayerbotQaAggregator maker = New();
        var views = new List<QaFindingView>();
        // Four old loop fragments of one area merge into one finding; five unrelated stalls follow; the cap is three.
        foreach (QaSignal loop in new[] { Loop(-10600f, 300f, 0, null, 531), Loop(-10650f, 250f, 0, null, 202), Loop(-10600f, 250f, 0, null, 202, 531), Loop(-10650f, 300f, 0, null, 202, 531, 949) })
        {
            views.Add(Fragment(maker, loop));
        }

        for (uint quest = 1; quest <= 5; quest++) views.Add(Fragment(maker, QaData.Stall(Guid.NewGuid(), 0, 0, quest: quest), oldFormat: false) with { Severity = 0 });

        PlayerbotQaAggregator capped = New(maxFindings: 3);
        int dropped = capped.Restore(new QaAggregatorState(views, new Dictionary<string, ulong[]>()));
        Assert.Equal(3, capped.Count);
        Assert.Equal(3, dropped); // three of the five stalls; the four loops were one finding and the cap counted that one
        Assert.Single(capped.Snapshot(), f => f.Kind == QaSignalKind.DeathLoop && f.Count == 4);

        // Published findings are never dropped by the cap, even beyond it.
        var published = Enumerable.Range(1, 5).Select(q => Fragment(maker, QaData.Stall(Guid.NewGuid(), 0, 0, quest: (uint)q), Published(100 + q), oldFormat: false)).ToList();
        PlayerbotQaAggregator tiny = New(maxFindings: 2);
        Assert.Equal(0, tiny.Restore(new QaAggregatorState(published, new Dictionary<string, ulong[]>())));
        Assert.Equal(5, tiny.Count);
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    /// <summary>
    /// Local-only: when <c>ARCANECORE_QA_REALISM_FILE</c> names a copy of a live <c>findings.json</c>, load it into an aggregator and check that every
    /// published or known-closed id survives or is recorded as absorbed. Without the variable the test is reported as Skipped (a copy of live data is never committed).
    /// </summary>
    [QaRealismFact]
    public void RealisticStore_CollapsesAndKeepsEveryPublishedId()
    {
        string path = Environment.GetEnvironmentVariable(QaRealismFactAttribute.Variable)!;
        QaStoreFile file = JsonSerializer.Deserialize(File.ReadAllText(path), QaStoreJsonContext.Default.QaStoreFile)!;
        DateTime newest = file.Findings.Max(f => f.LastSeenUtc);
        string[] pinned = [.. file.Findings.Where(f => f.Publication?.Pinned == true).Select(f => f.Id)];
        output.WriteLine($"before: {file.Findings.Count} findings, {file.Findings.Count(f => f.Publication?.State == QaPublicationState.Published)} published, "
            + $"{file.Findings.Count(f => f.Publication?.State == QaPublicationState.KnownClosed)} known-closed, newest LastSeen {newest:u}");

        var merge = new PlayerbotQaAggregator(100_000, 5, Salt);
        int dropped = merge.Restore(new QaAggregatorState(file.Findings, file.Bots, file.Absorbed), newest);
        QaAggregatorState merged = merge.Export();
        output.WriteLine($"merge only: {merged.Findings.Count} findings (dropped {dropped}, merged away {merge.Merged}); by kind: "
            + string.Join(", ", merged.Findings.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        output.WriteLine($"  count-1 findings {merged.Findings.Count(f => f.Count <= 1)}, single-bot {merged.Findings.Count(f => f.DistinctBots <= 1)}; "
            + $"published+closed after {merged.Findings.Count(f => f.Publication.Pinned)}; absorbed issues to close {merge.PendingDuplicates().Count}");

        string[] alive = [.. merged.Findings.Select(f => f.Id)];
        string[] aliases = [.. merged.Absorbed!.Select(a => a.Id)];
        string[] missing = [.. pinned.Where(id => !alive.Contains(id) && !aliases.Contains(id))];
        output.WriteLine($"  published/closed ids neither alive nor recorded as absorbed: {missing.Length}");
        Assert.Empty(missing);
        Assert.Equal(pinned.Length, merged.Findings.Count(f => f.Publication.Pinned) + merged.Absorbed!.Count(a => pinned.Contains(a.Id)));

        var aged = new PlayerbotQaAggregator(100_000, 5, Salt, TimeSpan.FromHours(24));
        aged.Restore(new QaAggregatorState(file.Findings, file.Bots, file.Absorbed), newest);
        output.WriteLine($"merge + 24h age-out relative to the newest finding: {aged.Count} findings (aged out {aged.AgedOut})");
    }
}
