namespace ArcaneCore.Game.WorldState.Events;

/// <summary>How yearly events account for February 29th (<c>World:GameEvents:LeapDayMode</c>).</summary>
public enum LeapDayMode
{
    /// <summary>
    /// Count only the Feb 29ths that fall inside [start, now): holidays stay on their calendar date, as in retail.
    /// Default. A deliberate departure from the vmangos code below.
    /// </summary>
    DateStable,

    /// <summary>
    /// vmangos GameEventMgr.cpp:241-251 literally: one leap day per leap YEAR in [start year, current year), which
    /// counts the start year's own Feb 29th even when it precedes the start, and the current year's never. A
    /// yearly event that starts in a leap year therefore begins one day late in 2021-2023, 2025-2027 and so on.
    /// </summary>
    VmangosLiteral,
}

/// <summary>
/// The unit a manual <c>StartEvent</c> / <c>StopEvent</c> (with overwrite) adds the event length in when it rewrites an end
/// (<c>World:GameEvents:ManualStartLengthUnit</c>).
/// </summary>
public enum GameEventManualLengthUnit
{
    /// <summary>
    /// Retail: vmangos GameEventMgr.cpp:95 and :111 (and mangos-classic) add <c>length</c>, a count of minutes, as raw seconds, so a
    /// hand-started event whose table end has passed is stopped by the next update after about <c>length</c> seconds. Default.
    /// </summary>
    Seconds,

    /// <summary>The length is minutes, as everywhere else (<c>length * MINUTE</c>): a hand-started event runs its whole length.</summary>
    Minutes,
}

/// <summary>
/// One event of the vmangos <c>game_event</c> table (GameEventMgr.h:43-60): when it first starts and last ends,
/// how often it recurs and how long each occurrence lasts (both in minutes).
/// </summary>
public sealed record GameEventDefinition(
    ushort Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    uint OccurenceMinutes,
    uint LengthMinutes,
    uint HolidayId,
    string Description,
    bool Hardcoded = false,
    bool Disabled = false)
{
    /// <summary>The mangos-classic <c>schedule_type</c> (<see cref="GameEventScheduleType.Date"/> for vmangos data, which has none).</summary>
    public GameEventScheduleType ScheduleType { get; init; } = GameEventScheduleType.Date;

    /// <summary>mangos-classic <c>linkedTo</c>: this event can only start while that event is active (0 = none).</summary>
    public ushort LinkedTo { get; init; }

    /// <summary>
    /// vmangos <c>isValid</c> (a length of 0 is invalid, the loader skips it); mangos-classic <c>isValid</c> also
    /// accepts a serverside event of any length (GameEventMgr.h:64).
    /// </summary>
    public bool IsValid => ScheduleType == GameEventScheduleType.Serverside || LengthMinutes > 0;
}

/// <summary>
/// The pure schedule maths of vmangos <c>GameEventMgr</c>: <c>CheckOneGameEvent</c> (GameEventMgr.cpp:38-44),
/// <c>NextCheck</c> (:46-70) and the leap-day adjustment of yearly events (:241-251). No clock and no state:
/// the current time is an argument. The service that starts and stops events, the status table, the commands
/// and the cmangos <c>schedule_type</c> dialect are not part of this slice (docs/areas/world-state.md).
/// </summary>
public static class GameEventSchedule
{
    /// <summary>vmangos <c>max_ge_check_delay</c>: one day, in seconds.</summary>
    public const uint MaxCheckDelaySeconds = 86400;

    /// <summary>vmangos <c>default_year_length</c>: 365 days, in minutes.</summary>
    public const uint DefaultYearLengthMinutes = 525600;

    private const long Day = 86400;
    private const long Minute = 60;

    /// <summary>
    /// Leap days between the event's start and <paramref name="now"/> (vmangos <c>leapDays</c>): only events that
    /// recur every 365 days and last less than that are adjusted.
    /// </summary>
    public static int LeapDays(GameEventDefinition definition, DateTimeOffset now, LeapDayMode mode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.OccurenceMinutes != DefaultYearLengthMinutes || definition.LengthMinutes >= DefaultYearLengthMinutes)
        {
            return 0;
        }

        DateTime start = definition.Start.UtcDateTime;
        DateTime current = now.UtcDateTime;
        int count = 0;
        if (mode == LeapDayMode.VmangosLiteral)
        {
            // (gmtime) for (i = tm_start.tm_year; i < tm_current.tm_year; i++) if (isLeapYear(i + 1900)) ++leapDays
            for (int year = start.Year; year < current.Year; year++)
            {
                if (DateTime.IsLeapYear(year))
                {
                    count++;
                }
            }

            return count;
        }

        for (int year = start.Year; year <= current.Year; year++)
        {
            if (DateTime.IsLeapYear(year))
            {
                var feb29 = new DateTime(year, 2, 29, 0, 0, 0, DateTimeKind.Utc);
                if (feb29 >= start && feb29 < current)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// vmangos <c>CheckOneGameEvent</c>: inside [start, end) and inside the recurrence window. <paramref name="boundary"/>
    /// <c>Exclusive</c> is the mangos-classic form (<c>start &lt; current</c>); <c>Auto</c> counts as inclusive here (the
    /// service resolves it from the data's dialect before it calls).
    /// </summary>
    public static bool IsActive(GameEventDefinition definition, DateTimeOffset now, LeapDayMode mode = LeapDayMode.DateStable, GameEventStartBoundary boundary = GameEventStartBoundary.Inclusive)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.OccurenceMinutes == 0)
        {
            return false; // vmangos would divide by zero; an occurence of 0 disables the event here
        }

        long current = now.ToUnixTimeSeconds();
        long start = definition.Start.ToUnixTimeSeconds();
        long end = definition.End.ToUnixTimeSeconds();
        bool started = boundary == GameEventStartBoundary.Exclusive ? start < current : start <= current;
        return started && current < end
            && (current - start - (LeapDays(definition, now, mode) * Day)) % (definition.OccurenceMinutes * Minute) < definition.LengthMinutes * Minute;
    }

    /// <summary>
    /// vmangos <c>NextCheck</c>: seconds until this event next needs looking at: one day for an outdated event,
    /// the time to the start for one that has not begun, else the end of the running occurrence or the start
    /// of the next, clipped at the event's end.
    /// </summary>
    public static uint NextCheckSeconds(GameEventDefinition definition, DateTimeOffset now, LeapDayMode mode = LeapDayMode.DateStable)
    {
        ArgumentNullException.ThrowIfNull(definition);
        long current = now.ToUnixTimeSeconds();
        long start = definition.Start.ToUnixTimeSeconds();
        long end = definition.End.ToUnixTimeSeconds();
        if (current > end || definition.OccurenceMinutes == 0)
        {
            return MaxCheckDelaySeconds; // outdated event (or one that can never run): the maximum
        }

        if (start > current)
        {
            return (uint)(start - current); // never started: the delay before the start
        }

        long occurence = definition.OccurenceMinutes * Minute;
        long length = definition.LengthMinutes * Minute;
        long into = (current - start - (LeapDays(definition, now, mode) * Day)) % occurence;
        long delay = into < length ? length - into : occurence - into;
        // in case the end is before the next check
        return end < current + delay ? (uint)(end - current) : (uint)delay;
    }
}
