using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A mover that can also say whether a destination can be reached (vmangos <c>PathFinder::getPathType</c> read by
/// <c>TargetedMovementGenerator::_setTargetLocation</c> into <c>m_bReachable</c>). <see cref="CreatureMapSystem"/> implements it;
/// a mover without it leaves every target reachable. Kept apart from <see cref="ICreatureMover"/> so the movement seam's other
/// implementations need no change.
/// </summary>
internal interface ICreaturePathQuery
{
    /// <summary>
    /// The path to <paramref name="destination"/> as <see cref="ICreatureMover.FindPath"/> gives it; <paramref name="reachable"/> is false
    /// when the pathfinder found no path (<see cref="Maps.Collision.PathType.NoPath"/>, the creature then goes straight) or only a partial
    /// one (<see cref="Maps.Collision.PathType.Incomplete"/>). Without navigation data everything is reachable.
    /// </summary>
    IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination, out bool reachable);
}

/// <summary>
/// vmangos TargetedMovementGenerator: keeps a creature at a target. <see cref="ChaseMovementGenerator"/>
/// runs into melee reach; <see cref="FollowMovementGenerator"/> holds a distance and angle. Every
/// <see cref="RecheckMs"/> (and whenever the creature stands still) the target is re-measured;
/// a new spline goes out only when the creature is out of place and the target has moved more
/// than <see cref="TargetMoveTolerance"/> since the last spline was aimed.
/// <para>
/// Reachability (vmangos <c>m_bReachable</c>, TargetedMovementGenerator.h:53): the last path query's verdict from
/// <see cref="ICreaturePathQuery"/>. While the target is unreachable and the creature stands at the end of its partial path it
/// re-paths every <see cref="RecheckMs"/> (the re-path) and <see cref="UnreachableMs"/> counts the time; the host reads it for the
/// unreachable-target evade (vmangos Creature::Update, Creature.cpp:1017-1040) and EventAI reads <see cref="IsReachable"/> for
/// EVENT_T_TARGET_NOT_REACHABLE. Being in position is always reachable. Time the creature cannot move (stunned, casting) does
/// not count. One generator per chase: a new target starts a new count.
/// </para>
/// </summary>
internal abstract class TargetedMovementGenerator(Unit target) : ICreatureMovementGenerator
{
    /// <summary>How often a moving chaser re-measures its target (ms); also the re-path cadence of a stuck chaser.</summary>
    public const int RecheckMs = 100;

    /// <summary>A moving target that has moved less than this (yd) keeps the current spline.</summary>
    public const float TargetMoveTolerance = 0.5f;

    /// <summary>vmangos CONTACT_DISTANCE: the gap kept between two bounding radii.</summary>
    public const float ContactDistance = 0.5f;

    /// <summary>A path whose end is closer than this (yd) to where the creature stands launches no spline.</summary>
    public const float ArrivedEpsilon = 0.1f;

    private int _recheckMs;
    private Vector3? _aimedAt;
    private bool _reachable = true;

    public Unit Target { get; } = target;

    public abstract MovementGeneratorType Type { get; }

    /// <summary>False since the last path query found the target unreachable (vmangos TargetedMovementGenerator::IsReachable).</summary>
    public bool IsReachable => _reachable;

