using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 boss_thermapluggAI and ActivateBombThermaplugg (mangos-classic
/// gnomeregan/boss_thermaplugg.cpp: Reset, Aggro, JustDied, JustReachedHome, JustSummoned, UpdateAI, OnEffectExecute).</summary>
public sealed class ThermapluggAi(Creature creature, GnomereganInstance instance) : CreatureAI(creature)
{
    public const uint Entry = 7800, WalkingBomb = 7915, KnockAway = 10101, KnockAwayAoe = 11130;
    public const uint ActivateBombA = 11511, ActivateBombB = 11795;
    private readonly Dictionary<ObjectGuid, (float X, float Y, float Z)> _landingBombs = [];
    private readonly HashSet<ObjectGuid> _summonedBombs = [];
    private readonly uint[] _faceTimers = new uint[6];
    private uint _knockMs, _activateMs;
    private bool _phaseTwo;
    private float _spawnX, _spawnY, _spawnZ;

    public bool PhaseTwo => _phaseTwo;

    public override void OnRespawn()
    {
        _knockMs = (uint)Random.Shared.Next(12000, 20001);
        _activateMs = (uint)Random.Shared.Next(10000, 15001);
        _phaseTwo = false;
        _landingBombs.Clear();
        _summonedBombs.Clear();
        Array.Clear(_faceTimers);
    }

    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, -1090024);
        instance.SetData(GnomereganInstance.TypeThermaplugg, EncounterState.InProgress);
        _spawnX = Me.X; _spawnY = Me.Y; _spawnZ = Me.Z;
    }

    public override void OnDeath(Unit? killer)
    {
        instance.SetData(GnomereganInstance.TypeThermaplugg, EncounterState.Done);
        _summonedBombs.Clear();
    }

    public override void OnReachedHome()
    {
        instance.SetData(GnomereganInstance.TypeThermaplugg, EncounterState.Fail);
        if (System is { } system)
            foreach (ObjectGuid guid in _summonedBombs)
                if (system.FindCreature(guid) is { } bomb) system.ForcedDespawn(bomb, 0);
        _summonedBombs.Clear();
    }

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1090027);

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry != WalkingBomb) return;
        _summonedBombs.Add(summoned.Guid);
        float x = 0.2f * _spawnX + 0.8f * summoned.X;
        float y = 0.2f * _spawnY + 0.8f * summoned.Y;
        float z = _spawnZ - 2;
        _landingBombs[summoned.Guid] = (x, y, z);
        summoned.Motion.MovePoint(1, x, y, z, run: true);
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        _summonedBombs.Remove(summoned.Guid);
        _landingBombs.Remove(summoned.Guid);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        MoveLandedBombs();
        float healthPercent = Me.MaxHealth == 0 ? 100f : 100f * Me.Health / Me.MaxHealth;
        if (!_phaseTwo && healthPercent < 50f)
        {
            _phaseTwo = true;
            System?.SayText(Me, -1090025);
        }

        if (_knockMs >= diffMs) _knockMs -= diffMs;
        else
        {
            uint spell = _phaseTwo ? KnockAwayAoe : KnockAway;
            if (DoCast(_phaseTwo ? Me : Victim, spell) == CreatureCastResult.Ok)
                _knockMs = _phaseTwo ? 12_000u : (uint)Random.Shared.Next(17000, 20001);
        }

        if (_activateMs >= diffMs) _activateMs -= diffMs;
        else if (DoCast(Me, _phaseTwo ? ActivateBombB : ActivateBombA) == CreatureCastResult.Ok)
        {
            instance.ActivateBombFace(Random.Shared.Next(6));
            _activateMs = (uint)(Random.Shared.Next(_phaseTwo ? 6 : 12, _phaseTwo ? 13 : 18) * 1000);
            if (Random.Shared.Next(6) == 0) System?.SayText(Me, -1090026);
        }

        for (int i = 0; i < 6; i++)
        {
            if (!instance.FaceActive(i)) { _faceTimers[i] = 0; continue; }
            if (_faceTimers[i] == 0) _faceTimers[i] = 3000;
            if (_faceTimers[i] >= diffMs) { _faceTimers[i] -= diffMs; continue; }
            if (instance.FaceObject(i) is { } face && System is { } system)
            {
                float x = 0.35f * _spawnX + 0.65f * face.X;
                float y = 0.35f * _spawnY + 0.65f * face.Y;
                system.SummonCorpseDespawn(Me, WalkingBomb, x, y, -316.2625f, 0);
            }
            _faceTimers[i] = (uint)Random.Shared.Next(10000, 25001);
        }
    }

    private void MoveLandedBombs()
    {
        if (System is not { } system) return;
        foreach ((ObjectGuid guid, (float x, float y, float z)) in _landingBombs.ToArray())
        {
            if (system.FindCreature(guid) is not { IsAlive: true } bomb)
            {
                _landingBombs.Remove(guid);
                continue;
            }
            float dx = bomb.X - x, dy = bomb.Y - y, dz = bomb.Z - z;
            if (dx * dx + dy * dy + dz * dz <= 1)
            {
                bomb.Motion.MoveFollow(Me, 0, 0);
                _landingBombs.Remove(guid);
            }
        }
    }
}
