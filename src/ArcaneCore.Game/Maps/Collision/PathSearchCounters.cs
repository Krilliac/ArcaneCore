using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;

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

/// <summary>Which kind of caller made a synchronous tile file read (<see cref="SyncTileReadCounters"/>).</summary>
public enum SyncReadCaller
{
    /// <summary>Not inside a map's update: the world thread's own services (teleports, logins, bot planning) or any other thread.</summary>
    OutsideMapUpdate = 0,

    /// <summary>The world thread, inside a map's update (the serial maps loop, or a map it runs itself in the parallel phase) without the serial gate.</summary>
    WorldThread = 1,

    /// <summary>A map worker thread in the parallel phase, not holding the serial gate.</summary>
    MapWorker = 2,

    /// <summary>Any thread inside a parallel-phase map update while it holds the world's single serial gate (the world thread included): every other map waits behind its read.</summary>
    MapWorkerHoldingGate = 3,
}

/// <summary>Which tile file a synchronous read was for.</summary>
public enum SyncReadFile
{
    /// <summary>A terrain <c>.map</c> file.</summary>
    Terrain = 0,

    /// <summary>A vmap tile file (<c>.vmtile</c>).</summary>
    VMap = 1,
}

/// <summary>Why an untaken prefetch left its tile prefetch cache.</summary>
public enum PrefetchDrop
{
    /// <summary>The approach budget was full and it was the oldest.</summary>
    Evicted = 0,

    /// <summary>The arrival budget was full and it was the oldest.</summary>
    ArrivalEvicted = 1,

    /// <summary>Nobody took it before it expired.</summary>
    Expired = 2,
}

/// <summary>
/// Terrain and vmap tile files read synchronously, that is a prefetch miss: the loader read and parsed the file on the thread that needed
/// the tile, inside a tick (the navigation mesh's own reads are in <see cref="PathSearchCounters"/>). Terrain time includes parsing the tile;
/// vmap time covers only the <c>.vmtile</c> read (not parsing, not the model reads made while instantiating). Counted per caller kind (<see cref="SyncReadCaller"/>) with the time they took; a read over
/// <see cref="AnomalyMs"/> is also reported to the anomaly ledger as <c>sync-tile-read</c> (subsystem <c>terrain</c> or <c>collision</c>).
/// Beside them, the prefetches of those files dropped untaken (<see cref="PrefetchDrops"/>): a dropped prefetch is one way to a later read,
/// so these tell a prefetch that was thrown away from one that came too late or was never asked for. A drop is not proof of a read: the tile
/// may never have been needed.
/// <para>
/// Thread affinity: any thread. The per-thread totals are thread-static (a caller takes <see cref="Current"/> before and after a piece
/// of work and subtracts); the process totals are interlocked. Each read adds a few integer increments; nothing allocates unless the
/// anomaly fires.
/// </para>
/// </summary>
public static class SyncTileReadCounters
{
    /// <summary>A synchronous read over this many milliseconds is reported as an anomaly.</summary>
    public const double AnomalyMs = 100;

    private const int CallerKinds = 4;
    private const int FileKinds = 2;
    private const int DropKinds = 3;

    [ThreadStatic] private static long t_reads;
    [ThreadStatic] private static long t_ticks;
    [ThreadStatic] private static long t_gateReads;
    [ThreadStatic] private static int t_serialMapUpdateDepth;
    private static readonly long[] s_reads = new long[CallerKinds * FileKinds];
    private static readonly long[] s_ticks = new long[CallerKinds * FileKinds];
    private static readonly long[] s_drops = new long[FileKinds * DropKinds];

    /// <summary>This thread's totals so far.</summary>
    public static SyncFileReadTotals Current => new(t_reads, t_ticks, t_gateReads);

    /// <summary>Reads and time (<see cref="Stopwatch"/> ticks) made so far by any thread, of one caller kind and file kind.</summary>
    public static (long Reads, long Ticks) Process(SyncReadCaller caller, SyncReadFile file)
    {
        int slot = SlotOf(caller, file);
        return (Interlocked.Read(ref s_reads[slot]), Interlocked.Read(ref s_ticks[slot]));
    }

    /// <summary>Prefetches of one file kind dropped untaken so far, for one reason, by any thread.</summary>
    public static long PrefetchDrops(SyncReadFile file, PrefetchDrop drop) => Interlocked.Read(ref s_drops[DropSlotOf(file, drop)]);

    /// <summary>
    /// The kind of the calling thread. Inside a parallel-phase map update (<see cref="TickOwner"/>) a thread holding the serial gate is
    /// <see cref="SyncReadCaller.MapWorkerHoldingGate"/> whichever thread it is (the world thread runs maps in the parallel phase too, and every
    /// other map still waits behind its read); otherwise the world thread is <see cref="SyncReadCaller.WorldThread"/> and any other thread
    /// <see cref="SyncReadCaller.MapWorker"/>. The serial maps loop has no <see cref="TickOwner"/>: it marks itself with <see cref="EnterMapUpdate"/>.
    /// </summary>
    public static SyncReadCaller CallerKind()
    {
        WorldRuntime? world = TickOwner.CurrentWorld;
        if (world is null)
        {
            return t_serialMapUpdateDepth > 0 ? SyncReadCaller.WorldThread : SyncReadCaller.OutsideMapUpdate;
        }

        if (world.IsParallelPhase && world.HoldsSerialGate)
        {
            return SyncReadCaller.MapWorkerHoldingGate;
        }

        return world.IsMainWorldThread ? SyncReadCaller.WorldThread : SyncReadCaller.MapWorker;
    }

