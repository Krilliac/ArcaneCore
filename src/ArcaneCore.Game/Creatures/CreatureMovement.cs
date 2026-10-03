using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>vmangos MovementGeneratorType values (MotionMaster.h) for the generators ArcaneCore runs.</summary>
public enum MovementGeneratorType : byte
{
    Idle = 0,
    Random = 1,
    Waypoint = 2,
    Chase = 5,
    Home = 6,
    Point = 8,
    Fleeing = 9,
    Follow = 14,
}

/// <summary>What a movement generator may ask of its owner (implemented by <see cref="CreatureMapSystem"/>).</summary>
internal interface ICreatureMover
{
    /// <summary>Launch a straight spline to (x, y, z) and tell the observers (SMSG_MONSTER_MOVE).</summary>
    void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation);

    /// <summary>Launch a linear spline through <paramref name="path"/> (destination last) and tell the observers.</summary>
    void MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing);

    /// <summary>Stop a moving creature where it is (stop packet).</summary>
    void StopMoving(Creature creature);

    /// <summary>A path from the creature to <paramref name="destination"/> (pathfinder seam; straight line by default).</summary>
    IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination);

    /// <summary>A uniform random number in [0, 1).</summary>
    double NextDouble();

    /// <summary>A uniform random integer in [min, max] (vmangos urand).</summary>
    int URand(int min, int max);

    /// <summary>Ground height near (x, y, z), or null when unknown.</summary>
    float? GetHeight(uint mapId, float x, float y, float z);

    /// <summary>Whether the creature is casting a spell (it stands still while it does).</summary>
    bool IsCasting(Creature creature);

    /// <summary>A generator that ends by itself finished (home reached, point reached).</summary>
    void OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId);
}

/// <summary>
/// One entry of a creature's <see cref="MotionMaster"/> stack (vmangos MovementGenerator). The
/// owner advances the spline, then calls <see cref="Update"/> on the top generator every map
/// tick while the creature is alive.
/// </summary>
internal interface ICreatureMovementGenerator
{
    MovementGeneratorType Type { get; }

    /// <summary>Start (vmangos Initialize): at spawn, at respawn, or when pushed.</summary>
    void Initialize(Creature creature, ICreatureMover mover);

    /// <summary>Another generator was pushed on top (vmangos Interrupt).</summary>
    void Interrupt(Creature creature, ICreatureMover mover)
    {
    }

    /// <summary>The generator above was removed and this one runs again (vmangos Reset).</summary>
    void Resume(Creature creature, ICreatureMover mover) => Initialize(creature, mover);

    /// <summary>Removed from the stack (vmangos Finalize); <paramref name="completed"/> when its own update ended it.</summary>
    void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
    }

    /// <summary>One tick; false when the generator is done and should be removed.</summary>
    bool Update(Creature creature, ICreatureMover mover, uint diffMs);

    /// <summary>
    /// Where evade sends the creature when this generator is the default one beneath the home move,
    /// or null for the built-in rule (vmangos MovementGenerator::GetResetPosition, MovementGenerator.h:64;
    /// random and waypoint movement override it, RandomMovementGenerator.cpp:131 and
    /// WaypointMovementGenerator.cpp:292; HomeMovementGenerator.cpp:52 consumes it).
    /// </summary>
    CreatureHome? GetResetPosition(Creature creature) => null;

    /// <summary>
    /// False once the generator has found its target unreachable (vmangos MovementGenerator::IsReachable,
    /// MovementGenerator.h:61, overridden by TargetedMovementGenerator.h:53; Creature.cpp:1016 uses it for
    /// the unreachable-target evade).
    /// </summary>
    bool IsReachable => true;
}

/// <summary>vmangos IdleMovementGenerator: stays put.</summary>
internal sealed class IdleMovementGenerator : ICreatureMovementGenerator
{
    public static readonly IdleMovementGenerator Instance = new();

    public MovementGeneratorType Type => MovementGeneratorType.Idle;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs) => true;
}

/// <summary>
/// vmangos RandomMovementGenerator for ground creatures: the first move comes 1 s after spawn;
/// then, whenever the creature stands still and the timer has run out, it walks to a random
/// point within the wander distance of its spawn point. It takes <c>i_wanderSteps</c> steps
/// 50 ms apart before a pause of urand(4, 10) s, after which a new step count is drawn from
/// urand(0, wander ≤ 1 ? 2 : 8). It walks unless CREATURE_FLAG_EXTRA_ALWAYS_RUN is set.
/// <para>
/// vmangos picks the point with a navmesh query (<c>Map::GetWalkRandomPosition</c>); without
/// navmeshes the point is uniform over the wander disc at the height provider's ground Z (or the
/// spawn Z). Recorded in docs/areas/creatures.md.
/// </para>
/// </summary>
internal sealed class RandomMovementGenerator : ICreatureMovementGenerator
{
    /// <summary>vmangos RandomMovementGenerator constructor default when the spawn has none.</summary>
    public const float DefaultWanderDistance = 5.0f;

