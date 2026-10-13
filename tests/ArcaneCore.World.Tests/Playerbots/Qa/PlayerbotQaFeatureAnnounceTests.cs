using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The feature hands the aggregator's new findings to the registered <see cref="IOpsAnnouncer"/> (World:Announce:BotQa), staff only.</summary>
public sealed class PlayerbotQaFeatureAnnounceTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    /// <summary>Thread-safe: the feature's own background loop may drain the bus on its task.</summary>
    private sealed class ConcurrentSink : IAnnounceSink
    {
        private readonly List<AnnounceDelivery> _sent = [];

        public IReadOnlyList<AnnounceDelivery> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public void Deliver(AnnounceDelivery delivery)
        {
            lock (_sent)
            {
                _sent.Add(delivery);
            }
        }
    }

    private (PlayerbotQaFeature Feature, PlayerbotQaSignalBus Bus, ServiceProvider Services) Start(IOpsAnnouncer? announcer)
    {
        var bus = new PlayerbotQaSignalBus(64);
        ServiceCollection collection = new();
        collection.AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(new PlayerbotQaOptions { Directory = _directory }));
        collection.AddSingleton(bus);
        if (announcer is not null)
        {
            collection.AddSingleton(announcer);
        }

        ServiceProvider services = collection.BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services) { Time = new ManualTime(QaData.T0) };
        feature.Attach(null!);
        Assert.NotNull(feature.Aggregator);
        return (feature, bus, services);
    }

    [Fact]
    public async Task ANewFinding_FromADrainedSignal_IsAnnouncedToStaffWithTheBotQaTag_AndNotToThePlayers()
    {
        var sink = new ConcurrentSink();
        var announcer = new OpsAnnouncer(new AnnounceOptions(), sink, NullLogger.Instance, () => 1_000_000);
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ServiceProvider services) = Start(announcer);
        using (services)
        {
            try
            {
                Assert.NotNull(feature.Aggregator!.FindingCreated);
                Assert.True(bus.TryWrite(QaData.Relay(7)));
                feature.Drain();
                await WaitFor(() => sink.Sent.Count > 0);

                AnnounceDelivery sent = Assert.Single(sink.Sent);
                Assert.Contains("[BotQa]", sent.StaffChat);
                Assert.Contains("RelayScriptMissing", sent.StaffChat);
                Assert.Null(sent.WorldChat);

                // The same finding met again is not announced a second time.
                Assert.True(bus.TryWrite(QaData.Relay(7)));
                feature.Drain();
                Assert.Single(sink.Sent);
                Assert.Equal(1, feature.Aggregator.Count);
            }
            finally
            {
                await feature.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    [Fact]
    public async Task WithNoAnnouncerRegistered_FindingsAreStillFolded_AndNoSubscriberIsAttached()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ServiceProvider services) = Start(announcer: null);
        using (services)
        {
            try
            {
                Assert.Null(feature.Aggregator!.FindingCreated);
                Assert.True(bus.TryWrite(QaData.Relay(7)));
                feature.Drain();
                await WaitFor(() => feature.Aggregator.Count == 1);
                Assert.Equal(1, feature.Aggregator.Count);
            }
            finally
            {
                await feature.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
