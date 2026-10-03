namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// New moons and the Chinese new year, for the <see cref="GameEventScheduleType.LunarNewYear"/> schedule
/// (mangos-classic <c>ComputeEventStartAndEndTime</c>, GameEventMgr.cpp:1274-1304; the date is the one in China, UTC+8). This is an independent implementation: the new-moon time is the mean lunation plus
/// the periodic terms of Jean Meeus, "Astronomical Algorithms" (2nd ed.) chapter 49: a different algorithm from the
/// Numerical Recipes <c>flmoon</c> that mangos-classic's <c>moon.cpp</c> uses, and no code or constant of that file is
/// used here. The planetary correction terms are left out, so a new moon is good to a few minutes; the answer only
/// changes when a new moon falls within that margin of midnight in China, and the Game test project pins the dates of
/// 2000-2040 against the real calendar.
/// Pure maths: no clock, no zone.
/// </summary>
public static class LunarPhase
{
    /// <summary>The mean length of a lunation in days (Meeus 49.1).</summary>
    public const double SynodicMonthDays = 29.530588861;

    /// <summary>The Julian Day of the Unix epoch.</summary>
    private const double UnixEpochJulianDay = 2440587.5;

    /// <summary>TT - UT in seconds around 2000-2040 (about 64-70 s); the new-moon time is TT, the answer is UT.</summary>
    private const double DeltaTSeconds = 69.0;

