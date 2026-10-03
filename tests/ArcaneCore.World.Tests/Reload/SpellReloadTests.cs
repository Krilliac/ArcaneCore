using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload spell_template</c> (vmangos ServerCommands.cpp:1409 → SpellMgr::LoadSpells,
/// SpellMgr.cpp:3702): the store is rebuilt from the content tables off the world thread and
/// swapped on it; an empty table keeps what is loaded.
/// </summary>
public sealed class SpellReloadTests
{
    private const uint Added = 9100;

    private static SpellContent WithExtraSpell(SpellContent content) => content with
    {
        Spells = [.. content.Spells, new SpellTemplateRow { Id = Added, SpellName = "Added By Reload", RangeIndex = 1, Effect1 = 10, EffectBasePoints1 = 4, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1 }],
    };

    private static (ReloadCoordinator Coordinator, MutableSpellContentStore Store, SpellFeature Feature) Reloader(WorldTestHost host)
    {
        var store = new MutableSpellContentStore(Content());
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(host.WorldServices.GetRequiredService<SpellFeature>())
            .AddSingleton<ISpellContentStore>(store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellContentReloadable(services));
        return (coordinator, store, host.WorldServices.GetRequiredService<SpellFeature>());
    }

    [Fact]
    public async Task Reload_SwapsTheStore_OnTheWorldThread_AndNewSpellsResolve()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        SpellStore before = feature.System.Store;
        Assert.Null(before.Get(Added));
        content.Content = WithExtraSpell(Content());

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.NotSame(before, feature.System.Store);
        Assert.Equal("Added By Reload", feature.System.Store.Get(Added)?.Name);
        Assert.Equal(before.Count + 1, feature.System.Store.Count);
        Assert.Contains($"{before.Count + 1} spells", result.Message);
    }

    [Fact]
    public async Task AnEmptyTable_KeepsTheLoadedStore()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        SpellStore before = feature.System.Store;
        Assert.True(before.Count > 0);
        content.Content = SpellContent.Empty;

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Same(before, feature.System.Store);
        Assert.Contains("empty", result.Message);
    }

    [Fact]
    public async Task AnEmptyTable_OnAnEmptyStore_IsHarmless()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        await host.OnWorldAsync(() => feature.System.Store = SpellStore.Empty);
        content.Content = SpellContent.Empty;

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(0, feature.System.Store.Count);
    }

    [Fact]
    public async Task AFailingRead_LeavesTheStoreUntouched()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        SpellStore before = feature.System.Store;
        content.Failure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Same(before, feature.System.Store);
    }

    [Fact]
    public async Task DuplicateSpellIds_AreRejected_AndTheStoreIsUntouched()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        SpellStore before = feature.System.Store;
        content.Content = WithExtraSpell(WithExtraSpell(Content()));

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains(result.Notes, n => n.Contains(Added.ToString(), StringComparison.Ordinal));
        Assert.Same(before, feature.System.Store);
    }

    [Fact]
    public async Task AnAuraAlreadyRunning_SurvivesTheReload()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("AURAHOLDER", "Auraholder");
        (ReloadCoordinator coordinator, MutableSpellContentStore content, SpellFeature feature) = Reloader(host);
        await host.OnWorldAsync(() =>
        {
            Game.Entities.Player player = host.World.FindOnlinePlayer("Auraholder")!;
            feature.System.CastSpell(player, Renew, ArcaneCore.Game.Spells.SpellCastTargets.ForSelf(), triggered: true);
        });
        Assert.True(await host.OnWorldAsync(() => feature.System.HasAura(host.World.FindOnlinePlayer("Auraholder")!, Renew)));
        content.Content = WithExtraSpell(Content());

        ReloadResult result = await coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(await host.OnWorldAsync(() => feature.System.HasAura(host.World.FindOnlinePlayer("Auraholder")!, Renew)));
    }

    [Fact]
    public async Task TheReloadFeature_RegistersSpellTemplate_ThroughDiscovery()
    {
        await using var host = WorldTestHost.Start();
        ReloadFeature reload = host.WorldServices.GetRequiredService<ReloadFeature>();

        Assert.Contains("spell_template", reload.Coordinator.Names);

        ReloadResult result = await reload.Coordinator.ReloadAsync("spell_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
    }

    private sealed class MutableSpellContentStore(SpellContent content) : ISpellContentStore
    {
        public SpellContent Content { get; set; } = content;

        public Exception? Failure { get; set; }

        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(Content) : Task.FromException<SpellContent>(Failure);

        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