    /// <summary>Milliseconds the target has been unreachable without a break (0 while reachable).</summary>
    public uint UnreachableMs { get; private set; }

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _recheckMs = 0;
        _aimedAt = null;
        _reachable = true;
        UnreachableMs = 0;
        if (!CannotMove(creature, mover))
        {
            Step(creature, mover);
        }
    }

    public void Interrupt(Creature creature, ICreatureMover mover) => _aimedAt = null;

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (!Target.IsInWorld || !ReferenceEquals(Target.Map, creature.Map) || !Target.IsAlive)
        {
            return false;
        }

        if (CannotMove(creature, mover))
        {
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }

            _aimedAt = null;
            return true;
        }

        if (!_reachable)
        {
            UnreachableMs += diffMs;
        }

        _recheckMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_recheckMs <= 0 || (!creature.IsMoving && _reachable))
        {
            Step(creature, mover);
        }

        // vmangos TargetedMovementGenerator::Update: a finalized spline informs once the re-measure is done (never while the
        // creature cannot move, which returned above).
        if (!creature.IsMoving)
        {
            OnSplineFinalized(creature, mover);
        }

        return true;
    }

    /// <summary>The generator ran with the creature standing (vmangos <c>movespline->Finalized()</c> after the re-measure).</summary>
    protected virtual void OnSplineFinalized(Creature creature, ICreatureMover mover)
    {
    }

    /// <summary>
    /// vmangos: a casting, stunned, rooted, fleeing or confused unit does not chase (UNIT_STATE_CASTING / CAN_NOT_MOVE, which holds
    /// UNIT_STATE_ROOT: UnitDefines.h:383, ChaseMovementGenerator::Update TargetedMovementGenerator.cpp:266-270). A creature's root is
    /// <c>MovementFlags.Root</c> (set by <c>CcState.RefreshRoot</c>), not a unit flag.
    /// </summary>
    private static bool CannotMove(Creature creature, ICreatureMover mover)
        => mover.IsCasting(creature) || (creature.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0
            || creature.Movement.HasFlag(MovementFlags.Root);

    /// <summary>True when the creature is where this generator wants it.</summary>
    protected abstract bool IsInPosition(Creature creature);

    /// <summary>The point to move to.</summary>
    protected abstract Vector3 Destination(Creature creature);

    protected abstract bool Run(Creature creature);

    protected virtual void OnInPosition(Creature creature)
    {
    }

    /// <summary>The point at <paramref name="distance"/> from the target in direction <paramref name="angle"/> (world angle).</summary>
    protected Vector3 PointAround(float distance, float angle)
        => new(Target.X + (distance * MathF.Cos(angle)), Target.Y + (distance * MathF.Sin(angle)), Target.Z);

    private void Step(Creature creature, ICreatureMover mover)
    {
        _recheckMs = RecheckMs;
        if (IsInPosition(creature))
        {
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }

            OnInPosition(creature);
            _aimedAt = null;
            SetReachable(true);
            return;
        }

        var targetPos = new Vector3(Target.X, Target.Y, Target.Z);
        if (creature.IsMoving && _aimedAt is { } aimed && Vector3.Distance(aimed, targetPos) < TargetMoveTolerance)
        {
            return;
        }

        Vector3 destination = Destination(creature);
        bool reachable = true;
        IReadOnlyList<Vector3> path = mover is ICreaturePathQuery query
            ? query.FindPath(creature, destination, out reachable)
            : mover.FindPath(creature, destination);

        // A stuck chaser's partial path ends where it already stands: no zero-length spline (and no SMSG_MONSTER_MOVE) every recheck.
        if (path.Count > 0 && Vector3.DistanceSquared(path[^1], new Vector3(creature.X, creature.Y, creature.Z)) > ArrivedEpsilon * ArrivedEpsilon)
        {
            mover.MovePath(creature, path, Run(creature), SplineFacing.None);
        }
        else if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }

        _aimedAt = targetPos;
        SetReachable(reachable);
    }

    private void SetReachable(bool reachable)
    {
        _reachable = reachable;
        if (reachable)
        {
            UnreachableMs = 0;
        }
    }
}

/// <summary>
/// vmangos ChaseMovementGenerator: run to the target's contact point (both bounding radii plus
/// <see cref="TargetedMovementGenerator.ContactDistance"/>, on the line from the target to the
/// chaser) until melee auto-attack reach (<see cref="MapCombat.CanReachWithMeleeAutoAttack"/>).
/// In reach, update the server facing for melee checks; the client turns to UNIT_FIELD_TARGET
/// without a separate facing packet (vmangos TargetedMovementGenerator::Update / SetInFront).
/// </summary>
internal sealed class ChaseMovementGenerator(Unit target) : TargetedMovementGenerator(target)
{
    public override MovementGeneratorType Type => MovementGeneratorType.Chase;

    protected override bool IsInPosition(Creature creature) => MapCombat.CanReachWithMeleeAutoAttack(creature, Target);

    protected override void OnInPosition(Creature creature)
    {
        float dx = Target.X - creature.X;
        float dy = Target.Y - creature.Y;
        if ((dx * dx) + (dy * dy) > 0.0001f)
        {
            creature.Orientation = Creature.NormalizeOrientation(MathF.Atan2(dy, dx));
        }
    }

    protected override Vector3 Destination(Creature creature)
    {
        float dx = creature.X - Target.X;
        float dy = creature.Y - Target.Y;
        float angle = (dx * dx) + (dy * dy) > 0.0001f ? MathF.Atan2(dy, dx) : Target.Orientation;
        return PointAround(Target.BoundingRadius + creature.BoundingRadius + ContactDistance, angle);
    }

    protected override bool Run(Creature creature) => true;
}

/// <summary>Keep a caster at its configured distance from the victim while retaining chase ownership.</summary>
internal sealed class RangedMovementGenerator(Unit target, float distance) : TargetedMovementGenerator(target)
{
    public float Distance { get; } = distance;

    public override MovementGeneratorType Type => MovementGeneratorType.Chase;