    /// <summary>China Standard Time, UTC+8 (cmangos: "china is gmt + 8").</summary>
    public static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>The instant of the new moon with lunation number <paramref name="k"/> (k = 0 is the new moon of 2000-01-06).</summary>
    public static DateTimeOffset NewMoon(int k)
    {
        double t = k / 1236.85;
        double t2 = t * t;
        double t3 = t2 * t;
        double t4 = t3 * t;

        double jde = 2451550.09766 + (SynodicMonthDays * k) + (0.00015437 * t2) - (0.000000150 * t3) + (0.00000000073 * t4);
        double e = 1 - (0.002516 * t) - (0.0000074 * t2);
        double m = Rad(2.5534 + (29.10535670 * k) - (0.0000014 * t2) - (0.00000011 * t3));
        double mp = Rad(201.5643 + (385.81693528 * k) + (0.0107582 * t2) + (0.00001238 * t3) - (0.000000058 * t4));
        double f = Rad(160.7108 + (390.67050284 * k) - (0.0016118 * t2) - (0.00000227 * t3) + (0.000000011 * t4));
        double om = Rad(124.7746 - (1.56375588 * k) + (0.0020672 * t2) + (0.00000215 * t3));

        double correction =
            (-0.40720 * Math.Sin(mp))
            + (0.17241 * e * Math.Sin(m))
            + (0.01608 * Math.Sin(2 * mp))
            + (0.01039 * Math.Sin(2 * f))
            + (0.00739 * e * Math.Sin(mp - m))
            - (0.00514 * e * Math.Sin(mp + m))
            + (0.00208 * e * e * Math.Sin(2 * m))
            - (0.00111 * Math.Sin(mp - (2 * f)))
            - (0.00057 * Math.Sin(mp + (2 * f)))
            + (0.00056 * e * Math.Sin((2 * mp) + m))
            - (0.00042 * Math.Sin(3 * mp))
            + (0.00042 * e * Math.Sin(m + (2 * f)))
            + (0.00038 * e * Math.Sin(m - (2 * f)))
            - (0.00024 * e * Math.Sin((2 * mp) - m))
            - (0.00017 * Math.Sin(om))
            - (0.00007 * Math.Sin(mp + (2 * m)))
            + (0.00004 * Math.Sin((2 * mp) - (2 * f)))
            + (0.00004 * Math.Sin(3 * m))
            + (0.00003 * Math.Sin(mp + m - (2 * f)))
            + (0.00003 * Math.Sin((2 * mp) + (2 * f)))
            - (0.00003 * Math.Sin(mp + m + (2 * f)))
            + (0.00003 * Math.Sin(mp - m + (2 * f)))
            - (0.00002 * Math.Sin(mp - m - (2 * f)))
            - (0.00002 * Math.Sin((3 * mp) + m))
            + (0.00002 * Math.Sin(4 * mp));

        double jdUt = jde + correction - (DeltaTSeconds / 86400.0);
        double unixSeconds = (jdUt - UnixEpochJulianDay) * 86400.0;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unixSeconds * 1000.0));
    }

    /// <summary>The first new moon at or after <paramref name="instant"/>.</summary>
    public static DateTimeOffset NextNewMoon(DateTimeOffset instant)
    {
        // Start a lunation or two early and walk forward: k is only an index, the instants decide.
        double years = (instant.UtcDateTime.Year + ((instant.UtcDateTime.DayOfYear - 1) / 365.25)) - 2000.0;
        int k = (int)Math.Floor(years * 12.3685) - 2;
        while (NewMoon(k) < instant)
        {
            k++;
        }

        return NewMoon(k);
    }

    /// <summary>
    /// The apparent geocentric ecliptic longitude of the sun in degrees (0 to 360), Meeus chapter 25 (low accuracy,
    /// about 0.01 degree, which is a quarter of an hour of time).
    /// </summary>
    public static double SolarLongitude(DateTimeOffset instant)
    {
        double jd = (instant.ToUnixTimeMilliseconds() / 86400000.0) + UnixEpochJulianDay;
        double t = (jd - 2451545.0) / 36525.0;
        double l0 = 280.46646 + (36000.76983 * t) + (0.0003032 * t * t);
        double m = Rad(357.52911 + (35999.05029 * t) - (0.0001537 * t * t));
        double c = ((1.914602 - (0.004817 * t) - (0.000014 * t * t)) * Math.Sin(m))
            + ((0.019993 - (0.000101 * t)) * Math.Sin(2 * m))
            + (0.000289 * Math.Sin(3 * m));
        double omega = Rad(125.04 - (1934.136 * t));
        double lambda = (l0 + c - 0.00569 - (0.00478 * Math.Sin(omega))) % 360.0;
        return lambda < 0 ? lambda + 360.0 : lambda;
    }

    /// <summary>The China (UTC+8) calendar day of an instant.</summary>
    private static DateOnly ChinaDay(DateTimeOffset instant) => DateOnly.FromDateTime((instant + ChinaOffset).DateTime);

    private static DateTimeOffset ChinaMidnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), ChinaOffset);

    /// <summary>Whether a major solar term (the sun's longitude passing a multiple of 30 degrees) falls on a China day in [first, next).</summary>
    private static bool HasMajorSolarTerm(DateOnly first, DateOnly next)
    {
        double start = SolarLongitude(ChinaMidnight(first));
        double advance = SolarLongitude(ChinaMidnight(next)) - start;
        if (advance < 0)
        {
            advance += 360.0;
        }

        return Math.Floor(start / 30.0) != Math.Floor((start + advance) / 30.0);
    }

    /// <summary>
    /// The calendar date, in China, of the Chinese new year of <paramref name="year"/>, by the rule of the Chinese calendar:
    /// the lunar month that contains the winter solstice (by China date) is the eleventh month, and the new year is the
    /// start of the month two lunations later, or three when the first or second month after the eleventh has no major
    /// solar term (a leap month 11 or 12, as before the new year of 2034). mangos-classic approximates this with the second
    /// new moon after December 21st, which is a month early in years such as 2015 (January 20th instead of February 19th).
    /// </summary>
    public static DateOnly LunarNewYear(int year)
    {
        // The China day of the winter solstice (longitude 270): the sun is before it at the start of the day and past it at the end.
        DateOnly solsticeDay = default;
        for (var day = new DateOnly(year - 1, 12, 15); day <= new DateOnly(year - 1, 12, 28); day = day.AddDays(1))
        {
            if (SolarLongitude(ChinaMidnight(day)) < 270.0 && SolarLongitude(ChinaMidnight(day.AddDays(1))) >= 270.0)
            {
                solsticeDay = day;
                break;
            }
        }

        if (solsticeDay == default)
        {
            throw new InvalidOperationException($"no winter solstice found in December {year - 1}");
        }

        // Month 11 starts on the last new-moon day on or before the solstice day; collect it and the starts after it.
        var starts = new List<DateOnly>();
        DateTimeOffset moon = NextNewMoon(ChinaMidnight(solsticeDay.AddDays(-32)));
        while (starts.Count == 0 || starts[^1] <= solsticeDay)
        {
            starts.Add(ChinaDay(moon));
            moon = NextNewMoon(moon.AddDays(1));
        }

        int a = starts.Count - 2; // the last start that is not after the solstice day
        while (starts.Count < a + 4)
        {
            starts.Add(ChinaDay(moon));
            moon = NextNewMoon(moon.AddDays(1));
        }

        // Month 12 starts at a + 1 and month 1 at a + 2, unless a month without a major solar term (a leap month) sits between.
        bool leap = !HasMajorSolarTerm(starts[a + 1], starts[a + 2]) || !HasMajorSolarTerm(starts[a + 2], starts[a + 3]);
        return leap ? starts[a + 3] : starts[a + 2];
    }
    private static double Rad(double degrees) => degrees * Math.PI / 180.0;
}
