using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Metrics;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The world instruments of Ops:Metrics (docs/ops/metrics.md) on a real world thread.</summary>
public sealed class WorldMetricsFeatureTests
{
    [Fact]
    public void FeatureIsDiscovered_AndAttachesNothingWhenDisabled()
    {
        Assert.Contains(typeof(WorldMetricsFeature), WorldFeatures.FeatureTypes);
        using var provider = new ServiceCollection().AddSingleton(new MetricsOptions()).BuildServiceProvider();
        using var feature = new WorldMetricsFeature(provider, NullLogger<WorldMetricsFeature>.Instance);
        Assert.Empty(feature.Samples);
    }

    [Fact]
    public void AddWorldMetrics_BindsTheWorldPrefix_NotTheRealmOne()
    {
        IConfiguration shared = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "true",
            ["Ops:Metrics:RealmPrometheusPrefix"] = "http://+:9601/metrics/",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddWorldMetrics(shared);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal("http://localhost:9464/metrics/", provider.GetRequiredService<MetricsHost>().ConfiguredPrometheusPrefix);

        IConfiguration overridden = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Metrics:PrometheusPrefix"] = "http://+:9600/metrics/",
        }).Build();
        var services2 = new ServiceCollection().AddLogging();
        services2.AddWorldMetrics(overridden);
        using ServiceProvider provider2 = services2.BuildServiceProvider();
        Assert.Equal("http://+:9600/metrics/", provider2.GetRequiredService<MetricsHost>().ConfiguredPrometheusPrefix);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task Enabled_RecordsTicksMapsAndPerMapSamples()
    {
        using var store = new MetricsStore();
        store.Start();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s =>
            s.AddSingleton(new MetricsOptions { Enabled = true, MapSampleIntervalSeconds = 1 }));
        var feature = host.WorldServices.GetRequiredService<WorldMetricsFeature>();
        host.World.Post(() => host.World.GetMap(0));
        await WorldTestHost.WaitForAsync(() => feature.Samples.Any(s => s.MapId == 0), "a sample of map 0");

        await WorldTestHost.WaitForAsync(
            () => store.Collect().Any(m => m.Name == "arcanecore.world.tick.duration" && m.Series.Any(s => s.Count >= 20)),
            "twenty recorded ticks");
        IReadOnlyList<MetricSnapshot> metrics = store.Collect();
        Assert.Contains(metrics, m => m.Name == "arcanecore.world.map_update.duration");
        Assert.Contains(metrics, m => m.Name == "arcanecore.world.ticks" && m.Series.Any(s => s.Value >= 20));
        Assert.Contains(metrics, m => m.Name == "arcanecore.world.sessions");
        Assert.Contains(metrics, m => m.Name == "arcanecore.db.save_queue_depth");
        Assert.Contains(metrics, m => m.Name == "arcanecore.nav.tile_installs");
        Assert.Contains(metrics, m => m.Name == "arcanecore.nav.tile_install_time" && m.Unit == "ms");
        Assert.Contains(metrics, m => m.Name == "arcanecore.db.save_queue_oldest_age" && m.Unit == "ms");
        Assert.Contains(metrics, m => m.Name == "arcanecore.map.creatures" && m.Series.Any(s => s.Tags.Any(t => t.Key == "map")));
    }

    /// <summary>A scope factory whose scopes cannot be created until released: the queue consumer parks in front of the store.</summary>
    private sealed class GatedScopeFactory(IServiceScopeFactory inner, ManualResetEventSlim gate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return inner.CreateScope();
        }
    }

    [Fact]
    public async Task SaveQueueOldestAgeGauge_GrowsWhileASnapshotIsStuck_AndReturnsToZeroWhenItDrains()
    {
        using var gate = new ManualResetEventSlim(false);
        using var store = new MetricsStore();
        store.Start();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s =>
        {
            s.AddSingleton(new MetricsOptions { Enabled = true, MapSampleIntervalSeconds = 1 });
            s.AddSingleton(sp => new CharacterSaveQueue(
                new GatedScopeFactory(sp.GetRequiredService<IServiceScopeFactory>(), gate),
                NullLogger<CharacterSaveQueue>.Instance));
        });
        double Age() => store.Collect().Single(m => m.Name == "arcanecore.db.save_queue_oldest_age").Series.Single().Value;
        double Depth() => store.Collect().Single(m => m.Name == "arcanecore.db.save_queue_depth").Series.Single().Value;

        await WorldTestHost.WaitForAsync(() => store.Collect().Any(m => m.Name == "arcanecore.db.save_queue_oldest_age"), "the gauge");
        Assert.Equal(0, Age()); // nothing queued: zero, not a stale or negative age
        Assert.Equal(0, Depth());

        host.SaveQueue.Enqueue(new ArcaneCore.Kernel.Characters.CharacterState(1, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: 5));
        await Task.Delay(120);
        Assert.Equal(1, Depth());
        Assert.True(Age() >= 100, $"oldest age {Age()} ms while the only snapshot is stuck");

        gate.Set();
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the queue draining");
        Assert.Equal(0, Age());
    }
}
