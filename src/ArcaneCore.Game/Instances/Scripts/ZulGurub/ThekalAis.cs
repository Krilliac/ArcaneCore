using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>mangos-classic zulgurub/boss_thekal.cpp boss_thekalBaseAI::JustPreventedDeath,
/// Revive, PreventRevive and the one-shot ACTION_RESSURECTION timer. Lethal damage is reported by the
/// instance before the combat host's invincibility clamp.
/// <para>
/// The fake death is SetCombatScriptStatus(true), not a combat stop: the creature keeps its combat and threat list, stops swinging,
/// moving and choosing victims, and does not evade while it lies there (cmangos Unit::SelectHostileTarget: "do not evade during
/// combat script running"). Once it rises, victim selection runs again, so after a wipe it evades: Thekal's slot then fails at home, a
/// zealot's goes back to NOT_STARTED (<see cref="ThekalZealotAI"/>).
/// </para></summary>
public abstract class ThekalCompanionAI(Creature creature, uint slot) : RaidBossAI(creature, slot)
{
    private bool _fakeDeath;
    private uint _resurrectMs;
    protected Unit? LastTarget;
    public bool FakeDeath => _fakeDeath;
    protected ZulGurubInstance? Raid => Instance as ZulGurubInstance;
    /// <summary>The creature's own instance slot (TYPE_THEKAL 3, TYPE_LORKHAN 6, TYPE_ZATH 7).</summary>
    protected uint Slot => slot;

    public void OnLethalDamage()
    {
        if (_fakeDeath || Me.InvincibilityHpThreshold == 0) return;
        LastTarget = Victim;
        System?.InterruptCast(Me);
        Me.Map?.Combat.AttackStop(Me);
        System?.MoveIdle(Me);
        Me.Target = default;
        Me.UnitFlags |= UnitFlags.NotSelectable; // UNIT_FLAG_UNINTERACTIBLE
        Me.StandState = StandState.Dead;
        Cast(19951, Me, triggered: true);
        _fakeDeath = true;
        _resurrectMs = 10000;
        Instance?.SetData(slot, EncounterState.Special);
        OnFakingDeath();
    }

    /// <summary>boss_thekalBaseAI::OnFakeingDeath.</summary>
    protected virtual void OnFakingDeath() { }

    /// <summary>ACTION_RESSURECTION: runs once, when the timer set at the fake death expires.</summary>
    protected abstract void OnResurrectTimer();

    protected void SetResurrectTimer(uint delayMs) => _resurrectMs = delayMs;

    /// <summary>boss_thekalBaseAI::PreventRevive and the zealots' DisableTimer(ACTION_RESSURECTION).</summary>
    public void PreventRevive()
    {
        System?.InterruptCast(Me);
        _resurrectMs = 0;
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _fakeDeath = false;
        _resurrectMs = 0;
        Me.InvincibilityHpThreshold = 1;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
        Me.StandState = StandState.Stand;
        System?.RemoveAuras(Me, 19951);
    }

    /// <summary>A fake-dead creature does not fight back (its combat script is running).</summary>
    public override void OnAttackedBy(Unit attacker)
    {
        if (!_fakeDeath) base.OnAttackedBy(attacker);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_fakeDeath)
        {
            // Still in combat, but no swing: an attack restarted by another path is dropped again.
            if (Victim is not null) Me.Map?.Combat.AttackStop(Me);
            if (_resurrectMs != 0)
            {
                _resurrectMs = _resurrectMs > diffMs ? _resurrectMs - diffMs : 0;
                if (_resurrectMs == 0) OnResurrectTimer();
            }
            return;
        }
        base.OnUpdate(diffMs);
    }

    /// <summary>boss_thekalBaseAI::Revive: full health, threat reset, back to fighting. With nobody left to fight (a wipe while it lay
    /// there) it evades at once, as cmangos' next SelectHostileTarget does; false then.</summary>
    protected virtual bool Revive()
    {
        _fakeDeath = false;
        _resurrectMs = 0;
        Me.Health = Me.MaxHealth;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
        Me.StandState = StandState.Stand;
        System?.RemoveAuras(Me, 19951);
        ResetThreat();
        Instance?.SetData(slot, EncounterState.InProgress);
        if (!Me.Combat.IsInCombat)
        {
            EnterEvadeMode();
            return false;
        }

        if (LastTarget is { IsAlive: true } target && Me.Combat.Threat.Contains(target)) AttackStart(target);
        UpdateVictim(); // SelectHostileTarget: in combat with an empty threat list it evades
        return !Me.IsEvading;
    }

    /// <summary>boss_thekalAI::JustReachedHome sets TYPE_THEKAL FAIL. The zealots have no JustReachedHome; their Reset sets NOT_STARTED.</summary>
    protected virtual bool FailsAtHome => true;

    public override void OnReachedHome()
    {
        if (FailsAtHome) base.OnReachedHome();
        ResetActions();
    }
}

