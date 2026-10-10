using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Metrics;
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
        Assert.Contains(metrics, m => m.Name == "arcanecore.map.creatures" && m.Series.Any(s => s.Tags.Any(t => t.Key == "map")));
    }
}
