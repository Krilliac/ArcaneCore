using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The scripted escort (vmangos npc_escortAI, AI/ScriptedEscortAI.cpp; ScriptDev2): a creature that walks its entry's script path point by
/// point once <see cref="Start"/>ed, pauses on request, breaks off to fight, then runs back to where the fight began and goes on, and at the
/// end of the path either loops home or disappears (and respawns at once when asked). Subclasses react to <see cref="WaypointReached"/>.
/// <para>
/// The path is vmangos <c>script_waypoint</c> (one path per entry, points in id order): here the entry's <c>creature_movement_template</c>
/// path <see cref="EscortPathId"/>, keyed by entry and point, which is where such rows import to (the world schema has no script_waypoint
/// table). An entry without rows cannot start, as in vmangos ("EscortAI Start with 0 waypoints").
/// </para>
/// <para>
/// Not ported, because no script of this code base escorts a player: the player and quest of an escort (the assist of a player in combat,
/// the player distance check that fails the escort, the group quest failure at death) and the aggro of passive escorts by nearby enemies.
/// </para>
/// </summary>
public abstract class EscortAI : CreatureAI
{
    /// <summary>The point id of the run back to the combat start position (POINT_LAST_POINT, ScriptedEscortAI.cpp:23-27).</summary>
    public const uint PointLastPoint = 0xFFFFFF;

    /// <summary>The point id of the run home of a looping escort (POINT_HOME).</summary>
    public const uint PointHome = 0xFFFFFE;

    /// <summary>The <c>creature_movement_template</c> path that holds an entry's escort points.</summary>
    public const uint EscortPathId = 0;

    /// <summary>The default delay before the first waypoint (m_uiDelayBeforeTheFirstWaypoint, 2.5 s).</summary>
    public const uint DefaultDelayBeforeFirstWaypointMs = 2500;

    private readonly List<CreatureWaypoint> _waypoints = [];
    private uint _waypointWaitMs;
    private bool _running;
    private bool _canInstantRespawn;
    private bool _canReturnToStart;
    private bool _respawnedOnce;
    private float _combatStartO;

    protected EscortAI(Creature creature)
        : base(creature)
    {
        _waypointWaitMs = DelayBeforeFirstWaypointMs;
        CombatStartPosition = new Vector3(creature.X, creature.Y, creature.Z);
        _combatStartO = creature.Orientation;
    }

    /// <summary>vmangos eEscortState.</summary>
    [Flags]
    public enum EscortState
    {
        None = 0x0,
        Escorting = 0x1,
        Returning = 0x2,
        Paused = 0x4,
    }

    public EscortState State { get; private set; }

    /// <summary>The index of the waypoint the escort walks to next (vmangos getCurrentWP).</summary>
    public int CurrentWaypointIndex { get; private set; }

    /// <summary>The number of points the escort loaded at its start.</summary>
    public int WaypointCount => _waypoints.Count;

    /// <summary>Whether the escort runs between its points (m_bIsRunning).</summary>
    public bool IsRunning => _running;

    /// <summary>Where the last fight began (SetCombatStartPosition): where the escort runs back to and goes on from.</summary>
    public Vector3 CombatStartPosition { get; protected set; }

    /// <summary>vmangos m_uiDelayBeforeTheFirstWaypoint.</summary>
    protected uint DelayBeforeFirstWaypointMs { get; set; } = DefaultDelayBeforeFirstWaypointMs;

    /// <inheritdoc />
    public override bool AggroesOnSight => !Me.Template.Civilian;

    public bool HasEscortState(EscortState state) => (State & state) != 0;

    protected void AddEscortState(EscortState state) => State |= state;

    protected void RemoveEscortState(EscortState state) => State &= ~state;

    /// <summary>vmangos setCurrentWP: an index past the path is refused (logged in vmangos).</summary>
    public void SetCurrentWaypoint(int index)
    {
        if (index >= 0 && index < _waypoints.Count)
        {
            CurrentWaypointIndex = index;
        }
    }

