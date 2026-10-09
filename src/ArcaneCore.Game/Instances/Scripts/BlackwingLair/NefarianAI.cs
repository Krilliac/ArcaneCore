using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_nefarian.cpp boss_nefarianAI:
/// JustRespawned, MovementInform, HandleAttackStart and ExecuteAction;
/// vmangos boss_nefarian.cpp HandleClassCall/UpdateAI (class targets and phase three).
/// </summary>
public sealed class NefarianAI : RaidBossAI
{
    private bool _landed, _raisedBones, _lowHealthLine;
    private uint _landingMs;

    public NefarianAI(Creature creature) : base(creature, 7)
    {
        AddAction(12000, () => Cast(22539), () => 12000);
        AddAction(30000, () => Cast(22686), () => 30000);
        AddAction(15000, () => Cast(22687, Victim), () => 15000);
        AddAction(7000, () => Cast(19983, Victim), () => 7000);
        AddAction(10000, () => Cast(23364), () => 10000);
        AddAction(35000, ClassCall, () => RandomDelay(35000, 40000));
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        _landed = false;
        MeleeEnabled = false;
        CombatMovement = false;
        Me.AddMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
        Cast(19818, triggered: true);
        Cast(22992, triggered: true);
        Me.Motion.MovePoint(1, -7449.145f, -1320.647f, 476.795f, run: true);
        System?.SayText(Me, 9973);
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point) return;
        if (pointId == 1) Me.Motion.MovePoint(2, -7495.964f, -1252.402f, 476.795f, run: true);
        else if (pointId == 2)
        {
            Me.RemoveMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
            System?.SayText(Me, 9974); // SAY_SHADOWFLAME
            _landingMs = 4000;
        }
    }

    public override bool AttackStart(Unit target) => _landed && base.AttackStart(target);

    /// <summary>
    /// mangos-classic boss_nefarianAI has no Aggro hook: TYPE_NEFARIAN stays SPECIAL from the 42nd drakonid to his death or evade.
    /// Setting IN_PROGRESS here would let a later drakonid death pass the instance's IN_PROGRESS check and spawn a second Nefarian.
    /// </summary>
    public override void OnAggro(Unit target)
    {
    }

    /// <summary>MC boss_nefarianAI::JustDied: SAY_DEATH (vmangos broadcast_text 9971), then TYPE_NEFARIAN DONE.</summary>
    public override void OnDeath(Unit? killer)
    {
        System?.SayText(Me, 9971);
        base.OnDeath(killer);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_landingMs != 0 && (_landingMs = _landingMs > diffMs ? _landingMs - diffMs : 0) == 0)
        {
            _landed = true;
            MeleeEnabled = true;
            CombatMovement = true;
            if (Me.Map?.Players.FirstOrDefault(p => p.IsAlive) is { } player) AttackStart(player);
        }
        if (_landed) base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        base.UpdateCombat(diffMs);
        if (!_raisedBones && Me.Health * 5 < Me.MaxHealth && Cast(23362))
        {
            _raisedBones = true;
            System?.SayText(Me, 9883);
            (Instance as BlackwingLairInstance)?.RaiseBones(Me);
        }
        if (!_lowHealthLine && Me.Health * 20 < Me.MaxHealth)
        {
            _lowHealthLine = true;
            System?.SayText(Me, -1469008); // SAY_XHEALTH (script_texts; no broadcast_text id in ClassicDB)
        }
    }

    /// <summary>
    /// MC ExecuteAction NEFARIAN_CLASS_CALL: the class of a random player, one self-centred cast of its call. The call's enemy area
    /// (target B 15) is narrowed to that class by <see cref="BlackwingLairTargetModule"/> (MC Spell::OnCheckTarget).
    /// </summary>
    private bool ClassCall()
    {
        Player[] players = [.. Me.Map?.Players.Where(p => p.IsAlive && !p.IsGameMaster) ?? []];
        if (players.Length == 0) return false;
        Class chosen = players[System?.RandomInt(0, players.Length - 1) ?? 0].Class;
        (uint spell, int text) = chosen switch
        {
            Class.Warrior => (23397u, 9855), Class.Paladin => (23418u, 9853),
            Class.Hunter => (23436u, 9849), Class.Rogue => (23414u, 9856),
            Class.Priest => (23401u, 9848), Class.Shaman => (23425u, 9854),
            Class.Mage => (23410u, 9850), Class.Warlock => (23427u, 9852),
            Class.Druid => (23398u, 9851), _ => (0u, 0),
        };
        if (spell == 0 || !Cast(spell)) return false;
        System?.SayText(Me, text);
        return true;
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _raisedBones = _lowHealthLine = false;
        _landingMs = 0;
    }
}
