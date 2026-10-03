using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// The game-event state machine: which events are running, when to look at them next, and what starting or stopping one does.
/// <see cref="Update"/> is vmangos / mangos-classic <c>GameEventMgr::Update</c> (GameEventMgr.cpp:708-763 and
/// mangos-classic :669-712), <see cref="StartEvent"/> / <see cref="StopEvent"/> are :72-113, <see cref="EnableEvent"/> is :115-152, and
/// <see cref="Initialize"/> is :673-696. One shell serves both data dialects; the rules that exist in only one reference are
/// guarded by <see cref="GameEventLoadResult.Dialect"/>: vmangos skips hardcoded events (a handler runs them instead) and has
/// no <c>linkedTo</c>; mangos-classic gates a linked event on its parent, skips serverside events after the first pass and
/// recomputes its computed schedules (here once per calendar day, <see cref="GameEventCalendar"/>).
/// <para>
/// The service never touches a map, a quest or a creature: the work of a start or stop goes through
/// <see cref="IGameEventEffects"/> and <see cref="IGameEventListener"/>, which keeps it testable with a clock and fakes. It
/// does no I/O: the running set goes to an <see cref="IGameEventStatusSink"/>. World thread.
/// </para>
/// </summary>
public sealed class GameEventService : IGameEventState
{
    private readonly SortedDictionary<ushort, GameEventDefinition> _definitions;
    private readonly Dictionary<ushort, GameEventSource> _sources;
    private readonly SortedSet<ushort> _active = [];
    private readonly List<IGameEventEffects> _effects = [];
    private readonly List<IGameEventListener> _listeners = [];
    private readonly Dictionary<ushort, IWorldEventHandler> _handlers = [];
    private readonly HashSet<ushort> _restoredServerside = [];
    private readonly GameEventLoadResult _load;
    private readonly GameEventOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;
    private readonly IGameEventStatusSink? _status;
    private readonly IGameEventAnnouncer? _announcer;
    private readonly Action<ushort, bool>? _persistDisabled;
    private bool _initialised;
    private DateOnly _computedFor;

    public GameEventService(
        GameEventLoadResult load,
        GameEventOptions options,
        Func<DateTimeOffset> utcNow,
        TimeZoneInfo zone,
        ILogger logger,
        IGameEventStatusSink? status = null,
        IGameEventAnnouncer? announcer = null,
        Action<ushort, bool>? persistDisabled = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _status = status;
        _announcer = announcer;
        _persistDisabled = persistDisabled;
        _definitions = new SortedDictionary<ushort, GameEventDefinition>(load.Definitions.ToDictionary(d => d.Id));
        _sources = new Dictionary<ushort, GameEventSource>(load.Sources);
        _computedFor = DateOnly.FromDateTime(GameEventCalendar.ToLocal(utcNow(), zone));
    }

    /// <summary>The dialect the tables were read in.</summary>
    public GameEventDialect Dialect => _load.Dialect;

    /// <summary>The start boundary rule in force (<see cref="GameEventOptions.StartBoundary"/> resolved against the dialect).</summary>
    public GameEventStartBoundary Boundary => _load.Boundary;

    /// <summary>The per-event row lists (spawns, creature data, quests, mails) of the tables, rows for unknown events dropped.</summary>
    public GameEventRows Rows => _load.Rows;

    /// <summary>Problems found while loading the tables (also logged by the feature).</summary>
    public IReadOnlyList<string> LoadIssues => _load.Issues;

    /// <summary>Every event, by id.</summary>
    public IReadOnlyCollection<GameEventDefinition> Events => _definitions.Values;

    public IReadOnlyCollection<ushort> ActiveEvents => [.. _active];

    /// <summary>Whether the first update has run (<c>m_IsGameEventsInit</c>).</summary>
    public bool IsInitialised => _initialised;

    public GameEventDefinition? Find(ushort eventId) => _definitions.GetValueOrDefault(eventId);

    /// <summary>vmangos <c>IsValidEvent</c>: the event exists and has a usable length.</summary>
    public bool IsValidEvent(ushort eventId) => _definitions.TryGetValue(eventId, out GameEventDefinition? definition) && definition.IsValid;