    /// <summary>
    /// vmangos npc_escortAI::Start (ScriptedEscortAI.cpp:452-506): refused in combat, while escorting, or without points. The points are
    /// loaded, the NPC flags cleared, the walk mode set and the first point waits <see cref="DelayBeforeFirstWaypointMs"/>... from the last
    /// reset of the timer (the constructor or a respawn), as in vmangos.
    /// </summary>
    public bool Start(bool run = false, bool instantRespawn = false, bool canLoopPath = false)
    {
        if (Me.Combat.IsInCombat || HasEscortState(EscortState.Escorting))
        {
            return false;
        }

        _waypoints.Clear();
        if (System is { } system)
        {
            _waypoints.AddRange(system.Content.GetEntryWaypoints(Me.Template.Entry, EscortPathId));
        }

        if (_waypoints.Count == 0)
        {
            System?.ReportEscortWithoutPath(Me);
            return false;
        }

        _running = run;
        _canInstantRespawn = instantRespawn;
        _canReturnToStart = canLoopPath;
        if (Me.Motion.DefaultType == MovementGeneratorType.Waypoint)
        {
            System?.MoveIdle(Me); // "EscortAI start with WAYPOINT_MOTION_TYPE, changed to MoveIdle."
        }

        Me.NpcFlags = 0;
        CurrentWaypointIndex = 0;
        System?.SetScriptRun(Me, _running);
        AddEscortState(EscortState.Escorting);
        JustStartedEscort();
        return true;
    }

    /// <summary>vmangos npc_escortAI::Stop: the escort ends where it stands.</summary>
    public void Stop() => RemoveEscortState(EscortState.Escorting | EscortState.Paused);

