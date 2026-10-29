using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// A pathfinder whose loaded navigation data other threads may search while its owner keeps loading and releasing tiles
/// (<see cref="MMaps.NavMeshPathfinder"/>). The owner's tile changes wait for those searches, and ask them to stop first.
/// </summary>
public interface ISharedPathSearch
{
    /// <summary>
    /// Whether <paramref name="mapId"/> has navigation data a <see cref="FindPathShared"/> call can search. Owner thread: it reads the map's
    /// navmesh parameters on first use, which a shared search never does.
    /// </summary>
    bool CanSearchShared(uint mapId);

    /// <summary>
    /// The path a <see cref="IPathfinder.FindPath"/> of the owner would answer with <paramref name="options"/>, over the tiles loaded
    /// right now. Any thread. No tile is read (<see cref="PathOptions.LoadTiles"/> is refused) and no collision model is consulted, so a
    /// flier's options are refused too. Throws <see cref="OperationCanceledException"/> when the owner needs the data for a tile change
    /// (the caller asks again later).
    /// </summary>
    PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options);
}

/// <summary>What <see cref="ISharedTiledPathSearch.ResolveSharedMisses"/> found for the tiles a shared search wanted and did not have.</summary>
public enum SharedMissVerdict
{
    /// <summary>Every missed tile has no data or is over the query's bounds: the shared answer is the verdict.</summary>
    None = 0,

    /// <summary>A missed tile is still being read: the answer may be only for want of it.</summary>
    Pending = 1,

    /// <summary>A missed tile is loaded now: the shared answer is out of date and the search should run again.</summary>
    Changed = 2,
}

/// <summary>
/// An <see cref="ISharedPathSearch"/> whose owner can also read the navigation tiles a detached search missed, without blocking
/// (the bots' path jobs: their queries cross tiles that no grid holds). Tile reads stay the owner's; the shared search only reports.
/// </summary>
public interface ISharedTiledPathSearch : ISharedPathSearch
{
    /// <summary>
    /// Owner thread: non-blocking tile read for both endpoints (stamps the on-demand tiles' use). False when an endpoint's tile is
    /// still being read (<see cref="PathSearchCounters.TilesPending"/> is bumped, as <see cref="IPathfinder.FindPath"/> does).
    /// </summary>
    bool PrepareShared(uint mapId, Vector3 start, Vector3 end);

    /// <summary>
    /// <see cref="ISharedPathSearch.FindPathShared(uint, Vector3, Vector3, PathOptions)"/>, adding to <paramref name="missedTiles"/> each
    /// Detour tile (x, y) a neighbour expansion wanted and found unloaded. Any thread; the list belongs to the caller.
    /// </summary>
    PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options, List<(int X, int Y)> missedTiles);

    /// <summary>
    /// Owner thread: for at most <see cref="MMaps.NavMeshPathfinder.MaxTilesPerQuery"/> of the missed tiles, skips those known to have no
    /// data, reports one loaded since the search ran as <see cref="SharedMissVerdict.Changed"/>, and otherwise starts or polls its
    /// non-blocking read (installing a finished one: <see cref="SharedMissVerdict.Changed"/>; one still open:
    /// <see cref="SharedMissVerdict.Pending"/>, with <see cref="PathSearchCounters.TilesPending"/> bumped).
    /// </summary>
    SharedMissVerdict ResolveSharedMisses(uint mapId, IReadOnlyList<(int X, int Y)> missedTiles);
}

/// <summary>
/// Mesh point queries a map thread may make outside the world's serial gate (parallel maps phase: creature wander legs). They read only
/// what is loaded, under the pathfinder's shared read lock, like <see cref="ISharedPathSearch.FindPathShared"/>.
/// </summary>
public interface ISharedMeshPointQuery
{
    /// <summary>
    /// <see cref="IMeshPointQuery.TryGetRandomPointAround"/> from any thread, with the same answer and the same draws from
    /// <paramref name="random"/>; null (nothing drawn) when the owner has not looked the map's mesh up yet, so the caller must ask the
    /// owner instead.
    /// </summary>
    bool? TryGetRandomPointAroundShared(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point);

