using System.Diagnostics;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// What the navigation-mesh searches of the calling thread have cost so far: searches run, A* nodes created, nodes expanded,
/// navigation tiles read on demand (<see cref="PathOptions.LoadTiles"/>, <see cref="ICollisionTileDemand"/>) and the time those reads
/// took. Thread-static and only ever added to, so a caller takes <see cref="Current"/> before and after a piece of work and subtracts
/// (a bot's think, one query).
/// Each search adds a few integer increments; nothing allocates.
/// </summary>
public static class PathSearchCounters
{
    [ThreadStatic] private static long t_searches;
    [ThreadStatic] private static long t_nodes;
    [ThreadStatic] private static long t_expansions;
    [ThreadStatic] private static long t_tileLoads;
    [ThreadStatic] private static long t_tileLoadTicks;
    [ThreadStatic] private static long t_capped;
    [ThreadStatic] private static long t_deadlineHits;
    [ThreadStatic] private static long t_tilesPending;
    [ThreadStatic] private static bool t_lastPendingWasRealRead;
    [ThreadStatic] private static long t_gridTileLoads;
    [ThreadStatic] private static long t_tileFailures;
    [ThreadStatic] private static long t_gridTileLoadTicks;
    [ThreadStatic] private static TileLoadDetail t_slowestSync;
    [ThreadStatic] private static bool t_captureTiles;
    private static long s_processTileReads;
    private static long s_processTileReadTicks;
    private static long s_bvTreesRejected;
    [ThreadStatic] private static TileLoadDetail t_slowestTile;

    /// <summary>The slowest tile load since BeginTileCapture on this thread.</summary>
    public static TileLoadDetail SlowestTile => t_slowestTile;

    public static void BeginTileCapture()
    {
        t_slowestTile = default;
        t_captureTiles = true;
    }

    public static void EndTileCapture() => t_captureTiles = false;

    /// <summary>Searches on this thread so far that hit their node cap (<see cref="PathOptions.MaxSearchNodes"/>).</summary>
    public static long CappedSearches => t_capped;

    /// <summary>Searches on this thread so far that stopped at their wall-time bound (<see cref="PathOptions.MaxSearchMs"/>); each is also counted in <see cref="CappedSearches"/>.</summary>
    public static long DeadlineHits => t_deadlineHits;

    /// <summary>A search stopped at its wall-time bound.</summary>
    internal static void RecordDeadline() => t_deadlineHits++;

    /// <summary>A* nodes created on this thread so far.</summary>
    public static long NodesCreated => t_nodes;

    /// <summary>This thread's totals so far.</summary>
    public static PathSearchTotals Current => new(t_searches, t_nodes, t_expansions, t_tileLoads, t_tileLoadTicks, t_capped, t_tilesPending,
        t_gridTileLoads, t_gridTileLoadTicks, t_tileFailures, t_deadlineHits);

    /// <summary>
    /// The slowest navigation tile installation (an on-demand read or a grid's tile load) on this thread since
    /// <see cref="ResetSlowestSyncTile"/>; always tracked, independent of <see cref="BeginTileCapture"/> (the slow-map-update line reads it).
    /// </summary>
    public static TileLoadDetail SlowestSyncTile => t_slowestSync;

    /// <summary>Forget <see cref="SlowestSyncTile"/> (this thread).</summary>
    public static void ResetSlowestSyncTile() => t_slowestSync = default;

    /// <summary>
    /// Navigation tile installations made on any thread so far: on-demand reads and grid tile loads, and the time they took
    /// (<see cref="Stopwatch"/> ticks). Process-wide for the metrics feature; the world thread makes essentially all of them.
    /// </summary>
    public static (long Reads, long Ticks) ProcessTileReads
        => (Interlocked.Read(ref s_processTileReads), Interlocked.Read(ref s_processTileReadTicks));

    /// <summary>
    /// Navigation tiles read, on any thread, whose BV tree was absent or failed verification (<c>NavMeshTile</c>), so nearest-polygon
    /// lookups on them scan every polygon. Non-zero on a map's data means that lookup is slow there. Bumped once per tile at parse.
    /// </summary>
    public static long BvTreesRejected => Interlocked.Read(ref s_bvTreesRejected);

    internal static void NoteBvTreeRejected() => Interlocked.Increment(ref s_bvTreesRejected);

    /// <summary>
    /// Times a search on this thread needed a navigation tile that was not resident and answered "pending" instead of reading it
    /// (<see cref="PathOptions.NonBlockingTiles"/>). A caller takes it before a piece of planning and compares afterwards: a change
    /// means the planning's failure was for want of a tile still being read, not a verdict on the destination.
    /// </summary>
    public static long TilesPending => t_tilesPending;

