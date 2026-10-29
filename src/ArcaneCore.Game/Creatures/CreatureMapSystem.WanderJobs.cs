using System.Numerics;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A wander leg's random-point pick and its path search as one job on the path-job pool (<c>Creatures:Movement:PathJobsWander</c>, with
/// <c>PathJobs</c> on). The generator (<see cref="RandomMovementGenerator"/>) asks <see cref="ICreaturePathQuery.TryWanderByJob"/> when
/// its timer has run out; the first ask submits a <see cref="NavJob"/> that carries a <see cref="NavJobPointPick"/>, and the creature idles
/// (no spline, no unit writes) while the worker picks the point and searches the path from the creature's position. Later asks find the job
/// still running (<see cref="WanderJobAnswer.Waiting"/>), held back by the in-flight cap (<see cref="WanderJobAnswer.Throttled"/>), or
/// finished; a finished job's path is draped and handed to the generator on this map's own thread
/// (<see cref="WanderJobAnswer.Applied"/>; the drape and every unit write stay here). A mesh with no point in reach answers
/// <see cref="WanderJobAnswer.NoMeshPoint"/> (the generator takes its disc fallback), and an unknown mesh or a faulted job answers
/// <see cref="WanderJobAnswer.NotQueued"/> (the synchronous route for that leg).
/// <para>
/// Determinism: with the feature off (PathJobs off, PathJobsWander off, or no shared mesh) the map RNG's draws and the outcomes are exactly
/// those of the synchronous route. With it on, a leg costs the map RNG ONE draw, the seed, at submit; the point is a pure function of
/// (seed, centre, radius, loaded navmesh), the answer <c>TryGetRandomPointAroundShared</c> gives with <c>new Random(seed)</c>. The same map
/// seed and the same tile state therefore give the same legs when each job is applied on the same update (as a test that waits for the
/// pool each tick does). In a live server worker timing can move a leg's apply update, and with it the later draws from the map RNG: the
/// step, pause and run draws and the next legs' seed draws happen at apply, on the shared map RNG, among other creatures' draws.
/// </para>
/// <para>
/// Timing, the one-tick shift: the leg the synchronous route starts on tick T (the timer ran out) is submitted on T and applied on the
/// first update after the worker published it: T+1 when the worker finished before the next update (the usual case), later when it did not
/// (counted in <see cref="WanderJobWaits"/>). The creature stands still meanwhile.
/// </para>
/// <para>
/// Ownership: the job dictionary is read and written on the map thread only (by <see cref="ICreaturePathQuery.TryWanderByJob"/> and the
/// sweep); the generator's <see cref="RandomMovementGenerator.Epoch"/> tells a job of an earlier run from the current one, so
/// Interrupt and Finish never touch the dictionary. The worker sees only the job's values.
/// </para>
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>The default for <see cref="WanderJobsMaxInFlight"/>.</summary>
    internal const int WanderJobsMaxInFlightPerMapDefault = 128;

    private readonly Dictionary<Creature, NavJob> _wanderJobs = [];
    private readonly List<Creature> _wanderJobSweep = [];
    private long _lastWanderSweepMs;
    private long _lastLoggedWanderSubmitted;

    private sealed record WanderJobKey(RandomMovementGenerator Generator, int Epoch, Vector3 Start, PathOptions Options, Vector3 Center, float Radius);

    /// <summary>Wander jobs submitted to the pool.</summary>
    public long WanderJobsSubmitted { get; private set; }

    /// <summary>Wander jobs whose plan was applied.</summary>
    public long WanderJobsApplied { get; private set; }

    /// <summary>Wander jobs dropped because their creature or generator no longer fit.</summary>
    public long WanderJobsStale { get; private set; }

    /// <summary>Wander jobs the pool canceled (a tile change).</summary>
    public long WanderJobsCanceled { get; private set; }

    /// <summary>Asks that found the job still running.</summary>
    public long WanderJobWaits { get; private set; }

    /// <summary>Requests held back because the in-flight cap was full.</summary>
    public long WanderJobsThrottled { get; private set; }

    /// <summary>Jobs that found no mesh point within reach.</summary>
    public long WanderJobsNoPoint { get; private set; }

    /// <summary>Requests the pool refused (the leg took the synchronous route).</summary>
    public long WanderJobsRejected { get; private set; }

    /// <summary>Outstanding wander jobs (queued, running, or finished and not yet claimed).</summary>
    public int WanderJobsInFlight => _wanderJobs.Count;

    /// <summary>Outstanding wander jobs whose answer is published. Map thread (or a test driving the ticks).</summary>
    internal int WanderJobsFinished
    {
        get
        {
            int finished = 0;
            foreach (NavJob job in _wanderJobs.Values)
            {
                if (job.IsDone) finished++;
            }

            return finished;
        }
    }

    /// <summary>
    /// The most outstanding wander jobs this map may have; a test lowers it. Unlike <c>PathJobsMaxInFlightPerMap</c>, which counts pending
    /// jobs, this cap counts finished-but-unclaimed jobs too; a stunned or casting creature holds its slot until it asks again or the
    /// 10 s unclaimed sweep drops the job.
    /// </summary>
    internal int WanderJobsMaxInFlight { get; set; } = WanderJobsMaxInFlightPerMapDefault;

    WanderJobAnswer ICreaturePathQuery.TryWanderByJob(Creature creature, RandomMovementGenerator generator, Vector3 center, float radius, out PlannedPath plan)
    {
        plan = default;
        _lastPlanQueued = false;
        if (!_options.Movement.PathJobsWander
            || !(_options.Movement.PathJobs || _pathJobsResolved)
            || PathJobs is not { } jobs
            || radius < 0.1f
            || creature.IsPet
            || creature.Combat.IsInCombat
            || creature.IsEvading
            || !ReferenceEquals(creature.Motion.Top, generator)
            || _creaturePathJobs.ContainsKey(creature))
        {
            return WanderJobAnswer.NotQueued;
        }

        PathOptions options = PathOptionsFor(creature, toSpawn: false);
        if (options.Mover is { CanFly: true } || options.LoadTiles || options.NonBlockingTiles)
        {
            return WanderJobAnswer.NotQueued;
        }

        ISharedMeshPointQuery? points;
        ISharedPathSearch? search;
        if (_ungatedMotion)
        {
            // Owner-only lookups were made in PrepareUngatedMotion; none are repeated here.
            if (_ungatedNoMesh || !_ungatedCanSearch)
            {
                return WanderJobAnswer.NotQueued;
            }

            points = _ungatedMeshPoints;
            search = _ungatedPathSearch;
        }
        else
        {
            points = Map.Collision.MeshPoints as ISharedMeshPointQuery;
            search = Map.Collision.PathfinderForMap as ISharedPathSearch;
            if (search is null || !search.CanSearchShared(Map.MapId))
            {
                return WanderJobAnswer.NotQueued;
            }
        }

        if (points is null || search is null)
        {
            return WanderJobAnswer.NotQueued;
        }

        var start = new Vector3(creature.X, creature.Y, creature.Z);
        if (_wanderJobs.TryGetValue(creature, out NavJob? job))
        {
            var key = (WanderJobKey)job.Tag!;
            if (WanderJobStale(creature, job, key)
                || Vector3.DistanceSquared(key.Start, start) > PathJobStartTolerance * PathJobStartTolerance
                || !ReferenceEquals(key.Options, options)
                || key.Center != center
                || key.Radius != radius)
            {
                job.Abandon();
                _wanderJobs.Remove(creature);
                WanderJobsStale++;
            }
            else if (!job.IsDone)
            {
                WanderJobWaits++;
                return WanderJobAnswer.Waiting;
            }
            else if (job.Status == NavJobStatus.Completed && job.PickOutcome == NavJobPickOutcome.Picked && job.Result is { } result)
            {
                if (!HasDrapeBudget)
                {
                    // The update's path time is spent: the result waits, with its creature, for the next update.
                    NoteDeferredPath(creature);
                    return WanderJobAnswer.Waiting;
                }

                _wanderJobs.Remove(creature);
                WanderJobsApplied++;
                CountJobSearch(creature, job);
                plan = FinishPlannedPath(creature, start, job.Destination, toSpawn: false, key.Options, result);
                return WanderJobAnswer.Applied;
            }
            else if (job.Status == NavJobStatus.Completed && job.PickOutcome == NavJobPickOutcome.NoPoint)
            {
                _wanderJobs.Remove(creature);
                WanderJobsNoPoint++;
                return WanderJobAnswer.NoMeshPoint;
            }
            else if (job.Status == NavJobStatus.Canceled)
            {
                _wanderJobs.Remove(creature);
                WanderJobsCanceled++;
            }
            else
            {
                // MeshUnknown or Faulted: this leg takes the synchronous route.
                _wanderJobs.Remove(creature);
                return WanderJobAnswer.NotQueued;
            }
        }

        if (_wanderJobs.Count >= WanderJobsMaxInFlight)
        {
            WanderJobsThrottled++;
            return WanderJobAnswer.Throttled;
        }

        int seed = _random.Next();
        var pick = new NavJobPointPick(points, center, radius, seed, MeshPointOptions);
        var next = new NavJob(search, Map.MapId, start, pick, options)
        {
            Tag = new WanderJobKey(generator, generator.Epoch, start, options, center, radius),
            SubmittedAtMs = _clockMs,
        };
        if (!jobs.TrySubmit(next))
        {
            WanderJobsRejected++;
            return WanderJobAnswer.NotQueued;
        }

        _wanderJobs[creature] = next;
        WanderJobsSubmitted++;
        return WanderJobAnswer.Waiting;
    }

    /// <summary>Whether a wander job no longer fits its creature or generator (the start position is checked separately).</summary>
    private bool WanderJobStale(Creature creature, NavJob job, WanderJobKey key)
        => !creature.IsAlive || !creature.IsInWorld || !_creatures.ContainsKey(creature.Guid) || creature.MapId != job.MapId
            || creature.Combat.IsInCombat || creature.IsEvading || !ReferenceEquals(creature.Motion.Top, key.Generator)
            || key.Generator.Epoch != key.Epoch;

    /// <summary>
    /// Drops the wander jobs of creatures that died, left the map, entered combat, evaded or changed generator, and the finished ones nobody
    /// claimed within <see cref="PathJobUnclaimedMs"/>, so their cap slots free up. At most every <see cref="PathJobSweepMs"/>.
    /// </summary>
    private void SweepWanderJobs()
    {
        if (_wanderJobs.Count == 0 || _clockMs - _lastWanderSweepMs < PathJobSweepMs)
        {
            return;
        }

        _lastWanderSweepMs = _clockMs;
        _wanderJobSweep.Clear();
        foreach ((Creature creature, NavJob job) in _wanderJobs)
        {
            if (WanderJobStale(creature, job, (WanderJobKey)job.Tag!) || (job.IsDone && _clockMs - job.SubmittedAtMs > PathJobUnclaimedMs))
            {
                job.Abandon();
                _wanderJobSweep.Add(creature);
            }
        }

        foreach (Creature creature in _wanderJobSweep)
        {
            _wanderJobs.Remove(creature);
            WanderJobsStale++;
        }

        _wanderJobSweep.Clear();
    }

    /// <summary>Measurement hook: when true, <see cref="RandomMotionTicksTotal"/> and <see cref="RandomMotionCalls"/> accumulate.</summary>
    internal bool TimeRandomMotion { get; set; }

    /// <summary>Stopwatch ticks spent in random motion updates (while <see cref="TimeRandomMotion"/> or map diagnostics is on).</summary>
    internal long RandomMotionTicksTotal { get; private set; }

    /// <summary>Random motion updates timed into <see cref="RandomMotionCalls"/>.</summary>
    internal long RandomMotionCalls { get; private set; }
}
