namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// What the rest of the server may ask about game events (vmangos <c>sGameEventMgr.IsActiveEvent</c> /
/// <c>IsActiveHoliday</c>, GameEventMgr.h and :1109-1121). Implemented by <see cref="GameEventService"/>; reached through the
/// world feature that owns it. World thread.
/// </summary>
public interface IGameEventState
{
    /// <summary>Whether the event is running right now (<c>IsActiveEvent</c>).</summary>
    bool IsActiveEvent(ushort eventId);

    /// <summary>
    /// Whether an event with this client holiday id is running (<c>IsActiveHoliday</c>: holiday 0 is never active). The
    /// battleground weekend rotation and the <c>ACTIVE_HOLIDAY</c> condition read it (vmangos BattleGroundMgr.cpp:1613,
    /// Conditions.cpp:350).
    /// </summary>
    bool IsActiveHoliday(uint holidayId);

    /// <summary>The running events (a snapshot).</summary>
    IReadOnlyCollection<ushort> ActiveEvents { get; }
}

/// <summary>
/// The per-kind work an event start or stop does (vmangos <c>ApplyNewEvent</c> / <c>UnApplyEvent</c>, GameEventMgr.cpp:765-805):
/// spawns, creature data, quests. The service calls the phases in vmangos' order for every registered instance, so features
/// that each own one phase need no registration order of their own. Every method is optional (default: nothing). A throwing
/// instance is logged and does not stop the others. World thread.
/// </summary>
public interface IGameEventEffects
{
    /// <summary>
    /// <c>GameEventSpawn(event_id)</c> (cpp:807-): add the objects listed under this event number. A positive number is an
    /// event starting, a negative one is the event of that id stopping (and, on the first update, an inactive event's
    /// negative-listed objects appearing).
    /// </summary>
    void SpawnEvent(int signedEventId)
    {
    }

    /// <summary><c>GameEventUnspawn(event_id)</c>: remove the objects listed under this event number.</summary>
    void UnspawnEvent(int signedEventId)
    {
    }

    /// <summary><c>UpdateCreatureData(event_id, activate)</c> (cpp:961-1021): apply or restore the event's creature changes.</summary>
    void UpdateCreatureData(ushort eventId, bool activate)
    {
    }

    /// <summary><c>UpdateEventQuests(event_id, activate)</c> (cpp:1023-1036): turn the event's quests on or off.</summary>
    void UpdateEventQuests(ushort eventId, bool activate)
    {
    }
}

/// <summary>
/// Told after an event has started or stopped and every effect ran (mangos-classic <c>OnEventHappened</c> to instance scripts,
/// GameEventMgr.cpp:1153-1160; EventAI, battleground and war-effort scripts are the intended users). A throwing listener is
/// logged and does not stop the others. World thread.
/// </summary>
public interface IGameEventListener
{
    /// <param name="eventId">The event.</param>
    /// <param name="active">True when it started, false when it stopped.</param>
    /// <param name="resume">True when the start only resumes an event that was running before a restart.</param>
    void OnEventChanged(ushort eventId, bool active, bool resume);
}

/// <summary>
/// A vmangos hardcoded world event (<c>WorldEvent</c>, HardcodedEvents.cpp / .h: elemental invasions, the nightmare dragons,
/// Darkmoon, the Scourge invasion, the AQ war effort), the events whose <c>game_event</c> row has <c>hardcoded = 1</c>. The
/// service runs it instead of the table's schedule: it is updated on every pass, enabled when its event starts and disabled
/// when it is disabled. Only the seam exists in this lane; the handlers themselves are bespoke C++ in vmangos and are not
/// delivered (docs/areas/game-events-weather.md).
/// </summary>
public interface IWorldEventHandler
{
    /// <summary>The <c>game_event</c> entry this handler owns.</summary>
    ushort EventId { get; }

    /// <summary>Seconds until its next update is due (vmangos <c>GetNextUpdateDelay</c>).</summary>
    uint NextUpdateDelaySeconds { get; }

    void Update();

    void Enable();

    void Disable();
}

/// <summary>Where the set of running events is stored (vmangos <c>game_event_status</c>). Never throws on the world thread.</summary>
public interface IGameEventStatusSink
{
    /// <summary>The set of running events changed; persist it (best effort, retried by the next change).</summary>
    void Changed(IReadOnlyCollection<ushort> activeEvents);
}

/// <summary>Where an event start announcement goes (vmangos <c>SendWorldText(LANG_EVENTMESSAGE)</c>, GameEventMgr.cpp:788-789).</summary>
public interface IGameEventAnnouncer
{
    void Announce(string description);
}