    protected override bool IsInPosition(Creature creature)
    {
        float dx = creature.X - Target.X;
        float dy = creature.Y - Target.Y;
        float dz = creature.Z - Target.Z;
        float actual = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        // Ranged mode supplies a maximum approach distance. It does not automatically retreat
        // when the victim closes; melee autoattack remains independently enabled by EventAI.
        return actual <= Distance + TargetMoveTolerance;
    }

    protected override void OnInPosition(Creature creature)
    {
        float dx = Target.X - creature.X;
        float dy = Target.Y - creature.Y;
        if ((dx * dx) + (dy * dy) > 0.0001f)
        {
            creature.Orientation = Creature.NormalizeOrientation(MathF.Atan2(dy, dx));
        }
    }

    protected override Vector3 Destination(Creature creature)
    {
        float dx = creature.X - Target.X;
        float dy = creature.Y - Target.Y;
        float angle = (dx * dx) + (dy * dy) > 0.0001f ? MathF.Atan2(dy, dx) : Target.Orientation;
        return PointAround(Distance, angle);
    }

    protected override bool Run(Creature creature) => true;
}

/// <summary>
/// vmangos FollowMovementGenerator: hold <c>distance</c> (beyond both radii) at <c>angle</c>
/// relative to the target's facing; run while the target runs, walk while it walks.
/// </summary>
internal sealed class FollowMovementGenerator(Unit target, float distance, float angle) : TargetedMovementGenerator(target)
{
    /// <summary>Arrival leeway around the follow point (yd).</summary>
    public const float Leeway = 1.0f;

    public override MovementGeneratorType Type => MovementGeneratorType.Follow;

    protected override bool IsInPosition(Creature creature)
    {
        Vector3 point = Destination(creature);
        float dx = creature.X - point.X;
        float dy = creature.Y - point.Y;
        return (dx * dx) + (dy * dy) <= Leeway * Leeway;
    }

    protected override Vector3 Destination(Creature creature)
        => PointAround(Target.BoundingRadius + creature.BoundingRadius + distance, Target.Orientation + angle);

    /// <summary>
    /// vmangos FollowMovementGenerator&lt;Creature&gt;::MovementInform (TargetedMovementGenerator.cpp:861-870): the AI hears
    /// FOLLOW_MOTION_TYPE with the target's low GUID (a pet compares it with its owner's, PetAI::MovementInform).
    /// </summary>
    protected override void OnSplineFinalized(Creature creature, ICreatureMover mover)
    {
        if (creature.IsAlive)
        {
            mover.OnMovementFinished(creature, MovementGeneratorType.Follow, Target.Guid.Counter);
        }
    }

    protected override bool Run(Creature creature)
        => Target is not Player || (Target.Movement.Flags & MovementFlags.WalkMode) == 0;
}

/// <summary>
/// vmangos FleeingMovementGenerator / TimedFleeingMovementGenerator: UNIT_FLAG_FLEEING is set
/// while it runs; the creature runs to points away from the source, pausing 0.5–1 s between
/// legs. Timed flight ends after its duration (0 = until removed). Point choice re-implements
/// the vmangos rule: closer than <see cref="MinQuietDistance"/> it runs a random 0.4–1.3 × the
/// missing distance away (± 45°); otherwise a random 0.4–1.0 × (max − min quiet distance) in any direction.
/// A stunned or rooted creature holds still (<see cref="CrowdControlGates.IsHeldInPlace"/>): the spline is stopped, no leg is
/// picked and the flight's duration does not run. The flag is left set at the end when a fear aura holds it
/// (<see cref="Creature.FearHeldByAura"/>).
/// </summary>
internal sealed class FleeingMovementGenerator(Unit? source, uint durationMs) : ICreatureMovementGenerator
{
    public const float MinQuietDistance = 28.0f;
    public const float MaxQuietDistance = 43.0f;

    private int _remainingMs;
    private int _pauseMs;

