using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Creature path searches on the path-job pool (multithreading plan W5, <c>Creatures:Movement:PathJobs</c>). An eligible request
/// (an out-of-combat wander, waypoint, point or scripted move, or an evade/home return of a walker; never a chase, follow, flee,
/// confused, formation, pet or flier search) is queued on <see cref="NavJobSystem"/> and answers <see cref="PathOutcome.Deferred"/>,
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

    private bool TryPlanPathByJob(Creature creature, Vector3 destination, bool toSpawn, bool chase, out PlannedPath plan)
    {
        plan = default;
        if (PathJobs is not { } jobs
            || chase
            || creature.IsPet
            || (creature.Combat.IsInCombat && !toSpawn)
            || creature.Motion.CurrentType is MovementGeneratorType.Chase or MovementGeneratorType.Follow or MovementGeneratorType.Fleeing
                or MovementGeneratorType.Confused or MovementGeneratorType.Formation
            || Map.Collision.PathfinderForMap is not ISharedPathSearch shared)
        {
            return false;
        }

        PathOptions options = PathOptionsFor(creature, toSpawn);
        if (options.Mover is { CanFly: true } || options.LoadTiles || options.NonBlockingTiles || !shared.CanSearchShared(Map.MapId))
        {
            return false;
        }

        var start = new Vector3(creature.X, creature.Y, creature.Z);
        bool anyDestination = !toSpawn && IsWanderLeg(creature);
        if (_creaturePathJobs.TryGetValue(creature, out NavJob? job))
        {
            var key = (PathJobKey)job.Tag!;
            bool fits = ReferenceEquals(key.Options, options) && key.ToSpawn == toSpawn && key.AnyDestination == anyDestination
                && Vector3.DistanceSquared(job.Start, start) <= PathJobStartTolerance * PathJobStartTolerance
                && (anyDestination || Vector3.DistanceSquared(job.Destination, destination) <= PathJobDestinationTolerance * PathJobDestinationTolerance);
            if (fits && !job.IsDone)
            {
                PathJobWaits++;
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

        if (_creaturePathJobs.Count >= Math.Max(1, _options.Movement.PathJobsMaxInFlightPerMap))
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
        plan = PlannedPath.Of(PathOutcome.Deferred);
        return true;
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
        if (_creaturePathJobs.Count == 0 || _clockMs - _lastPathJobSweepMs < 1000)
        {
            LogPathJobSummary();
            return;
        }

        _lastPathJobSweepMs = _clockMs;
        _pathJobSweep.Clear();
        foreach ((Creature creature, NavJob job) in _creaturePathJobs)
        {
            if (!creature.IsAlive || !creature.IsInWorld || !_creatures.ContainsKey(creature.Guid)
                || (job.IsDone && _clockMs - job.SubmittedAtMs > PathJobUnclaimedMs))
            {
                job.Abandon();
                _pathJobSweep.Add(creature);
            }
        }

        foreach (Creature creature in _pathJobSweep)
        {
            _creaturePathJobs.Remove(creature);
            PathJobsStale++;
        }

        _pathJobSweep.Clear();
        LogPathJobSummary();
    }

    private void LogPathJobSummary()
    {
        if (_pathJobs is not { } jobs || _clockMs - _lastPathJobLogMs < 60_000 || PathJobsSubmitted == _lastLoggedSubmitted)
        {
            return;
        }

        _lastPathJobLogMs = _clockMs;
        _lastLoggedSubmitted = PathJobsSubmitted;
        NavJobTotals totals = jobs.Totals;
        NavJobTotals window = totals - _lastPathJobLogTotals;
        _lastPathJobLogTotals = totals;
        double avgMs = window.Completed + window.Canceled > 0 ? window.ComputeTicks * 1000.0 / Stopwatch.Frequency / (window.Completed + window.Canceled) : 0;
        double queueMs = window.Completed + window.Canceled + window.Skipped > 0
            ? window.QueueTicks * 1000.0 / Stopwatch.Frequency / (window.Completed + window.Canceled + window.Skipped) : 0;
        _logger.LogInformation(
            "Creature path jobs on map {MapId}: submitted {Submitted}, applied {Applied}, stale {Stale}, canceled {Canceled}, waits {Waits}, rejected {Rejected}, in flight {InFlight}; pool ({Threads} threads, last minute, all maps): completed {Completed}, canceled {PoolCanceled}, faulted {Faulted}, queued {Queued}, avg search {AvgMs:F2} ms, avg queue wait {QueueMs:F1} ms, slowest search {MaxMs:F1} ms",
            Map.MapId, PathJobsSubmitted, PathJobsApplied, PathJobsStale, PathJobsCanceled, PathJobWaits, PathJobsRejected, _creaturePathJobs.Count,
            jobs.Threads, window.Completed, window.Canceled, window.Faulted, jobs.Queued, avgMs, queueMs, totals.MaxComputeTicks * 1000.0 / Stopwatch.Frequency);
    }
}
