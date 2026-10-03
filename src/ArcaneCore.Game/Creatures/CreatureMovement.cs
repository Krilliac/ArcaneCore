using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>What a movement generator may ask of its owner (implemented by <see cref="CreatureMapSystem"/>).</summary>
internal interface ICreatureMover
{
    /// <summary>Launch a straight spline to (x, y, z) and tell the observers (SMSG_MONSTER_MOVE).</summary>
    void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation);

    /// <summary>A uniform random number in [0, 1).</summary>
    double NextDouble();

    /// <summary>A uniform random integer in [min, max] (vmangos urand).</summary>
    int URand(int min, int max);

    /// <summary>Ground height near (x, y, z), or null when unknown.</summary>
    float? GetHeight(uint mapId, float x, float y, float z);
}

/// <summary>
/// A creature's idle-time movement (vmangos MovementGenerator subset). The owner calls
/// <see cref="Update"/> every map tick while the creature is alive; splines themselves are
/// advanced by the owner before the call.
/// </summary>
internal interface ICreatureMovementGenerator
{
    CreatureMovementType Type { get; }

    /// <summary>(Re)start, at spawn and at respawn (vmangos Initialize/Reset).</summary>
    void Reset(Creature creature, ICreatureMover mover);

    void Update(Creature creature, ICreatureMover mover, uint diffMs);
}

/// <summary>vmangos IdleMovementGenerator: stays put.</summary>
internal sealed class IdleMovementGenerator : ICreatureMovementGenerator
{
    public static readonly IdleMovementGenerator Instance = new();

    public CreatureMovementType Type => CreatureMovementType.Idle;

    public void Reset(Creature creature, ICreatureMover mover)
    {
    }

    public void Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
    }
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

    public CreatureMovementType Type => CreatureMovementType.Random;

    public int NextMoveMs => _nextMoveMs;

    public void Reset(Creature creature, ICreatureMover mover)
    {
        _nextMoveMs = 1000;
        _wanderSteps = 0;
    }

    public void Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        // vmangos UpdateAsync: the timer only runs while no spline is active.
        if (creature.IsMoving)
        {
            return;
        }

        _nextMoveMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_nextMoveMs > 0)
        {
            return;
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
    }
}

/// <summary>
/// vmangos WaypointMovementGenerator&lt;Creature&gt; for a spawn's <c>creature_movement</c> path:
/// it starts at the first point at once, walks node to node in point order, waits each node's
/// <c>waittime</c> on arrival (facing the node orientation when it is not 100 and the node has a
/// delay), and loops back to the first node after the last. Scripts and wander-at-node are not
/// driven (no script engine yet).
/// </summary>
internal sealed class WaypointMovementGenerator(IReadOnlyList<CreatureWaypoint> path) : ICreatureMovementGenerator
{
    /// <summary>vmangos: an orientation of 100 means "keep the travel direction".</summary>
    public const float NoOrientation = 100f;

    private int _current;
    private bool _arrivalDone;
    private int _waitMs;

    public CreatureMovementType Type => CreatureMovementType.Waypoint;

    public int CurrentIndex => _current;

    public void Reset(Creature creature, ICreatureMover mover)
    {
        _current = 0;
        _arrivalDone = false;
        _waitMs = 0;
        StartMove(creature, mover);
    }

    public void Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (path.Count == 0)
        {
            return;
        }

        if (_waitMs > 0)
        {
            _waitMs -= (int)Math.Min(diffMs, int.MaxValue);
            if (_waitMs <= 0)
            {
                _waitMs = 0;
                StartMove(creature, mover);
            }

            return;
        }

        if (!creature.IsMoving)
        {
            if (OnArrived())
            {
                StartMove(creature, mover);
            }
        }
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
        bool run = (creature.Template.ExtraFlags & Creature.ExtraFlagAlwaysRun) != 0;
        mover.MoveTo(creature, node.X, node.Y, node.Z, run, facing);
    }
}