    public bool IsActiveEvent(ushort eventId) => _active.Contains(eventId);

    public bool IsActiveHoliday(uint holidayId)
    {
        if (holidayId == 0)
        {
            return false;
        }

        foreach (ushort id in _active)
        {
            if (_definitions[id].HolidayId == holidayId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Register what a start or stop does for one kind of object (spawns, creature data, quests).</summary>
    public void AddEffects(IGameEventEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        _effects.Add(effects);
    }

    /// <summary>Register a listener that is told after every start and stop.</summary>
    public void AddListener(IGameEventListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners.Add(listener);
    }

    /// <summary>Register the handler of a hardcoded event (vmangos <c>mGameEventHardcodedList</c>); one handler per event.</summary>
    public void AddWorldEventHandler(IWorldEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(handler.EventId, handler))
        {
            throw new InvalidOperationException($"game event {handler.EventId} already has a world event handler");
        }
    }

    private DateTimeOffset Now => _utcNow();

    /// <summary>
    /// <c>GameEventMgr::Initialize</c> (cpp:673-696): forget every running event, then run the first <see cref="Update"/> with the
    /// events that were running at shutdown (they resume instead of starting: no mail, no announcement difference in vmangos'
    /// code). Returns the delay in milliseconds until the next update is due.
    /// </summary>
    public uint Initialize(IReadOnlySet<ushort> activeAtShutdown)
    {
        ArgumentNullException.ThrowIfNull(activeAtShutdown);
        _active.Clear();
        _status?.Changed([]); // vmangos TRUNCATEs game_event_status once it has read it
        if (_options.RestoreServersideEvents && _load.Dialect == GameEventDialect.CMangos)
        {
            // Serverside events are never started by the schedule (their start is the far future), so a stored one is re-applied
            // here, with resume, and kept out of the first pass below that would stop it.
            foreach (ushort id in activeAtShutdown.OrderBy(i => i))
            {
                if (_definitions.TryGetValue(id, out GameEventDefinition? definition) && definition is { ScheduleType: GameEventScheduleType.Serverside, Disabled: false })
                {
                    _restoredServerside.Add(id);
                    StartEvent(id, overwrite: false, resume: true);
                }
            }
        }

        uint delay = Update(activeAtShutdown);
        _restoredServerside.Clear();
        _initialised = true;
        _logger.LogInformation("Game event system initialized");
        return delay;
    }

    /// <summary>
    /// <c>GameEventMgr::Update</c>: start the events whose window is open, stop the ones whose window closed, and return the delay in
    /// milliseconds until the earliest next change (the smallest <c>NextCheck</c> plus one second, so the event has really
    /// started or stopped by then; at most a day).
    /// </summary>
    public uint Update(IReadOnlySet<ushort>? activeAtShutdown = null)
    {
        DateTimeOffset now = Now;
        RecomputeComputedSchedules(now);
        uint nextDelay = GameEventSchedule.MaxCheckDelaySeconds;

        // vmangos: the hardcoded handlers first (cpp:714-723)
        foreach ((ushort id, IWorldEventHandler handler) in _handlers.OrderBy(h => h.Key))
        {
            if (_definitions.TryGetValue(id, out GameEventDefinition? hardcoded) && hardcoded.Disabled)
            {
                continue;
            }

            Guarded(() => handler.Update(), $"world event handler {id}");
            nextDelay = Math.Min(nextDelay, handler.NextUpdateDelaySeconds);
        }

        foreach ((ushort id, GameEventDefinition definition) in _definitions.ToArray())
        {
            if (!ShouldProcess(definition))
            {
                continue;
            }

            if (GameEventSchedule.IsActive(definition, now, _options.LeapDayMode, _load.Boundary))
            {
                if (!IsActiveEvent(id) && (_load.Dialect != GameEventDialect.CMangos || definition.LinkedTo == 0 || IsActiveEvent(definition.LinkedTo)))
                {
                    StartEvent(id, overwrite: false, resume: activeAtShutdown?.Contains(id) == true);
                }
            }
            else if (IsActiveEvent(id))
            {
                StopEvent(id, overwrite: false);
            }
            else if (!_initialised)
            {
                // the first pass: the objects that exist only while the event is NOT running appear (cpp:745-752)
                RunEffects(e => e.SpawnEvent(-id), "spawn");
            }

            nextDelay = Math.Min(nextDelay, GameEventSchedule.NextCheckSeconds(definition, now, _options.LeapDayMode));
        }

        _logger.LogDebug("Next game event check in {Seconds} seconds", nextDelay + 1);
        return (nextDelay + 1) * 1000;
    }

    /// <summary>Whether <see cref="Update"/> looks at an event at all.</summary>
    private bool ShouldProcess(GameEventDefinition definition)
    {
        if (definition.Disabled || !definition.IsValid)
        {
            return false; // vmangos: hardcoded and disabled events are skipped (cpp:727-728); an unusable one has no schedule
        }

        if (_load.Dialect == GameEventDialect.VMangos)
        {
            return !definition.Hardcoded;
        }

        // mangos-classic: an event that can never run, and a serverside event once the system is initialised (cpp:676-677)
        if (definition.ScheduleType == GameEventScheduleType.Serverside)
        {
            return !_initialised && !_restoredServerside.Contains(definition.Id);
        }

        return definition.OccurenceMinutes != 0;
    }

    /// <summary>
    /// mangos-classic recomputes yearly, lunar new year and Easter events at its weekly reset (World.cpp:2337); ArcaneCore has no
    /// weekly reset, so it recomputes when the local calendar day changes (a documented departure, a superset of the weekly one).
    /// </summary>
    private void RecomputeComputedSchedules(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(GameEventCalendar.ToLocal(now, _zone));
        if (today == _computedFor)
        {
            return;
        }

        _computedFor = today;
        if (_load.Dialect != GameEventDialect.CMangos)
        {
            return;
        }

        foreach ((ushort id, GameEventDefinition current) in _definitions.ToArray())
        {
            if (!GameEventCalendar.IsComputed(current.ScheduleType) || !_sources.TryGetValue(id, out GameEventSource? source))
            {
                continue;
            }

            var ignored = new List<string>();
            GameEventDefinition rebuilt = GameEventValidation.BuildCMangos(source, now, _zone, _options.YearlyRebase, ignored);
            _definitions[id] = rebuilt with { LinkedTo = current.LinkedTo, Disabled = current.Disabled };
        }
    }

    /// <summary>
    /// <c>StartEvent</c>: apply the event, and with <paramref name="overwrite"/> make the schedule agree (start now; an end
    /// that is not after it becomes now plus the length). Both references add the length as SECONDS to the end, which would end a
    /// manually started event after <c>length</c> seconds; the length is minutes (<c>length * MINUTE</c> everywhere else), so
    /// minutes are used. Returns false for an unknown or unusable event.
    /// </summary>
    public bool StartEvent(ushort eventId, bool overwrite = false, bool resume = false)
    {
        if (!IsValidEvent(eventId))
        {
            _logger.LogError("GameEventMgr::StartEvent game event id ({EventId}) not exist in game_event", eventId);
            return false;
        }

        ApplyNewEvent(eventId, resume);
        GameEventDefinition definition = _definitions[eventId];
        if (definition.Hardcoded && !definition.Disabled && _handlers.TryGetValue(eventId, out IWorldEventHandler? handler))
        {
            Guarded(handler.Enable, $"world event handler {eventId} enable");
        }

        if (overwrite)
        {
            DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds());
            if (_load.Boundary == GameEventStartBoundary.Exclusive)
            {
                // mangos-classic's window opens AFTER its start second; an update in this very second would otherwise stop the event just started
                start = start.AddSeconds(-1);
            }

            DateTimeOffset end = definition.End <= start ? start + TimeSpan.FromMinutes(definition.LengthMinutes) : definition.End;
            _definitions[eventId] = definition with { Start = start, End = end };
        }

        return true;
    }

    /// <summary><c>StopEvent</c>: unapply the event, and with <paramref name="overwrite"/> back-date its start by its length so the schedule does not restart it.</summary>
    public bool StopEvent(ushort eventId, bool overwrite = false)
    {
        if (!IsValidEvent(eventId))
        {
            _logger.LogError("GameEventMgr::StopEvent game event id ({EventId}) not exist in game_event", eventId);
            return false;
        }

        UnApplyEvent(eventId);
        if (overwrite)
        {
            GameEventDefinition definition = _definitions[eventId];
            DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()) - TimeSpan.FromMinutes(definition.LengthMinutes);
            DateTimeOffset end = definition.End <= start ? start + TimeSpan.FromMinutes(definition.LengthMinutes) : definition.End;
            _definitions[eventId] = definition with { Start = start, End = end };
        }

        return true;
    }

