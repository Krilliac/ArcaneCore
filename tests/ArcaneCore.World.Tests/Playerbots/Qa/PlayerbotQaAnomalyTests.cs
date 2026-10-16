using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

public sealed class PlayerbotQaAnomalyTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    [Fact]
    public void AnomalyFeed_EmitsWhenCountReachesFive_OrDistinctSubjectsReachesThree_AndTracksDeltas()
    {
        var ledger = new AnomalyLedger();
        var feed = new PlayerbotQaAnomalyFeed(ledger);

        // 4 occurrences on 1 bot subject -> below both thresholds (count < 5 && distinctSubjects < 3).
        for (int i = 0; i < 4; i++)
        {
            ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 1)", "waited 60s", AnomalySeverity.Warning);
        }

        Assert.Empty(feed.Pull());

        // 5th occurrence crosses count >= 5 -> emits all 5 occurrences.
        ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 1)", "waited 60s", AnomalySeverity.Warning);
        QaSignal first = Assert.Single(feed.Pull());
        Assert.Equal(QaSignalKind.Anomaly, first.Kind);
        Assert.Equal(5, first.Occurrences);
        Assert.Equal(1, first.BotCount);
        Assert.Equal("bot-elevator", first.Goal);
        Assert.Equal("board-missed", first.Reason);
        Assert.Equal("bot-elevator/board-missed", first.FaultCode);

        // Subsequent pull with no new occurrences emits nothing.
        Assert.Empty(feed.Pull());

        // 2 more occurrences emit only the delta (+2).
        ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 1)", "waited 60s", AnomalySeverity.Warning);
        ledger.Report("bot-elevator", "board-missed", "bot Cairne (guid 2)", "waited 60s", AnomalySeverity.Warning);
        QaSignal delta = Assert.Single(feed.Pull());
        Assert.Equal(2, delta.Occurrences);
        Assert.Equal(2, delta.BotCount);

        // Separate key with 3 distinct subjects and count == 3 qualifies via distinctSubjects >= 3.
        ledger.Report("bot-quest", "giver-missing", "bot A (guid 10)", "npc 100 missing", AnomalySeverity.Warning);
        ledger.Report("bot-quest", "giver-missing", "bot B (guid 11)", "npc 100 missing", AnomalySeverity.Warning);
        Assert.Empty(feed.Pull());

        ledger.Report("bot-quest", "giver-missing", "bot C (guid 12)", "npc 100 missing", AnomalySeverity.Warning);
        QaSignal bySubjects = Assert.Single(feed.Pull());
        Assert.Equal("bot-quest", bySubjects.Goal);
        Assert.Equal("giver-missing", bySubjects.Reason);
        Assert.Equal(3, bySubjects.Occurrences);
        Assert.Equal(3, bySubjects.BotCount);
    }

    [Fact]
    public void AnomalyFeed_IgnoresNonBotSubsystems_InfoSeverity_BelowThreshold_AndOverflow()
    {
        var ledger = new AnomalyLedger(new AnomalyLedgerLimits(MaxKeys: 2), TimeProvider.System);
        var feed = new PlayerbotQaAnomalyFeed(ledger);

        // Non-bot subsystem with 10 Error occurrences -> ignored by bot QA feed.
        for (int i = 0; i < 10; i++)
        {
            ledger.Report("movement", "nan-pos", $"unit {i}", "nan", AnomalySeverity.Error);
        }

        // bot-* subsystem with Info severity and 10 occurrences across 5 bots -> ignored because severity < Warning.
        for (int i = 0; i < 10; i++)
        {
            ledger.Report("bot-travel", "repath", $"bot {i % 5}", "repath", AnomalySeverity.Info);
        }

        // 3rd key overflows MaxKeys=2 into overflow/overflow -> ignored.
        for (int i = 0; i < 10; i++)
        {
            ledger.Report("bot-combat", "overflowed", $"bot {i}", "overflow", AnomalySeverity.Error);
        }

        Assert.Empty(feed.Pull());
    }

    [Fact]
    public async Task QaFeature_FoldsQualifyingBotAnomaliesIntoFindings_OnRunPass()
    {
        var ledger = new AnomalyLedger();
        for (int i = 0; i < 5; i++)
        {
            ledger.Report("bot-dungeon", "wipe-recovery-stuck", $"bot Member{i} (guid {i + 1:X})", "ghost stuck at portal", AnomalySeverity.Warning);
        }

        var options = new PlayerbotQaOptions { Directory = _directory };
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(64))
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services) { AnomalySource = ledger };
        feature.Attach(null!);
        try
        {
            feature.RunPass(CancellationToken.None);

            QaFindingView finding = Assert.Single(feature.Aggregator!.Snapshot());
            Assert.Equal(QaSignalKind.Anomaly, finding.Kind);
            Assert.Equal(5, finding.Count);
            Assert.Equal(5, finding.DistinctBots);
            Assert.Equal("bot-dungeon", finding.Goal);
            Assert.Equal("wipe-recovery-stuck", finding.Reason);
            Assert.Equal(QaCause.BehaviourBug, finding.Cause);
            Assert.Contains("bot-dungeon/wipe-recovery-stuck", finding.Subject, StringComparison.Ordinal);
        }
        finally
        {
            await feature.StopAsync();
        }
    }

    [Fact]
    public void PlayerbotAnomalyHistory_RetainsBoundedPerBotRing_EvictsLruBot_AndFormatsCommandOutput()
    {
        var clock = new TestClock(new DateTimeOffset(QaData.T0, TimeSpan.Zero));
        var ledger = new AnomalyLedger(new AnomalyLedgerLimits(), clock);
        using var history = new PlayerbotAnomalyHistory(ledger, clock, maxNoticesPerBot: 3, maxBots: 2);

        // Non-bot subsystem and empty subject are ignored.
        ledger.Report("world-db", "dropped-row", "table characters", "timeout", AnomalySeverity.Error);
        ledger.Report("bot-elevator", "board-missed", string.Empty, "no subject", AnomalySeverity.Warning);
        Assert.Equal(0, history.TrackedSubjectCount);

        // Record 4 notices for Thrall -> ring capacity 3 keeps the newest 3.
        for (int i = 1; i <= 4; i++)
        {
            ledger.Report("bot-elevator", $"step-{i}", "bot Thrall (guid 10)", $"detail {i}", AnomalySeverity.Warning);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        IReadOnlyList<PlayerbotAnomalyRecord> thrall = history.ForBot("Thrall");
        Assert.Equal(3, thrall.Count);
        Assert.Equal(["bot-elevator/step-2", "bot-elevator/step-3", "bot-elevator/step-4"], thrall.Select(r => r.Key));

        // Add Jaina (2nd bot), touch Thrall so Jaina becomes LRU, then add Sylvanas (3rd bot -> evicts Jaina).
        ledger.Report("bot-quest", "no-giver", "bot Jaina (guid 20)", "quest 100", AnomalySeverity.Warning);
        ledger.Report("bot-elevator", "step-5", "bot Thrall (guid 10)", "detail 5", AnomalySeverity.Error);
        ledger.Report("bot-combat", "evade", "bot Sylvanas (guid 30)", "target evaded", AnomalySeverity.Warning);

        Assert.Equal(2, history.TrackedSubjectCount);
        Assert.Empty(history.ForBot("Jaina"));
        Assert.NotEmpty(history.ForBot("Thrall"));
        Assert.Single(history.ForBot("Sylvanas"));

        // .bot anomalies command formatting
        var lines = new List<string>();
        Assert.True(PlayerbotAnomalyHistory.RunCommand("Thrall", null, history, clock.GetUtcNow(), lines.Add));
        Assert.Equal(4, lines.Count);
        Assert.Contains("Bot anomalies for bot Thrall (guid 10) (3 recent):", lines[0], StringComparison.Ordinal);
        Assert.Contains("bot-elevator/step-5 (Error, #1): detail 5", lines[3], StringComparison.Ordinal);

        // Unknown bot negative case
        var none = new List<string>();
        Assert.True(PlayerbotAnomalyHistory.RunCommand("Nobody", null, history, clock.GetUtcNow(), none.Add));
        Assert.Equal("No recent anomalies recorded for bot 'Nobody'.", Assert.Single(none));

        // Syntax error on empty or multi-word arguments
        Assert.False(PlayerbotAnomalyHistory.RunCommand(string.Empty, null, history, clock.GetUtcNow(), _ => { }));
        Assert.False(PlayerbotAnomalyHistory.RunCommand("two words", null, history, clock.GetUtcNow(), _ => { }));
    }

    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
