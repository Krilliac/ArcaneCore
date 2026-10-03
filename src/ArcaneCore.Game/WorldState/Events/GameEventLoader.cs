using System.Globalization;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// The rows that hang off an event, with the rows that name a missing event dropped (mangos-classic / vmangos: "game event id
/// not exist in game_event", GameEventMgr.cpp). Spawn lists are keyed by the SIGNED event number (positive spawns during the
/// event, negative removes during it); quests and creature data by event id.
/// </summary>
public sealed class GameEventRows
{
    public static GameEventRows Empty { get; } = new();

    /// <summary>Creature guids by signed event number.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<uint>> Creatures { get; init; } = new Dictionary<int, IReadOnlyList<uint>>();

    /// <summary>Gameobject guids by signed event number.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<uint>> GameObjects { get; init; } = new Dictionary<int, IReadOnlyList<uint>>();

    /// <summary>Creature data by event id.</summary>
    public IReadOnlyDictionary<ushort, IReadOnlyList<GameEventCreatureDataRecord>> CreatureData { get; init; } = new Dictionary<ushort, IReadOnlyList<GameEventCreatureDataRecord>>();

    /// <summary>Quest ids by event id.</summary>
    public IReadOnlyDictionary<ushort, IReadOnlyList<uint>> Quests { get; init; } = new Dictionary<ushort, IReadOnlyList<uint>>();

    /// <summary>Mails by signed event number (read, not delivered: the system-mail primitive is another lane's).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<GameEventMailRecord>> Mails { get; init; } = new Dictionary<int, IReadOnlyList<GameEventMailRecord>>();
}

/// <summary>What <see cref="GameEventLoader"/> built from the tables.</summary>
public sealed record GameEventLoadResult(
    GameEventDialect Dialect,
    GameEventStartBoundary Boundary,
    IReadOnlyList<GameEventDefinition> Definitions,
    IReadOnlyDictionary<ushort, GameEventSource> Sources,
    GameEventRows Rows,
    IReadOnlyList<string> Issues);

