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

    private static string Occur(PlayerbotQaAggregator aggregator, Guid bot, uint quest = 1656, DateTime? at = null)
        => aggregator.Ingest(QaData.Stall(bot, 1, 1, quest, at: at), NoQaContentFacts.Instance, at ?? QaData.T0) ?? throw new InvalidOperationException("refused");

    private static QaFindingView Only(PlayerbotQaAggregator aggregator) => Assert.Single(aggregator.Snapshot());

    private static QaFindingView Of(PlayerbotQaAggregator aggregator, string id) => aggregator.Get(id)!;

    private int _restarts;

    /// <summary>
    /// Save the aggregator and the ledger to findings.json and load them into a new aggregator that runs <paramref name="newBuild"/>: a restart on another build,
    /// <paramref name="at"/> (default: an hour after the previous restart) being when it starts. With <paramref name="oldFiles"/> the file is first stripped of everything
    /// the build stamps added, as a build from before them wrote it.
    /// </summary>
    private (PlayerbotQaAggregator Aggregator, QaLedgerState Ledger) Restart(PlayerbotQaAggregator old, QaLedgerState ledger, string newBuild, DateTime? at = null, bool oldFiles = false)
    {
        QaAggregatorState state = old.Export();
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        Assert.True(store.Save(new QaStoreFile
        {
            SaltBase64 = Convert.ToBase64String(old.Salt), Findings = state.Findings, Bots = state.Bots, Absorbed = state.Absorbed ?? [], Tombstones = state.Tombstones ?? [], Ledger = ledger,
            Builds = state.Builds ?? [], EvictedBuilds = state.EvictedBuilds ?? [],
        }));
        if (oldFiles) StripBuildFields(store.FilePath);
        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;
        PlayerbotQaAggregator fresh = NewAggregator(newBuild, Convert.FromBase64String(loaded.SaltBase64));
        fresh.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots, loaded.Absorbed, loaded.Tombstones, loaded.Builds, loaded.EvictedBuilds), at ?? QaData.T0.AddHours(++_restarts));
        return (fresh, loaded.Ledger);
    }

    private static void StripBuildFields(string path)
    {
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        file.AsObject().Remove("Builds");
        foreach (JsonNode? finding in file["Findings"]!.AsArray())
        {
            foreach (string field in new[] { "FirstBuild", "LastBuild", "BuildCounts", "ClosedBuild", "ReportedBuild" }) finding!.AsObject().Remove(field);
        }

        File.WriteAllText(path, file.ToJsonString());
    }

    /// <summary><paramref name="times"/> occurrences by alternating bots, at <paramref name="at"/> (default: the time of the latest restart).</summary>
    private string Occurs(PlayerbotQaAggregator aggregator, int times, uint quest = 1656, DateTime? at = null)
    {
        string id = string.Empty;
        for (int i = 0; i < times; i++) id = Occur(aggregator, i % 2 == 0 ? QaData.Bot1 : QaData.Bot2, quest, at ?? QaData.T0.AddHours(_restarts));
        return id;
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
        Assert.Equal(1, (int)file["FormatVersion"]!); // the format did not move: an older build still reads what this one writes
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
        Occurs(two, 3);
        PlayerbotQaPublisher publisher = rig.Publisher(two, "build-2", restored);

        await rig.Run(publisher, two);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        string comment = Assert.Single(rig.CommentBodies());
        Assert.Contains("build-2", comment, StringComparison.Ordinal);
        Assert.Contains("3 occurrences on this build (total 6)", comment, StringComparison.Ordinal);
        Assert.Contains("distinct bots 2", comment, StringComparison.Ordinal);
        Assert.Contains("Sample:", comment, StringComparison.Ordinal);
        QaFindingView view = Of(two, id);
        Assert.Equal(new QaPublication(QaPublicationState.Published, issue), view.Publication);
        Assert.Null(view.ClosedBuild);
        Assert.Equal(6, view.OccurrencesReported);

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
        Occurs(two, 3);

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
        foreach (uint quest in new uint[] { 1656, 1657 }) Occurs(two, 3, quest);
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

    // ----- builds order by first sighting, not by name --------------------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_ToAnOlderBuild_DoesNotReopen_ButANewerBuildDoes()
    {
        var rig = new Rig();
        PlayerbotQaAggregator a1 = NewAggregator("build-A");
        string id = Occurs(a1, 3);
        (PlayerbotQaAggregator b, QaLedgerState ledgerB) = Restart(a1, new QaLedgerState([], []), "build-B");
        Occurs(b, 3);
        int issue = rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id), open: false);
        PlayerbotQaPublisher onB = rig.Publisher(b, "build-B", ledgerB);
        await rig.Run(onB, b);
        Assert.Equal("build-B", Of(b, id).ClosedBuild); // closed while B ran

        (PlayerbotQaAggregator a2, QaLedgerState ledgerA) = Restart(b, onB.Ledger.Export(), "build-A"); // rolled back to A
        Occurs(a2, 3);
        PlayerbotQaPublisher onA = rig.Publisher(a2, "build-A", ledgerA);
        await rig.Run(onA, a2);

        Assert.Equal(0, rig.GitHub.Patches); // A differs from B but is older: not a regression
        Assert.Equal(0, rig.GitHub.Comments);
        Assert.Equal(QaPublicationState.KnownClosed, Of(a2, id).Publication.State);

        (PlayerbotQaAggregator c, QaLedgerState ledgerC) = Restart(a2, onA.Ledger.Export(), "build-C"); // a genuinely newer build
        Occurs(c, 3);
        await rig.Run(rig.Publisher(c, "build-C", ledgerC), c);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Contains("build-C", Assert.Single(rig.CommentBodies()), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRegistry_IsBounded_OrdersByFirstSighting_AndKnowsNothingOfStrangers()
    {
        PlayerbotQaAggregator aggregator = NewAggregator("b0");
        Occurs(aggregator, 1);
        QaLedgerState ledger = new([], []);
        for (int i = 1; i <= 40; i++) (aggregator, _) = Restart(aggregator, ledger, "b" + i);

        IReadOnlyList<QaBuildSeen> known = aggregator.Export().Builds!;
        Assert.Equal(PlayerbotQaAggregator.MaxKnownBuilds, known.Count);
        Assert.DoesNotContain(known, b => b.Build == "b0");
        Assert.Contains(known, b => b.Build == "b40");
        Assert.True(aggregator.IsBuildNewer("b40", "b39"));
        Assert.False(aggregator.IsBuildNewer("b39", "b40"));
        Assert.False(aggregator.IsBuildNewer("b40", "b40"));
        Assert.False(aggregator.IsBuildNewer("b40", "never-seen"));
        Assert.False(aggregator.IsBuildNewer("never-seen", "b39"));
    }

    // ----- the file keeps its format ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFileWrittenWithBuildStamps_LoadsUnderTheFormat1Rules_AndAnUnknownMemberIsIgnored()
    {
        PlayerbotQaAggregator aggregator = NewAggregator("build-1");
        Occurs(aggregator, 3);
        QaAggregatorState state = aggregator.Export();
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        Assert.True(store.Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String(aggregator.Salt), Findings = state.Findings, Bots = state.Bots, Builds = state.Builds ?? [] }));

        JsonNode file = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(1, (int)file["FormatVersion"]!);
        Assert.NotNull(file["Builds"]);
        Assert.NotNull(file["Findings"]![0]!["BuildCounts"]);
        file["FutureField"] = 7;
        file["Findings"]![0]!["AnotherFutureField"] = "x";
        File.WriteAllText(store.FilePath, file.ToJsonString());

        var reader = new PlayerbotQaStore(_directory, new ListLogger());
        QaStoreFile loaded = reader.Load()!;

        Assert.NotNull(loaded);
        Assert.Null(reader.DisabledReason);
        Assert.Equal("build-1", Assert.Single(loaded.Findings).LastBuild);
        Assert.Equal("build-1", Assert.Single(loaded.Builds).Build);
    }

    // ----- the paths that decide a public reopen ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFormat1KnownClosedFinding_DoesNotReopenOnTheUpgradeBuild_ButOnALaterNewerOne()
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        (PlayerbotQaAggregator upgrade, QaLedgerState l1) = Restart(one, ledger, "build-U", oldFiles: true); // a file as a build before the stamps wrote it
        Assert.Null(Of(upgrade, id).ClosedBuild);
        Occurs(upgrade, 3);
        Assert.Equal("build-U", Of(upgrade, id).ClosedBuild); // taken as closed on the build that first met it again
        PlayerbotQaPublisher onU = rig.Publisher(upgrade, "build-U", l1);

        await rig.Run(onU, upgrade);

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal(0, rig.GitHub.Comments);

        (PlayerbotQaAggregator later, QaLedgerState l2) = Restart(upgrade, onU.Ledger.Export(), "build-V");
        Occurs(later, 3);
        await rig.Run(rig.Publisher(later, "build-V", l2), later);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Single(rig.CommentBodies());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APublishedIssueFoundClosedOutsideTheBot_ReopensOnlyOnANewerBuildThanItWasLastOpenOn(bool newerBuild)
    {
        var rig = new Rig();
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occurs(one, 3);
        int issue = rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id), open: true);
        PlayerbotQaPublisher first = rig.Publisher(one, "build-1");
        await rig.Run(first, one);
        Assert.Equal(1, rig.GitHub.Comments);
        QaFindingView published = Of(one, id);
        Assert.Equal(QaPublicationState.Published, published.Publication.State);
        Assert.Equal("build-1", published.ReportedBuild); // the issue was open on build-1
        rig.GitHub.Get(issue)["state"] = "closed"; // a person closes it

        PlayerbotQaAggregator current = one;
        QaLedgerState ledger = first.Ledger.Export();
        string build = "build-1";
        if (newerBuild)
        {
            build = "build-2";
            (current, ledger) = Restart(one, ledger, build);
        }

        Occurs(current, 3);
        rig.Time.Advance(TimeSpan.FromHours(25)); // the comment interval has passed: the pass looks the issue up
        await rig.Run(rig.Publisher(current, build, ledger), current);

        if (newerBuild)
        {
            Assert.Equal(1, rig.GitHub.Patches);
            Assert.Equal(2, rig.CommentBodies().Length);
            Assert.Contains("Regression:", rig.CommentBodies()[1], StringComparison.Ordinal);
            Assert.Equal(QaPublicationState.Published, Of(current, id).Publication.State);
        }
        else
        {
            Assert.Equal(0, rig.GitHub.Patches); // closed while the same build still met it: a decision, not a fix
            Assert.Single(rig.CommentBodies());
            Assert.Equal(QaPublicationState.KnownClosed, Of(current, id).Publication.State);
            Assert.Equal("build-1", Of(current, id).ClosedBuild);
        }
    }

    [Fact]
    public async Task ARevivedTombstone_IsClosedOnTheBuildThatRevivedIt_AndRegressesOnALaterNewerOne()
    {
        var (rig, one, id, _, ledger) = await ClosedOnBuildOne();
        QaPruneResult pruned = one.Prune(QaData.T0.AddHours(100), TimeSpan.FromHours(1), closed: true, dry: false);
        Assert.Equal(1, pruned.Count);
        Assert.Empty(one.Snapshot());

        Assert.Equal(id, Occurs(one, 3)); // the same problem again: it takes the closed issue back
        QaFindingView revived = Of(one, id);
        Assert.Equal(QaPublicationState.KnownClosed, revived.Publication.State);
        Assert.Equal("build-1", revived.ClosedBuild);
        PlayerbotQaPublisher onOne = rig.Publisher(one, "build-1", ledger);
        await rig.Run(onOne, one);
        Assert.Equal(0, rig.GitHub.Patches);

        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, onOne.Ledger.Export(), "build-2");
        Occurs(two, 3);
        await rig.Run(rig.Publisher(two, "build-2", l2), two);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Single(rig.CommentBodies());
    }

    [Fact]
    public async Task ReopenClosed_WithReopenOnRegression_GivesARegressionTheRegressionComment()
    {
        var (rig, one, _, issue, ledger) = await ClosedOnBuildOne();
        rig.Options.GitHub.ReopenClosed = true;
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 3);

        await rig.Run(rig.Publisher(two, "build-2", l2), two);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Contains("Regression:", Assert.Single(rig.CommentBodies()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReopenClosed_WithReopenOnRegression_ReopensWithoutAnyCommentWhenThereIsNoBudget()
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        rig.Options.GitHub.ReopenClosed = true;
        rig.Options.GitHub.MaxCommentsPerHour = 0;
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 3);

        await rig.Run(rig.Publisher(two, "build-2", l2), two);

        Assert.Equal(1, rig.GitHub.Patches); // ReopenClosed keeps its meaning: reopen whatever the cap says
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Empty(rig.CommentBodies());
        Assert.Equal(QaPublicationState.Published, Of(two, id).Publication.State);
    }

    [Fact]
    public async Task ReopenClosed_OnTheSameBuild_StillReopens_WithTheRoutineComment()
    {
        var (rig, one, _, issue, ledger) = await ClosedOnBuildOne();
        rig.Options.GitHub.ReopenClosed = true;
        Occurs(one, 3, at: QaData.T0);

        await rig.Run(rig.Publisher(one, "build-1", ledger), one);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.DoesNotContain("Regression:", Assert.Single(rig.CommentBodies()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegression_NeedsTheNewBuildsOwnOccurrences_NotASingleOne()
    {
        var (rig, one, id, _, ledger) = await ClosedOnBuildOne();
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 2); // MinOccurrences is 3
        PlayerbotQaPublisher onTwo = rig.Publisher(two, "build-2", l2);

        await rig.Run(onTwo, two);

        Assert.Equal(0, rig.GitHub.Patches);
        Occurs(two, 1);
        await rig.Run(onTwo, two);
        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal(QaPublicationState.Published, Of(two, id).Publication.State);
    }

    [Theory]
    [InlineData("not_planned", 0)]
    [InlineData("completed", 1)]
    public async Task AnIssueClosedAsNotPlanned_IsNeverReopenedByARegression(string reason, int expectedReopens)
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        rig.GitHub.Get(issue)["state_reason"] = reason;
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 3);

        await rig.Run(rig.Publisher(two, "build-2", l2), two);

        Assert.Equal(expectedReopens, rig.GitHub.Patches);
        Assert.Equal(expectedReopens, rig.CommentBodies().Length);
        Assert.Equal(expectedReopens == 0 ? QaPublicationState.KnownClosed : QaPublicationState.Published, Of(two, id).Publication.State);
    }

    [Fact]
    public async Task ACappedRegression_IsLoggedOncePerFindingPerProcess()
    {
        var (rig, one, _, _, ledger) = await ClosedOnBuildOne(git => git.MaxCommentsPerHour = 0);
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 3);
        var log = new ListLogger();
        var publisher = new PlayerbotQaPublisher(rig.Options, Path.GetTempPath(), two, new PlayerbotQaRedactor(), log,
            name => name == TokenVariable ? QaData.FakeToken : null,
            token => new PlayerbotQaGitHubClient(rig.GitHub.Http, QaData.Repo, token, rig.Options.GitHub, (_, _) => Task.CompletedTask, null, () => rig.Time.GetUtcNow()),
            rig.Time, "build-2", null, (_, _) => Task.CompletedTask);
        publisher.Ledger.Restore(l2);

        await rig.Run(publisher, two);
        await rig.Run(publisher, two);
        await rig.Run(publisher, two);

        Assert.Equal(1, log.At(Microsoft.Extensions.Logging.LogLevel.Information).Count(m => m.Contains("comment cap", StringComparison.Ordinal)));
        Assert.Equal(0, rig.GitHub.Patches);
    }

    // ----- the comment ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RegressionComment_NeverCarriesAPlayerNameOrAGuid_FromTheSample()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);
        QaFindingView view = QaData.View("aaaaaaaaaaaaaaaa", count: 6, samples: [new QaSample(QaData.T0, true, 0, 1, 2, 3, [], $"Playerbot {QaData.Bot1} (Ironwander) stalled 120s: goal=Quest")])
            with { BuildCounts = [new QaBuildCount("build-2", 3)] };

        string text = PlayerbotQaIssueComposer.RegressionComment(view, "build-2", "build-1", redactor);

        Assert.Contains("Sample:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ironwander", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(QaData.Bot1.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegressionComment_OmitsTheBotCount_ForAServerFault()
    {
        var redactor = new PlayerbotQaRedactor();
        QaFindingView fault = QaData.View("bbbbbbbbbbbbbbbb", count: 6, kind: QaSignalKind.ServerFault, bots: 1) with { BuildCounts = [new QaBuildCount("build-2", 3)] };
        QaFindingView stall = fault with { Kind = QaSignalKind.Stall };

        Assert.DoesNotContain("distinct bots", PlayerbotQaIssueComposer.RegressionComment(fault, "build-2", "build-1", redactor), StringComparison.Ordinal);
        Assert.Contains("distinct bots 1", PlayerbotQaIssueComposer.RegressionComment(stall, "build-2", "build-1", redactor), StringComparison.Ordinal);
    }

    // ----- merging ----------------------------------------------------------------------------------------------------------------------------

    private static string Loop(PlayerbotQaAggregator aggregator, DateTime at, params uint[] attackers)
        => aggregator.Ingest(new QaSignal
        {
            Kind = QaSignalKind.DeathLoop, AtUtc = at, BotKey = Guid.NewGuid(), HasPosition = true, Map = 0, X = 1, Y = 1, Z = 50f, Attackers = attackers,
            Message = "Playerbot <bot> death loop",
        }, NoQaContentFacts.Instance, at) ?? throw new InvalidOperationException("refused");

    [Fact]
    public void Merge_CombinesTheBuildStamps_AndHandsOverTheReportedBuild()
    {
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string x = Loop(one, QaData.T0, 1);
        Loop(one, QaData.T0, 1);
        Loop(one, QaData.T0, 1);
        string y = Loop(one, QaData.T0, 2);
        Assert.NotEqual(x, y);
        one.Apply(y, new QaPublicationUpdate(new QaPublication(QaPublicationState.DryRun), null, 1, Reported: true)); // y was reported on build-1, x was not
        Assert.Equal("build-1", Of(one, y).ReportedBuild);

        (PlayerbotQaAggregator two, _) = Restart(one, new QaLedgerState([], []), "build-2");
        DateTime later = QaData.T0.AddHours(1);
        Loop(two, later, 2);
        Loop(two, later, 1, 2); // bridges the two loops

        Assert.Equal(1, two.Merged);
        QaFindingView merged = Only(two);
        Assert.Equal(x, merged.Id); // the busier one survives
        Assert.Equal(6, merged.Count);
        Assert.Equal("build-1", merged.FirstBuild);
        Assert.Equal("build-2", merged.LastBuild);
        Assert.Equal([new QaBuildCount("build-2", 2), new QaBuildCount("build-1", 4)], merged.BuildCounts);
        Assert.Equal(QaPublicationState.DryRun, merged.Publication.State);
        Assert.Equal("build-1", merged.ReportedBuild);
    }

    [Fact]
    public void Merge_KeepsTheSurvivingKnownClosedFindingsClosedBuild()
    {
        PlayerbotQaAggregator aggregator = NewAggregator("build-1");
        string x = Loop(aggregator, QaData.T0, 1);
        Loop(aggregator, QaData.T0, 1);
        string y = Loop(aggregator, QaData.T0, 2);
        aggregator.Apply(x, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, 9)));
        Assert.Equal("build-1", Of(aggregator, x).ClosedBuild);

        Loop(aggregator, QaData.T0, 1, 2);

        QaFindingView merged = Only(aggregator);
        Assert.Equal(x, merged.Id);
        Assert.NotEqual(y, merged.Id);
        Assert.Equal(new QaPublication(QaPublicationState.KnownClosed, 9), merged.Publication);
        Assert.Equal("build-1", merged.ClosedBuild);
        Assert.Equal([new QaBuildCount("build-1", 4)], merged.BuildCounts);
    }

    // ----- eviction from the build registry -----------------------------------------------------------------------------------------------

    /// <summary>Restart on <paramref name="count"/> more builds that never meet the finding: enough to push the earliest builds out of the registry.</summary>
    private (PlayerbotQaAggregator Aggregator, QaLedgerState Ledger) Churn(PlayerbotQaAggregator aggregator, QaLedgerState ledger, int count)
    {
        for (int i = 1; i <= count; i++) (aggregator, ledger) = Restart(aggregator, ledger, "churn-" + i);
        return (aggregator, ledger);
    }

    [Fact]
    public async Task AReferenceBuildEvictedFromTheRegistry_StillLetsANewerBuildReopen()
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        (PlayerbotQaAggregator churned, QaLedgerState l1) = Churn(one, ledger, PlayerbotQaAggregator.MaxKnownBuilds + PlayerbotQaAggregator.MaxEvictedBuilds + 5);
        Assert.DoesNotContain(churned.Export().Builds!, b => b.Build == "build-1");
        Assert.DoesNotContain(churned.Export().EvictedBuilds!, b => b.Build == "build-1"); // even the evicted tail has forgotten it
        Assert.NotNull(Of(churned, id).ClosedBuildSeenUtc); // the finding kept the time itself

        (PlayerbotQaAggregator newest, QaLedgerState l2) = Restart(churned, l1, "build-newest");
        Occurs(newest, 3);
        await rig.Run(rig.Publisher(newest, "build-newest", l2), newest);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Single(rig.CommentBodies());
    }

    [Fact]
    public async Task ARollbackToAnEvictedBuild_IsStillOld_AndDoesNotReopen()
    {
        var rig = new Rig();
        PlayerbotQaAggregator a = NewAggregator("build-A");
        string id = Occurs(a, 3);
        (PlayerbotQaAggregator b, QaLedgerState lb) = Restart(a, new QaLedgerState([], []), "build-B");
        Occurs(b, 3);
        rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id), open: false);
        PlayerbotQaPublisher onB = rig.Publisher(b, "build-B", lb);
        await rig.Run(onB, b);
        Assert.Equal("build-B", Of(b, id).ClosedBuild);

        (PlayerbotQaAggregator churned, QaLedgerState l1) = Churn(b, onB.Ledger.Export(), PlayerbotQaAggregator.MaxKnownBuilds + 5);
        Assert.DoesNotContain(churned.Export().Builds!, x => x.Build == "build-A"); // A left the registry; its time is remembered in the evicted tail
        Assert.Contains(churned.Export().EvictedBuilds!, x => x.Build == "build-A");

        (PlayerbotQaAggregator rolledBack, QaLedgerState l2) = Restart(churned, l1, "build-A");
        Assert.False(rolledBack.IsBuildNewer("build-A", "build-B")); // not re-registered at "now"
        Occurs(rolledBack, 3);
        await rig.Run(rig.Publisher(rolledBack, "build-A", l2), rolledBack);

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal(0, rig.GitHub.Comments);
        Assert.Equal(QaPublicationState.KnownClosed, Of(rolledBack, id).Publication.State);
    }

    // ----- not planned ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANotPlannedFinding_MakesNoApiCallOnLaterPasses()
    {
        var (rig, one, id, issue, ledger) = await ClosedOnBuildOne();
        rig.GitHub.Get(issue)["state_reason"] = "not_planned";
        (PlayerbotQaAggregator two, QaLedgerState l2) = Restart(one, ledger, "build-2");
        Occurs(two, 3);
        PlayerbotQaPublisher publisher = rig.Publisher(two, "build-2", l2);

        await rig.Run(publisher, two); // the first pass looks the issue up and learns it is a decision

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.True(Of(two, id).NotPlanned);
        int calls = rig.GitHub.Http.Requests.Length;
        Occurs(two, 2);
        await rig.Run(publisher, two);
        await rig.Run(publisher, two);

        Assert.Equal(calls, rig.GitHub.Http.Requests.Length); // nothing is listed or looked up again
        (PlayerbotQaAggregator three, _) = Restart(two, publisher.Ledger.Export(), "build-3");
        Assert.True(Of(three, id).NotPlanned); // persisted
    }

    // ----- live status in the issue body (GitHub's last-seen lagged up to a day: one comment per CommentIntervalHours) ------------------------

    private string BodyOf(Rig rig, int issue) => (string)rig.GitHub.Get(issue)["body"]!;

    private static int Blocks(string body) => body.Split(PlayerbotQaIssueComposer.StatusBegin).Length - 1;

    [Fact]
    public async Task LiveStatus_TheNewIssueCarriesIt_AndItFollowsTheFinding_BetweenTheDailyComments()
    {
        var rig = new Rig();
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occurs(one, 3, at: QaData.T0);
        PlayerbotQaPublisher publisher = rig.Publisher(one, "build-1");
        await rig.Run(publisher, one);
        int issue = Of(one, id).Publication.Issue;
        string created = BodyOf(rig, issue);
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(id) + "\n" + PlayerbotQaIssueComposer.StatusBegin, created, StringComparison.Ordinal);
        Assert.Contains("<!-- arcanecore-bot-qa-status last-seen=2026-10-10T12:00:00Z count=3 build-count=3 bots=2 -->", created, StringComparison.Ordinal);
        Assert.Equal(QaData.T0, PlayerbotQaIssueComposer.StatusLastSeen(created));

        // Two hours later it happened again: no comment is due (one a day), but the body's status follows.
        rig.Time.Advance(TimeSpan.FromHours(2));
        Occur(one, QaData.Bot1, at: rig.Time.GetUtcNow().UtcDateTime);
        await rig.Run(publisher, one);

        Assert.Equal(0, rig.GitHub.Comments);
        Assert.Equal(1, rig.GitHub.BodyEdits);
        string edited = BodyOf(rig, issue);
        Assert.Contains("<!-- arcanecore-bot-qa-status last-seen=2026-10-10T14:00:00Z count=4 build-count=4 bots=2 -->", edited, StringComparison.Ordinal);
        Assert.Equal(1, Blocks(edited));
        string tail = created[(created.IndexOf(PlayerbotQaIssueComposer.StatusEnd, StringComparison.Ordinal) + PlayerbotQaIssueComposer.StatusEnd.Length)..];
        Assert.EndsWith(tail, edited, StringComparison.Ordinal); // the rest of the body is untouched

        // Ten minutes later: within StatusUpdateMinutes (60) of what the body shows, nothing is edited.
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        Occur(one, QaData.Bot2, at: rig.Time.GetUtcNow().UtcDateTime);
        await rig.Run(publisher, one);
        Assert.Equal(1, rig.GitHub.BodyEdits);

        // An hour after the time the body shows: edited again.
        rig.Time.Advance(TimeSpan.FromMinutes(55));
        Occur(one, QaData.Bot1, at: rig.Time.GetUtcNow().UtcDateTime);
        await rig.Run(publisher, one);
        Assert.Equal(2, rig.GitHub.BodyEdits);
        Assert.Equal(QaData.T0.AddMinutes(185), PlayerbotQaIssueComposer.StatusLastSeen(BodyOf(rig, issue)));
        Assert.Equal(0, rig.GitHub.Comments);
    }

    [Fact]
    public async Task LiveStatus_IsInsertedAfterTheMarker_OfAnIssueOpenedBeforeIt_KeepingTheRestOfTheBody()
    {
        var rig = new Rig();
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occurs(one, 3, at: QaData.T0);
        string old = PlayerbotQaIssueComposer.MarkerOf(id) + "\nPlayerbot QA finding: an old body\n\n| Field | Value |";
        int issue = rig.GitHub.Add(old);

        await rig.Run(rig.Publisher(one, "build-1"), one);

        Assert.Equal(1, rig.GitHub.Comments); // the occurrences since it was opened
        Assert.Equal(1, rig.GitHub.BodyEdits);
        string body = BodyOf(rig, issue);
        Assert.Equal(PlayerbotQaIssueComposer.WithStatus(old, PlayerbotQaIssueComposer.StatusBlock(Of(one, id), "build-1")), body);
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(id) + "\n" + PlayerbotQaIssueComposer.StatusBegin, body, StringComparison.Ordinal);
        Assert.EndsWith("\nPlayerbot QA finding: an old body\n\n| Field | Value |", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(60, 0)]
    public async Task LiveStatus_IsOffAtZeroMinutes_AndHeldByTheHourlyEditCap(int minutes, int cap)
    {
        var rig = new Rig(git =>
        {
            git.StatusUpdateMinutes = minutes;
            git.MaxStatusEditsPerHour = cap;
        });
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occurs(one, 3, at: QaData.T0);
        int issue = rig.GitHub.Add(PlayerbotQaIssueComposer.MarkerOf(id) + "\nold");

        await rig.Run(rig.Publisher(one, "build-1"), one);

        Assert.Equal(1, rig.GitHub.Comments);
        Assert.Equal(0, rig.GitHub.BodyEdits);
        Assert.Equal(PlayerbotQaIssueComposer.MarkerOf(id) + "\nold", BodyOf(rig, issue));
    }

    [Fact]
    public void LiveStatus_Block_IsReplacedInPlace_AndABodyWithoutAMarker_GetsItOnTop()
    {
        QaFindingView first = QaData.View("aaaaaaaaaaaaaaaa", count: 3);
        QaFindingView later = first with { Count = 9, LastSeenUtc = QaData.T0.AddHours(5) };
        string marker = PlayerbotQaIssueComposer.MarkerOf(first.Id);
        string body = marker + "\n" + PlayerbotQaIssueComposer.StatusBlock(first, "b") + "\n\nrest";

        Assert.Equal(marker + "\n" + PlayerbotQaIssueComposer.StatusBlock(later, "b") + "\n\nrest", PlayerbotQaIssueComposer.WithStatus(body, PlayerbotQaIssueComposer.StatusBlock(later, "b")));
        Assert.Equal(QaData.T0.AddHours(5), PlayerbotQaIssueComposer.StatusLastSeen(PlayerbotQaIssueComposer.WithStatus(body, PlayerbotQaIssueComposer.StatusBlock(later, "b"))));
        Assert.Equal(PlayerbotQaIssueComposer.StatusBlock(later, "b") + "\n\nplain", PlayerbotQaIssueComposer.WithStatus("plain", PlayerbotQaIssueComposer.StatusBlock(later, "b")));
        Assert.Null(PlayerbotQaIssueComposer.StatusLastSeen(marker + "\nno block"));
        Assert.Null(PlayerbotQaIssueComposer.StatusLastSeen(null));
    }

    // ----- closing the issue of a finding that stopped (AutoCloseQuietMultiple, off by default) --------------------------------------------------

    /// <summary>A finding met three times an hour apart on build-1 (usual gap 1 h), its issue opened there, then the server restarted on build-2 at T0+12h.</summary>
    private async Task<(Rig Rig, PlayerbotQaAggregator Two, PlayerbotQaPublisher Publisher, string Id, int Issue)> StoppedOnBuildOne(Action<PlayerbotQaGitHubOptions>? configure, double gapHours = 1)
    {
        var rig = new Rig(configure);
        PlayerbotQaAggregator one = NewAggregator("build-1");
        string id = Occur(one, QaData.Bot1, at: QaData.T0);
        Occur(one, QaData.Bot2, at: QaData.T0.AddHours(gapHours));
        Occur(one, QaData.Bot1, at: QaData.T0.AddHours(2 * gapHours));
        rig.Time.Advance(TimeSpan.FromHours(2 * gapHours));
        PlayerbotQaPublisher first = rig.Publisher(one, "build-1");
        await rig.Run(first, one);
        int issue = Of(one, id).Publication.Issue;
        Assert.Equal(1, rig.GitHub.Creates);

        rig.Time.Advance(TimeSpan.FromHours(12 - (2 * gapHours)));
        await rig.Run(first, one); // quiet for hours, but on the build that last met it: never closed
        Assert.Equal(0, rig.GitHub.Patches);

        (PlayerbotQaAggregator two, QaLedgerState ledger) = Restart(one, first.Ledger.Export(), "build-2", at: rig.Time.GetUtcNow().UtcDateTime);
        return (rig, two, rig.Publisher(two, "build-2", ledger), id, issue);
    }

    [Fact]
    public async Task QuietClose_ClosesTheIssueOfAFindingThatStoppedOnANewerBuild_AndARecurrenceReopensIt()
    {
        var (rig, two, publisher, id, issue) = await StoppedOnBuildOne(git =>
        {
            git.AutoCloseQuietMultiple = 3;
            git.AutoCloseMinQuietHours = 1;
        });

        rig.Time.Advance(TimeSpan.FromHours(2)); // build-2 has gone 2 h without it: less than 3 x 1 h
        await rig.Run(publisher, two);
        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);

        rig.Time.Advance(TimeSpan.FromHours(1.5));
        await rig.Run(publisher, two);

        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("closed", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Equal("completed", (string)rig.GitHub.Get(issue)["state_reason"]!);
        string comment = Assert.Single(rig.CommentBodies());
        Assert.StartsWith("Closing as completed", comment, StringComparison.Ordinal);
        Assert.Contains("build-1", comment, StringComparison.Ordinal);
        Assert.Contains("build-2", comment, StringComparison.Ordinal);
        QaFindingView closed = Of(two, id);
        Assert.Equal(new QaPublication(QaPublicationState.KnownClosed, issue), closed.Publication);
        Assert.Equal("build-1", closed.ClosedBuild); // the build it was last met on, so build-2 meeting it is a regression
        Assert.False(closed.NotPlanned);

        await rig.Run(publisher, two); // nothing more to do
        Assert.Equal(1, rig.GitHub.Patches);

        // It happens again on build-2: the issue is reopened with one comment.
        Occurs(two, 3, at: rig.Time.GetUtcNow().UtcDateTime);
        await rig.Run(publisher, two);

        Assert.Equal(2, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Equal(2, rig.CommentBodies().Length);
        Assert.StartsWith("Regression", rig.CommentBodies()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietClose_IsOffByDefault()
    {
        Assert.Equal(0, new PlayerbotQaGitHubOptions().AutoCloseQuietMultiple);
        var (rig, two, publisher, _, issue) = await StoppedOnBuildOne(null);

        rig.Time.Advance(TimeSpan.FromDays(60));
        await rig.Run(publisher, two);

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Empty(rig.CommentBodies());
    }

    [Fact]
    public async Task QuietClose_WaitsForTheMinimumQuietHours_WhenTheUsualGapIsShort()
    {
        // Met a minute apart: 3 x the gap is 3 minutes, but nothing closes before AutoCloseMinQuietHours (72 by default).
        var (rig, two, publisher, _, issue) = await StoppedOnBuildOne(git => git.AutoCloseQuietMultiple = 3, gapHours: 1.0 / 60);

        rig.Time.Advance(TimeSpan.FromHours(71));
        await rig.Run(publisher, two);
        Assert.Equal(0, rig.GitHub.Patches);

        rig.Time.Advance(TimeSpan.FromHours(1));
        await rig.Run(publisher, two);
        Assert.Equal(1, rig.GitHub.Patches);
        Assert.Equal("closed", (string)rig.GitHub.Get(issue)["state"]!);
    }

    [Fact]
    public async Task QuietClose_LeavesAFindingTheNewBuildAlreadyMet()
    {
        var (rig, two, publisher, id, issue) = await StoppedOnBuildOne(git =>
        {
            git.AutoCloseQuietMultiple = 3;
            git.AutoCloseMinQuietHours = 1;
        });
        Occur(two, QaData.Bot1, at: rig.Time.GetUtcNow().UtcDateTime); // once on build-2: it has not stopped

        rig.Time.Advance(TimeSpan.FromDays(30));
        await rig.Run(publisher, two);

        Assert.Equal(0, rig.GitHub.Patches);
        Assert.Equal("open", (string)rig.GitHub.Get(issue)["state"]!);
        Assert.Equal(QaPublicationState.Published, Of(two, id).Publication.State);
    }

    [Fact]
    public void Options_LiveStatusAndQuietClose_DefaultsAndBounds()
    {
        var git = new PlayerbotQaGitHubOptions();
        Assert.Equal(60, git.StatusUpdateMinutes);
        Assert.Equal(30, git.MaxStatusEditsPerHour);
        Assert.Equal(0, git.AutoCloseQuietMultiple);
        Assert.Equal(72, git.AutoCloseMinQuietHours);
        Assert.False(git.CloseAbsorbedDuplicates);
        Assert.Empty(git.Validate());
        Assert.Contains(new PlayerbotQaGitHubOptions { StatusUpdateMinutes = -1 }.Validate(), p => p.Contains("StatusUpdateMinutes", StringComparison.Ordinal));
        Assert.Contains(new PlayerbotQaGitHubOptions { MaxStatusEditsPerHour = 1001 }.Validate(), p => p.Contains("MaxStatusEditsPerHour", StringComparison.Ordinal));
        Assert.Contains(new PlayerbotQaGitHubOptions { AutoCloseQuietMultiple = -1 }.Validate(), p => p.Contains("AutoCloseQuietMultiple", StringComparison.Ordinal));
        Assert.Contains(new PlayerbotQaGitHubOptions { AutoCloseMinQuietHours = 0 }.Validate(), p => p.Contains("AutoCloseMinQuietHours", StringComparison.Ordinal));
    }
}
