using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>
/// The weather calls of vmangos <c>Map</c> that scripts and creature AI use (<c>Map::SetWeather</c>, Map.cpp:2039-2043; the
/// Scourge invasion and Ossirian scripts call it with <c>permanent = true</c>, scripts/world/scourge_invasion.cpp,
/// boss_ossirian.cpp). They operate on the map's <see cref="MapWeather"/> updater, so every map, instances included, has
/// its own weather. World thread.
/// </summary>
public static class MapWeatherExtensions
{
    /// <summary>
    /// Set the weather of <paramref name="zone"/> on this map (vmangos <c>Map::SetWeather</c>): the weather object is created
    /// when the zone has none, the permanent flag is stored, and the zone's players get one SMSG_WEATHER unless the type and
    /// grade did not change. A permanent weather never regenerates. Not guarded by <c>World:Weather:Enabled</c>: vmangos
    /// guards only <c>.wchange</c> and zone entry.
    /// </summary>
    public static void SetWeather(this Map map, uint zone, WeatherType type, float grade, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(map);
        (map.FindUpdater<MapWeather>() ?? throw new InvalidOperationException($"map {map.MapId} has no weather updater")).SetWeather(zone, type, grade, permanent);
    }

    /// <summary>The weather of <paramref name="zone"/> on this map, or null when the zone has none yet (nobody entered it and no script set it).</summary>
    public static WeatherState? GetWeather(this Map map, uint zone)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.FindUpdater<MapWeather>()?.Find(zone);
    }
}
