using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.ScarletMonastery;

/// <summary>ScriptDev2 boss_scarlet_commander_mograineAI (mangos-classic
/// scarlet_monastery/boss_mograine_and_whitemane.cpp: Reset, Aggro, JustPreventedDeath,
/// SpellHit, HandleLayOnHandsTimer, HandleRevivedTimer, ExecuteAction).</summary>
public sealed class MograineAi(Creature creature, ScarletMonasteryInstance instance) : ScriptedAI(creature)
{
    private uint _strikeMs = 8400, _hammerMs = 9600, _shieldMs = 40_000, _reviveMs, _resumeMs;
    private bool _fakeDeath, _healed, _shielded;

    public bool IsFeigningDeath => _fakeDeath;

    /// <summary>Whether Lay on Hands has run (m_bHeal): death prevention is off and the next killing blow is real.</summary>
    public bool IsRevived => _healed;

    /// <summary>Reset: on respawn, and on an evade before the event started (EnterEvadeMode at NOT_STARTED runs CombatAI's, which resets).</summary>
    protected override void Reset()
    {
        _fakeDeath = _healed = _shielded = false;
        _strikeMs = 8400;
        _hammerMs = 9600;
        _shieldMs = 40_000; // AddCombatAction(MOGRAINE_ACTION_DIVINE_SHIELD, 40000u)
        _reviveMs = _resumeMs = 0;
        Me.UnitFlags &= ~(UnitFlags.ImmuneToNpc | UnitFlags.NotSelectable);
        Me.StandState = StandState.Stand;
        Me.InvincibilityHpThreshold = 1;
    }

    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, -1189005);
        DoCast(Me, 8990);
        if (System is not { } system) return;
        foreach (uint entry in new uint[] { 4303, 4301, 4302, 4299, 4540, 4300 })
            foreach (Creature npc in system.CreaturesOfEntryInRange(Me, entry, 80))
                if (npc.IsAlive && system.SelectNearestTarget(npc, 80) is { } enemy) npc.AI?.AttackStart(enemy);
    }

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1189006);
    public override void OnAttackedBy(Unit attacker)
    {
        if (!_fakeDeath) base.OnAttackedBy(attacker);
    }

    /// <summary>
    /// CombatAI death prevention (JustPreventedDeath), called by the instance from DamageTaken before the combat clamp. Only while the
    /// prevention is on: HandleLayOnHandsTimer turns it off (SetDeathPrevention(false)), so after the revival a killing blow kills.
    /// Deviation: with no living Whitemane to raise him (boss_mograine_and_whitemane.cpp only asks that she is in storage) the
    /// prevention is dropped and the blow kills, instead of leaving him at 1 hp for good.
    /// </summary>
    public void OnLethalDamage()
    {
        if (_fakeDeath || _healed || Me.InvincibilityHpThreshold == 0) return;
        if (instance.FindWhitemane() is not { IsAlive: true } whitemane)
        {
            Me.InvincibilityHpThreshold = 0;
            return;
        }

        instance.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.InProgress);
        whitemane.Motion.MovePoint(1, 1163.113370f, 1398.856812f, 32.527786f, run: true);
        if (whitemane.AI is WhitemaneAi ai) ai.BeginIntro();
        System?.SayText(whitemane, -1189008);
        System?.InterruptCast(Me);
        Me.Map?.Combat.CombatStop(Me);
        System?.MoveIdle(Me);
        Me.Target = default;
        Me.UnitFlags |= UnitFlags.ImmuneToNpc | UnitFlags.NotSelectable;
        Me.StandState = StandState.Dead;
        _fakeDeath = true;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id == 9232) OnResurrected();
        else if (spell.Id == 28441) instance.CompleteAshbringer(caster);
    }

    public void OnResurrected()
    {
        if (!_fakeDeath || _healed) return;
        instance.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.Special);
        _reviveMs = 3000;
    }

    /// <summary>EnterEvadeMode: before the event, CombatAI's evade (and its Reset); once it ran, the event fails (the instance despawns both).</summary>
    public override void OnEvade()
    {
        uint state = instance.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane);
        if (state == EncounterState.NotStarted) Reset();
        else if (state != EncounterState.Fail)
            instance.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.Fail);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_fakeDeath)
        {
            Me.Map?.Combat.CombatStop(Me); // the lethal hit can reassert combat after DamageTaken returned
            if (_reviveMs > 0)
            {
                if (_reviveMs > diffMs) _reviveMs -= diffMs;
                else if (instance.FindWhitemane() is { } whitemane)
                {
                    _reviveMs = 0;
                    _healed = true;
                    Me.StandState = StandState.Stand;
                    System?.SayText(Me, -1189007);
                    Me.InvincibilityHpThreshold = 0;
                    DoCast(whitemane, 9257, triggered: true);
                    _resumeMs = 2000;
                    return;
                }
            }
            if (_resumeMs > 0)
            {
                if (_resumeMs > diffMs) _resumeMs -= diffMs;
                else
                {
                    _resumeMs = 0;
                    _fakeDeath = false;
                    Me.UnitFlags &= ~(UnitFlags.ImmuneToNpc | UnitFlags.NotSelectable);
                    // HandleRevivedTimer: Divine Shield is ready again, Crusader Strike and Hammer restart with their subsequent timers.
                    _shielded = false;
                    _shieldMs = 0;
                    _strikeMs = (uint)Random.Shared.Next(6000, 15001);
                    _hammerMs = (uint)Random.Shared.Next(7000, 18501);
                    DoCast(Me, 8990);
                    if (System?.SelectNearestTarget(Me, 80) is { } enemy) AttackStart(enemy);
                }
            }
            return;
        }

        if (!UpdateVictim()) return;
        if (_shieldMs >= diffMs) _shieldMs -= diffMs;
        else if (!_shielded && Me.MaxHealth > 0 && Me.Health * 2 <= Me.MaxHealth && DoCast(Me, 642) == CreatureCastResult.Ok)
            _shielded = true;
        if (_strikeMs >= diffMs) _strikeMs -= diffMs;
        else if (DoCast(Victim, 14518) == CreatureCastResult.Ok) _strikeMs = (uint)Random.Shared.Next(6000, 15001);
        if (_hammerMs >= diffMs) _hammerMs -= diffMs;
        else if (DoCast(Victim, 5589) == CreatureCastResult.Ok) _hammerMs = (uint)Random.Shared.Next(7000, 18501);
    }
}

