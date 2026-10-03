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

    private static bool WChange(CommandContext context, string args)
    {
        if (!WorldStateHooks.For(context.World).WeatherSettings.Enabled)
        {
            context.Reply(WeatherDisabledText);
            return true;
        }

        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint type)
            || type > (uint)WeatherType.Storm // Weather::IsValidWeatherType
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float grade)
            || float.IsNaN(grade))
        {
            return false;
        }

        // clamp grade from 0 to 1
        grade = Math.Clamp(grade, 0.0f, 1.0f);
        if (context.Player.Map?.FindUpdater<MapWeather>() is not { } weather)
        {
            return false;
        }

        weather.SetWeather(context.Player.ZoneId, (WeatherType)type, grade, permanent: false);
        return true;
    }
}
