using System.Text.Json;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Findings nobody has met for a day are noise; a published or known-closed finding is the only local memory of its issue and stays.</summary>
public sealed class PlayerbotQaAgeOutTests
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static PlayerbotQaAggregator New() => new(100, 5, Salt, Day);

    private static string Ingest(PlayerbotQaAggregator aggregator, QaSignal signal)
        => aggregator.Ingest(signal, NoQaContentFacts.Instance, QaData.T0) ?? throw new InvalidOperationException("refused");

    [Fact]
    public void AgeOut_DropsAStaleUnpublishedFinding_AndKeepsFreshAndPublishedOnes()
    {
        PlayerbotQaAggregator aggregator = New();
        string stale = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 1));
        string published = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 2));
        string closed = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 3));
        string fresh = Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 4, at: QaData.T0.AddHours(20)));
        aggregator.Apply(published, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 8)));
        aggregator.Apply(closed, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, 9)));

        Assert.Equal(0, aggregator.AgeOut(QaData.T0.AddHours(23)));
        Assert.Equal(1, aggregator.AgeOut(QaData.T0.AddHours(25)));

        Assert.Null(aggregator.Get(stale));
        Assert.NotNull(aggregator.Get(published));
        Assert.NotNull(aggregator.Get(closed));
        Assert.NotNull(aggregator.Get(fresh));
        Assert.Equal(1, aggregator.AgedOut);
    }

    [Fact]
    public void WithoutAnAgeLimit_NothingAgesOut()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, Salt);
        Ingest(aggregator, QaData.Stall(Guid.NewGuid(), 0, 0));
        Assert.Equal(0, aggregator.AgeOut(QaData.T0.AddDays(400)));
        Assert.Single(aggregator.Snapshot());
    }

    [Fact]
    public void Restore_DropsStaleUnpublishedFindings_ButNotPublishedOnes()
    {
        PlayerbotQaAggregator source = New();
        string stale = Ingest(source, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 1));
        string published = Ingest(source, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 2));
        string fresh = Ingest(source, QaData.Stall(Guid.NewGuid(), 0, 0, quest: 3, at: QaData.T0.AddHours(30)));
        source.Apply(published, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 8)));

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(source.Export(), QaData.T0.AddHours(40)));

        Assert.Null(target.Get(stale));
        Assert.NotNull(target.Get(published));
        Assert.NotNull(target.Get(fresh));
        Assert.Equal(1, target.AgedOut);
    }

    [Fact]
    public void AFileWithoutTheNewProperties_StillLoads_AndAPublishedFindingKeepsItsState()
    {
        PlayerbotQaAggregator source = New();
        string id = Ingest(source, QaData.Stall(Guid.NewGuid(), -5608.8f, -439.4f));
        source.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, 12)));
        QaFindingView v = source.Export().Findings.Single();

        // As the live file of 2026-10-11: no anchor, no samples, no attackers, only what the first store format wrote.
        string json = JsonSerializer.Serialize(new
        {
            Findings = new[] { new { v.Id, v.Key, Kind = v.Kind.ToString(), v.Count, v.Map, LastSeenUtc = v.LastSeenUtc, Publication = new { State = "Published", Issue = 12 } } },
        });
        QaStoreFile file = JsonSerializer.Deserialize(json, QaStoreJsonContext.Default.QaStoreFile) ?? throw new InvalidOperationException("not loaded");

        PlayerbotQaAggregator target = New();
        Assert.Equal(0, target.Restore(new QaAggregatorState(file.Findings, new Dictionary<string, ulong[]>()), QaData.T0.AddDays(30)));
        QaFindingView restored = Assert.Single(target.Snapshot());
        Assert.Equal(id, restored.Id);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 12), restored.Publication);
    }

    [Fact]
    public void StaleHours_IsValidated()
    {
        var options = new PlayerbotQaOptions();
        Assert.Equal(24, options.StaleHours);
        Assert.Empty(options.Validate());
        options.StaleHours = -1;
        Assert.Contains(options.Validate(), p => p.Contains("StaleHours", StringComparison.Ordinal));
        options.StaleHours = 0;
        Assert.Empty(options.Validate());
    }
}
