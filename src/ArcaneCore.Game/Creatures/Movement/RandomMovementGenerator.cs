using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos RandomMovementGenerator for ground creatures (Movement/RandomMovementGenerator.cpp): the first move comes 1 s after
/// spawn; then, whenever the creature stands still and the timer has run out, it goes to a random point within the wander distance of
/// its spawn point. It takes <c>i_wanderSteps</c> steps 50 ms apart before a pause of urand(4, 10) s, after which a new step count is
/// drawn from urand(0, wander ≤ 1 ? 2 : 8) (:56-66). Legs walk unless the template has ALWAYS_RUN (:53); a creature imported from a
/// cmangos-dialect row with RUN_DURING_WANDER runs a configurable share of its legs (cmangos RandomMovementGenerator.cpp:135-136).
/// <para>
/// UpdateAsync gates (:113-128): a creature that cannot move (stunned, rooted, confused, fleeing) zeroes its timer and starts no
/// leg; one that is casting a spell stops where it is and its timer does not run.
/// </para>
/// <para>
/// <see cref="GetResetPosition"/> (:131-144) is where evade sends the creature: where it stands when that is within the wander
/// distance of the spawn point, else the spawn point.
/// </para>
/// <para>
/// Point choice (docs/areas/creature-movement-spawns.md): like vmangos (<c>Creature::GetRandomPoint</c>, <c>MOVE_PATHFINDING | MOVE_EXCLUDE_STEEP_SLOPES</c>)
/// the point is a navmesh point within the wander distance (<see cref="ICreatureMover.TryGetRandomMeshPoint"/>) reached by a mesh path; where no
/// mesh is loaded it is uniform over the wander disc at the height provider's ground Z (or the spawn Z), walked straight (the pathfinder's
/// answer for a map or tile without mesh data). A leg with no path (the point is off the mesh) is skipped and tried again 1-1.5 s later; a leg
/// the path budget cannot serve waits for the next update. Flying creatures (<c>CanFly</c>, :28-44) keep the ground wander.
/// </para>
/// </summary>
internal sealed class RandomMovementGenerator : ICreatureMovementGenerator
{
    private readonly float? _wanderDistance;
    private readonly CreatureHome? _center;
    private readonly bool? _run;
    private readonly uint _expiryMs;
    private long _remainingMs;

    /// <summary>The spawn's wander, around its spawn point, walking by its template flags (vmangos InitializeMovement for MovementType 1).</summary>
    public RandomMovementGenerator()
    {
    }

    /// <summary>
    /// cmangos MotionMaster::MoveRandomAroundPoint (a relay's MOVEMENT command, ScriptMgr.cpp:2338-2356): wander within
    /// <paramref name="wanderDistance"/> of <paramref name="center"/> (the spawn point when null), walking or running as
    /// <paramref name="run"/> says (by the template flags when null). A non-zero <paramref name="expiryMs"/> (the relay's <c>datalong3</c>)
    /// ends the wander after that long: <see cref="Update"/> returns false and the generator beneath resumes (cmangos
    /// RandomMovementGenerator's duration timer).
    /// </summary>
    public RandomMovementGenerator(float? wanderDistance, CreatureHome? center, bool? run, uint expiryMs = 0)
    {
        _wanderDistance = wanderDistance;
        _center = center;
        _run = run;
        _expiryMs = expiryMs;
        _remainingMs = expiryMs;
    }

    /// <summary>How long a wander whose point has no path waits before it picks another (1-1.5 s, the wait of the flee and fear generators).</summary>
    public const int NoPathRetryMinMs = FleeingMovementGenerator.NoPathRetryMinMs;

    public const int NoPathRetryMaxMs = FleeingMovementGenerator.NoPathRetryMaxMs;

    /// <summary>vmangos RandomMovementGenerator constructor default when the spawn has none.</summary>
    public const float DefaultWanderDistance = 5.0f;

    /// <summary>The most doublings of the no-path wait (1-1.5 s, 2-3 s, ... up to 16-24 s).</summary>
    public const int MaxNoPathBackoffShift = 4;

    private int _nextMoveMs;
    private int _wanderSteps;
    private int _consecutiveNoPath;

    public MovementGeneratorType Type => MovementGeneratorType.Random;

    public int NextMoveMs => _nextMoveMs;

    /// <summary>Legs in a row that had no path (the back-off of <see cref="NoPathRetryDelay"/>; reset by a leg that moves).</summary>
    public int ConsecutiveNoPath => _consecutiveNoPath;

    /// <summary>
    /// The wait after the <paramref name="failures"/>-th leg in a row without a path: the 1-1.5 s retry doubled for each failure after the
    /// first, at most <see cref="MaxNoPathBackoffShift"/> times. Exactly one value is drawn from the mover per call.
    /// </summary>
    internal static int NoPathRetryDelay(ICreatureMover mover, int failures)
        => mover.URand(NoPathRetryMinMs, NoPathRetryMaxMs) << Math.Clamp(failures - 1, 0, MaxNoPathBackoffShift);

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _nextMoveMs = 1000;
        _wanderSteps = 0;
        _consecutiveNoPath = 0;
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (_expiryMs != 0)
        {
            // The duration runs from the push, whatever the creature does meanwhile; when it is over the wander ends.
            _remainingMs -= diffMs;
            if (_remainingMs <= 0)
            {
                return false;
            }
        }

