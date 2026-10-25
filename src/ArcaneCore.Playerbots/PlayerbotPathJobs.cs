using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where the bots' path jobs run (production: the shared <see cref="NavJobSystem"/> pool; tests: a manual runner).</summary>
internal interface IPlayerbotPathJobRunner
{
    /// <summary>Queue a job; false (nothing queued) when the pool is full or stopped. Any thread.</summary>
    bool TrySubmit(NavJob job);

    /// <summary>Worker threads behind the runner.</summary>
    int Threads { get; }
}

/// <summary><see cref="IPlayerbotPathJobRunner"/> over a <see cref="NavJobSystem"/>, started on first use (the creatures' process pool when it is running).</summary>
internal sealed class NavJobSystemRunner(Func<NavJobSystem> pool) : IPlayerbotPathJobRunner
{
    private NavJobSystem? _system;

    private NavJobSystem System => _system ??= pool();

    public bool TrySubmit(NavJob job) => System.TrySubmit(job);

    public int Threads => System.Threads;
}

/// <summary>How <see cref="PlayerbotPathJobs.Resolve"/> answered.</summary>
internal enum JobAnswer : byte
{
    /// <summary>A finished job's answer was applied: the result is a real mesh answer.</summary>
    Applied,

    /// <summary>The job (or a tile read) is still under way: a pending result.</summary>
    Waiting,

    /// <summary>A job was queued for this query now: a pending result.</summary>
    Submitted,

    /// <summary>The per-bot or global budget holds the query back: a pending result, the bot is in the admission line.</summary>
    BudgetRefused,

    /// <summary>The wait cannot go on (repeated cancellations, a fault, too long): the caller searches once on the world thread.</summary>
    Escalated,
}

/// <summary>Counters of a <see cref="PlayerbotPathJobs"/>.</summary>
internal readonly record struct PlayerbotPathJobTotals(long Submitted, long Applied, long Waits, long Stale, long Canceled, long Faulted,
    long BudgetRefused, long Escalations, long DeadlineHits,
    long StaleInvalid = 0, long StaleReachesAsked = 0, long StaleJoinsFrom = 0, long StaleWalked = 0, long StaleAway = 0);
// Stale is the sum of the five Stale* reasons: the answer's player, map, pathfinder or dataset changed (Invalid); its route ends too far
// from the destination asked now (ReachesAsked); the bot stands off the route (JoinsFrom); the bot walked past the entry's bound before
// the ask (Walked); the bot left the map or the world (Away).

/// <summary>
/// The bots' far navigation-mesh searches as detached jobs (multithreading plan W5): <see cref="PlayerbotThinkProbe.Find"/> routes a
/// query here when it is eligible (<see cref="Eligible"/>), the search runs on <see cref="NavJobSystem"/> workers over the loaded tiles,
/// and the bot answers "pending" (<see cref="PathType.TilesPending"/>, counted in <see cref="PendingAnswers"/>) until a later think
/// finds the answer and applies it. All planning after the mesh answer (risk, detours, smoothing, blacklist, anomaly checks) still runs
/// on the world thread exactly as for a synchronous answer; the cache holds raw mesh answers only.
/// <para>
/// Ownership: one per <c>ManagedPlayerbotFeature</c>; its methods are called on the world thread, and a lock makes the few off-thread
/// calls (a logout's <see cref="Forget"/>) safe. Workers touch only their <see cref="NavJob"/>. A pending job answer is never an
/// unreachable verdict (<see cref="TileWait.Hold"/>); a wait ends by <see cref="JobAnswer.Escalated"/> (one bounded synchronous search)
/// after repeated cancellations, a fault, or <see cref="PlayerbotOptions.PathJobWaitMaxMs"/>. The thread-static context
/// (<see cref="Bind"/>, <see cref="Deferrable"/>) is the thread of the think.
/// </para>
/// </summary>
internal sealed class PlayerbotPathJobs
{
    /// <summary>Finished answers older than this without an ask are dropped by <see cref="Sweep"/> (the creatures' value).</summary>
    internal const uint UnclaimedMs = 10_000;

    /// <summary>The most finished answers one bot keeps.</summary>
    internal const int MaxEntriesPerBot = 8;

    /// <summary>The most bytes of finished answers one bot keeps.</summary>
    internal const int MaxRetainedBytes = 64 * 1024;

    /// <summary>Consecutive cancellations or stale answers of one query before it escalates.</summary>
    internal const int EscalateAfterFailures = 3;

    /// <summary>One yard of <see cref="PlayerbotNavigation.SearchNodes"/>: a cached answer computed with at least this much less than asked is still the answer.</summary>
    internal const int NodeTolerance = 48;

    /// <summary>
    /// How far a finished job's destination may lie from the destination now asked and still answer it: 15% of the distance, 10..40 yards
    /// (a leader running at 7 yards per second drifts 7 to 14 yards over the 1 to 2 seconds a job takes to be asked, run and collected).
    /// </summary>
    internal static float DestinationTolerance(Vector3 from, Vector3 to) => Math.Clamp(Vector3.Distance(from, to) * 0.15f, 10f, 40f);

    /// <summary>How far the bot may have walked from where its main query was asked and still collect the answer: 2 seconds at the walking speed, at least 15 yards.</summary>
    internal static float MovedYards(PlayerbotOptions options) => MathF.Max(15f, options.MoveSpeed * 2f);

    /// <summary>A destination farther than this from the applied answer's gets a refresh job in the same think (the follower's re-plan distance).</summary>
    internal const float RefreshYards = 2f;

