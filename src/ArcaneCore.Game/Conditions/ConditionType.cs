namespace ArcaneCore.Game.Conditions;

/// <summary>
/// The condition type ids of the <c>conditions</c> table. The numbering is cmangos's
/// (D:\refs\mangos-classic\src\game\Globals\Conditions.h:30-82), which is the numbering classic-db
/// data is written in. It is NOT vmangos's Conditions.h numbering: vmangos reuses ids 11, 13, 27, 28,
/// 31, 35 and 40 for different meanings (11 SAVED_VARIABLE, 13 CANT_PATH_TO_VICTIM, 27 GENDER,
/// 35 MAP_EVENT_DATA, 40 HAS_PET), so reading gender or area-flag rows with the vmangos table would
/// silently evaluate the wrong thing. A test pins every id below.
/// </summary>
public enum ConditionType
{
    /// <summary>value1: condition id; returns !cond (Conditions.h:30).</summary>
    Not = -3,

    /// <summary>value1/value2 (and optional value3/value4): condition ids; any true (Conditions.h:31).</summary>
    Or = -2,

    /// <summary>value1/value2 (and optional value3/value4): condition ids; all true (Conditions.h:32).</summary>
    And = -1,

    /// <summary>Always met (Conditions.h:33).</summary>
    None = 0,

    /// <summary>value1 spell id, value2 effect index (Conditions.h:34).</summary>
    Aura = 1,

    /// <summary>value1 item id, value2 count; inventory without the bank (Conditions.h:35).</summary>
    Item = 2,

    /// <summary>value1 item id (Conditions.h:36).</summary>
    ItemEquipped = 3,

    /// <summary>value1 area/zone id, value2 0 in / 1 not in (Conditions.h:37).</summary>
    AreaId = 4,

    /// <summary>value1 faction id, value2 minimum rank (Conditions.h:38).</summary>
    ReputationRankMin = 5,

    /// <summary>value1 team: 469 Alliance, 67 Horde (Conditions.h:39).</summary>
    Team = 6,

    /// <summary>value1 skill id, value2 minimum base value (Conditions.h:40).</summary>
    Skill = 7,

    /// <summary>value1 quest id; rewarded and not repeatable (Conditions.h:41).</summary>
    QuestRewarded = 8,

    /// <summary>value1 quest id, value2 0 any state / 1 incomplete / 2 completed (Conditions.h:42).</summary>
    QuestTaken = 9,

    /// <summary>One of the AD commission auras is active (Conditions.h:43).</summary>
    AdCommissionAura = 10,

    /// <summary>value1 minimum, value2 maximum honor rank (Conditions.h:44).</summary>
    PvpRank = 11,

    /// <summary>value1 game event id (Conditions.h:45).</summary>
    ActiveGameEvent = 12,

    /// <summary>value1 area flags required, value2 area flags forbidden (Conditions.h:46).</summary>
    AreaFlag = 13,

    /// <summary>value1 race mask, value2 class mask (Conditions.h:47).</summary>
    RaceClass = 14,

    /// <summary>value1 level, value2 0 equal / 1 at least / 2 at most (Conditions.h:48).</summary>
    Level = 15,

    /// <summary>value1 spell id, value2 0 has / 1 has not (Conditions.h:50).</summary>
    Spell = 17,

    /// <summary>Instance script specific (Conditions.h:51).</summary>
    InstanceScript = 18,

    /// <summary>value1 quest id; the player can take the quest (Conditions.h:52).</summary>
    QuestAvailable = 19,

    /// <summary>value1 quest id; not taken and not rewarded (Conditions.h:55).</summary>
    QuestNone = 22,

    /// <summary>value1 item id, value2 count; bags and bank (Conditions.h:56).</summary>
    ItemWithBank = 23,

    /// <summary>value1 holiday id (Conditions.h:59).</summary>
    ActiveHoliday = 26,

    /// <summary>value1 spell id, value2 optional item id (Conditions.h:61).</summary>
    LearnableAbility = 28,

    /// <summary>value1 skill id, value2 value; 1 means "does not have the skill" (Conditions.h:65).</summary>
    SkillBelow = 29,

    /// <summary>value1 faction id, value2 maximum rank (Conditions.h:68).</summary>
    ReputationRankMax = 30,

    /// <summary>Dungeon encounter completed (Conditions.h:69).</summary>
    CompletedEncounter = 31,

    /// <summary>Last waypoint of a creature (Conditions.h:71).</summary>
    LastWaypoint = 33,

    /// <summary>value1 0 male / 1 female / 2 none (Conditions.h:73).</summary>
    Gender = 35,

    /// <summary>Dead or away (Conditions.h:74).</summary>
    DeadOrAway = 36,

    /// <summary>A creature entry within range (Conditions.h:76).</summary>
    CreatureInRange = 37,

    /// <summary>PvP script condition (Conditions.h:77).</summary>
    PvpScript = 38,

    /// <summary>Creature spawn count (Conditions.h:78).</summary>
    SpawnCount = 39,

    /// <summary>World script condition (Conditions.h:79).</summary>
    WorldScript = 40,

    /// <summary>World state comparison (Conditions.h:81).</summary>
    WorldState = 42,

    /// <summary>In combat / out of combat (Conditions.h:82).</summary>
    IsInCombat = 43,
}

/// <summary>The <c>flags</c> column (Conditions.h:87-88).</summary>
[Flags]
public enum ConditionFlags : byte
{
    None = 0,

    /// <summary>CONDITION_FLAG_REVERSE_RESULT: the result of the condition is negated.</summary>
    ReverseResult = 0x1,

    /// <summary>CONDITION_FLAG_SWAP_TARGETS: source and target are exchanged before evaluating.</summary>
    SwapTargets = 0x2,
}
