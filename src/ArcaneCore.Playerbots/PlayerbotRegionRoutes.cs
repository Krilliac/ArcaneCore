using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The portal lists of the region searches of <see cref="PlayerbotNavigation"/> (<see cref="NavRegionGraph.FindRoute"/>, up to
/// <see cref="PlayerbotNavigation.RegionSearchNodes"/> portals settled), kept per graph and per (from-region, to-region) pair. A far goal
/// is planned leg by leg, so the same pair is asked again on every leg and by every bot bound the same way.
/// <para>
/// Ownership: the cache of a graph lives in a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by the graph's <em>reference</em>. The
/// graph is built on the thread pool and a rebuild is a new instance (<see cref="NavMeshPathfinder.RegionGraph"/>), so a rebuilt graph
/// starts with an empty cache and the old one is collected with its graph; a cached list never answers for another graph. Each cache is
/// bounded (<see cref="MaxEntries"/>, oldest dropped first) and so is the work in flight (<see cref="MaxInFlight"/>). Thread affinity:
/// <see cref="TryRoute"/> is called on the world thread; the miss search runs on a thread pool thread (the graph is immutable and the
/// search keeps its sets per thread); every state of a cache is guarded by its lock.
/// </para>
/// <para>
/// The portals of a pair are those of the first positions asked: the search's costs start from the asking position, so a later start
/// in the same region may have chosen a slightly different first portal. The leg planner only uses them to pick a portal along the way
/// and plans the real path query to it. One answer depends on the start so much that it is not shared: "unreachable" with no portal at
/// all (no exit of the region is nearer the goal than the asking position itself) says nothing for a bot standing elsewhere in the
/// region, so it answers only starts within <see cref="StartDependentYards"/> of the one searched and is searched again for others.
/// </para>
/// <para>
/// A miss that stays unanswered (the pool is busy, or the search was lost) is not waited for forever: past the caller's wait bound the
/// ask is searched on the asking thread.
/// </para>
/// </summary>
internal static class PlayerbotRegionRoutes
{
    /// <summary>The most routes one graph's cache keeps.</summary>
    internal const int MaxEntries = 256;

    /// <summary>The most region searches running on the pool at once, across all graphs.</summary>
    internal const int MaxInFlight = 8;

    /// <summary>An "unreachable, no portal" answer (<see cref="RouteCache.TryGet"/>) holds for starts within this many yards of the one searched.</summary>
    internal const float StartDependentYards = 32f;

    private static readonly ConditionalWeakTable<NavRegionGraph, RouteCache> Caches = [];
    private static int s_inFlight;

    /// <summary>The cache of <paramref name="graph"/>.</summary>
    internal static RouteCache Of(NavRegionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Caches.GetValue(graph, static g => new RouteCache(g));
    }

    /// <summary>
    /// The route between two regions: a cache hit, or a miss. A miss is searched at once on this thread unless <paramref name="mayDefer"/>
    /// (the caller takes "pending" and asks again on a later think), when it is queued for the pool and false is returned (also when the
    /// pool is busy with <see cref="MaxInFlight"/> searches: the ask is repeated). A miss that has been asked for longer than
    /// <paramref name="maxWaitMs"/> (wall clock <paramref name="wallMs"/>) is searched here after all. <paramref name="portals"/> is a copy the
    /// caller owns. Faults of the search are recorded and logged through <paramref name="logger"/>.
    /// </summary>
    internal static bool TryRoute(NavRegionGraph graph, int fromRegion, Vector3 from, int toRegion, Vector3 to, bool mayDefer,
        out NavRegionRouteStatus status, out List<Vector3> portals, ILogger? logger = null, long wallMs = 0, long maxWaitMs = long.MaxValue)
    {
        if (fromRegion == toRegion)
        {
            status = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, out portals); // no search: one region
            return true;
        }

        RouteCache cache = Of(graph);
        if (cache.TryGet(fromRegion, toRegion, from, out status, out portals)) return true;
        if (!mayDefer || cache.WaitedPast(fromRegion, toRegion, wallMs, maxWaitMs))
        {
            cache.Search(fromRegion, from, toRegion, to, logger, out status, out portals);
            return true;
        }

        // Already queued by an earlier ask: nothing to start. Otherwise take a slot of the pool's bound, or leave the ask for a later think.
        if (cache.MarkInFlight(fromRegion, toRegion))
        {
            bool started = false;
            if (Interlocked.Increment(ref s_inFlight) <= MaxInFlight)
            {
                try
                {
                    Task.Run(() =>
                    {
                        try { cache.Search(fromRegion, from, toRegion, to, logger, out _, out _); }
                        finally
                        {
                            cache.ClearInFlight(fromRegion, toRegion);
                            Interlocked.Decrement(ref s_inFlight);
                        }
                    });
                    started = true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.region-route", ex);
                    (logger ?? NullLogger.Instance).LogFault("playerbot-region-route", ex, "Region route search could not be queued");
                }
            }

            if (!started)
            {
                Interlocked.Decrement(ref s_inFlight);
                cache.ClearInFlight(fromRegion, toRegion);
            }
        }

        status = NavRegionRouteStatus.Exhausted;
        portals = [];
        return false;
    }

    /// <summary>The routes of one graph.</summary>
    internal sealed class RouteCache(NavRegionGraph graph)
    {
        private readonly object _gate = new();
        private readonly Dictionary<(int From, int To), (NavRegionRouteStatus Status, Vector3[] Portals, Vector3? Start)> _routes = [];
        private readonly Queue<(int From, int To)> _order = new();
        private readonly HashSet<(int From, int To)> _inFlight = [];
        private readonly Dictionary<(int From, int To), long> _askedSince = [];
        private long _searches;

        /// <summary>The searches this cache ran (misses).</summary>
        internal long Searches => Interlocked.Read(ref _searches);

        /// <summary>The routes kept.</summary>
        internal int Count
        {
            get { lock (_gate) return _routes.Count; }
        }

        /// <summary>The kept route of the pair, unless it is a start-dependent answer (<see cref="StartDependentYards"/>) for another start.</summary>
        internal bool TryGet(int from, int to, Vector3 start, out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            lock (_gate)
            {
                if (_routes.TryGetValue((from, to), out var route)
                    && (route.Start is not { } searched || Vector3.Distance(searched, start) <= StartDependentYards))
                {
                    status = route.Status;
                    portals = [.. route.Portals];
                    return true;
                }
            }

            status = NavRegionRouteStatus.Exhausted;
            portals = [];
            return false;
        }

        /// <summary>Note that the pair is asked for at <paramref name="wallMs"/>; true when it has been asked for longer than <paramref name="maxWaitMs"/>.</summary>
        internal bool WaitedPast(int from, int to, long wallMs, long maxWaitMs)
        {
            lock (_gate)
            {
                if (!_askedSince.TryGetValue((from, to), out long since))
                {
                    _askedSince[(from, to)] = wallMs;
                    return false;
                }

                return wallMs - since > maxWaitMs;
            }
        }

        internal bool MarkInFlight(int from, int to)
        {
            lock (_gate) return _inFlight.Add((from, to));
        }

        internal void ClearInFlight(int from, int to)
        {
            lock (_gate) _inFlight.Remove((from, to));
        }

        /// <summary>Run the region search and keep its answer. A faulted search is kept as "no decision" (Exhausted, no portals), recorded and logged.</summary>
        internal void Search(int fromRegion, Vector3 from, int toRegion, Vector3 to, ILogger? logger = null)
            => Search(fromRegion, from, toRegion, to, logger, out _, out _);

        /// <summary>As the overload without outputs; also gives the answer to the asker (which may be one the cache does not keep for others).</summary>
        internal void Search(int fromRegion, Vector3 from, int toRegion, Vector3 to, ILogger? logger,
            out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            try
            {
                Interlocked.Increment(ref _searches);
                status = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, out portals);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.region-route", ex);
                (logger ?? NullLogger.Instance).LogFault("playerbot-region-route", ex, "Region route search from region {From} to region {To} failed", fromRegion, toRegion);
                status = NavRegionRouteStatus.Exhausted;
                portals = [];
            }

            Vector3? startKey = status == NavRegionRouteStatus.Unreachable && portals.Count == 0 ? from : null;
            lock (_gate)
            {
                _askedSince.Remove((fromRegion, toRegion));
                if (_routes.TryAdd((fromRegion, toRegion), (status, [.. portals], startKey)))
                {
                    _order.Enqueue((fromRegion, toRegion));
                    while (_routes.Count > MaxEntries && _order.TryDequeue(out (int From, int To) oldest)) _routes.Remove(oldest);
                }
                else if (_routes[(fromRegion, toRegion)].Start is not null)
                {
                    _routes[(fromRegion, toRegion)] = (status, [.. portals], startKey); // a start-dependent answer gives way to the newer one
                }
            }
        }
    }
}
