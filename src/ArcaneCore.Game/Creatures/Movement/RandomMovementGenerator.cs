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
/// Limits (docs/areas/creature-movement-spawns.md): vmangos picks the point with a navmesh query
/// (<c>Creature::GetRandomPoint</c>, <c>MOVE_PATHFINDING | MOVE_EXCLUDE_STEEP_SLOPES</c>); without navmeshes the point is uniform over the
/// wander disc at the height provider's ground Z (or the spawn Z). Flying creatures (<c>CanFly</c>, :28-44) keep the ground wander.
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

        float wander = WanderOf(creature);
        CreatureHome home = _center ?? creature.Home;
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

        mover.MoveTo(creature, destX, destY, destZ, _run ?? ShouldRun(creature, mover), finalOrientation: null);

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
