using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A detached request for one navigation query. It contains values only: a worker must never
/// receive a live player, map, session, collision view, or risk object.
/// </summary>
internal sealed record PlayerbotNavigationRequest(
    uint MapId,
    Vector3 Start,
    Vector3 Destination,
    PathOptions Options,
    NavRegionGraph? RegionGraph = null,
    float MaxRouteYards = 0,
    int MaxPathPoints = PathOptions.DefaultMaxPoints,
    NavMeshDatasetDescriptor? Dataset = null);

internal enum PlayerbotNavigationResultStatus
{
    Completed,
    Canceled,
    Faulted,
}

/// <summary>The detached answer and the work counters captured on the worker thread.</summary>
internal sealed record PlayerbotNavigationResult(
    PlayerbotNavigationResultStatus Status,
    PathResult? Path,
    Vector3 QueryDestination,
    bool RegionLeg,
    bool Capped,
    PathSearchTotals Search,
    TimeSpan ComputeTime,
    Exception? Error = null);

/// <summary>
/// A submitted query. Capacity is held until the calculation has stopped and the owner has
/// consumed or disposed the job. Disposing an active job requests cooperative cancellation; it
/// does not falsely make the slot available while the worker is still using its private context.
/// </summary>
internal sealed class PlayerbotNavigationJob : IDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly Action<PlayerbotNavigationJob> _release;
    private readonly TaskCompletionSource<PlayerbotNavigationResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _finished;
    private int _consumed;
    private int _released;

    internal PlayerbotNavigationJob(CancellationTokenSource cancel, Action<PlayerbotNavigationJob> release)
    {
        _cancel = cancel;
        _release = release;
    }

    internal Task<PlayerbotNavigationResult> Completion => _completion.Task;

    internal void Cancel()
    {
        try { _cancel.Cancel(); }
        catch (ObjectDisposedException ex)
        {
            // A finished and consumed job already released its linked source.
            _ = ex;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.nav-executor", ex);
        }
    }

    internal void Complete(PlayerbotNavigationResult result)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;

        _completion.TrySetResult(result);
        TryRelease();
    }

    internal void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Complete(new(PlayerbotNavigationResultStatus.Faulted, null, default, false, false, default, TimeSpan.Zero, error));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _consumed, 1) == 0)
        {
            Cancel();
            TryRelease();
        }
    }

    private void TryRelease()
    {
        if (Volatile.Read(ref _finished) == 0 || Volatile.Read(ref _consumed) == 0
            || Interlocked.Exchange(ref _released, 1) != 0)
            return;

        _release(this);
        _cancel.Dispose();
    }
}

/// <summary>
/// A bounded, single-worker executor for detached playerbot path queries. One private pathfinder
/// context is retained for the current map and is recreated when the worker changes maps. This
/// keeps mutable navmesh/tile state serial and prevents a queue burst from creating one task per
/// request.
/// </summary>
internal sealed partial class PlayerbotNavigationExecutor : IDisposable, IAsyncDisposable
{
    internal const int DefaultCapacity = 32;
    private const float RegionLegYards = 1000f;
    private const int RegionSearchNodes = 20_000;

    private readonly Func<NavMeshDatasetDescriptor?, IPathfinder> _pathfinderFactory;
    private readonly BlockingCollection<WorkItem> _queue;
    private readonly SemaphoreSlim _capacity;
    private readonly object _gate = new();
    private readonly HashSet<PlayerbotNavigationJob> _jobs = [];
    private readonly HashSet<PlanLease> _plans = [];
    private readonly Task _worker;
    private bool _disposed;
    private IPathfinder? _pathfinder;
    private uint _pathfinderMap;
    private bool _hasPathfinderMap;
    private NavMeshDatasetDescriptor? _pathfinderDataset;

    internal PlayerbotNavigationExecutor(Func<IPathfinder> pathfinderFactory, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(pathfinderFactory);
        _pathfinderFactory = _ => pathfinderFactory();
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _queue = new BlockingCollection<WorkItem>(capacity);
        _capacity = new SemaphoreSlim(capacity, capacity);
        _worker = Task.Run(WorkerLoopAsync);
    }

    internal PlayerbotNavigationExecutor(Func<NavMeshDatasetDescriptor, IPathfinder> pathfinderFactory,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(pathfinderFactory);
        _pathfinderFactory = dataset => dataset is { } value
            ? pathfinderFactory(value)
            : throw new InvalidOperationException("a detached navigation dataset is required");
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _queue = new BlockingCollection<WorkItem>(capacity);
        _capacity = new SemaphoreSlim(capacity, capacity);
        _worker = Task.Run(WorkerLoopAsync);
    }

