using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.Tuning;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Tuning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Tuning;

/// <summary>
/// The tuning tables in the world daemon: <see cref="TuningFeature"/> seeds empty tables and loads the rows at start, and
/// <c>.reload tuning</c> swaps a valid set in and rejects a set with any bad row (the active rows stay). Each test restores the
/// catalog that was active, since it is process-wide.
/// </summary>
public sealed class TuningWorldTests
{
    private sealed class MutableStore(TuningContent content) : ITuningContentStore
    {
        public TuningContent Content { get; set; } = content;

        public int SeedCalls { get; private set; }

        public Task<TuningContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Content);

        public Task<int> SeedEmptyTablesAsync(TuningContent defaults, CancellationToken cancellationToken = default)
        {
            SeedCalls++;
            if (Content.RowCount == 0)
            {
                Content = defaults;
                return Task.FromResult(defaults.RowCount);
            }

            return Task.FromResult(0);
        }
    }

    private static TuningContent WithPoorPercent(int percent) => TuningDefaults.Content with
    {
        Values = [.. TuningDefaults.Content.Values.Select(v => v.Quality == 0 && v.ItemClass == 0 ? v with { Percent = percent } : v)],
    };

    private static (TuningFeature Feature, ReloadCoordinator Coordinator, MutableStore Store) Start(WorldTestHost host, TuningContent content)
    {
        var store = new MutableStore(content);
        ServiceCollection services = new();
        services.AddSingleton<ITuningContentStore>(store);
        ServiceProvider provider = services.BuildServiceProvider();
        var feature = new TuningFeature(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TuningFeature>.Instance);
        feature.Attach(host.World);
        ServiceProvider withFeature = services.AddSingleton(feature).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new TuningContentReloadable(withFeature));
        return (feature, coordinator, store);
    }

    [Fact]
    public async Task Attach_SeedsEmptyTables_AndMakesTheRowsTheActiveCatalog()
    {
        TuningCatalog before = TuningCatalog.Active;
        try
        {
            await using var host = WorldTestHost.Start();
            (TuningFeature feature, _, MutableStore store) = Start(host, TuningContent.Empty);

            Assert.Equal(1, store.SeedCalls);
            Assert.Equal(TuningDefaults.Content.RowCount, store.Content.RowCount);
            Assert.Same(TuningCatalog.Active, feature.Catalog);
            Assert.Empty(feature.Catalog.Rejected);
            Assert.Equal(TuningCatalog.Shipped.ValueMatrix(), feature.Catalog.ValueMatrix());
        }
        finally
        {
            TuningCatalog.Replace(before);
        }
    }

    [Fact]
    public async Task ReloadTuning_ReplacesTheActiveCatalog()
    {
        TuningCatalog before = TuningCatalog.Active;
        try
        {
            await using var host = WorldTestHost.Start();
            (TuningFeature feature, ReloadCoordinator coordinator, MutableStore store) = Start(host, TuningDefaults.Content);
            Assert.Equal(0u, feature.Catalog.Value(0, 0));
            store.Content = WithPoorPercent(55);

            ReloadResult result = await coordinator.ReloadAsync("tuning");

            Assert.Equal(ReloadStatus.Applied, result.Status);
            Assert.Equal(55u, TuningCatalog.Active.Value(0, 0));
        }
        finally
        {
            TuningCatalog.Replace(before);
        }
    }

    [Fact]
    public async Task ReloadTuning_WithABadRow_IsRejected_AndKeepsTheActiveRows()
    {
        TuningCatalog before = TuningCatalog.Active;
        try
        {
            await using var host = WorldTestHost.Start();
            (_, ReloadCoordinator coordinator, MutableStore store) = Start(host, TuningDefaults.Content);
            TuningCatalog active = TuningCatalog.Active;
            store.Content = TuningDefaults.Content with { Ships = [new ShipRouteRecord(5, 9, 0, "bad means")] };

            ReloadResult result = await coordinator.ReloadAsync("tuning");

            Assert.Equal(ReloadStatus.Rejected, result.Status);
            Assert.Same(active, TuningCatalog.Active);
            Assert.Contains(result.Notes, n => n.StartsWith("playerbot_ship_route 5:", StringComparison.Ordinal));
        }
        finally
        {
            TuningCatalog.Replace(before);
        }
    }
}
