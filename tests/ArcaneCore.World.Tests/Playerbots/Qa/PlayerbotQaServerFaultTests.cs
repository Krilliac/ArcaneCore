using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Server faults from the fault ledger become QA findings, are issued without thresholds on bots, and leave redacted.</summary>
public sealed class PlayerbotQaServerFaultTests : IDisposable
{
    private const string TokenVariable = "ARCANECORE_GITHUB_TOKEN";
    private const string Dirty = "player Zorblax failed for 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e at 203.0.113.9 file C:\\Users\\Nathan\\data\\x.db and /var/lib/arcane/secret.db "
        + "guid 4294967296 0x0000000000001234 token=hunter2hunter2 " + QaData.FakeToken + " mail a@b.example";

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception One(string message)
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Two(string message)
    {
        try { throw new ArgumentException(message); } catch (Exception ex) { return ex; }
    }

    private static PlayerbotQaAggregator Fold(FaultLedger ledger, PlayerbotQaFaultFeed feed)
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);
        foreach (QaSignal signal in feed.Pull()) aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        return aggregator;
    }

    [Fact]
    public void TheFeed_SendsOccurrencesOnce_SkipsBotAndOverflow_AndFollowsAReset()
    {
        var ledger = new FaultLedger(new FaultLedgerLimits(MaxSignatures: 1), TimeProvider.System);
        var feed = new PlayerbotQaFaultFeed(ledger);
        ledger.Record("worldtick", One("a"));
        ledger.Record("worldtick", One("a"));
        ledger.Record("worldtick", One("a"));
        ledger.Record("playerbot", Two("b")); // overflow bucket here (cap 1), and a bot site anyway

        IReadOnlyList<QaSignal> first = feed.Pull();
        Assert.Equal(3, first.Sum(s => s.Occurrences));
        Assert.All(first, s => Assert.Equal(QaSignalKind.ServerFault, s.Kind));
        Assert.Empty(feed.Pull());

        ledger.Record("worldtick", One("a"));
        Assert.Equal(1, Assert.Single(feed.Pull()).Occurrences);

        ledger.Reset();
        ledger.Record("worldtick", One("a"));
        Assert.Equal(1, feed.Pull().Sum(s => s.Occurrences));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Socket()
    {
        try { throw new System.Net.Sockets.SocketException(); } catch (Exception ex) { return ex; }
    }

    [Fact]
    public void TheFeed_SkipsTheBotSite_AndConnectionNoise_WithTheDefaultCap()
    {
        var ledger = new FaultLedger();
        ledger.Record("playerbot", One("bot"));
        ledger.Record("session", Socket());
        ledger.Record("worldtick", Two("real"));
        Assert.Equal(3, ledger.Distinct); // all three hold a real slot, none is in the overflow bucket

        IReadOnlyList<QaSignal> signals = new PlayerbotQaFaultFeed(ledger).Pull();

        Assert.Equal("worldtick", Assert.Single(signals).Goal);
    }

    [Fact]
    public void TheFeed_SkipsEnvironmentSites_ButKeepsThemInTheLedger()
    {
        // Literal site names, as production records them (PlayerbotLocalPlanner, WorldSession.PacketCapture, ProcessDumpService): a renamed or
        // misspelled entry in EnvironmentSites fails here instead of silently filing nothing.
        string[] environment = ["playerbot.local-planner", "net.packet-capture", "dump.free-space"];
        Assert.Equal(environment.Order(), PlayerbotQaFaultFeed.EnvironmentSites.Order());
        var ledger = new FaultLedger();
        foreach (string site in environment)
        {
            ledger.Record(site, One(site));
        }

        ledger.Record("playerbot.party-zone-names", Two("party")); // its catch sees code defects (NRE, missing key): it is filed
        ledger.Record("worldtick", Two("real"));
        Assert.Equal(environment.Length + 2, ledger.Distinct);

        IReadOnlyList<QaSignal> signals = new PlayerbotQaFaultFeed(ledger).Pull();

        Assert.Equal(["playerbot.party-zone-names", "worldtick"], signals.Select(s => s.Goal).Order());
    }

    [Fact]
    public async Task TheFeature_FoldsTheLedgerIntoAServerFaultFinding_OnAPass()
    {
        var ledger = new FaultLedger();
        for (int i = 0; i < 3; i++) ledger.Record("worldtick", One("a"));
        var options = new PlayerbotQaOptions { Directory = _directory };
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(64))
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services) { FaultSource = ledger };
        feature.Attach(null!);
        try
        {
            feature.RunPass(CancellationToken.None);

            QaFindingView view = Assert.Single(feature.Aggregator!.Snapshot());
            Assert.Equal(QaSignalKind.ServerFault, view.Kind);
            Assert.Equal(3, view.Count);
        }
        finally
        {
            await feature.StopAsync();
        }
    }

    [Fact]
    public void AServerFault_BecomesOneFinding_WithTheOccurrenceCount_AndNoBotThreshold()
    {
        var ledger = new FaultLedger();
        for (int i = 0; i < 5; i++) ledger.Record("worldtick", One("a"));
        PlayerbotQaAggregator aggregator = Fold(ledger, new PlayerbotQaFaultFeed(ledger));

        QaFindingView view = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaSignalKind.ServerFault, view.Kind);
        Assert.Equal(5, view.Count);
        Assert.True(view.BotIndependent);
        Assert.Equal("worldtick", view.Goal);
        Assert.Equal(typeof(InvalidOperationException).FullName, view.Reason);
        Assert.Equal(ledger.Snapshot()[0].Id, PlayerbotQaIssueComposer.LedgerId(view));
        Assert.Equal(QaCause.ServerFault, view.Cause);
    }

    [Fact]
    public async Task TheIssue_IsRedacted_AndPublishedWithOneOccurrenceThresholdAndNoDistinctBots()
    {
        var ledger = new FaultLedger();
        for (int i = 0; i < 3; i++) ledger.Record("worldtick", One(Dirty));
        PlayerbotQaAggregator aggregator = Fold(ledger, new PlayerbotQaFaultFeed(ledger));
        var github = new FakeGitHub();
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2 } };
        var time = new ManualTime(QaData.T0);
        var redactor = new PlayerbotQaRedactor();
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, redactor, new ListLogger(),
            name => name == TokenVariable ? QaData.FakeToken : null,
            t => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, t, options.GitHub, (_, _) => Task.CompletedTask, null, () => time.GetUtcNow()),
            time, "test-build", null, (_, _) => Task.CompletedTask);

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.Equal(1, github.Creates);
        string body = (string)JsonNode.Parse(github.Http.Requests.Single(r => r.Method == HttpMethod.Post && r.PathAndQuery.Split('?')[0].EndsWith("/issues", StringComparison.Ordinal)).Body)!["body"]!;
        Assert.Contains("worldtick", body, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", body, StringComparison.Ordinal);
        Assert.Contains("| Occurrences | 3 |", body, StringComparison.Ordinal);
        Assert.Contains(nameof(One), body, StringComparison.Ordinal);
        Assert.DoesNotContain("failed for", body, StringComparison.Ordinal); // the exception message is never published
        Assert.DoesNotContain("Zorblax", body, StringComparison.Ordinal);
        string title = (string)JsonNode.Parse(github.Http.Requests.Single(r => r.Method == HttpMethod.Post && r.PathAndQuery.Split('?')[0].EndsWith("/issues", StringComparison.Ordinal)).Body)!["title"]!;
        Assert.DoesNotContain("Zorblax", title, StringComparison.Ordinal);
        foreach (string leak in new[] { "3f2b1c9e", "203.0.113.9", "Nathan", "/var/lib", "x.db", "4294967296", "0x0000000000001234", "hunter2", QaData.FakeToken, "a@b.example" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerFaultIssues_RespectTheHourlyCap()
    {
        var ledger = new FaultLedger();
        for (int i = 0; i < 3; i++)
        {
            ledger.Record("worldtick", One("a"));
            ledger.Record("worldtick", Two("b"));
        }

        PlayerbotQaAggregator aggregator = Fold(ledger, new PlayerbotQaFaultFeed(ledger));
        var github = new FakeGitHub();
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MaxIssuesPerHour = 1 } };
        var time = new ManualTime(QaData.T0);
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, new PlayerbotQaRedactor(), new ListLogger(),
            name => name == TokenVariable ? QaData.FakeToken : null,
            t => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, t, options.GitHub, (_, _) => Task.CompletedTask, null, () => time.GetUtcNow()),
            time, "test-build", null, (_, _) => Task.CompletedTask);

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.Equal(1, github.Creates);
        Assert.Equal(1, aggregator.Snapshot().Count(f => f.Publication.State == QaPublicationState.Pending));
    }

    [Fact]
    public void RedactFault_RemovesPathsAndIds_ButKeepsOrdinaryText()
    {
        string clean = new PlayerbotQaRedactor().RedactFault(Dirty);
        Assert.DoesNotContain("Nathan", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib", clean, StringComparison.Ordinal);
        Assert.Contains("failed for", clean, StringComparison.Ordinal); // RedactFault still keeps ordinary text; the composer simply never calls it on a message
        Assert.Equal("see http://example.org/a and a/b", new PlayerbotQaRedactor().RedactFault("see http://example.org/a and a/b"));
    }
}
