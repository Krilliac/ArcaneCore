namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// One <c>game_event</c> row as the data layer reads it, before the load-time rules turn it into a
/// <see cref="GameEventDefinition"/>. <see cref="Start"/> / <see cref="End"/> are the instants of the row's own
/// columns (vmangos <c>start_time</c>, <c>end_time</c>) or of its <c>game_event_time</c> row (mangos-classic), already
/// read with <see cref="GameEventCalendar.ReadWallTime"/>; null when there is none.
/// </summary>
public sealed record GameEventSource(
    ushort Id,
    GameEventScheduleType ScheduleType,
    uint OccurenceMinutes,
    uint LengthMinutes,
    uint HolidayId,
    ushort LinkedTo,
    string Description,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool Hardcoded = false,
    bool Disabled = false,
    byte PatchMin = 0,
    byte PatchMax = 10);

/// <summary>
/// The load-time rules of the two <c>game_event</c> dialects. Both build the same <see cref="GameEventDefinition"/>;
/// every rejected or adjusted row adds a line to <c>issues</c> (the loader logs them) and never throws: a bad row only
/// disables its own event. Pure: the clock and the zone are arguments.
/// </summary>
public static class GameEventValidation
{
    /// <summary>vmangos <c>WowPatch</c> for 1.12 (the patch the <c>patch_min</c> / <c>patch_max</c> columns are compared with).</summary>
    public const int WowPatch = 10;

    /// <summary>The default start of a mangos-classic event row that never got a time (<c>GameEventData()</c>: start 1, end 0).</summary>
    private static readonly DateTimeOffset s_defaultStart = DateTimeOffset.FromUnixTimeSeconds(1);

    private static readonly DateTimeOffset s_defaultEnd = DateTimeOffset.FromUnixTimeSeconds(0);

    /// <summary>
    /// mangos-classic <c>GameEventMgr::LoadFromDB</c> (GameEventMgr.cpp:107-226) for one row and its
    /// <c>game_event_time</c> row: serverside events get the far future, computed schedules are computed, an occurence of 0
    /// disables the event, a length of 0 or an occurence shorter than the length makes it unusable (start in the far
    /// future), and a time row on a non-date schedule is ignored.
    /// </summary>
    public static GameEventDefinition BuildCMangos(GameEventSource source, DateTimeOffset now, TimeZoneInfo zone, YearlyRebaseMode yearly, ICollection<string> issues)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(issues);

        GameEventDefinition definition = new(source.Id, s_defaultStart, s_defaultEnd, source.OccurenceMinutes, source.LengthMinutes, source.HolidayId, source.Description)
        {
            ScheduleType = source.ScheduleType,
            LinkedTo = source.LinkedTo,
        };

        switch (source.ScheduleType)
        {
            case GameEventScheduleType.Serverside:
                definition = definition with { Start = GameEventCalendar.FarFuture, End = GameEventCalendar.FarFuture };
                break;
            case GameEventScheduleType.Date:
            case GameEventScheduleType.Yearly:
                break; // loaded from the time row below
            case GameEventScheduleType.LunarNewYear:
            case GameEventScheduleType.Easter:
                (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(definition, now, zone, yearly);
                definition = definition with { Start = start, End = end };
                break;
            default:
                issues.Add($"game_event {source.Id}: schedule type {(int)source.ScheduleType} is not implemented (the computed Darkmoon Faire schedules); the event is disabled");
                definition = definition with { Start = GameEventCalendar.FarFuture };
                break;
        }

        if (definition.OccurenceMinutes == 0)
        {
            issues.Add($"game_event {source.Id} ({source.Description}) is disabled: its occurence is 0");
            definition = definition with { Start = GameEventCalendar.FarFuture, OccurenceMinutes = definition.LengthMinutes };
        }

        if (definition.LengthMinutes == 0 && source.ScheduleType != GameEventScheduleType.Serverside)
        {
            issues.Add($"game_event {source.Id} has length 0 and can't be used");
            definition = definition with { Start = GameEventCalendar.FarFuture };
        }

        if (definition.OccurenceMinutes < definition.LengthMinutes)
        {
            issues.Add($"game_event {source.Id} has occurence {definition.OccurenceMinutes} < length {definition.LengthMinutes} and can't be used");
            definition = definition with { Start = GameEventCalendar.FarFuture };
        }

        if (source.Start is not null || source.End is not null)
        {
            if (source.ScheduleType is not (GameEventScheduleType.Date or GameEventScheduleType.Yearly))
            {
                issues.Add($"game_event {source.Id} has game_event_time but is not date scheduled - ignoring");
            }
            else if (source.Start is null || source.End is null)
            {
                issues.Add($"game_event {source.Id} has an unreadable start or end in game_event_time and never runs");
            }
            else if (definition.Start != GameEventCalendar.FarFuture)
            {
                definition = definition with { Start = source.Start ?? s_defaultStart, End = source.End ?? s_defaultEnd };
                if (source.ScheduleType == GameEventScheduleType.Yearly)
                {
                    (DateTimeOffset start, DateTimeOffset end) = GameEventCalendar.Compute(definition, now, zone, yearly);
                    definition = definition with { Start = start, End = end };
                }
            }
        }

        return definition;
    }

    /// <summary>
    /// vmangos <c>GameEventMgr::LoadFromDB</c> (GameEventMgr.cpp:183-259) for one row: a length of 0 makes the event
    /// invalid, an invalid patch range is reset to 0..10, and an event outside the 1.12 patch range is disabled.
    /// A row without a start or end time is invalid.
    /// </summary>
    public static GameEventDefinition BuildVMangos(GameEventSource source, ICollection<string> issues)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(issues);

        GameEventDefinition definition = new(
            source.Id, source.Start ?? s_defaultStart, source.End ?? s_defaultEnd, source.OccurenceMinutes, source.LengthMinutes,
            source.HolidayId, source.Description, source.Hardcoded, source.Disabled);
        if (source.Start is null || source.End is null)
        {
            issues.Add($"game_event {source.Id} has no start_time or end_time and can't be used");
            definition = definition with { LengthMinutes = 0 };
        }

        if (source.LengthMinutes == 0)
        {
            issues.Add($"game_event {source.Id} has length 0 and can't be used");
            return definition;
        }

        byte min = source.PatchMin;
        byte max = source.PatchMax;
        if (min > max || max > 10)
        {
            issues.Add($"game_event {source.Id} has invalid values patch_min={min}, patch_max={max}");
            min = 0;
            max = 10;
        }

        return WowPatch >= min && WowPatch <= max ? definition : definition with { Disabled = true };
    }

    /// <summary>
    /// mangos-classic's final load check (GameEventMgr.cpp:184-191): a valid event linked to an event that does not exist or is
    /// not valid loses the link. Returns the definitions with the broken links cleared; the input is not changed.
    /// </summary>
    public static IReadOnlyList<GameEventDefinition> ResolveLinks(IReadOnlyList<GameEventDefinition> definitions, ICollection<string> issues)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(issues);
        var valid = new HashSet<ushort>(definitions.Where(d => d.IsValid).Select(d => d.Id));
        var result = new List<GameEventDefinition>(definitions.Count);
        foreach (GameEventDefinition definition in definitions)
        {
            if (definition.IsValid && definition.LinkedTo != 0 && !valid.Contains(definition.LinkedTo))
            {
                issues.Add($"game_event {definition.Id} is linked to invalid event {definition.LinkedTo}");
                result.Add(definition with { LinkedTo = 0 });
            }
            else
            {
                result.Add(definition);
            }
        }

        return result;
    }
}
