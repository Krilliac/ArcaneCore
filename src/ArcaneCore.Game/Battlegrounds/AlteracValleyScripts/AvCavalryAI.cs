using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos AV_NpcEventTroopsAI (scripts/battlegrounds/battleground_alterac.cpp:1335-1430), the wolf and ram riders (script npc_cavalry):
/// mounted until a fight, in their commander's formation after his speech, sent on alone along the rest of the path when he dies, and gone
/// ten minutes after they saw him dead.
/// </summary>
public sealed class AvCavalryAI : EscortAI
{
    /// <summary>m_leaderDieTimer: how long a rider outlives its commander.</summary>
    public const uint LeaderDeathDespawnMs = 600_000;

    private readonly AlteracValleyScripts _scripts;
    private bool _onHisOwn;
    private bool _goesOnAlone;
    private bool _leaderDead;
    private uint _leaderDieTimer = LeaderDeathDespawnMs;

    public AvCavalryAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
    }

    /// <summary>Whether the rider saw its commander dead (m_isLeaderDead).</summary>
    public bool LeaderDead => _leaderDead;

    /// <summary>
    /// The commander died (AV_NpcEventAI::JustDied, :2179-2190): Reset, the five-day respawn delay that marks a rider on its own, the escort
    /// with the commander's place on the path.
    /// </summary>
    internal void GoOnAlone(int waypoint)
    {
        Reset();
        _goesOnAlone = true; // SetRespawnDelay(5 * DAY)
        Start(run: true);
        SetCurrentWaypoint(waypoint);
    }

    /// <summary>Reset (:1351-1373): the mount; a rider on its own starts its escort once, or goes on with it.</summary>
    protected override void Reset()
    {
        if (Me.Template.Entry == NpcRamRider)
        {
            AvScript.Mount(Me, 2786);
        }
        else if (Me.Template.Entry == NpcWolfRider)
        {
            AvScript.Mount(Me, 1166);
        }

        if (_goesOnAlone && !_onHisOwn)
        {
            if (!HasEscortState(EscortState.Escorting))
            {
                Start(run: true);
                SetCurrentWaypoint(CurrentWaypointIndex);
                _onHisOwn = true;
            }
        }
        else if (_onHisOwn)
        {
            SetEscortPaused(false);
        }
    }

    /// <summary>Aggro (:1375-1384): off the mount, the escort holds, and the group fights together.</summary>
    protected override void Aggro(Unit target)
    {
        AvScript.Unmount(Me);
        SetEscortPaused(true);
        _scripts.GroupAggro(Me, target);
    }

    protected override void WaypointReached(uint pointId)
    {
    }

    /// <summary>After a fight a rider in formation takes its place beside its commander again; otherwise as any escort.</summary>
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

    /// <summary>UpdateEscortAI (:1390-1424): watch the commander within 200 yd; ten minutes after his death the rider disappears.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        uint commander = Me.Template.Entry switch
        {
            NpcWolfRider => NpcWolfRiderCommander,
            NpcRamRider => NpcRamRiderCommander,
            _ => 0,
        };

        if (!_leaderDead && commander != 0)
        {
            if (AvScript.Near(Me, commander, 200f).Any(c => !c.IsAlive))
            {
                _leaderDead = true;
            }
        }
        else if (_leaderDead)
        {
            if (_leaderDieTimer < diffMs)
            {
                System?.ForcedDespawn(Me, 0);
                return;
            }

            _leaderDieTimer -= diffMs;
        }

        UpdateVictim();
    }
}
