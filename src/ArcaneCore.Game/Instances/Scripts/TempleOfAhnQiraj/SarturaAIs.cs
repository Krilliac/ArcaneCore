using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// Shared Whirlwind phase of Sartura and her Royal Guards (vmangos boss_sartura.cpp):
/// temporarily stop melee, repeatedly shift threat, then resume melee and roll the next phase.
/// </summary>
public abstract class SarturaWhirlwindAI(Creature creature, uint? encounter, uint whirlwindSpell,
    int firstMin, int firstMax, uint phaseLength, int repeatMin, int repeatMax)
    : RaidBossAI(creature, encounter)
{
    private uint _whirlwind;
    private uint _phase;
    private uint _retarget;

    public bool IsWhirling => _phase != 0;

    protected override void ResetActions()
    {
        base.ResetActions();
        _whirlwind = RandomDelay(firstMin, firstMax);
        _phase = 0;
        _retarget = RandomDelay(5000, 7500);
        SetMeleeEnabled(true);
    }

    protected static bool Due(ref uint timer, uint diffMs)
    {
        timer = timer > diffMs ? timer - diffMs : 0;
        return timer == 0;
    }

    protected virtual bool CanRetarget(Unit target) => true;

    private void AssignRandomThreat()
    {
        if (RandomTarget() is not { } target || !CanRetarget(target)) return;
        ResetThreat();
        Me.Combat.Threat.AddThreat(target, RandomDelay(1000, 2000));
    }

    /// <summary>Advance the phase and return whether the boss is whirling after this update.</summary>
    protected bool UpdateWhirlwind(uint diffMs)
    {
        if (_phase != 0)
        {
            if (Due(ref _retarget, diffMs))
            {
                AssignRandomThreat();
                _retarget = RandomDelay(1000, 2000);
            }

            if (Due(ref _phase, diffMs))
            {
                _phase = 0;
                _whirlwind = RandomDelay(repeatMin, repeatMax);
                _retarget = RandomDelay(3000, 7000);
                SetMeleeEnabled(true);
            }

            return _phase != 0;
        }

        if (Due(ref _whirlwind, diffMs) && Cast(whirlwindSpell, Me))
        {
            AssignRandomThreat();
            _phase = phaseLength;
            _retarget = RandomDelay(1000, 2000);
            SetMeleeEnabled(false);
        }

        if (_phase == 0 && Due(ref _retarget, diffMs))
        {
            AssignRandomThreat();
            _retarget = RandomDelay(3000, 7000);
        }

        return _phase != 0;
    }
}

/// <summary>Battleguard Sartura (15516), vmangos boss_sarturaAI.</summary>
public sealed class SarturaAI
    : SarturaWhirlwindAI
{
    private readonly TempleOfAhnQirajInstance _temple;
    private uint _cleave = 4000;
    private uint _hardEnrage = 600_000;
    private uint _leash = 2500;
    private bool _softEnraged;
    private bool _hardEnraged;

    public SarturaAI(Creature creature, TempleOfAhnQirajInstance temple)
        : base(creature, TempleOfAhnQirajInstance.Sartura, 26083, 8000, 12000, 15000, 5000, 10000)
    {
        _temple = temple;
        ResetActions();
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _cleave = 4000;
        _hardEnrage = 600_000;
        _leash = 2500;
        _softEnraged = false;
        _hardEnraged = false;
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (who is Player && Victim is null && System is { } system && system.CanAggroOnSight(Me, who, scriptedRange: 85f))
            system.EnterCombatWithTarget(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, 11442);
        System?.SetInCombatWithZone(Me);
    }

    public override void OnDeath(Unit? killer)
    {
        System?.SayText(Me, 11444);
        base.OnDeath(killer);
    }

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, 11443);

    protected override bool CanRetarget(Unit target)
    {
        float dx = target.X - Me.X, dy = target.Y - Me.Y, dz = target.Z - Me.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= Map.VisibilityRange * Map.VisibilityRange;
    }

    public override void OnEvade()
    {
        _temple.SetData(TempleOfAhnQirajInstance.Sartura, EncounterState.Fail);
        foreach (Creature guard in System?.Creatures.Where(c => c.Entry == 15984).ToArray() ?? [])
        {
            if (!guard.IsAlive) System?.ForceRespawn(guard);
            else if (guard.Combat.IsInCombat) guard.AI?.EnterEvadeMode();
        }
        base.OnEvade();
    }

    protected override void UpdateCombat(uint diffMs)
    {
        bool whirling = UpdateWhirlwind(diffMs);
        if (!whirling && Due(ref _cleave, diffMs) && Cast(25174, Victim))
            _cleave = RandomDelay(3000, 4000);

        if (!_softEnraged && Below(20) && Cast(26527, Me, triggered: whirling))
        {
            _softEnraged = true;
            System?.SayText(Me, 2384);
        }

        if (!_hardEnraged && Due(ref _hardEnrage, diffMs) && Cast(27680, Me, triggered: whirling))
        {
            _hardEnraged = true;
            System?.SayText(Me, 4428);
        }

        if (Due(ref _leash, diffMs))
        {
            _leash = 2500;
            if (Me.Y > 1780) EnterEvadeMode();
        }
    }
}

/// <summary>Sartura's Royal Guard (15984), vmangos mob_sartura_royal_guardAI.</summary>
public sealed class SarturaRoyalGuardAI
    : SarturaWhirlwindAI
{
    private readonly TempleOfAhnQirajInstance _temple;
    private uint _knockback;
    private uint _leash;

    public SarturaRoyalGuardAI(Creature creature, TempleOfAhnQirajInstance temple)
        : base(creature, null, 26038, 8000, 10000, 8000, 2000, 6000)
    {
        _temple = temple;
        ResetActions();
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _knockback = RandomDelay(6000, 12000);
        _leash = 2500;
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SetInCombatWithZone(Me);
    }

    public override void OnEvade()
    {
        if (_temple.GetData(TempleOfAhnQirajInstance.Sartura) == EncounterState.InProgress)
            System?.Creatures.FirstOrDefault(c => c.Entry == 15516 && c.IsAlive)?.AI?.EnterEvadeMode();
        base.OnEvade();
    }

    public override void OnUpdate(uint diffMs)
    {
        // ClassicDB z2815 row 1598403 (EVENT_T_TARGET_NOT_REACHABLE) would be shadowed by this
        // scripted AI. Check before victim selection can refresh the chase generator.
        if (Victim is { } unreachable && !Me.Motion.IsReachable)
            Cast(21727, unreachable);
        base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        bool whirling = UpdateWhirlwind(diffMs);
        if (!whirling && Due(ref _knockback, diffMs) && Victim is { } victim
            && MapCombat.CanReachWithMeleeAutoAttack(Me, victim) && Cast(19813, victim))
            _knockback = RandomDelay(8000, 14000);

        if (Due(ref _leash, diffMs))
        {
            _leash = 2500;
            if (Me.Y > 1780) EnterEvadeMode();
        }
    }
}