    private int _nextMoveMs;
    private int _wanderSteps;

    public MovementGeneratorType Type => MovementGeneratorType.Random;

    public int NextMoveMs => _nextMoveMs;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _nextMoveMs = 1000;
        _wanderSteps = 0;
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        // vmangos UpdateAsync: the timer only runs while no spline is active.
        if (creature.IsMoving)
        {
            return true;
        }

        _nextMoveMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_nextMoveMs > 0)
        {
            return true;
        }

        float wander = creature.WanderDistance > 0 ? creature.WanderDistance : DefaultWanderDistance;
        CreatureHome home = creature.Home;
        float destX = home.X;
        float destY = home.Y;
        float destZ = home.Z;

        // WorldObject::GetRandomPoint: below 0.1 yd the spawn point itself.
        if (wander >= 0.1f)
        {
            double angle = mover.NextDouble() * 2 * Math.PI;
            double distance = wander * Math.Sqrt(mover.NextDouble());
            destX = home.X + (float)(distance * Math.Cos(angle));
            destY = home.Y + (float)(distance * Math.Sin(angle));
            destZ = mover.GetHeight(creature.MapId, destX, destY, home.Z) ?? home.Z;
        }

        bool run = (creature.Template.ExtraFlags & Creature.ExtraFlagAlwaysRun) != 0;
        mover.MoveTo(creature, destX, destY, destZ, run, finalOrientation: null);

        if (_wanderSteps > 0)
        {
            _wanderSteps--;
            _nextMoveMs = 50;
        }
        else
        {
            // "Retail seems to use rounded numbers so we do as well" (vmangos).
            _nextMoveMs = mover.URand(4, 10) * 1000;
            _wanderSteps = mover.URand(0, wander <= 1.0f ? 2 : 8);
        }

        return true;
    }
}

/// <summary>
/// vmangos WaypointMovementGenerator&lt;Creature&gt; for a spawn's <c>creature_movement</c> path:
/// it starts at the first point at once, walks node to node in point order, waits each node's
/// <c>waittime</c> on arrival (facing the node orientation when it is not 100 and the node has a
/// delay), and loops back to the first node after the last. A node's <c>Run</c> flag (world
/// schema creature-AI step) or CREATURE_FLAG_EXTRA_ALWAYS_RUN makes the leg to it run.
/// Interrupted by combat it keeps its node; resumed it heads for the same node again (vmangos
/// WaypointMovementGenerator::Reset → StartMoveNow). Scripts and wander-at-node are not driven.
/// </summary>
internal sealed class WaypointMovementGenerator(IReadOnlyList<CreatureWaypoint> path) : ICreatureMovementGenerator
{
    /// <summary>vmangos: an orientation of 100 means "keep the travel direction".</summary>
    public const float NoOrientation = 100f;

    private int _current;
    private bool _arrivalDone;
    private int _waitMs;

    public MovementGeneratorType Type => MovementGeneratorType.Waypoint;

    public int CurrentIndex => _current;

    public int WaitMs => _waitMs;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _current = 0;
        _arrivalDone = false;
        _waitMs = 0;
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
        if (path.Count == 0)
        {
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
            if (OnArrived())
            {
                StartMove(creature, mover);
            }
        }

        return true;
    }

    private bool OnArrived()
    {
        if (_arrivalDone)
        {
            return true;
        }

        _arrivalDone = true;
        CreatureWaypoint node = path[_current];
        if (node.WaitTimeMs > 0)
        {
            _waitMs = (int)Math.Min(node.WaitTimeMs, int.MaxValue);
            return false;
        }

        return true;
    }

    private void StartMove(Creature creature, ICreatureMover mover)
    {
        if (path.Count == 0 || _waitMs > 0)
        {
            return;
        }

        if (_arrivalDone)
        {
            _current = (_current + 1) % path.Count;
        }

        _arrivalDone = false;
        CreatureWaypoint node = path[_current];
        float? facing = node.Orientation != NoOrientation && node.WaitTimeMs != 0 ? node.Orientation : null;
        bool run = node.Run || (creature.Template.ExtraFlags & Creature.ExtraFlagAlwaysRun) != 0;
        mover.MoveTo(creature, node.X, node.Y, node.Z, run, facing);
    }
}
