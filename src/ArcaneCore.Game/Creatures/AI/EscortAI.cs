using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The scripted escort (vmangos npc_escortAI, AI/ScriptedEscortAI.cpp; ScriptDev2): a creature that walks its entry's script path point by
/// point once <see cref="Start"/>ed, pauses on request, breaks off to fight, then runs back to where the fight began and goes on, and at the
/// end of the path either loops home or disappears (and respawns at once when asked). Subclasses react to <see cref="WaypointReached"/>.
/// <para>
/// The path comes from one of two origins, as in mangos-classic npc_escortAI::Start (AI/ScriptDevAI/base/escort_ai.cpp:253-261). Without a
/// path id it is the entry's ScriptDev2 <c>script_waypoint</c> path (PATH_FROM_EXTERNAL; mangos-classic SystemMgr::LoadScriptWaypoints,
/// ScriptDevAI/system/system.cpp:63-121): the world-42 <c>script_waypoint</c> table when it has rows for the entry, else the copy imported
/// into <c>creature_movement_template</c> under <see cref="ScriptWaypointPathBit"/>, else the entry's own path <see cref="EscortPathId"/>
/// (scripts and tests that load their points there). With a path id it is that cmangos <c>waypoint_path</c> path
/// (PATH_FROM_WAYPOINT_PATH, keyed by path id alone, <see cref="CreatureContent.GetWaypointPath"/>) and nothing else. An escort without
/// points cannot start ("EscortAI attempt to start escorting ... but has no waypoints loaded").
/// </para>
/// <para>
/// A player-linked escort (<see cref="Start"/> with a player and quest) checks every second that the player or a member of its group is
/// within <see cref="MaxPlayerDistance"/>, and fails the quest for the group at death or when they are gone (vmangos npc_escortAI::JustDied,
/// IsPlayerOrGroupInRange, UpdateAI and ResetEscort, ScriptedEscortAI.cpp:133-143, 180-300). Not ported: the assist of the player in combat
/// and the aggro of passive escorts by nearby enemies.
/// </para>
/// </summary>
public abstract class EscortAI : CreatureAI
{
    /// <summary>The point id of the run back to the combat start position (POINT_LAST_POINT, ScriptedEscortAI.cpp:23-27).</summary>
    public const uint PointLastPoint = 0xFFFFFF;

    /// <summary>The point id of the run home of a looping escort (POINT_HOME).</summary>
    public const uint PointHome = 0xFFFFFE;

    /// <summary>The default <c>script_waypoint.PathId</c> for an escort, and the legacy fallback path for tests and manually loaded escorts.</summary>
    public const uint EscortPathId = 0;

    /// <summary>ScriptDev2 script_waypoint paths imported into creature_movement_template with a separate namespace.</summary>
    public const uint ScriptWaypointPathBit = CreatureContent.ScriptWaypointPathBit;

    /// <summary>The default delay before the first waypoint (m_uiDelayBeforeTheFirstWaypoint, 2.5 s).</summary>
    public const uint DefaultDelayBeforeFirstWaypointMs = 2500;

    /// <summary>vmangos ScriptedEscortAI.cpp DEFAULT_MAX_PLAYER_DISTANCE.</summary>
    public const float DefaultMaxPlayerDistance = 100f;

    private readonly List<CreatureWaypoint> _waypoints = [];
    private uint _waypointWaitMs;
    private bool _running;
    private bool _canInstantRespawn;
    private bool _canReturnToStart;
    private bool _respawnedOnce;
    private float _combatStartO;
    private ObjectGuid _escortPlayerGuid;
    private uint _escortQuestId;
    private uint _playerCheckMs = 1000;

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

    /// <summary>The maximum distance to the escort player or an online group member; zero disables the check.</summary>
    public float MaxPlayerDistance { get; set; } = DefaultMaxPlayerDistance;

