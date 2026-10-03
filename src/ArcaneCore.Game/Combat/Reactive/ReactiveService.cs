using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>vmangos ReactiveType (UnitDefines.h:748-754): the timed windows opened by an avoided attack.</summary>
public enum ReactiveType
{
    /// <summary>Opened on the victim of a dodge, parry or block: Revenge, Riposte (aura state Defense).</summary>
    Defense = 1,

    /// <summary>Opened on a hunter whose attack was parried: Counterattack (aura state HunterParry).</summary>
    HunterParry = 2,

    /// <summary>Opened on a warrior whose attack was dodged: Overpower (the combo point marker).</summary>
    Overpower = 5,
}

/// <summary>What happened to a melee or ranged attack, as the reactions care about it (vmangos procExtra PROC_EX_DODGE/PARRY/BLOCK).</summary>
[Flags]
public enum AttackAvoidance
{
    None = 0,
    Dodge = 1,
    Parry = 2,
    Block = 4,
}

/// <summary>
/// The reactive abilities (vmangos Unit::ProcSkillsAndReactives, Unit.cpp:8834-8915; StartReactiveTimer / UpdateReactives /
/// ClearAllReactives, Unit.cpp:9424-9486): an avoided attack opens a four second window (<see cref="ReactiveTimerStartMs"/>)
/// for the abilities that need it. The victim of a dodge (not a rogue), parry or block is put in the aura state Defense;
/// a hunter whose attack was parried in HunterParry (and gets a combo point as Counterattack's marker); a warrior whose
/// attack was dodged gets a combo point on the victim, the marker that allows Overpower. When the window ends the state
/// (or the warrior's marker) is removed. Patch-1.6.1-only branches and the Berserking crit state (builds up to 1.8.4) are
/// not part of a 1.12.1 server.
/// </summary>
public sealed class ReactiveService
{
    /// <summary>vmangos REACTIVE_TIMER_START (UnitDefines.h:746).</summary>
    public const uint ReactiveTimerStartMs = 4000;

    private const int ReactiveCount = 6;

    private sealed class State
    {
        public readonly uint[] Timers = new uint[ReactiveCount];
        public readonly ObjectGuid[] Targets = new ObjectGuid[ReactiveCount];
    }

    private readonly AuraStateService _states;
    private readonly Func<ComboPointService> _combos;
    private readonly ConditionalWeakTable<Unit, State> _reactives = new();
    private readonly List<Unit> _active = [];

    /// <param name="states">The aura states the reactions set and clear.</param>
    /// <param name="combos">The combo points (Overpower and Counterattack markers), resolved at first use.</param>
    public ReactiveService(AuraStateService states, Func<ComboPointService> combos)
    {
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _combos = combos ?? throw new ArgumentNullException(nameof(combos));
    }

    public AuraStateService AuraStates => _states;

    /// <summary>vmangos Unit::StartReactiveTimer.</summary>
    public void StartReactiveTimer(Unit unit, ReactiveType reactive, ObjectGuid target)
    {
        ArgumentNullException.ThrowIfNull(unit);
        State state = _reactives.GetOrCreateValue(unit);
        state.Timers[(int)reactive] = ReactiveTimerStartMs;
        state.Targets[(int)reactive] = target;
        if (!_active.Contains(unit))
        {
            _active.Add(unit);
        }
    }

    /// <summary>vmangos Unit::GetReactiveTarget: whom the window was opened against (empty when closed).</summary>
    public ObjectGuid GetReactiveTarget(Unit unit, ReactiveType reactive)
        => _reactives.TryGetValue(unit, out State? state) ? state.Targets[(int)reactive] : default;

    /// <summary>Whether a window is open.</summary>
    public bool IsReactiveOpen(Unit unit, ReactiveType reactive)
        => _reactives.TryGetValue(unit, out State? state) && state.Timers[(int)reactive] != 0;