    /// <summary>The bot must stand within this of the answer's early legs to be spliced onto it (<see cref="PlayerbotMotion.OffLegYards"/>).</summary>
    internal const float RejoinYards = PlayerbotMotion.OffLegYards;

    /// <summary>Consecutive deadline-capped answers of one bot that are dropped so the next ask searches again; after them the capped answer is kept.</summary>
    internal const int MaxCappedRetries = 3;

    /// <summary>A wait longer than this (wall ms) is reported once (<c>nav/job-wait-long</c>).</summary>
    internal const long WaitLongMs = 5_000;

    [ThreadStatic] private static PlayerbotPathJobs? t_bound;
    [ThreadStatic] private static Guid t_botId;
    [ThreadStatic] private static Player? t_player;
    [ThreadStatic] private static int t_deferDepth;
    [ThreadStatic] private static int t_noDeferDepth;
    [ThreadStatic] private static long t_pendingAnswers;
    [ThreadStatic] private static bool t_lastPendingWasJob;

    private readonly object _gate = new();
    private readonly PlayerbotOptions _options;
    private readonly IPlayerbotPathJobRunner _runner;
    private readonly Func<uint> _clockMs;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, BotState> _bots = [];
    private readonly List<NavJob> _active = [];
    private readonly LinkedList<Guid> _line = new();
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _lineNodes = [];
    private long _activeNodes;
    private PlayerbotPathJobTotals _totals;
    private PlayerbotPathJobTotals _logged;
    private uint _lastSummaryMs;

