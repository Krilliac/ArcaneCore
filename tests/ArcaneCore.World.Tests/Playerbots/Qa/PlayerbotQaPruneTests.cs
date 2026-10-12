using System.Text;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// <c>.playerbot qa prune</c> and the staleness of the views (docs/areas/playbots-qa.md): findings not seen for N hours, or whose issue is closed, are
/// forgotten locally; a recurrence is created anew and reattaches to its existing issue; <c>top</c> sinks what this build has not met.
/// </summary>
public sealed class PlayerbotQaPruneTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private sealed class Sink : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate) _lines.Add((value ?? string.Empty).Replace(WorldConsoleExecutor.Prefix, string.Empty, StringComparison.Ordinal));
        }

        public string[] Take()
        {
            lock (_gate)
            {
                string[] lines = [.. _lines];
                _lines.Clear();
                return lines;
            }
        }
    }

    private static async Task<string[]> RunAsync(WorldTestHost host, string line)
    {
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        var sink = new Sink();
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, sink);
        await host.World.InvokeAsync(() =>
        {
            executor.Execute(line);
            return 0;
        });
        return sink.Take();
    }

    private WorldTestHost StartWithQa()
    {
        var options = new PlayerbotQaOptions { Directory = _directory };
        return WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options));
            services.AddSingleton(new PlayerbotQaSignalBus(64));
        });
    }

    /// <summary>A stall of quest <paramref name="quest"/> last seen <paramref name="hoursAgo"/> hours ago; the finding's id.</summary>
    private static string Seen(PlayerbotQaFeature feature, uint quest, double hoursAgo)
        => feature.Aggregator!.Ingest(QaData.Stall(QaData.Bot1, 1, 1, quest, at: DateTime.UtcNow.AddHours(-hoursAgo)), NoQaContentFacts.Instance, DateTime.UtcNow)
            ?? throw new InvalidOperationException("refused");

    private static void Close(PlayerbotQaFeature feature, string id, QaPublicationState state = QaPublicationState.KnownClosed)
        => feature.Aggregator!.Apply(id, new QaPublicationUpdate(new QaPublication(state, 127)));

    [Fact]
    public async Task Prune_ByAge_ForgetsTheOldOnes_ReportsKindsAndWhatRemains()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string old = Seen(feature, 1, 30);
        string recent = Seen(feature, 2, 1);
        string now = Seen(feature, 3, 0);

        string[] lines = await RunAsync(host, ".playerbot qa prune");

        Assert.Single(lines);
        Assert.Equal("Bot QA: forgot 1 findings not seen for 12h (stall 1); 2 remain. GitHub is untouched.", lines[0]);
        Assert.Null(feature.Aggregator!.Get(old));
        Assert.NotNull(feature.Aggregator.Get(recent));
        Assert.NotNull(feature.Aggregator.Get(now));

        string[] wider = await RunAsync(host, ".playerbot qa prune #1");
        Assert.Equal("Bot QA: forgot 1 findings not seen for 1h (stall 1); 1 remain. GitHub is untouched.", wider[0]);
        Assert.Null(feature.Aggregator.Get(recent));

        string[] none = await RunAsync(host, ".playerbot qa prune 720");
        Assert.Equal("Bot QA: nothing not seen for 720h; 1 findings remain.", none[0]);
    }

    [Fact]
    public async Task Prune_Closed_AlsoForgetsKnownClosedAtAnyAge_AndKeepsPublishedOnes()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string closedNow = Seen(feature, 1, 0);
        string open = Seen(feature, 2, 0);
        Close(feature, closedNow);
        Close(feature, open, QaPublicationState.Published);

        string[] plain = await RunAsync(host, ".playerbot qa prune");
        Assert.Contains("nothing not seen for 12h", plain[0], StringComparison.Ordinal);
        Assert.NotNull(feature.Aggregator!.Get(closedNow));

        string[] withClosed = await RunAsync(host, ".playerbot qa prune closed");
        Assert.Equal("Bot QA: forgot 1 findings not seen for 12h or GitHub-closed (stall 1); 1 remain. GitHub is untouched.", withClosed[0]);
        Assert.Null(feature.Aggregator.Get(closedNow));
        Assert.Equal(QaPublicationState.Published, feature.Aggregator.Get(open)!.Publication.State);
    }

    [Fact]
    public async Task Prune_Dry_ListsWhatWouldGo_AndChangesNothing()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string old = Seen(feature, 1, 30);
        string closed = Seen(feature, 2, 0);
        Seen(feature, 3, 0);
        Close(feature, closed);

        string[] lines = await RunAsync(host, ".playerbot qa prune 12 closed dry");

        Assert.Equal("Bot QA: dry run, would forget 2 findings not seen for 12h or GitHub-closed (stall 2); 1 would remain. GitHub is untouched.", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.Contains(PlayerbotQaFingerprint.ShortId(old), StringComparison.Ordinal) && l.Contains("1d ago", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(PlayerbotQaFingerprint.ShortId(closed), StringComparison.Ordinal) && l.Contains("KnownClosed#127", StringComparison.Ordinal));
        Assert.Equal(3, feature.Aggregator!.Count);
        Assert.NotNull(feature.Aggregator.Get(old));
    }

    [Fact]
    public async Task Prune_DryList_IsBounded_AndKindsAreCapped()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        for (uint quest = 1; quest <= 30; quest++) Seen(feature, quest, 40);

        string[] lines = await RunAsync(host, ".playerbot qa prune dry");

        Assert.Equal(1 + 10 + 1, lines.Length);
        Assert.Contains("would forget 30 findings", lines[0], StringComparison.Ordinal);
        Assert.Equal("  ... and 20 more.", lines[^1]);
        Assert.All(lines, l => Assert.True(l.Length <= 220));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("99999999999")]
    public async Task Prune_HoursOutOfBounds_IsRefused_AndPrunesNothing(string hours)
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        Seen(feature, 1, 100);

        string[] lines = await RunAsync(host, ".playerbot qa prune " + hours);

        Assert.True(lines[0].Contains("1..720", StringComparison.Ordinal) || lines[0].Contains("Syntax", StringComparison.Ordinal), lines[0]);
        Assert.Equal(1, feature.Aggregator!.Count);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("-5")]
    [InlineData("12 24")]
    [InlineData("closed closed")]
    [InlineData("dry dry")]
    [InlineData("12 closed dry extra")]
    public async Task Prune_SyntaxErrors_ShowUsage_AndPruneNothing(string args)
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        Seen(feature, 1, 100);

        string[] lines = await RunAsync(host, ".playerbot qa prune " + args);

        Assert.Contains(lines, l => l.Contains("Syntax", StringComparison.Ordinal));
        Assert.Equal(1, feature.Aggregator!.Count);
    }

    [Fact]
    public async Task Prune_AcceptsTheWordsInAnyOrder_AndAnyCase()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string id = Seen(feature, 1, 0);
        Close(feature, id);

        string[] dry = await RunAsync(host, ".playerbot qa prune DRY Closed 5");
        Assert.Contains("would forget 1 findings not seen for 5h or GitHub-closed", dry[0], StringComparison.Ordinal);
        string[] real = await RunAsync(host, ".playerbot qa prune closed");
        Assert.StartsWith("Bot QA: forgot 1 findings", real[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_NeverTouchesTheStartUpCoverageFindings()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string gap = feature.Aggregator!.Ingest(PlayerbotQaSpellCoreTests.Gap(133, coverage: true) with { AtUtc = DateTime.UtcNow.AddHours(-100) }, NoQaContentFacts.Instance, DateTime.UtcNow)!;

        string[] lines = await RunAsync(host, ".playerbot qa prune");

        Assert.Contains("nothing", lines[0], StringComparison.Ordinal);
        Assert.NotNull(feature.Aggregator.Get(gap));
    }

    [Fact]
    public async Task Top_SinksStaleFindingsBehindAHint_ShowsTheAge_AndAllListsThemLast()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string stale = Seen(feature, 1, 30);
        string current = Seen(feature, 2, 0);
        for (int i = 0; i < 5; i++) Seen(feature, 1, 30); // the stale one has the higher count, so it would outrank the current one

        string[] top = await RunAsync(host, ".playerbot qa top");
        string[] all = await RunAsync(host, ".playerbot qa top all");
        string[] allN = await RunAsync(host, ".playerbot qa top 5 all");
        string[] root = await RunAsync(host, ".playerbot qa");

        Assert.Equal(3, top.Length);
        Assert.Contains("top 1 of 1 findings", top[0], StringComparison.Ordinal);
        Assert.StartsWith("#1 " + PlayerbotQaFingerprint.ShortId(current), top[1], StringComparison.Ordinal);
        Assert.Contains(" <1m ago ", top[1], StringComparison.Ordinal);
        Assert.Equal("1 stale findings (not seen since this server started) hidden; .playerbot qa top all lists them. .playerbot qa prune [#hours] [closed] [dry] forgets old ones; a finding with an open issue is always kept.", top[2]);
        Assert.DoesNotContain(top, l => l.Contains(PlayerbotQaFingerprint.ShortId(stale), StringComparison.Ordinal));

        Assert.Equal(3, all.Length);
        Assert.Contains("top 2 of 2 findings", all[0], StringComparison.Ordinal);
        Assert.StartsWith("#1 " + PlayerbotQaFingerprint.ShortId(current), all[1], StringComparison.Ordinal);
        Assert.StartsWith("#2 " + PlayerbotQaFingerprint.ShortId(stale), all[2], StringComparison.Ordinal);
        Assert.Contains(" 1d ago (stale) ", all[2], StringComparison.Ordinal);
        Assert.Equal(all, allN);
        Assert.Contains(root, l => l.Contains("stale findings", StringComparison.Ordinal)); // the bare command hides them the same way
    }

    [Fact]
    public async Task Top_WhenEveryFindingIsStale_SaysSo_AndHintsAtAll()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        Seen(feature, 1, 30);

        string[] top = await RunAsync(host, ".playerbot qa top");

        Assert.Single(top);
        Assert.StartsWith("Bot QA: no findings seen since this server started. 1 stale findings", top[0], StringComparison.Ordinal);
        Assert.Contains("qa top all lists them", top[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Top_BadArguments_ShowUsage()
    {
        await using WorldTestHost host = StartWithQa();

        Assert.Contains(await RunAsync(host, ".playerbot qa top banana"), l => l.Contains("Syntax", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(host, ".playerbot qa top 3 5"), l => l.Contains("Syntax", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(host, ".playerbot qa top all all"), l => l.Contains("Syntax", StringComparison.Ordinal));
    }

    // ----- the pure views --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "<1m ago")]
    [InlineData(59, "<1m ago")]
    [InlineData(60, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(3600, "1h ago")]
    [InlineData(2 * 3600 + 1800, "2h ago")]
    [InlineData(23 * 3600 + 3599, "23h ago")]
    [InlineData(24 * 3600, "1d ago")]
    [InlineData(30 * 3600, "1d ago")]
    [InlineData(5 * 86400, "5d ago")]
    [InlineData(-30, "<1m ago")]
    public void Age_IsAShortWord(int seconds, string expected)
        => Assert.Equal(expected, PlayerbotQaReport.Age(QaData.T0, QaData.T0.AddSeconds(seconds)));

    [Fact]
    public void Markdown_ListsStaleFindingsLast_InASectionOfTheirOwn()
    {
        DateTime start = QaData.T0;
        QaFindingView current = QaData.View("c0000000c0000000", severity: 10, last: start.AddHours(1), subject: "current thing");
        QaFindingView stale = QaData.View("5000000050000000", severity: 90, count: 50, last: start.AddHours(-30), subject: "stale thing");

        string md = PlayerbotQaReport.RenderMarkdown([stale, current], start.AddHours(2), "b", new PlayerbotQaRedactor(), since: start);

        Assert.Contains("1 more were not seen since", md, StringComparison.Ordinal);
        Assert.Contains("## Stale (not seen since " + start.ToString("u"), md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("current thing", StringComparison.Ordinal) < md.IndexOf("## Stale", StringComparison.Ordinal));
        Assert.True(md.IndexOf("## Stale", StringComparison.Ordinal) < md.IndexOf("stale thing", StringComparison.Ordinal));
        Assert.Contains("| 1 | `c0000000` | 10.0 | 3 | 1h ago |", md, StringComparison.Ordinal);
        Assert.Contains("| `50000000` | 90.0 | 50 | 1d ago |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("## 2.", md, StringComparison.Ordinal); // a stale finding has no detail block, only a row
    }

    [Fact]
    public void Markdown_WithoutAStartTime_HasNoStaleSection()
    {
        string md = PlayerbotQaReport.RenderMarkdown([QaData.View("5000000050000000", last: QaData.T0.AddDays(-9))], QaData.T0, "b", new PlayerbotQaRedactor());

        Assert.DoesNotContain("Stale", md, StringComparison.Ordinal);
        Assert.Contains("## 1.", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_MarksStaleEntries()
    {
        string json = PlayerbotQaReport.RenderJson([QaData.View("5000000050000000", last: QaData.T0.AddDays(-9)), QaData.View("6000000060000000", last: QaData.T0)], QaData.T0, "b",
            new PlayerbotQaRedactor(), since: QaData.T0.AddHours(-1));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"Stale\": true"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"Stale\": false"));
    }

    // ----- the feature: persistence, recurrence, the automatic prune -------------------------------------------------------------------------

    private sealed class MarkerTransport(Func<string> marked, Func<bool> open, Action onCreate, Action onComment) : IQaIssueTransport
    {
        public bool SupportsSearch => false;

        public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
        {
            var index = new QaIssueIndex(complete: true);
            if (marked() is { Length: > 0 } id) index.Add(new QaIssueRef(42, open(), "https://github.com/x/y/issues/42"), PlayerbotQaIssueComposer.MarkerOf(id));
            return Task.FromResult(index);
        }

        public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken) => Task.FromResult<(QaIssueRef, string)?>(null);

        public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

        public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
        {
            onCreate();
            return Task.FromResult(new QaIssueRef(99, true, "https://github.com/x/y/issues/99"));
        }

        public Task CommentAsync(int number, string body, CancellationToken cancellationToken)
        {
            onComment();
            return Task.CompletedTask;
        }

        public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private PlayerbotQaFeature NewFeature(PlayerbotQaOptions options, ManualTime time, PlayerbotQaSignalBus bus, Func<string, IQaIssueTransport>? transport = null)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(bus)
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services)
        {
            EnvironmentReader = _ => QaData.FakeToken,
            TransportFactory = transport,
            Time = time,
        };
        feature.Attach(null!);
        return feature;
    }

    private static PlayerbotQaOptions PublishOptions(string directory) => new()
    {
        Directory = directory,
        GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 1, MinDistinctBots = 1 },
    };

    [Fact]
    public async Task AnIssueLessFinding_PrunedByAge_AndRecurring_ReattachesToItsIssueByMarker_WithoutAnotherPublish()
    {
        var time = new ManualTime(QaData.T0);
        var bus = new PlayerbotQaSignalBus(64);
        string marked = string.Empty;
        int creates = 0, comments = 0;
        PlayerbotQaFeature feature = NewFeature(PublishOptions(_directory), time, bus, _ => new MarkerTransport(() => marked, () => true, () => creates++, () => comments++));

        Assert.True(bus.TryWrite(QaData.Relay(7, QaData.T0)));
        feature.Drain();
        string id = feature.Aggregator!.Snapshot().Single().Id; // never published: it holds no issue number, so age may forget it
        time.Advance(TimeSpan.FromHours(13));
        Assert.Equal(1, feature.Prune(12, closed: false, dry: true).Count);
        Assert.Equal(1, feature.Aggregator.Count);
        QaPruneResult pruned = feature.Prune(12, closed: false, dry: false);
        Assert.Equal(1, pruned.Count);
        Assert.Equal(0, pruned.Remaining);

        feature.RunPass(CancellationToken.None); // the next save: the finding is gone from the file too
        await feature.Publishing;
        Assert.DoesNotContain(id, await File.ReadAllTextAsync(Path.Combine(_directory, "findings.json")), StringComparison.Ordinal);

        marked = id; // meanwhile its issue exists on GitHub, carrying this finding's marker
        Assert.True(bus.TryWrite(QaData.Relay(7, time.GetUtcNow().UtcDateTime)));
        feature.Drain();
        QaFindingView again = feature.Aggregator.Snapshot().Single();
        Assert.Equal(id, again.Id);
        Assert.Equal(1, again.Count);
        Assert.Equal(QaPublicationState.Pending, again.Publication.State);

        feature.RunPass(CancellationToken.None);
        await feature.Publishing;

        Assert.Equal(0, creates);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 42), feature.Aggregator.Get(id)!.Publication);
        await feature.StopAsync();
    }

    [Fact]
    public async Task PruneByAge_NeverForgetsAFindingThatHoldsAnIssue()
    {
        var time = new ManualTime(QaData.T0);
        var bus = new PlayerbotQaSignalBus(64);
        string marked = string.Empty;
        PlayerbotQaFeature feature = NewFeature(PublishOptions(_directory), time, bus, _ => new MarkerTransport(() => marked, () => true, () => { }, () => { }));
        Assert.True(bus.TryWrite(QaData.Relay(7, QaData.T0)));
        feature.Drain();
        string id = feature.Aggregator!.Snapshot().Single().Id;
        marked = id;
        feature.RunPass(CancellationToken.None);
        await feature.Publishing;
        Assert.Equal(QaPublicationState.Published, feature.Aggregator.Get(id)!.Publication.State);
        time.Advance(TimeSpan.FromHours(100));

        QaPruneResult result = feature.Prune(12, closed: true, dry: false);

        Assert.Equal(0, result.Count);
        Assert.Equal(1, result.Kept);
        Assert.NotNull(feature.Aggregator.Get(id));
        await feature.StopAsync();
    }

    [Fact]
    public async Task PruneClosed_LeavesATombstone_SoARecurrence_EvenAfterARestart_StaysKnownClosed_WithoutCreateOrComment()
    {
        var time = new ManualTime(QaData.T0);
        var bus = new PlayerbotQaSignalBus(64);
        string marked = string.Empty;
        int creates = 0, comments = 0;
        PlayerbotQaFeature feature = NewFeature(PublishOptions(_directory), time, bus, _ => new MarkerTransport(() => marked, () => false, () => creates++, () => comments++));
        Assert.True(bus.TryWrite(QaData.Relay(7, QaData.T0)));
        feature.Drain();
        string id = feature.Aggregator!.Snapshot().Single().Id;
        marked = id; // issue #42 exists and is CLOSED
        feature.RunPass(CancellationToken.None);
        await feature.Publishing;
        Assert.Equal(new QaPublication(QaPublicationState.KnownClosed, 42), feature.Aggregator.Get(id)!.Publication);

        Assert.Equal(1, feature.Prune(12, closed: true, dry: false).Count);
        Assert.Equal(0, feature.Aggregator.Count);
        marked = string.Empty; // a marker lookup would find nothing now (a transport without search, a label removed): only the tombstone remembers
        await feature.StopAsync(); // saves the tombstone

        time.Advance(TimeSpan.FromHours(1));
        PlayerbotQaFeature restarted = NewFeature(PublishOptions(_directory), time, bus, _ => new MarkerTransport(() => marked, () => false, () => creates++, () => comments++));
        Assert.Equal(0, restarted.Aggregator!.Count);
        Assert.True(bus.TryWrite(QaData.Relay(7, time.GetUtcNow().UtcDateTime)));
        restarted.Drain();
        restarted.RunPass(CancellationToken.None);
        await restarted.Publishing;

        Assert.Equal(0, creates);
        Assert.Equal(0, comments);
        QaFindingView again = restarted.Aggregator.Get(id)!;
        Assert.Equal(new QaPublication(QaPublicationState.KnownClosed, 42), again.Publication);
        Assert.Equal(1, again.Count);
        await restarted.StopAsync();
    }

    private static QaSignal Loop(uint attacker, params uint[] more)
        => new()
        {
            Kind = QaSignalKind.DeathLoop, AtUtc = QaData.T0, BotKey = Guid.NewGuid(), HasPosition = true, Map = 0, X = -10600f, Y = 250f, Z = 50f, Attackers = [attacker, .. more],
            Message = "Playerbot <bot> death loop",
        };

    private static string Take(PlayerbotQaAggregator aggregator, QaSignal signal)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    [Fact]
    public void PruneClosed_OfADeathLoopPair_ThenVariantFirst_GivesEachLoopItsOwnIssue_NeverTheOthers()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, new byte[16]);
        string a = Take(aggregator, Loop(1));
        string b = Take(aggregator, Loop(2)); // the same cell, other attackers: a variant key and id
        Assert.NotEqual(a, b);
        aggregator.Apply(a, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, 11)));
        aggregator.Apply(b, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, 22)));

        QaPruneResult result = aggregator.Prune(QaData.T0.AddHours(1), TimeSpan.FromHours(12), closed: true, dry: false);
        Assert.Equal(2, result.Count);
        Assert.Equal(0, aggregator.Count);

        string b2 = Take(aggregator, Loop(2)); // the variant loop comes back FIRST: its v1 id is a's, which the tombstone of loop 1 holds
        string a2 = Take(aggregator, Loop(1));

        Assert.Equal(b, b2);
        Assert.Equal(a, a2);
        Assert.Equal(22, aggregator.Get(b)!.Publication.Issue);
        Assert.Equal(11, aggregator.Get(a)!.Publication.Issue);
        Assert.Equal(QaPublicationState.KnownClosed, aggregator.Get(a)!.Publication.State);
    }

    [Fact]
    public void PruneByAge_KeepsASurvivorThatWasHandedTheIssueOfAMergedFinding()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, new byte[16]);
        string a = Take(aggregator, Loop(1));
        string b = Take(aggregator, Loop(2));
        Take(aggregator, Loop(2));
        Take(aggregator, Loop(2)); // b is the busier one
        aggregator.Pin(a);
        Assert.Equal(b, Take(aggregator, Loop(1, 2))); // a is merged into b while the publisher works on it
        aggregator.Apply(a, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 77), QaData.T0, 1));
        aggregator.Unpin(a);
        Assert.Equal(77, aggregator.Get(b)!.Publication.Issue);

        QaPruneResult result = aggregator.Prune(QaData.T0.AddHours(500), TimeSpan.FromHours(12), closed: false, dry: false);

        Assert.Equal(0, result.Count);
        Assert.Equal(1, result.Kept);
        Assert.NotNull(aggregator.Get(b));
    }

    [Fact]
    public void Tombstones_AreBounded_AndSurviveARestoreOfTheState()
    {
        var aggregator = new PlayerbotQaAggregator(5000, 5, new byte[16]);
        for (uint quest = 1; quest <= PlayerbotQaAggregator.TombstoneCap + 20; quest++)
        {
            string id = aggregator.Ingest(QaData.Stall(QaData.Bot1, 1, 1, quest), NoQaContentFacts.Instance, QaData.T0)!;
            aggregator.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, (int)quest)));
        }

        aggregator.Prune(QaData.T0.AddHours(1), TimeSpan.FromHours(12), closed: true, dry: false);
        QaAggregatorState state = aggregator.Export();
        Assert.Equal(PlayerbotQaAggregator.TombstoneCap, state.Tombstones!.Count);

        var restored = new PlayerbotQaAggregator(5000, 5, new byte[16]);
        restored.Restore(state, QaData.T0);
        Assert.Equal(PlayerbotQaAggregator.TombstoneCap, restored.Export().Tombstones!.Count);
    }

    [Fact]
    public async Task AutoPruneHours_ForgetsOldFindingsOnThePass_AndZeroKeepsThem()
    {
        foreach (int autoPrune in new[] { 0, 6 })
        {
            string directory = Path.Combine(_directory, "auto" + autoPrune);
            var options = new PlayerbotQaOptions { Directory = directory, StaleHours = 0, AutoPruneHours = autoPrune };
            var time = new ManualTime(QaData.T0);
            var bus = new PlayerbotQaSignalBus(64);
            PlayerbotQaFeature feature = NewFeature(options, time, bus);
            Assert.True(bus.TryWrite(QaData.Relay(7, QaData.T0)));
            feature.Drain();
            time.Advance(TimeSpan.FromHours(5));
            feature.RunPass(CancellationToken.None);
            Assert.Equal(1, feature.Aggregator!.Count); // 5h old: inside both limits
            time.Advance(TimeSpan.FromHours(2));

            feature.RunPass(CancellationToken.None);

            Assert.Equal(autoPrune == 0 ? 1 : 0, feature.Aggregator.Count);
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task AutoPruneHours_AfterARestartWithALongGap_PrunesNothingUntilTheAgeHasPassedInThisProcess()
    {
        string directory = Path.Combine(_directory, "gap");
        var options = new PlayerbotQaOptions { Directory = directory, StaleHours = 0, AutoPruneHours = 6 };
        var bus = new PlayerbotQaSignalBus(64);
        PlayerbotQaFeature early = NewFeature(options, new ManualTime(QaData.T0), bus);
        Assert.True(bus.TryWrite(QaData.Relay(7, QaData.T0)));
        early.Drain();
        await early.StopAsync(); // saves the finding

        var time = new ManualTime(QaData.T0.AddHours(100)); // the server was down for four days
        PlayerbotQaFeature restarted = NewFeature(options, time, new PlayerbotQaSignalBus(64));
        Assert.Equal(1, restarted.Aggregator!.Count);

        restarted.RunPass(CancellationToken.None);
        Assert.Equal(1, restarted.Aggregator.Count);
        time.Advance(TimeSpan.FromHours(5));
        restarted.RunPass(CancellationToken.None);
        Assert.Equal(1, restarted.Aggregator.Count);
        time.Advance(TimeSpan.FromHours(2));
        restarted.RunPass(CancellationToken.None);
        Assert.Equal(0, restarted.Aggregator.Count);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task Top_DrainsTheQueuedSignalsFirst_SoAJustMetFindingIsNotStale()
    {
        await using WorldTestHost host = StartWithQa();
        var bus = host.WorldServices.GetRequiredService<PlayerbotQaSignalBus>();
        Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, 1, 1, 5, at: DateTime.UtcNow)));

        string[] top = await RunAsync(host, ".playerbot qa top");

        Assert.Contains("top 1 of 1 findings", top[0], StringComparison.Ordinal);
        Assert.DoesNotContain(top, l => l.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Top_StillListsAStaleFindingWithAnOpenIssue_ButHidesAStaleClosedOne()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        string open = Seen(feature, 1, 30);
        string closed = Seen(feature, 2, 30);
        Close(feature, open, QaPublicationState.Published);
        Close(feature, closed);

        string[] top = await RunAsync(host, ".playerbot qa top");

        Assert.Contains("top 1 of 1 findings", top[0], StringComparison.Ordinal);
        Assert.Contains(top, l => l.StartsWith("#1 " + PlayerbotQaFingerprint.ShortId(open), StringComparison.Ordinal) && l.Contains("1d ago (stale)", StringComparison.Ordinal));
        Assert.DoesNotContain(top, l => l.Contains(PlayerbotQaFingerprint.ShortId(closed), StringComparison.Ordinal));
        Assert.Contains(top, l => l.StartsWith("1 stale findings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prune_ReportsHowManyOldFindingsWithAnIssueWereKept()
    {
        await using WorldTestHost host = StartWithQa();
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        Close(feature, Seen(feature, 1, 30), QaPublicationState.Published);
        Close(feature, Seen(feature, 2, 30));
        Seen(feature, 3, 30);

        string[] lines = await RunAsync(host, ".playerbot qa prune");

        Assert.Equal("Bot QA: forgot 1 findings not seen for 12h (stall 1); 2 remain. 2 old ones with a GitHub issue are kept. GitHub is untouched.", lines[0]);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(8760, true)]
    [InlineData(8761, false)]
    public void AutoPruneHours_IsValidated(int hours, bool valid)
        => Assert.Equal(valid, new PlayerbotQaOptions { AutoPruneHours = hours }.Validate().Count == 0);
}