    /// <summary>
    /// The outcome of an attack by <paramref name="attacker"/> on <paramref name="victim"/> (vmangos ProcSkillsAndReactives
    /// for the victim and for the attacker, run for melee and ranged white hits and abilities).
    /// </summary>
    public void OnAttackAvoided(Unit attacker, Unit victim, AttackAvoidance avoidance)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (avoidance == AttackAvoidance.None)
        {
            return;
        }

        // For the victim.
        if ((avoidance & AttackAvoidance.Dodge) != 0 && victim.Class != Class.Rogue)
        {
            _states.ModifyAuraState(victim, AuraState.Defense, true);
            StartReactiveTimer(victim, ReactiveType.Defense, attacker.Guid);
        }

        if ((avoidance & AttackAvoidance.Parry) != 0)
        {
            if (victim.Class == Class.Hunter)
            {
                _states.ModifyAuraState(victim, AuraState.HunterParry, true);
                StartReactiveTimer(victim, ReactiveType.HunterParry, attacker.Guid);
                if (victim is Player hunter)
                {
                    _combos().AddComboPoints(hunter, attacker, 1);
                }
            }
            else
            {
                _states.ModifyAuraState(victim, AuraState.Defense, true);
                StartReactiveTimer(victim, ReactiveType.Defense, attacker.Guid);
            }
        }

        if ((avoidance & AttackAvoidance.Block) != 0)
        {
            _states.ModifyAuraState(victim, AuraState.Defense, true);
            StartReactiveTimer(victim, ReactiveType.Defense, attacker.Guid);
        }

        // For the attacker: Overpower on a dodge.
        if ((avoidance & AttackAvoidance.Dodge) != 0 && attacker is Player { Class: Class.Warrior } warrior)
        {
            _combos().AddComboPoints(warrior, victim, 1);
            StartReactiveTimer(warrior, ReactiveType.Overpower, victim.Guid);
        }
    }

    /// <summary>vmangos Unit::UpdateReactives for every unit with an open window.</summary>
    public void Update(uint diffMs)
    {
        foreach (Unit unit in _active.ToArray())
        {
            UpdateUnit(unit, diffMs);
        }
    }

    /// <summary>vmangos Unit::UpdateReactives: run one unit's windows down; an ended window removes its state or marker.</summary>
    public void UpdateUnit(Unit unit, uint diffMs)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_reactives.TryGetValue(unit, out State? state))
        {
            _active.Remove(unit);
            return;
        }

        bool any = false;
        for (int r = 0; r < ReactiveCount; r++)
        {
            if (state.Timers[r] == 0)
            {
                continue;
            }

            if (state.Timers[r] <= diffMs)
            {
                state.Timers[r] = 0;
                state.Targets[r] = default;
                OnWindowEnded(unit, (ReactiveType)r);
            }
            else
            {
                state.Timers[r] -= diffMs;
                any = true;
            }
        }

        if (!any)
        {
            _active.Remove(unit);
        }
    }

    /// <summary>A unit left the world: its windows are dropped without any effect.</summary>
    public void Forget(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _reactives.Remove(unit);
        _active.Remove(unit);
    }

    /// <summary>The units with an open window (diagnostics and the per-map updater).</summary>
    public IReadOnlyList<Unit> ActiveUnits => _active;

    private void OnWindowEnded(Unit unit, ReactiveType reactive)
    {
        switch (reactive)
        {
            case ReactiveType.Defense:
                _states.ModifyAuraState(unit, AuraState.Defense, false);
                break;
            case ReactiveType.HunterParry:
                if (unit.Class == Class.Hunter)
                {
                    _states.ModifyAuraState(unit, AuraState.HunterParry, false);
                }

                break;
            case ReactiveType.Overpower:
                if (unit is Player { Class: Class.Warrior } warrior)
                {
                    _combos().ClearComboPoints(warrior);
                }

                break;
        }
    }

    /// <summary>
    /// vmangos Unit::ClearAllReactives (Unit.cpp:9424-9444): every window closes at once, the Defense (and a hunter's
    /// HunterParry) state goes, and a warrior's marker is cleared.
    /// </summary>
    public void ClearAllReactives(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_reactives.TryGetValue(unit, out State? state))
        {
            Array.Clear(state.Timers);
            Array.Clear(state.Targets);
            _active.Remove(unit);
        }

        _states.ModifyAuraState(unit, AuraState.Defense, false);
        if (unit.Class == Class.Hunter)
        {
            _states.ModifyAuraState(unit, AuraState.HunterParry, false);
        }

        if (unit is Player { Class: Class.Warrior } warrior)
        {
            _combos().ClearComboPoints(warrior);
        }
    }

    /// <summary>A unit died: the health state and every window go (vmangos Unit::SetDeathState JUST_DIED, Unit.cpp:7357-7362).</summary>
    public void OnUnitDied(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _states.ClearHealthStates(unit);
        ClearAllReactives(unit);
    }

    /// <summary>
    /// Follow one map's combat: white swings that were dodged, parried or blocked open windows, and deaths close them
    /// (<see cref="OnAttackAvoided"/>, <see cref="OnUnitDied"/>). Observing a map twice is harmless.
    /// </summary>
    public void Observe(MapCombat combat)
    {
        ArgumentNullException.ThrowIfNull(combat);
        if (!_observed.Add(combat))
        {
            return;
        }

        combat.MeleeSwingResolved += info => OnAttackAvoided(info.Attacker, info.Target, AvoidanceOf(info.Outcome));
        combat.UnitKilled += (_, victim) => OnUnitDied(victim);
    }

    private readonly HashSet<MapCombat> _observed = new(ReferenceEqualityComparer.Instance);

    /// <summary>The reactions' view of a white swing outcome (vmangos CalculateMeleeDamage procEx, Unit.cpp:8806-8830).</summary>
    public static AttackAvoidance AvoidanceOf(MeleeHitOutcome outcome) => outcome switch
    {
        MeleeHitOutcome.Dodge => AttackAvoidance.Dodge,
        MeleeHitOutcome.Parry => AttackAvoidance.Parry,
        MeleeHitOutcome.Block => AttackAvoidance.Block,
        _ => AttackAvoidance.None,
    };
}