    /// <summary>
    /// <c>EnableEvent</c> (vmangos cpp:115-152): set the disabled flag (and tell the store), and stop an event that is running and is now
    /// disabled. A hardcoded event's handler is disabled or enabled instead of being stopped by the schedule.
    /// Returns false for an unknown event; true when nothing needed changing too.
    /// </summary>
    public bool EnableEvent(ushort eventId, bool enable)
    {
        if (!IsValidEvent(eventId))
        {
            _logger.LogError("GameEventMgr::EnableEvent game event id ({EventId}) not exist in game_event", eventId);
            return false;
        }

        GameEventDefinition definition = _definitions[eventId];
        if (definition.Disabled == !enable)
        {
            return true;
        }

        _definitions[eventId] = definition with { Disabled = !enable };
        Guarded(() => _persistDisabled?.Invoke(eventId, !enable), $"persisting the disabled flag of event {eventId}");
        if (!IsActiveEvent(eventId))
        {
            return true; // the next update starts it if its window is open
        }

        if (_handlers.TryGetValue(eventId, out IWorldEventHandler? handler))
        {
            Guarded(enable ? handler.Enable : handler.Disable, $"world event handler {eventId}");
        }
        else
        {
            StopEvent(eventId, overwrite: true);
        }

        return true;
    }