/// <summary>ScriptDev2 boss_high_inquisitor_whitemaneAI (mangos-classic
/// scarlet_monastery/boss_mograine_and_whitemane.cpp: EnterCombat, MovementInform,
/// HandleResurrection, HandleResurrectionCombat, ExecuteAction).</summary>
public sealed class WhitemaneAi(Creature creature, ScarletMonasteryInstance instance) : ScriptedAI(creature)
{
    private uint _healMs, _shieldMs, _smiteMs, _mindMs, _resurrectMs, _resumeMs;
    private bool _deepSleep, _intro, _mindArmed;

    public bool DeepSleepTriggered => _deepSleep;
    public bool IsInIntro => _intro;

    /// <summary>JustRespawned (CombatAI's Reset plus SetDeathPrevention(true)); the constructor's passive react state and ranged mode.</summary>
    protected override void Reset()
    {
        _deepSleep = _intro = false;
        _healMs = 10_000;
        _shieldMs = 15_000;
        _smiteMs = 100;
        _mindMs = 0;
        _mindArmed = false; // AddCombatAction(WHITEMANE_ACTION_DOMINATE_MIND, true): disabled until HandleResurrectionCombat
        _resurrectMs = _resumeMs = 0;
        Me.InvincibilityHpThreshold = 1;
        MeleeEnabled = false;
        CasterChaseDistance = 25f;
        Me.ReactState = CreatureReactState.Passive;
    }

    public void BeginIntro() => _intro = true;