/// <summary>
/// Turns the game-event tables into schedule definitions and per-event row lists: decides the dialect, reads the zone-less
/// date text, applies the load-time rules of <see cref="GameEventValidation"/>, resolves <c>linkedTo</c> and drops rows
/// that name a missing event. Pure: the clock and the zone are arguments; every problem becomes an issue line, nothing throws.
/// </summary>
public static class GameEventLoader
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// <see cref="GameEventDialect.Auto"/> is vmangos when any <c>game_event</c> row carries its own <c>start_time</c> (a mangos-classic
    /// table keeps its dates in <c>game_event_time</c>), else mangos-classic.
    /// </summary>
    public static GameEventDialect ResolveDialect(GameEventContent content, GameEventDialect requested)
    {
        ArgumentNullException.ThrowIfNull(content);
        return requested != GameEventDialect.Auto
            ? requested
            : content.Events.Any(e => e.StartTime is not null) ? GameEventDialect.VMangos : GameEventDialect.CMangos;
    }

    /// <summary>The boundary rule of a dialect (vmangos inclusive, mangos-classic exclusive) unless the option names one.</summary>
    public static GameEventStartBoundary ResolveBoundary(GameEventStartBoundary requested, GameEventDialect dialect)
        => requested != GameEventStartBoundary.Auto
            ? requested
            : dialect == GameEventDialect.VMangos ? GameEventStartBoundary.Inclusive : GameEventStartBoundary.Exclusive;

    public static GameEventLoadResult Load(GameEventContent content, GameEventOptions options, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(zone);
        var issues = new List<string>();
        GameEventDialect dialect = ResolveDialect(content, options.Dialect);
        GameEventStartBoundary boundary = ResolveBoundary(options.StartBoundary, dialect);
        Dictionary<uint, GameEventTimeRecord> times = content.Times.GroupBy(t => t.Entry).ToDictionary(g => g.Key, g => g.Last());

        var sources = new SortedDictionary<ushort, GameEventSource>();
        var definitions = new SortedDictionary<ushort, GameEventDefinition>();
        foreach (GameEventRecord record in content.Events)
        {
            if (record.Entry == 0 || record.Entry > ushort.MaxValue)
            {
                issues.Add($"game_event id {record.Entry} is reserved or out of range and can't be used");
                continue;
            }

            var id = (ushort)record.Entry;
            DateTimeOffset? start;
            DateTimeOffset? end;
            if (dialect == GameEventDialect.VMangos)
            {
                start = ReadDate(record.StartTime, id, "start_time", zone, options.DateTimeInterpretation, issues);
                end = ReadDate(record.EndTime, id, "end_time", zone, options.DateTimeInterpretation, issues);
            }
            else
            {
                GameEventTimeRecord? time = times.GetValueOrDefault(record.Entry);
                start = time is null ? null : ReadDate(time.StartTime, id, "start_time", zone, options.DateTimeInterpretation, issues);
                end = time is null ? null : ReadDate(time.EndTime, id, "end_time", zone, options.DateTimeInterpretation, issues);
            }

            ushort linkedTo = record.LinkedTo > ushort.MaxValue ? (ushort)0 : (ushort)record.LinkedTo;
            var source = new GameEventSource(
                id, (GameEventScheduleType)(byte)Math.Clamp(record.ScheduleType, 0, byte.MaxValue), record.OccurenceMinutes, record.LengthMinutes,
                record.Holiday, linkedTo, record.Description, start, end, record.Hardcoded, record.Disabled, record.PatchMin, record.PatchMax);
            sources[id] = source;
            definitions[id] = dialect == GameEventDialect.VMangos
                ? GameEventValidation.BuildVMangos(source, issues)
                : GameEventValidation.BuildCMangos(source, now, zone, options.YearlyRebase, issues);
        }

        IReadOnlyList<GameEventDefinition> resolved = dialect == GameEventDialect.CMangos
            ? GameEventValidation.ResolveLinks([.. definitions.Values], issues)
            : [.. definitions.Values];

        var valid = resolved.Where(d => d.IsValid).Select(d => d.Id).ToHashSet();
        GameEventRows rows = BuildRows(content, valid, issues);
        return new GameEventLoadResult(dialect, boundary, resolved, sources, rows, issues);
    }

    /// <summary>The instant of a source date text, or null (with an issue) when it is missing or not <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    private static DateTimeOffset? ReadDate(string? text, ushort id, string column, TimeZoneInfo zone, GameEventDateTimeInterpretation interpretation, List<string> issues)
    {
        if (text is null)
        {
            return null;
        }

        if (!DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            issues.Add($"game_event {id}: {column} '{text}' is not a {DateFormat} date and is ignored");
            return null;
        }

        return GameEventCalendar.ReadWallTime(parsed, zone, interpretation);
    }

    private static GameEventRows BuildRows(GameEventContent content, HashSet<ushort> validEvents, List<string> issues)
    {
        bool Known(int signed, string table)
        {
            int id = Math.Abs(signed);
            if (signed == 0)
            {
                issues.Add($"{table}: game event id 0 is not allowed");
                return false;
            }

            if (id > ushort.MaxValue || !validEvents.Contains((ushort)id))
            {
                issues.Add($"{table}: game event id {signed} does not exist in game_event");
                return false;
            }

            return true;
        }

        Dictionary<int, IReadOnlyList<uint>> Group(IEnumerable<GameEventSpawnRecord> spawns, string table)
            => spawns.Where(s => Known(s.Event, table)).GroupBy(s => s.Event)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<uint>)[.. g.Select(s => s.Guid).Distinct()]);

        Dictionary<ushort, IReadOnlyList<GameEventCreatureDataRecord>> data = content.CreatureData
            .Where(d => d.Event is > 0 and <= ushort.MaxValue && Known(d.Event, "game_event_creature_data"))
            .GroupBy(d => (ushort)d.Event).ToDictionary(g => g.Key, g => (IReadOnlyList<GameEventCreatureDataRecord>)[.. g]);
        Dictionary<ushort, IReadOnlyList<uint>> quests = content.Quests
            .Where(q => q.Event is > 0 and <= ushort.MaxValue && Known(q.Event, "game_event_quest"))
            .GroupBy(q => (ushort)q.Event).ToDictionary(g => g.Key, g => (IReadOnlyList<uint>)[.. g.Select(q => q.Quest).Distinct()]);
        Dictionary<int, IReadOnlyList<GameEventMailRecord>> mails = content.Mails
            .Where(m => Known(m.Event, "game_event_mail")).GroupBy(m => m.Event)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<GameEventMailRecord>)[.. g]);

        return new GameEventRows
        {
            Creatures = Group(content.Creatures, "game_event_creature"),
            GameObjects = Group(content.GameObjects, "game_event_gameobject"),
            CreatureData = data,
            Quests = quests,
            Mails = mails,
        };
    }
}
