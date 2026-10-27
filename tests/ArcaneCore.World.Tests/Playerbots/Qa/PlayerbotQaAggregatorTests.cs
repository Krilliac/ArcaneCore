using ArcaneCore.Kernel.Diagnostics;
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

    private static QaSignal WorldAnomaly(string subsystem, string code, string detail) => new()
    {
        Kind = QaSignalKind.Anomaly, AtUtc = QaData.T0, Goal = subsystem, Reason = code, FaultCode = AnomalyLedger.KeyOf(subsystem, code), Detail = detail,
        Message = detail, Occurrences = 1,
    };

    /// <summary>
    /// A content defect and a behaviour defect of the same chest family arrive under different ledger keys, so each finding keeps one cause
    /// whichever signal came last (the cause used to follow the last occurrence's detail text).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoDataAndGuaranteedMissingChestSignals_KeepAStableCausePerFinding_InEitherOrder(bool guaranteedFirst)
    {
        QaSignal noData = WorldAnomaly(ArcaneCore.Game.Loot.LootService.AnomalySubsystem, ArcaneCore.Game.Loot.LootService.GameObjectLootNoDataCode, "gameobject entry 1 loot 2: reason no-rows, row -");
        QaSignal missing = WorldAnomaly(ArcaneCore.Game.Loot.LootService.AnomalySubsystem, ArcaneCore.Game.Loot.LootService.GameObjectLootEmptyCode, "gameobject entry 3 loot 4: reason guaranteed-missing, row item 5");
        PlayerbotQaAggregator aggregator = New();

        foreach (QaSignal signal in guaranteedFirst ? new[] { missing, noData, missing, noData } : new[] { noData, missing, noData, missing })
        {
            Ingest(aggregator, signal);
        }

        IReadOnlyList<QaFindingView> findings = aggregator.Snapshot();
        Assert.Equal(2, findings.Count);
        Assert.Equal(QaCause.MissingData, Assert.Single(findings, f => f.Key.Contains(ArcaneCore.Game.Loot.LootService.GameObjectLootNoDataCode, StringComparison.Ordinal)).Cause);
        Assert.Equal(QaCause.BehaviourBug, Assert.Single(findings, f => f.Key.Contains(ArcaneCore.Game.Loot.LootService.GameObjectLootEmptyCode, StringComparison.Ordinal)).Cause);
    }

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

    private static QaSignal Loop(float x, float y, uint map = 1, params uint[] attackers)
        => new()
        {
            Kind = QaSignalKind.DeathLoop, AtUtc = QaData.T0, BotKey = Guid.NewGuid(), HasPosition = true, Map = map, X = x, Y = y, Z = 50f, Attackers = attackers,
            Message = "Playerbot <bot> death loop",
        };

    [Fact]
    public void DeathLoops_DriftingWithinTheAreaAreOneFinding_AndAnotherZoneOrOtherAttackersAreNew()
    {
        PlayerbotQaAggregator aggregator = New();
        string first = Ingest(aggregator, Loop(2036f, -3378f, 1, 3821));
        string drifted = Ingest(aggregator, Loop(2155f, -3386f, 1, 3821)); // 119 yd from the anchor, another 50-yd cell, the same zone cell
        Assert.Equal(first, drifted);
        Assert.Equal(2, Assert.Single(aggregator.Snapshot()).Count);

        Assert.Equal(first, Ingest(aggregator, Loop(2275f, -3010f, 1, 3782, 3821))); // 440 yd from the anchor, still the same 500-yd zone cell and the same attacker
        string far = Ingest(aggregator, Loop(4275f, -3010f, 1, 3782, 3821)); // two zone cells away
        Assert.NotEqual(first, far);
        Assert.Equal(2, aggregator.Snapshot().Count);

        string otherAttacker = Ingest(aggregator, Loop(2036f, -3378f, 1, 2829)); // the anchor's own spot, but nobody in common
        Assert.NotEqual(first, otherAttacker);
        Assert.NotEqual(far, otherAttacker);
        Assert.Equal(3, aggregator.Snapshot().Count);

        // The new one is found again by its attackers.
        Assert.Equal(otherAttacker, Ingest(aggregator, Loop(2040f, -3380f, 1, 2829)));
        Assert.Equal(first, Ingest(aggregator, Loop(2036f, -3378f, 1, 3821)));
        Assert.Equal(3, aggregator.Snapshot().Count);
    }

    [Fact]
    public void DeathLoops_WithoutAttackers_MergeOnlyOnTheSameDeathSource()
    {
        static QaSignal WithSource(float x, string source)
            => Loop(x, 0, 1) with { Bot = new QaBotObservation(10, "Human", "Alliance", "Mage", "Quest", 0, 0, null, source, []) };

        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, WithSource(0, "Environment"));
        Assert.Equal(a, Ingest(aggregator, WithSource(120, "Environment")));
        Assert.NotEqual(a, Ingest(aggregator, WithSource(60, "Creature")));
        Assert.Equal(2, aggregator.Snapshot().Count);
    }

    [Fact]
    public void Stalls_DriftingWithinTheAnchorRadius_AreOneFinding_UnlessTheGoalDiffers()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, goal: "Explore"));
        string b = Ingest(aggregator, QaData.Stall(QaData.Bot2, 77, 0, goal: "Explore"));
        Assert.Equal(a, b);
        Assert.Single(aggregator.Snapshot());

        string quest = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, goal: "Quest"));
        Assert.NotEqual(a, quest);
        Assert.Equal(2, aggregator.Snapshot().Count);

        QaSignal outside = QaData.Stall(QaData.Bot1, 10, 10, goal: "Explore") with { OutsideHome = true };
        Assert.NotEqual(a, Ingest(aggregator, outside));
        Assert.Equal(3, aggregator.Snapshot().Count);
    }

    [Fact]
    public void TheAnchor_DoesNotChain()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(QaData.Bot1, 450, 0, goal: "Explore"));
        Assert.Equal(a, Ingest(aggregator, QaData.Stall(QaData.Bot1, 590, 0, goal: "Explore"))); // the next zone cell, but 140 yd from the anchor
        string c = Ingest(aggregator, QaData.Stall(QaData.Bot1, 740, 0, goal: "Explore")); // 150 yd from the one before, 290 from the anchor
        Assert.NotEqual(a, c);
        Assert.Equal(2, aggregator.Snapshot().Count);
    }

    [Fact]
    public void DifferentMaps_NeverMerge()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, map: 0, goal: "Explore"));
        string b = Ingest(aggregator, QaData.Stall(QaData.Bot1, 60, 0, map: 1, goal: "Explore"));
        Assert.NotEqual(a, b);
        string c = Ingest(aggregator, Loop(0, 0, 0, 5));
        string d = Ingest(aggregator, Loop(60, 0, 1, 5));
        Assert.NotEqual(c, d);
        Assert.Equal(4, aggregator.Snapshot().Count);
    }

    [Fact]
    public void RestoredFindingWithoutSamples_StillMatchesThroughItsPersistedAnchor()
    {
        PlayerbotQaAggregator source = New();
        string id = Ingest(source, QaData.Stall(QaData.Bot1, 0, 0));
        string loopId = Ingest(source, Loop(2036f, -3378f, 1, 3821));
        QaAggregatorState saved = source.Export();
        Assert.All(saved.Findings, f => Assert.NotNull(f.Anchor));
        QaAggregatorState bare = saved with { Findings = [.. saved.Findings.Select(f => f with { Samples = [] })] };

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(bare));
        Assert.Equal(id, Ingest(target, QaData.Stall(QaData.Bot2, 140, 0)));
        Assert.Equal(loopId, Ingest(target, Loop(2150f, -3386f, 1, 3821, 99)));
        Assert.NotEqual(loopId, Ingest(target, Loop(2150f, -3386f, 1, 2829)));
        Assert.Equal(3, target.Snapshot().Count);

        // The anchor survives a second save and load.
        PlayerbotQaAggregator again = New();
        Assert.Equal(0, again.Restore(target.Export()));
        Assert.Equal(id, Ingest(again, QaData.Stall(QaData.Bot3, 155, 0)));
    }

    [Fact]
    public void OldPersistedState_WithoutAnAnchor_LoadsAndMatches()
    {
        PlayerbotQaAggregator source = New();
        string id = Ingest(source, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, goal: "Explore"));
        QaAggregatorState old = source.Export();

        // A file written before the anchor existed: no Anchor, no Attackers, the sample still there -> the first sample is the anchor.
        QaAggregatorState withSample = old with { Findings = [.. old.Findings.Select(f => f with { Anchor = null })] };
        PlayerbotQaAggregator a = New();
        Assert.Equal(0, a.Restore(withSample));
        Assert.Equal(id, Ingest(a, QaData.Stall(QaData.Bot2, -5608.8f + 150f, -439.4f, goal: "Explore")));
        Assert.NotEqual(id, Ingest(a, QaData.Stall(QaData.Bot2, -5608.8f + 170f, -439.4f, goal: "Explore")));

        // No anchor and no sample -> the centre of the key's 50-yard cell (-113,-9): (-5625,-425).
        QaAggregatorState bare = old with { Findings = [.. old.Findings.Select(f => f with { Anchor = null, Samples = [] })] };
        PlayerbotQaAggregator b = New();
        Assert.Equal(0, b.Restore(bare));
        Assert.Equal(id, Ingest(b, QaData.Stall(QaData.Bot2, -5625f + 159f, -425f, goal: "Explore")));
        Assert.NotEqual(id, Ingest(b, QaData.Stall(QaData.Bot2, -5625f + 161f, -425f, goal: "Explore")));

        // Through JSON, as a real old findings.json: the new properties are simply absent.
        string json = JsonSerializer.Serialize(new { Findings = old.Findings.Select(f => new { f.Id, f.Key, Kind = f.Kind.ToString(), f.Count, f.Map }) });
        QaStoreFile? file = JsonSerializer.Deserialize(json, QaStoreJsonContext.Default.QaStoreFile);
        Assert.NotNull(file);
        PlayerbotQaAggregator c = New();
        Assert.Equal(0, c.Restore(new QaAggregatorState(file.Findings, new Dictionary<string, ulong[]>())));
        QaFindingView restored = Assert.Single(c.Snapshot());
        Assert.Equal(new QaAnchor(0, -5625f, -425f), c.Export().Findings[0].Anchor); // the centre of cell (-113,-9)
        Assert.Equal(id, restored.Id);
        Assert.Equal(id, Ingest(c, QaData.Stall(QaData.Bot2, -5625f + 100f, -425f, goal: "Explore")));
        Assert.NotEqual(id, Ingest(c, QaData.Stall(QaData.Bot2, -5625f + 200f, -425f, goal: "Explore")));
    }

    private static QaFindingView OldLoop(QaFindingView v, IReadOnlyList<QaSample>? samples = null, string? subject = null)
        => v with { Anchor = null, Attackers = [], DeathSource = string.Empty, Samples = samples ?? v.Samples, Subject = subject ?? v.Subject };

    private static QaSignal LoopFrom(float x, float y, string source, params uint[] attackers)
        => Loop(x, y, 1, attackers) with { Bot = new QaBotObservation(10, "Human", "Alliance", "Mage", "Quest", 0, 0, null, source, []) };

    [Fact]
    public void OldFormatDeathLoop_TakesAttackersAndSourceFromItsSamples()
    {
        PlayerbotQaAggregator source = New();
        string withAttackers = Ingest(source, LoopFrom(2036f, -3378f, "Creature", 3821));
        string noAttackers = Ingest(source, LoopFrom(500f, 500f, "Environment"));
        QaAggregatorState old = source.Export();
        QaAggregatorState state = old with { Findings = [.. old.Findings.Select(f => OldLoop(f))] };

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(state));
        // The same cell: folds into the existing finding by the samples' attackers / source, not into a variant.
        Assert.Equal(withAttackers, Ingest(target, LoopFrom(2040f, -3380f, "Creature", 3821)));
        Assert.Equal(noAttackers, Ingest(target, LoopFrom(505f, 505f, "Environment")));
        Assert.Equal(2, target.Snapshot().Count);
        // ...and another attacker / source in the same cell is a different loop.
        Assert.NotEqual(withAttackers, Ingest(target, LoopFrom(2040f, -3380f, "Creature", 2829)));
        Assert.NotEqual(noAttackers, Ingest(target, LoopFrom(505f, 505f, "Creature")));
        Assert.Equal(4, target.Snapshot().Count);
    }

    [Fact]
    public void OldFormatDeathLoop_WithSamplesFromSeveralLoops_MatchesEveryLoopItAlreadyStoodFor()
    {
        // Before anchors one finding per cell merged every death loop there, so its samples hold several attacker sets.
        PlayerbotQaAggregator first = New();
        string id = Ingest(first, LoopFrom(2036f, -3378f, "Creature", 3821));
        PlayerbotQaAggregator second = New();
        Ingest(second, LoopFrom(2040f, -3380f, "Creature", 2829));
        QaSample[] samples = [.. first.Export().Findings[0].Samples!, .. second.Export().Findings[0].Samples!];
        QaAggregatorState old = first.Export();
        QaAggregatorState state = old with { Findings = [.. old.Findings.Select(f => OldLoop(f, samples: samples))] };

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(state));
        // The newer sample's attacker belongs to the existing finding (and its issue), not to a variant.
        Assert.Equal(id, Ingest(target, LoopFrom(2040f, -3380f, "Creature", 2829)));
        Assert.Equal(id, Ingest(target, LoopFrom(2040f, -3380f, "Creature", 3821)));
        Assert.Single(target.Snapshot());
    }

    [Fact]
    public void OldFormatDeathLoop_WithoutSamples_TakesAttackersFromTheSubject()
    {
        PlayerbotQaAggregator source = New();
        string id = Ingest(source, Loop(2036f, -3378f, 1, 3821)); // cell (40,-68)
        QaAggregatorState old = source.Export();
        QaAggregatorState state = old with
        {
            Findings = [.. old.Findings.Select(f => OldLoop(f, samples: [], subject: "map 1 near 2000,-3400 attackers 3821/3782"))],
        };

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(state));
        Assert.Equal(id, Ingest(target, Loop(2040f, -3380f, 1, 3821)));
        Assert.Equal(id, Ingest(target, Loop(2040f, -3380f, 1, 3782)));
        Assert.NotEqual(id, Ingest(target, Loop(2040f, -3380f, 1, 2829)));
    }

    [Fact]
    public void StoreRoundTrip_KeepsAnchorAttackersAndSource_AndStillMatchesNearby()
    {
        PlayerbotQaAggregator source = New();
        string stall = Ingest(source, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));
        string loop = Ingest(source, LoopFrom(2036f, -3378f, "Creature", 3821, 3782));
        QaAggregatorState saved = source.Export();

        string directory = QaData.NewTempDirectory();
        try
        {
            var store = new PlayerbotQaStore(directory, new ListLogger());
            Assert.True(store.Save(new QaStoreFile { SaltBase64 = Convert.ToBase64String(Salt), Findings = saved.Findings, Bots = saved.Bots }));
            QaStoreFile loaded = new PlayerbotQaStore(directory, new ListLogger()).Load() ?? throw new InvalidOperationException("not loaded");

            QaFindingView loadedLoop = loaded.Findings.Single(f => f.Id == loop);
            Assert.Equal(new QaAnchor(1, 2036f, -3378f), loadedLoop.Anchor);
            Assert.Equal(new uint[] { 3782, 3821 }, loadedLoop.Attackers);
            Assert.Equal("Creature", loadedLoop.DeathSource);
            Assert.Equal(new QaAnchor(0, -5608.8f, -439.4f), loaded.Findings.Single(f => f.Id == stall).Anchor);

            PlayerbotQaAggregator target = New();
            Assert.Equal(0, target.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots)));
            Assert.Equal(stall, Ingest(target, QaData.Stall(QaData.Bot2, -5608.8f + 150f, -439.4f)));
            Assert.Equal(loop, Ingest(target, LoopFrom(2036f + 150f, -3378f, "Creature", 3821)));
            Assert.Equal(2, target.Snapshot().Count);
        }
        finally
        {
            QaData.DeleteQuietly(directory);
        }
    }

    [Fact]
    public void DeathLoopIdentity_IsTheUnionOfItsSignalsAttackers()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, Loop(0, 0, 1, 1));
        Assert.Equal(a, Ingest(aggregator, Loop(10, 0, 1, 1, 2))); // overlaps {1}: folds, and the finding's attackers are now {1,2}
        Assert.Equal(a, Ingest(aggregator, Loop(20, 0, 1, 2, 3))); // overlaps {1,2}: the same loop, however the attacker sets drift
        string other = Ingest(aggregator, Loop(20, 0, 1, 9)); // nobody in common
        Assert.NotEqual(a, other);
        Assert.Equal(2, aggregator.Snapshot().Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, aggregator.Snapshot().Single(f => f.Id == a).Attackers);
    }

    [Fact]
    public void AFoldedSignalFromAnotherCell_KeepsTheFirstSignalsGoalAndCause()
    {
        PlayerbotQaAggregator aggregator = New();
        string id = Ingest(aggregator, QaData.Stall(QaData.Bot1, 0, 0, goal: "Explore"));
        QaFindingView first = aggregator.Snapshot().Single();
        Assert.Equal(id, Ingest(aggregator, QaData.Stall(QaData.Bot2, 100, 0, goal: "Explore", quest: 1656) with { Goal = "Explore" }));
        QaFindingView after = aggregator.Snapshot().Single();
        Assert.Equal(first.Subject, after.Subject);
        Assert.Equal(first.Goal, after.Goal);
        Assert.Equal(first.Cause, after.Cause);
        Assert.Equal(first.CauseDetail, after.CauseDetail);
        Assert.Equal(first.Quest, after.Quest);
        Assert.Equal(2, after.Count);
    }
}
