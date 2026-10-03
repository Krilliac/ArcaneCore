namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>
/// The loaded <c>game_weather</c> data (vmangos <c>WeatherMgr::mWeatherZoneMap</c>): zone to its
/// chance table. Replaced atomically (hot reload, <c>.reload game_weather</c> at vmangos
/// ServerCommands.cpp:1813); readers never see a half-built table. A zone without a row has no
/// weather. Thread-safe for concurrent reads.
/// </summary>
public sealed class WeatherChanceTable
{
    private volatile IReadOnlyDictionary<uint, ZoneWeatherChances> _zones = new Dictionary<uint, ZoneWeatherChances>();

    public int Count => _zones.Count;

    /// <summary>The zone's chances, or null (the weather then stays fine).</summary>
    public ZoneWeatherChances? Get(uint zone) => _zones.GetValueOrDefault(zone);

    /// <summary>Swap the whole table in one step.</summary>
    public void Replace(IEnumerable<KeyValuePair<uint, ZoneWeatherChances>> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _zones = zones.ToDictionary(z => z.Key, z => z.Value);
    }
}
