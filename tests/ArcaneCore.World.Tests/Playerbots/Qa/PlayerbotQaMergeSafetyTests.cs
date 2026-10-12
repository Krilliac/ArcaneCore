using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

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

    private PlayerbotQaPublisher Publisher(PlayerbotQaAggregator aggregator, PlayerbotQaOptions options, Func<IQaIssueTransport> make, ListLogger? log = null)
        => new(options, _directory, aggregator, new PlayerbotQaRedactor(), log ?? new ListLogger(), name => name == "ARCANECORE_GITHUB_TOKEN" ? QaData.FakeToken : null,
            _ => make(), new ManualTime(QaData.T0), "test-build");

    private static PlayerbotQaOptions Options(Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 3, MinDistinctBots = 2 } };
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
    /// published or known-closed id survives or is recorded as absorbed. Without the variable the test does nothing (a copy of live data is never committed).
    /// </summary>
    [Fact]
    public void RealisticStore_CollapsesAndKeepsEveryPublishedId()
    {
        string? path = Environment.GetEnvironmentVariable("ARCANECORE_QA_REALISM_FILE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
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
