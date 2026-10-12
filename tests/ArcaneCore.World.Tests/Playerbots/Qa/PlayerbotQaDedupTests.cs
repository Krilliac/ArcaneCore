using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// One real problem is one finding: the position a stall or death loop was met at is evidence, not identity. The subjects below are the ones
/// measured in the live store (2026-10-12): the same Duskwood/Westfall death loop was four findings, the same Explore stall four more.
/// </summary>
public sealed class PlayerbotQaDedupTests
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static PlayerbotQaAggregator New(int maxFindings = 100) => new(maxFindings, 5, Salt);

    private static string Ingest(PlayerbotQaAggregator aggregator, QaSignal signal)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    private static QaSignal Loop(float x, float y, params uint[] attackers)
        => new()
        {
            Kind = QaSignalKind.DeathLoop, AtUtc = QaData.T0, BotKey = Guid.NewGuid(), HasPosition = true, Map = 0, X = x, Y = y, Z = 50f, Attackers = attackers,
            Message = "Playerbot <bot> death loop",
        };

    private static QaSignal[] DuskwoodLoops() =>
    [
        Loop(-10600f, 300f, 531), // "map 0 near -10600,300 attackers 531"
        Loop(-10650f, 250f, 202), // "near -10650,250 attackers 202"
        Loop(-10600f, 250f, 202, 531), // "near -10600,250 attackers 202/531"
        Loop(-10650f, 300f, 202, 531, 949), // "near -10650,300 attackers 202/531/949"
    ];

    private static QaSignal Explore(float x, float y, uint quest = 109)
        => QaData.Stall(Guid.NewGuid(), x, y, quest: quest, target: 0, goal: "Explore");

    [Fact]
    public void TheFourDuskwoodDeathLoopSubjects_AreOneFinding_WithTheSummedCount()
    {
        PlayerbotQaAggregator aggregator = New();
        string[] ids = [.. DuskwoodLoops().Select(s => Ingest(aggregator, s))];

        QaFindingView f = Assert.Single(aggregator.Snapshot());
        // The first two loops share no attacker, so they begin as two findings; the third bridges them and they merge for good.
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(f.Id, ids[2]);
        Assert.Equal(f.Id, ids[3]);
        Assert.Equal(4, f.Count);
        Assert.Equal(4, f.DistinctBots);
        Assert.Equal(new uint[] { 202, 531, 949 }, f.Attackers);
        Assert.Equal(4, f.Samples.Count);
    }

    [Fact]
    public void TheFourExploreStalls_AreOneFinding()
    {
        PlayerbotQaAggregator aggregator = New();
        // "goal Explore map 0 near -10300,1400" / "-10350,1400" / "-10400,1500" / "-10350,1450": the last two lie across a zone cell border in y.
        foreach ((float x, float y) in new[] { (-10300f, 1400f), (-10350f, 1400f), (-10400f, 1500f), (-10350f, 1450f) })
        {
            Ingest(aggregator, Explore(x, y));
        }

        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(4, f.Count);
        Assert.Equal(4, f.DistinctBots);
    }

    [Fact]
    public void CombatStalls_AreTheirTarget_WhereverTheyStood()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -10850f, -2700f, quest: 0, target: 7669, goal: "Combat"));
        string b = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -10900f, -2650f, quest: 0, target: 7669, goal: "Combat"));
        string far = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 4200f, 900f, quest: 0, target: 7669, goal: "Combat"));
        string other = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -10850f, -2700f, quest: 0, target: 7670, goal: "Combat"));

        Assert.Equal(a, b);
        Assert.Equal(a, far);
        Assert.NotEqual(a, other);
        Assert.Equal(2, aggregator.Snapshot().Count);
        Assert.Equal(3, aggregator.Get(a)!.Count);
    }

    [Fact]
    public void TwoQuestsAtTheSameSpot_StaySeparate_OneQuestAtTwoSpots_IsOne()
    {
        PlayerbotQaAggregator aggregator = New();
        string q1 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -5608f, -439f, quest: 1656, target: 6747));
        string q2 = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -5608f, -439f, quest: 1657, target: 6747));
        Assert.NotEqual(q1, q2);

        Assert.Equal(q1, Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -5100f, -300f, quest: 1656, target: 6747)));
        Assert.Equal(2, aggregator.Snapshot().Count);
    }

    [Fact]
    public void DeathLoopsOfOtherAttackers_AndStallsOfOtherGoals_InTheSameArea_StaySeparate()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, Loop(-10600f, 300f, 531));
        string b = Ingest(aggregator, Loop(-10620f, 310f, 2829));
        Assert.NotEqual(a, b);
        string explore = Ingest(aggregator, Explore(-10300f, 1400f));
        string retreat = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), -10300f, 1400f, quest: 0, target: 0, goal: "Retreat"));
        Assert.NotEqual(explore, retreat);
        Assert.Equal(4, aggregator.Snapshot().Count);
    }

    [Fact]
    public void ABridgingDeathLoop_MergesTheFindingsItJoins_AndAPublishedOneSurvives()
    {
        PlayerbotQaAggregator aggregator = New();
        string a = Ingest(aggregator, Loop(-10600f, 300f, 531));
        string b = Ingest(aggregator, Loop(-10650f, 250f, 202));
        aggregator.Apply(b, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 77), null, 1));
        Assert.Equal(2, aggregator.Snapshot().Count);

        string bridged = Ingest(aggregator, Loop(-10600f, 250f, 202, 531));

        Assert.Equal(b, bridged);
        QaFindingView f = Assert.Single(aggregator.Snapshot());
        Assert.Equal(b, f.Id);
        Assert.Equal(3, f.Count);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 77), f.Publication);
        Assert.Null(aggregator.Get(a));
    }

    [Fact]
    public void AtTheCap_APublishedFindingSurvivesAStreamOfNoise_AndOneOffsGoBeforeABusyFinding()
    {
        PlayerbotQaAggregator aggregator = New(maxFindings: 4);
        string published = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 1));
        aggregator.Apply(published, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 5)));
        string busy = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 2, at: QaData.T0.AddMinutes(-30)));
        for (int i = 0; i < 5; i++) Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 2, at: QaData.T0.AddMinutes(-30)));

        for (uint quest = 100; quest < 140; quest++)
        {
            Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: quest, at: QaData.T0.AddMinutes(quest - 100)));
        }

        Assert.Equal(4, aggregator.Count);
        Assert.NotNull(aggregator.Get(published));
        Assert.Equal(QaPublicationState.Published, aggregator.Get(published)!.Publication.State);
        Assert.NotNull(aggregator.Get(busy)); // six occurrences of six bots outlive 40 one-offs although it is the oldest
        Assert.Equal(0, aggregator.Rejected);
    }

    [Fact]
    public void RestoringAnOldFormatStore_MergesTheFragments_AndKeepsThePublishedState()
    {
        // The old store: every position cell was a finding of its own, with the old-format fields (no anchor, no saved attackers).
        var findings = new List<QaFindingView>();
        var bots = new Dictionary<string, ulong[]>();
        foreach (QaSignal loop in DuskwoodLoops())
        {
            PlayerbotQaAggregator one = New();
            Ingest(one, loop);
            QaAggregatorState state = one.Export();
            findings.AddRange(state.Findings.Select(v => v with { Anchor = null, Attackers = [], DeathSource = string.Empty }));
            foreach ((string id, ulong[] hashes) in state.Bots) bots[id] = hashes;
        }

        foreach ((float x, float y) in new[] { (-10300f, 1400f), (-10350f, 1400f), (-10400f, 1500f), (-10350f, 1450f) })
        {
            PlayerbotQaAggregator one = New();
            Ingest(one, Explore(x, y));
            QaAggregatorState state = one.Export();
            findings.AddRange(state.Findings.Select(v => v with { Anchor = null }));
            foreach ((string id, ulong[] hashes) in state.Bots) bots[id] = hashes;
        }

        QaFindingView published = findings.First(f => f.Kind == QaSignalKind.DeathLoop && f.Samples![0].Attackers.SequenceEqual(new uint[] { 202 }));
        findings[findings.IndexOf(published)] = published with { Publication = new QaPublication(QaPublicationState.Published, 42), OccurrencesReported = 1 };
        Assert.Equal(8, findings.Count);

        PlayerbotQaAggregator restored = New();
        Assert.Equal(0, restored.Restore(new QaAggregatorState(findings, bots)));

        IReadOnlyList<QaFindingView> after = restored.Snapshot();
        Assert.Equal(2, after.Count);
        QaFindingView loop1 = after.Single(f => f.Kind == QaSignalKind.DeathLoop);
        Assert.Equal(published.Id, loop1.Id);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 42), loop1.Publication);
        Assert.Equal(4, loop1.Count);
        Assert.Equal(4, loop1.DistinctBots);
        Assert.Equal(4, after.Single(f => f.Kind == QaSignalKind.Stall).Count);
        Assert.Equal(6, restored.Merged);

        // The merged finding keeps counting: a new signal of the area is that finding, not a new one.
        Assert.Equal(published.Id, Ingest(restored, Loop(-10620f, 280f, 949)));
    }
}
