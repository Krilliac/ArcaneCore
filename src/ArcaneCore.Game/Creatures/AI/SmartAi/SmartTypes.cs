namespace ArcaneCore.Game.Creatures;

/// <summary>AzerothCore SmartScriptMgr.h SMART_EVENT values that slices 1 and 2 run (the numbering is AzerothCore's, so rows port unchanged).</summary>
public enum SmartEvent : byte
{
    UpdateInCombat = 0,
    UpdateOutOfCombat = 1,
    HealthPct = 2,
    ManaPct = 3,
    Aggro = 4,
    Kill = 5,
    Death = 6,
    Evade = 7,
    SpellHit = 8,
    Range = 9,
    Respawn = 11,
    FriendlyHealth = 14,
    SummonedUnit = 17,
    ReachedHome = 21,
    ReceiveEmote = 22,
    Reset = 25,
    AiInit = 37,
    AreaTriggerOnTrigger = 46,
    TimedEventTriggered = 59,
    Update = 60,
    Link = 61,
    GossipSelect = 62,
    JustCreated = 63,
    GossipHello = 64,
}

/// <summary>AzerothCore SMART_ACTION values that slices 1-3 run.</summary>
public enum SmartAction : byte
{
    None = 0,
    Talk = 1,
    SetFaction = 2,
    MorphToEntryOrModel = 3,
    Sound = 4,
    PlayEmote = 5,
    SetReactState = 8,
    RandomEmote = 10,
    Cast = 11,
    SummonCreature = 12,
    SetEmoteState = 17,
    SetUnitFlag = 18,
    RemoveUnitFlag = 19,
    AutoAttack = 20,
    AllowCombatMovement = 21,
    SetEventPhase = 22,
    IncEventPhase = 23,
    Evade = 24,
    FleeForAssist = 25,
    RemoveAurasFromSpell = 28,
    RandomPhase = 30,
    RandomPhaseRange = 31,
    Die = 37,
    CallForHelp = 39,
    ForceDespawn = 41,
    AttackStart = 49,
    CreateTimedEvent = 67,
    MoveToPos = 69,
    TriggerTimedEvent = 73,
    RemoveTimedEvent = 74,
    CallTimedActionList = 80,
    CallRandomTimedActionList = 87,
    CallRandomRangeTimedActionList = 88,
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
