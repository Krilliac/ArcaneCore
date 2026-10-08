using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 npc_blastmaster_emi_shortfuseAI (mangos-classic gnomeregan/gnomeregan.cpp:
/// StartEvent, WaypointStart, WaypointReached, UpdateEscortAI, DoSummonPack, SummonedCreatureJustDied).</summary>
public sealed class EmiShortfuseAi(Creature creature, GnomereganInstance instance) : EscortAI(creature)
{
    public const uint Entry = 7998, Grubbis = 7361, Chomper = 6215, Burrower = 6206, Ambusher = 6207;
    private static readonly (int Pack, uint Entry, float X, float Y, float Z, float O)[] Summons =
    [
        (1, Ambusher, -566.8114f, -111.7036f, -151.1891f, 5.986479f),
        (1, Ambusher, -568.5875f, -113.7559f, -151.1869f, 0.06981317f),
        (1, Ambusher, -570.2333f, -116.8126f, -151.2272f, 0.296706f),
        (1, Ambusher, -550.6331f, -108.7592f, -153.965f, 0.8901179f),
        (1, Ambusher, -558.9717f, -115.0669f, -151.8799f, 0.5235988f),
        (1, Ambusher, -556.6719f, -112.0526f, -152.8255f, 0.4886922f),
        (1, Ambusher, -552.6419f, -113.4385f, -153.0727f, 0.8028514f),
        (1, Ambusher, -549.1248f, -112.1469f, -153.7987f, 0.7504916f),
        (1, Ambusher, -546.7435f, -112.3051f, -154.2225f, 0.9250245f),
        (2, Ambusher, -571.4071f, -108.7721f, -150.6547f, 5.480334f),
        (2, Ambusher, -573.797f, -106.5265f, -150.4106f, 5.550147f),
        (2, Ambusher, -576.3784f, -108.0483f, -150.4227f, 5.585053f),
        (2, Ambusher, -576.697f, -111.7413f, -150.6484f, 5.759586f),
        (3, Ambusher, -571.3161f, -114.4412f, -151.0931f, 6.021386f),
        (3, Ambusher, -570.3127f, -111.7964f, -151.04f, 2.042035f),
        (4, Ambusher, -474.5954f, -104.074f, -146.0483f, 2.338741f),
        (4, Ambusher, -477.9396f, -108.6563f, -145.7394f, 1.553343f),
        (4, Ambusher, -475.6625f, -97.12168f, -146.5959f, 1.291544f),
        (4, Ambusher, -480.5233f, -88.40702f, -146.3772f, 3.001966f),
        (5, Ambusher, -474.2943f, -105.2212f, -145.9747f, 2.251475f),
        (5, Ambusher, -481.1831f, -101.4225f, -146.377f, 2.146755f),
        (5, Burrower, -475.0871f, -100.016f, -146.4382f, 2.303835f),
        (5, Ambusher, -478.8562f, -106.9321f, -145.8533f, 1.658063f),
        (6, Ambusher, -473.8762f, -107.4022f, -145.838f, 2.024582f),
        (6, Ambusher, -490.5134f, -92.72843f, -148.0954f, 3.054326f),
        (6, Ambusher, -491.401f, -88.25341f, -148.0358f, 3.560472f),
        (6, Ambusher, -479.1431f, -106.227f, -145.9097f, 1.727876f),
        (6, Ambusher, -475.3185f, -101.4804f, -146.2717f, 2.234021f),
        (6, Ambusher, -485.1559f, -89.57419f, -146.9299f, 3.071779f),
        (6, Ambusher, -482.2516f, -96.80614f, -146.6596f, 2.303835f),
        (6, Ambusher, -477.9874f, -92.82047f, -146.6944f, 3.124139f),
        (7, Grubbis, -476.3761f, -108.1901f, -145.7763f, 1.919862f),
        (7, Chomper, -473.1326f, -103.0901f, -146.1155f, 2.042035f),
    ];

    private readonly HashSet<ObjectGuid> _summoned = [];
    private uint _phaseTimer;
    private int _phase;
    private ObjectGuid _playerGuid;
    private bool _southOpen, _northOpen, _aggroText;

    public int Phase => _phase;

    protected override void Reset()
    {
        _aggroText = false;
        if (HasEscortState(EscortState.Escorting)) return;
        _phase = 0;
        _phaseTimer = 0;
        _southOpen = _northOpen = false;
        _summoned.Clear();
        if (instance.GetData(GnomereganInstance.TypeGrubbis) == EncounterState.Done) Me.NpcFlags = 0;
    }

    /// <summary>Emi's gossip action: begin the Grubbis encounter after a first try or a failed attempt.</summary>
    public bool StartEvent(Player player)
    {
        if (instance.GetData(GnomereganInstance.TypeGrubbis) is not (EncounterState.NotStarted or EncounterState.Fail)) return false;
        instance.SetData(GnomereganInstance.TypeGrubbis, EncounterState.InProgress);
        _phase = 1;
        _phaseTimer = 1000;
        _playerGuid = player.Guid;
        return true;
    }

    public override bool AttackStart(Unit target) => PreparingCharge() ? false : base.AttackStart(target);
    public override void MoveInLineOfSight(Unit who) { if (!PreparingCharge()) base.MoveInLineOfSight(who); }
    public override void OnAttackedBy(Unit attacker)
    {
        if (!_aggroText)
        {
            _aggroText = true;
            if (Random.Shared.Next(3) == 0) Say(Random.Shared.Next(2) == 0 ? -1090007 : -1090028, attacker);
        }
        base.OnAttackedBy(attacker);
    }

    public override void OnDeath(Unit? killer)
    {
        instance.SetData(GnomereganInstance.TypeGrubbis, EncounterState.Fail);
        if (_southOpen) instance.ToggleCave(GnomereganInstance.CaveSouth);
        if (_northOpen) instance.ToggleCave(GnomereganInstance.CaveNorth);
        if (System is { } system)
            foreach (ObjectGuid guid in _summoned)
                if (system.FindCreature(guid) is { } npc) system.ForcedDespawn(npc, 0);
        _summoned.Clear();
    }

    public override void OnJustSummoned(Creature summoned)
    {
        _summoned.Add(summoned.Guid);
        if (summoned.Template.Entry == Grubbis) Say(-1090023, summoned);
        else if (summoned.Template.Entry is Burrower or Ambusher)
        {
            uint cave = _phase > 20 ? GnomereganInstance.CaveNorth : GnomereganInstance.CaveSouth;
            if (instance.CaveObject(cave) is { } door)
                summoned.Motion.MovePoint(1, door.X, door.Y, door.Z, run: true);
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (summoned.Template.Entry == Grubbis)
        {
            instance.SetData(GnomereganInstance.TypeGrubbis, EncounterState.Done);
            _phaseTimer = 1000;
        }
        _summoned.Remove(summoned.Guid);
    }

    protected override void WaypointStart(uint pointId)
    {
        switch (pointId)
        {
            case 10:
                if (!_southOpen) instance.ToggleCave(GnomereganInstance.CaveSouth);
                _southOpen = true;
                break;
            case 12: Say(-1090008); break;
            case 16:
                Say(-1090016);
                if (!_northOpen) instance.ToggleCave(GnomereganInstance.CaveNorth);
                _northOpen = true;
                break;
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 4: _phaseTimer = 1000; break;
            case 9: _phaseTimer = 2000; break;
            case 11: _phaseTimer = 15000; break;
            case 13: _phaseTimer = 10000; break;
            case 15: SetEscortPaused(true); Say(-1090010); _phaseTimer = 5000; break;
            case 16: _phaseTimer = 15000; break;
            case 17: _phaseTimer = 10000; break;
            case 19: _phaseTimer = 2000; SetEscortPaused(true); break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_phaseTimer == 0 || Victim is not null) { UpdateVictim(); return; }
        if (_phaseTimer > diffMs) { _phaseTimer -= diffMs; return; }
        _phaseTimer = 0;
        switch (_phase++)
        {
            case 1: Say(-1090000); Me.NpcFlags = 0; _phaseTimer = 5000; break;
            case 2: Say(-1090001); _phaseTimer = 3500; break;
            case 3: Start(); break;
            case 4: Say(-1090002); break;
            case 5: Say(-1090003); _phaseTimer = 6000; break;
            case 6: Say(-1090004); _phaseTimer = 9000; break;
            case 7: Face(instance.CaveObject(GnomereganInstance.CaveSouth)); _phaseTimer = 2000; break;
            case 8: Say(-1090005); _phaseTimer = 5000; break;
            case 9: Say(-1090006); _phaseTimer = 2000; break;
            case 10: SummonPack(1); break;
            case 11: SummonPack(2); Charge(1); _phaseTimer = 1; break;
            case 12: break;
            case 13: SummonPack(3); Charge(2); _phaseTimer = 11000; break;
            case 14: _phaseTimer = 1; break;
            case 15: Face(Me.Map?.FindPlayer(_playerGuid)); Say(-1090009); break;
            case 16: Say(-1090011); _phaseTimer = 5000; break;
            case 17: Say(-1090012); _phaseTimer = 1000; break;
            case 18: DoCast(Me, 12159); _phaseTimer = 500; break;
            case 19:
                instance.ToggleCave(GnomereganInstance.CaveSouth);
                _southOpen = false;
                instance.SetData(GnomereganInstance.TypeExplosiveCharge, GnomereganInstance.ChargeUse);
                _phaseTimer = 5000;
                break;
            case 20: _phaseTimer = 6000; break;
            case 21: Say(-1090013); _phaseTimer = 6000; break;
            case 22: Say(-1090014); _phaseTimer = 3000; break;
            case 23: Face(instance.CaveObject(GnomereganInstance.CaveNorth)); _phaseTimer = 3000; break;
            case 24: Say(-1090015); _phaseTimer = 8000; break;
            case 25: SetEscortPaused(false); SummonPack(4); break;
            case 26: SummonPack(5); Charge(3); _phaseTimer = 1; break;
            case 27: break;
            case 28: SummonPack(6); Charge(4); _phaseTimer = 10000; break;
            case 29: _phaseTimer = 1; break;
            case 30: Say(-1090017); break;
            case 31: Face(instance.CaveObject(GnomereganInstance.CaveNorth)); Say(-1090018); _phaseTimer = 5000; break;
            case 32: Say(-1090019); _phaseTimer = 1000; break;
            case 33: SummonPack(7); break;
            case 34: Face(instance.CaveObject(GnomereganInstance.CaveNorth)); _phaseTimer = 5000; break;
            case 35: Say(-1090020); _phaseTimer = 5000; break;
            case 36: Say(-1090021); _phaseTimer = 2000; break;
            case 37: _phaseTimer = 1000; break;
            case 38: DoCast(Me, 12158); _phaseTimer = 500; break;
            case 39:
                instance.ToggleCave(GnomereganInstance.CaveNorth);
                _northOpen = false;
                instance.SetData(GnomereganInstance.TypeExplosiveCharge, GnomereganInstance.ChargeUse);
                _phaseTimer = 8000;
                break;
            case 40: DoCast(Me, 11542); Say(-1090022); break;
        }
    }

    private bool PreparingCharge() => _phase is 11 or 13 or 26 or 28;
    private void Say(int id, Unit? target = null) => System?.SayText(Me, id, target);
    private void Face(WorldObject? target)
    {
        if (target is not null)
            System?.SetFacingTo(Me, MathF.Atan2(target.Y - Me.Y, target.X - Me.X));
    }
    private void Charge(uint slot) => instance.SetData(GnomereganInstance.TypeExplosiveCharge, slot);
    private void SummonPack(int pack)
    {
        if (System is not { } system) return;
        foreach (var row in Summons)
            if (row.Pack == pack) system.SummonCorpseDespawn(Me, row.Entry, row.X, row.Y, row.Z, row.O);
    }
}
