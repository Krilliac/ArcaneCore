using ArcaneCore.Game.Reload;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Reload;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <c>.reload game_weather</c> (vmangos ServerCommands.cpp:1813-1818 → <c>LoadWeatherZoneChances</c>, Weather.cpp:446-505): the
/// table is overlaid on the loaded chances; vmangos never clears the map, so a zone that left the table keeps its row.
/// </summary>
public sealed class WeatherReloadTests
{
    private sealed class FixedStore(params GameWeatherRecord[] rows) : IWorldStateDataStore
    {
        public Exception? Failure { get; set; }

        public Task<WorldStateContent> LoadAsync(CancellationToken cancellationToken = default)
            => Failure is null ? Task.FromResult(new WorldStateContent(rows, [])) : Task.FromException<WorldStateContent>(Failure);
    }

    private static GameWeatherRecord Zone(uint zone, uint springRain, uint springSnow = 0)
        => new(zone, [springRain, springSnow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static (ReloadCoordinator Coordinator, WorldStateHooks Hooks, WeatherFeature Feature) Reloader(WorldTestHost host, FixedStore store)
    {
        WeatherFeature feature = host.WorldServices.GetRequiredService<WeatherFeature>();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(feature)
            .AddScoped<IWorldStateDataStore>(_ => store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new GameWeatherReloadable(services));
        return (coordinator, WorldStateHooks.For(host.World), feature);
    }

    [Fact]
    public async Task Reload_OverlaysTheTable_ListedZonesChange_UnlistedZonesKeepTheirRows()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, WorldStateHooks hooks, WeatherFeature feature) = Reloader(host, new FixedStore(Zone(12, 0, 30), Zone(99, 50)));
        await host.OnWorldAsync(() => feature.ReplaceChances([Zone(12, 100), Zone(40, 0, 70)]));
        Assert.Equal(100u, hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Rain);

        ReloadResult result = await coordinator.ReloadAsync("game_weather");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("2 weather definitions", result.Message, StringComparison.Ordinal);
        Assert.Equal((0u, 30u), (hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Rain, hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Snow));
        Assert.Equal(50u, hooks.WeatherChances.Get(99)![WeatherSeason.Spring].Rain);
        Assert.Equal(70u, hooks.WeatherChances.Get(40)![WeatherSeason.Spring].Snow); // not in the table any more: kept, as vmangos does
        Assert.Contains(result.Notes, n => n.Contains("1 zone(s) are no longer in game_weather", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reload_AChanceAbove100_BecomesTwentyFive_AndIsReported()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, WorldStateHooks hooks, _) = Reloader(host, new FixedStore(Zone(12, 150)));

        ReloadResult result = await coordinator.ReloadAsync("game_weather");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(25u, hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Rain);
        Assert.Contains(result.Notes, n => n.Contains("zone 12 season 0 has wrong rain chance > 100%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reload_AnEmptyTable_ChangesNothing_AsVmangosLogsAndReturns()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, WorldStateHooks hooks, WeatherFeature feature) = Reloader(host, new FixedStore());
        await host.OnWorldAsync(() => feature.ReplaceChances([Zone(12, 100)]));

        ReloadResult result = await coordinator.ReloadAsync("game_weather");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(1, hooks.WeatherChances.Count);
        Assert.Equal(100u, hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Rain);
        Assert.Contains(result.Notes, n => n.Contains("game_weather is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reload_ARowWithTheWrongColumnCount_IsRejected_NamingTheZone()
    {
        await using var host = WorldTestHost.Start();
        (ReloadCoordinator coordinator, WorldStateHooks hooks, WeatherFeature feature) = Reloader(host, new FixedStore(new GameWeatherRecord(77, [1, 2, 3])));
        await host.OnWorldAsync(() => feature.ReplaceChances([Zone(12, 100)]));

        ReloadResult result = await coordinator.ReloadAsync("game_weather");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains("zone 77", result.Message + string.Join(' ', result.Notes), StringComparison.Ordinal);
        Assert.Equal(1, hooks.WeatherChances.Count);
        Assert.Null(hooks.WeatherChances.Get(77));
    }

    [Fact]
    public async Task Reload_AFailingStore_ChangesNothing()
    {
        await using var host = WorldTestHost.Start();
        var store = new FixedStore(Zone(12, 0)) { Failure = new InvalidOperationException("world database unavailable") };
        (ReloadCoordinator coordinator, WorldStateHooks hooks, WeatherFeature feature) = Reloader(host, store);
        await host.OnWorldAsync(() => feature.ReplaceChances([Zone(12, 100)]));

        ReloadResult result = await coordinator.ReloadAsync("game_weather");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Equal(100u, hooks.WeatherChances.Get(12)![WeatherSeason.Spring].Rain);
    }

    [Fact]
    public async Task TheReloadFeature_RegistersGameWeather_ThroughDiscovery()
    {
        await using var host = WorldTestHost.Start();

        Assert.Contains("game_weather", host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names);
    }
}