    private void ApplyNewEvent(ushort eventId, bool resume)
    {
        _active.Add(eventId);
        _status?.Changed([.. _active]);
        GameEventDefinition definition = _definitions[eventId];
        if (_options.Announce)
        {
            Guarded(() => _announcer?.Announce(definition.Description), "announcement");
        }

        _logger.LogInformation("GameEvent {EventId} \"{Description}\" started", eventId, definition.Description);
        RunEffects(e => e.SpawnEvent(eventId), "spawn");
        RunEffects(e => e.UnspawnEvent(-eventId), "unspawn");
        RunEffects(e => e.UpdateCreatureData(eventId, true), "creature data");
        RunEffects(e => e.UpdateEventQuests(eventId, true), "event quests");
        // The start mails (SendEventMails) are not delivered: there is no system-mail sender in this tree (docs/areas/game-events-weather.md).
        Notify(eventId, true, resume);
    }

    private void UnApplyEvent(ushort eventId)
    {
        _active.Remove(eventId);
        _status?.Changed([.. _active]);
        _logger.LogInformation("GameEvent {EventId} \"{Description}\" removed", eventId, _definitions[eventId].Description);
        RunEffects(e => e.UnspawnEvent(eventId), "unspawn");
        RunEffects(e => e.SpawnEvent(-eventId), "spawn");
        RunEffects(e => e.UpdateCreatureData(eventId, false), "creature data");
        RunEffects(e => e.UpdateEventQuests(eventId, false), "event quests");
        Notify(eventId, false, false);
    }

    private void RunEffects(Action<IGameEventEffects> phase, string what)
    {
        foreach (IGameEventEffects effects in _effects.ToArray())
        {
            Guarded(() => phase(effects), what);
        }
    }

    private void Notify(ushort eventId, bool active, bool resume)
    {
        foreach (IGameEventListener listener in _listeners.ToArray())
        {
            Guarded(() => listener.OnEventChanged(eventId, active, resume), $"listener for event {eventId}");
        }
    }

    /// <summary>A throwing effect, listener or handler is logged and never stops the world tick or the others.</summary>
    private void Guarded(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "game event {What} failed", what);
        }
    }
}
