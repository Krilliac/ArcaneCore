using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
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
/// the point is a navmesh point within the wander distance (<see cref="ICreatureMover.TryGetRandomMeshPoint"/>) reached by a mesh path, both
/// off steep slopes (the leg's search excludes them, MoveSplineInit.h:129-130); where no mesh is loaded it is uniform over the wander disc at
/// the height provider's ground Z (or the spawn Z), walked straight (the pathfinder's answer for a map or tile without mesh data). A leg with
/// no path (the point is off the mesh, or a point drawn off the mesh whose path ends on another storey) is skipped and tried again 1-1.5 s
/// later, the wait doubling for each further leg in a row without a path up to 16-24 s (<see cref="NoPathRetryDelay"/>). A leg the path
/// budget cannot serve, or whose tiles are still loading, waits 1-1.5 s (4-10 s in a crowded wander pass); one queued on the path-job pool
/// asks again after 100 ms (<see cref="PathJobRetryMs"/>). Flying creatures (<c>CanFly</c>, :28-44) keep the ground wander.
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

    /// <summary>How soon (ms) a leg whose search went to the path-job pool asks again for the answer.</summary>
    public const int PathJobRetryMs = 100;

    public const int NoPathRetryMaxMs = FleeingMovementGenerator.NoPathRetryMaxMs;

    /// <summary>vmangos RandomMovementGenerator constructor default when the spawn has none.</summary>
    public const float DefaultWanderDistance = 5.0f;

    /// <summary>The most doublings of the no-path wait (1-1.5 s, 2-3 s, ... up to 16-24 s).</summary>
    public const int MaxNoPathBackoffShift = 4;

    private int _nextMoveMs;
    private int _wanderSteps;
    private int _consecutiveNoPath;

    public MovementGeneratorType Type => MovementGeneratorType.Random;

    /// <summary>Whether the wander ends by itself (a relay's timed wander: its update returns false and the generator is popped).</summary>
    internal bool Expires => _expiryMs != 0;

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

        if (mover is ICreaturePathQuery { HasPathBudget: false, CanQueuePathJob: false } budgeted)
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
            if (mover.GetHeight(creature.MapId, destX, destY, home.Z) is { } ground
                && float.IsFinite(ground)
                && MathF.Abs(ground - home.Z) <= MaxFallbackLegDropYards)
            {
                destZ = ground;
            }
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
            // A leg queued on the path-job pool is usually answered within an update or two: come back for it then, instead of the
            // budget back-off (which left finished legs unclaimed for up to 10 s and held the per-map job cap full on live, Oct 17).
            _nextMoveMs = mover is ICreaturePathQuery { LastPlanQueued: true } ? PathJobRetryMs
                : mover is ICreaturePathQuery { IsCrowdedRandomPass: true } ? mover.URand(4, 10) * 1000 : mover.URand(NoPathRetryMinMs, NoPathRetryMaxMs);
            return true;
        }

        if (!onMesh && plan.Corners.Count > 0
            && (plan.Corners[^1].Z < MathF.Min(creature.Z, home.Z) - MaxFallbackLegDropYards
                || plan.Corners[^1].Z > MathF.Max(creature.Z, home.Z) + MaxFallbackLegDropYards))
        {
            // A spawn off the mesh draws its point on the terrain (or at its own height where the terrain has a hole), and the search
            // snaps that end to the nearest polygon in a 200 yd tall column (vmangos getPolyByLocation): live 2026-10-18 Lordaeron
            // Citizen 3617 spawn 156011 (ruins, z 70.8, a terrain hole over Undercity) got incomplete legs ending on the Undercity floor
            // at z -44 and walked down through the ground (seen at z 9-13, creature/under-map after every restart). In caves (Grizzled Den)
            // raw terrain is 90 yd above the cave floor. A wander leg never jumps to another storey: skip it like a leg without a path.
            _nextMoveMs = NoPathRetryDelay(mover, ++_consecutiveNoPath);
            return true;
        }

        _consecutiveNoPath = 0;
        IReadOnlyList<Vector3> corners = DrapeCornersOverMeshFloor(creature, plan.Corners);
        mover.MovePath(creature, corners, run, SplineFacing.None);

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

    /// <summary>
    /// How far below both the creature and its wander centre a leg drawn off the mesh may end (vmangos PathFinder's farFromPoly bound,
    /// 7 yd): deeper is a floor below (a cave, a city under the terrain), not a wander point.
    /// </summary>
    internal const float MaxFallbackLegDropYards = 7f;

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

    /// <summary>
    /// Straight 3D chords between coarse polygon corners across an arched cave floor (such as Grizzled Den, where Wendigo 1135
    /// spawns 3564/3574/3576 dipped 2.6–3.2 yd below the cave mesh at z 365.8–368.5 under mesh 368.5–371.7) sink below the mesh
    /// surface. Samples every 4 yards along each segment and raises any point that sits below the local mesh floor.
    /// </summary>
    private static IReadOnlyList<Vector3> DrapeCornersOverMeshFloor(Creature creature, IReadOnlyList<Vector3> corners)
    {
        if (corners.Count == 0 || creature.Map?.Collision is not { } collision)
        {
            return corners;
        }

        List<Vector3>? draped = null;
        var prev = new Vector3(creature.X, creature.Y, creature.Z);
        for (int i = 0; i < corners.Count; i++)
        {
            Vector3 corner = corners[i];
            float dx = corner.X - prev.X;
            float dy = corner.Y - prev.Y;
            float flatDist = MathF.Sqrt((dx * dx) + (dy * dy));
            int steps = (int)MathF.Ceiling(flatDist / 4f);
            if (steps > 1)
            {
                for (int s = 1; s < steps; s++)
                {
                    float t = (float)s / steps;
                    var mid = Vector3.Lerp(prev, corner, t);
                    if (collision.TryGetMeshFloor(mid.X, mid.Y, mid.Z, 4f, 4f, out float midFloor) && midFloor > mid.Z + 0.05f)
                    {
                        if (draped is null)
                        {
                            draped = new List<Vector3>(corners.Count + steps);
                            for (int k = 0; k < i; k++)
                            {
                                draped.Add(corners[k]);
                            }
                        }

                        draped.Add(mid with { Z = midFloor });
                    }
                }
            }

            Vector3 adjusted = corner;
            if (collision.TryGetMeshFloor(corner.X, corner.Y, corner.Z, 4f, 4f, out float floorZ) && floorZ > corner.Z + 0.05f)
            {
                adjusted = corner with { Z = floorZ };
                if (draped is null)
                {
                    draped = new List<Vector3>(corners.Count);
                    for (int k = 0; k < i; k++)
                    {
                        draped.Add(corners[k]);
                    }
                }
            }

            draped?.Add(adjusted);
            prev = adjusted;
        }

        return draped ?? corners;
    }
}