    /// <summary>The escort owner still on this map, or null after logout or transfer.</summary>
    protected Player? GetPlayerForEscort() => _escortPlayerGuid.IsEmpty ? null : System?.Map.FindPlayer(_escortPlayerGuid);

    /// <summary>Where the last fight began (SetCombatStartPosition): where the escort runs back to and goes on from.</summary>
    public Vector3 CombatStartPosition { get; protected set; }

    /// <summary>vmangos m_uiDelayBeforeTheFirstWaypoint.</summary>
    protected uint DelayBeforeFirstWaypointMs { get; set; } = DefaultDelayBeforeFirstWaypointMs;

    /// <inheritdoc />
    public override bool AggroesOnSight => !Me.Template.Civilian;

    /// <summary>
    /// mangos-classic escort_ai (the waypoint-movement escort) does not end the path while the escort is paused at its last point; vmangos
    /// does. A script that pauses at its last point for a closing scene (Squire Rowe, Ranshalla) holds there until it unpauses.
    /// </summary>
    protected virtual bool HoldAtEndWhilePaused => false;

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
    /// reset of the timer (the constructor or a respawn), as in vmangos. A non-zero <paramref name="waypointPath"/> is a cmangos
    /// <c>waypoint_path</c> id (mangos-classic escort_ai.cpp:253-261, origin PATH_FROM_WAYPOINT_PATH); 0 is the entry's script path.
    /// </summary>
    public bool Start(bool run = false, bool instantRespawn = false, bool canLoopPath = false, Player? player = null, uint questId = 0, uint waypointPath = 0)
    {
        if (Me.Combat.IsInCombat || HasEscortState(EscortState.Escorting))
        {
            return false;
        }

        _waypoints.Clear();
        if (System is { } system)
        {
            if (waypointPath != 0)
            {
                _waypoints.AddRange(system.Content.GetWaypointPath(waypointPath));
            }
            else
            {
                _waypoints.AddRange(system.Content.GetScriptWaypoints(Me.Template.Entry, EscortPathId));
            }
        }

        if (_waypoints.Count == 0)
        {
            System?.ReportEscortWithoutPath(Me, waypointPath != 0
                ? $"waypoint_path {waypointPath}"
                : $"script_waypoint path 0 or creature_movement_template path {EscortPathId}");
            return false;
        }

        _running = run;
        _escortPlayerGuid = player?.Guid ?? default;
        _escortQuestId = questId;
        _playerCheckMs = 1000;
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
    public void Stop()
    {
        RemoveEscortState(EscortState.Escorting | EscortState.Paused);
        _escortPlayerGuid = default;
        _escortQuestId = 0;
    }

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
        _escortPlayerGuid = default;
        _escortQuestId = 0;
        _playerCheckMs = 1000;
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

    /// <summary>
    /// vmangos npc_escortAI::JustDied (ScriptedEscortAI.cpp:133-143): a player-linked escort fails its quest for the player's group
    /// (GroupEventFailHappens) and lets go of the player. The escort state stays until the respawn (<see cref="JustRespawned"/>), as there.
    /// </summary>
    public override void OnDeath(Unit? killer)
    {
        if (!HasEscortState(EscortState.Escorting) || _escortPlayerGuid.IsEmpty || _escortQuestId == 0)
        {
            return;
        }

        if (GetPlayerForEscort() is { } player)
        {
            System?.FailEscortQuest(player, _escortQuestId);
        }

        _escortPlayerGuid = default;
        _escortQuestId = 0;
    }

    /// <summary>
    /// An escort's evade raises the evade events although it does not run home. mangos-classic npc_escortAI has no EnterEvadeMode of its
    /// own (AI/ScriptDevAI/base/escort_ai.h:20). It takes CreatureAI::EnterEvadeMode (AI/BaseAI/CreatureAI.cpp:66-71), then
    /// UnitAI::EnterEvadeMode, which ends in Unit::TriggerEvadeEvents (AI/BaseAI/UnitAI.cpp:129). That raises LINKING_EVENT_EVADE and
    /// CREATURE_GROUP_EVENT_EVADE (Entities/Unit.cpp:591-595). This includes a subclass that rejoins a formation instead, such as the AV
    /// riders and soldiers: vmangos AV_NpcEventTroopsAI is a plain npc_escortAI (scripts/battlegrounds/battleground_alterac.cpp:1335).
    /// </summary>
    protected internal sealed override bool TakeOverEvadeRaisesEvadeEvents => true;

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

    /// <summary>vmangos npc_escortAI::UpdateAI (ScriptedEscortAI.cpp:204-288): waypoint timer, player/group range, then <see cref="UpdateEscortAI"/>.</summary>
    public sealed override void OnUpdate(uint diffMs)
    {
        if (HasEscortState(EscortState.Escorting) && !Me.Combat.IsInCombat && _waypointWaitMs != 0 && !HasEscortState(EscortState.Returning))
        {
            if (_waypointWaitMs <= diffMs)
            {
                if (CurrentWaypointIndex >= _waypoints.Count)
                {
                    if (!HoldAtEndWhilePaused || !HasEscortState(EscortState.Paused))
                    {
                        EndOfPath();
                        return;
                    }
                }
                else if (!HasEscortState(EscortState.Paused))
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

        if (HasEscortState(EscortState.Escorting) && !_escortPlayerGuid.IsEmpty && MaxPlayerDistance > 0
            && !Me.Combat.IsInCombat && !HasEscortState(EscortState.Returning))
        {
            if (_playerCheckMs < diffMs)
            {
                if (!IsPlayerOrGroupInRange())
                {
                    // "EscortAI failed because player/group was to far away or not found": JustDied, then ResetEscort.
                    OnDeath(null);
                    ResetEscort();
                    return;
                }

                _playerCheckMs = 1000;
            }
            else
            {
                _playerCheckMs -= diffMs;
            }
        }

        UpdateEscortAI(diffMs);
    }

    /// <summary>
    /// vmangos npc_escortAI::IsPlayerOrGroupInRange (ScriptedEscortAI.cpp:180-201): any member of the player's group, else the player, within
    /// <see cref="MaxPlayerDistance"/> on this map (IsWithinDistInMap: 3D, bounding radii added).
    /// </summary>
    private bool IsPlayerOrGroupInRange()
    {
        Player? leader = GetPlayerForEscort();
        if (leader is null)
        {
            return false;
        }

        IReadOnlyList<Player> members = System?.EscortGroupMembers(leader) ?? [];
        IEnumerable<Player> candidates = members.Count > 0 ? members : [leader];
        foreach (Player member in candidates)
        {
            if (!ReferenceEquals(member.Map, Me.Map))
            {
                continue;
            }

            float dx = Me.X - member.X;
            float dy = Me.Y - member.Y;
            float dz = Me.Z - member.Z;
            float reach = MaxPlayerDistance + Me.BoundingRadius + member.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= reach * reach)
            {
                return true;
            }
        }

        return false;
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

        ResetEscort();
    }

    /// <summary>vmangos npc_escortAI::ResetEscort (ScriptedEscortAI.cpp:292-300): quest giver again, disappear, and come back at once when asked.</summary>
    private void ResetEscort()
    {
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

        if (point.ScriptId != 0)
        {
            // mangos-classic WaypointMovementGenerator<Creature>::OnArrived: source is the escort, target is its linked player or itself.
            System?.StartDbScript(DbScriptKind.CreatureMovement, point.ScriptId, Me, GetPlayerForEscort() ?? (WorldObject)Me);
        }
        if (!Me.IsInWorld || !Me.IsAlive) return;
        WaypointReached(point.Point);
        _waypointWaitMs = point.WaitTimeMs + 1;
        CurrentWaypointIndex++;
    }
}
