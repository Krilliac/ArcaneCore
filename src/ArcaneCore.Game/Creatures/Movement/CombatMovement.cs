using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos TargetedMovementGenerator: keeps a creature at a target. <see cref="ChaseMovementGenerator"/>
/// runs into melee reach; <see cref="FollowMovementGenerator"/> holds a distance and angle. Every
/// <see cref="RecheckMs"/> (and whenever the creature stands still) the target is re-measured;
/// a new spline goes out only when the creature is out of place and the target has moved more
/// than <see cref="TargetMoveTolerance"/> since the last spline was aimed.
/// </summary>
internal abstract class TargetedMovementGenerator(Unit target) : ICreatureMovementGenerator
{
    /// <summary>How often a moving chaser re-measures its target (ms).</summary>
    public const int RecheckMs = 100;

    /// <summary>A moving target that has moved less than this (yd) keeps the current spline.</summary>
    public const float TargetMoveTolerance = 0.5f;

    /// <summary>vmangos CONTACT_DISTANCE: the gap kept between two bounding radii.</summary>
    public const float ContactDistance = 0.5f;

    private int _recheckMs;
    private Vector3? _aimedAt;

    public Unit Target { get; } = target;

    public abstract MovementGeneratorType Type { get; }

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _recheckMs = 0;
        _aimedAt = null;
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

        _recheckMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_recheckMs > 0 && creature.IsMoving)
        {
            return true;
        }

        Step(creature, mover);
        return true;
    }

    /// <summary>vmangos: a casting, stunned, fleeing or confused unit does not chase (UNIT_STATE_CASTING / CAN_NOT_MOVE).</summary>
    private static bool CannotMove(Creature creature, ICreatureMover mover)
        => mover.IsCasting(creature) || (creature.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0;

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
            return;
        }

        var targetPos = new Vector3(Target.X, Target.Y, Target.Z);
        if (creature.IsMoving && _aimedAt is { } aimed && Vector3.Distance(aimed, targetPos) < TargetMoveTolerance)
        {
            return;
        }

        Vector3 destination = Destination(creature);
        mover.MovePath(creature, mover.FindPath(creature, destination), Run(creature), SplineFacing.None);
        _aimedAt = targetPos;
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

    protected override bool Run(Creature creature)
        => Target is not Player || (Target.Movement.Flags & MovementFlags.WalkMode) == 0;
}

/// <summary>
/// vmangos FleeingMovementGenerator / TimedFleeingMovementGenerator: UNIT_FLAG_FLEEING is set
/// while it runs; the creature runs to points away from the source, pausing 0.5–1 s between
/// legs. Timed flight ends after its duration (0 = until removed). Point choice re-implements
/// the vmangos rule: closer than <see cref="MinQuietDistance"/> it runs a random 0.4–1.3 × the
/// missing distance away (± 45°); otherwise a random 0.4–1.0 × (max − min quiet distance) in any direction.
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
        MoveAway(creature, mover);
    }

    public void Resume(Creature creature, ICreatureMover mover)
    {
        creature.UnitFlags |= UnitFlags.Fleeing;
        MoveAway(creature, mover);
    }

    public void Interrupt(Creature creature, ICreatureMover mover) => creature.UnitFlags &= ~UnitFlags.Fleeing;

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        creature.UnitFlags &= ~UnitFlags.Fleeing;
        if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
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
        mover.MovePath(creature, mover.FindPath(creature, destination), run: true, SplineFacing.ToAngle(home.Orientation));
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
