using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Names;
using ArcaneCore.World.Names;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

public sealed class ReservedNameReloadTests
{
    [Fact]
    public async Task ReloadReplacesSqlExactNamesAndKeepsCatalogReferenceAtomic()
    {
        var store = new MutableReservedNameStore(["oldname"]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IReservedNameStore>(store));
        NameCatalogFeature feature = host.WorldServices.GetRequiredService<NameCatalogFeature>();
        var coordinator = new ReloadCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new ReservedNameContentReloadable(host.WorldServices));
        NameCatalog before = feature.Catalog;
        store.Names = new HashSet<string>(["newname"], StringComparer.Ordinal);

        ReloadResult result = await coordinator.ReloadAsync("reserved_name");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(NameCatalogResult.Allowed, feature.Catalog.Check("oldname"));
        Assert.Equal(NameCatalogResult.Reserved, feature.Catalog.Check("newname"));
        Assert.NotSame(before, feature.Catalog);
    }

    [Fact]
    public async Task FailedReadLeavesTheCompleteCatalogUntouched()
    {
        var store = new MutableReservedNameStore(["oldname"]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IReservedNameStore>(store));
        NameCatalogFeature feature = host.WorldServices.GetRequiredService<NameCatalogFeature>();
        var coordinator = new ReloadCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new ReservedNameContentReloadable(host.WorldServices));
        NameCatalog before = feature.Catalog;
        store.Failure = new InvalidOperationException("reserved_name unavailable");

        ReloadResult result = await coordinator.ReloadAsync("reserved_name");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Same(before, feature.Catalog);
        Assert.Equal(NameCatalogResult.Reserved, feature.Catalog.Check("oldname"));
    }

    [Fact]
    public async Task CandidateFreezesProducerAndTransactionRollbackRestoresPreviousCatalog()
    {
        var store = new MutableReservedNameStore(["frozen"]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IReservedNameStore>(store));
        NameCatalogFeature feature = host.WorldServices.GetRequiredService<NameCatalogFeature>();
        var reloadable = new ReservedNameContentReloadable(host.WorldServices);
        NameCatalog before = feature.Catalog;

        ContentCandidate candidate = await reloadable.BuildAsync(CancellationToken.None);
        store.Backing.Clear();
        var transaction = new ReloadTransaction();
        await host.OnWorldAsync(() => candidate.Commit(host.World, transaction));

        Assert.Equal(NameCatalogResult.Reserved, feature.Catalog.Check("frozen"));
        Assert.Empty(transaction.Rollback());
        Assert.Same(before, feature.Catalog);
    }

    private sealed class MutableReservedNameStore(IEnumerable<string> names) : IReservedNameStore
    {
        public HashSet<string> Backing { get; } = names.ToHashSet(StringComparer.Ordinal);
        public IReadOnlySet<string> Names { get => Backing; set { Backing.Clear(); Backing.UnionWith(value); } }
        public Exception? Failure { get; set; }

        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(Names) : Task.FromException<IReadOnlySet<string>>(Failure);
    }
}