    public MovementGeneratorType Type => MovementGeneratorType.Fleeing;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        creature.UnitFlags |= UnitFlags.Fleeing;
        _remainingMs = durationMs == 0 ? int.MaxValue : (int)Math.Min(durationMs, int.MaxValue);
        _pauseMs = 0;
        if (!CrowdControlGates.IsHeldInPlace(creature))
        {
            MoveAway(creature, mover); // else the first Update after the hold lifts starts the first leg (the pause timer is 0)
        }
    }

    public void Resume(Creature creature, ICreatureMover mover)
    {
        creature.UnitFlags |= UnitFlags.Fleeing;
        if (!CrowdControlGates.IsHeldInPlace(creature))
        {
            MoveAway(creature, mover);
        }
    }

    public void Interrupt(Creature creature, ICreatureMover mover) => ClearOwnFlag(creature);

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        ClearOwnFlag(creature);
        if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    /// <summary>
    /// Drop the flag this flight set, unless a fear aura holds it too (it landed during the flight): the aura's flag must outlive the
    /// flight, so the fear flight of the movement hook takes over (vmangos never lets a flight generator clear UNIT_FLAG_FLEEING).
    /// </summary>
    private static void ClearOwnFlag(Creature creature)
    {
        if (!creature.FearHeldByAura)
        {
            creature.UnitFlags &= ~UnitFlags.Fleeing;
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        // vmangos (Timed)FleeingMovementGenerator::Update (FleeingMovementGenerator.cpp:164-169, 220-225): a stunned or rooted unit
        // holds still, picks no leg, and its flee timer does not run.
        if (CrowdControlGates.IsHeldInPlace(creature))
        {
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }

            return true;
        }

        if (durationMs != 0)
        {
            _remainingMs -= (int)Math.Min(diffMs, int.MaxValue);
            if (_remainingMs <= 0)
            {
                return false;
            }
        }

        if (creature.IsMoving)
        {
            return true;
        }

        _pauseMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_pauseMs <= 0)
        {
            MoveAway(creature, mover);
        }

        return true;
    }

    private void MoveAway(Creature creature, ICreatureMover mover)
    {
        float distance;
        float angle;
        float fromSource = source is null ? float.MaxValue : MathF.Sqrt(Sq(creature.X - source.X) + Sq(creature.Y - source.Y));
        if (source is not null && fromSource < MinQuietDistance)
        {
            float away = fromSource > 0.2f ? MathF.Atan2(creature.Y - source.Y, creature.X - source.X) : (float)(mover.NextDouble() * 2 * Math.PI);
            distance = Lerp(0.4f, 1.3f, mover.NextDouble()) * (MinQuietDistance - fromSource);
            angle = away + Lerp(-MathF.PI / 4, MathF.PI / 4, mover.NextDouble());
        }
        else
        {
            distance = Lerp(0.4f, 1.0f, mover.NextDouble()) * (MaxQuietDistance - MinQuietDistance);
            angle = (float)(mover.NextDouble() * 2 * Math.PI);
        }

        distance = Math.Max(distance, 2.0f);
        float x = creature.X + (distance * MathF.Cos(angle));
        float y = creature.Y + (distance * MathF.Sin(angle));
        float z = mover.GetHeight(creature.MapId, x, y, creature.Z) ?? creature.Z;
        mover.MovePath(creature, mover.FindPath(creature, new Vector3(x, y, z)), run: true, SplineFacing.None);
        _pauseMs = mover.URand(500, 1000);
    }

    private static float Sq(float v) => v * v;

    private static float Lerp(float a, float b, double t) => a + ((b - a) * (float)t);
}

/// <summary>
/// vmangos HomeMovementGenerator: run back to the evade point and face its orientation; when
/// the spline ends the creature has reached home (the AI's JustReachedHome).
/// </summary>
internal sealed class HomeMovementGenerator(CreatureHome home) : ICreatureMovementGenerator
{
    private bool _arrived;

    public MovementGeneratorType Type => MovementGeneratorType.Home;

    public CreatureHome Home => home;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _arrived = false;
        var destination = new Vector3(home.X, home.Y, home.Z);

        // vmangos sets the final facing only when the spawn point is the target (HomeMovementGenerator.cpp:54-56, 64-65); a position a
        // movement generator supplied (GetResetPosition) carries no orientation (NaN here) and the creature faces its travel direction.
        SplineFacing facing = float.IsNaN(home.Orientation) ? SplineFacing.None : SplineFacing.ToAngle(home.Orientation);
        mover.MovePath(creature, mover.FindPath(creature, destination), run: true, facing);
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (creature.IsMoving)
        {
            return true;
        }

        _arrived = true;
        return false;
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        if (completed && _arrived)
        {
            mover.OnMovementFinished(creature, MovementGeneratorType.Home, 0);
        }
    }
}

/// <summary>vmangos PointMovementGenerator: go to one point; the AI is told (MovementInform) on arrival.</summary>
internal sealed class PointMovementGenerator(uint id, Vector3 point, bool run) : ICreatureMovementGenerator
{
    private bool _arrived;

    public MovementGeneratorType Type => MovementGeneratorType.Point;

    public uint Id => id;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _arrived = false;
        mover.MovePath(creature, mover.FindPath(creature, point), run, SplineFacing.None);
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (creature.IsMoving)
        {
            return true;
        }

        _arrived = true;
        return false;
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        if (completed && _arrived)
        {
            mover.OnMovementFinished(creature, MovementGeneratorType.Point, id);
        }
        else if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }
}
