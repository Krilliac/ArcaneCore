using System.Collections.Frozen;

namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// What each smart-script source can run, in AzerothCore numbering (src/server/game/AI/SmartScripts/SmartScriptMgr.h), and the structural rules
/// a row must pass before the catalog accepts it. The sets here are the single source of truth: <c>SmartScriptCatalog</c> validates against
/// them and the engine enums in ArcaneCore.Game are asserted to match them in tests. Everything outside a set is rejected at load with a
/// reason, never silently skipped (ARCANECORE_CHARTER.md: unknown input fails closed).
/// </summary>
public static class SmartScriptSupport
{
    // ---- events the engine runs ----

    private static readonly FrozenSet<byte> CreatureEvents = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 14, 17, 21, 22, 25, 59, 60, 61, 62, 64 }.ToFrozenSet();

    // GO: UPDATE_OOC, AI_INIT, TIMED_EVENT_TRIGGERED, UPDATE, LINK, JUST_CREATED, GOSSIP_HELLO (the ArcaneCore GO hooks: Update and OnUse).
    private static readonly FrozenSet<byte> GameObjectEvents = new byte[] { 1, 37, 59, 60, 61, 63, 64 }.ToFrozenSet();

    // Area trigger: AREATRIGGER_ONTRIGGER and LINK.
    private static readonly FrozenSet<byte> AreaTriggerEvents = new byte[] { 46, 61 }.ToFrozenSet();

    // ---- events AzerothCore allows per source (SmartAIEventMask, SmartScriptMgr.h:1844-1930), used only to word a rejection ----

    /// <summary>Creature-source events AC forbids: transport 41-44, instance 45, area trigger 46, quest 47-51, GO-only 70-71.</summary>
    private static readonly FrozenSet<byte> CreatureForbidden = new byte[] { 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 70, 71 }.ToFrozenSet();

    /// <summary>GO-source events AC allows (the mask rows with SMART_SCRIPT_TYPE_MASK_GAMEOBJECT).</summary>
    private static readonly FrozenSet<byte> GameObjectAcEvents =
        new byte[] { 1, 8, 11, 17, 19, 20, 35, 37, 38, 52, 59, 60, 61, 62, 63, 64, 66, 68, 69, 70, 71, 77, 82 }.ToFrozenSet();

    // ---- actions ----

    private static readonly FrozenSet<byte> CreatureActions = new byte[] { 1, 2, 3, 4, 5, 8, 10, 11, 12, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28, 30, 31, 37, 39, 41, 49, 67, 69, 73, 74, 80, 87, 88 }.ToFrozenSet();

    // SUMMON_CREATURE (12) and MOVE_TO_POS (69) need a creature owner; a GO has no summoner or motion master here.
    private static readonly FrozenSet<byte> GameObjectActions = new byte[] { 1, 11, 22, 23, 30, 31, 67, 73, 74, 80, 87, 88 }.ToFrozenSet();

    private static readonly FrozenSet<byte> AreaTriggerActions = new byte[] { 1, 80, 87, 88 }.ToFrozenSet();

    // ---- targets (SMART_TARGET ids) ----

    private static readonly FrozenSet<byte> CreatureTargets = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 17, 18, 19, 21, 23, 24 }.ToFrozenSet();

    private static readonly FrozenSet<byte> GameObjectTargets = new byte[] { 0, 1, 7, 8, 9, 11, 17, 18, 19, 21 }.ToFrozenSet();

    // Area trigger: the invoking player is the reference point (AC GetWorldObjectsInDist needs a base object, SmartScript.cpp:4293-4297).
    private static readonly FrozenSet<byte> AreaTriggerTargets = new byte[] { 7, 19, 21 }.ToFrozenSet();

    /// <summary>The SMART_EVENT ids the engine runs for <paramref name="source"/> (a timed action list ignores its stored event type).</summary>
    public static IReadOnlySet<byte> Events(SmartScriptSource source) => source switch
    {
        SmartScriptSource.Creature => CreatureEvents,
        SmartScriptSource.GameObject => GameObjectEvents,
        SmartScriptSource.AreaTrigger => AreaTriggerEvents,
        _ => FrozenSet<byte>.Empty,
    };

    /// <summary>The SMART_ACTION ids the engine runs for <paramref name="source"/> (a timed action list runs its owner's: the creature set).</summary>
    public static IReadOnlySet<byte> Actions(SmartScriptSource source) => source switch
    {
        SmartScriptSource.Creature or SmartScriptSource.TimedActionList => CreatureActions,
        SmartScriptSource.GameObject => GameObjectActions,
        SmartScriptSource.AreaTrigger => AreaTriggerActions,
        _ => FrozenSet<byte>.Empty,
    };

    /// <summary>The SMART_TARGET ids the engine resolves for <paramref name="source"/>.</summary>
    public static IReadOnlySet<byte> Targets(SmartScriptSource source) => source switch
    {
        SmartScriptSource.Creature or SmartScriptSource.TimedActionList => CreatureTargets,
        SmartScriptSource.GameObject => GameObjectTargets,
        SmartScriptSource.AreaTrigger => AreaTriggerTargets,
        _ => FrozenSet<byte>.Empty,
    };

    /// <summary>Whether AzerothCore's SmartAIEventMask lets <paramref name="eventType"/> be used by <paramref name="source"/> (SmartScriptMgr.h:1844-1930).</summary>
    public static bool AcAllowsEvent(SmartScriptSource source, byte eventType) => source switch
    {
        SmartScriptSource.Creature => (eventType <= 77 || eventType == 82) && !CreatureForbidden.Contains(eventType),
        SmartScriptSource.GameObject => GameObjectAcEvents.Contains(eventType),
        SmartScriptSource.AreaTrigger => eventType is 46 or 61,
        _ => false,
    };

    /// <summary>
    /// The structural rules of one accepted-so-far row, steps 6-9 of the catalog order: event, action and target for its source, then the
    /// parameter rules. Returns the reason the engine cannot run it, or null. <paramref name="timedListExists"/> says whether a timed action list
    /// id has rows; <paramref name="anyTimedListIn"/> (inclusive range) is needed only to check action 88 - without it that check is skipped.
    /// </summary>
    public static string? Check(SmartScriptRow row, Func<uint, bool> timedListExists, Func<uint, uint, bool>? anyTimedListIn = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(timedListExists);
        var source = (SmartScriptSource)row.SourceType;
        bool list = source == SmartScriptSource.TimedActionList;

        if (!list)
        {
            if (!AcAllowsEvent(source, row.EventType))
            {
                return $"AzerothCore forbids event {row.EventType} for source type {row.SourceType} (SmartScriptMgr.h:1844-1930)";
            }

            if (!Events(source).Contains(row.EventType))
            {
                return $"event {row.EventType} has no ArcaneCore hook for source type {row.SourceType}";
            }
        }

        if (!Actions(source).Contains(row.ActionType))
        {
            string why = (source, row.ActionType) switch
            {
                (SmartScriptSource.GameObject, 12) => ": CreatureMapSystem.SummonAt needs a creature summoner",
                (SmartScriptSource.GameObject, 69) => ": a game object has no motion master",
                (SmartScriptSource.GameObject, 24) => ": a game object cannot evade",
                (SmartScriptSource.GameObject, 20 or 21 or 25 or 37 or 39 or 49) => ": the action acts on the script's own creature",
                (SmartScriptSource.AreaTrigger, 11) => ": AzerothCore summons a trigger creature to cast and ArcaneCore has none",
                (SmartScriptSource.AreaTrigger, 22 or 23 or 30 or 31 or 67 or 73 or 74) => ": the area-trigger script lives for one trigger only",
                _ => string.Empty,
            };
            return $"action {row.ActionType} is not supported for source type {row.SourceType}{why}";
        }

        if (!Targets(source).Contains(row.TargetType))
        {
            string why = source == SmartScriptSource.AreaTrigger && row.TargetType is 9 or 11 or 17 or 18
                ? ": range targets search around a base object and an area trigger has none"
                : string.Empty;
            return $"target {row.TargetType} is not supported for source type {row.SourceType}{why}";
        }

        if (list)
        {
            if (row.EventParam1 > row.EventParam2)
            {
                return $"timed action list row has delay min {row.EventParam1} above max {row.EventParam2} (SmartScriptMgr.cpp:1083-1091)";
            }

            if (row.EventParam3 > row.EventParam4)
            {
                return $"timed action list row has repeat min {row.EventParam3} above max {row.EventParam4} (SmartScriptMgr.cpp:1083-1091)";
            }

            if (row.Link != 0)
            {
                return "timed action list row has a link; links from timed action lists are not supported";
            }
        }

        if (!list && CheckEventParams(row) is { } badEvent)
        {
            return badEvent;
        }

        if (CheckActionParams(row) is { } badAction)
        {
            return badAction;
        }

        if (row.EventType == 64 && row.EventParam1 > 1)
        {
            return row.EventParam1 == 2
                ? "GOSSIP_HELLO filter 2 (report use only) can never fire: CMSG_GAMEOBJ_REPORT_USE does not exist in 1.12"
                : $"GOSSIP_HELLO filter {row.EventParam1} is not 0 or 1";
        }

        return CheckTimedListCall(row, timedListExists, anyTimedListIn);
    }

    /// <summary>The event parameter rules of SmartAIMgr::IsEventValid (SmartScriptMgr.cpp:1096-1200) for the events slice 3d added.</summary>
    private static string? CheckEventParams(SmartScriptRow row)
    {
        switch (row.EventType)
        {
            case 3:
                // SMART_EVENT_MANA_PCT: IsMinMaxValid on the band and the repeat.
                if (row.EventParam1 > row.EventParam2)
                {
                    return $"MANA_PCT mana {row.EventParam1}..{row.EventParam2} has min above max (SmartScriptMgr.cpp:1101-1106)";
                }

                return row.EventParam3 > row.EventParam4 ? $"MANA_PCT repeat {row.EventParam3}..{row.EventParam4} has min above max" : null;
            case 9:
                // SMART_EVENT_RANGE: the first timer, the repeat and the range (params 5-6) are each a min/max pair (SmartScriptMgr.cpp:1134-1145).
                if (row.EventParam1 > row.EventParam2)
                {
                    return $"RANGE first timer {row.EventParam1}..{row.EventParam2} has min above max (SmartScriptMgr.cpp:1134-1145)";
                }

                if (row.EventParam3 > row.EventParam4)
                {
                    return $"RANGE repeat {row.EventParam3}..{row.EventParam4} has min above max (SmartScriptMgr.cpp:1134-1145)";
                }

                return row.EventParam5 > row.EventParam6
                    ? $"RANGE range {row.EventParam5}..{row.EventParam6} has min above max (SmartScriptMgr.cpp:1134-1145)" : null;
            case 14:
                // SMART_EVENT_FRIENDLY_HEALTH: a radius, and a valid repeat (SmartScriptMgr.cpp:1190-1196).
                if (row.EventParam2 == 0)
                {
                    return "FRIENDLY_HEALTH radius 0 finds nobody (SmartScriptMgr.cpp:1190-1196)";
                }

                return row.EventParam3 > row.EventParam4 ? $"FRIENDLY_HEALTH repeat {row.EventParam3}..{row.EventParam4} has min above max" : null;
            default:
                return null;
        }
    }

    /// <summary>The action parameter rules of SmartAIMgr::IsActionValid (SmartScriptMgr.cpp) for the actions slice 3d added.</summary>
    private static string? CheckActionParams(SmartScriptRow row) => row.ActionType switch
    {
        // SMART_ACTION_SET_REACT_STATE: REACT_PASSIVE 0 .. REACT_AGGRESSIVE 2 (SmartScriptMgr.cpp:1753-1758).
        8 when row.ActionParam1 > 2 => $"SET_REACT_STATE react state {row.ActionParam1} is not 0, 1 or 2 (SmartScriptMgr.cpp:1753-1758)",

        // SMART_ACTION_RANDOM_EMOTE: at least one emote (SmartScriptMgr.cpp:1599-1605).
        10 when row.ActionParam1 == 0 && row.ActionParam2 == 0 && row.ActionParam3 == 0 && row.ActionParam4 == 0 && row.ActionParam5 == 0
            && row.ActionParam6 == 0 => "RANDOM_EMOTE has no non-zero emote (SmartScriptMgr.cpp:1599-1605)",

        // SMART_ACTION_SET/REMOVE_UNIT_FLAG type 1 is UNIT_FIELD_FLAGS_2, a field 1.12 does not have.
        18 or 19 when row.ActionParam2 != 0 => $"action {row.ActionType} type {row.ActionParam2} names UNIT_FIELD_FLAGS_2, which 1.12 does not have",
        _ => null,
    };

    private static string? CheckTimedListCall(SmartScriptRow row, Func<uint, bool> timedListExists, Func<uint, uint, bool>? anyTimedListIn)
    {
        const byte callList = 80, callRandom = 87, callRandomRange = 88;
        if (row.ActionType is not (callList or callRandom or callRandomRange))
        {
            return null;
        }

        // SmartScript.cpp:2136-2160: 80/87/88 with TARGET_NONE log an error and do nothing.
        if (row.TargetType == 0)
        {
            return $"action {row.ActionType} needs a target to run the list on (target 0 does nothing)";
        }

        switch (row.ActionType)
        {
            case callList:
                // SmartScriptMgr.cpp:2000-2001: allowOverride is a SAIBool.
                if (row.ActionParam3 > 1)
                {
                    return $"action 80 allowOverride {row.ActionParam3} is not 0 or 1";
                }

                return timedListExists(row.ActionParam1) ? null : $"action 80 calls timed action list {row.ActionParam1}, which has no usable rows";
            case callRandom:
            {
                uint[] ids = [row.ActionParam1, row.ActionParam2, row.ActionParam3, row.ActionParam4, row.ActionParam5, row.ActionParam6];
                if (ids.All(id => id == 0))
                {
                    return "action 87 names no timed action list (SmartScriptMgr.cpp:1829-1833)";
                }

                uint missing = ids.FirstOrDefault(id => id != 0 && !timedListExists(id));
                return missing == 0 ? null : $"action 87 calls timed action list {missing}, which has no usable rows";
            }

            default:
                if (row.ActionParam1 > row.ActionParam2)
                {
                    return $"action 88 range {row.ActionParam1}..{row.ActionParam2} has min above max (SmartScriptMgr.cpp:1613-1620)";
                }

                return anyTimedListIn is null || anyTimedListIn(row.ActionParam1, row.ActionParam2)
                    ? null : $"action 88 range {row.ActionParam1}..{row.ActionParam2} holds no usable timed action list";
        }
    }
}
