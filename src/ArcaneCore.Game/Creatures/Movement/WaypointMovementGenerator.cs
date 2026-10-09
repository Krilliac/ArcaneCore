using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>What stops a creature's default movement generator from starting new legs (vmangos UNIT_STATE_CAN_NOT_MOVE).</summary>
internal static class CreatureMovementGates
{
    /// <summary>Stunned, confused, fleeing or rooted.</summary>
    public static bool CannotMove(Creature creature)
        => (creature.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0 || creature.Movement.HasFlag(MovementFlags.Root);
}

/// <summary>
/// vmangos / mangos-classic WaypointMovementGenerator&lt;Creature&gt; for a creature waypoint path (the creature_movement rows of its
/// spawn, or the creature_movement_template path of its entry): it starts at the first node at once, walks node to node in point-id
/// order, waits the waittime of each node on arrival (facing the node orientation when it is not 100 and the node has a delay), and
/// loops back to the first node after the last (Movement/WaypointMovementGenerator.cpp:184-242).
/// <list type="bullet">
/// <item>Legs walk, unless the template runs always (:240) or <see cref="CreatureMovementOptions.HonorWaypointRunColumn"/> is on and the Run column of the node is set.</item>
/// <item>The route to a node goes through the pathfinder of the map (:235 MOVE_PATHFINDING; a straight line without navmeshes).</item>
/// <item>Arriving at a node records it as the last reached one and tells the AI (MovementInform(WAYPOINT, node), :158-160) before the delay starts.</item>
/// <item>The last reached node is where evade sends the creature (<see cref="GetResetPosition"/>, :292-303).</item>
/// <item>A creature that cannot move starts no leg and its timers stand still; a casting one stops, and when the cast ends it sets off for the same node again (:249-272).</item>
/// <item>Interrupted by combat it keeps its node; resumed it heads for the same node again (Reset, StartMoveNow, :108-126).</item>
/// </list>
/// Node ids are the ids of the data (1-based, possibly with gaps); vmangos stores them minus one and treats the first node as "none
/// reached" (<c>!m_lastReachedWaypoint</c>), which cmangos does not. The data ArcaneCore loads is cmangos-shaped, so a node is reached
/// when it is reached.
/// <para>
/// Limits (docs/areas/creature-movement-spawns.md): node scripts, wander at a node, sub-paths (<c>path_id</c>), non-repeating paths,
/// interaction pauses are not driven. A spawn group formation's leader walks its formation's path through this generator: a
/// LINEAR_WP path (cmangos LinearWPMovementGenerator, MotionGenerators/WaypointMovementGenerator.cpp:617-647) turns back at either end
/// instead of looping, and a new leader resumes at the node after the last one the old leader reached (FormationData::SetMasterMovement,
/// <c>SetNextWaypoint(m_lastWP + 1)</c>).
/// </para>
/// </summary>
internal sealed class WaypointMovementGenerator(IReadOnlyList<CreatureWaypoint> path, bool linear = false, int startIndex = 0) : ICreatureMovementGenerator
{
    private bool _backwards;

    /// <summary>vmangos: an orientation of 100 means "keep the travel direction".</summary>
    public const float NoOrientation = 100f;

    private int _current;
    private bool _arrivalDone;
    private int _waitMs;
    private uint _lastReachedPoint;

    public MovementGeneratorType Type => MovementGeneratorType.Waypoint;

    public int CurrentIndex => _current;

    public int WaitMs => _waitMs;

    /// <summary>The point id of the last node reached since the generator started, 0 when none.</summary>
    public uint LastReachedPoint => _lastReachedPoint;

    /// <summary>Whether the path turns back at its ends (cmangos LINEAR_WP_MOTION_TYPE) instead of looping.</summary>
    public bool IsLinear => linear;