    /// <summary>
    /// <see cref="IMeshPointQuery.Raycast"/> from any thread over the loaded tiles; null when the owner has not looked the map's mesh up
    /// yet, so the caller must ask the owner instead.
    /// </summary>
    MeshRaycastResult? RaycastShared(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null);
}

/// <summary>
/// The mesh floor query (<see cref="IMeshFloorQuery"/>) a map thread may make outside the world's serial gate: over the loaded tiles, under
/// the pathfinder's shared read lock, like <see cref="ISharedMeshPointQuery"/>.
/// </summary>
public interface ISharedMeshFloorQuery
{
    /// <summary>
    /// <see cref="IMeshFloorQuery.TryGetMeshFloor"/> from any thread with the same answer; null when the owner has not looked the map's
    /// mesh up yet, or the map has none (the owner's answer then comes from the fallback pathfinder), so the caller must ask the owner.
    /// </summary>
    bool? TryGetMeshFloorShared(uint mapId, float x, float y, float z, float below, float above, out float height);
}

/// <summary>How a <see cref="NavJob"/> ended.</summary>
public enum NavJobStatus
{
    Pending = 0,
    Completed = 1,
    Canceled = 2,
    Faulted = 3,
}

/// <summary>
/// One detached path query: values only (map id, endpoints, options) and the pathfinder to search. The worker never sees a creature, map
/// or session. The owner polls <see cref="IsDone"/> on its own thread and validates the answer before it uses it (a result that no
/// longer fits the request is dropped, never applied).
/// </summary>
public sealed class NavJob
{
    private int _status;
    private int _abandoned;

    private static readonly List<(int X, int Y)> NoMissedTiles = [];
    private readonly List<(int X, int Y)>? _missed;

    public NavJob(ISharedPathSearch pathfinder, uint mapId, Vector3 start, Vector3 destination, PathOptions options, bool urgent = false,
        bool trackMissedTiles = false)
    {
        ArgumentNullException.ThrowIfNull(pathfinder);
        ArgumentNullException.ThrowIfNull(options);
        Pathfinder = pathfinder;
        MapId = mapId;
        Start = start;
        Destination = destination;
        Options = options;
        Urgent = urgent;
        TrackMissedTiles = trackMissedTiles && pathfinder is ISharedTiledPathSearch;
        if (TrackMissedTiles) _missed = [];
    }

    /// <summary>The worker reports the navigation tiles its search wanted and did not have (<see cref="MissedTiles"/>); needs an <see cref="ISharedTiledPathSearch"/>.</summary>
    public bool TrackMissedTiles { get; }

    /// <summary>Detour tiles the search wanted and found unloaded; valid once the job is done.</summary>
    public IReadOnlyList<(int X, int Y)> MissedTiles => _missed ?? NoMissedTiles;

    internal List<(int X, int Y)> MissedList => _missed ?? throw new InvalidOperationException("the job tracks no missed tiles");

    public ISharedPathSearch Pathfinder { get; }

    public uint MapId { get; }

    public Vector3 Start { get; }

    public Vector3 Destination { get; }

    public PathOptions Options { get; }

    /// <summary>Served before every queued non-urgent job (evade/home returns, scripted moves).</summary>
    public bool Urgent { get; }

    /// <summary>Owner-side tag: what the request was for (the owner's own matching data). Never read by a worker.</summary>
    public object? Tag { get; set; }

    /// <summary>Owner-side stamp (the owner's clock when it was submitted). Never read by a worker.</summary>
    public long SubmittedAtMs { get; set; }

    public NavJobStatus Status => (NavJobStatus)Volatile.Read(ref _status);

    public bool IsDone => Volatile.Read(ref _status) != (int)NavJobStatus.Pending;

    /// <summary>The answer (when <see cref="Status"/> is <see cref="NavJobStatus.Completed"/>). Its point list is the job's own.</summary>
    public PathResult? Result { get; private set; }

    /// <summary>The worker's search counters for this query.</summary>
    public PathSearchTotals Search { get; private set; }

    /// <summary>Wall time the worker spent on the query (<see cref="Stopwatch"/> ticks).</summary>
    public long ComputeTicks { get; private set; }

    /// <summary>Time the job waited in the queue before a worker took it (<see cref="Stopwatch"/> ticks).</summary>
    public long QueueTicks { get; private set; }