/// <summary>
/// mob_zealot_lorkhanAI::Reset and mob_zealot_zathAI::Reset: <c>SetData(TYPE_LORKHAN/TYPE_ZATH, NOT_STARTED)</c>. ScriptDev2 calls Reset
/// when the evade starts (ScriptedAI::EnterEvadeMode) and when the creature respawns, so after a wipe the slot is back to NOT_STARTED,
/// not FAIL.
/// </summary>
public abstract class ThekalZealotAI(Creature creature, uint slot) : ThekalCompanionAI(creature, slot)
{
    protected override bool FailsAtHome => false;

    public override void OnEvade()
    {
        base.OnEvade();
        Instance?.SetData(Slot, EncounterState.NotStarted);
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        Instance?.SetData(Slot, EncounterState.NotStarted);
    }
}

/// <summary>mangos-classic boss_thekal.cpp boss_thekalAI::OnFakeingDeath, OnRevive,
/// CanPreventAddsResurrect, JustDied, EnterEvadeMode and ExecuteAction.
/// The two zealots must fake death together before the tiger phase becomes killable.</summary>
public sealed class ThekalAI : ThekalCompanionAI
{
    private bool _tiger;
    private uint _tigerDelay;
    private bool _enraged;

    public ThekalAI(Creature creature) : base(creature, 3)
    {
        // ClassicDB z2815 creature_spell_list 1450901/1450902.
        AddAction(4000, () => !_tiger && Cast(22859, Victim), () => RandomDelay(15000, 20000));
        AddAction(10000, 15000, () => !_tiger && Cast(24185, Me), () => RandomDelay(20000, 30000));
        AddAction(9000, () => !_tiger && Cast(22666, RandomTarget()), () => RandomDelay(20000, 25000));
        AddAction(12000, () => _tiger && Cast(24408, RandomTarget()), () => RandomDelay(15000, 22000));
        AddAction(30000, () => _tiger && Cast(23128, Me), () => 30000);
        AddAction(4000, () => _tiger && Cast(24189, Me), () => RandomDelay(16000, 21000));
        AddAction(15000, 20000, () => _tiger && Cast(24192, Me), () => RandomDelay(20000, 25000));
        AddAction(25000, () => _tiger && Cast(24183, Me), () => 50000);
    }

    private IEnumerable<ThekalCompanionAI> Zealots()
    {
        if (Raid is not { } raid) yield break;
        foreach (uint entry in new uint[] { 11347, 11348 })
            if (raid.FindThekalCompanion(entry)?.AI is ThekalCompanionAI ai)
                yield return ai;
    }

    /// <summary>boss_thekalAI::CanPreventAddsResurrect: both zealots fake-dead stops their pending resurrections.</summary>
    private bool CanPreventAddsResurrect()
    {
        if (Instance?.GetData(6) != EncounterState.Special || Instance.GetData(7) != EncounterState.Special) return false;
        foreach (ThekalCompanionAI zealot in Zealots()) zealot.PreventRevive();
        return true;
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _tiger = _enraged = false;
        _tigerDelay = 0;
        MeleeEnabled = true;
    }

