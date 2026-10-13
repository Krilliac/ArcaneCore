using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The detectors that report straight to bot QA (run failures, tick hangs): their fingerprints, how repeated reports aggregate, that no bot or group identity
/// reaches a finding, and the Prometheus series over the findings (docs/areas/playbots-qa.md, docs/ops/metrics.md).
/// </summary>
public sealed class PlayerbotQaDetectorTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static QaSignal Run(string reason, string stage, uint boss = 0, uint map = 36, int wing = 0, string name = "The Deadmines", DateTime? at = null) => new()
    {
        Kind = QaSignalKind.RunFailed, AtUtc = at ?? QaData.T0, Map = map, Segment = wing, Stage = stage, Target = boss, Reason = reason, Detail = name,
        Message = $"dungeon run ended: {reason} in {name} (map {map} wing {wing}) at {stage}, 0/1 bosses dead, 0 wipes",
    };

    private static QaSignal Hang(string phase, string item, long ms, DateTime? at = null) => new()
    {
        Kind = QaSignalKind.TickHang, AtUtc = at ?? QaData.T0, Stage = phase, Reason = item, Message = $"The world tick is hung (running {ms} ms): hang threshold 100 ms",
    };

    [Fact]
    public void RunFailures_AtOneStage_AreOneFinding_WhoeverTheGroupWas_AndOtherStagesOrReasonsAreTheirOwn()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);
        var facts = new FakeFacts();
        string? first = aggregator.Ingest(Run("no-path-to-boss", "clear", boss: 644), facts, QaData.T0);
        string? second = aggregator.Ingest(Run("no-path-to-boss", "clear", boss: 644, at: QaData.T0.AddMinutes(9)), facts, QaData.T0.AddMinutes(9));
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, aggregator.Ingest(Run("no-path-to-boss", "boss", boss: 644), facts, QaData.T0));
        Assert.NotEqual(first, aggregator.Ingest(Run("wiped", "clear", boss: 644), facts, QaData.T0));
        Assert.NotEqual(first, aggregator.Ingest(Run("no-path-to-boss", "clear", boss: 645), facts, QaData.T0));
        Assert.NotEqual(first, aggregator.Ingest(Run("no-path-to-boss", "clear", boss: 644, map: 389), facts, QaData.T0));

        QaFindingView view = aggregator.Get(first!)!;
        Assert.Equal(QaSignalKind.RunFailed, view.Kind);
        Assert.Equal(2, view.Count);
        Assert.Equal(0, view.DistinctBots);
        Assert.True(view.BotIndependent);
        Assert.Equal("no-path-to-boss in The Deadmines at clear", view.Subject);
        Assert.Equal(QaCause.MissingData, view.Cause);
        Assert.Equal(5, aggregator.Count);
    }

    [Fact]
    public void ARunFailure_CarriesNoBotOrGroupIdentity_InItsSubjectSamplesOrPublishedIssue()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);
        string? id = aggregator.Ingest(Run("travel-timeout", "travel"), new FakeFacts(), QaData.T0);
        QaFindingView view = aggregator.Get(id!)!;
        var redactor = new PlayerbotQaRedactor();
        QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(view, ["bot-qa"], "test-build", redactor);

        Assert.Equal("travel-timeout in The Deadmines at travel", view.Subject);
        Assert.Contains("run failed", draft.Title, StringComparison.Ordinal);
        Assert.All(view.Samples, sample => Assert.Null(sample.Bot));
        Assert.DoesNotContain(QaData.Bot1.ToString(), draft.Body, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("v1|runfailed|map=36|wing=0|stage=travel|boss=0|reason=travel-timeout", PlayerbotQaFingerprint.Key(Run("travel-timeout", "travel")), StringComparison.Ordinal);
    }

    [Fact]
    public void TickHangs_AreKeyedByPhaseAndWork_NotByDurationOrTime_AndOutrankTheBotKinds()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);
        var facts = new FakeFacts();
        string? id = aggregator.Ingest(Hang("Features", "SpellFeature", 120), facts, QaData.T0);
        Assert.Equal(id, aggregator.Ingest(Hang("Features", "SpellFeature", 98_000, QaData.T0.AddHours(3)), facts, QaData.T0.AddHours(3)));
        Assert.NotEqual(id, aggregator.Ingest(Hang("Maps", "SpellFeature", 120), facts, QaData.T0));
        Assert.NotEqual(id, aggregator.Ingest(Hang("Features", "QuestFeature", 120), facts, QaData.T0));

        QaFindingView view = aggregator.Get(id!)!;
        Assert.Equal(2, view.Count);
        Assert.Equal("world tick hung in Features: SpellFeature", view.Subject);
        Assert.True(view.BotIndependent);
        Assert.Equal(QaCause.ServerFault, view.Cause);
        Assert.False(QaKinds.Of(QaSignalKind.TickHang).SingleOccurrence);
        Assert.All(QaKinds.All.Where(r => r.Kind != QaSignalKind.TickHang), r => Assert.True(QaKinds.Of(QaSignalKind.TickHang).Weight > r.Weight));
        Assert.InRange(QaKinds.Of(QaSignalKind.RunFailed).Weight, QaKinds.Of(QaSignalKind.Stall).Weight, QaKinds.Of(QaSignalKind.Quarantine).Weight);
    }

    private (ServiceProvider Services, PlayerbotQaSignalBus Bus) Build(bool metrics)
    {
        var bus = new PlayerbotQaSignalBus(64);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(new PlayerbotQaOptions { Directory = _directory }))
            .AddSingleton(bus)
            .AddSingleton(new MetricsOptions { Enabled = metrics })
            .BuildServiceProvider();
        return (services, bus);
    }

    // The feature's own task drains the bus: a test waits for the folded result instead of racing it.
    private static void Eventually(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "the signals were not folded in time");
    }

    /// <summary>The findings gauge of exactly this feature (its meter's scope), read through a listener so other tests' features and meters never mix in.</summary>
    private static Dictionary<(string Kind, string State), long> FindingsOf(PlayerbotQaFeature feature, out bool registered)
    {
        var values = new Dictionary<(string Kind, string State), long>();
        bool found = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, feature) && instrument.Name == "arcanecore.botqa.findings")
            {
                found = true;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string kind = "", state = "";
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind") kind = (string)tag.Value!;
                if (tag.Key == "state") state = (string)tag.Value!;
            }

            values[(kind, state)] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        registered = found;
        return values;
    }

    [Fact]
    public async Task WithMetricsOn_TheFindingsGauge_HasOneSeriesPerKindAndState_AndTheOccurrenceCounterCountsFoldedSignals()
    {
        using var store = new MetricsStore();
        store.Start();
        long runFailedOccurrences = 0;
        using var counter = new System.Diagnostics.Metrics.MeterListener();
        counter.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PlayerbotQaMeters.MeterName && instrument.Name == "arcanecore.botqa.occurrences") l.EnableMeasurementEvents(instrument);
        };
        counter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "kind" && (string?)tag.Value == "run-failed") Interlocked.Add(ref runFailedOccurrences, value);
        });
        counter.Start();
        (ServiceProvider services, PlayerbotQaSignalBus bus) = Build(metrics: true);
        using ServiceProvider _ = services;
        var feature = new PlayerbotQaFeature(services);
        feature.Attach(null!);
        try
        {
            var sink = new PlayerbotQaSink(bus);
            sink.Report(Run("wiped", "boss", boss: 644));
            sink.Report(Run("wiped", "boss", boss: 644));
            sink.Report(Hang("Maps", "map", 500));
            Eventually(() => feature.Aggregator!.Snapshot().Count == 2 && Interlocked.Read(ref runFailedOccurrences) >= 2);

            Dictionary<(string Kind, string State), long> gauge = FindingsOf(feature, out bool registered);
            Assert.True(registered);
            Assert.Equal(QaKinds.All.Count * Enum.GetValues<QaPublicationState>().Length, gauge.Count);
            Assert.Equal(1, gauge[("run-failed", "pending")]);
            Assert.Equal(1, gauge[("tick-hang", "pending")]);
            Assert.Equal(0, gauge[("run-failed", "published")]);
            Assert.Equal(0, gauge[("stall", "pending")]);
            Assert.Equal(2, Interlocked.Read(ref runFailedOccurrences));

            // The exporter's store sees the same instrument with exactly the two bounded labels.
            Assert.Contains(store.Collect(), m => m.Name == "arcanecore.botqa.findings" && m.Kind == MetricKind.Gauge
                && m.Series.Any(s => s.Tags.Select(t => t.Key).Order().SequenceEqual(["kind", "state"]) && s.Tags.Any(t => t.Key == "kind" && t.Value == "run-failed")));
            Assert.Contains(store.Collect(), m => m.Name == "arcanecore.botqa.occurrences" && m.Kind == MetricKind.Counter
                && m.Series.Any(s => s.Tags.Count == 1 && s.Tags[0].Key == "kind"));
        }
        finally
        {
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task WithMetricsOff_NoFindingsGaugeIsRegistered()
    {
        (ServiceProvider services, PlayerbotQaSignalBus bus) = Build(metrics: false);
        using ServiceProvider _ = services;
        var feature = new PlayerbotQaFeature(services);
        feature.Attach(null!);
        try
        {
            new PlayerbotQaSink(bus).Report(Run("wiped", "boss"));
            Eventually(() => feature.Aggregator!.Snapshot().Count == 1);
            FindingsOf(feature, out bool registered);
            Assert.False(registered);
        }
        finally
        {
            await feature.StopAsync();
        }
    }

    [Fact]
    public void TheObservedGauge_ListsEveryKindAndStateWithAZero_ForNoFindings()
    {
        var measurements = PlayerbotQaMeters.Observe([]).ToList();
        Assert.Equal(QaKinds.All.Count * Enum.GetValues<QaPublicationState>().Length, measurements.Count);
        Assert.All(measurements, m => Assert.Equal(0, m.Value));
        Assert.Contains(measurements, m => m.Tags.ToArray().Any(t => t.Key == "kind" && (string?)t.Value == "run-failed")
            && m.Tags.ToArray().Any(t => t.Key == "state" && (string?)t.Value == "knownclosed"));
    }
}
