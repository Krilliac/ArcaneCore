using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The EventAI machinery of one creature, re-implemented from mangos-classic <c>CreatureEventAI</c>
/// (src/game/AI/EventAI/CreatureEventAI.cpp): the holders, the periodic timer batch (<c>UpdateEventTimers</c>,
/// :1929), the ready lists with a nesting depth (<c>ProcessEvents</c>, :244), <c>CheckEvent</c> (:255) for the
/// generic gates, <c>ProcessEvent</c> (:598) for chance, action selection and the combat-action contract,
/// <c>ResetEvent</c> (:575), and the lifecycle signals (<c>JustRespawned</c> :1382, <c>Reset</c> :1425,
/// <c>JustReachedHome</c> :1462, <c>EnterEvadeMode</c> :1475, <c>JustDied</c> :1492, <c>KilledUnit</c> :1516,
/// <c>EnterCombat</c> :1597, <c>SpellHit</c> :1667). Type-specific behaviour lives in
/// <see cref="EventAiEventHandler"/> and <see cref="EventAiActionHandler"/> classes.
/// </summary>
public sealed class EventAiEngine
{
    private readonly CreatureEventAI _ai;
    private readonly EventAiRegistry _registry;
    private readonly List<List<EventAiHolder>> _ready = [];
    private List<EventAiHolder> _holders = [];
    private int _depth;
    private uint _updateTimeMs;
    private uint _diffMs;

    internal EventAiEngine(CreatureEventAI ai, EventAiRegistry registry, EventAiOptions options)
    {
        _ai = ai;
        _registry = registry;
        Context = new EventAiContext(ai, options);
        Rebuild();
    }

    public EventAiContext Context { get; }

    /// <summary>The live rows (cmangos m_CreatureEventAIList).</summary>
    public IReadOnlyList<EventAiHolder> Holders => _holders;

    /// <summary>Unsupported event or action types found in this creature's rows ("event type 77 (row 2)").</summary>
    public IReadOnlyList<string> Unsupported { get; private set; } = [];

    // --- construction ---------------------------------------------------------------------------

    /// <summary>
    /// cmangos InitAI (:102-163): the holders of the creature's entry rows, then its spawn-guid rows; debug-only
    /// rows are skipped unless enabled. Every holder starts armed with timer 0.
    /// </summary>
    private void Rebuild()
    {
        Creature me = Context.Me;
        IEnumerable<CreatureAiEvent> rows = Context.Content.GetEvents(me.Entry);
        if (me.Spawn is { } spawn)
        {
            rows = rows.Concat(Context.Content.GetGuidEvents(spawn.Guid));
        }

        _holders = [.. rows
            .Where(r => Context.Options.DebugOnlyEvents || ((EventAiFlags)r.Flags & EventAiFlags.DebugOnly) == 0)
            .Select(r => new EventAiHolder(r, _registry.FindEvent(r.EventType)))];
        Unsupported = [.. _holders.SelectMany(Describe).Distinct()];
    }

    private IEnumerable<string> Describe(EventAiHolder holder)
    {
        if (holder.Handler is null)
        {
            yield return $"event type {holder.Event.EventType} (row {holder.Event.Id})";
        }
        else if (holder.Handler.UnsupportedReason(holder.Event) is { } reason)
        {
            yield return $"{reason} (row {holder.Event.Id})";
        }

        foreach (CreatureAiAction action in holder.Event.Actions)
        {
            if (!action.IsEmpty && _registry.FindAction(action.Type) is null)
            {
                yield return $"action type {action.Type} (row {holder.Event.Id})";
            }
        }
    }

    // --- lifecycle signals -----------------------------------------------------------------------

    /// <summary>cmangos JustRespawned: fresh holders, the update timer reset, then each type's respawn branch and the spawned events.</summary>
    public void Respawn()
    {
        Rebuild();
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is not { } handler)
            {
                continue;
            }

