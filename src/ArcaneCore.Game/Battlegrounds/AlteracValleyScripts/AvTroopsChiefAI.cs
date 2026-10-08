using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos AV_npc_troops_chief_EventAI (scripts/battlegrounds/battleground_alterac.cpp:3126-3297), Field Marshal Teravaine, Warmaster Garrick
/// and their ground troops (script npc_AV_troops_chief): handed the assault orders the chief leads the attack (<see cref="LaunchAttack"/>);
/// at its second point it stops, gives the speech, and six seconds later its troops give their war cry and fall in behind it; it stops for
/// good at its last point (48 for Teravaine, 53 for Garrick); when it dies its troops go on along their own path.
/// </summary>
public sealed class AvTroopsChiefAI : EscortAI
{
    private readonly AlteracValleyScripts _scripts;
    private bool _aggro;
    private bool _speechDone;
    private uint _eventTimer;
    private uint _point;

    public AvTroopsChiefAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
    }

    private uint Entry => Me.Template.Entry;

    /// <summary>Whether the chief gave its rally speech to its troops (m_speechDone).</summary>
    public bool SpeechDone => _speechDone;

    /// <summary>
    /// QuestComplete_AV_npc_troops_chief (battleground_alterac.cpp:3299-3341), the chief's part once the assault orders were rewarded: the
    /// team's ground supplies are spent, its ground-assault flag is set again, and the chief starts its escort, open to attack and walking.
    /// </summary>
    public void LaunchAttack(Team team)
    {
        _scripts.Match.ResetGroundChallenge(team);
        _scripts.Match.SetPlayerGoStatus(team, AssaultGround, true);
        Start(run: true);
        Me.UnitFlags = (Me.UnitFlags & ~(UnitFlags.Spawning | UnitFlags.ImmuneToPlayer)) | UnitFlags.Pvp;
        AvScript.SetWalk(Me, true);
    }

    /// <summary>Reset (:3141-3151): after a fight the escort goes on from where the fight began.</summary>
    protected override void Reset()
    {
        if (_aggro)
        {
            SetEscortPaused(false);
            Me.Motion.MovePoint(PointLastPoint, CombatStartPosition.X, CombatStartPosition.Y, CombatStartPosition.Z, run: true);
            _aggro = false;
        }
    }

    protected override void Aggro(Unit target)
    {
        _aggro = true;
        SetEscortPaused(true);
        _scripts.GroupAggro(Me, target);
    }

    /// <summary>After a fight a soldier in formation takes its place behind its chief again; otherwise as any escort.</summary>
    public override void OnEvade()
    {
        if (_scripts.FollowLeader(Me))
        {
            Me.IsEvading = false;
            Reset();
            return;
        }

        base.OnEvade();
    }

    /// <summary>WaypointReached (:3159-3192).</summary>
    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 2:
                if (Entry == NpcFieldMarshalTeravaine)
                {
                    SetEscortPaused(true);
                    AvScript.Say(Me, AvEventAI.SayRamRiderCommander);
                    _eventTimer = 6000;
                    _point = pointId;
                }
                else if (Entry == NpcWarmasterGarrick)
                {
                    SetEscortPaused(true);
                    AvScript.Say(Me, AvEventAI.SayWolfRiderCommander);
                    _eventTimer = 6000;
                    _point = pointId;
                }

                break;
            case 48:
                if (Entry == NpcFieldMarshalTeravaine)
                {
                    StopHere();
                }

                break;
            case 53:
                if (Entry == NpcWarmasterGarrick)
                {
                    StopHere();
                }

                break;
        }
    }

    private void StopHere()
    {
        System?.SetHomePosition(Me, Me.X, Me.Y, Me.Z, 0);
        Stop();
    }

    /// <summary>
    /// JustDied (:3194-3222): troops of the four levels within 100 yd start their own escort. vmangos looks up the Frostwolf reavers when
    /// Teravaine dies and the Stormpike commandos when Garrick dies (the other side's troops, not the chief's own): kept as it is.
    /// </summary>
    public override void OnDeath(Unit? killer)
    {
        _aggro = false;
        _speechDone = false;
        uint troops = Entry switch
        {
            NpcFieldMarshalTeravaine => NpcFrostwolfReaver,
            NpcWarmasterGarrick => NpcStormpikeCommando,
            _ => 0,
        };
        if (troops == 0)
        {
            return;
        }

        for (uint i = 0; i < 4; i++)
        {
            foreach (Creature soldier in AvScript.Near(Me, troops + i, 100f))
            {
                if (soldier.AI is AvTroopsChiefAI escort)
                {
                    _scripts.LeaveGroup(soldier);
                    soldier.Motion.Clear();
                    escort.Start(run: true);
                }
            }
        }
    }

    /// <summary>UpdateEscortAI (:3224-3296): six seconds after the speech the troops of the four levels within 40 yd rally behind the chief.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        (uint troops, int warcry) = Entry switch
        {
            NpcFieldMarshalTeravaine => (NpcStormpikeCommando, AvEventAI.SayWarcryAlliance),
            NpcWarmasterGarrick => (NpcFrostwolfReaver, AvEventAI.SayWarcryHorde),
            _ => (0u, 0),
        };
        if (troops == 0)
        {
            UpdateVictim();
            return;
        }

        if (_eventTimer <= diffMs)
        {
            if (_point == 2)
            {
                if (!_speechDone)
                {
                    _speechDone = true;
                    AvScript.SetWalk(Me, false);
                    for (uint i = 0; i < 4; i++)
                    {
                        foreach (Creature soldier in AvScript.Near(Me, troops + i, 40f))
                        {
                            AvScript.Say(soldier, warcry);
                            AvScript.SetWalk(soldier, false);
                            float distance = MathF.Sqrt(((Me.X - soldier.X) * (Me.X - soldier.X)) + ((Me.Y - soldier.Y) * (Me.Y - soldier.Y)));
                            _scripts.JoinGroup(soldier, Me, AvScript.Angle(Me, soldier) - Me.Orientation, distance);
                        }
                    }
                }

                SetEscortPaused(false);
                _eventTimer = 0;
            }
        }
        else
        {
            _eventTimer -= diffMs;
        }

        UpdateVictim();
    }
}