/// <summary>
/// An ability that was avoided opens the reactive windows too (vmangos ProcDamageAndSpell for melee and ranged class
/// spells): a melee or ranged damage-class spell that a target dodged or parried.
/// </summary>
public sealed class ReactiveSpellObserver(ReactiveService reactives) : ISpellCastObserver
{
    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (cast.Spell.DamageClass is not (SpellDamageClass.Melee or SpellDamageClass.Ranged)
            || ReferenceEquals(outcome.Target, cast.Caster))
        {
            return;
        }

        AttackAvoidance avoidance = outcome.Miss switch
        {
            SpellMissInfo.Dodge => AttackAvoidance.Dodge,
            SpellMissInfo.Parry => AttackAvoidance.Parry,
            SpellMissInfo.Block => AttackAvoidance.Block,
            _ => AttackAvoidance.None,
        };
        reactives.OnAttackAvoided(cast.Caster, outcome.Target, avoidance);
    }
}

/// <summary>
/// The per-map tick of the reactives: runs the windows down and refreshes the 20% health state of the players and the
/// units in combat (vmangos Unit::Update, Unit.cpp:228 and 318-319).
/// </summary>
public sealed class ReactiveUpdater(ReactiveService reactives) : IMapUpdater
{
    public void Update(Map map, uint diffMs)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (Unit unit in reactives.ActiveUnits.Where(u => u.Map is null).ToArray())
        {
            reactives.Forget(unit);
        }

        // Only the windows of this map's units run down here, so each runs once per world tick.
        foreach (Unit unit in reactives.ActiveUnits.Where(u => ReferenceEquals(u.Map, map)).ToArray())
        {
            reactives.UpdateUnit(unit, diffMs);
        }

        foreach (Player player in map.Players)
        {
            reactives.AuraStates.UpdateHealthState(player);
        }

        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            foreach (Unit unit in combat.TrackedUnits.ToArray())
            {
                reactives.AuraStates.UpdateHealthState(unit);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }
}