    internal PlayerbotPathJobs(PlayerbotOptions options, IPlayerbotPathJobRunner runner, Func<uint> clockMs, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(clockMs);
        _options = options;
        _runner = runner;
        _clockMs = clockMs;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Test seam: the wall clock in milliseconds (the wait bounds are wall time; the game clock may run faster).</summary>
    internal Func<long> WallMs { get; init; } = static () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>The service the running think is bound to (<see cref="Bind"/>), if any.</summary>
    internal static PlayerbotPathJobs? Bound => t_bound;

    /// <summary>Path-job answers of "pending" given on this thread so far (a query waiting for its job).</summary>
    internal static long PendingAnswers => t_pendingAnswers;

    /// <summary>Whether the latest pending answer on this thread was for a path job, not a navigation tile read (<see cref="TileWait.Hold"/>).</summary>
    internal static bool LastPendingWasJob => t_lastPendingWasJob;

    internal static void NoteTilePending() => t_lastPendingWasJob = false;

    /// <summary>Whether the running query may be answered pending (a <see cref="Deferrable"/> scope, not inside <see cref="NoDefer"/>).</summary>
    internal static bool InDeferrableScope => t_deferDepth > 0 && t_noDeferDepth == 0;

    internal PlayerbotPathJobTotals Totals
    {
        get
        {
            lock (_gate) return _totals;
        }
    }

    /// <summary>Jobs submitted and not yet finished by a worker.</summary>
    internal int InFlight
    {
        get
        {
            lock (_gate)
            {
                PruneActive();
                return _active.Count;
            }
        }
    }

    /// <summary>The sum of the node budgets of <see cref="InFlight"/> jobs.</summary>
    internal long InFlightNodes
    {
        get
        {
            lock (_gate)
            {
                PruneActive();
                return _activeNodes;
            }
        }
    }

    /// <summary>Bots waiting in the admission line.</summary>
    internal int Waiters
    {
        get
        {
            lock (_gate) return _line.Count;
        }
    }

    /// <summary>The bot ids in the admission line, oldest first (tests).</summary>
    internal Guid[] LineForTests()
    {
        lock (_gate) return [.. _line];
    }

    // ---- thread context -----------------------------------------------------------------------------------------------------

    /// <summary>Bind the running think (thread-static) to <paramref name="botId"/> and <paramref name="player"/> until the scope is disposed.</summary>
    internal BindScope Bind(Guid botId, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var scope = new BindScope(t_bound, t_botId, t_player);
        t_bound = this;
        t_botId = botId;
        t_player = player;
        return scope;
    }

    /// <summary>The scope of <see cref="Bind"/>.</summary>
    internal readonly struct BindScope(PlayerbotPathJobs? previous, Guid previousBot, Player? previousPlayer) : IDisposable
    {
        public void Dispose()
        {
            t_bound = previous;
            t_botId = previousBot;
            t_player = previousPlayer;
            t_lastPendingWasJob = false;
        }
    }

    /// <summary>The queries made until the scope is disposed may be answered pending (the planning wrappers: they all handle <see cref="PlanOutcome.Pending"/>).</summary>
    internal static DeferrableScope Deferrable()
    {
        t_deferDepth++;
        return default;
    }

    /// <summary>The scope of <see cref="Deferrable"/>.</summary>
    internal readonly struct DeferrableScope : IDisposable
    {
        public void Dispose()
        {
            if (t_deferDepth > 0) t_deferDepth--;
        }
    }

    /// <summary>The queries made until the scope is disposed run synchronously whatever encloses them (chase and trial plans).</summary>
    internal static NoDeferScope NoDefer()
    {
        t_noDeferDepth++;
        return new NoDeferScope(true);
    }

    /// <summary>The scope of <see cref="NoDefer"/>. A default scope never entered, so disposing it leaves an enclosing scope's depth alone.</summary>
    internal readonly struct NoDeferScope(bool entered) : IDisposable
    {
        public void Dispose()
        {
            if (entered && t_noDeferDepth > 0) t_noDeferDepth--;
        }
    }

    // ---- eligibility --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a query with <paramref name="options"/> on <paramref name="collision"/>'s map goes to a job: a bound think, a
    /// <see cref="Deferrable"/> scope, a caller that already accepts a pending answer (LoadTiles and NonBlockingTiles), more than
    /// <see cref="PlayerbotOptions.PathJobSyncNodeThreshold"/> nodes, a walker, a map with shared tiled search, and <see cref="PlayerbotOptions.PathJobs"/> on.
    /// </summary>
    internal bool Eligible(MapCollision collision, PathOptions options, out ISharedTiledPathSearch shared)
    {
        shared = null!;
        if (!_options.PathJobs || t_player is not { } player || !InDeferrableScope
            || !options.LoadTiles || !options.NonBlockingTiles
            || options.MaxSearchNodes <= _options.PathJobSyncNodeThreshold
            || options.Mover is { CanFly: true }
            || !ReferenceEquals(collision.Map, player.Map) || !player.IsInWorld
            || collision.PathfinderForMap is not ISharedTiledPathSearch tiled
            || !tiled.CanSearchShared(collision.Map.MapId))
        {
            return false;
        }

        shared = tiled;
        return true;
    }

    // ---- the query ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The answer to one eligible query (<see cref="Eligible"/>): a finished job's answer, or a <see cref="PathType.TilesPending"/> result
    /// after queueing (or keeping) its job, or, for <see cref="JobAnswer.Escalated"/>, nothing (the caller searches once itself, with the
    /// world-thread deadline). <paramref name="job"/> is the applied job (its search counters, worker time and node count).
    /// </summary>
    internal PathResult Resolve(ISharedTiledPathSearch shared, Map map, Vector3 from, Vector3 to, PathOptions options, out JobAnswer how, out NavJob? job)
    {
        job = null;
        Guid botId = t_botId;
        Player player = t_player ?? throw new InvalidOperationException("no bot is bound to this think");
        uint nowMs = _clockMs();
        long wallMs = WallMs();
        PathOptions key = options with { MaxSearchNodes = 0, MaxSearchMs = 0, CancellationToken = default };
        lock (_gate)
        {
            if (!_bots.TryGetValue(botId, out BotState? state))
            {
                _bots[botId] = state = new BotState();
            }

            if (state.PendingSinceWallMs != 0 && unchecked(nowMs - state.LastAskMs) > UnaskedAbandonMs()) state.PendingSinceWallMs = 0; // an old episode
            state.LastAskMs = nowMs;
            // The same source as the query's from (PlayerbotMotion.CurrentPosition): player.X lags by up to a heartbeat while walking.
            Vector3 botPosition = PlayerbotMotion.CurrentPosition(player);
            DropStale(state, botPosition);
            Entry? entry = state.Find(key, to, from, botPosition, options.MaxSearchNodes, _options);
            Carry carry = default;
            JobAnswer settled = JobAnswer.Submitted;
            if (entry is not null)
            {
                entry.LastAskMs = nowMs;
                carry = new Carry(entry.Failures, entry.StaleFailures, entry.CanceledFailures);
                if (entry.Job.IsDone)
                {
                    PathResult? applied = TryApply(shared, map, player, entry, to, from, wallMs, state, ref carry, out how, out settled);
                    if (applied is not null)
                    {
                        state.PendingSinceWallMs = 0;
                        job = entry.Job;
                        // The answer is for where the destination was; the next one tracks where it is now.
                        if (Vector3.DistanceSquared(entry.To, to) > RefreshYards * RefreshYards)
                            TryRefresh(shared, map, player, botId, state, from, to, options, key, nowMs, wallMs);
                        return applied;
                    }

                    if (settled == JobAnswer.Escalated)
                    {
                        state.PendingSinceWallMs = 0;
                        string why = entry.Job.Status == NavJobStatus.Faulted
                            ? "faulted"
                            : string.Create(CultureInfo.InvariantCulture,
                                $"failed repeatedly ({carry.StaleFailures} stale, {carry.CanceledFailures} canceled)");
                        return Escalate(state, null, why, player, from, to, out how);
                    }
                }
            }

            // The wait is the bot's, not the entry's: a destination that moves every think, or a fallback query that replaces the
            // main one, starts a new entry each time but the clock goes on, so the wait always ends (one bounded synchronous search).
            if (state.PendingSinceWallMs != 0 && wallMs - state.PendingSinceWallMs > _options.PathJobWaitMaxMs)
            {
                state.PendingSinceWallMs = 0;
                return Escalate(state, entry, "waited too long", player, from, to, out how);
            }

            if (entry is not null && !entry.Job.IsDone)
            {
                ReportLongWait(entry, state, wallMs, player);
                return Pending(state, wallMs, from, JobAnswer.Waiting, out how);
            }

            if (settled == JobAnswer.Waiting)
            {
                return Pending(state, wallMs, from, JobAnswer.Waiting, out how); // a missed tile is still being read
            }

            // The old answer (if any) is dropped; a fresh job replaces it below, carrying the failure count.
            return Submit(shared, map, player, botId, state, from, to, options, key, carry, nowMs, wallMs, out how);
        }
    }

    /// <summary>The consecutive failures of one query: all of them, and how many of those were stale answers (the rest were cancellations).</summary>
    private readonly record struct Carry(int Failures, int StaleFailures, int CanceledFailures);

    /// <summary>Which check dropped a finished answer as stale (the <see cref="PlayerbotPathJobTotals"/> breakdown).</summary>
    private enum StaleReason : byte
    {
        /// <summary>No result, or the player, map, pathfinder or dataset changed (<see cref="StillValid"/>).</summary>
        Invalid,

        /// <summary>The route ends too far from the destination asked now (<see cref="ReachesAsked"/>).</summary>
        ReachesAsked,

        /// <summary>The bot stands off the route (<see cref="JoinsFrom"/>).</summary>
        JoinsFrom,
    }

    private PathResult Pending(BotState state, long wallMs, Vector3 from, JobAnswer how, out JobAnswer answer)
    {
        answer = how;
        if (state.PendingSinceWallMs == 0) state.PendingSinceWallMs = wallMs;
        if (how == JobAnswer.Waiting) _totals = _totals with { Waits = _totals.Waits + 1 };
        t_pendingAnswers++;
        t_lastPendingWasJob = true;
        return new PathResult(PathType.TilesPending, [from]);
    }

    private PathResult Escalate(BotState state, Entry? entry, string why, Player player, Vector3 from, Vector3 to, out JobAnswer how)
    {
        if (entry is not null)
        {
            entry.Job.Abandon();
            state.Remove(entry);
        }

        LeaveLine(t_botId);
        _totals = _totals with { Escalations = _totals.Escalations + 1 };
        how = JobAnswer.Escalated;
        if (player.IsInWorld)
        {
            PlayerbotAnomalies.Report(player, "nav", "job-escalated",
                string.Create(CultureInfo.InvariantCulture,
                    $"path job {why}: searching once on the world thread from ({from.X:F1}, {from.Y:F1}, {from.Z:F1}) to ({to.X:F1}, {to.Y:F1}, {to.Z:F1}); {_totals.Escalations} escalations"),
                AnomalySeverity.Info);
        }

        return new PathResult(PathType.TilesPending, [from]); // not used: the caller searches itself
    }

    private void ReportLongWait(Entry entry, BotState state, long wallMs, Player player)
    {
        // No run of pending answers is open right after an applied answer or an old-episode reset (PendingSinceWallMs is 0 until Pending
        // sets it): the job's own submit time is the start then, never the clock's zero.
        long since = state.PendingSinceWallMs != 0 ? state.PendingSinceWallMs : entry.SubmittedWallMs;
        long waited = wallMs - since;
        if (entry.LongWaitReported || waited <= WaitLongMs || !player.IsInWorld) return;
        entry.LongWaitReported = true;
        PlayerbotAnomalies.Report(player, "nav", "job-wait-long",
            string.Create(CultureInfo.InvariantCulture, $"a path job has been pending for {waited} ms ({entry.Job.Status}, {entry.MaxNodes} nodes)"),
            AnomalySeverity.Info);
    }

    /// <summary>The applied answer of a finished job; null when it was dropped (<paramref name="settled"/> says what the caller does next).</summary>
    private PathResult? TryApply(ISharedTiledPathSearch shared, Map map, Player player, Entry entry, Vector3 to, Vector3 from, long wallMs,
        BotState state, ref Carry carry, out JobAnswer how, out JobAnswer settled)
    {
        how = JobAnswer.Applied;
        settled = JobAnswer.Submitted;
        NavJob job = entry.Job;
        switch (job.Status)
        {
            case NavJobStatus.Faulted:
                _totals = _totals with { Faulted = _totals.Faulted + 1 };
                state.Remove(entry);
                settled = JobAnswer.Escalated;
                return null;
            case NavJobStatus.Canceled:
                _totals = _totals with { Canceled = _totals.Canceled + 1 };
                state.Remove(entry);
                carry = carry with { Failures = carry.Failures + 1, CanceledFailures = carry.CanceledFailures + 1 };
                if (carry.Failures >= EscalateAfterFailures) settled = JobAnswer.Escalated;
                return null;
        }

        List<Vector3> points = [];
        StaleReason? reason = null;
        PathResult? finished = job.Result;
        if (finished is null || !StillValid(shared, map, player, entry))
            reason = StaleReason.Invalid;
        else if (!ReachesAsked(entry, finished, to, from))
            reason = StaleReason.ReachesAsked;
        else if (!JoinsFrom(entry, finished, from, out points))
            reason = StaleReason.JoinsFrom;

        if (reason is { } why)
        {
            _totals = why switch
            {
                StaleReason.Invalid => _totals with { Stale = _totals.Stale + 1, StaleInvalid = _totals.StaleInvalid + 1 },
                StaleReason.ReachesAsked => _totals with { Stale = _totals.Stale + 1, StaleReachesAsked = _totals.StaleReachesAsked + 1 },
                _ => _totals with { Stale = _totals.Stale + 1, StaleJoinsFrom = _totals.StaleJoinsFrom + 1 },
            };
            state.Remove(entry);
            carry = carry with { Failures = carry.Failures + 1, StaleFailures = carry.StaleFailures + 1 };
            if (carry.Failures >= EscalateAfterFailures) settled = JobAnswer.Escalated;
            return null;
        }

        PathResult result = finished!; // non-null: a null result is the Invalid reason above
        if ((result.Type & (PathType.Incomplete | PathType.NoPath | PathType.NotUsingPath)) != 0 && job.MissedTiles.Count > 0)
        {
            // The answer may be only for want of a tile the search wanted: the owner starts or polls its non-blocking read.
            switch (shared.ResolveSharedMisses(map.MapId, job.MissedTiles))
            {
                case SharedMissVerdict.Pending:
                    settled = JobAnswer.Waiting;
                    return null;
                case SharedMissVerdict.Changed:
                    state.Remove(entry);
                    settled = JobAnswer.Submitted;
                    return null;
            }
        }

        if (!entry.AppliedOnce)
        {
            _totals = _totals with { Applied = _totals.Applied + 1, DeadlineHits = _totals.DeadlineHits + (job.Search.DeadlineHits > 0 ? 1 : 0) };
            entry.AppliedOnce = true;
            LatencyRegistry.Shared.Record(LatencyNames.BotPathJobWait, Math.Max(0, (wallMs - entry.SubmittedWallMs) * 1000));
        }

        carry = default;

        // An answer cut short by the worker's time bound or a deadline (Incomplete) says how far the search got in that time, not what
        // the route is: it is used for this ask only and the next ask searches again, up to MaxCappedRetries in a row for the bot. A
        // query that is always cut keeps its capped answer after that (as before), so it escalates, reports its wait and does not feed
        // the pool forever. A missed tile that resolved to None (no data, out of bounds) explains the cut: that answer is kept too.
        // A full answer ends the run.
        if ((result.Type & PathType.Incomplete) != 0 && job.Search.DeadlineHits > 0 && job.MissedTiles.Count == 0)
        {
            if (state.CappedStreak < MaxCappedRetries)
            {
                state.CappedStreak++;
                state.Remove(entry);
            }
        }
        else
        {
            state.CappedStreak = 0;
        }

        return result with
        {
            // A route to the destination of a moment ago ends short of the one asked now, by design: Short keeps it from being read as a
            // route that fell short (nav/path-ends-short) and from being refused as a partial exact plan.
            Type = entry.To == to ? result.Type : result.Type | PathType.Short,
            Points = points,
        };
    }

    /// <summary>
    /// An answer found for a destination near the asked one stands only if its route ends within the tolerance of the asked one.
    /// </summary>
    private static bool ReachesAsked(Entry entry, PathResult result, Vector3 to, Vector3 from)
    {
        if (entry.To == to) return true;
        if (result.Points.Count == 0) return false;
        float tolerance = DestinationTolerance(from, to);
        return Vector3.DistanceSquared(result.Points[^1], to) <= tolerance * tolerance;
    }

    /// <summary>
    /// The points of <paramref name="result"/> as the route of a bot standing at <paramref name="from"/>. A query asked from there is its
    /// own; a bot that walked on since (a main query, <see cref="Entry.IsMain"/>) is spliced onto the first leg it stands within
    /// <see cref="RejoinYards"/> of, among the legs it can have walked (<see cref="MovedYards"/>): the route starts at the bot and goes on from that leg's end.
    /// False when it stands off the route.
    /// </summary>
    private bool JoinsFrom(Entry entry, PathResult result, Vector3 from, out List<Vector3> points)
    {
        IReadOnlyList<Vector3> source = result.Points;
        if (Vector3.DistanceSquared(entry.Start, from) <= 1f)
        {
            points = new List<Vector3>(source);
            return true;
        }

        points = [];
        float reach = MovedYards(_options);
        float walked = 0f;
        for (int i = 1; i < source.Count && walked <= reach; i++)
        {
            Vector3 a = source[i - 1], b = source[i];
            if (FlatDistanceToSegment(from, a, b) <= RejoinYards)
            {
                int next = Vector2.DistanceSquared(new Vector2(b.X, b.Y), new Vector2(from.X, from.Y)) <= 0.3f * 0.3f ? i + 1 : i;
                if (next >= source.Count) return false;
                points = new List<Vector3>(source.Count - next + 1) { from };
                for (int k = next; k < source.Count; k++) points.Add(source[k]);
                return true;
            }

            walked += Vector3.Distance(a, b);
        }

        return false;
    }

    private static float FlatDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var pp = new Vector2(p.X, p.Y);
        var aa = new Vector2(a.X, a.Y);
        Vector2 leg = new Vector2(b.X, b.Y) - aa;
        float lengthSq = leg.LengthSquared();
        float along = lengthSq <= 1e-6f ? 0f : Math.Clamp(Vector2.Dot(pp - aa, leg) / lengthSq, 0f, 1f);
        return Vector2.Distance(pp, aa + (leg * along));
    }

