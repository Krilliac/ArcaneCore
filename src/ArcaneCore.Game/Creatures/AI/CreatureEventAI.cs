using ArcaneCore.Game.Combat;
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
/// <para>
/// Not sealed so that a ScriptName AI can host the entry's EventAI rows: derive from it, register the type with
/// <see cref="CreatureAiFactory.RegisterEventAiScript"/> and call <c>base</c> from every hook the script overrides; the rows then fire
/// exactly as under the plain EventAI (see <see cref="StonedEventAI"/>).
/// </para>
/// </summary>
public class CreatureEventAI : AggressorAI
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

    /// <summary>
    /// EventAI on the live handler registry, <see cref="EventAiRegistry.Default"/>: the registry object is the swappable holder (its table is
    /// replaced by <see cref="EventAiRegistry.CommitSwap"/>), so this creature sees a module swap without being rebuilt.
    /// </summary>
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
    public bool UsesGuardSightRules { get; set; }

    /// <summary>vmangos CreatureEventAI::MoveInLineOfSight has no guard call (only BasicAI does; combat entry still calls).</summary>
    protected override bool CallsGuardsOnSight => false;

    /// <summary>The guard rule for a GuardEventAI, else the aggressor rule (vmangos GuardEventAI::MoveInLineOfSight, GuardEventAI.cpp:50-77).</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        _engine.MoveInLineOfSight(who); // cmangos: the OOC LOS events first, then UnitAI::MoveInLineOfSight (:1621-1650)
        if (!Me.IsAlive)
        {
            return;
        }

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

    /// <summary>cmangos m_attackDistance: the chase distance an EventAI RANGED_MOVEMENT set (0: melee reach); reset to the ranged-mode distance.</summary>
    internal float AttackDistance { get; set; }

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
    /// ACTION_T_SET_RANGED_MODE (57). The types are cmangos UnitAI RangedModeType: 0 none, 1 full caster, 2 proximity,
    /// 3 no melee mode (keeps the creature in ranged mode even when the victim is inside melee reach) and 4 distancer.
    /// Types 2 and 4 leave ranged mode while the victim is inside melee reach and return once it is 3 yd past it
    /// (cmangos UnitAI::UpdateAI, UnitAI.cpp:1096-1110; <see cref="UpdateRangedModeByReach"/>). Type 1 (7 creatures) leaves ranged mode
    /// when its main spell is blocked (UnitAI::OnSpellInterrupt), which needs the main-spell registry this AI does not have; it is
    /// refused and reported as an unsupported script action instead of silently behaving as type 3. Movement selection remains owned by
    /// CreatureMapSystem; this state is the contract consumed by EventAI gates and melee control.
    /// </summary>
    internal bool SetRangedMode(bool enabled, float chaseDistance, int type)
    {
        if (type == 1)
        {
            CreatureAnomalies.ReportScriptActionUnsupported(Me, $"EventAI action 57 ranged mode type 1 (full caster) on entry {Me.Entry}: the main-spell fallback to melee is not implemented");
            return false;
        }

        if (type is < 0 or > 4 || !float.IsFinite(chaseDistance) || chaseDistance < 0)
        {
            return false;
        }

        _rangedMode = enabled;
        _chaseDistance = chaseDistance;
        AttackDistance = chaseDistance;
        _rangedModeType = type;
        _currentRangedMode = enabled;
        return true;
    }

    /// <summary>cmangos UnitAI::SetCurrentRangedMode for the proximity and distancer types: ranged back on chases at the chase distance, off closes in.</summary>
    private void SetCurrentRangedMode(bool state)
    {
        _currentRangedMode = state;
        AttackDistance = state ? _chaseDistance : 0f;
        System?.ApplyCombatMovement(Me);
    }

    /// <summary>
    /// cmangos UnitAI::UpdateAI for TYPE_PROXIMITY and TYPE_DISTANCER (UnitAI.cpp:1096-1110): in ranged mode a victim inside melee reach ends
    /// it; out of ranged mode it returns once the victim is more than 3 yd past melee reach. Not delivered: the distancing step away from an
    /// immobilised victim (DistanceYourself) and the main-spell-prevented conditions.
    /// </summary>
    private void UpdateRangedModeByReach()
    {
        if (!_rangedMode || _rangedModeType is not (2 or 4) || Victim is not { } victim || !CombatMovement || !_engine.Context.CanExecuteCombatAction)
        {
            return;
        }

        if (_currentRangedMode)
        {
            if (MapCombat.CanReachWithMeleeAutoAttack(Me, victim))
            {
                SetCurrentRangedMode(false);
            }

            return;
        }

        float reach = MathF.Max(Me.GetFloat(UpdateFields.UnitFieldCombatreach), CombatConstants.MinMeleeCombatReach)
            + MathF.Max(victim.GetFloat(UpdateFields.UnitFieldCombatreach), CombatConstants.MinMeleeCombatReach) + CombatConstants.BaseMeleeRangeOffset;
        reach = MathF.Max(reach, CombatConstants.AttackDistance) + 3f;
        float dx = Me.X - victim.X, dy = Me.Y - victim.Y;
        if (!MapCombat.CanReachWithMeleeAutoAttack(Me, victim) && (dx * dx) + (dy * dy) > reach * reach)
        {
            SetCurrentRangedMode(true);
        }
    }

    /// <summary>Back to the engine's reset state with combat movement and melee on (cmangos Reset).</summary>
    public void Reset()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        AttackDistance = _chaseDistance;
        System?.ClearCombatOnlyRoot(Me);
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.Reset();
    }

    public override void OnRespawn()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        AttackDistance = _chaseDistance; // cmangos JustRespawned/Reset: m_attackDistance = m_chaseDistance, so action 29 of the last life does not carry over
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.Respawn();
    }

    public override void OnAggro(Unit target) => _engine.EnterCombat(target);

    public override void OnDeath(Unit? killer) => _engine.Death(killer);

    public override void OnKilledUnit(Unit victim) => _engine.Kill(victim);

    /// <summary>cmangos CreatureEventAI::ReceiveEmote: the EVENT_T_RECEIVE_EMOTE rows.</summary>
    public override void OnReceiveEmote(Player player, uint textEmote) => _engine.ReceiveEmote(player, textEmote);

    /// <summary>cmangos CreatureEventAI::ReceiveAIEvent: the EVENT_T_RECEIVE_AI_EVENT rows.</summary>
    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue) => _engine.ReceiveAiEvent(eventType, sender, invoker);

    public override void OnEvade() => _engine.Evade();

    public override void OnReachedHome()
    {
        CombatMovement = true;
        _currentRangedMode = _rangedMode;
        AttackDistance = _chaseDistance;
        System?.ClearCombatOnlyRoot(Me);
        SetMeleeEnabled(Me.MeleeAllowedByTemplate);
        _engine.ReachedHome();
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell) => _engine.SpellHit(caster, spell);

    public override void OnSpellHitTarget(Unit target, SpellInfo spell) => _engine.SpellHitTarget(target, spell);

    public override void OnJustSummoned(Creature summoned) => _engine.JustSummoned(summoned);

    public override void OnSummonedCreatureJustDied(Creature summoned) => _engine.SummonedJustDied(summoned);

    public override void OnSummonedCreatureDespawn(Creature summoned) => _engine.SummonedDespawned(summoned);

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

        UpdateRangedModeByReach();
        _engine.UpdateEventTimers(diffMs);
    }
}
