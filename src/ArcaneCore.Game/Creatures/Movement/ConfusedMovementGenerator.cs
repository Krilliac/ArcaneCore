using System.Numerics;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The stagger of a creature under a confuse aura (ModConfuse, which a Polymorph carries): it walks to a random point
/// within <see cref="StaggerRadius"/> of the spot where it lost its wits, and picks a new point every 800-1500 ms,
/// even mid-leg, so a leg is cut off part-way (mangosserver MotionGenerators/ConfusedMovementGenerator.cpp:
/// STAGGER_RADIUS, STAGGER_INTERVAL_MIN/MAX, MOVE_WALK). The anchor stays where it was set when the generator
/// resumes after an interruption (cpp Reset keeps m_anchor). A stunned or rooted creature holds still: its spline is
/// stopped and the timer restarts at once, so it moves on the first free tick (cpp Intent: "Ignore while any OTHER
/// no-reaction state applies").
/// <para>
/// <c>UnitFlags.Confused</c> belongs to the auras (<c>CcState.RefreshFear</c>), not to this generator: the movement hook of the
/// map tick starts and ends the generator from the flag, and neither Interrupt nor Finish touches it.
/// </para>
/// Limits: the point is uniform over the disc at the height provider's ground Z (or the anchor Z), with no navmesh
/// reachability test (the reference routes it through the frame's RandomPoint); the route goes through
/// <see cref="ICreatureMover.FindPath"/> like every other creature move.
/// Thread affinity: world thread (the creature's map). Allocation: one object per confuse application; a leg allocates
/// what the mover's path does, at most once per 800 ms.
/// </summary>
internal sealed class ConfusedMovementGenerator : ICreatureMovementGenerator
{
    /// <summary>How far from where it was confused a unit may stagger (yd).</summary>
    public const float StaggerRadius = 10.0f;

    public const int StaggerIntervalMinMs = 800;
    public const int StaggerIntervalMaxMs = 1500;

    private float _anchorX;
    private float _anchorY;
    private float _anchorZ;
    private int _staggerMs;
    private bool _interrupted;

    public MovementGeneratorType Type => MovementGeneratorType.Confused;

    /// <summary>Where the stagger is centred (set when the generator starts).</summary>
    public Vector3 Anchor => new(_anchorX, _anchorY, _anchorZ);

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _anchorX = creature.X;
        _anchorY = creature.Y;
        _anchorZ = creature.Z;
        Resume(creature, mover);
    }

    public void Resume(Creature creature, ICreatureMover mover)
    {
        _staggerMs = 0;
        _interrupted = false;
        if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    public void Interrupt(Creature creature, ICreatureMover mover)
    {
        _interrupted = true;
        if (creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        // Buried under a pushed generator (a point, a flee for assistance), the running spline is that generator's: stopping it would
        // make a point look arrived where the creature stands.
        if (!_interrupted && creature.IsMoving)
        {
            mover.StopMoving(creature);
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (CrowdControlGates.IsHeldInPlace(creature))
        {
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }

            _staggerMs = 0;
            return true;
        }

        _staggerMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_staggerMs > 0)
        {
            return true;
        }

        double angle = mover.NextDouble() * 2 * Math.PI;
        double distance = StaggerRadius * Math.Sqrt(mover.NextDouble());
        float x = _anchorX + (float)(distance * Math.Cos(angle));
        float y = _anchorY + (float)(distance * Math.Sin(angle));
        float z = mover.GetHeight(creature.MapId, x, y, _anchorZ) ?? _anchorZ;
        mover.MovePath(creature, mover.FindPath(creature, new Vector3(x, y, z)), run: false, SplineFacing.None);
        _staggerMs = mover.URand(StaggerIntervalMinMs, StaggerIntervalMaxMs);
        return true;
    }
}
