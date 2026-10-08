using System.Globalization;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.WorldState;

/// <summary><c>.wchange</c> (vmangos ServerCommands.cpp:98-130; Chat.cpp:1281 SEC_BASIC_ADMIN, the top staff tier here).</summary>
public sealed class WeatherCommands : ICommandGroup
{
    /// <summary>mangos_string 407, LANG_WEATHER_DISABLED (cmangos-classic / vmangos world DB).</summary>
    public const string WeatherDisabledText = "Weather system disabled at server.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("wchange", AccountSecurity.Administrator, "Syntax: .wchange #weathertype #status — set the weather of your zone. Type: 0 fine, 1 rain, 2 snow, 3 sandstorm; status 0..1.", WChange),
    ];

    /// <summary>
    /// The <c>.wchange</c> arguments "#weathertype #status" (vmangos ServerCommands.cpp:106-120): a type up to
    /// <see cref="WeatherType.Storm"/> (Weather::IsValidWeatherType) and a grade clamped to 0..1. Shared with
    /// <c>.fx weather</c>, which applies the same values to more than one zone.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> parts, out WeatherType type, out float grade)
    {
        type = WeatherType.Fine;
        grade = 0;
        if (parts.Count != 2
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint raw)
            || raw > (uint)WeatherType.Storm // Weather::IsValidWeatherType
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out grade)
            || float.IsNaN(grade))
        {
            return false;
        }

        type = (WeatherType)raw;
        // clamp grade from 0 to 1
        grade = Math.Clamp(grade, 0.0f, 1.0f);
        return true;
    }

    private static bool WChange(CommandContext context, string args)
    {
        if (!WorldStateHooks.For(context.World).WeatherSettings.Enabled)
        {
            context.Reply(WeatherDisabledText);
            return true;
        }

        if (!TryParse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), out WeatherType type, out float grade))
        {
            return false;
        }

        if (context.Player.Map?.FindUpdater<MapWeather>() is not { } weather)
        {
            return false;
        }

        weather.SetWeather(context.Player.ZoneId, type, grade, permanent: false);
        return true;
    }
}
