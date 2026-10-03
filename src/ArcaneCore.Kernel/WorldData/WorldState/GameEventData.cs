namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// One <c>game_event</c> row, in either dialect. mangos-classic (classic-db) rows have <see cref="ScheduleType"/> and
/// <see cref="LinkedTo"/> and take their dates from a <see cref="GameEventTimeRecord"/>; vmangos rows carry
/// <see cref="StartTime"/> / <see cref="EndTime"/> themselves (and <see cref="Hardcoded"/>, <see cref="Disabled"/>, the patch
/// range). The dates are the source's zone-less <c>yyyy-MM-dd HH:mm:ss</c> text: how they become instants is the
/// game-event service's <c>DateTimeInterpretation</c> option, not a storage decision.
/// </summary>
public sealed record GameEventRecord(
    uint Entry,
    int ScheduleType,
    uint OccurenceMinutes,
    uint LengthMinutes,
    uint Holiday,
    uint LinkedTo,
    string Description,
    string? StartTime = null,
    string? EndTime = null,
    bool Hardcoded = false,
    bool Disabled = false,
    byte PatchMin = 0,
    byte PatchMax = 10);

/// <summary>A <c>game_event_time</c> row (mangos-classic): the absolute dates of a date or yearly event.</summary>
public sealed record GameEventTimeRecord(uint Entry, string StartTime, string EndTime);

/// <summary>
/// A <c>game_event_creature</c> or <c>game_event_gameobject</c> row. A positive <paramref name="Event"/> spawns the object while
/// the event runs, a negative one removes it while the event runs (the object is there otherwise).
/// </summary>
public sealed record GameEventSpawnRecord(uint Guid, int Event);

/// <summary>A <c>game_event_creature_data</c> row: what a creature changes while the event runs (vmangos <c>display_id</c> is <paramref name="ModelId"/>).</summary>
public sealed record GameEventCreatureDataRecord(uint Guid, int Event, uint EntryId, uint ModelId, uint EquipmentId, uint SpellStart, uint SpellEnd);

/// <summary>A <c>game_event_quest</c> row: a quest that is only active while the event runs.</summary>
public sealed record GameEventQuestRecord(uint Quest, int Event);

/// <summary>A <c>game_event_mail</c> row: a mail sent at the start (positive event) or stop (negative) of the event.</summary>
public sealed record GameEventMailRecord(int Event, uint RaceMask, uint Quest, uint MailTemplateId, uint SenderEntry);

/// <summary>Every game-event table, as the world daemon loads them.</summary>
public sealed record GameEventContent(
    IReadOnlyList<GameEventRecord> Events,
    IReadOnlyList<GameEventTimeRecord> Times,
    IReadOnlyList<GameEventSpawnRecord> Creatures,
    IReadOnlyList<GameEventSpawnRecord> GameObjects,
    IReadOnlyList<GameEventCreatureDataRecord> CreatureData,
    IReadOnlyList<GameEventQuestRecord> Quests,
    IReadOnlyList<GameEventMailRecord> Mails)
{
    public static GameEventContent Empty { get; } = new([], [], [], [], [], [], []);
}

/// <summary>Reads the game-event tables of the world database (scoped).</summary>
public interface IGameEventDataStore
{
    Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>game_event_status</c> of the characters database (vmangos <c>sql/characters.sql:499-502</c>): the events that are running,
/// so a restart can resume them (<c>GameEventMgr::Initialize</c> reads and truncates it, GameEventMgr.cpp:679-689).
/// </summary>
public interface IGameEventStatusStore
{
    /// <summary>The events recorded as running.</summary>
    Task<IReadOnlyList<int>> LoadActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Make the stored set exactly <paramref name="events"/>, in one transaction (a failure keeps the old set). The events must
    /// be distinct. vmangos does this with an <c>INSERT</c> per start and a <c>DELETE</c> per stop; one atomic replace has the
    /// same observable result without per-provider insert-or-ignore syntax.
    /// </summary>
    Task ReplaceActiveAsync(IReadOnlyCollection<int> events, CancellationToken cancellationToken = default);
}
