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
    /// The path to <paramref name="destination"/>; <paramref name="reachable"/> is false when the pathfinder found no path
    /// (<see cref="Maps.Collision.PathType.NoPath"/>: the result is EMPTY and the creature does not move) or only a partial
    /// one (<see cref="Maps.Collision.PathType.Incomplete"/>: the creature walks it). Without navigation data everything is reachable.
    /// </summary>
    IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination, out bool reachable);

    /// <summary>Whether this update's creature path-search budget (searches and A* nodes) still has room for one more search.</summary>
    bool HasPathBudget { get; }

    /// <summary>A chase/follow re-path was put off to a later update because <see cref="HasPathBudget"/> was false.</summary>
    void NoteDeferred(Creature creature);

    /// <summary>True when Pass 2 random movement is running on a crowded continent map (&gt;256 active random-wandering creatures).</summary>
    bool IsCrowdedRandomPass => false;

    /// <summary>
    /// The path-job pool (<c>Creatures:Movement:PathJobs</c>) can take another search for this map now, so an out-of-combat request
    /// need not wait for this update's synchronous budget.
    /// </summary>
    bool CanQueuePathJob => false;

    /// <summary>
    /// The last <c>PlanPath</c> answered <see cref="PathOutcome.Deferred"/> because its search is queued or running on the
    /// path-job pool (not because the budget was spent): the answer is typically ready by the next update, so ask again soon.
    /// </summary>
    bool LastPlanQueued => false;
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
/// re-paths every <see cref="RecheckMs"/> (the re-path). The host's unreachable-target timer (vmangos m_targetNotReachableTimer,
/// Creature.cpp:1013-1046, <c>CreatureMapSystem.UpdateUnreachableTarget</c>) and EventAI's EVENT_T_TARGET_NOT_REACHABLE read
/// <see cref="IsReachable"/>. Being in position is always reachable; a creature that cannot move keeps the last verdict.
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
    private bool _deferred;
    private int _leashRefreshMs = LeashRefreshIntervalMs;
    private bool _pathLaunched;

    /// <summary>
    /// The period (ms) of the chaser's leash extension timer (vmangos TargetedMovementGenerator.h:107, <c>m_leashExtensionTimer{ 5000 }</c>;
    /// re-armed to 5000 when the generator starts a new path, <c>m_bRecalculateTravel</c>, TargetedMovementGenerator.cpp:399-401).
    /// </summary>
    public const int LeashRefreshIntervalMs = 5000;

    public Unit Target { get; } = target;

    public abstract MovementGeneratorType Type { get; }

    /// <summary>False since the last path query found the target unreachable (vmangos TargetedMovementGenerator::IsReachable).</summary>
    public bool IsReachable => _reachable;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _recheckMs = 0;
        _aimedAt = null;
        _reachable = true;
        _leashRefreshMs = LeashRefreshIntervalMs;
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

        _deferred = false;
        _pathLaunched = false;
        _recheckMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_recheckMs <= 0 || (!creature.IsMoving && _reachable))
        {
            Step(creature, mover);
        }

        TickLeashRefresh(creature, mover, diffMs);

        // vmangos TargetedMovementGenerator::Update: a finalized spline informs once the re-measure is done (never while the
        // creature cannot move, which returned above).
        // A chaser whose re-path was deferred has not finished anything: it must not inform on every deferred update.
        if (!creature.IsMoving && !_deferred)
        {
            OnSplineFinalized(creature, mover);
        }

        return true;
    }

    /// <summary>
    /// vmangos TargetedMovementGenerator::Update (TargetedMovementGenerator.cpp:389-401): while the chase spline is finalized (the creature
    /// stands still, whether at melee reach or at a ranged chase's distance) a 5000 ms timer runs and, each time it passes, the creature's
    /// leash extension clock is refreshed (<c>Creature::UpdateLeashExtensionTime</c>), so a mob does not leash while its victim stands and waits.
    /// A new path (a launched spline, not each tick of a moving one) re-arms the timer to 5000. A chase whose re-path is deferred does not count (it has not finished anything), and a
    /// creature that cannot move never reaches this. Chase generators only (melee and ranged); Follow does not refresh a creature's leash.
    /// </summary>
    private void TickLeashRefresh(Creature creature, ICreatureMover mover, uint diffMs)
    {
        // A moving chaser neither counts nor re-arms: the timer is re-armed only when a new path starts (Step).
        if (creature.IsMoving || _deferred || _pathLaunched || Type != MovementGeneratorType.Chase)
        {
            return;
        }

        _leashRefreshMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_leashRefreshMs <= 0)
        {
            _leashRefreshMs = LeashRefreshIntervalMs;
            mover.ExtendLeash(creature);
        }
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
    private bool CannotMove(Creature creature, ICreatureMover mover)
        => mover.IsCasting(creature) || (creature.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0
            || creature.Movement.HasFlag(MovementFlags.Root)
            || (Type == MovementGeneratorType.Follow && creature.FollowMovementDisabled); // cmangos UNIT_STAT_NO_FOLLOW_MOVEMENT

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

        if (mover is ICreaturePathQuery { HasPathBudget: false } budgeted
            && (!budgeted.CanQueuePathJob || (Type == MovementGeneratorType.Follow && (!mover.MovementOptions.PathJobsFollow || creature.Combat.IsInCombat))))
        {
            // Over this update's search budget: keep the current spline (or standing), and re-path on the next update.
            budgeted.NoteDeferred(creature);
            _recheckMs = 0;
            _deferred = true;
            return;
        }

        Vector3 destination = Destination(creature);
        bool reachable = true;
        IReadOnlyList<Vector3> path = mover is ICreaturePathQuery query
            ? query.FindPath(creature, destination, out reachable)
            : mover.FindPath(creature, destination);
        if (path.Count == 0 && mover is ICreaturePathQuery { LastPlanQueued: true })
        {
            // The search went to the path-job pool (Creatures:Movement:PathJobs / PathJobsFollow): keep the current spline and come back
            // for the answer on the next update. Not aimed yet, so the next update asks again rather than waiting for the target to move.
            _recheckMs = 0;
            _deferred = true;
            return;
        }

        // PATHFIND_NOPATH (path is empty, TargetedMovementGenerator.cpp:188-189): no move. The creature stands (or runs out the spline it
        // has) and the re-check in RecheckMs asks again; meanwhile IsReachable is false, which feeds the unreachable-target evade.
        if (path.Count > 0)
        {
            // A stuck chaser's partial path ends where it already stands: no zero-length spline (and no SMSG_MONSTER_MOVE) every recheck.
            if (Vector3.DistanceSquared(path[^1], new Vector3(creature.X, creature.Y, creature.Z)) > ArrivedEpsilon * ArrivedEpsilon)
            {
                mover.MovePath(creature, path, Run(creature), SplineFacing.None);
                _leashRefreshMs = LeashRefreshIntervalMs; // vmangos m_bRecalculateTravel: a new path re-arms the leash extension timer
                _pathLaunched = true; // the launching update does not also count
            }
            else if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }
        }

        _aimedAt = targetPos;
        SetReachable(reachable);
    }

    private void SetReachable(bool reachable) => _reachable = reachable;
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

    /// <summary>The longest leg walked on the navmesh (vmangos FleeingMovementGenerator caps its path at 30 yd).</summary>
    public const float MeshFleeCap = 30.0f;

    /// <summary>A mesh flight leg shorter than this (yd) is not walked: another direction is drawn.</summary>
    public const float MinMeshFleeLeg = 1.0f;

    /// <summary>The most other directions drawn when the first mesh leg is cut to nothing.</summary>
    public const int MeshFleeRedraws = 4;

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
        if (mover is ICreaturePathQuery { HasPathBudget: false } budgeted)
        {
            budgeted.NoteDeferred(creature);
            _pauseMs = 0;
            return;
        }

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
        float z;

        // vmangos FleeingMovementGenerator (:108 -> Map::GetWalkHitPosition): the leg is walked on the navmesh, capped at 30 yd and cut
        // where the mesh ends or a steep slope begins. Without mesh data for the spot the point stays, on the floor there (terrain or a
        // static model).
        Vector3 from = new(creature.X, creature.Y, creature.Z);
        float cut = Math.Min(distance, MeshFleeCap);
        if (mover.TryRaycastMesh(creature, from, new Vector3(creature.X + (cut * MathF.Cos(angle)), creature.Y + (cut * MathF.Sin(angle)), creature.Z), out Vector3 reached))
        {
            // A wall right in front cuts the leg to nothing: aim another way (random direction) rather than issue a zero-length move.
            for (int redraw = 0; redraw < MeshFleeRedraws && Sq(reached.X - from.X) + Sq(reached.Y - from.Y) < MinMeshFleeLeg * MinMeshFleeLeg; redraw++)
            {
                float other = (float)(mover.NextDouble() * 2 * Math.PI);
                mover.TryRaycastMesh(creature, from, new Vector3(creature.X + (cut * MathF.Cos(other)), creature.Y + (cut * MathF.Sin(other)), creature.Z), out reached);
            }

            if (Sq(reached.X - from.X) + Sq(reached.Y - from.Y) < MinMeshFleeLeg * MinMeshFleeLeg)
            {
                _pauseMs = mover.URand(500, 1000); // boxed in on every side tried: stay, and try again after the pause
                return;
            }

            x = reached.X;
            y = reached.Y;
            z = reached.Z;
        }
        else
        {
            z = mover.GetHeight(creature.MapId, x, y, creature.Z) ?? creature.Z;
        }

        PlannedPath plan = mover.PlanPath(creature, new Vector3(x, y, z));
        switch (plan.Outcome)
        {
            case PathOutcome.NoPath:
                // vmangos (:60-62): no path, wait 1-1.5 s and pick another point; the creature does not run through the wall.
                _pauseMs = mover.URand(NoPathRetryMinMs, NoPathRetryMaxMs);
                return;
            case PathOutcome.Deferred:
                _pauseMs = 0; // the search budget is spent: ask again on the next update
                return;
            default:
                mover.MovePath(creature, plan.Corners, run: true, SplineFacing.None);
                _pauseMs = mover.URand(500, 1000);
                return;
        }
    }

    /// <summary>How long a flight with no path waits before it picks another point (vmangos urand(1000, 1500), FleeingMovementGenerator.cpp:62).</summary>
    public const int NoPathRetryMinMs = 1000;

    public const int NoPathRetryMaxMs = 1500;

    private static float Sq(float v) => v * v;

    private static float Lerp(float a, float b, double t) => a + ((b - a) * (float)t);
}