            handler.OnRespawn(Context, holder);
            if (handler.Trigger == EventAiTrigger.Respawn && handler.AllowTrigger(Context, holder))
            {
                CheckAndReady(holder, null, null);
            }
        }

        ProcessEvents(null, null);
    }

    /// <summary>cmangos Reset: the update timer, then every type's reset branch.</summary>
    public void Reset()
    {
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
        foreach (EventAiHolder holder in _holders)
        {
            holder.Handler?.OnReset(Context, holder);
        }
    }

    /// <summary>cmangos JustReachedHome: the reached-home events, then <see cref="Reset"/>.</summary>
    public void ReachedHome()
    {
        Dispatch(EventAiTrigger.ReachedHome, null, null);
        Reset();
    }

    /// <summary>cmangos EnterEvadeMode: the evade events.</summary>
    public void Evade() => Dispatch(EventAiTrigger.Evade, null, null);

    /// <summary>cmangos JustDied: <see cref="Reset"/>, the death events, then the phase returns to 0.</summary>
    public void Death(Unit? killer)
    {
        Reset();
        Dispatch(EventAiTrigger.Death, killer, null);
        Context.Phase = 0;
    }

    /// <summary>cmangos KilledUnit.</summary>
    public void Kill(Unit victim) => Dispatch(EventAiTrigger.Kill, victim, null);

    /// <summary>cmangos EnterCombat: per-type combat branches (in-combat timers re-armed, aggro events armed and considered), then the batch.</summary>
    public void EnterCombat(Unit enemy)
    {
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is not { } handler)
            {
                continue;
            }

            handler.OnEnterCombat(Context, holder);
            if (handler.ArmsOnEnterCombat)
            {
                holder.Enabled = true;
                CheckAndReady(holder, enemy, null);
            }
        }

        ProcessEvents(enemy, null);
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
    }

    /// <summary>cmangos SpellHit: rows whose spell id and school mask match the hit.</summary>
    public void SpellHit(Unit caster, SpellInfo spell)
    {
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is { Trigger: EventAiTrigger.SpellHit } handler && handler.MatchesSpell(holder.Event, spell))
            {
                CheckAndReady(holder, caster, null);
            }
        }

        ProcessEvents(caster, null);
    }

    /// <summary>
    /// cmangos CreatureEventAI::ReceiveEmote (:1829-1842): every row whose emote is <paramref name="textEmote"/> is readied with the
    /// player as the invoker, then the batch runs.
    /// </summary>
    public void ReceiveEmote(Player player, uint textEmote)
    {
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is { Trigger: EventAiTrigger.ReceiveEmote } handler && handler.MatchesEmote(holder.Event, textEmote))
            {
                CheckAndReady(holder, player, null);
            }
        }

        ProcessEvents(player, null);
    }

    /// <summary>
    /// cmangos CreatureEventAI::MoveInLineOfSight (:1621-1648): while the creature has no victim, every EVENT_T_OOC_LOS row whose hostility,
    /// player-only and range conditions <paramref name="who"/> meets is readied with it as the invoker.
    /// </summary>
    public void MoveInLineOfSight(Unit who)
    {
        if (Context.Victim is not null)
        {
            return;
        }

        DispatchWhere(EventAiTrigger.OutOfCombatLineOfSight, holder => holder.Handler!.MatchesUnit(Context, holder, who), who, null);
    }

    /// <summary>cmangos JustSummoned (the EVENT_T_SUMMONED_UNIT rows; the summoned creature is the invoker).</summary>
    public void JustSummoned(Creature summoned) => DispatchWhere(EventAiTrigger.Summoned, static _ => true, summoned, null);

    /// <summary>cmangos SummonedCreatureJustDied (:1540-1549).</summary>
    public void SummonedJustDied(Creature summoned) => DispatchWhere(EventAiTrigger.SummonedDied, static _ => true, summoned, null);

    /// <summary>cmangos SummonedCreatureDespawn (:1551-1560).</summary>
    public void SummonedDespawned(Creature summoned) => DispatchWhere(EventAiTrigger.SummonedDespawned, static _ => true, summoned, null);

    /// <summary>
    /// cmangos CreatureEventAI::ReceiveAIEvent (:1562-1574): the EVENT_T_RECEIVE_AI_EVENT rows of <paramref name="eventType"/> whose sender
    /// entry is 0 or the sender's are readied with the invoker and the sender (TARGET_T_EVENT_SENDER).
    /// </summary>
    public void ReceiveAiEvent(uint eventType, Creature sender, Unit? invoker)
    {
        ArgumentNullException.ThrowIfNull(sender);
        DispatchWhere(EventAiTrigger.AiEvent,
            holder => holder.Param(0) == eventType && (holder.Param(1) == 0 || holder.Param(1) == sender.Entry), invoker, sender);
    }

    /// <summary>cmangos SpellHitTarget (:1680-1691): the EVENT_T_SPELLHIT_TARGET rows whose spell id and school mask match.</summary>
    public void SpellHitTarget(Unit target, SpellInfo spell)
        => DispatchWhere(EventAiTrigger.SpellHitTarget, holder => holder.Handler!.MatchesSpell(holder.Event, spell), target, null);

    private void DispatchWhere(EventAiTrigger trigger, Func<EventAiHolder, bool> match, Unit? invoker, Unit? sender)
    {
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is { } handler && handler.Trigger == trigger && match(holder))
            {
                CheckAndReady(holder, invoker, sender);
            }
        }

        ProcessEvents(invoker, sender);
    }

    private void Dispatch(EventAiTrigger trigger, Unit? invoker, Unit? sender)
    {
        EnsureDepth();
        foreach (EventAiHolder holder in _holders)
        {
            if (holder.Handler is { } handler && handler.Trigger == trigger && handler.AllowTrigger(Context, holder))
            {
                CheckAndReady(holder, invoker, sender);
            }
        }

        ProcessEvents(invoker, sender);
    }

    // --- periodic update ----------------------------------------------------------------------------

    /// <summary>
    /// cmangos UpdateEventTimers (:1929): events are evaluated once per <see cref="EventAiOptions.UpdateIntervalMs"/>.
    /// Between batches the elapsed time accumulates; at a batch every timer drops by the accumulated time unless the
    /// current phase hides the event, and armed events at timer 0 that are timer-executed are considered.
    /// </summary>
    public void UpdateEventTimers(uint diffMs)
    {
        if (_updateTimeMs < diffMs)
        {
            _diffMs += diffMs;
            EnsureDepth();
            foreach (EventAiHolder holder in _holders)
            {
                if (holder.Handler is { CheckedEveryBatch: true })
                {
                    CheckAndReady(holder, null, null);
                    continue;
                }

                if (holder.TimerMs != 0 && (holder.Event.InversePhaseMask & (1u << Context.Phase)) == 0)
                {
                    holder.TimerMs = holder.TimerMs > _diffMs ? holder.TimerMs - _diffMs : 0;
                }

                if (!holder.Enabled || holder.TimerMs != 0)
                {
                    continue;
                }

                if (holder.Handler is { TimerExecuted: true })
                {
                    CheckAndReady(holder, null, null);
                }
            }

            ProcessEvents(null, null);
            _diffMs = 0;
            _updateTimeMs = Context.Options.UpdateIntervalMs;
        }
        else
        {
            _diffMs += diffMs;
            _updateTimeMs -= diffMs;
        }
    }

    // --- checking and processing -----------------------------------------------------------------------

    private void EnsureDepth()
    {
        while (_ready.Count <= _depth)
        {
            _ready.Add([]);
        }
    }

    private void ProcessEvents(Unit? invoker, Unit? sender)
    {
        int current = _depth;
        EnsureDepth();
        _depth++;
        foreach (EventAiHolder holder in _ready[current].ToArray())
        {
            ProcessEvent(holder, invoker, sender);
        }

        _ready[_depth - 1].Clear();
        _depth--;
    }

    private void CheckAndReady(EventAiHolder holder, Unit? invoker, Unit? sender)
    {
        if (CheckEvent(holder, invoker))
        {
            holder.InProgress = true;
            EnsureDepth();
            _ready[_depth].Add(holder);
        }
    }

    /// <summary>cmangos CheckEvent (:255-557): the generic gates, then the type's own condition.</summary>
    private bool CheckEvent(EventAiHolder holder, Unit? invoker)
    {
        if (!holder.Enabled || holder.TimerMs != 0 || holder.InProgress || holder.Handler is not { } handler)
        {
            return false;
        }

        var flags = (EventAiFlags)holder.Event.Flags;
        if ((flags & EventAiFlags.CombatAction) != 0 && !Context.CanExecuteCombatAction)
        {
            return false;
        }

        // The event does not trigger while the current phase bit is set in the inverse mask.
        if ((holder.Event.InversePhaseMask & (1u << Context.Phase)) != 0)
        {
            return false;
        }

        if (Context.RangedMode ? (flags & EventAiFlags.MeleeModeOnly) != 0 : (flags & EventAiFlags.RangedModeOnly) != 0)
        {
            return false;
        }

        return handler.Check(Context, holder, invoker);
    }

    /// <summary>cmangos ResetEvent (:575-587): re-arm the repeat timer of timer-based events; disable non-repeating ones.</summary>
    private void ResetEvent(EventAiHolder holder)
    {
        if (holder.Handler is not { } handler)
        {
            return;
        }

        if (handler.TimerBased)
        {
            holder.UpdateRepeatTimer(Context, holder.Param(handler.RepeatMinParam), holder.Param(handler.RepeatMaxParam));
        }

        if (handler.Repeatable && ((EventAiFlags)holder.Event.Flags & EventAiFlags.Repeatable) == 0)
        {
            holder.Enabled = false;
        }
    }

    /// <summary>cmangos ProcessEvent (:598-663).</summary>
    private void ProcessEvent(EventAiHolder holder, Unit? invoker, Unit? sender)
    {
        CreatureAiEvent row = holder.Event;
        var flags = (EventAiFlags)row.Flags;
        if ((flags & EventAiFlags.CombatAction) != 0 && !Context.CanExecuteCombatAction)
        {
            holder.InProgress = false;
            return;
        }

        uint random = Context.NextRandom();

        // A failed chance roll re-arms the timer (and disables a non-repeating event) without running anything.
        if (row.Chance <= random % 100)
        {
            ResetEvent(holder);
            holder.InProgress = false;
            return;
        }

        random = Context.NextRandom();
        bool actionSuccess = false;
        if ((flags & EventAiFlags.RandomAction) == 0)
        {
            actionSuccess = ProcessAction(row.Action1, random, row.Id, invoker, sender, holder.EventTarget);
            if ((flags & EventAiFlags.CombatAction) == 0 || actionSuccess)
            {
                ProcessAction(row.Action2, random, row.Id, invoker, sender, holder.EventTarget);
                ProcessAction(row.Action3, random, row.Id, invoker, sender, holder.EventTarget);
            }
        }
        else
        {
            CreatureAiAction[] actions = [.. row.Actions.Where(a => !a.IsEmpty)];
            if (actions.Length > 0)
            {
                uint index = random % (uint)actions.Length;
                random = Context.NextRandom(); // randomize again so a random event with a random action is not always the same
                actionSuccess = ProcessAction(actions[index], random, row.Id, invoker, sender, holder.EventTarget);
            }
        }

        if ((flags & EventAiFlags.CombatAction) == 0 || actionSuccess)
        {
            ResetEvent(holder);
        }

        holder.InProgress = false;
    }

    private bool ProcessAction(CreatureAiAction action, uint random, uint eventId, Unit? invoker, Unit? sender, Unit? eventTarget)
    {
        if (action.IsEmpty || _registry.FindAction(action.Type) is not { } handler)
        {
            return false;
        }

        return handler.Execute(Context, action, new EventAiInvocation(random, eventId, invoker, sender, eventTarget));
    }
}
