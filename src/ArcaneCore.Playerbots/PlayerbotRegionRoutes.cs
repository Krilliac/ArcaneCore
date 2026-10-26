using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;

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
/// and plans the real path query to it.
/// </para>
/// </summary>
internal static class PlayerbotRegionRoutes
{
    /// <summary>The most routes one graph's cache keeps.</summary>
    internal const int MaxEntries = 256;

    /// <summary>The most region searches running on the pool at once, across all graphs.</summary>
    internal const int MaxInFlight = 8;

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
    /// pool is busy with <see cref="MaxInFlight"/> searches: the ask is repeated). <paramref name="portals"/> is a copy the caller owns.
    /// </summary>
    internal static bool TryRoute(NavRegionGraph graph, int fromRegion, Vector3 from, int toRegion, Vector3 to, bool mayDefer,
        out NavRegionRouteStatus status, out List<Vector3> portals)
    {
        if (fromRegion == toRegion)
        {
            status = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, out portals); // no search: one region
            return true;
        }

        RouteCache cache = Of(graph);
        if (cache.TryGet(fromRegion, toRegion, out status, out portals)) return true;
        if (!mayDefer)
        {
            cache.Search(fromRegion, from, toRegion, to);
            return cache.TryGet(fromRegion, toRegion, out status, out portals);
        }

        // Already queued by an earlier ask: nothing to start. Otherwise take a slot of the pool's bound, or leave the ask for a later think.
        if (cache.MarkInFlight(fromRegion, toRegion))
        {
            if (Interlocked.Increment(ref s_inFlight) <= MaxInFlight)
            {
                Task.Run(() =>
                {
                    try { cache.Search(fromRegion, from, toRegion, to); }
                    finally
                    {
                        cache.ClearInFlight(fromRegion, toRegion);
                        Interlocked.Decrement(ref s_inFlight);
                    }
                });
            }
            else
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
        private readonly Dictionary<(int From, int To), (NavRegionRouteStatus Status, Vector3[] Portals)> _routes = [];
        private readonly Queue<(int From, int To)> _order = new();
        private readonly HashSet<(int From, int To)> _inFlight = [];
        private long _searches;

        /// <summary>The searches this cache ran (misses).</summary>
        internal long Searches => Interlocked.Read(ref _searches);

        /// <summary>The routes kept.</summary>
        internal int Count
        {
            get { lock (_gate) return _routes.Count; }
        }

        internal bool TryGet(int from, int to, out NavRegionRouteStatus status, out List<Vector3> portals)
        {
            lock (_gate)
            {
                if (_routes.TryGetValue((from, to), out var route))
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

        internal bool MarkInFlight(int from, int to)
        {
            lock (_gate) return _inFlight.Add((from, to));
        }

        internal void ClearInFlight(int from, int to)
        {
            lock (_gate) _inFlight.Remove((from, to));
        }

        /// <summary>Run the region search and keep its answer. A faulted search is kept as "no decision" (Exhausted, no portals) and reported.</summary>
        internal void Search(int fromRegion, Vector3 from, int toRegion, Vector3 to)
        {
            NavRegionRouteStatus status;
            List<Vector3> portals;
            try
            {
                Interlocked.Increment(ref _searches);
                status = graph.FindRoute(fromRegion, from, toRegion, to, PlayerbotNavigation.RegionSearchNodes, out portals);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.region-route", ex);
                status = NavRegionRouteStatus.Exhausted;
                portals = [];
            }

            lock (_gate)
            {
                if (_routes.TryAdd((fromRegion, toRegion), (status, [.. portals])))
                {
                    _order.Enqueue((fromRegion, toRegion));
                    while (_routes.Count > MaxEntries && _order.TryDequeue(out (int From, int To) oldest)) _routes.Remove(oldest);
                }
            }
        }
    }
}
