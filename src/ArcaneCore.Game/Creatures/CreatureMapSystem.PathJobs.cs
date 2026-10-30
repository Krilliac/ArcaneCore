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
/// takes its finished leg whatever new point it drew, since the point is random anyway). Anything that no longer fits is dropped and
/// asked again. The search itself is the same <see cref="ISharedPathSearch"/> query over the same loaded tiles, so the answer is the one
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

    private sealed record PathJobKey(PathOptions Options, bool ToSpawn, bool AnyDestination);

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
        float destinationTolerance = (follow || formation || chasing) ? PathJobFollowTolerance : PathJobDestinationTolerance;
        if (_creaturePathJobs.TryGetValue(creature, out NavJob? job))
        {
            var key = (PathJobKey)job.Tag!;
            bool sameRequest = ReferenceEquals(key.Options, options) && key.ToSpawn == toSpawn && key.AnyDestination == anyDestination
                && Vector3.DistanceSquared(job.Start, start) <= PathJobStartTolerance * PathJobStartTolerance;
            bool done = job.IsDone; // read once: a worker may publish between two reads
            bool destinationFits = anyDestination
                || Vector3.DistanceSquared(job.Destination, destination) <= destinationTolerance * destinationTolerance;

            // A moving target (follow, formation, chase) whose point has drifted past the tolerance: the job is still waited for, and its
            // answer is walked only if the path it found ends closer to the target's new point than the creature stands
            // (LaggingAnswerStillCloses). Dropping it would restart the wait every time the target moves another yard, so a pool slower
            // than that would never answer at all.
            bool lagging = sameRequest && !destinationFits && (follow || formation || chasing);
            if (!done && ((sameRequest && destinationFits) || lagging))
            {
                PathJobWaits++;
                _lastPlanQueued = true;
                plan = PlannedPath.Of(PathOutcome.Deferred);
                return true;
            }

            bool fits = sameRequest
                && (destinationFits || (lagging && job.Status == NavJobStatus.Completed && LaggingAnswerStillCloses(start, job.Result, destination)));

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
                PathJobsApplied++;
                CountJobSearch(creature, job);
                plan = FinishPlannedPath(creature, start, job.Destination, toSpawn, options, result);
                return true;
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
            Tag = new PathJobKey(options, toSpawn, anyDestination),
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
    /// follower would stand until the target stopped. vmangos TargetedMovementGenerator::Update re-aims a moving chaser or follower
    /// synchronously, mid-spline, as soon as the target has moved; this code does not yet: a creature already moving keeps its spline
    /// and its pool answers are dropped (the job start no longer fits), so it re-aims only once that spline ends.
    /// </summary>
    private static bool LaggingAnswerStillCloses(Vector3 start, PathResult? result, Vector3 wanted)
        => result is { HasPath: true } path
            && Vector3.DistanceSquared(path.Points[^1], wanted) < Vector3.DistanceSquared(start, wanted);

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
