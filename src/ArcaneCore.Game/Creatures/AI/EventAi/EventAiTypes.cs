using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos-classic EventAI event types that have a handler (<c>EventAI_Type</c>,
/// src/game/AI/EventAI/CreatureEventAI.h:40-85). A row whose type has no handler is reported as unsupported
/// and never fires.
/// </summary>
public enum EventAiEventType : byte
{
    TimerInCombat = 0,
    TimerOutOfCombat = 1,
    HealthPercent = 2,
    Aggro = 4,
    Kill = 5,
    Death = 6,
    Evade = 7,
    SpellHit = 8,
    Spawned = 11,
    ReachedHome = 21,
    ReceiveEmote = 22,
}

/// <summary>cmangos-classic EventAI action types that have a handler (<c>EventAI_ActionType</c>, CreatureEventAI.h:89-159).</summary>
public enum EventAiActionType : byte
{
    None = 0,
    Text = 1,
    Cast = 11,
    Summon = 12,
    ThreatSingle = 13,
    ThreatAllPercent = 14,
    AutoAttack = 20,
    CombatMovement = 21,
    SetPhase = 22,
    IncrementPhase = 23,
    Evade = 24,
    FleeForAssist = 25,
    Die = 37,
    CallForHelp = 39,
    TextNew = 54,
    /// <summary>cmangos ACTION_T_SET_RANGED_MODE: range mode type and chase distance.</summary>
    SetRangedMode = 57,
}

/// <summary>cmangos EventAI target types (<c>Target</c>, CreatureEventAI.h:161-198); the ones with a resolver.</summary>
public enum EventAiTarget
{
    Self = 0,
    Victim = 1,
    SecondOnThreat = 2,
    LastOnThreat = 3,
    RandomOnThreat = 4,
    RandomNotTop = 5,
    Invoker = 6,

    /// <summary>The default spell target (TARGET_T_NONE = 15): no unit target is passed to the cast.</summary>
    None = 15,
}

/// <summary>cmangos EventAI event flags (<c>EventFlags</c>, CreatureEventAI.h:200-213). The column is a full 32-bit value.</summary>
[Flags]
public enum EventAiFlags : uint
{
    None = 0,

    /// <summary>EFLAG_REPEATABLE: the event repeats.</summary>
    Repeatable = 0x01,

    /// <summary>EFLAG_RANDOM_ACTION: run one of the existing actions instead of all of them.</summary>
    RandomAction = 0x20,

    /// <summary>EFLAG_DEBUG_ONLY: the row only exists in a debug build (<c>Creatures:EventAi:DebugOnlyEvents</c>).</summary>
    DebugOnly = 0x80,

    /// <summary>EFLAG_RANGED_MODE_ONLY: the row only runs while the creature is in ranged (caster) mode.</summary>
    RangedModeOnly = 0x100,

    /// <summary>EFLAG_MELEE_MODE_ONLY: the row only runs while the creature is in melee mode.</summary>
    MeleeModeOnly = 0x200,

    /// <summary>EFLAG_COMBAT_ACTION: the first action must succeed, else the event keeps its timer and retries.</summary>
    CombatAction = 0x400,
}

/// <summary>cmangos <c>SpawnedEventMode</c> (CreatureEventAI.h:216-221): the condition of a SPAWNED event.</summary>
public enum EventAiSpawnedCondition : uint
{
    Always = 0,
    Map = 1,
    Zone = 2,
}

/// <summary>What the engine does when a game signal reaches it (which holders it considers).</summary>
public enum EventAiTrigger
{
    /// <summary>Not signal driven (periodic or checked by another path).</summary>
    None,

    /// <summary>The creature entered combat (cmangos EnterCombat).</summary>
    EnterCombat,

    /// <summary>The creature spawned or respawned (cmangos JustRespawned).</summary>
    Respawn,

    /// <summary>The creature reached home after evading (cmangos JustReachedHome).</summary>
    ReachedHome,

    /// <summary>The creature started evading (cmangos EnterEvadeMode).</summary>
    Evade,

    /// <summary>The creature died (cmangos JustDied).</summary>
    Death,

    /// <summary>The creature killed a unit (cmangos KilledUnit).</summary>
    Kill,

    /// <summary>A spell hit the creature (cmangos SpellHit).</summary>
    SpellHit,

    /// <summary>A player aimed a text emote at the creature (cmangos ReceiveEmote).</summary>
    ReceiveEmote,
}

/// <summary>Everything an action handler needs about the event that fired it.</summary>
/// <param name="Random">cmangos <c>rnd</c>: a fresh random value for this action (before the modulo the action takes).</param>
/// <param name="EventId">The <c>creature_ai_scripts.id</c> of the row.</param>
/// <param name="Invoker">The unit that caused the event (cmangos actionInvoker), or null.</param>
/// <param name="Sender">The unit that sent an AI event (cmangos AIEventSender), or null.</param>
/// <param name="EventTarget">The target an event filled in for its actions (cmangos eventTarget), or null.</param>
public readonly record struct EventAiInvocation(uint Random, uint EventId, Unit? Invoker, Unit? Sender, Unit? EventTarget);
