using System.Text.Json;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Signals fold into findings by a fingerprint that holds content ids only (never a bot name or id).</summary>
public sealed class PlayerbotQaAggregatorTests
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static PlayerbotQaAggregator New(int maxFindings = 100, int maxSamples = 5) => new(maxFindings, maxSamples, Salt);

    private static string Ingest(PlayerbotQaAggregator aggregator, QaSignal signal, DateTime? now = null)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, now ?? QaData.T0) ?? throw new InvalidOperationException("refused");

    [Fact]
    public void SameStallFromThreeBots_IsOneFinding_ThreeDistinctBots()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));
        string b = Ingest(aggregator, QaData.Stall(QaData.Bot2, -5620.0f, -441.0f));
        string c = Ingest(aggregator, QaData.Stall(QaData.Bot3, -5601.0f, -420.0f));
        string again = Ingest(aggregator, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a, again);
        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(4, f.Count);
        Assert.Equal(3, f.DistinctBots); // the first bot twice counts once
    }

    [Fact]
    public void Fingerprint_ExcludesBotNamesAndGuids()
    {
        QaSignal one = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", "stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0"));
        QaSignal two = QaData.Parse(QaData.BotLine(QaData.Bot2, "Graveweaver", "stalled 300s: goal=Quest target=6747 quest=1656 at -5610.0,-440.0,399.0 map 0"));

        Assert.Equal(PlayerbotQaFingerprint.Of(one), PlayerbotQaFingerprint.Of(two));
        string key = PlayerbotQaFingerprint.Key(one);
        foreach (string forbidden in new[] { "Ironwander", "Graveweaver", QaData.Bot1.ToString(), QaData.Bot2.ToString() })
        {
            Assert.DoesNotContain(forbidden, key, StringComparison.OrdinalIgnoreCase);
        }

        // Nothing of the bot reaches the stored state either: the persisted form has salted hashes and redacted samples only.
        PlayerbotQaAggregator aggregator = New();
        Ingest(aggregator, one);
        Ingest(aggregator, two);
        QaAggregatorState state = aggregator.Export();
        string json = JsonSerializer.Serialize(state.Findings.Select(f => new { f.Id, f.Key, f.Subject, f.Samples, f.CauseDetail }))
            + JsonSerializer.Serialize(state.Bots);
        foreach (string forbidden in new[] { "Ironwander", "Graveweaver", QaData.Bot1.ToString(), QaData.Bot2.ToString() })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fingerprint_IsStable_AcrossProcessesAndPositionsInOneCell()
    {
        // Key: v1|stall|goal=Quest|quest=1656|target=6747|map=0|cell=-113,-9 (50-yard cells, floor division: x in [-5650,-5600), y in [-450,-400)).
        // The expected id is the first 16 hex characters of SHA-256 over that key, computed outside the code under test (sha256sum).
        const string expectedKey = "v1|stall|goal=Quest|quest=1656|target=6747|map=0|cell=-113,-9";
        const string expectedId = "428d6d853e162fd3";

        foreach ((float x, float y) in new[] { (-5608.8f, -439.4f), (-5649.9f, -450.0f), (-5600.1f, -400.1f) })
        {
            QaSignal s = QaData.Stall(QaData.Bot1, x, y);
            Assert.Equal(expectedKey, PlayerbotQaFingerprint.Key(s));
            Assert.Equal(expectedId, PlayerbotQaFingerprint.Of(s));
        }

        // A death loop in cell (-113, 3) of map 0, again against an independently computed SHA-256.
        QaSignal loop = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", "death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers=16422"));
        Assert.Equal("v1|deathloop|map=0|cell=-113,3", PlayerbotQaFingerprint.Key(loop));
        Assert.Equal("ce89ef1806a62e2d", PlayerbotQaFingerprint.Of(loop));
        Assert.Equal("ce89ef18", PlayerbotQaFingerprint.ShortId("ce89ef1806a62e2d"));
    }

    [Fact]
    public void Fingerprint_DiffersAcrossCellsMapsQuestsKinds()
    {
        string[] ids =
        [
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -439.4f)),
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5599.9f, -439.4f)), // next cell in x (-112)
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -399.9f)), // next cell in y (-8)
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -439.4f, map: 1)),
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -439.4f, quest: 1657)),
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -439.4f, target: 6748)),
            PlayerbotQaFingerprint.Of(QaData.Stall(null, -5608.8f, -439.4f, goal: "Grind")),
            PlayerbotQaFingerprint.Of(new QaSignal { Kind = QaSignalKind.DeathLoop, HasPosition = true, X = -5608.8f, Y = -439.4f }),
            PlayerbotQaFingerprint.Of(QaData.Relay(4021)),
            PlayerbotQaFingerprint.Of(QaData.Relay(4022)),
        ];
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{16}$", id));
    }

    [Fact]
    public void DeathLoopAttackerOrder_DoesNotChangeFingerprint()
    {
        QaSignal a = QaData.Parse(QaData.BotLine(QaData.Bot1, "Dawnrover", "death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers=2000/16422"));
        QaSignal b = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", "death loop: died 4 times in 120s within 60 yd of -5605.6,155.8,453.3 map 0 attackers=16422/2000"));
        Assert.Equal(PlayerbotQaFingerprint.Of(a), PlayerbotQaFingerprint.Of(b));
        Assert.Equal(a.Attackers, b.Attackers);

        PlayerbotQaAggregator aggregator = New();
        Ingest(aggregator, a);
        Ingest(aggregator, b);
        Assert.Single(aggregator.Snapshot());
    }

    [Fact]
    public void FaultMessageNumbersAndGuids_AreNormalisedOutOfFingerprint()
    {
        string guidA = "11111111-2222-3333-4444-555555555555";
        string guidB = "66666666-7777-8888-9999-000000000000";
        QaSignal a = QaData.Fault(QaData.Bot1, $"playerbot-recovery-stalled: bot {guidA} after 17 tries at 0x7FF61234");
        QaSignal b = QaData.Fault(QaData.Bot2, $"playerbot-recovery-stalled: bot {guidB} after 203 tries at 0x7FF69999");
        Assert.Equal(PlayerbotQaFingerprint.Of(a), PlayerbotQaFingerprint.Of(b));

        QaSignal different = QaData.Fault(QaData.Bot2, $"playerbot-recovery-stalled: ghost {guidB} after 203 tries at 0x7FF69999");
        Assert.NotEqual(PlayerbotQaFingerprint.Of(a), PlayerbotQaFingerprint.Of(different));
        QaSignal otherType = QaData.Fault(QaData.Bot2, a.Detail, "NullReferenceException");
        Assert.NotEqual(PlayerbotQaFingerprint.Of(a), PlayerbotQaFingerprint.Of(otherType));

        Assert.Equal(PlayerbotQaFingerprint.Normalise("A  B\t17 x"), PlayerbotQaFingerprint.Normalise("a b 3 X"));
        Assert.True(PlayerbotQaFingerprint.Normalise(new string('z', 1000)).Length <= 160);
    }

    [Fact]
    public void FirstLastSeenCountsAndSamples_AreTracked_SamplesBounded()
    {
        PlayerbotQaAggregator aggregator = New(maxSamples: 3);
        DateTime t = QaData.T0;
        // In arrival order: t+5, t+1 (older, late), t+9, t+3, t+7.
        foreach (int minute in new[] { 5, 1, 9, 3, 7 })
        {
            QaSignal s = QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, at: t.AddMinutes(minute)) with { Message = "sample at minute " + minute };
            Ingest(aggregator, s, t.AddMinutes(20));
        }

        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(5, f.Count);
        Assert.Equal(t.AddMinutes(1), f.FirstSeenUtc);
        Assert.Equal(t.AddMinutes(9), f.LastSeenUtc);
        Assert.Equal(3, f.Samples.Count);
        Assert.Equal(new[] { "sample at minute 9", "sample at minute 3", "sample at minute 7" }, f.Samples.Select(s => s.Message).ToArray()); // the newest three arrivals
        Assert.Equal(1656u, f.Quest);
        Assert.Equal(6747u, f.Target);
        Assert.Equal(0u, f.Map);
        Assert.Contains("quest 1656", f.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxFindings_EvictsLowestSeverityOldest()
    {
        PlayerbotQaAggregator aggregator = New(maxFindings: 3);
        DateTime now = QaData.T0.AddMinutes(2);
        string quarantine = Ingest(aggregator, QaData.Parse($"Playerbot {QaData.Bot1} quarantined for 30 s (fault 1 of 5): action: playerbot-recovery-stalled", null, QaData.T0), now);
        string oldStall = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 1, at: QaData.T0), now);
        string newStall = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 2, at: QaData.T0.AddMinutes(1)), now);
        Assert.Equal(3, aggregator.Count);

        string newcomer = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 3, goal: "Grind", at: QaData.T0.AddMinutes(2)), now);

        string[] ids = [.. aggregator.Snapshot().Select(f => f.Id)];
        Assert.Equal(3, ids.Length);
        Assert.DoesNotContain(oldStall, ids); // equal severity, so the older one went
        Assert.Contains(quarantine, ids);
        Assert.Contains(newStall, ids);
        Assert.Contains(newcomer, ids);
    }

    [Fact]
    public void MaxFindings_NeverEvictsPublishedOrKnownClosed_AndRefusesWhenNothingCanGo()
    {
        PlayerbotQaAggregator aggregator = New(maxFindings: 2);
        string first = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 1));
        string second = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 2));
        aggregator.Apply(first, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 10)));
        aggregator.Apply(second, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, 11)));

        string? refused = aggregator.Ingest(QaData.Stall(QaData.Bot1, 0, 0, quest: 3), NoQaContentFacts.Instance, QaData.T0);

        Assert.Null(refused);
        Assert.Equal(1, aggregator.Rejected);
        Assert.Equal(2, aggregator.Count);
        // An existing pinned finding still counts new occurrences.
        Ingest(aggregator, QaData.Stall(QaData.Bot2, 0, 0, quest: 1));
        Assert.Equal(2, aggregator.Get(first)!.Count);
    }

    [Fact]
    public void Reset_OneOrAll()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 1));
        Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 2));
        Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 3));

        Assert.Equal(0, aggregator.Reset("0000000000000000"));
        Assert.Equal(0, aggregator.Reset(a[..8])); // exact id only: a prefix never forgets anything
        Assert.Equal(3, aggregator.Count);
        Assert.Equal(1, aggregator.Reset(a));
        Assert.Null(aggregator.Get(a));
        Assert.Equal(2, aggregator.Snapshot().Count);

        Assert.Equal(2, aggregator.Reset(null));
        Assert.Empty(aggregator.Snapshot());
        Assert.Equal(0, aggregator.Count);

        // The finding comes back as a new one after a reset.
        Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, quest: 1));
        Assert.Equal(1, Assert.Single(aggregator.Snapshot()).Count);
    }

    [Fact]
    public void DistinctBots_SaturatesAtTheTrackedLimit()
    {
        PlayerbotQaAggregator aggregator = New();
        for (int i = 0; i < QaFinding.MaxTrackedBots + 88; i++) Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0));
        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaFinding.MaxTrackedBots + 88, f.Count);
        Assert.Equal(QaFinding.MaxTrackedBots, f.DistinctBots);
    }

    [Fact]
    public void ExportRestore_KeepsFindingsAndBotCounts_WithTheSameSalt()
    {
        PlayerbotQaAggregator before = New();
        Ingest(before, QaData.Stall(QaData.Bot1, 0, 0));
        Ingest(before, QaData.Stall(QaData.Bot2, 0, 0));
        QaAggregatorState state = before.Export();

        PlayerbotQaAggregator after = New();
        Assert.Equal(0, after.Restore(state));
        Ingest(after, QaData.Stall(QaData.Bot1, 0, 0)); // a bot met before: not a new distinct bot
        QaFindingView restored = Assert.Single(after.Snapshot());
        Assert.Equal(3, restored.Count);
        Assert.Equal(2, restored.DistinctBots);
        Ingest(after, QaData.Stall(QaData.Bot3, 0, 0));
        Assert.Equal(3, Assert.Single(after.Snapshot()).DistinctBots);
    }

    [Fact]
    public void Restore_DropsFindingWhoseIdIsNotTheHashOfItsKey()
    {
        PlayerbotQaAggregator source = New();
        string good = Ingest(source, QaData.Stall(QaData.Bot1, 0, 0, quest: 1));
        Ingest(source, QaData.Stall(QaData.Bot1, 0, 0, quest: 2));
        QaAggregatorState state = source.Export();
        QaFindingView planted = state.Findings.First(f => f.Id != good) with { Id = "deadbeefdeadbeef" };
        var tampered = new QaAggregatorState([state.Findings.First(f => f.Id == good), planted], state.Bots);

        PlayerbotQaAggregator target = New();
        Assert.Equal(1, target.Restore(tampered));
        Assert.Equal(good, Assert.Single(target.Snapshot()).Id);
    }

    [Fact]
    public void SharedSetAside_CountsItsOwnBotsWithoutBotIds()
    {
        PlayerbotQaAggregator aggregator = New();
        Ingest(aggregator, QaData.Parse("Playerbots: 3 bots stalled on quest 1656 for 30 min; every bot sets it aside"));
        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaSignalKind.SharedSetAside, f.Kind);
        Assert.Equal(3, f.DistinctBots);
        Assert.Contains("set aside for every bot after 3 stalled", f.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentIngest_KeepsExactCounts()
    {
        PlayerbotQaAggregator aggregator = New();
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 250; i++) aggregator.Ingest(QaData.Stall(Guid.NewGuid(), 0, 0, quest: (uint)(i % 5) + 1), NoQaContentFacts.Instance, QaData.T0);
        });
        IReadOnlyList<QaFindingView> all = aggregator.Snapshot();
        Assert.Equal(5, all.Count);
        Assert.Equal(2000, all.Sum(f => f.Count));
    }
}
