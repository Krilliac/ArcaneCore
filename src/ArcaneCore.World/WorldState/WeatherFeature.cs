using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The world daemon's weather feature (docs/areas/world-state.md): binds <c>World:Weather</c>, loads
/// <c>game_weather</c> from the world database (when a store is registered) into
/// <see cref="WorldStateHooks.WeatherChances"/>, and is the zone-entry listener that sends a player
/// the zone's current weather after the world states (vmangos <c>Player::UpdateZone</c>,
/// Player.cpp:6594-6604). The per-map simulation is <see cref="MapWeather"/> (a default map updater).
/// </summary>
public sealed class WeatherFeature(IServiceProvider services, ILogger<WeatherFeature> logger) : IWorldFeature, IPlayerLocationListener
{
    private WorldStateHooks? _hooks;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _hooks = WorldStateHooks.For(world);
        services.GetService<IConfiguration>()?.GetSection(WeatherOptions.SectionName).Bind(_hooks.WeatherSettings);

        using IServiceScope scope = services.CreateScope();
        IWorldStateDataStore? store = scope.ServiceProvider.GetService<IWorldStateDataStore>();
        if (store is null)
        {
            logger.LogInformation("no world-state data store registered; zones have no weather (game_weather is empty)");
            return;
        }

        WorldStateContent content = store.LoadAsync().GetAwaiter().GetResult();
        ReplaceChances(content.Weather);
        logger.LogInformation("loaded {Count} weather definitions", content.Weather.Count);
    }

    /// <summary>
    /// Swap the weather chances (startup, and <c>.reload game_weather</c> once the reload coordinator
    /// calls it). A chance above 100 is replaced by 25 and logged, as vmangos does.
    /// </summary>
    public void ReplaceChances(IEnumerable<GameWeatherRecord> rows)
    {
        WorldStateHooks hooks = _hooks ?? throw new InvalidOperationException("the weather feature is not attached");
        hooks.WeatherChances.Replace(rows.Select(r => new KeyValuePair<uint, ZoneWeatherChances>(
            r.Zone, ZoneWeatherChances.FromColumns(r.Zone, r.Chances, message => logger.LogError("{Message}", message)))));
    }

    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
        if (_hooks is { WeatherSettings.Enabled: true } && player.Map?.FindUpdater<MapWeather>() is { } weather)
        {
            weather.SendTo(player, newZone);
        }
    }
}
