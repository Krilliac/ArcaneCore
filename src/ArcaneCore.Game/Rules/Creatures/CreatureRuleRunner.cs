using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Rules.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

/// <summary>The game signal that makes the runner consider the rules of an event type (the hook of the creature AI that raises it).</summary>
public enum CreatureSignal : byte
{
    /// <summary>Not signal driven (periodic, or armed by the lifecycle only).</summary>
    None = 0,
    Respawn,
    ReachedHome,
    Evade,
    Death,
    Kill,
    SpellHit,
    SpellHitTarget,
    ReceiveEmote,
    OutOfCombatLineOfSight,
    Summoned,
    SummonedDied,
    SummonedDespawned,
    AiEvent,
}

/// <summary>The data of one rule firing that the primitives of the creature host share (a stack-like frame: nested firings save and restore it).</summary>
internal struct CreatureFireFrame
{
    public int RuleId;
    public uint Random;
    public bool RandomDrawn;
    public int Picked;
    public bool CombatAction;
    public int FirstSeq;
    public bool FirstFailed;
    public Unit? Invoker;
    public Unit? Sender;
    public Unit? EventTarget;
}

/// <summary>What the runner knows about one rule of a binding, derived once per binding: its event primitive and its row options.</summary>
internal readonly record struct CreatureRuleInfo(CompiledRule Rule, CreatureEvent? Event, CreatureRuleIds.RowFlags Flags, int ActionCount)
{
    public bool Repeatable => (Flags & CreatureRuleIds.RowFlags.Repeatable) != 0;

    public bool OneShot => (Flags & CreatureRuleIds.RowFlags.OneShot) != 0;

    public bool CombatAction => (Flags & CreatureRuleIds.RowFlags.CombatAction) != 0;
}

/// <summary>The per-binding table of <see cref="CreatureRuleInfo"/> in slot order (cached with the binding, shared by every creature that uses it).</summary>
internal sealed class CreatureRuleTable
{
    private static readonly ConditionalWeakTable<RuleBinding, CreatureRuleTable> Cache = new();

    private CreatureRuleTable(CreatureRuleInfo[] rules) => Rules = rules;

    public CreatureRuleInfo[] Rules { get; }

    public static CreatureRuleTable For(RuleBinding binding) => Cache.GetValue(binding, static b => Build(b));

    private static CreatureRuleTable Build(RuleBinding binding)
    {
        var rules = new CreatureRuleInfo[binding.SlotCount];
        foreach (short eventType in binding.EventTypes)
        {
            foreach (BoundRule bound in binding.ForEvent(eventType))
            {
                CompiledRule rule = bound.Rule;
                CreatureRuleIds.RowFlags flags = rule.Has(RuleFlags.Repeatable) ? CreatureRuleIds.RowFlags.Repeatable : CreatureRuleIds.RowFlags.None;
                int count = 0;
                if (rule.Actions.Length > 0 && rule.Actions[0].Action is CreatureRowAction)
                {
                    flags = (CreatureRuleIds.RowFlags)rule.Actions[0].Param(1);
                    count = rule.Actions[0].Param(2);
                }

                rules[bound.Slot] = new CreatureRuleInfo(rule, rule.Event as CreatureEvent, flags, count);
            }
        }

        return new CreatureRuleTable(rules);
    }
}

/// <summary>
/// The rule engine on one creature (the creature host's runtime): it fires the creature events of the creature's bound rules at the points
/// the EventAI engine fires them, with the same timers, phases, ready lists and combat-action contract, and runs each rule that passes
/// through the shared <see cref="RuleEvaluator"/>. It replaces <see cref="EventAiEngine"/> once the cutover lane has recorded the tier (b)
/// parity run; until then both exist and <see cref="CreatureRuleAI"/> is the only user.
/// <para>
/// Division of work. The runner owns what EventAI makes per-row state: the repeat and initial timers (frozen while the phase hides a rule),
/// whether a row is enabled, the two-step check-then-process of a batch, the combat-action retry and the EFLAG options of the row marker
/// (<see cref="CreatureRowAction"/>). The evaluator owns the phase mask, the chance roll and the action sequence. A rule is handed to the
/// evaluator alone: <see cref="CreatureEvent.Matches"/> accepts only the rule the runner is processing, and the rule's own cooldown
/// fields are the repeat range the runner draws from (the evaluator's cooldown is neutralised: its random source returns the minimum and
/// the runner clears the cooldown slot).
/// </para>
/// <para>
/// Ownership and threads: one runner belongs to one creature and is touched only on the creature's map thread. The ambient
/// <see cref="Current"/> runner is thread-static; the primitives of this namespace read it because the engine's primitive entry points
/// carry no creature. Per-signal work allocates nothing once the unit wrappers are cached; an invoker new to a creature allocates one
/// small wrapper.
/// </para>
/// </summary>
public sealed class CreatureRuleRunner : IRuleSubject, IRuleSpeaker, IRuleCaster
{
    [ThreadStatic]
    private static CreatureRuleRunner? t_current;

