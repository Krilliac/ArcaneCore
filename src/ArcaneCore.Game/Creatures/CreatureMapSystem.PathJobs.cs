using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature path searches on the path-job pool (multithreading plan W5, <c>Creatures:Movement:PathJobs</c>). An eligible request
/// (an out-of-combat wander, waypoint, point or scripted move, an evade/home return of a walker, a chase, or with <c>PathJobsFollow</c>
/// an out-of-combat follow or formation move; never a flee, confused or flier search) is queued on <see cref="NavJobSystem"/> and answers <see cref="PathOutcome.Deferred"/>,
/// exactly as a spent per-update budget does: every motion already waits and asks again. When it asks again and the job has finished,
/// the answer is used if it still fits (the creature has not moved off the job's start, the same destination and options; a wander leg
/// takes its finished leg whatever new point it drew, since the point is random anyway). A follower, formation member or chaser that has
/// only run on along its own spline since it asked still fits: the answer is re-anchored at where the creature now is
/// (<see cref="TryReanchorAtCreature"/>), so a creature moving behind a moving target re-aims mid-spline instead of running its spline
/// out first. Anything that no longer fits is dropped and asked again. The search itself is the same <see cref="ISharedPathSearch"/> query over the same loaded tiles, so the answer is the one
/// the synchronous search would have given; only its timing moves off the map's thread. A full pool or a full per-map in-flight cap
/// falls back to the synchronous, budgeted search.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>How far (yd) a creature may stand from a job's start for the job's path to still apply.</summary>
    internal const float PathJobStartTolerance = 0.5f;

    /// <summary>How far (yd) a re-asked destination may lie from the job's for the job's path to still apply.</summary>
    internal const float PathJobDestinationTolerance = 0.25f;

    /// <summary>
    /// The most outstanding path jobs one map may have; past it a request is searched synchronously under the per-update budget, as
    /// without jobs. Live peaks at ~40-70 in flight per continent (MT-J1 after 680f3cf8), so 512 is head-room, not a tuning knob.
    /// </summary>
    internal const int PathJobsMaxInFlightPerMap = 512;

    /// <summary>How far (yd) a re-asked follow, formation or chase point may lie from the job's for the job's path to still apply.</summary>
    internal const float PathJobFollowTolerance = 1f;

    /// <summary>
    /// How far (yd) a follower, formation member or chaser may have run along its own spline from a job's start for the job's path to
    /// still apply, re-anchored at the creature (<see cref="TryReanchorAtCreature"/>). At run speed (7 yd/s) this is about 570 ms of
    /// pool lag; a slower answer is dropped and asked again from where the creature then is.
    /// </summary>
    internal const float PathJobCarryYards = 4f;

    /// <summary>
    /// How far (yd, 2D) the navmesh walk from the creature toward the re-anchored path's first corner may stop short of it before the
    /// re-anchored path is refused (<see cref="TryReanchorAtCreature"/>): the corner lies behind a wall or off the mover's mesh from here.
    /// </summary>
    internal const float PathJobReanchorSlackYards = 0.5f;

    /// <summary>
    /// How far (yd) the mesh surface the walk ends on may lie above or below the re-anchored first corner (<see cref="TryReanchorAtCreature"/>):
    /// farther, the walk reached the corner's column on another layer (the floor under a bridge the corner is on), and the leg is refused.
    /// </summary>
    internal const float PathJobReanchorStepYards = 2f;

    /// <summary>The most already-passed leading corners a re-anchored path drops (<see cref="TryReanchorAtCreature"/>).</summary>
    internal const int PathJobReanchorMaxSkips = 4;

    /// <summary>
    /// A job still queued or running this long (ms) after it was submitted is dropped and asked again when its creature asks for it, so
    /// a search stuck in a worker cannot hold a creature forever (live queue waits are milliseconds; <see cref="PathJobUnclaimedMs"/>
    /// is the finished-job sweep).
    /// </summary>
    internal const long PathJobPendingTimeoutMs = 5_000;

    /// <summary>A finished job nobody asked for within this long (ms) is dropped by the sweep.</summary>
    internal const long PathJobUnclaimedMs = 10_000;

    private NavJobSystem? _pathJobs;
    private bool _pathJobsResolved;
    private readonly Dictionary<Creature, NavJob> _creaturePathJobs = [];
    private readonly List<Creature> _pathJobSweep = [];
    private long _lastPathJobSweepMs;
    private long _lastPathJobLogMs;
    private NavJobTotals _lastPathJobLogTotals;
    private long _lastLoggedSubmitted;
    private int _pathJobsPending;
    private bool _lastPlanQueued;

    /// <summary>How often (ms) the sweep runs and recounts the jobs still queued or running.</summary>
    internal const long PathJobSweepMs = 250;

    /// <summary>
    /// What a job was asked with. <c>Spline</c> is the spline a follower, formation member or chaser was running when it asked (null
    /// otherwise, or when it stood): only travel along that spline carries the job's start (<see cref="RanOnAlongItsSpline"/>).
    /// </summary>
    private sealed record PathJobKey(PathOptions Options, bool ToSpawn, bool AnyDestination, CreatureSpline? Spline = null);

    /// <summary>
    /// The pool this map's eligible searches go to, or null (synchronous searches only): the process pool when
    /// <c>Creatures:Movement:PathJobs</c> is on (read once), or the one a test sets.
    /// </summary>
    internal NavJobSystem? PathJobs
    {
        get
        {
            if (!_pathJobsResolved)
            {
                _pathJobsResolved = true;
                if (_options.Movement.PathJobs)
                {
                    _pathJobs = NavJobSystem.Shared(_options.Movement.PathJobThreads, _logger);
                }
            }

            return _pathJobs;
        }
        set
        {
            _pathJobs = value;
            _pathJobsResolved = true;
        }
    }

    /// <summary>Jobs queued for this map's creatures (diagnostics, tests).</summary>
    public long PathJobsSubmitted { get; private set; }

    /// <summary>Finished jobs whose path a creature walked.</summary>
    public long PathJobsApplied { get; private set; }

    /// <summary>Jobs dropped because the request had changed (the creature moved or wants another destination) or nobody asked again.</summary>
    public long PathJobsStale { get; private set; }

    /// <summary>Jobs a tile change stopped (asked again).</summary>
    public long PathJobsCanceled { get; private set; }

    /// <summary>Requests that found their job still running.</summary>
    public long PathJobWaits { get; private set; }

    /// <summary>Requests the pool or the per-map cap refused, searched synchronously instead.</summary>
    public long PathJobsRejected { get; private set; }

    /// <summary>Jobs this map has outstanding (running, queued, or finished and not yet asked for).</summary>
    public int PathJobsInFlight => _creaturePathJobs.Count;

    /// <summary>
    /// Jobs still queued or running (recounted by each sweep, plus those submitted since). The per-map cap counts these: a finished job
    /// waiting for its creature to ask again costs no pool time.
    /// </summary>
    public int PathJobsPending => _pathJobsPending;

    /// <summary>
    /// Outstanding jobs (<see cref="PathJobsInFlight"/>) whose answer is published (<see cref="NavJob.IsDone"/>). Map thread (or a
    /// test driving the ticks). A pool counter (<see cref="NavJobSystem.Totals"/>) moves just before its job publishes, so a wait on
    /// the counter alone can still find the job pending on the next update.
    /// </summary>
    internal int PathJobsFinished
    {
        get
        {
            int finished = 0;
            foreach (NavJob job in _creaturePathJobs.Values)
            {
                if (job.IsDone) finished++;
            }

            return finished;
        }
    }

    bool ICreaturePathQuery.CanQueuePathJob => PathJobs is not null && _pathJobsPending < PathJobsMaxInFlightPerMap;

    bool ICreaturePathQuery.LastPlanQueued => _lastPlanQueued;

    private bool TryPlanPathByJob(Creature creature, Vector3 destination, bool toSpawn, bool chase, out PlannedPath plan, ISharedPathSearch? known = null)
    {
        plan = default;
        MovementGeneratorType motion = creature.Motion.CurrentType;
        // An out-of-combat follow (a pet behind its owner, an escort behind its leader) or formation slot move may go to the pool with
        // PathJobsFollow; a chase goes to the pool whenever PathJobs is active.
        bool follow = chase && !toSpawn && _options.Movement.PathJobsFollow && motion == MovementGeneratorType.Follow
            && !creature.Combat.IsInCombat;
        bool formation = !chase && !toSpawn && _options.Movement.PathJobsFollow && motion == MovementGeneratorType.Formation
            && !creature.Combat.IsInCombat;
        bool chasing = chase && !toSpawn && motion == MovementGeneratorType.Chase;
        if (PathJobs is not { } jobs
            || (chase && !follow && !chasing)
            || (creature.IsPet && !follow && !chasing)
            || (creature.Combat.IsInCombat && !toSpawn && !chasing)
            || (!follow && motion is MovementGeneratorType.Follow)
            || (!formation && motion is MovementGeneratorType.Formation)
            || motion is MovementGeneratorType.Fleeing or MovementGeneratorType.Confused
            || (known ?? Map.Collision.PathfinderForMap as ISharedPathSearch) is not { } shared)
        {
            return false;
        }

        PathOptions options = PathOptionsFor(creature, toSpawn, chase);
        if (options.Mover is { CanFly: true } || options.LoadTiles || options.NonBlockingTiles || (known is null && !shared.CanSearchShared(Map.MapId)))
        {
            return false;
        }

        var start = new Vector3(creature.X, creature.Y, creature.Z);
        bool anyDestination = !toSpawn && IsWanderLeg(creature);
        bool tracking = follow || formation || chasing;
        // Taking an answer mid-spline is part of PathJobsFollow (follow and formation need it anyway; a chase only with it), so PathJobs
        // alone keeps a chase's earlier behaviour: an answer whose start the chaser has run past is dropped and asked again.
        bool mayCarry = tracking && _options.Movement.PathJobsFollow;
        float destinationTolerance = tracking ? PathJobFollowTolerance : PathJobDestinationTolerance;
        if (_creaturePathJobs.TryGetValue(creature, out NavJob? job))
        {
            var key = (PathJobKey)job.Tag!;
            bool sameKey = ReferenceEquals(key.Options, options) && key.ToSpawn == toSpawn && key.AnyDestination == anyDestination;
            bool startFits = Vector3.DistanceSquared(job.Start, start) <= PathJobStartTolerance * PathJobStartTolerance;

            // A follower, formation member or chaser running at ~7 yd/s is 0.7 yd past the job's start one 100 ms update later: past
            // PathJobStartTolerance, but only along the spline it was running when it asked. That still fits (the answer is re-anchored
            // at the creature below), and the job is waited for rather than dropped and asked again on every update.
            bool carried = sameKey && !startFits && mayCarry && RanOnAlongItsSpline(creature, key.Spline, job.Start, start);
            bool sameRequest = sameKey && (startFits || carried);
            bool done = job.IsDone; // read once: a worker may publish between two reads
            bool overdue = !done && _clockMs - job.SubmittedAtMs > PathJobPendingTimeoutMs;
            bool destinationFits = anyDestination
                || Vector3.DistanceSquared(job.Destination, destination) <= destinationTolerance * destinationTolerance;

            // A moving target (follow, formation, chase) whose point has drifted past the tolerance: the job is still waited for, and its
            // answer is walked only if the path it found ends closer to the target's new point than the creature stands
            // (LaggingAnswerStillCloses). Dropping it would restart the wait every time the target moves another yard, so a pool slower
            // than that would never answer at all.
            bool lagging = sameRequest && !destinationFits && (follow || formation || chasing);
            if (!done && !overdue && ((sameRequest && destinationFits) || lagging))
            {
                PathJobWaits++;
                _lastPlanQueued = true;
                plan = PlannedPath.Of(PathOutcome.Deferred);
                return true;
            }

            // A carried answer without a path is not applied either: no path from a point the creature has already run past says
            // nothing about where it stands now (on a chase it would feed the unreachable-target evade). It is asked again from here.
            bool fits = !overdue
                && sameRequest
                && (destinationFits || (lagging && job.Status == NavJobStatus.Completed && LaggingAnswerStillCloses(start, job.Result, destination)))
                && (!carried || job.Status != NavJobStatus.Completed || job.Result is { HasPath: true });

            if (fits && job.Status == NavJobStatus.Completed && job.Result is not null && !HasDrapeBudget)
            {
                // The update's path time is spent (the searches and drapes before this one): the result waits, with its creature, for the
                // next update. The job stays; the creature that waited longest is where the next update's creature walk starts.
                NoteDeferredPath(creature);
                _lastPlanQueued = true;
                plan = PlannedPath.Of(PathOutcome.Deferred);
                return true;
            }

            _creaturePathJobs.Remove(creature);
            if (fits && job.Status == NavJobStatus.Completed && job.Result is { } result)
            {
                if (!carried || TryReanchorAtCreature(creature, job.Start, start, result, options, out result))
                {
                    PathJobsApplied++;
                    CountJobSearch(creature, job);
                    plan = FinishPlannedPath(creature, start, job.Destination, toSpawn, options, result);
                    return true;
                }

                fits = false; // the re-anchored first leg is blocked from here: asked again from where the creature stands
            }

            if (!fits)
            {
                job.Abandon();
                PathJobsStale++;
            }
            else
            {
                PathJobsCanceled++;
            }
        }

        if (_pathJobsPending >= PathJobsMaxInFlightPerMap)
        {
            PathJobsRejected++;
            return false;
        }

        var next = new NavJob(shared, Map.MapId, start, destination, options, urgent: !anyDestination)
        {
            Tag = new PathJobKey(options, toSpawn, anyDestination, mayCarry ? creature.Spline : null),
            SubmittedAtMs = _clockMs,
        };
        if (!jobs.TrySubmit(next))
        {
            PathJobsRejected++;
            return false;
        }

        _creaturePathJobs[creature] = next;
        PathJobsSubmitted++;
        _pathJobsPending++;
        _lastPlanQueued = true;
        plan = PlannedPath.Of(PathOutcome.Deferred);
        return true;
    }

    /// <summary>
    /// A finished follow, formation or chase answer whose target has since moved past <see cref="PathJobFollowTolerance"/> is still
    /// walked when the path it found ends closer to the target's new point than the creature stands. What counts is where the path
    /// ends, not the point it was asked for: a partial path may stop farther from the target than the creature already is. A lagging
    /// answer without a path is never applied (it would report a stale point unreachable, which on a chase feeds the unreachable-target
    /// evade): it is dropped and asked again. Dropping every lagging answer would starve the follower whenever the pool answers slower
    /// than the target moves 1 yd (an owner running 7 yd/s with answers two 100 ms updates late): every answer would be stale and the
    /// follower would stand until the target stopped. A creature already moving takes such an answer mid-spline, re-anchored at where
    /// it now stands (<see cref="TryReanchorAtCreature"/>); <paramref name="start"/> is that position.
    /// </summary>
    private static bool LaggingAnswerStillCloses(Vector3 start, PathResult? result, Vector3 wanted)
        => result is { HasPath: true } path
            && Vector3.DistanceSquared(path.Points[^1], wanted) < Vector3.DistanceSquared(start, wanted);

    /// <summary>
    /// Whether the creature got from <paramref name="jobStart"/> to <paramref name="here"/> only by running the spline it ran when the job
    /// was asked (<paramref name="spline"/>): it is still on that spline, or the spline has run out and the creature stands at its end.
    /// No farther than <see cref="PathJobCarryYards"/>. A creature stopped part-way, put elsewhere (a knockback, a teleport) or sent on
    /// another spline is not carried: its answer is dropped as before.
    /// </summary>
    private bool RanOnAlongItsSpline(Creature creature, CreatureSpline? spline, Vector3 jobStart, Vector3 here)
    {
        if (spline is null || Vector3.DistanceSquared(jobStart, here) > PathJobCarryYards * PathJobCarryYards)
        {
            return false;
        }

        if (ReferenceEquals(creature.Spline, spline))
        {
            return true;
        }

        return creature.Spline is null && spline.IsFinished(_clockMs)
            && Vector3.DistanceSquared(new Vector3(spline.EndX, spline.EndY, spline.EndZ), here) <= PathJobStartTolerance * PathJobStartTolerance;
    }

    /// <summary>
    /// A path found from <paramref name="jobStart"/>, a point the creature has since run past (<see cref="RanOnAlongItsSpline"/>),
    /// re-anchored at where it now stands: the job's start is replaced by <paramref name="here"/> and the creature heads for the path's
    /// first corner still ahead, then on along the path (vmangos TargetedMovementGenerator::Update re-aims a moving chaser or follower
    /// from its current position, mid-spline). A leading corner the creature has already passed is dropped (at most
    /// <see cref="PathJobReanchorMaxSkips"/>): one on the stretch of its own spline it ran since the job's start, or one whose next corner
    /// it can walk to straight from here; otherwise it would turn back to it.
    /// <para>
    /// The new first leg was not part of the search, so on a map with navigation it is walked on the navmesh with the job's own options
    /// (<paramref name="options"/>: the mover's include flags, and no slope exclusion for a follow, formation or chase path, so a creature
    /// on a ramp is placed on the ramp's polygon). The leg is refused when the creature cannot be placed on the mesh (off it, its tile
    /// not loaded), when the walk stops more than <see cref="PathJobReanchorSlackYards"/> short of the corner, or when it ends on a surface
    /// more than <see cref="PathJobReanchorStepYards"/> above or below the corner (another layer). Only a map without a mesh query at all
    /// (no navigation: its paths are straight lines) takes the leg unchecked. The caller puts the result on the ground and smooths it as
    /// it does a path searched from here (<see cref="FinishPlannedPath"/>).
    /// </para>
    /// </summary>
    private bool TryReanchorAtCreature(Creature creature, Vector3 jobStart, Vector3 here, PathResult found, PathOptions options, out PathResult reanchored)
    {
        reanchored = found;
        if (_ungatedMotion)
        {
            return false; // follow, formation and chase never run outside the gate; refuse rather than read the mesh unguarded
        }

        IMeshPointQuery? mesh = Map.Collision.MeshPoints;
        IReadOnlyList<Vector3> points = found.Points;
        int last = points.Count - 1;
        int first = 1;
        for (int skipped = 0; first < last && skipped < PathJobReanchorMaxSkips; skipped++)
        {
            Vector3 corner = points[first];
            bool passed = Vector3.DistanceSquared(corner, here) <= TargetedMovementGenerator.ArrivedEpsilon * TargetedMovementGenerator.ArrivedEpsilon
                || OnStretchRun(corner, jobStart, here)
                || (mesh is not null && LegWalks(mesh, creature, here, points[first + 1], options));
            if (!passed)
            {
                break;
            }

            first++;
        }

        if (mesh is not null && !LegWalks(mesh, creature, here, points[first], options))
        {
            return false;
        }

        var spliced = new List<Vector3>(points.Count - first + 1) { here };
        for (int i = first; i < points.Count; i++)
        {
            spliced.Add(points[i]);
        }

        reanchored = found with { Points = spliced };
        return true;
    }

    /// <summary>
    /// Whether <paramref name="corner"/> lies on the stretch the creature ran since the job's start (within
    /// <see cref="PathJobStartTolerance"/> of the segment from <paramref name="jobStart"/> to <paramref name="here"/>; at most
    /// <see cref="PathJobCarryYards"/> of its own spline, taken as straight).
    /// </summary>
    private static bool OnStretchRun(Vector3 corner, Vector3 jobStart, Vector3 here)
    {
        Vector3 run = here - jobStart;
        float length = run.LengthSquared();
        float t = length > 1e-6f ? Math.Clamp(Vector3.Dot(corner - jobStart, run) / length, 0f, 1f) : 0f;
        return Vector3.DistanceSquared(jobStart + (run * t), corner) <= PathJobStartTolerance * PathJobStartTolerance;
    }

    /// <summary>
    /// Whether the creature can walk straight from <paramref name="from"/> to <paramref name="to"/> on the navmesh with its path's
    /// <paramref name="options"/> (see <see cref="TryReanchorAtCreature"/>): the start is placed on the mesh, the walk ends within
    /// <see cref="PathJobReanchorSlackYards"/> of the point (2D) and on a surface within <see cref="PathJobReanchorStepYards"/> of its
    /// height. The walk's end point is the surface of the polygon it ends on, at the point's column (<c>NavMeshQuery.Raycast</c>).
    /// </summary>
    private static bool LegWalks(IMeshPointQuery mesh, Creature creature, Vector3 from, Vector3 to, PathOptions options)
    {
        MeshRaycastResult walk = mesh.Raycast(creature.MapId, from, to, options);
        return walk.Available
            && Vector2.DistanceSquared(new Vector2(walk.Point.X, walk.Point.Y), new Vector2(to.X, to.Y)) <= PathJobReanchorSlackYards * PathJobReanchorSlackYards
            && MathF.Abs(walk.Point.Z - to.Z) <= PathJobReanchorStepYards;
    }

    /// <summary>A job's search in this map's counters (not in the per-update budget: it did not run on this thread).</summary>
    private void CountJobSearch(Creature creature, NavJob job)
    {
        _timing.JobSearches++;
        _timing.JobSearchTicks += job.ComputeTicks;
        if (job.ComputeTicks > _timing.SlowestJobSearchTicks)
        {
            _timing.SlowestJobSearchTicks = job.ComputeTicks;
        }

        _ = creature;
    }

    /// <summary>
    /// Drop the jobs of creatures that died, left the map or stopped asking (once a second), and write the summary line once a minute
    /// while jobs run. Start of the update.
    /// </summary>
    private void SweepPathJobs()
    {
        if (_creaturePathJobs.Count == 0)
        {
            _pathJobsPending = 0;
            LogPathJobSummary();
            return;
        }

        if (_clockMs - _lastPathJobSweepMs < PathJobSweepMs)
        {
            LogPathJobSummary();
            return;
        }

        _lastPathJobSweepMs = _clockMs;
        _pathJobSweep.Clear();
        int pending = 0;
        foreach ((Creature creature, NavJob job) in _creaturePathJobs)
        {
            if (!job.IsDone)
            {
                pending++;
            }

            if (!creature.IsAlive || !creature.IsInWorld || !_creatures.ContainsKey(creature.Guid)
                || (job.IsDone && _clockMs - job.SubmittedAtMs > PathJobUnclaimedMs))
            {
                job.Abandon();
                _pathJobSweep.Add(creature);
            }
        }

        foreach (Creature creature in _pathJobSweep)
        {
            if (_creaturePathJobs.Remove(creature, out NavJob? dropped) && !dropped.IsDone)
            {
                pending--;
            }

            PathJobsStale++;
        }

        _pathJobsPending = Math.Max(0, pending);

        _pathJobSweep.Clear();
        LogPathJobSummary();
    }

    private void LogPathJobSummary()
    {
        if (_pathJobs is not { } jobs || _clockMs - _lastPathJobLogMs < 60_000 || PathJobsSubmitted == _lastLoggedSubmitted && WanderJobsSubmitted == _lastLoggedWanderSubmitted)
        {
            return;
        }

        _lastPathJobLogMs = _clockMs;
        _lastLoggedSubmitted = PathJobsSubmitted;
        _lastLoggedWanderSubmitted = WanderJobsSubmitted;
        NavJobTotals totals = jobs.Totals;
        NavJobTotals window = totals - _lastPathJobLogTotals;
        _lastPathJobLogTotals = totals;
        double avgMs = window.Completed + window.Canceled > 0 ? window.ComputeTicks * 1000.0 / Stopwatch.Frequency / (window.Completed + window.Canceled) : 0;
        double queueMs = window.Completed + window.Canceled + window.Skipped > 0
            ? window.QueueTicks * 1000.0 / Stopwatch.Frequency / (window.Completed + window.Canceled + window.Skipped) : 0;
        _logger.LogInformation(
            "Creature path jobs on map {MapId}: submitted {Submitted}, applied {Applied}, stale {Stale}, canceled {Canceled}, waits {Waits}, rejected {Rejected}, in flight {InFlight} ({Pending} pending); pool ({Threads} threads, last minute, all maps): completed {Completed}, canceled {PoolCanceled}, faulted {Faulted}, queued {Queued}, avg search {AvgMs:F2} ms, avg queue wait {QueueMs:F1} ms, slowest search {MaxMs:F1} ms; wander jobs: submitted {WSubmitted}, applied {WApplied}, stale {WStale}, canceled {WCanceled}, waits {WWaits}, throttled {WThrottled}, no point {WNoPoint}, rejected {WRejected}, in flight {WInFlight}",
            Map.MapId, PathJobsSubmitted, PathJobsApplied, PathJobsStale, PathJobsCanceled, PathJobWaits, PathJobsRejected, _creaturePathJobs.Count, _pathJobsPending,
            jobs.Threads, window.Completed, window.Canceled, window.Faulted, jobs.Queued, avgMs, queueMs, totals.MaxComputeTicks * 1000.0 / Stopwatch.Frequency,
            WanderJobsSubmitted, WanderJobsApplied, WanderJobsStale, WanderJobsCanceled, WanderJobWaits, WanderJobsThrottled, WanderJobsNoPoint, WanderJobsRejected, _wanderJobs.Count);
    }
}