    /// <summary>A search needed a tile whose background read had not finished (or had to be started).</summary>
    internal static void RecordTilePending()
    {
        t_tilesPending++;
        t_lastPendingWasRealRead = false;
    }

    /// <summary>
    /// <see cref="RecordTilePending"/> for a tile whose read really runs on a background thread (the navigation mesh), as opposed to a
    /// pathfinder that only answers "pending" (tests): a waiter then owes the read its wall-clock time.
    /// </summary>
    internal static void RecordTilePendingOnRealRead()
    {
        t_tilesPending++;
        t_lastPendingWasRealRead = true;
    }

    /// <summary>Whether the latest pending answer on this thread was for a background read in flight (see <see cref="RecordTilePendingOnRealRead"/>).</summary>
    public static bool LastPendingWasRealRead => t_lastPendingWasRealRead;

    /// <summary>One A* search ended: <paramref name="nodes"/> created, <paramref name="expansions"/> expanded, whether the node cap was hit.</summary>
    internal static void RecordSearch(int nodes, int expansions, bool capped)
    {
        t_searches++;
        t_nodes += nodes;
        t_expansions += expansions;
        if (capped) t_capped++;
    }

    /// <summary>
    /// A navigation tile was read for a query, taking <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks). A read that installed
    /// nothing (<paramref name="installed"/> false: no such tile, unreadable file) counts as a failure, but its time still counts.
    /// </summary>
    internal static void RecordTileLoad(long ticks, uint mapId, int tileX, int tileY, bool prefetchHit, bool installed = true)
    {
        if (installed) t_tileLoads++; else t_tileFailures++;
        t_tileLoadTicks += ticks;
        NoteSync(ticks, mapId, tileX, tileY, prefetchHit, installed);
    }

    /// <summary>A grid's navigation tile was installed on this thread (<c>OnTileLoaded</c>), taking <paramref name="ticks"/>; a prefetch hit only adds the parsed tile.</summary>
    internal static void RecordGridTileLoad(long ticks, uint mapId, int tileX, int tileY, bool prefetchHit, bool installed = true)
    {
        if (installed) t_gridTileLoads++; else t_tileFailures++;
        t_gridTileLoadTicks += ticks;
        NoteSync(ticks, mapId, tileX, tileY, prefetchHit, installed);
    }

    private static void NoteSync(long ticks, uint mapId, int tileX, int tileY, bool prefetchHit, bool installed)
    {
        if (installed) Interlocked.Increment(ref s_processTileReads);
        Interlocked.Add(ref s_processTileReadTicks, ticks);
        if (ticks > t_slowestSync.Ticks)
            t_slowestSync = new TileLoadDetail(mapId, tileX, tileY, ticks, prefetchHit);
        if (t_captureTiles && ticks > t_slowestTile.Ticks)
            t_slowestTile = new TileLoadDetail(mapId, tileX, tileY, ticks, prefetchHit);
    }
}

/// <summary>Largest synchronous navmesh tile installation within an active capture.</summary>
public readonly record struct TileLoadDetail(uint MapId, int X, int Y, long Ticks, bool PrefetchHit);

/// <summary>A snapshot of <see cref="PathSearchCounters"/>; the difference of two is the work between them.</summary>
public readonly record struct PathSearchTotals(long Searches, long Nodes, long Expansions, long TileLoads, long TileLoadTicks, long Capped, long TilesPending = 0,
    long GridTileLoads = 0, long GridTileLoadTicks = 0, long TileLoadFailures = 0, long DeadlineHits = 0)
{
    public static PathSearchTotals operator -(PathSearchTotals a, PathSearchTotals b)
        => new(a.Searches - b.Searches, a.Nodes - b.Nodes, a.Expansions - b.Expansions, a.TileLoads - b.TileLoads,
            a.TileLoadTicks - b.TileLoadTicks, a.Capped - b.Capped, a.TilesPending - b.TilesPending,
            a.GridTileLoads - b.GridTileLoads, a.GridTileLoadTicks - b.GridTileLoadTicks, a.TileLoadFailures - b.TileLoadFailures,
            a.DeadlineHits - b.DeadlineHits);

    /// <summary>The tile reads' time in milliseconds.</summary>
    public double TileLoadMs => TileLoadTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Every navigation tile installation: on-demand reads (<see cref="TileLoads"/>) and grid tile loads.</summary>
    public long SyncTileReads => TileLoads + GridTileLoads;

    /// <summary>The time of every installation in milliseconds.</summary>
    public double SyncTileReadMs => (TileLoadTicks + GridTileLoadTicks) * 1000.0 / Stopwatch.Frequency;
}