    public override void OnAggro(Unit target)
    {
        if (instance.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane) != EncounterState.InProgress) return;
        _intro = false;
        MeleeEnabled = true;
        Me.ReactState = CreatureReactState.Aggressive;
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point) return;
        if (pointId == 1 && _intro)
        {
            Me.ReactState = CreatureReactState.Aggressive;
            if (System?.SelectNearestTarget(Me, 80) is { } enemy) AttackStart(enemy);
        }
        else if (pointId == 2 && _deepSleep) _resurrectMs = 3000;
    }

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1189009);
    public override void OnEvade()
    {
        CombatMovement = true;
        MeleeEnabled = true;
        uint state = instance.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane);
        if (state == EncounterState.NotStarted) Reset();
        else if (state != EncounterState.Fail)
            instance.SetData(ScarletMonasteryInstance.TypeMograineAndWhitemane, EncounterState.Fail);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_resurrectMs > 0)
        {
            if (_resurrectMs > diffMs) _resurrectMs -= diffMs;
            else
            {
                _resurrectMs = 0;
                if (instance.FindMograine() is { } mograine)
                {
                    DoCast(mograine, 9232);
                    (mograine.AI as MograineAi)?.OnResurrected();
                    System?.SayText(Me, -1189010);
                    _resumeMs = 5700;
                    return;
                }
            }
        }
        if (_resumeMs > 0)
        {
            if (_resumeMs > diffMs) _resumeMs -= diffMs;
            else
            {
                _resumeMs = 0;
                // HandleResurrectionCombat: every combat action restarts with its subsequent timer, and Dominate Mind is armed only now.
                _healMs = 13_000;
                _shieldMs = (uint)Random.Shared.Next(22000, 45001);
                _smiteMs = (uint)Random.Shared.Next(2000, 3001);
                _mindMs = (uint)(System?.RandomInt(5000, 10000) ?? 5000);
                _mindArmed = true;
                CombatMovement = true;
                MeleeEnabled = true;
                Me.InvincibilityHpThreshold = 0;
                if (System?.SelectNearestTarget(Me, 80) is { } enemy) AttackStart(enemy);
            }
        }
        if (_deepSleep && (_resurrectMs > 0 || _resumeMs > 0)) return;
        if (!UpdateVictim()) return;
        if (!_deepSleep && Me.MaxHealth > 0 && Me.Health * 2 <= Me.MaxHealth)
        {
            DoCast(Me, 9256);
            _deepSleep = true;
            CombatMovement = false;
            MeleeEnabled = false;
            Me.Target = default;
            if (instance.FindMograine() is { } mograine)
            {
                Me.Motion.Clear();
                Me.Motion.MovePoint(2, mograine.X, mograine.Y, mograine.Z, run: true);
            }
            return;
        }

        if (_healMs >= diffMs) _healMs -= diffMs;
        else
        {
            // DoSelectLowestHpFriendly(50.0f): any friendly in combat within 50 yd (herself and the Scarlet adds too) missing the most health.
            if (SelectLowestHpFriendly(50f) is { } friend && DoCast(friend, 12039) == CreatureCastResult.Ok)
                _healMs = 13_000;
        }
        if (_shieldMs >= diffMs) _shieldMs -= diffMs;
        else if (DoCast(Me, 22187) == CreatureCastResult.Ok) _shieldMs = (uint)Random.Shared.Next(22000, 45001);
        if (_smiteMs >= diffMs) _smiteMs -= diffMs;
        else if (DoCast(Victim, 9481) == CreatureCastResult.Ok) _smiteMs = (uint)Random.Shared.Next(2000, 3001);
        if (!_mindArmed) return;
        if (_mindMs >= diffMs) _mindMs -= diffMs;
        else if ((System?.RandomInt(0, 50) ?? 1) == 0
            && SelectRandomAttackingTarget(0, playerOnly: true) is { } dominated && DoCast(dominated, 14515) == CreatureCastResult.Ok)
        {
            // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0, SPELL_DOMINATEMIND, SELECT_FLAG_PLAYER): a random player of the threat list.
            // The source tests DoCastSpellIfCan's result as a bool (CAST_OK is 0), so it re-arms on a failed cast; the intent, re-arming
            // 20-30 s after a cast that went off, is kept here.
            _mindMs = (uint)Random.Shared.Next(20000, 30001);
        }
    }
}
