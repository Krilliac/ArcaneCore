namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// The <c>schedule_type</c> column of the mangos-classic <c>game_event</c> table (GameEventMgr.h:33-49). vmangos has
/// no such column: its events are always "date" events with a start and an end (GameEventMgr.cpp:183). Values 2-10
/// (the computed Darkmoon Faire schedules) are listed so a row naming one is recognised and rejected loudly; they
/// are a documented limit (docs/areas/game-events-weather.md).
/// </summary>
public enum GameEventScheduleType : byte
{
    /// <summary>Completely handled by the server (scripts or commands start and end it).</summary>
    Serverside = 0,

    /// <summary>Start and end date from <c>game_event_time</c> (vmangos: <c>start_time</c> / <c>end_time</c>).</summary>
    Date = 1,

    DarkmoonFaire1 = 2,
    DarkmoonFaire2 = 3,
    DarkmoonBuildingStage11 = 5,
    DarkmoonBuildingStage12 = 6,
    DarkmoonBuildingStage21 = 8,
    DarkmoonBuildingStage22 = 9,

    /// <summary>A date event whose start and end are moved to the current year (<c>ComputeEventStartAndEndTime</c>).</summary>
    Yearly = 11,

    /// <summary>Starts on the Chinese new year (the second new moon after the winter solstice), lasts 21 days.</summary>
    LunarNewYear = 12,

    /// <summary>Starts on Easter Sunday (Gauss algorithm), lasts 7 days.</summary>
    Easter = 13,
}

/// <summary>Which table layout the game-event data is in (<c>World:GameEvents:Dialect</c>).</summary>
public enum GameEventDialect
{
    /// <summary>Decided from the columns the loaded table has (<c>schedule_type</c> present = mangos-classic).</summary>
    Auto,

    /// <summary>mangos-classic / classic-db: <c>schedule_type</c>, <c>linkedTo</c> and the <c>game_event_time</c> table.</summary>
    CMangos,

    /// <summary>vmangos: <c>start_time</c>, <c>end_time</c>, <c>hardcoded</c>, <c>disabled</c> on <c>game_event</c>.</summary>
    VMangos,
}

/// <summary>Whether an event is active at its exact start second (<c>World:GameEvents:StartBoundary</c>).</summary>
public enum GameEventStartBoundary
{
    /// <summary>The rule of the data's dialect: inclusive for vmangos tables, exclusive for mangos-classic tables.</summary>
    Auto,

    /// <summary>vmangos <c>start &lt;= current</c> (GameEventMgr.cpp:42).</summary>
    Inclusive,

    /// <summary>mangos-classic <c>start &lt; current</c> (GameEventMgr.cpp:39).</summary>
    Exclusive,
}

/// <summary>How a zone-less <c>start_time</c> / <c>end_time</c> is turned into an instant (<c>World:GameEvents:DateTimeInterpretation</c>).</summary>
public enum GameEventDateTimeInterpretation
{
    /// <summary>The wall-clock time in the game's local zone, daylight saving included (vmangos: MySQL <c>UNIX_TIMESTAMP</c>, GameEventMgr.cpp:183).</summary>
    Wall,

    /// <summary>The wall-clock time at the zone's standard offset, never daylight saving (mangos-classic <c>mktime</c> on a zeroed <c>tm</c>, Field.cpp:24-31).</summary>
    StandardTime,
}

/// <summary>How <see cref="GameEventScheduleType.Yearly"/> events are moved to the current year (<c>World:GameEvents:YearlyRebase</c>).</summary>
public enum YearlyRebaseMode
{
    /// <summary>
    /// The start and end are moved to the current year, and a window that crosses New Year keeps running: the end is
    /// never before the window's own end, and in January the previous year's window is used while it is still open
    /// (Feast of Winter Veil runs to January 4th). Default: this is how the holiday behaves in retail.
    /// </summary>
    SpanNewYear,

    /// <summary>
    /// mangos-classic exactly (GameEventMgr.cpp:1262-1273): both dates take the current year, so a window that
    /// crosses New Year is cut at the table's end date (December 31st) and absent in January.
    /// </summary>
    MangosLiteral,
}