    private static bool StillValid(ISharedTiledPathSearch shared, Map map, Player player, Entry entry)
        => ReferenceEquals(player, entry.Player) && player.IsInWorld && ReferenceEquals(player.Map, entry.Map) && ReferenceEquals(map, entry.Map)
            && ReferenceEquals(shared, entry.Pathfinder) && Equals(DatasetOf(shared), entry.Dataset);

    private static object? DatasetOf(ISharedTiledPathSearch search) => (search as NavMeshPathfinder)?.Dataset;

    private PathResult Submit(ISharedTiledPathSearch shared, Map map, Player player, Guid botId, BotState state, Vector3 from, Vector3 to,
        PathOptions options, PathOptions key, Carry carry, uint nowMs, long wallMs, out JobAnswer how)
    {
        // Both endpoint tiles are the owner's to read (non-blocking): a tile still being read is an ordinary pending answer.
        if (!shared.PrepareShared(map.MapId, from, to))
        {
            how = JobAnswer.Waiting;
            t_pendingAnswers++;
            t_lastPendingWasJob = false;
            return new PathResult(PathType.TilesPending, [from]);
        }

        PruneActive();
        int mine = 0;
        foreach (Entry e in state.Entries)
            if (!e.Job.IsDone) mine++;

        if (mine >= _options.PathJobsMaxPerBot)
        {
            return Pending(state, wallMs, from, JobAnswer.Waiting, out how); // the bot's own job is still running: its next ask comes after it
        }

        if (!Admitted(botId, options.MaxSearchNodes))
        {
            JoinLine(botId);
            _totals = _totals with { BudgetRefused = _totals.BudgetRefused + 1 };
            return Pending(state, wallMs, from, JobAnswer.BudgetRefused, out how);
        }

        if (!TrySubmitCore(shared, map, player, state, from, to, options, key, carry, nowMs, wallMs))
        {
            JoinLine(botId);
            _totals = _totals with { BudgetRefused = _totals.BudgetRefused + 1 };
            return Pending(state, wallMs, from, JobAnswer.BudgetRefused, out how);
        }

        LeaveLine(botId);
        return Pending(state, wallMs, from, JobAnswer.Submitted, out how);
    }

