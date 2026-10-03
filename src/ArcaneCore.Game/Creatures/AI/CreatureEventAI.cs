using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>cmangos-classic EventAI event types ArcaneCore runs (doc/EventAI.txt numbering).</summary>
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
}

/// <summary>cmangos-classic EventAI action types ArcaneCore runs.</summary>
public enum EventAiActionType : byte
{
    None = 0,
    Text = 1,
    Cast = 11,
    Summon = 12,
    AutoAttack = 20,
    CombatMovement = 21,
    SetPhase = 22,
    IncrementPhase = 23,
    Evade = 24,
    FleeForAssist = 25,
    Die = 37,
    CallForHelp = 39,
}

/// <summary>cmangos EventAI target types.</summary>
public enum EventAiTarget
{
    Self = 0,
    Victim = 1,
    SecondOnThreat = 2,
    LastOnThreat = 3,
    RandomOnThreat = 4,
    RandomNotTop = 5,
    Invoker = 6,
}

/// <summary>
/// The data-driven creature script (cmangos-classic CreatureEventAI, re-implemented from its
/// documented table semantics; no code copied). Rows come from <c>creature_ai_scripts</c> for
/// the creature's entry; texts from <c>creature_ai_texts</c>. Supported events: in-combat and
/// out-of-combat timers, health percent, aggro, kill, death, evade, spell hit, spawned and
/// reached home. Supported actions: text (say/yell/emote/whisper), cast (interrupt-previous,
/// triggered and aura-not-present flags), summon, auto attack, combat movement, set/increment
/// phase, evade, flee for assist, die and call for help. Rows using anything else are reported
/// once per creature entry and their unsupported actions are skipped (docs/areas/creature-ai.md).
/// <para>
/// Semantics: an event runs only when bit <c>phase</c> of its inverse phase mask is clear; the
/// chance is rolled each time it triggers; without the repeatable flag (0x01) it fires once until
/// the next reset (spawn, evade end); the random-action flag (0x20) runs one non-empty action
/// instead of all three. Timers are in milliseconds, drawn uniformly from [min, max]. Reset
/// (respawn, reaching home) returns to phase 0 and re-arms every event.
/// </para>
/// </summary>
public sealed class CreatureEventAI : AggressorAI
{
    public const byte FlagRepeatable = 0x01;
    public const byte FlagRandomAction = 0x20;

    public const int CastInterruptPrevious = 0x01;
    public const int CastTriggered = 0x02;
    public const int CastAuraNotPresent = 0x20;

    /// <summary>cmangos MAX_PHASE: phases 0..31.</summary>
    public const int MaxPhase = 32;

    private readonly List<Holder> _holders;
    private readonly CreatureAiContent _content;