    /// <summary>vmangos SetEscortPaused (only while escorting).</summary>
    public void SetEscortPaused(bool paused)
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            return;
        }

        if (paused)
        {
            AddEscortState(EscortState.Paused);
        }
        else
        {
            RemoveEscortState(EscortState.Paused);
        }
    }

    /// <summary>vmangos SetRun.</summary>
    public void SetRun(bool run)
    {
        if (run != _running)
        {
            System?.SetScriptRun(Me, run);
        }

        _running = run;
    }

    /// <summary>A point of the path was reached (its point id).</summary>
    protected abstract void WaypointReached(uint pointId);

    /// <summary>The escort set off towards a point.</summary>
    protected virtual void WaypointStart(uint pointId)
    {
    }

    protected virtual void JustStartedEscort()
    {
    }

    /// <summary>vmangos ScriptedAI::Reset: the constructor, every evade and every respawn.</summary>
    protected virtual void Reset()
    {
    }

    /// <summary>vmangos ScriptedAI::Aggro, after the combat start position was taken.</summary>
    protected virtual void Aggro(Unit target)
    {
    }

    /// <summary>
    /// vmangos npc_escortAI::JustRespawned (ScriptedEscortAI.cpp:146-162): no escort, combat movement back on, the first-waypoint delay, the
    /// template faction, then <see cref="Reset"/>.
    /// </summary>
    protected virtual void JustRespawned()
    {
        State = EscortState.None;
        CombatMovement = true;
        _waypointWaitMs = DelayBeforeFirstWaypointMs;
        if (Me.FactionTemplate != Me.Template.Faction)
        {
            Me.FactionTemplate = Me.Template.Faction;
        }

        Reset();
    }

    /// <summary>The script's own update, after the waypoint logic (vmangos UpdateEscortAI). By default it keeps the victim from the threat list.</summary>
    protected virtual void UpdateEscortAI(uint diffMs) => UpdateVictim();

    /// <summary>
    /// The creature entered the world alive for the first time with this AI (the work a ScriptDev script does in its constructor, which here
    /// runs when the map has placed the creature), before the first <see cref="Reset"/>.
    /// </summary>
    protected virtual void JustSpawned()
    {
    }

    /// <summary>The first call is the spawn (<see cref="JustSpawned"/>, then Reset), every later one a respawn (<see cref="JustRespawned"/>).</summary>
    public sealed override void OnRespawn()
    {
        if (!_respawnedOnce)
        {
            _respawnedOnce = true;
            JustSpawned();
            Reset();
            return;
        }

        JustRespawned();
    }

    /// <summary>vmangos npc_escortAI::EnterCombat: the place the fight began, unless the escort was still running back to the last one.</summary>
    public sealed override void OnAggro(Unit target)
    {
        if (!HasEscortState(EscortState.Returning))
        {
            CombatStartPosition = new Vector3(Me.X, Me.Y, Me.Z);
            _combatStartO = Me.Orientation;
        }

        Aggro(target);
    }

    /// <summary>vmangos npc_escortAI::EnterEvadeMode: back to the combat start position (<see cref="ReturnToCombatStartPosition"/>), then <see cref="Reset"/>.</summary>
    public override void OnEvade()
    {
        ReturnToCombatStartPosition();
        Reset();
    }

    /// <summary>
    /// vmangos ReturnToCombatStartPosition (ScriptedEscortAI.cpp:521-548): an escort runs back to where the fight began (unless that move is
    /// already under way) instead of going home; a creature that does not escort goes home as any other. vmangos' chase took the active slot
    /// of the waypoint move, so the move is cleared here too (the stack keeps the interrupted waypoint move beneath the chase).
    /// </summary>
    protected void ReturnToCombatStartPosition()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            return; // the map system's evade sends it home
        }

        Me.IsEvading = false;
        if (Me.Motion.Top is PointMovementGenerator { Id: PointLastPoint })
        {
            return; // already on the way back (vmangos: a point move is the active motion)
        }

        AddEscortState(EscortState.Returning);
        Me.Motion.Clear();
        Vector3 start = CombatStartPosition;
        if (Vector2.Distance(new Vector2(Me.X, Me.Y), new Vector2(start.X, start.Y)) > 1000f)
        {
            CombatStartPosition = new Vector3(Me.X, Me.Y, Me.Z);
            OnMovementInform(MovementGeneratorType.Point, PointLastPoint);
            return;
        }

        Me.Motion.MovePoint(PointLastPoint, start.X, start.Y, start.Z, run: true);
    }

    /// <summary>vmangos npc_escortAI::UpdateAI (ScriptedEscortAI.cpp:216-305) without the player check: the waypoint timer, then <see cref="UpdateEscortAI"/>.</summary>
    public sealed override void OnUpdate(uint diffMs)
    {
        if (HasEscortState(EscortState.Escorting) && !Me.Combat.IsInCombat && _waypointWaitMs != 0 && !HasEscortState(EscortState.Returning))
        {
            if (_waypointWaitMs <= diffMs)
            {
                if (CurrentWaypointIndex >= _waypoints.Count)
                {
                    EndOfPath();
                    return;
                }

                if (!HasEscortState(EscortState.Paused))
                {
                    CreatureWaypoint point = _waypoints[CurrentWaypointIndex];
                    Me.Motion.MovePoint(point.Point, point.X, point.Y, point.Z, _running);
                    WaypointStart(point.Point);
                    _waypointWaitMs = 0;
                }
            }
            else
            {
                _waypointWaitMs -= diffMs;
            }
        }

        UpdateEscortAI(diffMs);
    }

    /// <summary>"End of the line": loop home, or disappear (and come back at once when asked).</summary>
    private void EndOfPath()
    {
        if (_canReturnToStart)
        {
            Vector3 home = new(Me.Home.X, Me.Home.Y, Me.Home.Z);
            Me.Motion.MovePoint(PointHome, home.X, home.Y, home.Z, run: _running);
            _waypointWaitMs = 0;
            return;
        }

        Me.NpcFlags |= (uint)Npc.NpcFlags.QuestGiver;
        if (System is { } system)
        {
            system.ForcedDespawn(Me, 0);
            if (_canInstantRespawn)
            {
                system.ForceRespawn(Me);
            }
        }
    }

    /// <summary>vmangos npc_escortAI::MovementInform (ScriptedEscortAI.cpp:333-392).</summary>
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || !HasEscortState(EscortState.Escorting))
        {
            return;
        }

        if (pointId == PointLastPoint)
        {
            System?.SetScriptRun(Me, _running);
            RemoveEscortState(EscortState.Returning);
            if (_waypointWaitMs == 0)
            {
                _waypointWaitMs = 1;
            }

            return;
        }

        if (pointId == PointHome)
        {
            if (HasEscortState(EscortState.Returning))
            {
                return;
            }

            CurrentWaypointIndex = 0;
            _waypointWaitMs = 1;
            return;
        }

        if (HasEscortState(EscortState.Returning) || CurrentWaypointIndex >= _waypoints.Count)
        {
            return;
        }

        CreatureWaypoint point = _waypoints[CurrentWaypointIndex];
        if (Vector3.Distance(new Vector3(Me.X, Me.Y, Me.Z), new Vector3(point.X, point.Y, point.Z)) > 10f)
        {
            _waypointWaitMs = 1; // the move was cut short: walk to it again
            return;
        }

        if (point.Point != pointId)
        {
            return; // "Waypoint out of order"
        }

        WaypointReached(point.Point);
        _waypointWaitMs = point.WaitTimeMs + 1;
        CurrentWaypointIndex++;
    }
}