    /// <summary>
    /// A job for the destination asked now, queued beside the answer just applied for the one asked a moment ago, so that the next answer
    /// tracks a destination that keeps moving. Silent in every way <see cref="Submit"/> is not: no pending answer, no place in line, no
    /// refusal counted; it does nothing when an endpoint tile is still being read, when the bot already has a job running or the pool's
    /// limits are reached.
    /// </summary>
    private void TryRefresh(ISharedTiledPathSearch shared, Map map, Player player, Guid botId, BotState state, Vector3 from, Vector3 to,
        PathOptions options, PathOptions key, uint nowMs, long wallMs)
    {
        if (!shared.PrepareShared(map.MapId, from, to)) return;
        PruneActive();
        int mine = 0;
        foreach (Entry e in state.Entries)
            if (!e.Job.IsDone) mine++;

        if (mine >= _options.PathJobsMaxPerBot || !Admitted(botId, options.MaxSearchNodes)) return;
        if (TrySubmitCore(shared, map, player, state, from, to, options, key, default, nowMs, wallMs)) LeaveLine(botId);
    }

    /// <summary>Queue the job and record its entry; false when the runner refuses it (nothing recorded).</summary>
    private bool TrySubmitCore(ISharedTiledPathSearch shared, Map map, Player player, BotState state, Vector3 from, Vector3 to,
        PathOptions options, PathOptions key, Carry failures, uint nowMs, long wallMs)
    {
        PathOptions jobOptions = options with
        {
            LoadTiles = false,
            NonBlockingTiles = false,
            MaxSearchMs = _options.PathJobWorkerMaxMs,
            CancellationToken = default,
        };
        var job = new NavJob(shared, map.MapId, from, to, jobOptions, urgent: false, trackMissedTiles: true) { SubmittedAtMs = nowMs };
        if (!_runner.TrySubmit(job)) return false;
        _active.Add(job);
        _activeNodes += options.MaxSearchNodes;
        _totals = _totals with { Submitted = _totals.Submitted + 1 };
        state.Add(new Entry(job, key, to, from, PlayerbotMotion.CurrentPosition(player), options.MaxSearchNodes, player, map, shared, DatasetOf(shared), wallMs)
        {
            Failures = failures.Failures,
            StaleFailures = failures.StaleFailures,
            CanceledFailures = failures.CanceledFailures,
            LastAskMs = nowMs,
        });
        TrimEntries(state);
        return true;
    }

