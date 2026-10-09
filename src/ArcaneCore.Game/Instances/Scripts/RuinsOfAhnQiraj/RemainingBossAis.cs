using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp
/// DoSendNextArmyWave and boss_rajaxx.cpp; the instance drives the seven army waves. Rajaxx's own aggro
/// does not start the event (OnCreatureEnterCombat has no Rajaxx case: Andorov's dialogue does); his
/// death completes it and his evade fails it (OnCreatureDeath, OnCreatureEvade).</summary>
public sealed class RajaxxAI : RaidBossAI
{
    public RajaxxAI(Creature creature) : base(creature, null)
    {
        // ClassicDB z2815 creature_ai_scripts 1534101-1534107; the script AI
        // supersedes EventAI, so keep its combat spell cadence here.
        AddAction(7000, 9000, () => Cast(6713, Victim), () => RandomDelay(7000, 9000));
        AddAction(12000, 18000, () => Cast(25599, Me), () => RandomDelay(16000, 21000));
        AddAction(8000, 12000, () => Cast(20477, RandomTarget()), () => RandomDelay(8000, 12000));
        AddAction(600000, () => Cast(8269, Me), () => 120000);
    }
    public override void OnRespawn() { base.OnRespawn(); Cast(18943, Me, triggered: true); }
    public override void OnDeath(Unit? killer) => Instance?.SetData(1, EncounterState.Done);
}

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_moam.cpp boss_moamAI::Reset,
/// ExecuteAction and SummonManaFiendsMoam::OnEffectExecute.</summary>
public sealed class MoamAI : RaidBossAI
{
    private uint _energizeMs;
    private readonly HashSet<ObjectGuid> _fiends = [];
    public MoamAI(Creature creature) : base(creature, 2)
    {
        AddAction(0, ArcaneEruption, () => 5000);
        AddAction(9000, () => Cast(15550, Victim), () => 15000);
        AddAction(8000, 12000, () => Cast(18941, Me), () => RandomDelay(8000, 12000));
        AddAction(6000, () => Cast(25676, Me), () => 6000);
        AddAction(90000, () => Cast(25684, Me), () => 90000);
        AddAction(90000, Energize, () => 180000);
    }
    private bool ArcaneEruption()
    {
        if (Me.GetUInt32(UpdateFields.UnitFieldPower1) < Me.GetUInt32(UpdateFields.UnitFieldMaxpower1) || !Cast(25672, Me))
            return false;
        System?.SayText(Me, -1509001); // EMOTE_MANA_FULL
        return true;
    }
    private bool Energize()
    {
        if (!Cast(25685, Me)) return false;
        System?.SayText(Me, -1509028); // EMOTE_ENERGIZING
        _energizeMs = 90000;
        return true;
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, -1509000); // EMOTE_AGGRO
    }
    // boss_moamAI::Reset: no mana, and the summoned fiends go.
    protected override void ResetActions()
    {
        base.ResetActions();
        _energizeMs = 0;
        Me.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        if (System is { } system)
            foreach (Creature fiend in system.Creatures.Where(c => _fiends.Contains(c.Guid)).ToArray())
                system.ForcedDespawn(fiend, 0);
        _fiends.Clear();
    }
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry != 15527) return;
        _fiends.Add(summoned.Guid);
        if (RandomTarget() is { } target) summoned.AI?.AttackStart(target);
    }
    public override void OnSummonedCreatureJustDied(Creature summoned) => FiendGone(summoned);
    public override void OnSummonedCreatureDespawn(Creature summoned) => FiendGone(summoned);
    private void FiendGone(Creature summoned)
    {
        if (_fiends.Remove(summoned.Guid) && _fiends.Count == 0)
            System?.RemoveAuras(Me, 25685);
    }
    public override void OnUpdate(uint diffMs)
    {
        if (_energizeMs != 0)
        {
            _energizeMs = _energizeMs > diffMs ? _energizeMs - diffMs : 0;
            if (_energizeMs == 0) System?.RemoveAuras(Me, 25685);
        }
        base.OnUpdate(diffMs);
    }
}

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_buru.cpp boss_buruAI::
/// Reset, DoAttackNewTarget, HandlePhaseTwo and ExecuteAction.</summary>
public sealed class BuruAI : RaidBossAI
{
    private bool _transforming, _transformed;
    private uint _transitionMs;
    public BuruAI(Creature creature) : base(creature, 3)
    {
        AddAction(5000, () => !_transforming && Cast(96, Victim), () => 5000);
        AddAction(9000, () => !_transforming && Cast(1834, Me), () => 9000);
        AddAction(60000, () => !_transforming && Cast(1557, Me), () => 60000);
        AddAction(0, () => _transforming && Cast(20512, Me), () => 6000);
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(25640, Me);
        AttackNewTarget();
    }
    protected override void ResetActions()
    {
        base.ResetActions();
        _transforming = _transformed = false;
        _transitionMs = 0;
    }
    protected override void UpdateCombat(uint diffMs)
    {
        if (!_transforming && Below(19))
        {
            _transforming = true;
            _transitionMs = 2000;
            ResetThreat();
        }
        if (_transforming && !_transformed)
        {
            _transitionMs = _transitionMs > diffMs ? _transitionMs - diffMs : 0;
            if (_transitionMs == 0 && Cast(24721, Me))
            {
                System?.RemoveAuras(Me, 25640);
                Cast(1557, Me, triggered: true);
                _transformed = true;
            }
        }
        base.UpdateCombat(diffMs);
    }
    public void OnEggDestroyed()
    {
        if (_transforming) return;
        System?.RemoveAuras(Me, 1557);
        System?.RemoveAuras(Me, 1834);
        AttackNewTarget();
    }