    public CreatureEventAI(Creature creature, CreatureAiContent content)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _holders = [.. content.GetEvents(creature.Entry).Select(e => new Holder(e))];
        Unsupported = [.. _holders.SelectMany(h => Describe(h.Event)).Distinct()];
        Reset();
    }

    /// <summary>The current phase (0..31).</summary>
    public int Phase { get; private set; }

    /// <summary>Unsupported event/action types found in this creature's rows (reported by the map system).</summary>
    public IReadOnlyList<string> Unsupported { get; }

    public int EventCount => _holders.Count;

    /// <summary>Back to phase 0, every event armed, combat movement and melee on (cmangos CreatureEventAI::Reset).</summary>
    public void Reset()
    {
        Phase = 0;
        CombatMovement = true;
        MeleeEnabled = true;
        foreach (Holder holder in _holders)
        {
            holder.Enabled = true;
            holder.ReadyAtMs = 0;
            holder.TimerMs = holder.Event.EventType == (byte)EventAiEventType.TimerOutOfCombat
                ? Roll(holder.Event.Param1, holder.Event.Param2)
                : 0;
        }
    }

    public override void OnRespawn()
    {
        Reset();
        Fire(EventAiEventType.Spawned, invoker: null);
    }

    public override void OnAggro(Unit target)
    {
        foreach (Holder holder in _holders)
        {
            switch ((EventAiEventType)holder.Event.EventType)
            {
                case EventAiEventType.TimerInCombat:
                    holder.TimerMs = Roll(holder.Event.Param1, holder.Event.Param2);
                    break;
                case EventAiEventType.HealthPercent:
                    holder.TimerMs = 0;
                    break;
            }
        }

        Fire(EventAiEventType.Aggro, target);
    }

    public override void OnDeath(Unit? killer) => Fire(EventAiEventType.Death, killer);

    public override void OnKilledUnit(Unit victim) => FireTimed(EventAiEventType.Kill, victim, static _ => true);

    public override void OnEvade() => Fire(EventAiEventType.Evade, invoker: null);

    public override void OnReachedHome()
    {
        Reset();
        Fire(EventAiEventType.ReachedHome, invoker: null);
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
        => FireTimed(EventAiEventType.SpellHit, caster, e =>
            e.Param1 != 0 ? (uint)e.Param1 == spell.Id : (e.Param2 & (1 << (int)spell.School)) != 0);

    public override void OnUpdate(uint diffMs)
    {
        bool inCombat = Me.Combat.IsInCombat && Victim is not null;
        if (Me.Combat.IsInCombat)
        {
            inCombat = UpdateVictim();
        }

        if (!Me.IsAlive || Me.IsInEvadeMode)
        {
            return;
        }

        int diff = (int)Math.Min(diffMs, int.MaxValue);
        foreach (Holder holder in _holders.ToArray())
        {
            if (!holder.Enabled)
            {
                continue;
            }

            CreatureAiEvent e = holder.Event;
            switch ((EventAiEventType)e.EventType)
            {
                case EventAiEventType.TimerInCombat when inCombat:
                case EventAiEventType.TimerOutOfCombat when !Me.Combat.IsInCombat:
                    holder.TimerMs -= diff;
                    if (holder.TimerMs <= 0)
                    {
                        holder.TimerMs = Roll(e.Param3, e.Param4);
                        Process(holder, Victim);
                    }

                    break;

                case EventAiEventType.HealthPercent when inCombat:
                    holder.TimerMs = Math.Max(0, holder.TimerMs - diff);
                    if (holder.TimerMs > 0 || Me.MaxHealth == 0)
                    {
                        break;
                    }

                    uint percent = (uint)((ulong)Me.Health * 100 / Me.MaxHealth);
                    if (percent <= (uint)Math.Max(0, e.Param1) && percent >= (uint)Math.Max(0, e.Param2))
                    {
                        holder.TimerMs = Roll(e.Param3, e.Param4);
                        Process(holder, Victim);
                    }

                    break;
            }
        }
    }

    // --- processing -------------------------------------------------------------------------

    private void Fire(EventAiEventType type, Unit? invoker)
    {
        foreach (Holder holder in _holders.ToArray())
        {
            if (holder.Enabled && holder.Event.EventType == (byte)type)
            {
                Process(holder, invoker);
            }
        }
    }

    /// <summary>Kill and spell-hit events repeat after a cooldown drawn from their last two parameters.</summary>
    private void FireTimed(EventAiEventType type, Unit? invoker, Func<CreatureAiEvent, bool> matches)
    {
        long now = System?.ClockMs ?? 0;
        foreach (Holder holder in _holders.ToArray())
        {
            if (holder.Enabled && holder.Event.EventType == (byte)type && now >= holder.ReadyAtMs && matches(holder.Event))
            {
                holder.ReadyAtMs = now + Roll(holder.Event.Param3, holder.Event.Param4);
                Process(holder, invoker);
            }
        }
    }

    private void Process(Holder holder, Unit? invoker)
    {
        CreatureAiEvent e = holder.Event;
        if ((e.InversePhaseMask & (1u << Phase)) != 0)
        {
            return;
        }

        if ((e.Flags & FlagRepeatable) == 0)
        {
            holder.Enabled = false;
        }

        if (e.Chance < 100 && (System?.RandomInt(0, 99) ?? 0) >= e.Chance)
        {
            return;
        }

        if ((e.Flags & FlagRandomAction) != 0)
        {
            CreatureAiAction[] candidates = [.. e.Actions.Where(a => !a.IsEmpty)];
            if (candidates.Length > 0)
            {
                DoAction(candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0], invoker);
            }

            return;
        }

        foreach (CreatureAiAction action in e.Actions)
        {
            if (!action.IsEmpty)
            {
                DoAction(action, invoker);
            }
        }
    }

    private void DoAction(CreatureAiAction action, Unit? invoker)
    {
        CreatureMapSystem? system = System;
        if (system is null)
        {
            return;
        }

        switch ((EventAiActionType)action.Type)
        {
            case EventAiActionType.Text:
            {
                int[] ids = [.. new[] { action.Param1, action.Param2, action.Param3 }.Where(id => id != 0)];
                if (ids.Length > 0 && _content.FindText(ids[system.RandomInt(0, ids.Length - 1)]) is { } text)
                {
                    system.Say(Me, text, invoker ?? Victim);
                }

                break;
            }

            case EventAiActionType.Cast:
            {
                Unit? target = ResolveTarget((EventAiTarget)action.Param2, invoker);
                if (target is null && (EventAiTarget)action.Param2 != EventAiTarget.Self)
                {
                    break;
                }

                int flags = action.Param3;
                if ((flags & CastAuraNotPresent) != 0 && system.HasAura(target ?? Me, (uint)action.Param1))
                {
                    break;
                }

                if ((flags & CastInterruptPrevious) != 0)
                {
                    system.InterruptCast(Me);
                }

                DoCast(target, (uint)action.Param1, (flags & CastTriggered) != 0);
                break;
            }

            case EventAiActionType.Summon:
            {
                Unit? target = ResolveTarget((EventAiTarget)action.Param2, invoker);
                system.Summon(Me, (uint)action.Param1, target, (uint)Math.Max(0, action.Param3));
                break;
            }

            case EventAiActionType.AutoAttack:
                MeleeEnabled = action.Param1 != 0;
                if (Victim is { } current)
                {
                    system.SetMelee(Me, current, MeleeEnabled);
                }

                break;

            case EventAiActionType.CombatMovement:
                CombatMovement = action.Param1 != 0;
                system.ApplyCombatMovement(Me);
                break;

            case EventAiActionType.SetPhase:
                Phase = Math.Clamp(action.Param1, 0, MaxPhase - 1);
                break;

            case EventAiActionType.IncrementPhase:
                Phase = Math.Clamp(Phase + action.Param1, 0, MaxPhase - 1);
                break;

            case EventAiActionType.Evade:
                EnterEvadeMode();
                break;

            case EventAiActionType.FleeForAssist:
                system.FleeForAssistance(Me);
                break;

            case EventAiActionType.Die:
                system.KillCreature(Me);
                break;

            case EventAiActionType.CallForHelp:
                DoCallForHelp(action.Param1);
                break;
        }
    }

    private Unit? ResolveTarget(EventAiTarget target, Unit? invoker)
    {
        IReadOnlyList<Combat.ThreatEntry> threat = Me.Combat.HasThreatList ? Me.Combat.Threat.Entries : [];
        switch (target)
        {
            case EventAiTarget.Self:
                return Me;
            case EventAiTarget.Victim:
                return Victim;
            case EventAiTarget.SecondOnThreat:
                return threat.Count > 1 ? threat[1].Target : null;
            case EventAiTarget.LastOnThreat:
                return threat.Count > 0 ? threat[^1].Target : null;
            case EventAiTarget.RandomOnThreat:
                return threat.Count > 0 ? threat[System?.RandomInt(0, threat.Count - 1) ?? 0].Target : null;
            case EventAiTarget.RandomNotTop:
                return threat.Count > 1 ? threat[System?.RandomInt(1, threat.Count - 1) ?? 1].Target : null;
            case EventAiTarget.Invoker:
                return invoker;
            default:
                return null;
        }
    }

    private int Roll(int min, int max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        min = Math.Max(0, min);
        max = Math.Max(0, max);
        return System is { } system ? system.RandomInt(min, max) : min;
    }

    private static IEnumerable<string> Describe(CreatureAiEvent e)
    {
        if (!Enum.IsDefined((EventAiEventType)e.EventType))
        {
            yield return $"event type {e.EventType} (row {e.Id})";
        }

        foreach (CreatureAiAction action in e.Actions)
        {
            if (!action.IsEmpty && !Enum.IsDefined((EventAiActionType)action.Type))
            {
                yield return $"action type {action.Type} (row {e.Id})";
            }
        }
    }

    private sealed class Holder(CreatureAiEvent e)
    {
        public CreatureAiEvent Event { get; } = e;

        public bool Enabled { get; set; } = true;

        public int TimerMs { get; set; }

        public long ReadyAtMs { get; set; }
    }
}
