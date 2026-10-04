using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Threat;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Threat;

/// <summary>
/// The spell_threat feature (startup load) and <c>.reload spell_threats</c> (vmangos SpellMgr::LoadSpellThreats, Spells/SpellMgr.cpp:834-875:
/// the map is cleared first, so an empty table leaves no entries unless <c>HotReload:EmptyTables = KeepLoaded</c>).
/// </summary>
public sealed class SpellThreatReloadTests
{
    private static SpellThreatContent Content(params (uint Spell, ushort Threat)[] rows)
        => new(rows.Select(r => new SpellThreatRecord(r.Spell, r.Threat, 1f, 0)));

    private static (ReloadCoordinator Coordinator, SpellThreatFeature Feature, MutableStore Store) Reloader(
        WorldTestHost host, SpellThreatContent initial, EmptyTablePolicy empty = EmptyTablePolicy.Retail)
    {
        var store = new MutableStore(initial);
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new HotReloadOptions { EmptyTables = empty }))
            .AddSingleton<ISpellThreatDataStore>(store);
        ServiceProvider provider = services.BuildServiceProvider();
        var feature = new SpellThreatFeature(provider, NullLogger<SpellThreatFeature>.Instance);
        feature.Attach(host.World);
        ServiceProvider withFeature = services.AddSingleton(feature).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellThreatReloadable(withFeature));
        return (coordinator, feature, store);
    }

    [Fact]
    public async Task Attach_LoadsTheTableFromTheStore_AndWithoutAStoreTheTableIsEmpty()
    {
        await using var host = WorldTestHost.Start();
        (_, SpellThreatFeature feature, _) = Reloader(host, Content((72, 180), (8092, 0)));

        Assert.Equal(180, feature.Table.Find(72)!.Threat);
        Assert.Equal(2, feature.Table.Count);

        var bare = new SpellThreatFeature(new ServiceCollection().BuildServiceProvider(), NullLogger<SpellThreatFeature>.Instance);
        bare.Attach(host.World);
        Assert.Equal(0, bare.Table.Count);
        Assert.Null(bare.Table.Find(72));
    }

    [Fact]
    public async Task Reload_ReplacesTheRows()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, SpellThreatFeature feature, MutableStore store) = Reloader(host, Content((72, 180)));
        store.Content = Content((72, 200), (78, 20));

        ReloadResult result = await coordinator.ReloadAsync("spell_threats");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(200, feature.Table.Find(72)!.Threat);
        Assert.Equal(20, feature.Table.Find(78)!.Threat);
        Assert.Contains("2 spell threat entries", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_OfAnEmptyTable_EmptiesIt_AsVmangosDoes()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, SpellThreatFeature feature, MutableStore store) = Reloader(host, Content((72, 180)));
        store.Content = SpellThreatContent.Empty;

        ReloadResult result = await coordinator.ReloadAsync("spell_threats");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(0, feature.Table.Count);
    }

    [Fact]
    public async Task Reload_OfAnEmptyTable_KeepsTheLoadedRows_WhenTheOptionSaysKeepLoaded()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, SpellThreatFeature feature, MutableStore store) = Reloader(host, Content((72, 180)), EmptyTablePolicy.KeepLoaded);
        store.Content = SpellThreatContent.Empty;

        ReloadResult result = await coordinator.ReloadAsync("spell_threats");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal(180, feature.Table.Find(72)!.Threat);
    }

    [Fact]
    public async Task TheReloadFeature_RegistersSpellThreat_ThroughDiscovery()
    {
        await using var host = WorldTestHost.Start();

        Assert.Contains("spell_threats", host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
        Assert.NotNull(host.WorldServices.GetRequiredService<SpellThreatFeature>());
    }

    private sealed class MutableStore(SpellThreatContent content) : ISpellThreatDataStore
    {
        public SpellThreatContent Content { get; set; } = content;

        public Task<SpellThreatContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Content);
    }
}
