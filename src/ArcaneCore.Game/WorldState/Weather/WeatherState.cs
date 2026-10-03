namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>vmangos <c>WeatherType</c> (Weather.h) / wow_messages <c>WeatherType</c> (smsg_weather.wowm).</summary>
public enum WeatherType : uint
{
    Fine = 0,
    Rain = 1,
    Snow = 2,
    Storm = 3,
}

/// <summary>vmangos season order of the <c>game_weather</c> columns (Weather.cpp:446-505).</summary>
public enum WeatherSeason
{
    Spring = 0,
    Summer = 1,
    Fall = 2,
    Winter = 3,
}

/// <summary>
/// The random source of the weather engine, injectable so tests script every branch.
/// vmangos uses <c>urand(min, max)</c> (inclusive both ends) and <c>rand_norm_f()</c> ([0, 1]).
/// </summary>
public interface IWeatherRandom
{
    /// <summary>A value in [<paramref name="min"/>, <paramref name="max"/>] (vmangos <c>urand</c>).</summary>
    uint Next(uint min, uint max);

    /// <summary>A float in [0, 1] (vmangos <c>rand_norm_f</c>).</summary>
    float NextFloat();
}

/// <summary>The default <see cref="IWeatherRandom"/>, backed by <see cref="Random.Shared"/>.</summary>
public sealed class SharedWeatherRandom : IWeatherRandom
{
    public static SharedWeatherRandom Instance { get; } = new();

    public uint Next(uint min, uint max) => (uint)Random.Shared.NextInt64(min, (long)max + 1);

    public float NextFloat() => (float)Random.Shared.NextDouble();
}

/// <summary>
/// One zone's weather: type, grade and the <c>ReGenerate</c> state machine, a line-by-line port of
/// vmangos <c>Weather</c> (src/game/Weather.cpp, mangos-classic's is identical). All grade
/// arithmetic is float32 like the C++ (the constants <c>0.33333334f</c>, <c>0.6666667f</c>,
/// <c>0.9999f</c> matter). No clock and no static RNG: the season and the random source are
/// arguments.
/// </summary>
public sealed class WeatherState
{
    /// <summary>1/3 as vmangos writes it (Weather.cpp:121-137).</summary>
    public const float Third = 0.33333334f;

    /// <summary>2/3 as vmangos writes it in the "radical change" branch (Weather.cpp:151-165).</summary>
    public const float TwoThirds = 0.6666667f;

    public WeatherState(uint zone, ZoneWeatherChances? chances)
    {
        Zone = zone;
        Chances = chances;
    }

    public uint Zone { get; }

    /// <summary>The zone's chance table; null when <c>game_weather</c> has no row (the weather then stays fine).</summary>
    public ZoneWeatherChances? Chances { get; set; }

    public WeatherType Type { get; private set; } = WeatherType.Fine;

    public float Grade { get; private set; }

    /// <summary>vmangos <c>m_isPermanentWeather</c>: set by <see cref="SetWeather"/> (GM command); never regenerates.</summary>
    public bool IsPermanent { get; set; }

    /// <summary>
    /// vmangos <c>Weather::ReGenerate</c> (Weather.cpp:87-210): returns true if and only if the
    /// weather changed.
    /// </summary>
    public bool ReGenerate(WeatherSeason season, IWeatherRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (IsPermanent)
        {
            return false;
        }

        WeatherType oldType = Type;
        float oldGrade = Grade;

        if (Chances is null)
        {
            Type = WeatherType.Fine;
            Grade = 0.0f;
            return oldType != Type || oldGrade != Grade;
        }

        // Statistics: 30% no change, 30% better (or change type), 30% worse, 10% radical change.
        uint u = random.Next(0, 99);
        if (u < 30)
        {
            return false;
        }

        if (u < 60 && Grade < Third) // get fair
        {
            Type = WeatherType.Fine;
            Grade = 0.0f;
        }

        // The get-better and get-worse branches return without NormalizeGrade (vmangos): a grade
        // can leave [0, 1) here until the packet builder normalizes it.
        if (u < 60 && Type != WeatherType.Fine) // get better
        {
            Grade -= Third;
            return true;
        }

        if (u < 90 && Type != WeatherType.Fine) // get worse
        {
            Grade += Third;
            return true;
        }

        if (Type != WeatherType.Fine)
        {
            // Radical change: light -> heavy; medium -> change weather type; heavy -> 50% light,
            // 50% change weather type.
            if (Grade < Third)
            {
                Grade = 0.9999f; // go nuts
                return true;
            }

            if (Grade > TwoThirds)
            {
                // Severe change, but how severe?
                uint severity = random.Next(0, 99);
                if (severity < 50)
                {
                    Grade -= TwoThirds;
                    return true;
                }
            }

            Type = WeatherType.Fine; // clear up
            Grade = 0;
        }

        // Only weather that is not doing anything remains, in a zone that has weather data.
        SeasonChances season3 = Chances[season];
        uint chance1 = season3.Rain;
        uint chance2 = chance1 + season3.Snow;
        uint chance3 = chance2 + season3.Storm;

        uint roll = random.Next(1, 100);
        if (roll <= chance1)
        {
            Type = WeatherType.Rain;
        }
        else if (roll <= chance2)
        {
            Type = WeatherType.Snow;
        }
        else if (roll <= chance3)
        {
            Type = WeatherType.Storm;
        }
        else
        {
            Type = WeatherType.Fine;
        }

        // New weather (if not fine): 85% light, 7% medium, 7% heavy. Fine is always sunny.
        if (Type == WeatherType.Fine)
        {
            Grade = 0.0f;
        }
        else if (u < 90)
        {
            Grade = random.NextFloat() * 0.3333f;
        }
        else
        {
            // Severe change, but how severe?
            roll = random.Next(0, 99);
            Grade = roll < 50 ? (random.NextFloat() * 0.3333f) + 0.3334f : (random.NextFloat() * 0.3333f) + 0.6667f;
        }

        NormalizeGrade();
        return Type != oldType || Grade != oldGrade;
    }

    /// <summary>
    /// vmangos <c>Weather::SetWeather</c> (Weather.cpp:257-267) as used by <c>.wchange</c>: set type
    /// and grade, clamp the grade; the weather becomes non-permanent unless <paramref name="permanent"/>.
    /// </summary>
    public void SetWeather(WeatherType type, float grade, bool permanent = false)
    {
        Type = type;
        Grade = grade;
        IsPermanent = permanent;
        NormalizeGrade();
    }

    /// <summary>vmangos <c>Weather::NormalizeGrade</c> (Weather.cpp:304-310).</summary>
    public void NormalizeGrade()
    {
        if (Grade >= 1)
        {
            Grade = 0.9999f;
        }
        else if (Grade < 0)
        {
            Grade = 0.0001f;
        }
    }

    /// <summary>
    /// The sound id sent with the weather (vmangos <c>Weather::GetSound</c>, Weather.cpp:402-443):
    /// 0 below grade 0.3, then light/medium/heavy at 0.3/0.6/0.9 per type.
    /// </summary>
    public uint GetSound() => WeatherSounds.For(Type, Grade);

    /// <summary>The SMSG_WEATHER body for this state (normalizes the grade first, as vmangos <c>SendWeatherUpdateToPlayer</c> does).</summary>
    public byte[] BuildPacket()
    {
        NormalizeGrade();
        return WeatherPackets.Build(Type, Grade, GetSound());
    }
}