    /// <summary>Marks the calling thread as inside a map's update in the serial maps loop (no <see cref="TickOwner"/> there) until disposed; nests.</summary>
    internal static MapUpdateScope EnterMapUpdate()
    {
        t_serialMapUpdateDepth++;
        return default;
    }

    /// <summary>A map update in the serial loop; disposing it leaves the update.</summary>
    internal readonly struct MapUpdateScope : IDisposable
    {
        public void Dispose() => t_serialMapUpdateDepth--;
    }

    /// <summary>
    /// The process totals for the hang and overrun detail (empty until a read or a drop happened): per caller kind, the reads of both
    /// file kinds with their time, then per file kind the prefetches dropped untaken, as "; "-separated segments. Reads outside a map update
    /// are in it, which no map's slow-update line can show.
    /// </summary>
    public static string DescribeProcess()
    {
        var sb = new System.Text.StringBuilder();
        for (int caller = 0; caller < CallerKinds; caller++)
        {
            long reads = 0;
            long ticks = 0;
            for (int file = 0; file < FileKinds; file++)
            {
                (long r, long t) = Process((SyncReadCaller)caller, (SyncReadFile)file);
                reads += r;
                ticks += t;
            }

            if (reads == 0)
            {
                continue;
            }

            sb.Append(sb.Length == 0 ? "terrain/vmap tile file reads since start (prefetch misses): " : ", ")
                .Append((SyncReadCaller)caller).Append(' ').Append(reads.ToString(CultureInfo.InvariantCulture)).Append(" (")
                .Append((ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)).Append(" ms)");
        }

        AppendDrops(sb);
        return sb.ToString();
    }

    private static void AppendDrops(System.Text.StringBuilder sb)
    {
        bool any = false;
        for (int file = 0; file < FileKinds; file++)
        {
            long evicted = PrefetchDrops((SyncReadFile)file, PrefetchDrop.Evicted);
            long arrivalEvicted = PrefetchDrops((SyncReadFile)file, PrefetchDrop.ArrivalEvicted);
            long expired = PrefetchDrops((SyncReadFile)file, PrefetchDrop.Expired);
            if (evicted + arrivalEvicted + expired == 0)
            {
                continue;
            }

            if (any)
            {
                sb.Append(", ");
            }
            else
            {
                sb.Append(sb.Length == 0 ? string.Empty : "; ").Append("terrain/vmap tile prefetches dropped untaken since start: ");
                any = true;
            }

            sb.Append(CultureInfo.InvariantCulture, $"{(SyncReadFile)file} {evicted} evicted, {arrivalEvicted} arrival evicted, {expired} expired");
        }
    }

    /// <summary>A prefetch of <paramref name="file"/> left its cache untaken.</summary>
    internal static void RecordPrefetchDrop(SyncReadFile file, PrefetchDrop drop) => Interlocked.Increment(ref s_drops[DropSlotOf(file, drop)]);

    /// <summary>A tile file was read on the calling thread (a prefetch miss), taking <paramref name="ticks"/> (<see cref="Stopwatch"/> ticks).</summary>
    internal static void Record(SyncReadFile file, long ticks, uint mapId, int tileX, int tileY)
        => Record(CallerKind(), file, ticks, mapId, tileX, tileY);

    /// <summary><see cref="Record(SyncReadFile, long, uint, int, int)"/> for a given caller kind (tests).</summary>
    internal static void Record(SyncReadCaller caller, SyncReadFile file, long ticks, uint mapId, int tileX, int tileY)
    {
        int slot = SlotOf(caller, file);
        Interlocked.Increment(ref s_reads[slot]);
        Interlocked.Add(ref s_ticks[slot], ticks);
        t_reads++;
        t_ticks += ticks;
        if (caller == SyncReadCaller.MapWorkerHoldingGate)
        {
            t_gateReads++;
        }

        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        if (ms > AnomalyMs)
        {
            AnomalyLedger.Shared.Report(file == SyncReadFile.Terrain ? "terrain" : "collision", "sync-tile-read",
                string.Create(CultureInfo.InvariantCulture, $"map {mapId} tile ({tileX}, {tileY}) by {caller}"),
                string.Create(CultureInfo.InvariantCulture, $"{file} tile read on the {caller} thread took {ms:F0} ms: the prefetch missed"));
        }
    }

    private static int SlotOf(SyncReadCaller caller, SyncReadFile file) => ((int)caller * FileKinds) + (int)file;

    private static int DropSlotOf(SyncReadFile file, PrefetchDrop drop) => ((int)file * DropKinds) + (int)drop;
}

/// <summary>A snapshot of <see cref="SyncTileReadCounters.Current"/>; the difference of two is the work between them.</summary>
public readonly record struct SyncFileReadTotals(long Reads, long Ticks, long ReadsHoldingGate)
{
    public static SyncFileReadTotals operator -(SyncFileReadTotals a, SyncFileReadTotals b)
        => new(a.Reads - b.Reads, a.Ticks - b.Ticks, a.ReadsHoldingGate - b.ReadsHoldingGate);

    /// <summary>The reads' time in milliseconds.</summary>
    public double Ms => Ticks * 1000.0 / Stopwatch.Frequency;
}
