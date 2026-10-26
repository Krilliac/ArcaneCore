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

    [Fact]
    public void TheFeed_CountsEveryOccurrenceAfterAReset_EvenWhenTheCountGrewPastTheOldOne()
    {
        var ledger = new FaultLedger();
        var feed = new PlayerbotQaFaultFeed(ledger);
        ledger.Record("worldtick", One("a"));
        ledger.Record("worldtick", One("a"));
        Assert.Equal(2, feed.Pull().Sum(s => s.Occurrences));

        // Before the ledger had an epoch, three occurrences after a reset (count 3, above the 2 already sent) were sent as one.
        ledger.Reset();
        ledger.Record("worldtick", One("a"));
        ledger.Record("worldtick", One("a"));
        ledger.Record("worldtick", One("a"));
        Assert.Equal(3, feed.Pull().Sum(s => s.Occurrences));
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
        // Literal site names, as production records them (PlayerbotPolicyTelemetry, WorldSession.PacketCapture, ProcessDumpService): a renamed or
        // misspelled entry in EnvironmentSites fails here instead of silently filing nothing.
        string[] environment = ["playerbot.policy-telemetry-io", "net.packet-capture", "dump.free-space"];
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

    [Fact]
    public void TwoSubjectsOfAPerSubjectKey_FileTwoFindings()
    {
        var anomalyLedger = new AnomalyLedger();
        var feed = new PlayerbotQaFaultFeed(new FaultLedger(), null, anomalyLedger);
        Assert.Contains("spell/effect-unhandled", PlayerbotQaFaultFeed.PerSubjectKeys);
        for (int i = 0; i < 3; i++)
        {
            anomalyLedger.Report("spell", "effect-unhandled", "spell 100", "kind=effect value=7", AnomalySeverity.Warning);
            anomalyLedger.Report("spell", "effect-unhandled", "spell 200", "kind=aura value=9", AnomalySeverity.Warning);
        }

        IReadOnlyList<QaSignal> signals = feed.Pull();

        Assert.Equal(2, signals.Select(s => s.FaultId).Distinct().Count());
        Assert.Equal(6, signals.Sum(s => s.Occurrences));
        Assert.Contains(signals, s => s.Diag?.Contains("[spell 100]", StringComparison.Ordinal) == true);
        Assert.Contains(signals, s => s.Diag?.Contains("[spell 200]", StringComparison.Ordinal) == true);
        Assert.All(signals, s => Assert.Equal("Anomaly:spell/effect-unhandled", s.ExceptionType));
        Assert.Empty(feed.Pull());

        anomalyLedger.Report("spell", "effect-unhandled", "spell 100", "kind=effect value=7", AnomalySeverity.Warning);
        QaSignal again = Assert.Single(feed.Pull());
        Assert.Equal(1, again.Occurrences);
        Assert.Equal(signals.First(s => s.Diag?.Contains("[spell 100]", StringComparison.Ordinal) == true).FaultId, again.FaultId);
    }

    [Fact]
    public void AKeyThatIsNotPerSubject_ListsItsTopSubjectsAndTheDistinctCount_InTheDiag()
    {
        var anomalyLedger = new AnomalyLedger();
        var feed = new PlayerbotQaFaultFeed(new FaultLedger(), null, anomalyLedger);
        anomalyLedger.Report("combat", "stale-threat-entry", "creature 1", "a", AnomalySeverity.Warning);
        anomalyLedger.Report("combat", "stale-threat-entry", "creature 2", "b", AnomalySeverity.Warning);
        anomalyLedger.Report("combat", "stale-threat-entry", "creature 2", "c", AnomalySeverity.Warning);
        anomalyLedger.Report("combat", "stale-threat-entry", "creature 3", "d", AnomalySeverity.Warning); // three subjects: it qualifies
        IReadOnlyList<QaSignal> signals = feed.Pull(); // the first occurrence and the rest are two signals with the same text
        Assert.Equal(4, signals.Sum(s => s.Occurrences));
        QaSignal signal = signals[0];

        Assert.EndsWith("| subjects=3: creature 2 x2; creature 1 x1; creature 3 x1", signal.Diag, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerNameInAnomalyDetail_IsRedacted()
    {
        var redactor = new PlayerbotQaRedactor();
        string[] details =
        [
            "player Zorblax (guid 77) still had the loot window open",
            "award to Zorblax (guid 77) refused",
            "player Zorblax failed",
        ];

        foreach (string detail in details)
        {
            Assert.DoesNotContain("Zorblax", redactor.Redact(detail), StringComparison.Ordinal);
            Assert.DoesNotContain("Zorblax", redactor.RedactFault(detail), StringComparison.Ordinal);
        }

        Assert.Equal("player alive 5yd, creature (guid 5)", redactor.RedactFault("player alive 5yd, creature (guid 5)")); // kinds and ordinary words stay

        var anomalyLedger = new AnomalyLedger();
        var feed = new PlayerbotQaFaultFeed(new FaultLedger(), null, anomalyLedger);
        anomalyLedger.Report("loot", "window-left-open", "gameobject 5 spawn 9", "player Zorblax (guid 77) left it open", AnomalySeverity.Error);
        QaSignal signal = Assert.Single(feed.Pull());
        Assert.DoesNotContain("Zorblax", redactor.RedactFault(signal.Diag), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleOccurrence_ServerFault_ActionFault_Quarantine_AndDisabled_PublishImmediately_UnderDefaultThresholds()
    {
        var ledger = new FaultLedger();
        ledger.Record("worldtick", One("single crash"));
        PlayerbotQaAggregator aggregator = Fold(ledger, new PlayerbotQaFaultFeed(ledger));
        aggregator.Ingest(QaData.Fault(QaData.Bot1, "playerbot-recovery-stalled"), new FakeFacts(), QaData.T0);
        aggregator.Ingest(QaData.Parse($"Playerbot {QaData.Bot1} quarantined for 30 s (fault 1 of 5): action: playerbot-recovery-stalled"), new FakeFacts(), QaData.T0);
        aggregator.Ingest(QaData.Parse($"Playerbot {QaData.Bot1} disabled after 5 faults within 600 s: action: playerbot-recovery-stalled"), new FakeFacts(), QaData.T0);

        var github = new FakeGitHub();
        var options = new PlayerbotQaOptions
        {
            GitHub = { Enabled = true, Repository = QaData.Repo, Publish = true, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 10 },
        };
        var time = new ManualTime(QaData.T0);
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, new PlayerbotQaRedactor(), new ListLogger(),
            name => name == TokenVariable ? QaData.FakeToken : null,
            t => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, t, options.GitHub, (_, _) => Task.CompletedTask, null, () => time.GetUtcNow()),
            time, "test-build", null, (_, _) => Task.CompletedTask);

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        Assert.Equal(4, github.Creates);
        Assert.All(aggregator.Snapshot(), f => Assert.Equal(QaPublicationState.Published, f.Publication.State));
    }

    [Fact]
    public void TerminatingCrash_PersistsToJournal_AndReplaysIntoServerFaultOnNextPull()
    {
        string journal = Path.Combine(_directory, "crash-faults.jsonl");
        var crashingProcessLedger = new FaultLedger();
        crashingProcessLedger.ConfigureCrashJournal(journal);
        crashingProcessLedger.Record("unhandled", One("fatal crash"), terminating: true);
        Assert.True(File.Exists(journal));

        var restartedLedger = new FaultLedger();
        restartedLedger.ConfigureCrashJournal(journal);
        var feed = new PlayerbotQaFaultFeed(restartedLedger);

        QaSignal signal = Assert.Single(feed.Pull());
        Assert.Equal(QaSignalKind.ServerFault, signal.Kind);
        Assert.Equal("unhandled", signal.Goal);
        Assert.Equal(1, signal.Occurrences);
        Assert.False(File.Exists(journal));
        Assert.Empty(feed.Pull());
    }

    [Fact]
    public void InvariantFailures_AndServerErrorAnomalies_ArePulledAsServerFaults()
    {
        var ledger = new FaultLedger();
        var anomalyLedger = new AnomalyLedger();
        anomalyLedger.Report("persist", "write-retry-exhausted", "CharacterSave", "busy after 6 attempts", AnomalySeverity.Error);
        anomalyLedger.Report("creature", "under-map", "spawn 1", "z below floor", AnomalySeverity.Error); // spatial observer key is excluded

        var invariants = new List<InvariantFailure>
        {
            // A full caller path in this platform's separators, as [CallerFilePath] gives it: on Linux "D:\src\MoveSpline.cs" is one file name.
            new("Step", Path.GetFullPath(Path.Combine("src", "MoveSpline.cs")), 88, 2, "spline index out of range"),
        };
        var feed = new PlayerbotQaFaultFeed(ledger, () => invariants, anomalyLedger);

        IReadOnlyList<QaSignal> signals = feed.Pull();
        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s.Goal == "invariant" && s.Occurrences == 2 && s.Diag == "Step(MoveSpline.cs:88)");
        Assert.Contains(signals, s => s.Goal == "persist" && s.ExceptionType == "Anomaly:persist/write-retry-exhausted" && s.Occurrences == 1 && s.Diag?.StartsWith("persist/write-retry-exhausted: busy after 6 attempts", StringComparison.Ordinal) == true);
        Assert.Empty(feed.Pull());
    }

    [Fact]
    public void RecurringServerWarningAnomalies_QualifyAtFiveOccurrencesOrThreeDistinctSubjects_AndIncludeDiagDetail()
    {
        var ledger = new FaultLedger();
        var anomalyLedger = new AnomalyLedger();
        var feed = new PlayerbotQaFaultFeed(ledger, null, anomalyLedger);

        // 4 Warning occurrences on 1 subject -> below both thresholds (5 occurrences or 3 subjects).
        for (int i = 0; i < 4; i++)
        {
            anomalyLedger.Report("combat", "stale-threat-entry", "creature 400", "victim offline for 12s", AnomalySeverity.Warning);
        }

        // Info anomalies and bot-* anomalies never qualify in the server fault feed even above thresholds.
        for (int i = 0; i < 10; i++)
        {
            anomalyLedger.Report("logon", "handshake-timeout", $"session {i}", "read timed out", AnomalySeverity.Info);
            anomalyLedger.Report("bot-combat", "evade-loop", $"bot {i}", "evaded", AnomalySeverity.Warning);
        }

        Assert.Empty(feed.Pull());

        // 5th occurrence on the same subject crosses DefaultMinServerWarningCount (5).
        anomalyLedger.Report("combat", "stale-threat-entry", "creature 400", "victim offline for 12s", AnomalySeverity.Warning);
        IReadOnlyList<QaSignal> byCount = feed.Pull();
        Assert.Equal(5, byCount.Sum(s => s.Occurrences));
        Assert.All(byCount, s =>
        {
            Assert.Equal(QaSignalKind.ServerFault, s.Kind);
            Assert.Equal("combat", s.Goal);
            Assert.Equal("Anomaly:combat/stale-threat-entry", s.ExceptionType);
            Assert.Equal("combat/stale-threat-entry", s.Detail);
            Assert.StartsWith("combat/stale-threat-entry: victim offline for 12s | subjects=1: creature 400 x5", s.Diag);
            Assert.Equal(string.Empty, s.Message);
        });

        // 3 distinct subjects at Count == 3 crosses DefaultMinServerWarningSubjects (3).
        anomalyLedger.Report("creature", "evade-loop", "spawn 10", "evaded 6 times", AnomalySeverity.Warning);
        anomalyLedger.Report("creature", "evade-loop", "spawn 11", "evaded 6 times", AnomalySeverity.Warning);
        Assert.Empty(feed.Pull());

        anomalyLedger.Report("creature", "evade-loop", "spawn 12", string.Empty, AnomalySeverity.Warning);
        IReadOnlyList<QaSignal> bySubjects = feed.Pull();
        Assert.Equal(3, bySubjects.Sum(s => s.Occurrences));
        Assert.All(bySubjects, s =>
        {
            Assert.Equal("creature", s.Goal);
            Assert.Equal("Anomaly:creature/evade-loop", s.ExceptionType);
            Assert.StartsWith("creature/evade-loop | subjects=3: ", s.Diag);
        });
    }

    [Fact]
    public void QaPipelineCatchSites_RecordToFaultLedgerAndAnomalyLedger_OnCorruptStoreAndCallbackException()
    {
        File.WriteAllText(Path.Combine(_directory, "findings.json"), "{not-valid-json");
        var store = new PlayerbotQaStore(_directory, new ListLogger());

        using AnomalyCapture storeCapture = AnomalyLedger.Shared.Capture(n => n.Key == "qa/store-corrupt");
        Assert.Null(store.Load());
        Assert.NotNull(store.DisabledReason);
        Assert.Contains(storeCapture.Notices, n => n.Detail == "JsonException");
        Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == "playerbot.qa");

        var aggregator = new PlayerbotQaAggregator(100, 5);
        aggregator.FindingCreated = _ => throw new InvalidOperationException("callback boom");
        using AnomalyCapture callbackCapture = AnomalyLedger.Shared.Capture(n => n.Key == "qa/pass-fault");
        string? id = aggregator.Ingest(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f), new FakeFacts(), QaData.T0);
        Assert.NotNull(id);
        Assert.Contains(callbackCapture.Notices, n => n.Subject == id && n.Detail == "InvalidOperationException");
    }
}