        if (CreatureMovementGates.CannotMove(creature))
        {
            _nextMoveMs = 0; // i_nextMoveTime.Reset(0): the first free tick moves
            return true;
        }

        if (mover.IsCasting(creature))
        {
            mover.StopMoving(creature);
            return true;
        }

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

        if (mover is ICreaturePathQuery { HasPathBudget: false } budgeted)
        {
            budgeted.NoteDeferred(creature);
            _nextMoveMs = budgeted.IsCrowdedRandomPass ? mover.URand(4, 10) * 1000 : mover.URand(NoPathRetryMinMs, NoPathRetryMaxMs);
            return true;
        }

        float wander = WanderOf(creature);
        CreatureHome home = _center ?? creature.Home;
        float destX = home.X;
        float destY = home.Y;
        float destZ = home.Z;

        // vmangos Creature::GetRandomPoint with MOVE_PATHFINDING: a point on the navmesh within the wander distance, reachable from the
        // spawn point, then walked to along the mesh path. Without mesh data for the spot (no navmesh for the map, tile not loaded) the
        // terrain-height point below stays, drawn exactly as before.
        bool onMesh = false;
        if (wander >= 0.1f && mover.TryGetRandomMeshPoint(creature, new Vector3(home.X, home.Y, home.Z), wander, out Vector3 meshPoint))
        {
            onMesh = true;
            destX = meshPoint.X;
            destY = meshPoint.Y;
            destZ = meshPoint.Z;
        }

        // WorldObject::GetRandomPoint: below 0.1 yd the spawn point itself.
        if (!onMesh && wander >= 0.1f)
        {
            double angle = mover.NextDouble() * 2 * Math.PI;
            double distance = wander * Math.Sqrt(mover.NextDouble());
            destX = home.X + (float)(distance * Math.Cos(angle));
            destY = home.Y + (float)(distance * Math.Sin(angle));
            destZ = mover.GetHeight(creature.MapId, destX, destY, home.Z) ?? home.Z;
        }

        bool run = _run ?? ShouldRun(creature, mover);

        // The leg goes through the pathfinder whether or not the point came from the mesh: a terrain-height point on a map that has a mesh
        // but nothing at that spot (a house, a wall) has no path, and a wandering creature never walks a straight line through geometry.
        PlannedPath plan = mover.PlanPath(creature, new Vector3(destX, destY, destZ));
        if (plan.Outcome == PathOutcome.NoPath)
        {
            // Skip this leg and pick again later (vmangos' mesh point is always reachable, so it never meets this). A spawn that keeps
            // drawing points it cannot reach (off the mesh, a closed room) backs off: each search costs the update, the wait does not.
            _nextMoveMs = NoPathRetryDelay(mover, ++_consecutiveNoPath);
            return true;
        }

        if (plan.Outcome == PathOutcome.Deferred)
        {
            _nextMoveMs = mover is ICreaturePathQuery { IsCrowdedRandomPass: true } ? mover.URand(4, 10) * 1000 : mover.URand(NoPathRetryMinMs, NoPathRetryMaxMs);
            return true;
        }

        _consecutiveNoPath = 0;
        mover.MovePath(creature, plan.Corners, run, SplineFacing.None);

        if (_wanderSteps > 0 && mover is not ICreaturePathQuery { IsCrowdedRandomPass: true })
        {
            _wanderSteps--;
            _nextMoveMs = 50;
        }
        else
        {
            // "Retail seems to use rounded numbers so we do as well" (vmangos).
            _nextMoveMs = mover.URand(4, 10) * 1000;
            _wanderSteps = mover is ICreaturePathQuery { IsCrowdedRandomPass: true } ? 0 : mover.URand(0, wander <= 1.0f ? 2 : 8);
        }

        return true;
    }

    /// <summary>vmangos RandomMovementGenerator::GetResetPosition: the current position when within the wander distance of the spawn point, else the spawn point.</summary>
    public CreatureHome? GetResetPosition(Creature creature)
    {
        CreatureHome start = _center ?? creature.Home;
        float dx = creature.X - start.X;
        float dy = creature.Y - start.Y;
        float wander = WanderOf(creature);
        return (dx * dx) + (dy * dy) <= wander * wander
            ? new CreatureHome(creature.X, creature.Y, creature.Z, float.NaN) // no facing: GetPosition only (RandomMovementGenerator.cpp:134-135)
            : start;
    }

    private float WanderOf(Creature creature) => _wanderDistance ?? (creature.WanderDistance > 0 ? creature.WanderDistance : DefaultWanderDistance);

    private static bool ShouldRun(Creature creature, ICreatureMover mover)
    {
        CreatureBehaviourFlags behaviour = creature.Template.Behaviour;
        if ((behaviour & CreatureBehaviourFlags.RunDuringWander) != 0)
        {
            return mover.URand(0, 99) < mover.MovementOptions.RunDuringWanderChancePercent;
        }

        return (behaviour & CreatureBehaviourFlags.AlwaysRun) != 0;
    }
}
