using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// When <see cref="PlayerbotQaAggregator.FindingCreated"/> fires: once per finding that a real occurrence created, never for the start-up coverage,
/// a merge or an alias, never under the lock, and never so that a subscriber's failure reaches the ingest path.
/// </summary>
public sealed class PlayerbotQaFindingCreatedTests
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private static PlayerbotQaAggregator New() => new(100, 5, Salt);

    private static string Ingest(PlayerbotQaAggregator aggregator, QaSignal signal)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    private static QaSignal Loop(Guid bot, string attackers)
        => QaData.Parse(QaData.BotLine(bot, "Dawnrover", $"death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers={attackers}"));

    [Fact]
    public void ARealOccurrence_FiresOncePerNewFinding_WithTheViewAfterTheSignalWasFoldedIn()
    {
        PlayerbotQaAggregator aggregator = New();
        var created = new List<QaFindingView>();
        aggregator.FindingCreated = created.Add;

        string id = Ingest(aggregator, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));
        Ingest(aggregator, QaData.Stall(QaData.Bot2, -5608.8f, -439.4f)); // the same finding again

        QaFindingView view = Assert.Single(created);
        Assert.Equal(id, view.Id);
        Assert.Equal(1, view.Count);
    }

    [Fact]
    public void ACoverageSignal_CreatesAFindingWithNoCallback_AndALaterRealOccurrenceOnItIsStillNotAnnounced()
    {
        PlayerbotQaAggregator aggregator = New();
        var created = new List<QaFindingView>();
        aggregator.FindingCreated = created.Add;

        QaSignal coverage = PlayerbotQaSpellCoreTests.Gap(133, coverage: true);
        string id = Ingest(aggregator, coverage);
        Assert.Equal(1, aggregator.Count);
        Assert.Empty(created);

        // Documented behaviour: the finding already exists, so the first bot to meet the gap does not announce it.
        string again = Ingest(aggregator, PlayerbotQaSpellCoreTests.Gap(133, bot: QaData.Bot1));
        Assert.Equal(id, again);
        Assert.Equal(1, aggregator.Count);
        Assert.Empty(created);

        // Control: the callback is live, a different real finding does fire.
        Ingest(aggregator, PlayerbotQaSpellCoreTests.Gap(134, bot: QaData.Bot1));
        Assert.Single(created);
    }

    [Fact]
    public void AMerge_ThroughResolve_FiresNothing_ThoughItFoldsTwoFindingsIntoOne()
    {
        PlayerbotQaAggregator aggregator = New();
        var created = new List<QaFindingView>();
        aggregator.FindingCreated = created.Add;

        // Two death loops of one area with different attackers are two findings (two announcements).
        Ingest(aggregator, Loop(QaData.Bot1, "100"));
        Ingest(aggregator, Loop(QaData.Bot2, "200"));
        Assert.Equal(2, aggregator.Count);
        Assert.Equal(2, created.Count);

        // A loop with both attackers bridges them: Resolve merges the two. That is no new problem, so nothing is announced.
        string survivor = Ingest(aggregator, Loop(QaData.Bot3, "100/200"));

        Assert.Equal(1, aggregator.Merged);
        Assert.Equal(1, aggregator.Count);
        Assert.Equal(survivor, Assert.Single(aggregator.Snapshot()).Id);
        Assert.Equal(2, created.Count);
    }

    [Fact]
    public void AStall_ReachingAnAbsorbedFindingThroughItsAlias_FiresNothing()
    {
        QaSignal first = QaData.Stall(QaData.Bot1, -5608.8f, -439.4f);
        QaSignal other = QaData.Stall(QaData.Bot2, -5608.8f, -439.4f, quest: 1700); // another group: it would be a finding of its own
        PlayerbotQaAggregator seed = New();
        string survivorId = Ingest(seed, first);
        QaAggregatorState saved = seed.Export();

        PlayerbotQaAggregator aggregator = New();
        var absorbed = new QaAbsorbed(PlayerbotQaFingerprint.Of(other), survivorId);
        aggregator.Restore(new QaAggregatorState(saved.Findings, saved.Bots, [absorbed]), QaData.T0);
        var created = new List<QaFindingView>();
        aggregator.FindingCreated = created.Add;

        string id = Ingest(aggregator, other);

        Assert.Equal(survivorId, id);
        Assert.Equal(1, aggregator.Count);
        Assert.Empty(created);

        // Control: without the alias the same stall is a new finding and fires.
        PlayerbotQaAggregator plain = New();
        var plainCreated = new List<QaFindingView>();
        plain.FindingCreated = plainCreated.Add;
        Ingest(plain, other);
        Assert.Single(plainCreated);
    }

    [Fact]
    public void AThrowingCallback_DoesNotMakeIngestThrow_AndIngestStillReturnsTheId()
    {
        PlayerbotQaAggregator aggregator = New();
        int calls = 0;
        aggregator.FindingCreated = _ =>
        {
            calls++;
            throw new InvalidOperationException("subscriber failed");
        };
        QaSignal signal = QaData.Stall(QaData.Bot1, -5608.8f, -439.4f);

        string? id = aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0);

        Assert.Equal(1, calls);
        Assert.Equal(PlayerbotQaFingerprint.Of(signal), id);
        Assert.Equal(1, aggregator.Count); // the finding is kept, the state is intact
        Assert.Equal(1, Assert.Single(aggregator.Snapshot()).Count);
    }

    [Fact]
    public void TheCallback_RunsOutsideTheLock_SoAnotherThreadCanReadTheAggregatorFromInsideIt()
    {
        PlayerbotQaAggregator aggregator = New();
        bool? readOnAnotherThread = null;
        aggregator.FindingCreated = _ =>
        {
            // Count takes the aggregator's lock. Monitor locks are reentrant, so only a different thread can tell whether the caller still holds it.
            Task<int> read = Task.Run(() => aggregator.Count);
            readOnAnotherThread = read.Wait(TimeSpan.FromSeconds(5));
        };

        string id = Ingest(aggregator, QaData.Stall(QaData.Bot1, -5608.8f, -439.4f));

        Assert.True(readOnAnotherThread, "the callback ran while the aggregator's lock was held");
        Assert.NotEmpty(id);
    }
}
