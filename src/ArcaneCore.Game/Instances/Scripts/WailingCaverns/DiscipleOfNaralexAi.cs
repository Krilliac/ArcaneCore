using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>Disciple of Naralex escort and ritual, from mangos-classic
/// wailing_caverns/wailing_cavernsScripts.cpp:109-462, npc_disciple_of_naralexAI::WaypointReached,
/// SummonedCreatureJustDied and UpdateEscortAI. The path is script_waypoint entry/path 3678.</summary>
public sealed class DiscipleOfNaralexAi(Creature creature, WailingCavernsInstance instance) : EscortAI(creature)
{
    public const uint PathId = 3678;
    private uint _eventTimer, _sleepTimer = 5_000, _potionTimer = 5_000;
    private uint _point;
    private int _phase;
    private int _summonedAlive;
    private bool _firstHit;
    public int EventPhase => _phase;

    protected override void Reset()
    {
        _sleepTimer = 5_000;
        _potionTimer = 5_000;
        _firstHit = false;
        if (!HasEscortState(EscortState.Escorting))
        {
            _eventTimer = 0;
            _point = 0;
            _phase = 0;
            _summonedAlive = 0;
        }
    }

    protected override void JustRespawned()
    {
        base.JustRespawned();
        if (instance.GetData(WailingCavernsInstance.TypeDisciple) != EncounterState.Done)
        {
            instance.SetData(WailingCavernsInstance.TypeDisciple, EncounterState.Fail);
        }
    }

    public override void OnAttackedBy(Unit attacker)
    {
        if (!_firstHit)
        {
            int text = attacker is Creature { Entry: 3654 } ? 1276 : _point >= 30 ? 1274
                : (System?.RandomInt(0, 99) ?? 0) < 90 ? 1273 : 1277;
            System?.SayText(Me, text, attacker);
            _firstHit = true;
        }

        base.OnAttackedBy(attacker);
    }

    protected override void JustStartedEscort()
        => instance.SetData(WailingCavernsInstance.TypeDisciple, EncounterState.InProgress);

    protected override void WaypointReached(uint pointId)
    {
        if (pointId is not (12 or 30 or 70))
        {
            return;
        }

        _point = pointId;
        _phase = 0;
        _eventTimer = pointId == 12 ? 2_000u : 1_000u;
        SetEscortPaused(true);
        if (pointId == 12) System?.SayText(Me, 1256);
    }

    public override void OnJustSummoned(Creature summoned) => _summonedAlive++;

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (_summonedAlive > 0 && --_summonedAlive == 0 && _eventTimer == 0)
        {
            _eventTimer = 2_000;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_eventTimer != 0)
        {
            if (_eventTimer > diffMs)
            {
                _eventTimer -= diffMs;
            }
            else
            {
                _eventTimer = 0;
                if (_point == 12) UpdateCorner();
                else if (_point == 30) UpdateCircle();
                else if (_point == 70) UpdateChamber();
            }
        }

        if (_potionTimer <= diffMs)
        {
            if (Me.MaxHealth != 0 && Me.Health * 100 < Me.MaxHealth * 80)
            {
                if (DoCast(Me, 8141) == CreatureCastResult.Ok) _potionTimer = 45_000;
            }
            else _potionTimer = 5_000;
        }
        else _potionTimer -= diffMs;

