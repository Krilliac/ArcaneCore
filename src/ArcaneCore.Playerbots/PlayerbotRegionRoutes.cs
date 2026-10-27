using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The portal lists of the region searches of <see cref="PlayerbotNavigation"/> (<see cref="NavRegionGraph"/> FindRoute, up to
/// <see cref="PlayerbotNavigation.RegionSearchNodes"/> portals settled), kept per graph and per (from-region, to-region) pair. A far goal
/// is planned leg by leg, so the same pair is asked again on every leg and by every bot bound the same way.
/// <para>
/// Ownership: the cache of a graph lives in a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by the graph's <em>reference</em>. The
/// graph is built on the thread pool and a rebuild is a new instance (<see cref="NavMeshPathfinder.RegionGraph"/>), so a rebuilt graph
/// starts with an empty cache and the old one is collected with its graph; a cached list never answers for another graph. Each cache is
/// bounded (<see cref="MaxEntries"/>, oldest dropped first). Thread affinity: <see cref="TryRoute"/> is called on the world thread; a
/// deferred miss is searched by a pool worker (the graph is immutable and the search keeps its sets per thread); every state of a cache is
/// guarded by its lock.
/// </para>
/// <para>
/// An entry is start-independent: the search always chains to the settled portal nearest the goal, and "no exit of the region is nearer the
/// goal than the asking position" is derived per asker at lookup (<see cref="RouteCache.TryGet"/>) from that chain, so bots at any
/// distance apart share one entry and nothing is ever replaced. Found and Unreachable depend on the two regions only; the nearest portal is
/// the one for the goal of the first asker of the pair (a limit: bots with different goals in one target region share it).
/// </para>
/// <para>
/// Deferred misses go through one process-wide FIFO (<see cref="MaxQueued"/> pairs) drained by at most <see cref="MaxInFlight"/> pool
/// workers. A pair is Cached, Asked (its work is queued or running; remembered in the cache's asked map) or Unasked; an ask never resets,
/// replaces or removes anything, so no order of asks can move a pair backwards. A pair whose work was lost is searched on the asking thread
/// past the caller's wait bound, at most <see cref="SyncSearchBudget.MaxPerWindow"/> per <see cref="SyncSearchBudget.WindowMs"/>.
/// Lock order: the queue's gate, then a cache's lock.
/// </para>
/// </summary>
internal static class PlayerbotRegionRoutes
{
    /// <summary>The most routes one graph's cache keeps.</summary>
    internal const int MaxEntries = 1024;

    /// <summary>The most pool workers draining the queue at once, across all graphs.</summary>
    internal const int MaxInFlight = 8;

    /// <summary>The most deferred pairs queued at once, across all graphs (at least the bots asking at once).</summary>
    internal const int MaxQueued = 4096;

    private static readonly ConditionalWeakTable<NavRegionGraph, RouteCache> Caches = [];
    private static readonly object QueueGate = new();
    private static readonly Queue<Work> Queued = new();
    private static readonly SyncSearchBudget DefaultBudget = new();
    private static int s_workers;        // under QueueGate
    private static long s_syncFallbacks; // Interlocked

    private readonly record struct Work(RouteCache Cache, int From, Vector3 Start, int To, Vector3 Goal, ILogger? Logger);

    /// <summary>The pairs waiting in the queue (test seam, telemetry).</summary>
    internal static int QueuedCount
    {
        get { lock (QueueGate) return Queued.Count; }
    }

    /// <summary>The workers draining the queue (test seam, telemetry).</summary>
    internal static int Workers
    {
        get { lock (QueueGate) return s_workers; }
    }

    /// <summary>The searches run on the asking thread because a queued pair was not answered in time or the queue was full.</summary>
    internal static long SyncFallbacks => Interlocked.Read(ref s_syncFallbacks);

    /// <summary>The cache of <paramref name="graph"/>.</summary>
    internal static RouteCache Of(NavRegionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Caches.GetValue(graph, static g => new RouteCache(g));
    }

    /// <summary>
    /// The route between two regions: a cache hit, or a miss. A miss is searched at once on this thread unless <paramref name="mayDefer"/>
    /// (the caller takes "pending" and asks again on a later think), when it is queued for the pool and false is returned. A pair asked for
    /// longer than <paramref name="maxWaitMs"/> (wall clock <paramref name="wallMs"/>) whose answer never came, or one that finds the queue
    /// full, is searched here after all, as far as <paramref name="budget"/> allows (otherwise the ask is repeated). <paramref name="portals"/>
    /// is a copy the caller owns. Faults of the search are recorded and logged through <paramref name="logger"/>.
    /// </summary>
    internal static bool TryRoute(NavRegionGraph graph, int fromRegion, Vector3 from, int toRegion, Vector3 to, bool mayDefer,
        out NavRegionRouteStatus status, out List<Vector3> portals, ILogger? logger = null, long wallMs = 0, long maxWaitMs = long.MaxValue,
        SyncSearchBudget? budget = null)
    {
        if (fromRegion == toRegion)
        {
            status = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, out portals); // no search: one region
            return true;
        }

        RouteCache cache = Of(graph);
        if (cache.TryGet(fromRegion, toRegion, from, out status, out portals)) return true;
        if (!mayDefer)
        {
            cache.Search(fromRegion, from, toRegion, to, logger, out status, out portals);
            return true;
        }

        budget ??= DefaultBudget;
        if (cache.TryAskedSince(fromRegion, toRegion, out long since))
        {
            if (wallMs - since > maxWaitMs && budget.TryTake(wallMs))
            {
                Interlocked.Increment(ref s_syncFallbacks);
                cache.Search(fromRegion, from, toRegion, to, logger, out status, out portals);
                return true;
            }

            bool start;
            lock (QueueGate) start = ReserveWorker();
            if (start) StartWorker(logger);
            return Pending(out status, out portals);
        }

        bool queueFull, answered = false, startWorker = false;
        status = NavRegionRouteStatus.Exhausted;
        portals = [];
        lock (QueueGate)
        {
            queueFull = Queued.Count >= MaxQueued;
            if (!queueFull)
            {
                ClaimResult claim = cache.Claim(fromRegion, toRegion, wallMs);
                if (claim == ClaimResult.Cached)
                {
                    answered = cache.TryGet(fromRegion, toRegion, from, out status, out portals);
                }
                else
                {
                    if (claim == ClaimResult.Claimed) Queued.Enqueue(new Work(cache, fromRegion, from, toRegion, to, logger));
                    startWorker = ReserveWorker();
                }
            }
        }

        if (startWorker) StartWorker(logger);
        if (!queueFull) return answered;

        // The queue is full: nothing is recorded (the bot asks again on its next think); a search here only within the budget.
        if (budget.TryTake(wallMs))
        {
            Interlocked.Increment(ref s_syncFallbacks);
            cache.Search(fromRegion, from, toRegion, to, logger, out status, out portals);
            return true;
        }

        return Pending(out status, out portals);
    }

    private static bool Pending(out NavRegionRouteStatus status, out List<Vector3> portals)
    {
        status = NavRegionRouteStatus.Exhausted;
        portals = [];
        return false;
    }

    /// <summary>
    /// Count a worker when work is queued and fewer than <see cref="MaxInFlight"/> run. True: the caller starts it with
    /// <see cref="StartWorker"/> after it released the gate. The caller holds the queue's gate.
    /// </summary>
    private static bool ReserveWorker()
    {
        if (Queued.Count == 0 || s_workers >= MaxInFlight) return false;
        s_workers++;
        return true;
    }

    /// <summary>
    /// Start the worker <see cref="ReserveWorker"/> counted. The caller does not hold the gate: a refusal is recorded and logged here, and
    /// nothing is logged under the gate. The count is given back when the pool refuses; the work then waits for the next ask to start one.
    /// </summary>
    private static void StartWorker(ILogger? logger)
    {
        try
        {
            Task.Run(Worker);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (QueueGate) s_workers--;
            FaultLedger.Shared.Record("playerbot.region-route", ex);
            (logger ?? NullLogger.Instance).LogFault("playerbot-region-route", ex, "Region route worker could not be started");
        }
        catch (OutOfMemoryException)
        {
            lock (QueueGate) s_workers--; // the count is given back; out of memory is not recorded as a fault
            throw;
        }
    }

    private static void Worker()
    {
        bool drained = false;
        Work? running = null;
        try
        {
            while (true)
            {
                Work work;
                lock (QueueGate)
                {
                    if (!Queued.TryDequeue(out work))
                    {
                        s_workers--;
                        drained = true;
                        return;
                    }
                }

                running = work;
                work.Cache.SearchQueued(work.From, work.Start, work.To, work.Goal, work.Logger);
                running = null;
            }
        }
        finally
        {
            if (!drained)
            {
                // An exception that SearchQueued does not catch (OutOfMemoryException) ended this worker with its count still held and its
                // pair still marked asked: give both back so the queue keeps draining (the count changes only under the gate).
                if (running is { } lost) lost.Cache.ClearAsked(lost.From, lost.To);
                bool replace;
                lock (QueueGate)
                {
                    s_workers--;
                    replace = ReserveWorker();
                }

                if (replace) StartWorker(running?.Logger);
            }
        }
    }

    internal enum ClaimResult
    {
        Cached,
        Asked,
        Claimed,
    }

    /// <summary>
    /// Bounds the searches the asking (world) thread runs itself when the pool did not answer: at most <see cref="MaxPerWindow"/> per
    /// <see cref="WindowMs"/> of wall clock. One per <see cref="PlayerbotPathJobs"/>.
    /// </summary>
    internal sealed class SyncSearchBudget
    {
        /// <summary>One world tick.</summary>
        internal const int WindowMs = 50;

        /// <summary>The searches allowed in one window.</summary>
        internal const int MaxPerWindow = 1;

        private readonly object _gate = new();
        private long _windowStart = long.MinValue / 2;
        private int _taken;

        /// <summary>Take one search of the window containing <paramref name="wallMs"/>; false when the window is spent.</summary>
        internal bool TryTake(long wallMs)
        {
            lock (_gate)
            {
                if (wallMs - _windowStart >= WindowMs)
                {
                    _windowStart = wallMs;
                    _taken = 0;
                }

                if (_taken >= MaxPerWindow) return false;
                _taken++;
                return true;
            }
        }
    }

    /// <summary>The routes of one graph.</summary>
    internal sealed class RouteCache(NavRegionGraph graph)
    {
        // NearestYards: distance from the chain's last portal to Goal (infinite without portals); Found entries ignore it.
        private readonly record struct Route(NavRegionRouteStatus Status, Vector3[] Portals, Vector3 Goal, float NearestYards);

        private readonly object _gate = new();
        private readonly Dictionary<(int From, int To), Route> _routes = [];
        private readonly Queue<(int From, int To)> _order = new();
        private readonly Dictionary<(int From, int To), long> _asked = [];
        private readonly HashSet<(int From, int To)> _faultedOnce = [];
        private long _searches;

        /// <summary>Test seam: runs at the start of each search, inside its fault handling (a test makes the search fault by throwing here).</summary>
        internal Action<int, int>? SearchProbe { get; set; }

        /// <summary>The searches this cache ran (misses).</summary>
        internal long Searches => Interlocked.Read(ref _searches);

        /// <summary>The routes kept.</summary>
        internal int Count
        {
            get { lock (_gate) return _routes.Count; }
        }

        /// <summary>The pairs queued or running (or marked by <see cref="MarkAsked"/>).</summary>
        internal int AskedCount
        {
            get { lock (_gate) return _asked.Count; }
        }

        /// <summary>
        /// The kept route of the pair as it answers an asker at <paramref name="start"/>: the chain, or no portal when the route is not
        /// found and no exit of the region is nearer the goal than the start (what a search from that start would have answered).
        /// </summary>
        internal bool TryGet(int from, int to, Vector3 start, out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            lock (_gate)
            {
                if (_routes.TryGetValue((from, to), out Route route))
                {
                    Derive(route, start, out status, out portals);
                    return true;
                }
            }

            status = NavRegionRouteStatus.Exhausted;
            portals = [];
            return false;
        }

        /// <summary>Test seam: note the pair as asked at <paramref name="wallMs"/> without queueing work (a search that was lost).</summary>
        internal void MarkAsked(int from, int to, long wallMs)
        {
            lock (_gate) _asked[(from, to)] = wallMs;
        }

        /// <summary>Forget that the pair was asked (its work was lost).</summary>
        internal void ClearAsked(int from, int to)
        {
            lock (_gate) _asked.Remove((from, to));
        }

        internal bool TryAskedSince(int from, int to, out long since)
        {
            lock (_gate) return _asked.TryGetValue((from, to), out since);
        }

        /// <summary>Cached, already asked, or now marked asked (the caller then queues the work). The caller holds the queue's gate.</summary>
        internal ClaimResult Claim(int from, int to, long wallMs)
        {
            lock (_gate)
            {
                if (_routes.ContainsKey((from, to))) return ClaimResult.Cached;
                return _asked.TryAdd((from, to), wallMs) ? ClaimResult.Claimed : ClaimResult.Asked;
            }
        }

        /// <summary>Run a queued search unless the pair was answered meanwhile (a fallback search on the asking thread). Does not throw.</summary>
        internal void SearchQueued(int fromRegion, Vector3 from, int toRegion, Vector3 to, ILogger? logger)
        {
            try
            {
                bool cached;
                lock (_gate) cached = _routes.ContainsKey((fromRegion, toRegion));
                if (cached)
                {
                    lock (_gate) _asked.Remove((fromRegion, toRegion));
                    return;
                }

                Search(fromRegion, from, toRegion, to, logger, out _, out _);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.region-route", ex);
                (logger ?? NullLogger.Instance).LogFault("playerbot-region-route", ex, "Queued region route search from region {From} to region {To} failed", fromRegion, toRegion);
                lock (_gate) _asked.Remove((fromRegion, toRegion));
            }
        }

        /// <summary>Run the region search and keep its answer. A faulted search is kept as "no decision" (Exhausted, no portals), recorded and logged.</summary>
        internal void Search(int fromRegion, Vector3 from, int toRegion, Vector3 to, ILogger? logger = null)
            => Search(fromRegion, from, toRegion, to, logger, out _, out _);

        /// <summary>As the overload without outputs; also gives the answer to the asker (as <see cref="TryGet"/> would for its position).</summary>
        internal void Search(int fromRegion, Vector3 from, int toRegion, Vector3 to, ILogger? logger,
            out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            NavRegionRouteStatus found;
            List<Vector3> chain;
            bool faulted = false;
            try
            {
                Interlocked.Increment(ref _searches);
                SearchProbe?.Invoke(fromRegion, toRegion);
                found = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, nearestFromAnywhere: true, out chain, out _);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.region-route", ex);
                (logger ?? NullLogger.Instance).LogFault("playerbot-region-route", ex, "Region route search from region {From} to region {To} failed", fromRegion, toRegion);
                found = NavRegionRouteStatus.Exhausted;
                chain = [];
                faulted = true;
            }

            Vector3[] kept = [.. chain];
            float nearest = kept.Length == 0 ? float.PositiveInfinity : Vector3.Distance(kept[^1], to);
            var route = new Route(found, kept, to, nearest);
            lock (_gate)
            {
                _asked.Remove((fromRegion, toRegion));
                // A first fault is not kept (a transient one must not refuse the pair until it is evicted): the next ask searches again. A
                // pair that faults twice is kept as "no decision" so a lasting fault is not searched on every ask.
                if (faulted && !_routes.ContainsKey((fromRegion, toRegion)) && _faultedOnce.Count < MaxEntries && _faultedOnce.Add((fromRegion, toRegion)))
                {
                    Derive(route, from, out status, out portals);
                    return;
                }

                _faultedOnce.Remove((fromRegion, toRegion));
                if (_routes.TryAdd((fromRegion, toRegion), route))
                {
                    _order.Enqueue((fromRegion, toRegion));
                    while (_routes.Count > MaxEntries && _order.TryDequeue(out (int From, int To) oldest)) _routes.Remove(oldest);
                }
                else
                {
                    route = _routes[(fromRegion, toRegion)]; // the earlier answer stands; this asker gets what a hit would give
                }
            }

            Derive(route, from, out status, out portals);
        }

        private static void Derive(Route route, Vector3 start, out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            status = route.Status;
            portals = route.Status != NavRegionRouteStatus.Found && route.Portals.Length > 0
                && Vector3.Distance(start, route.Goal) <= route.NearestYards ? [] : [.. route.Portals];
        }
    }
}
