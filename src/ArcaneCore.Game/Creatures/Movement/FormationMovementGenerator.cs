using System.Numerics;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A formation follower's default movement (cmangos FormationMovementGenerator, MotionGenerators/TargetedMovementGenerator.cpp:1157-1491;
/// no code copied): it keeps its slot, <see cref="FormationSlot.Distance"/> yards at <see cref="FormationSlot.Angle"/> from the leader's
/// heading. While the leader walks a leg, the follower is sent to the slot around the end of that leg, along the leader's heading there,
/// walking or running as the leader does (running when it lags behind); when the leader stops, it steps into its slot around the leader and
/// takes the leader's facing. A follower far from the leader (beyond <see cref="MoveToMasterDistance"/>) runs to it first.
/// <para>
/// Simplified from cmangos: the follower's path is not built point by point along the leader's spline with a matched speed (BuildPath);
/// it is one leg to the slot at the leader's next stop, recomputed whenever the leader starts a new leg. No teleport back to the leader.
/// </para>
/// </summary>
internal sealed class FormationMovementGenerator(FormationState formation, FormationSlot slot) : ICreatureMovementGenerator
{
    /// <summary>How often a follower that is out of its slot re-measures (ms).</summary>
    public const int RecheckMs = 500;

    /// <summary>Within this of its slot a follower stands still (yd).</summary>
    public const float Leeway = 1.0f;

    /// <summary>cmangos m_moveToMasterDistance: min(slot distance × 3, 100); a follower farther than that runs to the leader.</summary>
    public float MoveToMasterDistance => Math.Min(Math.Max(slot.Distance * 3f, 15f), 100f);

    private uint _leaderSplineId = uint.MaxValue;
    private bool _leaderWasMoving;
    private int _recheckMs;
    private CreatureHome? _resetPoint;

    public MovementGeneratorType Type => MovementGeneratorType.Formation;

    public FormationSlot Slot => slot;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _leaderSplineId = uint.MaxValue;
        _recheckMs = 0;
        _resetPoint = null;
        Step(creature, mover, force: true);
    }

    public void Resume(Creature creature, ICreatureMover mover)
    {
        _leaderSplineId = uint.MaxValue;
        _recheckMs = 0;
        Step(creature, mover, force: true);
    }

    /// <summary>cmangos FormationMovementGenerator::Interrupt: the spot it was at when a fight took it is where evade returns it.</summary>
    public void Interrupt(Creature creature, ICreatureMover mover)
        => _resetPoint = new CreatureHome(creature.X, creature.Y, creature.Z, creature.Orientation);

    public CreatureHome? GetResetPosition(Creature creature) => _resetPoint;

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (CreatureMovementGates.CannotMove(creature) || mover.IsCasting(creature))
        {
            return true;
        }

        _recheckMs -= (int)Math.Min(diffMs, int.MaxValue);
        Step(creature, mover, force: false);
        return true;
    }

    private void Step(Creature creature, ICreatureMover mover, bool force)
    {
        if (formation.Master is not Creature leader || ReferenceEquals(leader, creature) || !leader.IsAlive || !ReferenceEquals(leader.Map, creature.Map))
        {
            return;
        }

        CreatureSpline? spline = leader.Spline;
        bool leaderMoving = spline is not null;
        bool newLeg = leaderMoving && spline!.Id != _leaderSplineId;
        bool leaderStopped = !leaderMoving && _leaderWasMoving;
        _leaderWasMoving = leaderMoving;
        if (!force && !newLeg && !leaderStopped && (_recheckMs > 0 || creature.IsMoving))
        {
            return;
        }

        _recheckMs = RecheckMs;
        var here = new Vector3(creature.X, creature.Y, creature.Z);
        var leaderAt = new Vector3(leader.X, leader.Y, leader.Z);
        if (Vector2.Distance(new Vector2(here.X, here.Y), new Vector2(leaderAt.X, leaderAt.Y)) > MoveToMasterDistance + slot.Distance)
        {
            mover.MovePath(creature, mover.FindPath(creature, leaderAt), run: true, SplineFacing.None); // HandleMasterDistanceCheck
            return;
        }

        Vector3 destination;
        float heading;
        bool run;
        if (leaderMoving)
        {
            _leaderSplineId = spline!.Id;
            IReadOnlyList<Vector3> points = spline.Points;
            Vector3 end = points[^1];
            Vector3 before = points.Count > 1 ? points[^2] : new Vector3(spline.StartX, spline.StartY, spline.StartZ);
            heading = Vector2.DistanceSquared(new Vector2(end.X, end.Y), new Vector2(before.X, before.Y)) > 0.0001f
                ? MathF.Atan2(end.Y - before.Y, end.X - before.X)
                : leader.Orientation;
            destination = SlotAround(end, heading);
            float leaderLeft = Vector3.Distance(leaderAt, end);
            run = spline.Run || Vector3.Distance(here, destination) > leaderLeft + 5f; // catch up when it lags
        }
        else
        {
            heading = leader.Orientation;
            destination = SlotAround(leaderAt, heading);
            run = false;
            if (Vector2.Distance(new Vector2(here.X, here.Y), new Vector2(destination.X, destination.Y)) <= Leeway)
            {
                if (creature.IsMoving)
                {
                    mover.StopMoving(creature);
                }

                if (MathF.Abs(Creature.NormalizeOrientation(creature.Orientation - heading)) > 0.05f && !force)
                {
                    mover.SetFacingTo(creature, heading);
                }

                return;
            }
        }

        if (mover.GetHeight(creature.MapId, destination.X, destination.Y, destination.Z + 2f) is { } ground)
        {
            destination.Z = ground;
        }

        IReadOnlyList<Vector3> path = mover.FindPath(creature, destination);
        if (path.Count > 0 && Vector3.DistanceSquared(path[^1], here) > 0.01f)
        {
            mover.MovePath(creature, path, run, leaderMoving ? SplineFacing.None : SplineFacing.ToAngle(heading));
        }
    }

    /// <summary>The slot's point around <paramref name="center"/> when the leader heads <paramref name="heading"/>.</summary>
    private Vector3 SlotAround(Vector3 center, float heading)
    {
        float angle = heading + slot.Angle;
        return new Vector3(center.X + (slot.Distance * MathF.Cos(angle)), center.Y + (slot.Distance * MathF.Sin(angle)), center.Z);
    }
}
