namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// The computed schedules of the mangos-classic <c>game_event</c> dialect: yearly rebasing (<c>schedule_type</c> 11),
/// the Chinese new year (12) and Easter (13), plus the zone rules for reading a zone-less date. Mirrors
/// <c>GameEventMgr::ComputeEventStartAndEndTime</c> (mangos-classic GameEventMgr.cpp:1210-1324) and
/// <c>gaussEaster</c> (:1162-1208). Pure: the current time and the zone are arguments. The computed Darkmoon Faire
/// schedules (2-10) are not implemented: classic-db has no row that uses them (documented limit).
/// </summary>
public static class GameEventCalendar
{
    /// <summary>mangos-classic <c>FAR_FUTURE</c>, 2100-01-01 (GameEventMgr.h:27): the start of an event that can never run.</summary>
    public const long FarFutureUnixSeconds = 4102444800;

    /// <summary>mangos-classic <c>FAR_FUTURE</c> as an instant.</summary>
    public static DateTimeOffset FarFuture { get; } = DateTimeOffset.FromUnixTimeSeconds(FarFutureUnixSeconds);

    /// <summary>The lunar new year event lasts 21 days from its start (GameEventMgr.cpp:1301-1302).</summary>
    public const int LunarNewYearDays = 21;

    /// <summary>The Easter event is 7 days from its start (GameEventMgr.cpp:1316-1317).</summary>
    public const int EasterDays = 7;

    /// <summary>Whether <paramref name="type"/> is one of the mangos-classic Darkmoon Faire schedule types (2..10).</summary>
    public static bool IsDarkmoon(GameEventScheduleType type)
        => (int)type is >= (int)GameEventScheduleType.DarkmoonFaire1 and <= 10;

    /// <summary>Whether a schedule type has an implementation here (serverside, date, Darkmoon Faire 2..10, yearly, lunar new year, Easter).</summary>
    public static bool IsSupported(GameEventScheduleType type) => type is GameEventScheduleType.Serverside or GameEventScheduleType.Date
        or GameEventScheduleType.Yearly or GameEventScheduleType.LunarNewYear or GameEventScheduleType.Easter || IsDarkmoon(type);

    /// <summary>Whether the start and end of this type are recomputed from the current date.</summary>
    public static bool IsComputed(GameEventScheduleType type) => type is GameEventScheduleType.Yearly or GameEventScheduleType.LunarNewYear or GameEventScheduleType.Easter || IsDarkmoon(type);

    /// <summary>The day of the month (1..7) of the first Friday of <paramref name="month"/> in <paramref name="year"/>.</summary>
    public static int FirstFridayOfMonth(int year, int month)
    {
        for (int day = 1; day <= 7; day++)
        {
            if (new DateTime(year, month, day).DayOfWeek == DayOfWeek.Friday)
            {
                return day;
            }
        }

        throw new InvalidOperationException("Unreachable: every 7-day range contains a Friday.");
    }

    /// <summary>
    /// mangos-classic <c>gaussEaster</c>: the month and day of Easter Sunday of <paramref name="year"/> by the Gauss
    /// algorithm, with the float arithmetic and the two corner cases (D 29 / E 6 gives April 19th, D 28 / E 6 April
    /// 18th) of the original. Its century terms are the 21st century's: it is the true Easter for 2006-2099 (checked
    /// in the tests) and one day off in 2100.
    /// </summary>
    public static (int Month, int Day) GaussEaster(int year)
    {
        float a = year % 19;
        float b = year % 4;
        float c = year % 7;
        float p = (float)Math.Floor(year / 100.0);
        float q = (float)Math.Floor((13 + (8 * p)) / 25);
        float m = (int)(15 - q + p - (p / 4)) % 30;
        float n = (int)(4 + p - (p / 4)) % 7;
        float d = (int)((19 * a) + m) % 30;
        float e = (int)((2 * b) + (4 * c) + (6 * d) + n) % 7;
        int days = (int)(22 + d + e);

        if (d == 29 && e == 6)
        {
            return (4, 19);
        }

        if (d == 28 && e == 6)
        {
            return (4, 18);
        }

        return days > 31 ? (4, days - 31) : (3, days);
    }

