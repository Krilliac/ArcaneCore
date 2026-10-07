using ArcaneCore.Data.Content.Items;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

public sealed class ItemEnchantmentReloadTests
{
    [Fact]
    public async Task PresentEmptySqlCharges_ClearsThePublishedCatalog_AndUnknownRowsAreIgnored()
    {
        await using var host = WorldTestHost.Start();
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        uint knownSpell = await host.OnWorldAsync(() => feature.System.Store.All.First().Id);
        var store = new MutableChargesStore([new SpellEnchantCharges(knownSpell, 3), new SpellEnchantCharges(0xFFFF_FFFEu, 9)]);
        var services = new ServiceCollection()
            .AddSingleton(feature)
            .AddSingleton<ISpellEnchantChargesStore>(store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellEnchantChargesContentReloadable(services));

        ReloadResult applied = await coordinator.ReloadAsync("spell_enchant_charges");
        Assert.Equal(ReloadStatus.Applied, applied.Status);
        Assert.Equal((uint)3, feature.System.SpellEnchantCharges.Find(knownSpell));
        store.Rows = [];
        ReloadResult cleared = await coordinator.ReloadAsync("spell_enchant_charges");
        Assert.Equal(ReloadStatus.Applied, cleared.Status);
        Assert.Null(feature.System.SpellEnchantCharges.Find(knownSpell));
    }

    [Fact]
    public async Task ChargeStoreFailure_LeavesThePublishedCatalogUntouched()
    {
        await using var host = WorldTestHost.Start();
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        uint knownSpell = await host.OnWorldAsync(() => feature.System.Store.All.First().Id);
        var store = new MutableChargesStore([new SpellEnchantCharges(knownSpell, 4)]);
        var services = new ServiceCollection().AddSingleton(feature).AddSingleton<ISpellEnchantChargesStore>(store).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellEnchantChargesContentReloadable(services));
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("spell_enchant_charges")).Status);
        store.Failure = new InvalidOperationException("charges unavailable");
        ReloadResult result = await coordinator.ReloadAsync("spell_enchant_charges");
        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal((uint)4, feature.System.SpellEnchantCharges.Find(knownSpell));
    }

    [Fact]
    public async Task ScopedSqlStores_AreResolvedInsideBuildScopes_WithValidationEnabled_AndEmptyPpmClears()
    {
        await using var host = WorldTestHost.Start();
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        var state = new ScopedRowsState { Procs = [new ItemEnchantProc(70001, 2.5f)] };
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(feature)
            .AddSingleton(state)
            .AddScoped<IItemEnchantProcStore>(_ => new ScopedProcStore(state))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        var reloadable = new ItemEnchantProcContentReloadable(services);
        Assert.True(reloadable.IncludedInAll); // IServiceProviderIsService must not resolve the scoped store.
        coordinator.Register(reloadable);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("spell_proc_item_enchant")).Status);
        Assert.Equal(2.5f, feature.EnchantmentCatalogProvider!.Current.PpmRate(70001));
        state.Procs = [];
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("spell_proc_item_enchant")).Status);
        Assert.Null(feature.EnchantmentCatalogProvider.Current.PpmRate(70001));
        Assert.Equal(2, state.ProcDisposals);
    }

    [Fact]
    public async Task ScopedChargeStoreFailure_LeavesExactPublishedCatalogReferenceUntouched()
    {
        await using var host = WorldTestHost.Start();
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        uint knownSpell = await host.OnWorldAsync(() => feature.System.Store.All.First().Id);
        var state = new ScopedRowsState { Charges = [new SpellEnchantCharges(knownSpell, 8)] };
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(feature)
            .AddSingleton(state)
            .AddScoped<ISpellEnchantChargesStore>(_ => new ScopedChargeStore(state))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellEnchantChargesContentReloadable(services));
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("spell_enchant_charges")).Status);
        ISpellEnchantChargesCatalog published = feature.System.SpellEnchantCharges;
        state.Failure = new InvalidOperationException("scoped charge store unavailable");
        Assert.Equal(ReloadStatus.Failed, (await coordinator.ReloadAsync("spell_enchant_charges")).Status);
        Assert.Same(published, feature.System.SpellEnchantCharges);
        Assert.Equal(2, state.ChargeDisposals);
    }

    private sealed class MutableChargesStore(IReadOnlyList<SpellEnchantCharges> rows) : ISpellEnchantChargesStore
    {
        public IReadOnlyList<SpellEnchantCharges> Rows { get; set; } = rows;
        public Exception? Failure { get; set; }
        public Task<IReadOnlyList<SpellEnchantCharges>> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(Rows) : Task.FromException<IReadOnlyList<SpellEnchantCharges>>(Failure);
    }

    private sealed class ScopedRowsState
    {
        public IReadOnlyList<ItemEnchantProc> Procs { get; set; } = [];
        public IReadOnlyList<SpellEnchantCharges> Charges { get; set; } = [];
        public Exception? Failure { get; set; }
        public int ProcDisposals { get; set; }
        public int ChargeDisposals { get; set; }
    }

    private sealed class ScopedProcStore(ScopedRowsState state) : IItemEnchantProcStore, IAsyncDisposable
    {
        public Task<IReadOnlyList<ItemEnchantProc>> LoadAsync(CancellationToken cancellationToken = default)
            => state.Failure is null ? Task.FromResult(state.Procs.ToArray() as IReadOnlyList<ItemEnchantProc>)
                : Task.FromException<IReadOnlyList<ItemEnchantProc>>(state.Failure);

        public ValueTask DisposeAsync()
        {
            state.ProcDisposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopedChargeStore(ScopedRowsState state) : ISpellEnchantChargesStore, IAsyncDisposable
    {
        public Task<IReadOnlyList<SpellEnchantCharges>> LoadAsync(CancellationToken cancellationToken = default)
            => state.Failure is null ? Task.FromResult(state.Charges.ToArray() as IReadOnlyList<SpellEnchantCharges>)
                : Task.FromException<IReadOnlyList<SpellEnchantCharges>>(state.Failure);

        public ValueTask DisposeAsync()
        {
            state.ChargeDisposals++;
            return ValueTask.CompletedTask;
        }
    }
}
