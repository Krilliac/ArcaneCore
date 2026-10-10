namespace ArcaneCore.Game.Creatures;

/// <summary>AzerothCore SmartScriptMgr.h SMART_EVENT values that slice 1 runs (the numbering is AzerothCore's, so rows port unchanged).</summary>
public enum SmartEvent : byte
{
    UpdateInCombat = 0,
    UpdateOutOfCombat = 1,
    HealthPct = 2,
    Aggro = 4,
    Death = 6,
    Evade = 7,
    SpellHit = 8,
    ReachedHome = 21,
    Reset = 25,
    TimedEventTriggered = 59,
    Update = 60,
    Link = 61,
}

/// <summary>AzerothCore SMART_ACTION values that slice 1 runs.</summary>
public enum SmartAction : byte
{
    None = 0,
    Talk = 1,
    Cast = 11,
    SummonCreature = 12,
    SetEventPhase = 22,
    IncEventPhase = 23,
    RandomPhase = 30,
    RandomPhaseRange = 31,
    CreateTimedEvent = 67,
    MoveToPos = 69,
    TriggerTimedEvent = 73,
    RemoveTimedEvent = 74,
}

/// <summary>AzerothCore SMART_TARGET values that slice 1 resolves.</summary>
public enum SmartTarget : byte
{
    None = 0,
    Self = 1,
    Victim = 2,
    HostileSecondAggro = 3,
    HostileLastAggro = 4,
    HostileRandom = 5,
    HostileRandomNotTop = 6,
    ActionInvoker = 7,
    Position = 8,
    CreatureRange = 9,
    CreatureDistance = 11,
    PlayerRange = 17,
    PlayerDistance = 18,
    ClosestCreature = 19,
    ClosestPlayer = 21,
    OwnerOrSummoner = 23,
    ThreatList = 24,
}

/// <summary>AzerothCore SMART_EVENT_FLAG_* (SmartScriptMgr.h:1961-1976) that matter on 1.12 (the difficulty bits do not).</summary>
[Flags]
public enum SmartEventFlags : uint
{
    None = 0,
    NotRepeatable = 0x001,
    DebugOnly = 0x080,
    DontReset = 0x100,
}

/// <summary>AzerothCore SMARTCAST_* (SmartScriptMgr.h:1981-1991) that slice 1 honours.</summary>
[Flags]
public enum SmartCastFlags : uint
{
    None = 0,
    InterruptPrevious = 0x001,
    Triggered = 0x002,
    AuraNotPresent = 0x020,
    ThreatListNotSingle = 0x080,
}
