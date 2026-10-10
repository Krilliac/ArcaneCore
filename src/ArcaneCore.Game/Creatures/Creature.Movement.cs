using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>A creature's home, movement type, motion master and spline state.</summary>
public sealed partial class Creature : Unit, ICombatCreature
{
    /// <summary>Where the creature returns to (spawn point; vmangos GetRespawnCoord / home position).</summary>
    public CreatureHome Home { get; private set; }

    public CreatureMovementType MovementType { get; }

    public float WanderDistance { get; }

    /// <summary>The movement generator stack (vmangos MotionMaster).</summary>
    public MotionMaster Motion { get; }

    /// <summary>The active spline, or null when standing.</summary>
    public CreatureSpline? Spline { get; private set; }

    public bool IsMoving => Spline is not null;

    internal void SetHome(CreatureHome home) => Home = home;

    /// <summary>
    /// A creature's flight is server state (vmangos Unit::SetFly sets MOVEFLAG_FLYING on m_movementInfo, Unit.cpp:7304-7310): its moves and
    /// relocations keep it, as they keep the walk mode.
    /// </summary>
    private protected override MovementFlags RelocationKeptFlags
        => MovementFlags.Root | MovementFlags.WaterWalking | MovementFlags.Hover | MovementFlags.SafeFall | MovementFlags.WalkMode | MovementFlags.Flying;

    // --- movement --------------------------------------------------------------------------

    /// <summary>Start a straight move to (x, y, z); returns the spline (world thread).</summary>
    internal CreatureSpline StartSpline(float x, float y, float z, bool run, float? finalOrientation, uint splineId, long clockMs)
        => StartSpline([new Vector3(x, y, z)], run, finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None, splineId, clockMs);

    /// <summary>
    /// Start a linear move through <paramref name="path"/> (every point after the current
    /// position, destination last). The duration is the path length over the walk or run speed
    /// (vmangos MoveSpline::Initialize: computeDuration over a linear spline), at least 1 ms.
    /// </summary>
    internal CreatureSpline StartSpline(IReadOnlyList<Vector3> path, bool run, SplineFacing facing, uint splineId, long clockMs)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new ArgumentException("a spline needs at least one point", nameof(path));
        }

        // The live speed: slows, hastes and the wounded slowdown change it (Locomotion/Speed/UnitSpeed.cs); it starts as the template speed.
        float speed = run ? RunSpeed : WalkSpeed;
        var start = new Vector3(X, Y, Z);
        float length = 0;
        Vector3 previous = start;
        for (int i = 0; i < path.Count; i++) // indexed: no boxed enumerator over the IReadOnlyList
        {
            length += Vector3.Distance(previous, path[i]);
            previous = path[i];
        }

        uint duration = Math.Max(1u, (uint)MathF.Round(length / speed * 1000f));
        Vector3 end = path[^1];
        var spline = new CreatureSpline(splineId, X, Y, Z, end.X, end.Y, end.Z, run, facing.FinalAngle, clockMs, duration)
        {
            Path = path.Count > 1 ? [.. path] : [],
            Facing = facing,
        };
        Spline = spline;

        // While on a spline the unit faces its direction of travel.
        Vector3 first = path[0];
        float dx = first.X - X;
        float dy = first.Y - Y;
        if ((dx * dx) + (dy * dy) > 0.0001f)
        {
            Orientation = NormalizeOrientation(MathF.Atan2(dy, dx));
        }

        return spline;
    }

    /// <summary>Advance along the spline; returns true when it finished this step.</summary>
    internal bool AdvanceSpline(long clockMs, uint serverTimeMs)
    {
        if (Spline is not { } spline)
        {
            return false;
        }

        (float x, float y, float z) = spline.PositionAt(clockMs);
        if (spline.IsFinished(clockMs))
        {
            Spline = null;
            Relocate(spline.EndX, spline.EndY, spline.EndZ, spline.FinalOrientation ?? Orientation, serverTimeMs);
            return true;
        }

        // Keep the movement block in step so a create block shows the live position.
        Relocate(x, y, z, Orientation, serverTimeMs);
        return false;
    }

    /// <summary>Stop where the creature is (world thread).</summary>
    internal void StopSpline(long clockMs, uint serverTimeMs)
    {
        if (Spline is { } spline)
        {
            (float x, float y, float z) = spline.PositionAt(clockMs);
            Spline = null;
            Relocate(x, y, z, Orientation, serverTimeMs);
        }
    }

    internal void ResetToHome(uint serverTimeMs)
    {
        Spline = null;
        Relocate(Home.X, Home.Y, Home.Z, Home.Orientation, serverTimeMs);
    }

    internal static float NormalizeOrientation(float o)
    {
        const float TwoPi = MathF.PI * 2f;
        o %= TwoPi;
        return o < 0 ? o + TwoPi : o;
    }
}
