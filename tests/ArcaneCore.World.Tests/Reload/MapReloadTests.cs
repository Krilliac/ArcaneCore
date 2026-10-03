using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.GridTerrain.InMemoryMapDataStore;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload game_tele</c> and <c>.reload areatrigger_teleport</c> (vmangos ServerCommands.cpp:1638 and
/// :1032): the teleport locations and the area-trigger tables are rebuilt off the world thread and
/// swapped on it; the map registry, area table and terrain are left alone.
/// </summary>
public sealed class MapReloadTests
{
    private static (ReloadCoordinator Coordinator, FixedMapStore Store, WorldMaps Maps) Reloader(WorldTestHost host, MapContent content)
    {
        var store = new FixedMapStore(content);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(host.WorldServices.GetRequiredService<TeleportFeature>())
            .AddSingleton<IMapDataStore>(store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new GameTeleContentReloadable(services));
        coordinator.Register(new AreaTriggerTeleportContentReloadable(services));
        return (coordinator, store, host.WorldServices.GetRequiredService<TeleportFeature>().Maps);
    }

    [Fact]
    public async Task GameTele_ReplacesTheLocations_AndLeavesTheMapRegistryAlone()
    {
        await using var host = WorldTestHost.Start();
        MapContent changed = Content with { GameTeles = [.. Content.GameTeles, new GameTele(4, 9947.52f, 2482.73f, 1316.21f, 0f, 1, "Darnassus")] };
        (ReloadCoordinator coordinator, _, WorldMaps maps) = Reloader(host, changed);
        var registry = maps.Registry;
        Assert.Null(maps.FindGameTele("Darnassus"));

        ReloadResult result = await coordinator.ReloadAsync("game_tele");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(4u, maps.FindGameTele("Darnassus")?.Id);
        Assert.Equal(Content.GameTeles.Count + 1, maps.GameTeles.Count);
        Assert.Same(registry, maps.Registry);
        Assert.Contains($"{Content.GameTeles.Count + 1} teleport locations", result.Message);
    }

    [Fact]
    public async Task GameTele_Empty_KeepsTheLoadedLocations()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, _, WorldMaps maps) = Reloader(host, Content with { GameTeles = [] });

        ReloadResult result = await coordinator.ReloadAsync("game_tele");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal(Content.GameTeles.Count, maps.GameTeles.Count);
        Assert.NotNull(maps.FindGameTele("Stormwind"));
    }

    [Fact]
    public async Task AreaTriggerTeleport_SwapsTheTargets_AndListsTheRejectedRows()
    {
        await using var host = WorldTestHost.Start();
        AreaTriggerTeleport moved = new(ShortcutTrigger, "Shortcut", "", 0, 1, -441.8f, -2596.08f, 96.2155f, 1f);
        const uint StrandedTrigger = 81;
        MapContent changed = Content with
        {
            AreaTriggers = [.. Content.AreaTriggers, new AreaTriggerTemplate(StrandedTrigger, 0, -8940f, -140f, 83.5f, 3, 0, 0, 0, 0, "Stranded")],
            AreaTriggerTeleports =
            [
                Content.AreaTriggerTeleports[0],
                moved,
                new AreaTriggerTeleport(OrphanTeleport, "No trigger row", "", 0, 0, 1, 1, 1, 0),
                new AreaTriggerTeleport(StrandedTrigger, "Unknown map", "", 0, 99, 1, 1, 1, 0),
            ],
        };
        (ReloadCoordinator coordinator, _, WorldMaps maps) = Reloader(host, changed);
        Assert.Equal(0u, maps.FindAreaTriggerTeleport(ShortcutTrigger)?.TargetMap);

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_teleport");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(1u, maps.FindAreaTriggerTeleport(ShortcutTrigger)?.TargetMap);
        Assert.Equal(2, maps.AreaTriggerTeleportCount);
        Assert.Contains(result.Notes, n => n.Contains($"areatrigger_teleport {OrphanTeleport}: no areatrigger_template row", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("unknown target map 99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AreaTriggerTeleport_WithNoUsableRows_KeepsTheLoadedOnes()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, _, WorldMaps maps) = Reloader(host, Content with { AreaTriggerTeleports = [] });
        int before = maps.AreaTriggerTeleportCount;
        Assert.True(before > 0);

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_teleport");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal(before, maps.AreaTriggerTeleportCount);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, FixedMapStore store, WorldMaps maps) = Reloader(host, Content);
        store.Failure = new InvalidOperationException("world database unavailable");
        int before = maps.GameTeles.Count;

        ReloadResult result = await coordinator.ReloadAsync("game_tele");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Equal(before, maps.GameTeles.Count);
    }

    [Fact]
    public async Task TheReloadFeature_RegistersBothMapReloads_ThroughDiscovery()
    {
        await using var host = WorldTestHost.Start();
        IReadOnlyList<string> names = host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names;

        Assert.Contains("game_tele", names);
        Assert.Contains("areatrigger_teleport", names);

        ReloadResult result = await host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.ReloadAsync("areatrigger_teleport");

        Assert.Equal(ReloadStatus.Applied, result.Status);
    }

    private sealed class FixedMapStore(MapContent content) : IMapDataStore
    {
        public Exception? Failure { get; set; }

        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(content) : Task.FromException<MapContent>(Failure);
    }
}
