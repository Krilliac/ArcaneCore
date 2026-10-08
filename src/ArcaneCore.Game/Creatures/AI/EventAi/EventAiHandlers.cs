using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// One EventAI event type. The engine owns the generic machinery (holders, the 500 ms timer batch, chance,
/// flags, phases, the repeat-timer reset, the combat-action contract: cmangos CreatureEventAI.cpp
/// CheckEvent / ProcessEvent / ResetEvent); a handler owns what is specific to its type: the condition, how
/// its timers are laid out and how it reacts to the lifecycle signals. Handlers are discovered by
/// <see cref="EventAiRegistry"/>, so adding an event type is adding one class.
/// </summary>
public abstract class EventAiEventHandler
{
    /// <summary>The <c>creature_ai_scripts.event_type</c> this handler runs.</summary>
    public abstract byte EventType { get; }

    /// <summary>
    /// The signal that makes the engine consider rows of this type (cmangos hooks: EnterCombat,
    /// JustRespawned, JustReachedHome, EnterEvadeMode, JustDied, KilledUnit, SpellHit).
    /// <see cref="EventAiTrigger.None"/> for types the periodic update drives or that no hook feeds yet.
    /// </summary>
    public virtual EventAiTrigger Trigger => EventAiTrigger.None;

    /// <summary>
    /// Whether the periodic update considers the event at every batch regardless of its timer and armed state
    /// (cmangos special-cases EVENT_T_TARGET_NOT_REACHABLE in UpdateEventTimers, :1937-1941).
    /// </summary>
    public virtual bool CheckedEveryBatch => false;

    /// <summary>
    /// cmangos IsRepeatableEvent: false for the events that happen once per spawn or fight (spawned, death, aggro,
    /// evade, reached home), which the repeatable flag does not gate.
    /// </summary>
    public virtual bool Repeatable => true;

    /// <summary>cmangos IsTimerBasedEvent: finishing the event re-arms its timer from the repeat parameters.</summary>
    public virtual bool TimerBased => false;

    /// <summary>cmangos IsTimerExecutedEvent: the periodic update evaluates the event whenever its timer is 0.</summary>
    public virtual bool TimerExecuted => false;

    /// <summary>Index of the repeat-minimum parameter (cmangos GetRepeatTimers: 2 for most types).</summary>
    public virtual int RepeatMinParam => 2;

    /// <summary>Index of the repeat-maximum parameter (3 for most types).</summary>
    public virtual int RepeatMaxParam => 3;

    /// <summary>
    /// The type-specific condition of cmangos <c>CheckEvent</c> (after the generic checks passed). May set
    /// <see cref="EventAiHolder.EventTarget"/>.
    /// </summary>
    public abstract bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker);

    /// <summary>
    /// A reason this particular row cannot run although its type is handled (a parameter value that needs a system that
    /// does not exist), or null. The row never fires and the reason is listed in <see cref="CreatureEventAI.Unsupported"/>.
    /// </summary>
    public virtual string? UnsupportedReason(CreatureAiEvent row) => null;

    /// <summary>Whether a row of this type is considered at all when its trigger fires (cmangos: the SPAWNED condition check).</summary>
    public virtual bool AllowTrigger(EventAiContext context, EventAiHolder holder) => true;

    /// <summary>Whether a spell hit matches this row (only for <see cref="EventAiTrigger.SpellHit"/> handlers).</summary>
    public virtual bool MatchesSpell(CreatureAiEvent row, SpellInfo spell) => false;

    /// <summary>Whether a unit that moved in sight matches this row (only for <see cref="EventAiTrigger.OutOfCombatLineOfSight"/> handlers).</summary>
    public virtual bool MatchesUnit(EventAiContext context, EventAiHolder holder, Unit who) => false;

    /// <summary>Whether a text emote matches this row (only for <see cref="EventAiTrigger.ReceiveEmote"/> handlers).</summary>
    public virtual bool MatchesEmote(CreatureAiEvent row, uint textEmote) => false;

    /// <summary>Whether an AI event of <paramref name="eventType"/> sent by <paramref name="sender"/> matches this row (only for <see cref="EventAiTrigger.AiEvent"/> handlers).</summary>
    public virtual bool MatchesAiEvent(CreatureAiEvent row, uint eventType, Unit sender) => false;

    /// <summary>cmangos JustRespawned's per-type branch for a fresh holder (default: armed, timer 0).</summary>
    public virtual void OnRespawn(EventAiContext context, EventAiHolder holder)
    {
        holder.Enabled = true;
        holder.TimerMs = 0;
    }

    /// <summary>cmangos Reset's per-type branch (default: armed, timer 0).</summary>
    public virtual void OnReset(EventAiContext context, EventAiHolder holder)
    {
        holder.Enabled = true;
        holder.TimerMs = 0;
    }

    /// <summary>cmangos EnterCombat's per-type branch (default: nothing).</summary>
    public virtual void OnEnterCombat(EventAiContext context, EventAiHolder holder)
    {
    }

    /// <summary>
    /// Whether entering combat arms the row and considers it at once (cmangos EVENT_T_AGGRO:
    /// <c>enabled = true; CheckAndReadyEventForExecution</c>).
    /// </summary>
    public virtual bool ArmsOnEnterCombat => false;
}

/// <summary>
/// One EventAI action type (cmangos <c>ProcessAction</c> case). It returns whether the action succeeded: the
/// combat-action flag (0x400) makes a failed first action leave the event's timer untouched so it retries.
/// </summary>
public abstract class EventAiActionHandler
{
    /// <summary>The <c>actionN_type</c> this handler runs.</summary>
    public abstract byte ActionType { get; }

    public abstract bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation);
}