    /// <summary>The index of the last node reached since the generator started, -1 when none.</summary>
    public int LastReachedIndex { get; private set; } = -1;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _current = path.Count == 0 ? 0 : Math.Clamp(startIndex, 0, path.Count - 1);
        _backwards = false;
        LastReachedIndex = -1;
        _arrivalDone = false;
        _waitMs = 0;
        _lastReachedPoint = 0;
        StartMove(creature, mover);
    }

    public void Resume(Creature creature, ICreatureMover mover)
    {
        if (_waitMs > 0)
        {
            return; // still waiting at the node it reached
        }

        _arrivalDone = false;
        StartMove(creature, mover);
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (path.Count == 0 || CreatureMovementGates.CannotMove(creature))
        {
            return true;
        }

        if (mover.IsCasting(creature) || creature.WaypointsPaused)
        {
            // A cast, or a relay script's PAUSE_WAYPOINTS (cmangos UNIT_STAT_WAYPOINT_PAUSED, MotionMaster::PauseWaypoints): stop and set off
            // for the same node again afterwards.
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
                _waitMs = 1;          // i_nextMoveTime.Reset(1)
                _arrivalDone = false; // the same leg again once the cast is over
            }

            return true;
        }

        if (_waitMs > 0)
        {
            _waitMs -= (int)Math.Min(diffMs, int.MaxValue);
            if (_waitMs <= 0)
            {
                _waitMs = 0;
                StartMove(creature, mover);
            }

            return true;
        }

        if (!creature.IsMoving)
        {
            if (OnArrived(creature, mover))
            {
                StartMove(creature, mover);
            }
        }

        return true;
    }

    /// <summary>vmangos WaypointMovementGenerator::GetResetPosition: the last reached node, none (the spawn point) before any.</summary>
    public CreatureHome? GetResetPosition(Creature creature)
    {
        if (_lastReachedPoint == 0)
        {
            return null;
        }

        foreach (CreatureWaypoint node in path)
        {
            if (node.Point == _lastReachedPoint)
            {
                return new CreatureHome(node.X, node.Y, node.Z, float.NaN); // no facing: the creature faces its travel direction
            }
        }

        return null;
    }

    private bool OnArrived(Creature creature, ICreatureMover mover)
    {
        if (_arrivalDone)
        {
            return true;
        }

        _arrivalDone = true;
        CreatureWaypoint node = path[_current];
        _lastReachedPoint = node.Point;
        LastReachedIndex = _current;
        mover.OnMovementFinished(creature, MovementGeneratorType.Waypoint, node.Point);
        if (!creature.IsAlive)
        {
            return false;
        }

        if (node.WaitTimeMs > 0)
        {
            _waitMs = (int)Math.Min(node.WaitTimeMs, int.MaxValue);
            return false;
        }

        return true;
    }

    /// <summary>cmangos LinearWPMovementGenerator::SwitchToNextNode: to the end, then back to the start, and again.</summary>
    private int NextLinear()
    {
        if (path.Count < 2)
        {
            return 0;
        }

        if (!_backwards && _current + 1 >= path.Count)
        {
            _backwards = true;
        }
        else if (_backwards && _current == 0)
        {
            _backwards = false;
        }

        return _backwards ? _current - 1 : _current + 1;
    }

    private void StartMove(Creature creature, ICreatureMover mover)
    {
        if (path.Count == 0 || _waitMs > 0)
        {
            return;
        }

        if (_arrivalDone)
        {
            _current = linear ? NextLinear() : (_current + 1) % path.Count;
        }

        _arrivalDone = false;
        CreatureWaypoint node = path[_current];
        SplineFacing facing = node.Orientation != NoOrientation && node.WaitTimeMs != 0 ? SplineFacing.ToAngle(node.Orientation) : SplineFacing.None;
        bool run = (creature.Template.Behaviour & CreatureBehaviourFlags.AlwaysRun) != 0
            || (node.Run && mover.MovementOptions.HonorWaypointRunColumn);
        mover.MovePath(creature, mover.FindPath(creature, new Vector3(node.X, node.Y, node.Z)), run, facing);
    }
}