        if (!UpdateVictim() || Victim is not { } victim) return;
        if (_sleepTimer <= diffMs)
        {
            if (DoCast(victim, 1090) == CreatureCastResult.Ok) _sleepTimer = 30_000;
        }
        else _sleepTimer -= diffMs;
    }

    private void UpdateCorner()
    {
        if (_phase++ == 0)
        {
            Summon(3636, -67.44779f, 214.5348f, -93.42037f, 0);
            Summon(3636, -67.85276f, 203.7873f, -93.57328f, 0);
        }
        else
        {
            SetEscortPaused(false);
        }
    }

    private void UpdateCircle()
    {
        switch (_phase++)
        {
            case 0:
                System?.SayText(Me, 1258);
                DoCast(Me, 6270);
                _eventTimer = 20_000;
                break;
            case 1:
                (float x, float y, float z, float o)[] points =
                [
                    (-50.1237f, 274.7166f, -92.7608f, 3.0368f), (-60.2538f, 273.0981f, -92.7608f, 0.4014f),
                    (-57.5452f, 280.2068f, -92.7608f, 5.0789f),
                ];
                foreach ((float x, float y, float z, float o) in points)
                {
                    uint entry = (System?.RandomInt(0, 1) ?? 0) == 0 ? 5048u : 5755u;
                    Summon(entry, x, y, z, o);
                }

                break;
            case 2: _eventTimer = 10_000; break;
            case 3:
                System?.SayText(Me, 1259);
                _point++; // source avoids its special evade path after purification
                SetEscortPaused(false);
                break;
        }
    }

    private void UpdateChamber()
    {
        switch (_phase++)
        {
            case 0: System?.SayText(Me, 1264); CombatMovement = false; _eventTimer = 5_000; break;
            case 1: DoCast(Me, 6271); System?.SayText(Me, 1265); _eventTimer = 3_000; break;
            case 2:
                Summon(5762, 171.39545f, 213.76605f, -105.50746f, 0);
                Summon(5762, 156.72229f, 189.91829f, -107.48995f, 0);
                Summon(5762, 121.39977f, 166.31746f, -105.54061f, 0);
                _eventTimer = 5_000;
                break;
            case 3:
                if (instance.Naralex is { } naralex) System?.SayText(naralex, 1268);
                break;
            case 4:
                foreach ((float x, float y, float z) in new (float, float, float)[]
                {
                    (162.06705f, 218.71494f, -105.36240f), (115.55489f, 168.22847f, -105.68655f),
                    (82.065025f, 280.37723f, -103.29671f), (144.84305f, 278.07928f, -104.57445f),
                    (155.84459f, 186.68817f, -107.08412f), (145.35356f, 219.34600f, -102.98572f),
                    (164.62735f, 274.12335f, -107.29780f),
                }) Summon(5763, x, y, z, 0);
                _eventTimer = 20_000;
                break;
            case 5:
                if (instance.Naralex is { } naralex2) System?.SayText(naralex2, 1269);
                break;
            case 6:
                if (instance.Naralex is { } naralex3) System?.SayText(naralex3, 1270);
                Summon(3654, 150.94276f, 262.79715f, -103.90348f, 0);
                break;
            case 7:
                if (instance.Naralex is { } awake)
                {
                    awake.StandState = StandState.Stand;
                    System?.SayText(awake, 1271);
                }

                instance.SetData(WailingCavernsInstance.TypeDisciple, EncounterState.Done);
                _eventTimer = 5_000;
                break;
            case 8: System?.SayText(Me, 2101); _eventTimer = 1_000; break;
            case 9:
                if (instance.Naralex is { } thanks) System?.SayText(thanks, 1272);
                _eventTimer = 7_000;
                break;
            case 10:
                if (instance.Naralex is { } farewell) System?.SayText(farewell, 2103);
                _eventTimer = 3_000;
                break;
            case 11:
                DoCast(Me, 8153);
                if (instance.Naralex is { } bird) System?.CastSpell(bird, 8153, bird, false);
                _eventTimer = 8_000;
                break;
            case 12:
                Me.AddMovementFlags(MovementFlags.Levitating);
                System?.SetScriptHover(Me, true);
                SetEscortPaused(false);
                SetRun(true);
                if (instance.Naralex is { } follower)
                {
                    follower.AddMovementFlags(MovementFlags.Levitating);
                    System?.SetScriptHover(follower, true);
                    System?.SetScriptRun(follower, true);
                    follower.Motion.MoveFollow(Me, 5f, 0);
                }

                _eventTimer = 30_000;
                break;
            case 13:
                if (instance.Naralex is { } gone) System?.ForcedDespawn(gone, 1_000);
                System?.ForcedDespawn(Me, 1_000);
                instance.DespawnAll();
                break;
        }
    }

    private void Summon(uint entry, float x, float y, float z, float o)
        => System?.SummonCorpseDespawn(Me, entry, x, y, z, o);
}
