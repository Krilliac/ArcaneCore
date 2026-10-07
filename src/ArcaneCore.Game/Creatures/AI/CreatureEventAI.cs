using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The data-driven creature script (cmangos-classic CreatureEventAI, re-implemented from the cloned sources; no code
/// copied). A thin <see cref="CreatureAI"/> that forwards the AI hooks to an <see cref="EventAiEngine"/>, which runs
/// the <c>creature_ai_scripts</c> rows of the creature's entry and spawn through the event and action handlers
/// discovered by <see cref="EventAiRegistry"/>. The events and actions with a handler are listed in
/// docs/areas/creature-ai.md; rows using anything else are reported once per creature entry and their unsupported
/// parts are skipped.
/// <para>
/// Semantics follow cmangos: timer-driven events are evaluated in batches every 500 ms (configurable,
/// <c>Creatures:EventAi:UpdateIntervalMs</c>); an event runs only when bit <c>phase</c> of its inverse phase mask is
/// clear; the chance is rolled each time it triggers and a failed roll re-arms its timer; without the repeatable flag
/// (0x01) it is disabled after it triggers until the next reset; the random-action flag (0x20) runs one non-empty
/// action instead of all three; the combat-action flag (0x400) keeps the timer when the first action fails so the event
/// retries. The phase returns to 0 when the creature dies and is otherwise kept (cmangos JustDied, Reset).
/// </para>
/// </summary>
public sealed class CreatureEventAI : AggressorAI
{
    public const byte FlagRepeatable = (byte)EventAiFlags.Repeatable;
    public const byte FlagRandomAction = (byte)EventAiFlags.RandomAction;

    public const int CastInterruptPrevious = CastAction.InterruptPrevious;
    public const int CastTriggered = CastAction.Triggered;
    public const int CastAuraNotPresent = CastAction.AuraNotPresent;

    /// <summary>cmangos MAX_PHASE: phases 0..31.</summary>
    public const int MaxPhase = 32;

    private readonly EventAiEngine _engine;
    private bool _rangedMode;
    private bool _currentRangedMode;
    private float _chaseDistance;
    private int _rangedModeType;

    public CreatureEventAI(Creature creature, CreatureAiContent content)
        : this(creature, content, EventAiRegistry.Default)
    {
    }

    public CreatureEventAI(Creature creature, CreatureAiContent content, EventAiRegistry registry)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(registry);
        AiContent = content;
        _engine = new EventAiEngine(this, registry, creature.System?.Options.EventAi ?? new EventAiOptions());
    }

    public EventAiEngine Engine => _engine;

    /// <summary>
    /// vmangos GuardEventAI (AI/GuardEventAI.cpp; selected for AIName 'GuardEventAI', or a GUARD-flagged template whose AIName is
    /// 'EventAI', CreatureAISelector.cpp:66-69): the script runs as any EventAI script, but whom it attacks on sight is the guard rule
    /// (<see cref="CreatureMapSystem.CanGuardAggroOnSight"/>).
    /// </summary>
    public bool UsesGuardSightRules { get; init; }

    /// <summary>vmangos CreatureEventAI::MoveInLineOfSight has no guard call (only BasicAI does; combat entry still calls).</summary>
    protected override bool CallsGuardsOnSight => false;

    /// <summary>The guard rule for a GuardEventAI, else the aggressor rule (vmangos GuardEventAI::MoveInLineOfSight, GuardEventAI.cpp:50-77).</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if (!UsesGuardSightRules)
        {
            base.MoveInLineOfSight(who);
            return;
        }

        if (System is { } system && system.CanGuardAggroOnSight(Me, who))
        {
            AttackStart(who);
        }
    }

    /// <summary>The current phase (0..31).</summary>
    public int Phase => _engine.Context.Phase;

    /// <summary>Unsupported event/action types and values found in this creature's rows (reported by the map system).</summary>
    public IReadOnlyList<string> Unsupported => _engine.Unsupported;

    public int EventCount => _engine.Holders.Count;

    /// <summary>Whether the current combat state is ranged (cmangos m_currentRangedMode).</summary>
    internal bool CurrentRangedMode => _currentRangedMode;

    /// <summary>The configured ranged mode (cmangos m_rangedMode).</summary>
    internal bool RangedMode => _rangedMode;

    internal int RangedModeType => _rangedModeType;

    internal float ChaseDistance => _chaseDistance;

    internal CreatureAiContent AiContent { get; }

    internal CreatureMapSystem? Host => System;

    /// <summary>cmangos UnitAI::SetMeleeEnabled: a change starts or stops the swing at the current victim.</summary>
    internal void SetMeleeEnabled(bool enabled)
    {
        if (enabled == MeleeEnabled)
        {
            return;
        }

        MeleeEnabled = enabled;
        if (Victim is { } current)
        {
            System?.SetMelee(Me, current, enabled);
        }
    }

    /// <summary>
    /// ACTION_T_SET_RANGED_MODE (57). Type 3 is TYPE_NO_MELEE_MODE: it keeps the creature in
    /// ranged mode even when the victim is inside melee reach. Movement selection remains owned by
    /// CreatureMapSystem; this state is the contract consumed by EventAI gates and melee control.
    /// </summary>
    internal bool SetRangedMode(bool enabled, float chaseDistance, int type)
    {
        if (type is not (0 or 3) || !float.IsFinite(chaseDistance) || chaseDistance < 0)
        {
            return false;
        }

        _rangedMode = enabled;
        _chaseDistance = chaseDistance;
        _rangedModeType = type;
        _currentRangedMode = enabled;
        return true;
    }

    /// <summary>Back to the engine's reset state with combat movement and melee on (cmangos Reset).</summary>
    public void Reset()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.Reset();
    }

    public override void OnRespawn()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.Respawn();
    }

    public override void OnAggro(Unit target) => _engine.EnterCombat(target);

    public override void OnDeath(Unit? killer) => _engine.Death(killer);

    public override void OnKilledUnit(Unit victim) => _engine.Kill(victim);

    public override void OnEvade() => _engine.Evade();

    public override void OnReachedHome()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.ReachedHome();
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell) => _engine.SpellHit(caster, spell);

    /// <summary>cmangos UnitAI::UpdateAI: choose the victim, then the event timer batch.</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (Me.Combat.IsInCombat)
        {
            UpdateVictim();
        }

        if (!Me.IsAlive)
        {
            return;
        }

        _engine.UpdateEventTimers(diffMs);
    }
}