/// <summary>
/// vmangos HomeMovementGenerator: run back to the evade point and face its orientation; when
/// the spline ends the creature has reached home (the AI's JustReachedHome).
/// </summary>
internal sealed class HomeMovementGenerator(CreatureHome home) : ICreatureMovementGenerator
{
    /// <summary>How long a return home may wait for path budget before the creature is teleported home instead.</summary>
    public const int DeferredTeleportMs = 3000;

    private bool _arrived;
    private bool _waitingForBudget;
    private int _waitedMs;

    public MovementGeneratorType Type => MovementGeneratorType.Home;

    public CreatureHome Home => home;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _arrived = false;
        _waitingForBudget = false;
        _waitedMs = 0;
        Start(creature, mover);
    }

    /// <summary>
    /// vmangos HomeMovementGenerator::Initialize (HomeMovementGenerator.cpp:58-71): run home when the path is a NORMAL one (a whole mesh
    /// path, or the straight line of a map with no mesh data); with no path or only a partial one (<c>too far away or no path</c>) the
    /// creature is teleported home, never walked through the geometry. A spent search budget waits for a later update, and past
    /// <see cref="DeferredTeleportMs"/> teleports too. Either way the generator ends the same way a run does, so the reset and the AI's
    /// JustReachedHome happen as usual.
    /// </summary>
    private void Start(Creature creature, ICreatureMover mover)
    {
        var destination = new Vector3(home.X, home.Y, home.Z);

        // vmangos sets the final facing only when the spawn point is the target (HomeMovementGenerator.cpp:54-56, 64-65); a position a
        // movement generator supplied (GetResetPosition) carries no orientation (NaN here) and the creature faces its travel direction.
        PlannedPath plan = mover.PlanPath(creature, destination, toSpawn: true);
        switch (plan.Outcome)
        {
            case PathOutcome.Deferred when _waitedMs < DeferredTeleportMs:
                _waitingForBudget = true;
                return;
            case PathOutcome.Mesh or PathOutcome.Direct:
                _waitingForBudget = false;
                SplineFacing facing = float.IsNaN(home.Orientation) ? SplineFacing.None : SplineFacing.ToAngle(home.Orientation);
                mover.MovePath(creature, plan.Corners, run: true, facing);
                return;
            default:
                _waitingForBudget = false;
                mover.TeleportHome(creature, home.X, home.Y, home.Z, float.IsNaN(home.Orientation) ? creature.Orientation : home.Orientation);
                return;
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (_waitingForBudget)
        {
            _waitedMs += (int)Math.Min(diffMs, int.MaxValue);
            Start(creature, mover);
            if (_waitingForBudget)
            {
                return true;
            }
        }

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
        Start(creature, mover);
    }

    /// <summary>The point is authored: the mesh path, the straight line when the mesh has none; an empty path (budget spent) waits for a later update.</summary>
    private void Start(Creature creature, ICreatureMover mover)
    {
        IReadOnlyList<Vector3> path = mover.FindPath(creature, point);
        _waitingForBudget = path.Count == 0;
        if (!_waitingForBudget)
        {
            mover.MovePath(creature, path, run, SplineFacing.None);
        }
    }

    private bool _waitingForBudget;

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (_waitingForBudget)
        {
            Start(creature, mover);
            return true;
        }

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
