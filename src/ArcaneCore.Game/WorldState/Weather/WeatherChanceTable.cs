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

    /// <summary>The zones as they are now (an immutable snapshot: <see cref="Replace"/> and <see cref="Merge"/> build a new one).</summary>
    public IReadOnlyDictionary<uint, ZoneWeatherChances> Snapshot() => _zones;

    /// <summary>
    /// Overlay <paramref name="zones"/> on the current table in one step: a listed zone takes the new row, an unlisted zone keeps
    /// its old one. This is what vmangos <c>LoadWeatherZoneChances</c> does on a reload (it writes <c>mWeatherZoneMap[zone]</c>
    /// and never clears the map, Weather.cpp:472), so a zone deleted from <c>game_weather</c> keeps its chances until restart.
    /// </summary>
    public void Merge(IEnumerable<KeyValuePair<uint, ZoneWeatherChances>> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var merged = new Dictionary<uint, ZoneWeatherChances>(_zones);
        foreach ((uint zone, ZoneWeatherChances chances) in zones)
        {
            merged[zone] = chances;
        }

        _zones = merged;
    }

    /// <summary>Swap the whole table in one step.</summary>
    public void Replace(IEnumerable<KeyValuePair<uint, ZoneWeatherChances>> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _zones = zones.ToDictionary(z => z.Key, z => z.Value);
    }
}