    private const int MaxCachedUnits = 24;

    /// <summary>The cast flags of the EventAI cast action (cmangos CastFlags): interrupt the previous cast, triggered, skip a target that has the aura.</summary>
    internal const int CastInterruptPrevious = 0x01;
    internal const int CastTriggered = 0x02;
    internal const int CastAuraNotPresent = 0x20;

    private readonly CreatureRuleAI _ai;
    private readonly RuleCatalog _catalog;
    private readonly List<List<int>> _ready = [];
    private readonly List<CreatureUnitSubject> _units = [];
    private readonly RuleEvaluator _evaluator;
    private RuleSubjectState _state;
    private CreatureRuleInfo[] _rules = [];
    private long[] _timerUntil = [];
    private bool[] _enabled = [];
    private bool[] _inProgress = [];
    private int[] _readyPhase = [];
    private Unit?[] _eventTargets = [];
    private int _depth;
    private uint _updateTimeMs;
    private uint _diffMs;
    private long _clock;
    private bool _inBatch;
    private CreatureFireFrame _frame;

    internal CreatureRuleRunner(CreatureRuleAI ai, RuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(catalog);
        _ai = ai;
        _catalog = catalog;
        Context = ai.Engine.Context;
        _evaluator = new RuleEvaluator(new HostRandom(this)) { Conditions = new Gate(this) };
        _state = null!;
        Rebuild();
    }

    /// <summary>The runner whose rule is being processed on this thread, or null outside a firing.</summary>
    internal static CreatureRuleRunner? Current => t_current;

    public Creature Me => _ai.Me;

    internal EventAiContext Context { get; }

    internal CreatureRuleAI Ai => _ai;

    internal CreatureMapSystem? System => Context.System;

    internal RuleCatalog Catalog => _catalog;

    /// <summary>The evaluator this creature's rules run through (its counters tell what ran).</summary>
    public RuleEvaluator Evaluator => _evaluator;

    /// <summary>The current phase (0..31).</summary>
    public int Phase
    {
        get => _state.Phase;
        set => _state.Phase = value;
    }

    /// <summary>How many rules are bound to this creature.</summary>
    public int RuleCount => _rules.Length;

    internal ref CreatureFireFrame Frame => ref _frame;

    /// <summary>The runner's own clock in milliseconds: the sum of the update steps it saw (the unit of every timer).</summary>
    internal long Clock => _clock;

    /// <summary>The time the timers were last settled at: the clock as of the last batch (cmangos decrements timers only at a batch).</summary>
    private long FrozenNow => _inBatch ? _clock : _clock - _diffMs;

    // --- IRuleSubject -------------------------------------------------------------------------------

    ulong IRuleSubject.Guid => Me.Guid.Value;

    bool IRuleSubject.IsInCombat => Me.Combat.IsInCombat;

    RuleSubjectFacts IRuleSubject.Facts => new(0, 0, Me.Level, 0, Me.MapId, 0, 0, RuleSubjectFlags.Creature);

    void IRuleSpeaker.Say(RuleText text, IRuleSubject? target) => System?.Say(Me, CreatureTexts.ToAiText(text), UnitOf(target));

    void IRuleCaster.Cast(int spellId, int flags, IRuleSubject? target) => CastSpell(spellId, flags, UnitOf(target));

    // --- construction and lifecycle signals --------------------------------------------------------------