    internal long EnqueuedAt { get; set; }

    /// <summary>The owner no longer wants the answer: a worker that has not started it skips it.</summary>
    public void Abandon() => Volatile.Write(ref _abandoned, 1);

    internal bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

    internal void Finish(NavJobStatus status, PathResult? result, PathSearchTotals search, long computeTicks, long queueTicks)
    {
        Result = result;
        Search = search;
        ComputeTicks = computeTicks;
        QueueTicks = queueTicks;
        Volatile.Write(ref _status, (int)status); // publishes the fields above
    }
}

/// <summary>Counters of a <see cref="NavJobSystem"/> (diagnostics, tests, the periodic summary line).</summary>
public readonly record struct NavJobTotals(long Submitted, long Rejected, long Completed, long Canceled, long Faulted, long Skipped, long ComputeTicks, long QueueTicks, long MaxComputeTicks)
{
    public static NavJobTotals operator -(NavJobTotals a, NavJobTotals b)
        => new(a.Submitted - b.Submitted, a.Rejected - b.Rejected, a.Completed - b.Completed, a.Canceled - b.Canceled, a.Faulted - b.Faulted,
            a.Skipped - b.Skipped, a.ComputeTicks - b.ComputeTicks, a.QueueTicks - b.QueueTicks, a.MaxComputeTicks);
}

/// <summary>
/// A bounded pool of dedicated path-search threads (multithreading plan W5: the generalized <c>PlayerbotNavigationExecutor</c>). Owners
/// submit <see cref="NavJob"/>s and poll them on their own thread (Albion / async-navigation pattern: compute off-thread, validate and
/// apply on the owner). Two lanes: urgent jobs first, then the rest in submission order. A full queue refuses the job and the owner keeps
/// its synchronous, budgeted path. Workers run below the world thread's priority so they never take its core under host load.
/// </summary>
public sealed class NavJobSystem : IDisposable
{
    public const int DefaultCapacity = 4096;

    private readonly ConcurrentQueue<NavJob> _urgent = new();
    private readonly ConcurrentQueue<NavJob> _normal = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly Thread[] _workers;
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly int _capacity;
    private int _queued;

    private long _submitted;
    private long _rejected;
    private long _completed;
    private long _canceled;
    private long _faulted;
    private long _skipped;
    private long _computeTicks;
    private long _queueTicks;
    private long _maxComputeTicks;

