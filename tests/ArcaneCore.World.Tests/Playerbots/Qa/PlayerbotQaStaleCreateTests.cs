using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>An in-memory GitHub that records creates, searches, comments and body edits; its search never finds anything (index lag / cold cache).</summary>
internal sealed class StaleCreateTransport : IQaIssueTransport
{
    private readonly Dictionary<int, (bool Open, string Body)> _issues = [];
    private int _next = 900;

    public bool SupportsSearch => true;

    public List<string> Created { get; } = [];

    public int Searches { get; private set; }

    public List<int> Edits { get; } = [];

    public List<int> Comments { get; } = [];

    public int Add(string body, bool open = true)
    {
        _issues[_next] = (open, body);
        return _next++;
    }

    public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        var index = new QaIssueIndex(complete: true);
        foreach ((int number, (bool open, string body)) in _issues) index.Add(new QaIssueRef(number, open, $"https://github.com/x/y/issues/{number}"), body);
        return Task.FromResult(index);
    }

    public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken)
        => Task.FromResult<(QaIssueRef, string)?>(_issues.TryGetValue(number, out (bool Open, string Body) issue) ? (new QaIssueRef(number, issue.Open, $"https://github.com/x/y/issues/{number}"), issue.Body) : null);

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken)
    {
        Searches++;
        return Task.FromResult<QaIssueRef?>(null);
    }

    public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        int number = Add(body);
        Created.Add(title);
        return Task.FromResult(new QaIssueRef(number, true, $"https://github.com/x/y/issues/{number}"));
    }

    public Task CommentAsync(int number, string body, CancellationToken cancellationToken)
    {
        Comments.Add(number);
        return Task.CompletedTask;
    }

    public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsCompletedAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EditBodyAsync(int number, string body, CancellationToken cancellationToken)
    {
        Edits.Add(number);
        _issues[number] = (_issues[number].Open, body);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A finding restored from the persisted store after a restart is only given a NEW issue when it is recent (it occurred on the running build, or was seen within the
/// create window); a stale one with no known issue is skipped, while a finding that already has an issue is still updated and a marker in the list is still honoured.
/// </summary>
public sealed class PlayerbotQaStaleCreateTests : IDisposable
{
    private const string OldBuild = "build-1";
    private const string CurrentBuild = "build-2";
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    /// <summary>A real finding view (valid key and id) for the stall of <paramref name="quest"/>, shaped by <paramref name="shape"/>.</summary>
    private static QaFindingView Persisted(uint quest, Func<QaFindingView, QaFindingView> shape)
    {
        var seed = new PlayerbotQaAggregator(100, 5, Salt);
        Assert.NotNull(seed.Ingest(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, quest: quest), NoQaContentFacts.Instance, QaData.T0));
        QaFindingView view = Assert.Single(seed.Export().Findings);
        return shape(view with { Cause = QaCause.Unknown, OccurrencesReported = 0, Publication = QaPublication.Pending });
    }

    /// <summary>The real start-up path: the saved findings are loaded into a fresh aggregator with <see cref="PlayerbotQaAggregator.Restore"/> at "now" (<see cref="QaData.T0"/>).</summary>
    private static PlayerbotQaAggregator Restored(params QaFindingView[] views)
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, Salt, build: CurrentBuild);
        aggregator.Restore(new QaAggregatorState(views, new Dictionary<string, ulong[]>()), QaData.T0);
        return aggregator;
    }

    /// <summary>Not seen for six days and not once on the running build: 2247 occurrences, 227 bots, all on the older build.</summary>
    private static QaFindingView Stale(uint quest = 1656) => Persisted(quest, v => v with
    {
        Count = 2247, DistinctBots = 227, FirstSeenUtc = QaData.T0.AddDays(-7), LastSeenUtc = QaData.T0.AddDays(-6),
        FirstBuild = OldBuild, LastBuild = OldBuild, BuildCounts = [new QaBuildCount(OldBuild, 2247)],
    });

    /// <summary>Recurring on the running build since this start-up: last seen at the publisher's construction time (<see cref="QaData.T0"/>), which is when it begins counting.</summary>
    private static QaFindingView Fresh(uint quest = 1700) => Persisted(quest, v => v with
    {
        Count = 2247, DistinctBots = 227, FirstSeenUtc = QaData.T0.AddDays(-7), LastSeenUtc = QaData.T0,
        FirstBuild = OldBuild, LastBuild = CurrentBuild, BuildCounts = [new QaBuildCount(CurrentBuild, 5), new QaBuildCount(OldBuild, 2242)],
    });

    private PlayerbotQaPublisher NewPublisher(PlayerbotQaAggregator state, StaleCreateTransport transport, bool publish = true, Action<PlayerbotQaOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = publish } };
        configure?.Invoke(options);
        return new PlayerbotQaPublisher(options, _directory, state, new PlayerbotQaRedactor(), new ListLogger(), _ => QaData.FakeToken, _ => transport, new ManualTime(QaData.T0), CurrentBuild);
    }

    private static Task Run(PlayerbotQaPublisher publisher, PlayerbotQaAggregator state) => publisher.RunAsync(state.Snapshot(), CancellationToken.None);

    // ----- the bug: a restart files issues for old findings ----------------------------------------------------------------------------------

    // RED on current code: the stale finding gets an issue created (and a search lookup first).
    [Fact]
    public async Task StaleRestoredFinding_WithNoKnownIssue_IsNeitherSearchedNorCreated()
    {
        QaFindingView stale = Stale();
        PlayerbotQaAggregator state = Restored(stale);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 0, $"a finding last seen 6 days ago with 0 occurrences on the running build was given a new issue ({transport.Created.Count} created)");
        Assert.True(transport.Searches == 0, $"a stale finding with no known issue was looked up by search ({transport.Searches} searches)");
        QaFindingView after = state.Get(stale.Id)!;
        Assert.False(after.Publication.HoldsIssue, "the stale finding was marked as holding an issue");
        Assert.NotEqual(QaPublicationState.Published, after.Publication.State);
    }

    [Fact]
    public async Task RecurringOnTheCurrentBuild_OpensExactlyOneIssue()
    {
        QaFindingView fresh = Fresh();
        PlayerbotQaAggregator state = Restored(fresh);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 1, $"a finding recurring on the running build should open exactly one issue, opened {transport.Created.Count}");
        QaFindingView after = state.Get(fresh.Id)!;
        Assert.Equal(QaPublicationState.Published, after.Publication.State);
        Assert.True(after.Publication.HoldsIssue, "the created issue's number was not recorded on the finding");
    }

    // The persisted per-build counts say the CURRENT build met it, but that was before the restart: a restart on the same build must not make it new.
    [Fact]
    public async Task SixDayOldFinding_WhoseBuildCountsHoldTheCurrentBuild_IsNeitherSearchedNorCreated()
    {
        QaFindingView old = Persisted(1656, v => v with
        {
            Count = 2247, DistinctBots = 227, FirstSeenUtc = QaData.T0.AddDays(-7), LastSeenUtc = QaData.T0.AddDays(-6),
            FirstBuild = CurrentBuild, LastBuild = CurrentBuild, BuildCounts = [new QaBuildCount(CurrentBuild, 2247)],
        });
        Assert.True(old.CountOnBuild(CurrentBuild) >= 5);
        PlayerbotQaAggregator state = Restored(old);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 0, $"a finding last seen 6 days ago, met on the current build only before the restart, was given a new issue ({transport.Created.Count} created)");
        Assert.True(transport.Searches == 0, $"it was looked up by search ({transport.Searches} searches)");
        Assert.False(state.Get(old.Id)!.Publication.HoldsIssue);
    }

    [Fact]
    public async Task SeenTwoHoursAgoOnThePreviousBuild_JustAfterARestart_StillOpensOneIssue()
    {
        QaFindingView recent = Persisted(1656, v => v with
        {
            Count = 50, DistinctBots = 10, FirstSeenUtc = QaData.T0.AddDays(-1), LastSeenUtc = QaData.T0.AddHours(-2),
            FirstBuild = OldBuild, LastBuild = OldBuild, BuildCounts = [new QaBuildCount(OldBuild, 50)],
        });
        Assert.Equal(0, recent.CountOnBuild(CurrentBuild));
        PlayerbotQaAggregator state = Restored(recent);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 1, $"a finding seen 2 h ago (inside the create window) must still open an issue after a restart, opened {transport.Created.Count}");
    }

    [Fact]
    public async Task CreateWithinHoursZero_KeepsAFindingSeenTwoHoursAgoOnThePreviousBuild_FromOpeningAnIssue()
    {
        QaFindingView recent = Persisted(1656, v => v with
        {
            Count = 50, DistinctBots = 10, FirstSeenUtc = QaData.T0.AddDays(-1), LastSeenUtc = QaData.T0.AddHours(-2),
            FirstBuild = OldBuild, LastBuild = OldBuild, BuildCounts = [new QaBuildCount(OldBuild, 50)],
        });
        PlayerbotQaAggregator state = Restored(recent);
        var transport = new StaleCreateTransport();
        PlayerbotQaPublisher publisher = NewPublisher(state, transport, configure: o => o.GitHub.CreateWithinHours = 0);

        await Run(publisher, state);

        Assert.True(transport.Created.Count == 0, $"CreateWithinHours = 0 allows the running build only, yet {transport.Created.Count} issue(s) were opened for a finding last seen on the previous build");
        Assert.True(transport.Searches == 0, "a finding that may not open an issue was looked up by search");
    }

    [Fact]
    public async Task CreateWithinHours200_LetsASixDayOldFindingOpenAnIssue()
    {
        QaFindingView stale = Stale();
        PlayerbotQaAggregator state = Restored(stale);
        var transport = new StaleCreateTransport();
        PlayerbotQaPublisher publisher = NewPublisher(state, transport, configure: o => o.GitHub.CreateWithinHours = 200);

        await Run(publisher, state);

        Assert.True(transport.Created.Count == 1, $"a finding last seen 6 days (144 h) ago is inside a 200 h window and should open one issue, opened {transport.Created.Count}");
    }

    // ----- dedupe and updates are unaffected -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FreshFinding_WhoseMarkerIsInTheIssueList_IsNotDuplicated_WhenSearchFindsNothing()
    {
        QaFindingView fresh = Fresh();
        PlayerbotQaAggregator state = Restored(fresh);
        var transport = new StaleCreateTransport();
        int issue = transport.Add(PlayerbotQaIssueComposer.MarkerOf(fresh.Id) + "\nPlayerbot QA finding: opened before the restart");

        await Run(NewPublisher(state, transport), state); // the first pass of a freshly constructed publisher

        Assert.True(transport.Created.Count == 0, $"a duplicate issue was created although the list already carries the marker (issue #{issue})");
        QaFindingView after = state.Get(fresh.Id)!;
        Assert.Equal(QaPublicationState.Published, after.Publication.State);
        Assert.Equal(issue, after.Publication.Issue);
    }

    [Fact]
    public async Task StaleFinding_WhoseMarkerIsInTheIssueList_IsNotDuplicated()
    {
        // The fresh finding is Pending and the stale one Failed (no issue number), so the fresh one is published first: it lists the issue index and creates its issue,
        // and only then does the stale one come up, with the index already read by this pass. It must be linked to its listed issue, not skipped and not duplicated.
        QaFindingView fresh = Fresh(1700);
        QaFindingView stale = Stale(1656) with { Publication = new QaPublication(QaPublicationState.Failed, 0, "earlier failure") };
        PlayerbotQaAggregator state = Restored(stale, fresh);
        var transport = new StaleCreateTransport();
        int issue = transport.Add(PlayerbotQaIssueComposer.MarkerOf(stale.Id) + "\nPlayerbot QA finding: opened last week");

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 1, $"only the fresh finding may open an issue, {transport.Created.Count} were created");
        Assert.Equal(QaPublicationState.Published, state.Get(fresh.Id)!.Publication.State);
        QaFindingView after = state.Get(stale.Id)!;
        Assert.Equal(QaPublicationState.Published, after.Publication.State);
        Assert.Equal(issue, after.Publication.Issue);
        // The fresh finding is not in the list, so it searches once before its create; the stale one is linked from the list without a search.
        Assert.True(transport.Searches == 1, $"only the fresh finding may search, {transport.Searches} searches were made");
    }

    [Fact]
    public async Task StaleFinding_WhoseMarkerIsInTheIssueList_IsNotDuplicated_WhenItIsTheOnlyFinding()
    {
        QaFindingView stale = Stale();
        PlayerbotQaAggregator state = Restored(stale);
        var transport = new StaleCreateTransport();
        transport.Add(PlayerbotQaIssueComposer.MarkerOf(stale.Id) + "\nPlayerbot QA finding: opened last week");

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 0, "a duplicate issue was created for a stale finding whose marker is in the issue list");
    }

    // A finding that holds #N which the list, GetIssue and the search all fail to find: it creates only when it is recent.
    [Fact]
    public async Task StaleFinding_HoldingAnIssueNumberNobodyFinds_IsNotRecreated()
    {
        QaFindingView stale = Stale() with { Publication = new QaPublication(QaPublicationState.Failed, 777, "earlier failure"), OccurrencesReported = 0 };
        PlayerbotQaAggregator state = Restored(stale);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 0, $"a stale finding holding an unfound issue number was given a new issue ({transport.Created.Count} created)");
    }

    [Fact]
    public async Task FreshFinding_HoldingAnIssueNumberNobodyFinds_OpensExactlyOneIssue()
    {
        QaFindingView fresh = Fresh() with { Publication = new QaPublication(QaPublicationState.Failed, 777, "earlier failure"), OccurrencesReported = 0 };
        PlayerbotQaAggregator state = Restored(fresh);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 1, $"a recurring finding whose issue number is gone should open exactly one issue, opened {transport.Created.Count}");
        Assert.Equal(QaPublicationState.Published, state.Get(fresh.Id)!.Publication.State);
    }

    [Fact]
    public async Task StaleFinding_ThatAlreadyHoldsAnIssue_StillHasItsStatusBlockUpdated_AndNothingIsCreated()
    {
        QaFindingView probe = Stale();
        var transport = new StaleCreateTransport();
        int issue = transport.Add(PlayerbotQaIssueComposer.MarkerOf(probe.Id) + "\nPlayerbot QA finding: an old body");
        QaFindingView published = probe with { Publication = new QaPublication(QaPublicationState.Published, issue), OccurrencesReported = probe.Count };
        PlayerbotQaAggregator state = Restored(published);

        await Run(NewPublisher(state, transport), state);

        Assert.True(transport.Created.Count == 0, "a finding holding an issue must never be created again");
        Assert.True(transport.Edits.Contains(issue), "the stale finding's existing issue did not get its status block updated");
        Assert.Equal(issue, state.Get(published.Id)!.Publication.Issue);
    }

    // ----- dry run ---------------------------------------------------------------------------------------------------------------------------

    // RED on current code: the stale finding's pending-issue file is written.
    [Fact]
    public async Task DryRun_SkipsTheStaleFinding_ButStillWritesTheFreshOne()
    {
        QaFindingView stale = Stale(1656);
        QaFindingView fresh = Fresh(1700);
        PlayerbotQaAggregator state = Restored(stale, fresh);
        var transport = new StaleCreateTransport();

        await Run(NewPublisher(state, transport, publish: false), state);

        string folder = Path.Combine(_directory, "pending-issues");
        Assert.True(File.Exists(Path.Combine(folder, fresh.Id + ".md")), "the fresh finding's dry-run file was not written");
        Assert.False(File.Exists(Path.Combine(folder, stale.Id + ".md")), "a dry run wrote a pending issue for a finding last seen 6 days ago with 0 occurrences on the running build");
        Assert.False(File.Exists(Path.Combine(folder, stale.Id + ".json")), "a dry run wrote the json of a stale finding");
        Assert.Empty(transport.Created);
    }
}