    /// <summary>cmangos InitAI: the rules of the creature's entry and its spawn guid, plus the global creature rules, with fresh state.</summary>
    private void Rebuild()
    {
        Creature me = Me;
        RuleBinding binding = _catalog.Bind(RuleHostKind.Creature,
            new RuleOwner(RuleOwnerKind.Entry, (int)me.Entry),
            me.Spawn is { } spawn ? new RuleOwner(RuleOwnerKind.Guid, (int)spawn.Guid) : default);
        _state = binding.CreateState();
        _rules = CreatureRuleTable.For(binding).Rules;
        int count = _rules.Length;
        _timerUntil = new long[count];
        _enabled = new bool[count];
        _inProgress = new bool[count];
        _readyPhase = new int[count];
        _eventTargets = new Unit?[count];
        Array.Fill(_enabled, true);
        _units.Clear();
    }

    /// <summary>cmangos JustRespawned: fresh state, the update timer reset, each type's respawn branch, then the spawned events.</summary>
    public void Respawn()
    {
        Rebuild();
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is not { } handler)
            {
                continue;
            }

            handler.OnRespawn(this, slot, _rules[slot].Rule);
            if (handler.Signal == CreatureSignal.Respawn && handler.AllowTrigger(this, _rules[slot].Rule))
            {
                CheckAndReady(slot, null);
            }
        }

        ProcessEvents(null, null);
    }

    /// <summary>cmangos Reset: the update timer, then every type's reset branch.</summary>
    public void Reset()
    {
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            _rules[slot].Event?.OnReset(this, slot, _rules[slot].Rule);
        }

        Array.Clear(_state.Fired);
    }

    /// <summary>cmangos JustReachedHome: the reached-home events, then <see cref="Reset"/>.</summary>
    public void ReachedHome()
    {
        Dispatch(CreatureSignal.ReachedHome, null, null, checkAllow: true);
        Reset();
    }

    /// <summary>cmangos EnterEvadeMode: the evade events.</summary>
    public void Evade() => Dispatch(CreatureSignal.Evade, null, null, checkAllow: true);

    /// <summary>cmangos JustDied: <see cref="Reset"/>, the death events, then the phase returns to 0.</summary>
    public void Death(Unit? killer)
    {
        Reset();
        Dispatch(CreatureSignal.Death, killer, null, checkAllow: true);
        _state.Phase = 0;
    }

    /// <summary>cmangos KilledUnit.</summary>
    public void Kill(Unit victim) => Dispatch(CreatureSignal.Kill, victim, null, checkAllow: true);

    /// <summary>cmangos EnterCombat: the combat branches (timers re-armed, aggro rules armed and considered), then the batch.</summary>
    public void EnterCombat(Unit enemy)
    {
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is not { } handler)
            {
                continue;
            }

            handler.OnEnterCombat(this, slot, _rules[slot].Rule);
            if (handler.ArmsOnEnterCombat)
            {
                _enabled[slot] = true;
                CheckAndReady(slot, enemy);
            }
        }

        ProcessEvents(enemy, null);
        _updateTimeMs = Context.Options.UpdateIntervalMs;
        _diffMs = 0;
    }

    /// <summary>cmangos SpellHit: the rules whose spell id and school mask match the hit.</summary>
    public void SpellHit(Unit caster, SpellInfo spell)
    {
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { Signal: CreatureSignal.SpellHit } handler && handler.MatchesSpell(_rules[slot].Rule, spell))
            {
                CheckAndReady(slot, caster);
            }
        }

        ProcessEvents(caster, null);
    }

    /// <summary>cmangos SpellHitTarget: the rules whose spell id and school mask match a spell the creature cast.</summary>
    public void SpellHitTarget(Unit target, SpellInfo spell)
    {
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { Signal: CreatureSignal.SpellHitTarget } handler && handler.MatchesSpell(_rules[slot].Rule, spell))
            {
                CheckAndReady(slot, target);
            }
        }

        ProcessEvents(target, null);
    }

    /// <summary>cmangos ReceiveEmote: every rule whose emote is <paramref name="textEmote"/> is readied with the player as invoker.</summary>
    public void ReceiveEmote(Player player, uint textEmote)
    {
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { Signal: CreatureSignal.ReceiveEmote } handler && handler.MatchesEmote(_rules[slot].Rule, textEmote))
            {
                CheckAndReady(slot, player);
            }
        }

        ProcessEvents(player, null);
    }

    /// <summary>cmangos MoveInLineOfSight: while the creature has no victim, the out-of-combat sight rules <paramref name="who"/> satisfies.</summary>
    public void MoveInLineOfSight(Unit who)
    {
        if (Context.Victim is not null || _rules.Length == 0)
        {
            return;
        }

        EnsureDepth();
        bool anyReady = false;
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { Signal: CreatureSignal.OutOfCombatLineOfSight } handler && handler.MatchesUnit(this, _rules[slot].Rule, who))
            {
                CheckAndReady(slot, who);
                anyReady = true;
            }
        }

        if (anyReady)
        {
            ProcessEvents(who, null);
        }
    }

    public void JustSummoned(Creature summoned) => Dispatch(CreatureSignal.Summoned, summoned, null, checkAllow: false);

    public void SummonedJustDied(Creature summoned) => Dispatch(CreatureSignal.SummonedDied, summoned, null, checkAllow: false);

    public void SummonedDespawned(Creature summoned) => Dispatch(CreatureSignal.SummonedDespawned, summoned, null, checkAllow: false);

    /// <summary>cmangos ReceiveAIEvent: the rules of <paramref name="eventType"/> whose sender entry is 0 or the sender's, with the invoker and the sender.</summary>
    public void ReceiveAiEvent(uint eventType, Unit sender, Unit? invoker)
    {
        ArgumentNullException.ThrowIfNull(sender);
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { Signal: CreatureSignal.AiEvent } handler && handler.MatchesAiEvent(_rules[slot].Rule, eventType, sender))
            {
                CheckAndReady(slot, invoker);
            }
        }

        ProcessEvents(invoker, sender);
    }

    private void Dispatch(CreatureSignal signal, Unit? invoker, Unit? sender, bool checkAllow)
    {
        EnsureDepth();
        for (int slot = 0; slot < _rules.Length; slot++)
        {
            if (_rules[slot].Event is { } handler && handler.Signal == signal && (!checkAllow || handler.AllowTrigger(this, _rules[slot].Rule)))
            {
                CheckAndReady(slot, invoker);
            }
        }

        ProcessEvents(invoker, sender);
    }

    // --- periodic update ------------------------------------------------------------------------------

    /// <summary>
    /// One update step of <paramref name="diffMs"/> ms (the creature is alive): delayed actions run, then cmangos UpdateEventTimers: events
    /// are evaluated once per <see cref="EventAiOptions.UpdateIntervalMs"/>; between batches the elapsed time accumulates; at a batch every
    /// timer drops by the accumulated time unless the current phase hides the rule, and armed rules at timer 0 that are timer-executed are considered.
    /// </summary>
    public void Update(uint diffMs)
    {
        _clock += diffMs;
        if (_state.PendingCount > 0)
        {
            CreatureRuleRunner? previous = t_current;
            t_current = this;
            try
            {
                _evaluator.Update(_state, this, _clock);
            }
            finally
            {
                t_current = previous;
            }
        }

        if (_updateTimeMs < diffMs)
        {
            _diffMs += diffMs;
            EnsureDepth();
            _inBatch = true;
            try
            {
                for (int slot = 0; slot < _rules.Length; slot++)
                {
                    if (_rules[slot].Event is not { } handler)
                    {
                        continue;
                    }

                    if (handler.CheckedEveryBatch)
                    {
                        CheckAndReady(slot, null);
                        continue;
                    }

                    // A pending timer drops by the accumulated time (its due time stays); one hidden by the phase does not run.
                    if (_timerUntil[slot] > _clock - _diffMs && HiddenByPhase(_rules[slot].Rule))
                    {
                        _timerUntil[slot] += _diffMs;
                    }

                    if (!_enabled[slot] || _timerUntil[slot] > _clock)
                    {
                        continue;
                    }

                    if (handler.TimerExecuted)
                    {
                        CheckAndReady(slot, null);
                    }
                }

                ProcessEvents(null, null);
                _diffMs = 0;
            }
            finally
            {
                _inBatch = false;
            }

            _updateTimeMs = Context.Options.UpdateIntervalMs;
        }
        else
        {
            _diffMs += diffMs;
            _updateTimeMs -= diffMs;
        }
    }

    // --- checking and processing ---------------------------------------------------------------------------

    private void EnsureDepth()
    {
        while (_ready.Count <= _depth)
        {
            _ready.Add([]);
        }
    }

    private bool HiddenByPhase(CompiledRule rule)
        => rule.PhaseMask != 0 && ((uint)_state.Phase > 31 || (rule.PhaseMask & (1u << _state.Phase)) == 0);

    private void ProcessEvents(Unit? invoker, Unit? sender)
    {
        int current = _depth;
        if (current >= _ready.Count)
        {
            return;
        }

        List<int> ready = _ready[current];
        if (ready.Count == 0)
        {
            return;
        }

        // An action that throws must not leave the depth raised or the queued rules in progress (as in the EventAI engine).
        _depth++;
        try
        {
            for (int i = 0; i < ready.Count; i++)
            {
                ProcessEvent(ready[i], invoker, sender);
            }
        }
        finally
        {
            foreach (int slot in ready)
            {
                _inProgress[slot] = false;
            }

            ready.Clear();
            _depth--;
        }
    }

    private void CheckAndReady(int slot, Unit? invoker)
    {
        if (CheckEvent(slot, invoker))
        {
            _inProgress[slot] = true;
            _readyPhase[slot] = _state.Phase;
            EnsureDepth();
            _ready[_depth].Add(slot);
        }
    }

    /// <summary>cmangos CheckEvent: the generic gates, then the event type's own condition.</summary>
    private bool CheckEvent(int slot, Unit? invoker)
    {
        CreatureRuleInfo info = _rules[slot];
        if (!_enabled[slot] || _timerUntil[slot] > FrozenNow || _inProgress[slot] || info.Event is not { } handler)
        {
            return false;
        }

        if (info.CombatAction && !Context.CanExecuteCombatAction)
        {
            return false;
        }

        if (HiddenByPhase(info.Rule))
        {
            return false;
        }

        CreatureRuleIds.RowFlags flags = info.Flags;
        if (Context.RangedMode ? (flags & CreatureRuleIds.RowFlags.MeleeModeOnly) != 0 : (flags & CreatureRuleIds.RowFlags.RangedModeOnly) != 0)
        {
            return false;
        }

        return handler.Check(this, slot, info.Rule, invoker);
    }

    /// <summary>cmangos ResetEvent: re-arm the repeat timer of timer-based events; disable non-repeating ones.</summary>
    private void ResetEvent(int slot)
    {
        CreatureRuleInfo info = _rules[slot];
        if (info.Event is not { } handler)
        {
            return;
        }

        if (handler.TimerBased)
        {
            if (info.OneShot)
            {
                _enabled[slot] = false;
            }
            else
            {
                UpdateRepeatTimer(slot, (uint)info.Rule.CooldownMinMs, (uint)info.Rule.CooldownMaxMs);
            }
        }

        if (handler.NaturallyRepeatable && !info.Repeatable)
        {
            _enabled[slot] = false;
        }
    }

    /// <summary>cmangos ProcessEvent: the rule goes to the evaluator (phase of the check, chance, actions); the outcome decides the timer reset.</summary>
    private void ProcessEvent(int slot, Unit? invoker, Unit? sender)
    {
        CreatureRuleInfo info = _rules[slot];
        CompiledRule rule = info.Rule;
        if (info.CombatAction && !Context.CanExecuteCombatAction)
        {
            _inProgress[slot] = false;
            return;
        }

        CreatureFireFrame saved = _frame;
        CreatureRuleRunner? previous = t_current;
        t_current = this;
        // The evaluator's phase gate sees the phase the rule was readied in (cmangos checks it when it readies a row and never again); the live
        // phase is left alone, so every action reads and writes it, also when this firing nests inside another one.
        int outerGate = _state.GatePhase;
        int readyPhase = _readyPhase[slot];
        try
        {
            _frame = new CreatureFireFrame
            {
                RuleId = rule.Id,
                Picked = -1,
                FirstSeq = -1,
                CombatAction = info.CombatAction,
                Invoker = invoker,
                Sender = sender,
                EventTarget = _eventTargets[slot],
            };

            // cmangos rolls the chance with one random draw whatever the chance is; the evaluator draws only below 100.
            if (rule.ChancePct >= 100)
            {
                Context.NextRandom();
            }

            _state.GatePhase = readyPhase;
            _state.CooldownUntil[slot] = 0;
            int ran = _evaluator.Fire(_state, this, rule.EventType, default, _clock, Wrap(invoker));
            _state.CooldownUntil[slot] = 0;

            // No run means the chance roll failed (the runner checked everything else), which resets the event like a run does.
            bool actionSuccess = ran > 0 && !_frame.FirstFailed;
            if (ran == 0 || !info.CombatAction || actionSuccess)
            {
                ResetEvent(slot);
            }
        }
        finally
        {
            _state.GatePhase = outerGate;
            _frame = saved;
            t_current = previous;
            _inProgress[slot] = false;
        }
    }

    // --- timers ------------------------------------------------------------------------------------------

    /// <summary>
    /// cmangos CreatureEventAIHolder::UpdateRepeatTimer: equal bounds set the timer to them, a larger maximum draws uniformly, a smaller
    /// maximum disables the event.
    /// </summary>
    internal bool UpdateRepeatTimer(int slot, uint min, uint max)
    {
        if (min == max)
        {
            _timerUntil[slot] = FrozenNow + min;
        }
        else if (max > min)
        {
            _timerUntil[slot] = FrozenNow + Context.Random((int)Math.Min(min, int.MaxValue), (int)Math.Min(max, int.MaxValue));
        }
        else
        {
            _enabled[slot] = false;
            return false;
        }

        return true;
    }

    /// <summary>Arm the rule with the range (initial timers, or the repeat range): enabled when a timer was set.</summary>
    internal void Arm(int slot, uint min, uint max)
    {
        if (UpdateRepeatTimer(slot, min, max))
        {
            _enabled[slot] = true;
        }
    }

    /// <summary>The default respawn/reset branch: armed, timer 0.</summary>
    internal void ArmNow(int slot)
    {
        _enabled[slot] = true;
        _timerUntil[slot] = 0;
    }

    internal void Enable(int slot) => _enabled[slot] = true;

    internal void SetEventTarget(int slot, Unit? target) => _eventTargets[slot] = target;

    // --- unit wrappers and shared helpers ---------------------------------------------------------------------

    /// <summary>The subject the engine passes around for <paramref name="unit"/> (the runner itself for the creature).</summary>
    internal IRuleSubject? Wrap(Unit? unit)
    {
        if (unit is null)
        {
            return null;
        }

        if (ReferenceEquals(unit, Me))
        {
            return this;
        }

        foreach (CreatureUnitSubject cached in _units)
        {
            if (ReferenceEquals(cached.Unit, unit))
            {
                return cached;
            }
        }

        var wrapper = new CreatureUnitSubject(unit);
        if (_units.Count >= MaxCachedUnits)
        {
            _units.RemoveAt(0);
        }

        _units.Add(wrapper);
        return wrapper;
    }

    /// <summary>The unit a resolved subject stands for (the creature for the runner, null for no unit).</summary>
    internal Unit? UnitOf(IRuleSubject? subject) => subject switch
    {
        CreatureRuleRunner => Me,
        CreatureUnitSubject unit => unit.Unit,
        _ => null,
    };

    /// <summary>
    /// cmangos GetTargetByType for the action at <paramref name="seq"/>: null when the action sits out this firing or the target did not
    /// resolve (a failure of the action); the "no unit" subject when the target is the default one, or unresolved and <paramref name="optional"/>.
    /// </summary>
    internal IRuleSubject? ResolveTarget(int seq, int selectorType, bool optional)
    {
        if (!AllowsAction(seq))
        {
            return null;
        }

        Unit? unit = Context.SelectTarget(selectorType, Invocation(), out bool error);
        if (error)
        {
            if (optional)
            {
                return CreatureUnitSubject.NoUnit;
            }

            NoteFailure(seq);
            return null;
        }

        return unit is null ? CreatureUnitSubject.NoUnit : Wrap(unit);
    }

    /// <summary>cmangos TEXT audience (:700-718): the invoker when it is a player, else the victim when there is no invoker.</summary>
    internal IRuleSubject? ResolveTextAudience(int seq)
    {
        if (!AllowsAction(seq))
        {
            return null;
        }

        Unit? audience = _frame.Invoker is { } invoker ? (invoker is Player ? invoker : null) : Context.Victim;
        return audience is null ? CreatureUnitSubject.NoUnit : Wrap(audience);
    }

    /// <summary>The random value the action sequence of this firing shares (cmangos <c>rnd</c>), drawn on first use when the row has no marker.</summary>
    internal uint ActionRandom()
    {
        if (!_frame.RandomDrawn)
        {
            _frame.Random = Context.NextRandom();
            _frame.RandomDrawn = true;
        }

        return _frame.Random;
    }

    internal EventAiInvocation Invocation() => new(_frame.Random, (uint)_frame.RuleId, _frame.Invoker, _frame.Sender, _frame.EventTarget);

    internal void NoteFailure(int seq)
    {
        if (seq == _frame.FirstSeq)
        {
            _frame.FirstFailed = true;
        }
    }

    /// <summary>Whether the action at <paramref name="seq"/> takes part in this firing (random-action pick, combat-action abort); the first that does is "the first action".</summary>
    internal bool AllowsAction(int seq)
    {
        if (seq == 0)
        {
            return true;
        }

        if (_frame.Picked >= 0 && seq - 1 != _frame.Picked)
        {
            return false;
        }

        if (_frame.CombatAction && _frame.FirstSeq >= 0 && _frame.FirstFailed && seq != _frame.FirstSeq)
        {
            return false;
        }

        if (_frame.FirstSeq < 0)
        {
            _frame.FirstSeq = seq;
        }

        return true;
    }

    /// <summary>
    /// A creature cast with the EventAI cast flags (cmangos UnitAI::DoCastSpellIfCan): the aura-not-present flag skips a target that has the
    /// aura, a creature that is casting only casts again when the spell is triggered or interrupts the previous one. True when the cast was accepted.
    /// </summary>
    internal bool CastSpell(int spellId, int flags, Unit? target)
    {
        if (System is not { } system)
        {
            return false;
        }

        if ((flags & CastAuraNotPresent) != 0 && system.HasAura(target ?? Me, (uint)spellId))
        {
            return false;
        }

        if (Context.IsCasting && (flags & (CastTriggered | CastInterruptPrevious)) == 0)
        {
            return false;
        }

        if ((flags & CastInterruptPrevious) != 0)
        {
            system.InterruptCast(Me);
        }

        return system.CastSpell(Me, (uint)spellId, target, (flags & CastTriggered) != 0) == CreatureCastResult.Ok;
    }

    /// <summary>The evaluator's random source: draws come from the creature's map random stream, as cmangos' do; the cooldown draw is neutralised.</summary>
    private sealed class HostRandom(CreatureRuleRunner runner) : Random
    {
        /// <summary>The chance roll: one draw of the map stream modulo the bound (cmangos <c>urand() % 100</c>).</summary>
        public override int Next(int maxValue) => (int)(runner.Context.NextRandom() % (uint)maxValue);

        /// <summary>The evaluator's cooldown draw. The runner draws the repeat range itself (after the actions, as cmangos does) and clears the slot.</summary>
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    /// <summary>The evaluator's condition gate: a <c>conditions</c> row, checked for the player controlling the invoker (as the EventAI conditions are).</summary>
    private sealed class Gate(CreatureRuleRunner runner) : IRuleConditionGate
    {
        public bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker)
            => runner.System?.AiServices.Conditions is null ? null : EventAiSearch.ConditionHolds(runner.Context, conditionId, runner.UnitOf(invoker));
    }
}

/// <summary>A unit (player, creature, pet) as the rule engine sees a target or an invoker. <see cref="Unit"/> is null for the "no unit" target.</summary>
internal sealed class CreatureUnitSubject(Unit? unit) : IRuleSubject
{
    /// <summary>The default-target sentinel: the action runs with no unit target (the spell picks its own).</summary>
    public static CreatureUnitSubject NoUnit { get; } = new(null);

    public Unit? Unit { get; } = unit;

    public ulong Guid => Unit?.Guid.Value ?? 0;

    public bool IsInCombat => Unit?.Combat.IsInCombat ?? false;

    public RuleSubjectFacts Facts => default;
}

/// <summary>Conversions between rule texts and the creature text type the map system speaks.</summary>
internal static class CreatureTexts
{
    private static readonly ConditionalWeakTable<RuleText, CreatureAiText> Cache = new();

    public static CreatureAiText ToAiText(RuleText text) => Cache.GetValue(text, static t => new CreatureAiText(t.Id, t.Content, t.Type, t.Language, (uint)t.Emote)
    {
        Sound = (uint)t.Sound,
        BroadcastTextId = (uint)t.BroadcastTextId,
    });
}