    private void TrimEntries(BotState state)
    {
        // Finished answers only: an unfinished job is never dropped for room (the per-bot limit bounds those).
        while (state.Entries.Count > MaxEntriesPerBot || state.RetainedBytes > MaxRetainedBytes)
        {
            Entry? oldest = null;
            foreach (Entry e in state.Entries)
                if (e.Job.IsDone && (oldest is null || e.LastAskMs < oldest.LastAskMs)) oldest = e;
            if (oldest is null) return;
            state.Remove(oldest);
        }
    }

    // ---- admission ----------------------------------------------------------------------------------------------------------

    private void PruneActive()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!_active[i].IsDone) continue;
            _activeNodes -= _active[i].Options.MaxSearchNodes;
            _active.RemoveAt(i);
        }
    }

    /// <summary>
    /// Whether <paramref name="botId"/> may start a job of <paramref name="nodes"/> now: within the global job and node limits, and among the
    /// oldest waiters (a bot at line position p is served only when p is below the free slots), so a flood of new askers never overtakes
    /// the bots that were refused first. A single job over the node limit runs alone.
    /// </summary>
    private bool Admitted(Guid botId, int nodes)
    {
        int free = _options.PathJobsMaxInFlight - _active.Count;
        if (free <= 0) return false;
        if (_active.Count > 0 && _activeNodes + nodes > _options.PathJobsMaxInFlightNodes) return false;
        if (!_lineNodes.TryGetValue(botId, out LinkedListNode<Guid>? mine)) return _line.Count < free;

        // Among the first `free` waiters? The walk stops after `free` steps, so it is bounded by the free slots (at most
        // PathJobsMaxInFlight), never by the number of waiters.
        int position = 0;
        for (LinkedListNode<Guid>? n = _line.First; n is not null && position < free; n = n.Next, position++)
        {
            if (ReferenceEquals(n, mine)) return true;
        }

        return false;
    }

    private void JoinLine(Guid botId)
    {
        if (!_lineNodes.ContainsKey(botId)) _lineNodes[botId] = _line.AddLast(botId);
    }

    private void LeaveLine(Guid botId)
    {
        if (_lineNodes.Remove(botId, out LinkedListNode<Guid>? node)) _line.Remove(node);
    }

    /// <summary>Drops the waiters that left or stopped asking (<see cref="UnaskedAbandonMs"/>), keeping the order of the rest. Run by <see cref="Sweep"/>, not per refused submit.</summary>
    private void ExpireLine()
    {
        uint now = _clockMs();
        uint bound = UnaskedAbandonMs();
        for (LinkedListNode<Guid>? n = _line.First; n is not null;)
        {
            LinkedListNode<Guid>? next = n.Next;
            if (!_bots.TryGetValue(n.Value, out BotState? s) || unchecked(now - s.LastAskMs) > bound)
            {
                _lineNodes.Remove(n.Value);
                _line.Remove(n);
            }

            n = next;
        }
    }

    /// <summary>How long a bot may go without asking before its unfinished job is abandoned and its place in line is lost (2 s at least, a few thinks).</summary>
    private uint UnaskedAbandonMs() => (uint)Math.Clamp(4L * _options.ThinkIntervalMs, 2_000, UnclaimedMs);

    // ---- staleness, lifecycle ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Drops the entries asked while the bot stood farther from <paramref name="position"/> than it may have walked since: a yard for a
    /// fallback query, <see cref="MovedYards"/> for a main one (a walking bot still collects its answer; <see cref="JoinsFrom"/> refuses
    /// one it has walked off). Judged by where the bot was, not where the query started, so a fallback query that starts beside the bot or at
    /// a detour point leaves the others alone.
    /// </summary>
    private void DropStale(BotState state, Vector3 position)
    {
        for (int i = state.Entries.Count - 1; i >= 0; i--)
        {
            Entry e = state.Entries[i];
            float bound = e.IsMain ? MovedYards(_options) : 1f;
            if (Vector3.DistanceSquared(e.BotPosition, position) <= bound * bound) continue;
            if (!e.Job.IsDone) e.Job.Abandon();
            _totals = _totals with { Stale = _totals.Stale + 1, StaleWalked = _totals.StaleWalked + 1 };
            state.Entries.RemoveAt(i);
        }
    }

    /// <summary>The bot left (logout, removal, detach): abandon its jobs and drop its answers. Any thread.</summary>
    internal void Forget(Guid botId)
    {
        lock (_gate)
        {
            if (_bots.Remove(botId, out BotState? state))
            {
                foreach (Entry e in state.Entries)
                    if (!e.Job.IsDone) e.Job.Abandon();
            }

            LeaveLine(botId);
            _deadlineReported.Remove(botId);
        }
    }

    /// <summary>Forget every bot (the feature stops or is detached).</summary>
    internal void ForgetAll()
    {
        lock (_gate)
        {
            foreach (BotState state in _bots.Values)
                foreach (Entry e in state.Entries)
                    if (!e.Job.IsDone) e.Job.Abandon();
            _bots.Clear();
            _line.Clear();
            _lineNodes.Clear();
            _deadlineReported.Clear();
        }
    }

    /// <summary>
    /// Once per feature update: drop what nobody will ask for again. A finished answer unasked for <see cref="UnclaimedMs"/>, an
    /// unfinished job unasked for a few thinks (abandoned: a worker that has not started it skips it), an answer of a bot that left its
    /// map or the world, and the bots with nothing left. Also the once-a-minute summary line.
    /// </summary>
    internal void Sweep()
    {
        uint now = _clockMs();
        uint unasked = UnaskedAbandonMs();
        lock (_gate)
        {
            PruneActive();
            List<Guid>? gone = null;
            foreach ((Guid id, BotState state) in _bots)
            {
                foreach (Entry e in state.Entries.ToArray())
                {
                    uint idle = unchecked(now - e.LastAskMs);
                    bool away = !e.Player.IsInWorld || !ReferenceEquals(e.Player.Map, e.Map);
                    if (away || (e.Job.IsDone ? idle > UnclaimedMs : idle > unasked))
                    {
                        if (!e.Job.IsDone) e.Job.Abandon();
                        if (away) _totals = _totals with { Stale = _totals.Stale + 1, StaleAway = _totals.StaleAway + 1 };
                        state.Remove(e);
                    }
                }

                if (state.Entries.Count == 0 && unchecked(now - state.LastAskMs) > UnclaimedMs) (gone ??= []).Add(id);
            }

            if (gone is not null)
                foreach (Guid id in gone)
                {
                    _bots.Remove(id);
                    LeaveLine(id);
                }

            ExpireLine();
            if (unchecked(now - _lastSummaryMs) >= 60_000)
            {
                _lastSummaryMs = now;
                LogSummary();
            }
        }
    }

    private void LogSummary()
    {
        PlayerbotPathJobTotals d = _totals;
        PlayerbotPathJobTotals b = _logged;
        _logged = d;
        if (d == b) return;
        _logger.LogInformation(
            "Playerbot path jobs: {Submitted} submitted, {Applied} applied, {Waits} waits, {Stale} stale ({StaleInvalid} invalid, {StaleReaches} ends short of the goal, {StaleJoins} off the route, {StaleWalked} walked, {StaleAway} away), {Canceled} canceled, {Faulted} faulted, {Refused} held by the budget, {Escalations} escalated, {Deadline} at the deadline (last minute); {InFlight} in flight, {Line} in line",
            d.Submitted - b.Submitted, d.Applied - b.Applied, d.Waits - b.Waits, d.Stale - b.Stale,
            d.StaleInvalid - b.StaleInvalid, d.StaleReachesAsked - b.StaleReachesAsked, d.StaleJoinsFrom - b.StaleJoinsFrom,
            d.StaleWalked - b.StaleWalked, d.StaleAway - b.StaleAway, d.Canceled - b.Canceled, d.Faulted - b.Faulted,
            d.BudgetRefused - b.BudgetRefused, d.Escalations - b.Escalations, d.DeadlineHits - b.DeadlineHits, _active.Count, _line.Count);
    }

    // ---- sync deadline anomaly -------------------------------------------------------------------------------------------

    private readonly Dictionary<Guid, uint> _deadlineReported = [];

    /// <summary>A world-thread search ended at the deadline: its capped answer stands and it is reported (once a minute per bot).</summary>
    internal void ReportSyncDeadline(string phase, Vector3 from, Vector3 to, long nodes)
    {
        if (t_player is not { IsInWorld: true } player) return;
        uint now = _clockMs();
        lock (_gate)
        {
            if (_deadlineReported.TryGetValue(t_botId, out uint last) && unchecked(now - last) < 60_000) return;
            _deadlineReported[t_botId] = now;
        }

        PlayerbotAnomalies.Report(player, "nav", "sync-deadline",
            string.Create(CultureInfo.InvariantCulture,
                $"a world-thread path search hit the {_options.SyncPathQueryMaxMs} ms deadline in phase {phase} after {nodes} nodes, from ({from.X:F1}, {from.Y:F1}, {from.Z:F1}) to ({to.X:F1}, {to.Y:F1}, {to.Z:F1})"),
            AnomalySeverity.Info);
    }

    // ---- state ---------------------------------------------------------------------------------------------------------------

    private sealed class BotState
    {
        internal readonly List<Entry> Entries = [];
        internal uint LastAskMs;

        /// <summary>Consecutive deadline-capped answers dropped for a retry; reset by a full answer.</summary>
        internal int CappedStreak;

        /// <summary>Wall ms when the bot's current run of pending answers began (0 = not waiting); ended by an applied answer or an escalation.</summary>
        internal long PendingSinceWallMs;

        internal int RetainedBytes
        {
            get
            {
                int bytes = 0;
                foreach (Entry e in Entries)
                    if (e.Job.IsDone && e.Job.Result is { } r) bytes += (r.Points.Count * 12) + 96;
                return bytes;
            }
        }

        internal Entry? Find(PathOptions key, Vector3 to, Vector3 from, Vector3 botPosition, int nodes, PlayerbotOptions options)
        {
            // An exact destination wins; otherwise the nearest finished answer within the tolerance, else the nearest running job within it
            // (a destination that keeps moving, such as a leader, still finds the job asked a few thinks ago; a finished answer near the
            // goal beats waiting for a fresher one, the follower asks again next think anyway). The query is the entry's own when it starts
            // where the entry's did; a main query of a bot that walked on since (from its own spot) also finds the entries asked from where
            // the bot stood. TryApply re-validates a near match against the destination and the position asked now.
            float tolerance = DestinationTolerance(from, to);
            float movedYards = MovedYards(options);
            bool fromBot = Vector3.DistanceSquared(from, botPosition) <= 1f;
            Entry? done = null, running = null;
            float doneSq = tolerance * tolerance, runningSq = doneSq;
            foreach (Entry e in Entries)
            {
                if (e.MaxNodes < nodes - NodeTolerance || !e.Key.Equals(key)) continue;
                bool sameStart = Vector3.DistanceSquared(e.Start, from) <= 1f;
                if (!sameStart && !(fromBot && e.IsMain && Vector3.DistanceSquared(e.BotPosition, botPosition) <= movedYards * movedYards)) continue;
                if (e.To == to) return e;
                float d = Vector3.DistanceSquared(e.To, to);
                if (e.Job.IsDone)
                {
                    if (d <= doneSq)
                    {
                        done = e;
                        doneSq = d;
                    }
                }
                else if (d <= runningSq)
                {
                    running = e;
                    runningSq = d;
                }
            }

            return done ?? running;
        }

        internal void Add(Entry entry) => Entries.Add(entry);

        internal void Remove(Entry entry) => Entries.Remove(entry);
    }

    private sealed class Entry(NavJob job, PathOptions key, Vector3 to, Vector3 start, Vector3 botPosition, int maxNodes, Player player, Map map,
        ISharedTiledPathSearch pathfinder, object? dataset, long submittedWallMs)
    {
        internal readonly NavJob Job = job;
        internal readonly PathOptions Key = key;
        internal readonly Vector3 To = to;
        internal readonly Vector3 Start = start;
        internal readonly int MaxNodes = maxNodes;
        internal readonly Player Player = player;
        internal readonly Map Map = map;
        internal readonly ISharedTiledPathSearch Pathfinder = pathfinder;
        internal readonly object? Dataset = dataset;
        internal readonly long SubmittedWallMs = submittedWallMs;
        internal readonly Vector3 BotPosition = botPosition;

        /// <summary>Asked from where the bot stood (a fallback query starts beside it or at a detour point and is not main).</summary>
        internal bool IsMain => Vector3.DistanceSquared(Start, BotPosition) <= 1f;
        internal int Failures;

        /// <summary>How many of <see cref="Failures"/> were stale answers.</summary>
        internal int StaleFailures;

        /// <summary>How many of <see cref="Failures"/> were cancellations.</summary>
        internal int CanceledFailures;
        internal uint LastAskMs;
        internal bool LongWaitReported;
        internal bool AppliedOnce;
    }
}