    [ThreadStatic] private static PlanLease? t_currentLease;

    private static void ReportExecutorPlanDropped(string reason, uint? mapId = null, Vector3? start = null, Vector3? destination = null)
    {
        string where = mapId is { } map && start is { } from && destination is { } to
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $" on map {map} from ({from.X:F1},{from.Y:F1},{from.Z:F1}) toward ({to.X:F1},{to.Y:F1},{to.Z:F1})")
            : string.Empty;
        AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + "nav", "plan-dropped", "navigation executor", reason + where, AnomalySeverity.Warning);
    }

    internal static void AttachCurrentLease(PlayerbotAsyncNavigationPlan plan)
    {
        if (t_currentLease is { Plan: null } lease)
            lease.Plan = plan;
    }

    /// <summary>Number of submitted jobs whose result has not yet been consumed.</summary>
    internal int Outstanding => DefaultOutstanding();

    /// <summary>Owner-side query caches are admitted separately and bounded for an entire plan.</summary>
    internal bool TryAcquirePlan(out IDisposable lease)
    {
        lock (_gate)
        {
            lease = null!;
            if (_disposed) return false;
            if (_plans.Count >= DefaultCapacity)
            {
                ReportExecutorPlanDropped("plan lease capacity exhausted");
                return false;
            }

            var admitted = new PlanLease(this);
            _plans.Add(admitted);
            t_currentLease = admitted;
            lease = admitted;
            return true;
        }
    }

    internal int ActivePlans { get { lock (_gate) return _plans.Count; } }

    internal bool TrySubmit(PlayerbotNavigationRequest request, out PlayerbotNavigationJob job)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options is null) throw new ArgumentException("request options are required", nameof(request));

        job = null!;
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (!_capacity.Wait(0))
            {
                if (t_currentLease?.Plan is { } plan && plan.MapId == request.MapId && plan.Start == request.Start)
                    plan.ReportDropped("executor job capacity exhausted");
                else
                    ReportExecutorPlanDropped("executor job capacity exhausted", request.MapId, request.Start, request.Destination);
                return false;
            }

            // The job owns the linked source: caller cancellation and job disposal both cancel
            // the same token, including while the item is still queued.
            var owned = CancellationTokenSource.CreateLinkedTokenSource(request.Options.CancellationToken);
            job = new PlayerbotNavigationJob(owned, Release);
            _jobs.Add(job);
            try
            {
                if (!_queue.TryAdd(new WorkItem(request, job, owned)))
                {
                    _jobs.Remove(job);
                    _capacity.Release();
                    job.Dispose();
                    job = null!;
                    return false;
                }
            }
            catch (InvalidOperationException ex)
            {
                FaultLedger.Shared.Record("playerbot.nav-executor", ex);
                _jobs.Remove(job);
                _capacity.Release();
                job.Dispose();
                job = null!;
                ReportExecutorPlanDropped("executor queue closed", request.MapId, request.Start, request.Destination);
                return false;
            }

            return true;
        }
    }

    private int DefaultOutstanding()
    {
        lock (_gate) return _jobs.Count;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            foreach (WorkItem item in _queue.GetConsumingEnumerable())
                await ExecuteAsync(item).ConfigureAwait(false);
        }
        finally
        {
            DisposePathfinder();
        }
    }

    private Task ExecuteAsync(WorkItem item)
    {
        PlayerbotNavigationRequest request = item.Request;
        PlayerbotNavigationJob job = item.Job;
        Stopwatch watch = Stopwatch.StartNew();
        PathSearchTotals before = PathSearchCounters.Current;
        try
        {
            item.Cancel.Token.ThrowIfCancellationRequested();
            if (!_hasPathfinderMap || _pathfinderMap != request.MapId || _pathfinderDataset != request.Dataset)
            {
                DisposePathfinder();
                _pathfinder = _pathfinderFactory(request.Dataset) ?? throw new InvalidOperationException("navigation factory returned null");
                _pathfinderMap = request.MapId;
                _hasPathfinderMap = true;
                _pathfinderDataset = request.Dataset;
            }

            IPathfinder pathfinder = _pathfinder
                ?? throw new InvalidOperationException("navigation pathfinder was not initialized");
            PathOptions options = request.Options with { CancellationToken = item.Cancel.Token };
            Vector3 queryDestination = FirstDestination(request, options, pathfinder, out bool regionLeg);
            if (request.MaxRouteYards > 0)
                options = options with { MaxSearchNodes = PlayerbotNavigation.SearchNodes(request.Start, queryDestination) };
            PathResult path = pathfinder.FindPath(request.MapId, request.Start, queryDestination, options);
            item.Cancel.Token.ThrowIfCancellationRequested();
            PathSearchTotals search = PathSearchCounters.Current - before;
            // Copy the points before publishing. A worker-owned pathfinder must not retain a
            // mutable list or a tile-backed object through the world-side completion queue.
            if (path.Points.Count > Math.Max(2, options.MaxPoints))
                throw new InvalidOperationException("navigation answer exceeds the requested point limit");
            // `with` keeps the answer's other facts (PathResult.StartFar, issue #1501) along with the copied points.
            PathResult detached = path with { Points = Array.AsReadOnly(path.Points.ToArray()) };
            job.Complete(new(PlayerbotNavigationResultStatus.Completed, detached, queryDestination, regionLeg,
                search.Capped > 0, search, watch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            PathSearchTotals search = PathSearchCounters.Current - before;
            job.Complete(new(PlayerbotNavigationResultStatus.Canceled, null, default, false,
                search.Capped > 0, search, watch.Elapsed));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.nav-executor", error);
            PathSearchTotals search = PathSearchCounters.Current - before;
            job.Complete(new(PlayerbotNavigationResultStatus.Faulted, null, default, false,
                search.Capped > 0, search, watch.Elapsed, error));
        }

        return Task.CompletedTask;
    }

    private static Vector3 FirstDestination(PlayerbotNavigationRequest request, PathOptions options,
        IPathfinder pathfinder, out bool regionLeg)
    {
        regionLeg = false;
        Vector3 start = request.Start;
        Vector3 goal = request.Destination;
        float distance = Vector3.Distance(start, goal);
        float maxRoute = request.MaxRouteYards > 0 && float.IsFinite(request.MaxRouteYards)
            ? request.MaxRouteYards : float.PositiveInfinity;
        float chunk = MathF.Min(maxRoute * 0.9f, Math.Max(1, request.MaxPathPoints - 2));

        if (distance > chunk && request.RegionGraph is { } graph && pathfinder is NavMeshPathfinder nav
            && graph.MapId == request.MapId)
        {
            int from = nav.RegionAt(graph, start, options);
            int to = nav.RegionAt(graph, goal, options);
            if (from >= 0 && to >= 0)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                NavRegionRouteStatus status = graph.FindRoute(from, start, to, goal, RegionSearchNodes, out List<Vector3> portals);
                options.CancellationToken.ThrowIfCancellationRequested();
                if (portals.Count > 0)
                {
                    if (status == NavRegionRouteStatus.Found)
                        portals.Add(goal);
                    float leg = MathF.Min(RegionLegYards, maxRoute * 0.5f);
                    int pick = 0;
                    float along = Vector3.Distance(start, portals[0]);
                    for (int i = 1; i < portals.Count; i++)
                    {
                        along += Vector3.Distance(portals[i - 1], portals[i]);
                        if (along > leg) break;
                        pick = i;
                    }

                    regionLeg = true;
                    return portals[pick];
                }
            }
        }

        if (distance > chunk && distance <= maxRoute)
            return goal;
        return distance > chunk ? start + ((goal - start) * (chunk / distance)) : goal;
    }

    private void Release(PlayerbotNavigationJob job)
    {
        lock (_gate)
        {
            if (_jobs.Remove(job))
                _capacity.Release();
        }
    }

    private void DisposePathfinder()
    {
        if (_pathfinder is IDisposable disposable)
            disposable.Dispose();
        _pathfinder = null;
        _hasPathfinderMap = false;
        _pathfinderDataset = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (PlayerbotNavigationJob job in _jobs.ToArray()) job.Cancel();
            _queue.CompleteAdding();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
        PlayerbotNavigationJob[] remaining;
        lock (_gate) remaining = _jobs.ToArray();
        foreach (PlayerbotNavigationJob job in remaining) job.Dispose();
        PlanLease[] plans;
        lock (_gate) plans = _plans.ToArray();
        foreach (PlanLease plan in plans) plan.Dispose();
    }

    private sealed class PlanLease(PlayerbotNavigationExecutor owner) : IDisposable
    {
        private int _released;
        internal PlayerbotAsyncNavigationPlan? Plan { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (ReferenceEquals(t_currentLease, this)) t_currentLease = null;
            bool executorDisposed;
            lock (owner._gate)
            {
                owner._plans.Remove(this);
                executorDisposed = owner._disposed;
            }

            Plan?.OnLeaseReleased(executorDisposed);
        }
    }

    private sealed record WorkItem(PlayerbotNavigationRequest Request, PlayerbotNavigationJob Job,
        CancellationTokenSource Cancel);
}
