using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Build stamps on findings (first and last build, a bounded per-build count), their persistence (format 2, format 1 still read), the report's
/// "quiet on this build" fields, and the regression rule: a closed issue is reopened, once, when its finding recurs on another build than the one it was closed on.
/// </summary>
public sealed class PlayerbotQaBuildStampTests : IDisposable
{
    private const string TokenVariable = "ARCANECORE_GITHUB_TOKEN";

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static PlayerbotQaAggregator NewAggregator(string build, byte[]? salt = null) => new(100, 5, salt, null, null, build);

    private static string Occur(PlayerbotQaAggregator aggregator, Guid bot, uint quest = 1656)
        => aggregator.Ingest(QaData.Stall(bot, 1, 1, quest), NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    private static QaFindingView Only(PlayerbotQaAggregator aggregator) => Assert.Single(aggregator.Snapshot());

    private static QaFindingView Of(PlayerbotQaAggregator aggregator, string id) => aggregator.Get(id)!;

    /// <summary>Save the aggregator and the ledger to findings.json and load them into a new aggregator that runs <paramref name="newBuild"/>: a restart on another build.</summary>
    private (PlayerbotQaAggregator Aggregator, QaLedgerState Ledger) Restart(PlayerbotQaAggregator old, QaLedgerState ledger, string newBuild)
    {
        QaAggregatorState state = old.Export();
        Assert.True(new PlayerbotQaStore(_directory, new ListLogger()).Save(new QaStoreFile
        {
            SaltBase64 = Convert.ToBase64String(old.Salt), Findings = state.Findings, Bots = state.Bots, Absorbed = state.Absorbed ?? [], Tombstones = state.Tombstones ?? [], Ledger = ledger,
        }));
        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;
        PlayerbotQaAggregator fresh = NewAggregator(newBuild, Convert.FromBase64String(loaded.SaltBase64));
        fresh.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots, loaded.Absorbed, loaded.Tombstones), QaData.T0);
        return (fresh, loaded.Ledger);
    }

    // ----- stamps --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Stamps_AreSet_CountPerBuild_AreBounded_AndSurviveAReload()
    {
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occur(one, QaData.Bot1);
        Occur(one, QaData.Bot2);
        Occur(one, QaData.Bot1);
        QaFindingView first = Of(one, id);
        Assert.Equal("build-1", first.FirstBuild);
        Assert.Equal("build-1", first.LastBuild);
        Assert.Equal([new QaBuildCount("build-1", 3)], first.BuildCounts);

        (PlayerbotQaAggregator two, QaLedgerState ledger) = Restart(one, new QaLedgerState([], []), "build-2");
        QaFindingView reloaded = Of(two, id);
        Assert.Equal("build-1", reloaded.FirstBuild); // persisted and read back
        Assert.Equal([new QaBuildCount("build-1", 3)], reloaded.BuildCounts);
        Occur(two, QaData.Bot2);
        Occur(two, QaData.Bot2);
        QaFindingView second = Of(two, id);
        Assert.Equal("build-1", second.FirstBuild);
        Assert.Equal("build-2", second.LastBuild);
        Assert.Equal([new QaBuildCount("build-2", 2), new QaBuildCount("build-1", 3)], second.BuildCounts);
        Assert.Equal(5, second.Count);

        (PlayerbotQaAggregator three, _) = Restart(two, ledger, "build-3");
        Occur(three, QaData.Bot1);
        QaFindingView third = Of(three, id);
        Assert.Equal("build-1", third.FirstBuild); // the first build is kept even when its count is dropped
        Assert.Equal("build-3", third.LastBuild);
        Assert.Equal([new QaBuildCount("build-3", 1), new QaBuildCount("build-2", 2)], third.BuildCounts); // only the current and the previous build keep a count
        Assert.Equal(1, third.CountOnBuild("build-3"));
        Assert.Equal(0, third.CountOnBuild("build-1"));
    }

    [Fact]
    public void OldFormatFile_LoadsWithUnknownBuilds_AndIsStampedByTheNextOccurrence()
    {
        PlayerbotQaAggregator writer = NewAggregator("build-1");
        string id = Occur(writer, QaData.Bot1);
        Occur(writer, QaData.Bot2);
        Occur(writer, QaData.Bot2);
        QaAggregatorState state = writer.Export();
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        Assert.True(store.Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String(writer.Salt), Findings = state.Findings, Bots = state.Bots }));

        // Turn the file into one a format 1 build wrote: no build fields at all.
        JsonNode file = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        file["FormatVersion"] = 1;
        foreach (JsonNode? finding in file["Findings"]!.AsArray())
        {
            JsonObject o = finding!.AsObject();
            foreach (string field in new[] { "FirstBuild", "LastBuild", "BuildCounts", "ClosedBuild", "ReportedBuild" }) o.Remove(field);
        }

        File.WriteAllText(store.FilePath, file.ToJsonString());

        var reader = new PlayerbotQaStore(_directory, new ListLogger());
        QaStoreFile loaded = reader.Load()!;
        Assert.NotNull(loaded);
        Assert.Null(reader.DisabledReason);
        PlayerbotQaAggregator aggregator = NewAggregator("build-2", Convert.FromBase64String(loaded.SaltBase64));
        aggregator.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots, loaded.Absorbed, loaded.Tombstones), QaData.T0);

        QaFindingView old = Of(aggregator, id);
        Assert.Equal(3, old.Count);
        Assert.Null(old.FirstBuild);
        Assert.Null(old.LastBuild);
        Assert.Empty(old.BuildCounts);
        Occur(aggregator, QaData.Bot1);
        QaFindingView stamped = Of(aggregator, id);
        Assert.Equal("build-2", stamped.FirstBuild);
        Assert.Equal([new QaBuildCount("build-2", 1)], stamped.BuildCounts);
    }

    // ----- the report ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ReportJson_SaysWhetherTheCurrentBuildMetTheFinding_AndSinceWhen()
    {
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occur(one, QaData.Bot1);
        Occur(one, QaData.Bot2);
        (PlayerbotQaAggregator two, _) = Restart(one, new QaLedgerState([], []), "build-2");
        string quiet = id;
        string active = Occur(two, QaData.Bot1, quest: 77);
        DateTime started = QaData.T0.AddHours(1);

        JsonNode report = JsonNode.Parse(PlayerbotQaReport.RenderJson(two.Snapshot(), started.AddHours(5), "build-2", new PlayerbotQaRedactor(), null, started))!;

        Assert.Equal(started, report["BuildSinceUtc"]!.GetValue<DateTime>());
        JsonObject quietEntry = report["Findings"]!.AsArray().Select(n => n!.AsObject()).Single(e => (string)e["Id"]! == quiet);
        Assert.False((bool)quietEntry["SeenOnCurrentBuild"]!);
        Assert.Equal(0, (long)quietEntry["OccurrencesOnCurrentBuild"]!);
        Assert.Equal("build-1", (string)quietEntry["LastBuild"]!);
        Assert.Equal("build-1", (string)quietEntry["FirstBuild"]!);
        Assert.True((bool)quietEntry["Stale"]!); // the existing stale rule is untouched: not seen since the server started
        JsonObject activeEntry = report["Findings"]!.AsArray().Select(n => n!.AsObject()).Single(e => (string)e["Id"]! == active);
        Assert.True((bool)activeEntry["SeenOnCurrentBuild"]!);
        Assert.Equal(1, (long)activeEntry["OccurrencesOnCurrentBuild"]!);
        Assert.Equal("build-2", (string)activeEntry["Builds"]![0]!["Build"]!);
    }

    [Fact]
    public void ChatShow_ListsTheBuilds()
    {
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occur(one, QaData.Bot1);
        (PlayerbotQaAggregator two, _) = Restart(one, new QaLedgerState([], []), "build-2");
        Occur(two, QaData.Bot1);

        IReadOnlyList<string> lines = PlayerbotQaReport.ChatShow(Of(two, id), new PlayerbotQaRedactor(), "build-2");

        string line = Assert.Single(lines, l => l.StartsWith("builds first", StringComparison.Ordinal));
        Assert.Contains("first build-1, last build-2", line, StringComparison.Ordinal);
        Assert.Contains("build-2=1, build-1=1", line, StringComparison.Ordinal);
        Assert.Contains("on this build (build-2): 1", line, StringComparison.Ordinal);
    }

    // ----- regression handling ---------------------------------------------------------------------------------------------------------------

    private sealed class Rig(Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        public PlayerbotQaOptions Options { get; } = Build(configure);

        public FakeGitHub GitHub { get; } = new();

        public ManualTime Time { get; } = new(QaData.T0);

        private static PlayerbotQaOptions Build(Action<PlayerbotQaGitHubOptions>? configure)
        {
            var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2, MaxCommentsPerHour = 10 } };
            configure?.Invoke(options.GitHub);
            return options;
        }

        public PlayerbotQaPublisher Publisher(IQaPublicationState state, string build, QaLedgerState? ledger = null)
        {
            var publisher = new PlayerbotQaPublisher(Options, Path.GetTempPath(), state, new PlayerbotQaRedactor(), new ListLogger(),
                name => name == TokenVariable ? QaData.FakeToken : null,
                token => new PlayerbotQaGitHubClient(GitHub.Http, QaData.Repo, token, Options.GitHub, (_, _) => Task.CompletedTask, null, () => Time.GetUtcNow()),
                Time, build, null, (_, _) => Task.CompletedTask);
            publisher.Ledger.Restore(ledger);
            return publisher;
        }

        public Task Run(PlayerbotQaPublisher publisher, PlayerbotQaAggregator aggregator) => publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        public string[] CommentBodies() => [.. GitHub.Http.Requests
            .Where(r => r.Method == HttpMethod.Post && r.PathAndQuery.Split('?')[0].EndsWith("/comments", StringComparison.Ordinal))
            .Select(r => (string)JsonNode.Parse(r.Body)!["body"]!)];
    }

    /// <summary>A finding of 3 occurrences by 2 bots whose issue a person closed, found closed by a pass on <c>build-1</c>: KnownClosed there.</summary>
    private async Task<(Rig Rig, PlayerbotQaAggregator Aggregator, string Id, int Issue, QaLedgerState Ledger)> ClosedOnBuildOne(Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var rig = new Rig(configure);
        PlayerbotQaAggregator aggregator = NewAggregator("build-1");
        string id = Occur(aggregator, QaData.Bot1);
        Occur(aggregator, QaData.Bot2);
        Occur(aggregator, QaData.Bot1);
        int issue = rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id), open: false);
        PlayerbotQaPublisher publisher = rig.Publisher(aggregator, "build-1");

        await rig.Run(publisher, aggregator);

        QaFindingView view = Of(aggregator, id);
        Assert.Equal(new QaPublication(QaPublicationState.KnownClosed, issue), view.Publication);
        Assert.Equal("build-1", view.ClosedBuild); // the build it was first observed closed on
        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal(0, rig.GitHub.Comments);
        return (rig, aggregator, id, issue, publisher.Ledger.Export());
    }

    [Fact]
    public async Task Regression_OnANewerBuild_ReopensTheIssue_AndCommentsOnce()
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        (PlayerbotQaAggregator two, QaLedgerState restored) = Restart(one, ledger, "build-2");
        Occur(two, QaData.Bot2);
        Occur(two, QaData.Bot2);
        PlayerbotQaPublisher publisher = rig.Publisher(two, "build-2", restored);

        await rig.Run(publisher, two);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        string comment = Assert.Single(rig.CommentBodies());
        Assert.Contains("build-2", comment, StringComparison.Ordinal);
        Assert.Contains("2 occurrences on this build (total 5)", comment, StringComparison.Ordinal);
        Assert.Contains("distinct bots 2", comment, StringComparison.Ordinal);
        Assert.Contains("Sample:", comment, StringComparison.Ordinal);
        QaFindingView view = Of(two, id);
        Assert.Equal(new QaPublication(QaPublicationState.Published, issue), view.Publication);
        Assert.Null(view.ClosedBuild);
        Assert.Equal(5, view.OccurrencesReported);

        await rig.Run(publisher, two); // nothing is due: no second reopen, no second comment
        Occur(two, QaData.Bot1);
        await rig.Run(publisher, two); // a new occurrence on the open issue waits for the comment interval like any other
        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Single(rig.CommentBodies());
        Assert.Equal(0, rig.GitHub.Creates);
    }

    [Fact]
    public async Task Recurrence_OnTheBuildItWasClosedOn_DoesNotReopen()
    {
        var (rig, aggregator, id, _, ledger) = await ClosedOnBuildOne();
        Occur(aggregator, QaData.Bot2);
        Occur(aggregator, QaData.Bot1);
        PlayerbotQaPublisher publisher = rig.Publisher(aggregator, "build-1", ledger);

        await rig.Run(publisher, aggregator);

        Assert.Equal(0, rig.GitHub.Patches); // a person closed it while it was still happening (won't-fix): left alone
        Assert.Equal(0, rig.GitHub.Comments);
        Assert.Equal(QaPublicationState.KnownClosed, Of(aggregator, id).Publication.State);

        // The same after a restart on the same build.
        (PlayerbotQaAggregator again, QaLedgerState restored) = Restart(aggregator, ledger, "build-1");
        Occur(again, QaData.Bot1);
        await rig.Run(rig.Publisher(again, "build-1", restored), again);
        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal(0, rig.GitHub.Comments);
    }

    [Fact]
    public async Task Regression_WithReopenOnRegressionOff_IsLeftClosed()
    {
        var (rig, one, id, _, ledger) = await ClosedOnBuildOne(git => git.ReopenOnRegression = false);
        (PlayerbotQaAggregator two, QaLedgerState restored) = Restart(one, ledger, "build-2");
        Occur(two, QaData.Bot2);
        Occur(two, QaData.Bot1);

        await rig.Run(rig.Publisher(two, "build-2", restored), two);

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal(0, rig.GitHub.Comments);
        Assert.Equal(QaPublicationState.KnownClosed, Of(two, id).Publication.State);
    }

    [Fact]
    public async Task Regression_HonoursTheHourlyCommentCap_ReopeningOnlyWithAComment()
    {
        var rig = new Rig(git => git.MaxCommentsPerHour = 1);
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string[] ids = [.. new uint[] { 1656, 1657 }.Select(q =>
        {
            string id = Occur(one, QaData.Bot1, q);
            Occur(one, QaData.Bot2, q);
            Occur(one, QaData.Bot1, q);
            rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id), open: false);
            return id;
        })];
        PlayerbotQaPublisher first = rig.Publisher(one, "build-1");
        await rig.Run(first, one);
        Assert.All(ids, id => Assert.Equal(QaPublicationState.KnownClosed, Of(one, id).Publication.State));

        (PlayerbotQaAggregator two, QaLedgerState ledger) = Restart(one, first.Ledger.Export(), "build-2");
        foreach (uint quest in new uint[] { 1656, 1657 }) Occur(two, QaData.Bot2, quest);
        PlayerbotQaPublisher second = rig.Publisher(two, "build-2", ledger);

        await rig.Run(second, two);

        Assert.Equal(1, rig.GitHub.Patches); // one comment an hour: one regression reopened, the other waits (a reopen never goes without its comment)
        Assert.Single(rig.CommentBodies());
        Assert.Equal(1, ids.Count(id => Of(two, id).Publication.State == QaPublicationState.Published));
        Assert.Equal(1, ids.Count(id => Of(two, id).Publication.State == QaPublicationState.KnownClosed));

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Run(second, two);

        Assert.Equal(2, rig.GitHub.Patches);
        Assert.Equal(2, rig.CommentBodies().Length);
        Assert.All(ids, id => Assert.Equal(QaPublicationState.Published, Of(two, id).Publication.State));
    }
}