    public override void OnAggro(Unit target) { base.OnAggro(target); System?.SayText(Me, -1309009); }

    protected override void OnFakingDeath()
    {
        // If both zealots are already down, don't wait ten seconds.
        if (CanPreventAddsResurrect()) SetResurrectTimer(1000);
    }

    // "resurrect him in any case"
    protected override void OnResurrectTimer() => _ = Revive();

    protected override bool Revive()
    {
        if (!base.Revive() || !CanPreventAddsResurrect()) return false;
        // boss_thekalAI::OnRevive: uninteractible, no melee, impact visual, tiger form five seconds later.
        Me.UnitFlags |= UnitFlags.NotSelectable;
        MeleeEnabled = false;
        Me.Target = default;
        Cast(24171, Me);
        _tigerDelay = 5000;
        return true;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_tigerDelay != 0)
        {
            _tigerDelay = _tigerDelay > diffMs ? _tigerDelay - diffMs : 0;
            if (_tigerDelay == 0)
            {
                Cast(24169, Me);
                _tiger = true;
                Me.InvincibilityHpThreshold = 0;
                Me.UnitFlags &= ~UnitFlags.NotSelectable;
                MeleeEnabled = true;
            }
        }
        base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        // boss_thekal.cpp ExecuteAction(THEKAL_TIGER_ENRAGE): GetHealthPercent() < 11.
        if (_tiger && !_enraged && HealthBelowPct(11) && Cast(8269, Me)) _enraged = true;
        base.UpdateCombat(diffMs);
    }

    public override void OnEvade()
    {
        base.OnEvade();
        foreach (ThekalCompanionAI zealot in Zealots()) System?.EnterEvadeMode(zealot.Me);
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        System?.SayText(Me, -1309010);
        // Remove the two zealots.
        foreach (ThekalCompanionAI zealot in Zealots())
        {
            System?.SayText(zealot.Me, 10453);
            System?.ForcedDespawn(zealot.Me, 1000);
        }
    }
}

/// <summary>mangos-classic boss_thekal.cpp mob_zealot_lorkhanAI::OnFakeingDeath and
/// ExecuteAction(ACTION_RESSURECTION).</summary>
public sealed class LorKhanAI : ThekalZealotAI
{
    public LorKhanAI(Creature creature) : base(creature, 6)
    {
        // ClassicDB z2815 creature_spell_list 1134701.
        AddAction(0, () => Cast(17201, Me), () => RandomDelay(15000, 20000));
        AddAction(1000, () => Cast(20545, Me), () => 61000);
        AddAction(32000, () => Cast(24208, Me), () => RandomDelay(15000, 20000));
        AddAction(6000, () => Cast(6713, Victim), () => RandomDelay(15000, 25000));
    }

    protected override void OnResurrectTimer()
    {
        if (Instance?.GetData(3) != EncounterState.Special || Instance.GetData(7) != EncounterState.Special) _ = Revive();
    }
}

/// <summary>mangos-classic boss_thekal.cpp mob_zealot_zathAI::OnFakeingDeath and
/// ExecuteAction(ACTION_RESSURECTION).</summary>
public sealed class ZathAI : ThekalZealotAI
{
    public ZathAI(Creature creature) : base(creature, 7)
    {
        // ClassicDB z2815 creature_spell_list 1134801.
        AddAction(8000, () => Cast(15581, Victim), () => RandomDelay(8000, 16000));
        AddAction(25000, () => Cast(12540, Me), () => RandomDelay(17000, 27000));
        AddAction(18000, () => Cast(15614, RandomTarget()), () => RandomDelay(15000, 25000));
        AddAction(5000, () => Cast(21060, Victim), () => RandomDelay(10000, 20000));
        AddAction(0, () => Cast(18765, Me), () => RandomDelay(20000, 25000));
    }

    protected override void OnResurrectTimer()
    {
        if (Instance?.GetData(3) != EncounterState.Special || Instance.GetData(6) != EncounterState.Special) _ = Revive();
    }
}