    public NavJobSystem(int threads, int capacity = DefaultCapacity, ILogger? logger = null, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _logger = logger ?? NullLogger.Instance;
        _workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"nav-job-{i}", Priority = priority };
            _workers[i].Start();
        }
    }

    public int Threads => _workers.Length;

    /// <summary>Jobs submitted and not yet taken by a worker.</summary>
    public int Queued => Volatile.Read(ref _queued);

    public NavJobTotals Totals => new(
        Interlocked.Read(ref _submitted), Interlocked.Read(ref _rejected), Interlocked.Read(ref _completed), Interlocked.Read(ref _canceled),
        Interlocked.Read(ref _faulted), Interlocked.Read(ref _skipped), Interlocked.Read(ref _computeTicks), Interlocked.Read(ref _queueTicks),
        Interlocked.Read(ref _maxComputeTicks));

    /// <summary>Queue a job; false (and nothing queued) when the system is full or stopped. Any thread.</summary>
    public bool TrySubmit(NavJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_stop.IsCancellationRequested)
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        if (Interlocked.Increment(ref _queued) > _capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _rejected);
            return false;
        }

        job.EnqueuedAt = Stopwatch.GetTimestamp();
        (job.Urgent ? _urgent : _normal).Enqueue(job);
        Interlocked.Increment(ref _submitted);
        _signal.Release();
        return true;
    }

    private void WorkerLoop()
    {
        CancellationToken stop = _stop.Token;
        while (true)
        {
            try
            {
                _signal.Wait(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!_urgent.TryDequeue(out NavJob? job) && !_normal.TryDequeue(out job))
            {
                continue; // a signal per job: the matching job was taken by another worker's dequeue order; never happens in steady state
            }

            Interlocked.Decrement(ref _queued);
            Run(job);
        }
    }

    /// <summary>Run one job on the calling thread (a worker, or a test).</summary>
    internal void Run(NavJob job)
    {
        long taken = Stopwatch.GetTimestamp();
        long queueTicks = job.EnqueuedAt == 0 ? 0 : taken - job.EnqueuedAt;
        Interlocked.Add(ref _queueTicks, queueTicks);
        if (job.IsAbandoned)
        {
            Interlocked.Increment(ref _skipped);
            job.Finish(NavJobStatus.Canceled, null, default, 0, queueTicks);
            return;
        }

        PathSearchTotals before = PathSearchCounters.Current;
        try
        {
            PathResult result = job.TrackMissedTiles && job.Pathfinder is ISharedTiledPathSearch tiled
                ? tiled.FindPathShared(job.MapId, job.Start, job.Destination, job.Options, job.MissedList)
                : job.Pathfinder.FindPathShared(job.MapId, job.Start, job.Destination, job.Options);
            long compute = Stopwatch.GetTimestamp() - taken;
            Account(compute);
            Interlocked.Increment(ref _completed);
            job.Finish(NavJobStatus.Completed, result, PathSearchCounters.Current - before, compute, queueTicks);
        }
        catch (OperationCanceledException)
        {
            long compute = Stopwatch.GetTimestamp() - taken;
            Account(compute);
            Interlocked.Increment(ref _canceled);
            job.Finish(NavJobStatus.Canceled, null, PathSearchCounters.Current - before, compute, queueTicks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            long compute = Stopwatch.GetTimestamp() - taken;
            Account(compute);
            Interlocked.Increment(ref _faulted);
            FaultLedger.Shared.Record("collision.path-search", ex);
            AnomalyLedger.Shared.Report("collision", "query-fault", $"map {job.MapId}", ex.Message);
            _logger.LogFault("nav.job", ex, "Path job on map {MapId} from ({SX:F1},{SY:F1},{SZ:F1}) to ({DX:F1},{DY:F1},{DZ:F1}) failed",
                job.MapId, job.Start.X, job.Start.Y, job.Start.Z, job.Destination.X, job.Destination.Y, job.Destination.Z);
            job.Finish(NavJobStatus.Faulted, null, PathSearchCounters.Current - before, compute, queueTicks);
        }
    }

    private void Account(long compute)
    {
        Interlocked.Add(ref _computeTicks, compute);
        long max;
        while (compute > (max = Interlocked.Read(ref _maxComputeTicks)) && Interlocked.CompareExchange(ref _maxComputeTicks, compute, max) != max)
        {
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        foreach (Thread worker in _workers)
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }

        while (_urgent.TryDequeue(out NavJob? job) || _normal.TryDequeue(out job))
        {
            job.Finish(NavJobStatus.Canceled, null, default, 0, 0);
        }
    }

    private static readonly object s_sharedGate = new();
    private static NavJobSystem? s_shared;

    /// <summary>The shared pool when something has started it (diagnostics: <c>.debug mt</c>); null before the first <see cref="Shared"/> call.</summary>
    public static NavJobSystem? SharedIfStarted
    {
        get
        {
            lock (s_sharedGate)
            {
                return s_shared;
            }
        }
    }

    private static readonly HashSet<int> s_mismatchLogged = [];

    /// <summary>
    /// The process's path-job pool (creatures and playerbots), started on first use with <paramref name="threads"/> workers. Later calls get
    /// the same pool whatever they ask (the first starter wins); a later starter that asked for another count is warned once per count.
    /// Its threads are background threads and end with the process.
    /// </summary>
    public static NavJobSystem Shared(int threads, ILogger? logger = null)
    {
        lock (s_sharedGate)
        {
            int asked = Math.Clamp(threads, 1, 64);
            if (s_shared is { } have)
            {
                if (have.Threads != asked && s_mismatchLogged.Add(asked))
                {
                    (logger ?? have._logger).LogWarning("Path-job pool already runs {Have} threads; a later starter asked for {Asked}, ignored (first starter wins)", have.Threads, asked);
                }

                return have;
            }

            return s_shared = new NavJobSystem(asked, DefaultCapacity, logger);
        }
    }
}