    /// <summary>
    /// The instant of a wall-clock time in <paramref name="zone"/>. A time that does not exist (the hour skipped when
    /// daylight saving starts) moves forward by that hour; an ambiguous one takes the standard offset.
    /// </summary>
    public static DateTimeOffset LocalToInstant(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>
    /// A zone-less <c>start_time</c> / <c>end_time</c> as an instant (<see cref="GameEventDateTimeInterpretation"/>).
    /// </summary>
    public static DateTimeOffset ReadWallTime(DateTime value, TimeZoneInfo zone, GameEventDateTimeInterpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return interpretation == GameEventDateTimeInterpretation.Wall
            ? LocalToInstant(value, zone)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), zone.BaseUtcOffset);
    }

    /// <summary>The wall-clock time of an instant in <paramref name="zone"/>.</summary>
    public static DateTime ToLocal(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone).DateTime;
    }

    /// <summary>
    /// The start and end of <paramref name="definition"/> for <paramref name="now"/>
    /// (<c>ComputeEventStartAndEndTime</c>). Serverside and date events keep what they have; Darkmoon Faire, yearly, lunar new year and
    /// Easter events are recomputed. Throws <see cref="NotSupportedException"/> for unknown schedule types
    /// (see <see cref="IsSupported"/>).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Compute(
        GameEventDefinition definition,
        DateTimeOffset now,
        TimeZoneInfo zone,
        YearlyRebaseMode yearly = YearlyRebaseMode.SpanNewYear)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(zone);
        switch (definition.ScheduleType)
        {
            case GameEventScheduleType.Serverside:
            case GameEventScheduleType.Date:
                return (definition.Start, definition.End);

            case GameEventScheduleType.Yearly:
                return ComputeYearly(definition, now, zone, yearly);

            case GameEventScheduleType.LunarNewYear:
            {
                DateOnly day = LunarPhase.LunarNewYear(ToLocal(now, zone).Year);
                DateTime startLocal = day.ToDateTime(TimeOnly.MinValue);
                return (LocalToInstant(startLocal, zone), LocalToInstant(startLocal.AddDays(LunarNewYearDays), zone));
            }

            case GameEventScheduleType.Easter:
            {
                // mangos-classic takes the year from gmtime (the UTC year) and the dates from the local midnight.
                (int month, int day) = GaussEaster(now.UtcDateTime.Year);
                DateTime startLocal = new(now.UtcDateTime.Year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
                return (LocalToInstant(startLocal, zone), LocalToInstant(startLocal.AddDays(EasterDays), zone));
            }

            case var dmf when IsDarkmoon(dmf):
                return ComputeDarkmoon(definition, now, zone);

            default:
                throw new NotSupportedException($"game event schedule type {(int)definition.ScheduleType} ({definition.ScheduleType}) is not implemented");
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ComputeDarkmoon(GameEventDefinition definition, DateTimeOffset now, TimeZoneInfo zone)
    {
        int targetRemainder = ((int)definition.ScheduleType - (int)GameEventScheduleType.DarkmoonFaire1) % 3;
        DateTime localNow = ToLocal(now, zone);
        int year = localNow.Year;
        int month = localNow.Month;

        for (int i = 0; i < 12; i++)
        {
            if ((month - 1) % 3 == targetRemainder)
            {
                int firstFridayDay = FirstFridayOfMonth(year, month);
                DateTime startLocal = new(year, month, firstFridayDay, 0, 0, 0, DateTimeKind.Unspecified);
                DateTimeOffset start = LocalToInstant(startLocal, zone);
                DateTimeOffset end = start.AddMinutes(definition.LengthMinutes);
                if (end > now || (definition.LengthMinutes == 0 && start >= now))
                {
                    return (start, end);
                }
            }

            month++;
            if (month > 12)
            {
                month = 1;
                year++;
            }
        }

        throw new InvalidOperationException("Unreachable: Darkmoon Faire rotation must match within 12 months.");
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ComputeYearly(GameEventDefinition definition, DateTimeOffset now, TimeZoneInfo zone, YearlyRebaseMode mode)
    {
        int year = ToLocal(now, zone).Year;
        DateTime startLocal = ToLocal(definition.Start, zone);
        DateTime endLocal = ToLocal(definition.End, zone);
        DateTimeOffset start = LocalToInstant(Rebase(startLocal, year), zone);
        DateTimeOffset end = LocalToInstant(Rebase(endLocal, year), zone);
        if (mode == YearlyRebaseMode.MangosLiteral)
        {
            return (start, end);
        }

        TimeSpan length = TimeSpan.FromMinutes(definition.LengthMinutes);
        if (now < start)
        {
            // Not open yet this year: last year's window may still be running across New Year.
            DateTimeOffset previous = LocalToInstant(Rebase(startLocal, year - 1), zone);
            if (now < previous + length)
            {
                return (previous, previous + length);
            }
        }

        // A window that runs past the table's end date is not cut at it.
        return (start, end < start + length ? start + length : end);
    }

    /// <summary>The same month, day and time of day in <paramref name="year"/>; a February 29th in a common year becomes March 1st (as <c>mktime</c> normalises it).</summary>
    private static DateTime Rebase(DateTime local, int year)
        => new DateTime(year, local.Month, 1, local.Hour, local.Minute, local.Second, DateTimeKind.Unspecified).AddDays(local.Day - 1);
}
