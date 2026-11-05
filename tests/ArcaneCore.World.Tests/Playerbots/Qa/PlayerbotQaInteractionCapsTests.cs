using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The interaction kinds' own caps (<see cref="QaKindInfo.Cap"/>): a first deploy that meets many unsupported objects, script gaps and unhandled
/// opcodes (bot-independent, one occurrence each) opens a few issues a pass and a day, the day survives a restart, and other kinds are not held back.
/// </summary>
public sealed class PlayerbotQaInteractionCapsTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private (PlayerbotQaPublisher Publisher, QaInteractionTransport Transport, PlayerbotQaAggregator Findings, ManualTime Time) Rig(QaLedgerState? ledger = null)
    {
        var options = new PlayerbotQaOptions
        {
            GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 1000 },
        };
        var transport = new QaInteractionTransport();
        var time = new ManualTime(QaData.T0);
        var aggregator = new PlayerbotQaAggregator(1000, 5, build: "test-build"); // the publisher gets the same build, as in PlayerbotQaFeature
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, new PlayerbotQaRedactor(), new ListLogger(), _ => QaData.FakeToken, _ => transport, time,
            "test-build", null, (_, _) => Task.CompletedTask);
        if (ledger is not null) publisher.Ledger.Restore(ledger);
        return (publisher, transport, aggregator, time);
    }

    private static QaSignal Object(uint entry) => new()
    {
        Kind = QaSignalKind.ObjectUnsupported, AtUtc = QaData.T0, BotKey = QaData.Bot1, GameObject = entry, Code = 26, Reason = "type", Message = "a bot used it",
    };

    private static QaSignal Opcode(int opcode) => new()
    {
        Kind = QaSignalKind.OpcodeUnhandled, AtUtc = QaData.T0, BotKey = QaData.Bot1, Code = opcode, Reason = "no-handler", Message = "a bot sent it",
    };

    [Fact]
    public void TheKindTable_CapsEveryInteractionKind_AndTheBotIndependentOnesHardest()
    {
        foreach (QaSignalKind kind in new[] { QaSignalKind.InteractionFailed, QaSignalKind.QuestBroken, QaSignalKind.ObjectUnsupported, QaSignalKind.ScriptUnsupported, QaSignalKind.OpcodeUnhandled })
        {
            QaKindInfo row = QaKinds.Of(kind);
            QaKindCap cap = Assert.NotNull(row.Cap);
            Assert.InRange(cap.PerPass, 1, row.BotIndependent ? 1 : 2);
            Assert.InRange(cap.PerDay, cap.PerPass, row.BotIndependent ? 4 : 8);
            Assert.False(row.SpellKind);
        }

        Assert.Null(QaKinds.Of(QaSignalKind.Stall).Cap); // the older kinds keep only the hourly cap
    }

    [Fact]
    public async Task AFirstDeployWithManyContentGaps_OpensOneOfEachKindAPass_ThenStopsAtTheDailyCaps()
    {
        var (publisher, transport, findings, time) = Rig();
        for (uint entry = 1; entry <= 30; entry++) findings.Ingest(Object(180000 + entry), new FakeFacts(), QaData.T0);
        for (int opcode = 1; opcode <= 30; opcode++) findings.Ingest(Opcode(opcode), new FakeFacts(), QaData.T0);

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Equal(2, transport.Created.Count); // one object, one opcode

        for (int pass = 0; pass < 10; pass++)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        }

        Assert.Equal(4, transport.Created.Count(c => c.Title.Contains("object unsupported", StringComparison.Ordinal)));
        Assert.Equal(3, transport.Created.Count(c => c.Title.Contains("opcode unhandled", StringComparison.Ordinal)));

        // A new day: the cap frees and one more of each kind opens. The gaps were met by this build, so they may open however long ago
        // (GitHub:CreateWithinHours bounds only findings the running build has not met).
        time.Advance(TimeSpan.FromHours(24));
        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Equal(9, transport.Created.Count); // one more of each
        Assert.Equal(5, transport.Created.Count(c => c.Title.Contains("object unsupported", StringComparison.Ordinal)));
        Assert.Equal(4, transport.Created.Count(c => c.Title.Contains("opcode unhandled", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheDailyKindCap_SurvivesARestart()
    {
        var (first, transport, findings, time) = Rig();
        for (uint entry = 1; entry <= 10; entry++) findings.Ingest(Object(180000 + entry), new FakeFacts(), QaData.T0);
        for (int pass = 0; pass < 6; pass++)
        {
            await first.RunAsync(findings.Snapshot(), CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(4, transport.Created.Count);
        QaLedgerState saved = first.Ledger.Export();
        Assert.Equal(4, Assert.Single(saved.KindCreatesUtc!).Value.Length);

        // A restart: the store file round trip, then a new publisher over the same findings.
        var store = new PlayerbotQaStore(_directory, NullLogger.Instance);
        store.Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String(findings.Salt), Findings = findings.Export().Findings, Ledger = saved });
        QaStoreFile loaded = store.Load()!;
        Assert.Equal(saved.KindCreatesUtc!["ObjectUnsupported"], loaded.Ledger.KindCreatesUtc!["ObjectUnsupported"]);

        var (second, again, restored, _) = Rig(loaded.Ledger);
        restored.Restore(new QaAggregatorState(loaded.Findings.Where(f => f.Publication.State == QaPublicationState.Pending).ToList(), loaded.Bots));
        await second.RunAsync(restored.Snapshot(), CancellationToken.None);
        Assert.Empty(again.Created); // the day's four were already opened before the restart
    }

    [Fact]
    public void ALedgerOfAnOlderFormat_OrWithAnUnknownKind_Restores()
    {
        var ledger = new QaLedger();
        ledger.Restore(new QaLedgerState([], [])); // a file written before the interaction kinds
        Assert.True(ledger.CanCreateKind(QaSignalKind.ObjectUnsupported, QaData.T0, 1));
        ledger.Restore(new QaLedgerState([], [], null, null, null, new Dictionary<string, DateTime[]> { ["SomeFutureKind"] = [QaData.T0], ["OpcodeUnhandled"] = [QaData.T0] }));
        Assert.False(ledger.CanCreateKind(QaSignalKind.OpcodeUnhandled, QaData.T0, 1));
        Assert.Single(ledger.Export().KindCreatesUtc!);
    }

    [Fact]
    public async Task TheKindCaps_DoNotHoldBackOtherKinds()
    {
        var (publisher, transport, findings, _) = Rig();
        for (uint entry = 1; entry <= 5; entry++) findings.Ingest(Object(180000 + entry), new FakeFacts(), QaData.T0);
        for (int i = 0; i < 3; i++)
        {
            findings.Ingest(QaData.Relay(7), new FakeFacts(), QaData.T0);
        }

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Equal(2, transport.Created.Count); // one object (its cap) and the relay finding (no cap of its own)
    }
}