    /// <summary>boss_buruAI::DoAttackNewTarget: a random player gets a fixed huge threat and the target emote.</summary>
    private void AttackNewTarget()
    {
        if (_transforming) return;
        ResetThreat();
        if (Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>().Where(p => p.IsAlive).ToArray() is { Length: > 0 } players &&
            System is { } system)
        {
            Player target = players[system.RandomInt(0, players.Length - 1)];
            Me.Combat.Threat.AddThreat(target, 1000000f);
            AttackStart(target);
            system.SayText(Me, -1509002, target); // EMOTE_TARGET
        }
    }
}

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_ayamiss.cpp boss_ayamissAI::
/// Reset, ExecuteAction and phase-2 landing; spells use imported summon/larva effects.</summary>
public sealed class AyamissAI : RaidBossAI
{
    private bool _ground, _frenzy;
    private Unit? _paralyzedTarget;
    private readonly List<Creature> _swarmers = [];
    public AyamissAI(Creature creature) : base(creature, 4)
    {
        AddAction(15000, Paralyze, () => 15000);
        AddAction(20000, 30000, () => Cast(25749, Me), () => RandomDelay(15000, 20000));
        AddAction(5000, () => !_ground && Cast(25748, Victim), () => RandomDelay(2000, 3000));
        AddAction(5000, () => Cast(25708, Me), () => 5000);
        AddAction(60000, SendSwarmers, () => 60000);
        AddAction(5000, () => _ground && Cast(25852, Victim), () => RandomDelay(8000, 15000));
        AddAction(3000, () => _ground && Cast(3391, Victim), () => RandomDelay(5000, 7000));
    }
    private bool Paralyze()
    {
        Unit? target = RandomTarget();
        if (target is null || !Cast(25725, target)) return false;
        _paralyzedTarget = target;
        Cast(System!.RandomInt(0, 1) == 0 ? 26538u : 26539u, Me, triggered: true);
        return true;
    }
    private bool SendSwarmers()
    {
        foreach (Creature swarmer in _swarmers.Where(c => c.IsAlive))
            if (RandomTarget() is { } target) swarmer.AI?.AttackStart(target);
        _swarmers.Clear();
        return true;
    }
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry == 15546)
        {
            _swarmers.Add(summoned);
            uint[] teleport = [25709, 25825, 25826, 25827, 25828];
            if (System is { } system)
                system.CastSpell(summoned, teleport[system.RandomInt(0, 4)], summoned, triggered: true);
        }
        else if (summoned.Entry == 15555 && _paralyzedTarget is { IsAlive: true } target)
            summoned.AI?.AttackStart(target);
        else if (summoned.Entry == 15934 && RandomTarget() is { } hornetTarget)
            summoned.AI?.AttackStart(hornetTarget);
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        CombatMovement = false;
        MeleeEnabled = false;
        System?.SetMelee(Me, target, false);
        Me.AddMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
        Me.Motion.MovePoint(1, -9689.292f, 1547.912f, 48.02729f, run: true);
    }
    protected override void ResetActions()
    {
        base.ResetActions();
        _ground = _frenzy = false;
        _paralyzedTarget = null;
        _swarmers.Clear();
        CombatMovement = false;
        MeleeEnabled = false;
        Me.RemoveMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
    }
    protected override void UpdateCombat(uint diffMs)
    {
        if (!_ground && Below(70))
        {
            _ground = true;
            Me.RemoveMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
            CombatMovement = true;
            MeleeEnabled = true;
            if (Victim is { } victim) System?.SetMelee(Me, victim, true);
            Me.Motion.MovePoint(2, Me.Home.X, Me.Home.Y, Me.Home.Z, run: true);
            ResetThreat();
        }
        if (!_frenzy && Below(19) && Cast(8269, Me)) _frenzy = true;
        base.UpdateCombat(diffMs);
    }
}

/// <summary>mangos-classic ruins_of_ahnqiraj/boss_ossirian.cpp boss_ossirianAI::
/// Reset, SpellHit, ExecuteAction and GOUse_go_ossirian_crystal. Crystal use is
/// connected by RuinsOfAhnQirajInstance.OnObjectUsed.</summary>
public sealed class OssirianAI : RaidBossAI
{
    private uint _supremeMs = 45000;
    private RuinsOfAhnQirajInstance? Raid => Instance as RuinsOfAhnQirajInstance;
    public OssirianAI(Creature creature) : base(creature, 5)
    {
        // OSSIRIAN_INITIAL_SPAWN: ten seconds in, five more crystals (DoSpawnNextCrystal(5)); runs once.
        AddAction(10000, () => { Raid?.SpawnOssirianCrystals(5); return true; }, () => uint.MaxValue);
        AddAction(20000, () => Cast(25189, Victim), () => 20000);
        AddAction(30000, () => Cast(25188, Me), () => 30000);
        AddAction(30000, () => Cast(25195, Me), () => RandomDelay(20000, 30000));
    }
    protected override void ResetActions()
    {
        base.ResetActions();
        _supremeMs = 45000;
        Cast(19818, Me, triggered: true);
        Cast(25176, Me, triggered: true);
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, -1509025);
    }
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (caster is Creature { Entry: 15590 } && spell.Id is 25177 or 25178 or 25180 or 25181 or 25183)
        {
            System?.RemoveAuras(Me, 25176);
            _supremeMs = 45000;
            Raid?.SpawnOssirianCrystals(1);
        }
    }
    protected override void UpdateCombat(uint diffMs)
    {
        _supremeMs = _supremeMs > diffMs ? _supremeMs - diffMs : 0;
        if (_supremeMs == 0 && Cast(25176, Me)) _supremeMs = 45000;
        base.UpdateCombat(diffMs);
    }
}
