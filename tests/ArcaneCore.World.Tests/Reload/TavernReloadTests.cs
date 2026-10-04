using ArcaneCore.Data.World.Rest;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Progression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.GridTerrain.InMemoryMapDataStore;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload areatrigger_tavern</c> (vmangos ReloadCommands.cpp:294 → ObjectMgr::LoadTavernAreaTriggers, ObjectMgr.cpp:1656-1700): the
/// inn triggers are read off the world thread and swapped on it; a row naming no known area trigger is skipped; an empty table empties
/// the set unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </summary>
public sealed class TavernReloadTests
{
    private static WorldTestHost StartHost() => WorldTestHost.Start(configureServices: services =>
        services.AddSingleton(new InMemoryTavernStore { Ids = { DeadminesTrigger } }));

    private static (ReloadCoordinator Coordinator, InMemoryTavernStore Store, TavernTriggers Taverns) Reloader(WorldTestHost host, EmptyTablePolicy empty = EmptyTablePolicy.Retail)
    {
        var store = new InMemoryTavernStore();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(Options.Create(new HotReloadOptions { EmptyTables = empty }))
            .AddSingleton(host.WorldServices.GetRequiredService<RestFeature>())
            .AddSingleton(host.WorldServices.GetRequiredService<TeleportFeature>())
            .AddSingleton<IAreaTriggerTavernStore>(store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new TavernContentReloadable(services));
        return (coordinator, store, host.WorldServices.GetRequiredService<RestFeature>().Taverns);
    }

    [Fact]
    public async Task TheStartupLoad_ReadsTheTable()
    {
        await using WorldTestHost host = StartHost();
        TavernTriggers taverns = host.WorldServices.GetRequiredService<RestFeature>().Taverns;
        Assert.Equal([DeadminesTrigger], taverns.Ids);
        Assert.True(taverns.Contains(DeadminesTrigger));
        Assert.False(taverns.Contains(ShortcutTrigger));
    }

    [Fact]
    public async Task Reload_ReplacesTheSet_AndSkipsRowsThatNameNoAreaTrigger()
    {
        await using WorldTestHost host = StartHost();
        (ReloadCoordinator coordinator, InMemoryTavernStore store, TavernTriggers taverns) = Reloader(host);
        store.Ids.AddRange([ShortcutTrigger, 4242]);

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_tavern");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal([ShortcutTrigger], taverns.Ids);
        Assert.False(taverns.Contains(DeadminesTrigger));
        Assert.False(taverns.Contains(4242));
        Assert.Contains("1 inn triggers (1 rows name an unknown area trigger and were skipped)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_OfAnEmptyTable_EmptiesTheSet_AsVmangosDoes()
    {
        // ObjectMgr.cpp:1660 clears the set before the table is read.
        await using WorldTestHost host = StartHost();
        (ReloadCoordinator coordinator, _, TavernTriggers taverns) = Reloader(host);
        Assert.Equal(1, taverns.Count);

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_tavern");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(0, taverns.Count);
    }

    [Fact]
    public async Task Reload_OfAnEmptyTable_KeepsTheLoadedSet_WhenTheOptionSaysKeepLoaded()
    {
        await using WorldTestHost host = StartHost();
        (ReloadCoordinator coordinator, _, TavernTriggers taverns) = Reloader(host, EmptyTablePolicy.KeepLoaded);

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_tavern");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal([DeadminesTrigger], taverns.Ids);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        await using WorldTestHost host = StartHost();
        (ReloadCoordinator coordinator, InMemoryTavernStore store, TavernTriggers taverns) = Reloader(host);
        store.Ids.Add(ShortcutTrigger);
        store.Failure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await coordinator.ReloadAsync("areatrigger_tavern");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal([DeadminesTrigger], taverns.Ids);
    }

    [Fact]
    public void TheReloadable_IsPartOfAll_LikeVmangosAllArea()
        => Assert.True(((IContentReloadable)new TavernContentReloadable(new ServiceCollection().BuildServiceProvider())).IncludedInAll);
}
