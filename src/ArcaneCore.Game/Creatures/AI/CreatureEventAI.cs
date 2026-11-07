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
    public const int CastMainSpell = CastAction.MainSpell;

    /// <summary>cmangos MAX_PHASE: phases 0..31.</summary>
    public const int MaxPhase = 32;

    /// <summary>cmangos UnitAI::UpdateAI: yards past melee reach before a proximity or distancer creature returns to ranged mode.</summary>
    private const float ReturnThreshold = 3f;

    private readonly EventAiEngine _engine;
    private bool _rangedMode;
    private bool _currentRangedMode;
    private float _chaseDistance;
    private int _rangedModeType;
    private readonly HashSet<uint> _mainSpells = [];
    private uint _mainSpellId;

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
        RebuildMainSpells();
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

    /// <inheritdoc/>
    public override bool RunsSpellList => true;

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
    /// cmangos UnitAI::AddMainSpell as the EventAI constructor feeds it (CreatureEventAI.cpp:139-140): the spell ids of the creature's
    /// ACTION_T_CAST rows that carry CAST_MAIN_SPELL (0x100), then the ranged entries of its spell list (UnitAI::SpellListChanged). Rebuilt with
    /// the rows, which a respawn re-reads, and when the list changes.
    /// </summary>
    internal void RebuildMainSpells()
    {
        _mainSpells.Clear();
        _mainSpellId = 0;
        foreach (EventAiHolder holder in _engine.Holders)
        {
            foreach (CreatureAiAction action in holder.Event.Actions)
            {
                if (action.Type == (byte)EventAiActionType.Cast && (action.Param3 & CastMainSpell) != 0 && action.Param1 > 0)
                {
                    _mainSpells.Add((uint)action.Param1);
                    if (_mainSpellId == 0)
                    {
                        _mainSpellId = (uint)action.Param1; // cmangos AddMainSpell keeps the first as m_mainSpellId (UnitAI.cpp:962)
                    }
                }
            }
        }

        if (Me.SpellLists is not { } list)
        {
            return;
        }

        foreach (CreatureSpellListEntry entry in list.ActiveEntries)
        {
            if ((entry.Flags & CreatureSpellListFlags.RangedAction) != 0)
            {
                _mainSpells.Add(entry.SpellId);
                if (_mainSpellId == 0)
                {
                    _mainSpellId = entry.SpellId;
                }
            }
        }
    }

    /// <summary>Whether <paramref name="spellId"/> is one of this creature's main spells (cmangos m_mainSpells).</summary>
    internal bool IsMainSpell(uint spellId) => _mainSpells.Contains(spellId);

    /// <summary>
    /// cmangos UnitAI::HandleSpellCastResult (UnitAI.cpp:838-858): a main spell that fails for lack of power, because the victim is too close
    /// or because the cast is prevented (silence, pacify) ends ranged mode so the creature closes to melee. Not delivered: the
    /// full-caster return to ranged mode on the main spell's cooldown (OnSpellCooldownAdded) is approximated by a main spell that was cast,
    /// and the interrupt fallback (OnSpellInterrupt) has no hook in this AI.
    /// </summary>
    internal void HandleMainSpellCastResult(uint spellId, CreatureCastResult result)
    {
        if (!_rangedMode || !_mainSpells.Contains(spellId))
        {
            return;
        }

        if (_currentRangedMode)
        {
            if (result is CreatureCastResult.NoPower or CreatureCastResult.TooClose or CreatureCastResult.Prevented)
            {
                SetCurrentRangedMode(false);
            }
        }
        else if (_rangedModeType == 1 && result == CreatureCastResult.Ok)
        {
            SetCurrentRangedMode(true);
        }
    }

    /// <summary>
    /// ACTION_T_SET_RANGED_MODE (57). The types are cmangos UnitAI RangedModeType: 0 none, 1 full caster, 2 proximity,
    /// 3 no melee mode (keeps the creature in ranged mode even when the victim is inside melee reach) and 4 distancer.
    /// Types 2 and 4 leave ranged mode while the victim is inside melee reach and return once it is 3 yd past it
    /// (cmangos UnitAI::UpdateAI, UnitAI.cpp:1096-1110; <see cref="UpdateRangedModeByReach"/>); every type leaves it when a main spell
    /// (CAST_MAIN_SPELL) fails for power, range or prevention (<see cref="HandleMainSpellCastResult"/>). Movement selection remains owned by
    /// CreatureMapSystem; this state is the contract consumed by EventAI gates and melee control.
    /// </summary>
    internal bool SetRangedMode(bool enabled, float chaseDistance, int type)
    {
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

    /// <summary>
    /// cmangos <c>IsEligibleForDistancing() &amp;&amp; !IsMainSpellPrevented(m_mainSpellInfo)</c> (UnitAI.cpp:1100-1104, 1126-1147): the first main spell is
    /// ready, the caster is not silenced or pacified for it and, for a mana user, has more than twice its cost. True when the creature has no
    /// main spell or no spell caster is attached (nothing to gate on).
    /// </summary>
    private bool MainSpellUsable() => _mainSpellId == 0 || (System?.CanCastMainSpell(Me, _mainSpellId) ?? true);

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
    /// immobilised victim (DistanceYourself). The return needs a usable main spell (<see cref="MainSpellUsable"/>), so an out-of-mana or silenced creature keeps closing to melee.
    /// </summary>
    private void UpdateRangedModeByReach()
    {
        if (!_rangedMode || _rangedModeType is not (2 or 4) || Victim is not { } victim || !_engine.Context.CanExecuteCombatAction)
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

        // cmangos CanReachWithMeleeAttack(victim, 3.f): the 3 yd is added to the reach before the minimum (ATTACK_DISTANCE) clamp.
        if (!MapCombat.CanReachWithMeleeAutoAttack(Me, victim, ReturnThreshold) && MainSpellUsable())
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
        RebuildMainSpells();
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
