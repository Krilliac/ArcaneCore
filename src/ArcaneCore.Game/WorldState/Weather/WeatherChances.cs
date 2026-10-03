namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>The rain, snow and storm chance (percent) of one season (vmangos <c>WeatherSeasonChances</c>).</summary>
public readonly record struct SeasonChances(uint Rain, uint Snow, uint Storm);

/// <summary>
/// One zone's <c>game_weather</c> row (vmangos <c>WeatherZoneChances</c>): four seasons of
/// rain/snow/storm chances, in the column order spring, summer, fall, winter.
/// </summary>
public sealed class ZoneWeatherChances
{
    /// <summary>The substitute vmangos stores for a chance above 100 (Weather.cpp:480-495).</summary>
    public const uint InvalidChanceReplacement = 25;

    private readonly SeasonChances[] _seasons;

    private ZoneWeatherChances(SeasonChances[] seasons) => _seasons = seasons;

    public SeasonChances this[WeatherSeason season] => _seasons[(int)season];

    /// <summary>
    /// Build from the 12 columns <c>spring_rain, spring_snow, spring_storm, summer_rain, ...,
    /// winter_storm</c> in <c>game_weather</c> order. A chance above 100 becomes 25 and is reported
    /// through <paramref name="onInvalid"/> (vmangos logs a DB error and does the same).
    /// </summary>
    public static ZoneWeatherChances FromColumns(uint zone, IReadOnlyList<uint> columns, Action<string>? onInvalid = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count != 12)
        {
            throw new ArgumentException("game_weather has 12 chance columns", nameof(columns));
        }

        var seasons = new SeasonChances[4];
        string[] kinds = ["rain", "snow", "storm"];
        for (int season = 0; season < 4; season++)
        {
            uint[] values = new uint[3];
            for (int kind = 0; kind < 3; kind++)
            {
                uint value = columns[(season * 3) + kind];
                if (value > 100)
                {
                    value = InvalidChanceReplacement;
                    onInvalid?.Invoke($"Weather for zone {zone} season {season} has wrong {kinds[kind]} chance > 100%");
                }

                values[kind] = value;
            }

            seasons[season] = new SeasonChances(values[0], values[1], values[2]);
        }

        return new ZoneWeatherChances(seasons);
    }
}

/// <summary>
/// The season of a date for weather (vmangos Weather.cpp:100-103): 78 days between January 1st
/// and March 20th, 91 days per season, from the SERVER-LOCAL day of the year (0-based
/// <c>tm_yday</c>).
/// </summary>
public static class WeatherSeasons
{
    public static WeatherSeason Of(DateTimeOffset local)
    {
        int yday = local.DayOfYear - 1;
        return (WeatherSeason)(((yday - 78 + 365) / 91) % 4);
    }
}
