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
    long BudgetRefused, long Escalations, long DeadlineHits);

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
    private readonly List<Guid> _line = [];
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

    /// <summary>Path-job answers of "pending" given on this thread so far (a query waiting for its job, or cut at the search deadline inside a <see cref="Deferrable"/> scope).</summary>
    internal static long PendingAnswers => t_pendingAnswers;

    /// <summary>Whether the latest pending answer on this thread was for a path job, not a navigation tile read (<see cref="TileWait.Hold"/>).</summary>
    internal static bool LastPendingWasJob => t_lastPendingWasJob;

    internal static void NoteTilePending() => t_lastPendingWasJob = false;

    /// <summary>A search past its deadline inside a <see cref="Deferrable"/> scope: the plan answers pending (counted like a job wait, but a retry that <see cref="TileWait"/> bounds).</summary>
    internal static void NoteDeadlinePending()
    {
        t_pendingAnswers++;
        t_lastPendingWasJob = false;
    }

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
        return default;
    }

    /// <summary>The scope of <see cref="NoDefer"/>.</summary>
    internal readonly struct NoDeferScope : IDisposable
    {
        public void Dispose()
        {
            if (t_noDeferDepth > 0) t_noDeferDepth--;
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

            state.LastAskMs = nowMs;
            DropMovedAway(state, from);
            Entry? entry = state.Find(key, to, options.MaxSearchNodes);
            Carry carry = default;
            if (entry is not null)
            {
                entry.LastAskMs = nowMs;
                carry = new Carry(entry.Failures, entry.WaitStartWallMs);
                if (!entry.Job.IsDone)
                {
                    if (wallMs - entry.WaitStartWallMs > _options.PathJobWaitMaxMs)
                    {
                        return Escalate(state, entry, "waited too long", player, from, to, out how);
                    }

                    ReportLongWait(entry, wallMs, player);
                    return Pending(from, JobAnswer.Waiting, out how);
                }

                PathResult? applied = TryApply(shared, map, player, entry, from, to, wallMs, state, ref carry, out how, out JobAnswer settled);
                if (applied is not null)
                {
                    job = entry.Job;
                    return applied;
                }

                if (settled == JobAnswer.Escalated)
                {
                    return Escalate(state, null, carry.Failures >= EscalateAfterFailures ? "repeatedly canceled" : "faulted", player, from, to, out how);
                }

                if (settled == JobAnswer.Waiting)
                {
                    return Pending(from, JobAnswer.Waiting, out how); // a missed tile is still being read
                }

                // settled == Submitted: the old answer is dropped; a fresh job replaces it below, carrying the failure count.
            }

            if (carry.WaitStartWallMs == 0) carry = carry with { WaitStartWallMs = wallMs };
            return Submit(shared, map, player, botId, state, from, to, options, key, carry, nowMs, wallMs, out how);
        }
    }

    private readonly record struct Carry(int Failures, long WaitStartWallMs);

    private PathResult Pending(Vector3 from, JobAnswer how, out JobAnswer answer)
    {
        answer = how;
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

    private void ReportLongWait(Entry entry, long wallMs, Player player)
    {
        if (entry.LongWaitReported || wallMs - entry.WaitStartWallMs <= WaitLongMs || !player.IsInWorld) return;
        entry.LongWaitReported = true;
        PlayerbotAnomalies.Report(player, "nav", "job-wait-long",
            string.Create(CultureInfo.InvariantCulture, $"a path job has been pending for {wallMs - entry.WaitStartWallMs} ms ({entry.Job.Status}, {entry.MaxNodes} nodes)"),
            AnomalySeverity.Info);
    }

    /// <summary>The applied answer of a finished job; null when it was dropped (<paramref name="settled"/> says what the caller does next).</summary>
    private PathResult? TryApply(ISharedTiledPathSearch shared, Map map, Player player, Entry entry, Vector3 from, Vector3 to, long wallMs,
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
                carry = carry with { Failures = carry.Failures + 1 };
                if (carry.Failures >= EscalateAfterFailures) settled = JobAnswer.Escalated;
                return null;
        }

        if (job.Result is not { } result || !StillValid(shared, map, player, entry))
        {
            _totals = _totals with { Stale = _totals.Stale + 1 };
            state.Remove(entry);
            carry = carry with { Failures = carry.Failures + 1 };
            if (carry.Failures >= EscalateAfterFailures) settled = JobAnswer.Escalated;
            return null;
        }

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
        return result with { Points = new List<Vector3>(result.Points) };
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
            return Pending(from, JobAnswer.Waiting, out how); // the bot's own job is still running: its next ask comes after it
        }

        if (!Admitted(botId, options.MaxSearchNodes))
        {
            JoinLine(botId);
            _totals = _totals with { BudgetRefused = _totals.BudgetRefused + 1 };
            return Pending(from, JobAnswer.BudgetRefused, out how);
        }

        PathOptions jobOptions = options with
        {
            LoadTiles = false,
            NonBlockingTiles = false,
            MaxSearchMs = _options.PathJobWorkerMaxMs,
            CancellationToken = default,
        };
        var job = new NavJob(shared, map.MapId, from, to, jobOptions, urgent: false, trackMissedTiles: true) { SubmittedAtMs = nowMs };
        if (!_runner.TrySubmit(job))
        {
            JoinLine(botId);
            _totals = _totals with { BudgetRefused = _totals.BudgetRefused + 1 };
            return Pending(from, JobAnswer.BudgetRefused, out how);
        }

        LeaveLine(botId);
        _active.Add(job);
        _activeNodes += options.MaxSearchNodes;
        _totals = _totals with { Submitted = _totals.Submitted + 1 };
        state.Add(new Entry(job, key, to, from, options.MaxSearchNodes, player, map, shared, DatasetOf(shared), wallMs, carry.WaitStartWallMs)
        {
            Failures = carry.Failures,
            LastAskMs = nowMs,
        });
        TrimEntries(state);
        return Pending(from, JobAnswer.Submitted, out how);
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
        ExpireLine();
        int free = _options.PathJobsMaxInFlight - _active.Count;
        if (free <= 0) return false;
        if (_active.Count > 0 && _activeNodes + nodes > _options.PathJobsMaxInFlightNodes) return false;
        int position = _line.IndexOf(botId);
        return position < 0 ? _line.Count < free : position < free;
    }

    private void JoinLine(Guid botId)
    {
        if (!_line.Contains(botId)) _line.Add(botId);
    }

    private void LeaveLine(Guid botId) => _line.Remove(botId);

    private void ExpireLine()
    {
        uint now = _clockMs();
        uint bound = UnaskedAbandonMs();
        for (int i = _line.Count - 1; i >= 0; i--)
        {
            if (!_bots.TryGetValue(_line[i], out BotState? s) || unchecked(now - s.LastAskMs) > bound)
            {
                _line.RemoveAt(i);
            }
        }
    }

    /// <summary>How long a bot may go without asking before its unfinished job is abandoned and its place in line is lost (2 s at least, a few thinks).</summary>
    private uint UnaskedAbandonMs() => (uint)Math.Clamp(4L * _options.ThinkIntervalMs, 2_000, UnclaimedMs);

    // ---- staleness, lifecycle ----------------------------------------------------------------------------------------------

    /// <summary>The bot moved more than a yard from where its answers were asked: every one of them is out of date at once.</summary>
    private void DropMovedAway(BotState state, Vector3 from)
    {
        bool moved = false;
        foreach (Entry e in state.Entries)
        {
            if (Vector3.DistanceSquared(e.Start, from) > 1f)
            {
                moved = true;
                break;
            }
        }

        if (!moved) return;
        foreach (Entry e in state.Entries.ToArray())
        {
            if (!e.Job.IsDone) e.Job.Abandon();
            _totals = _totals with { Stale = _totals.Stale + 1 };
            state.Remove(e);
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

            _line.Remove(botId);
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
                        if (away) _totals = _totals with { Stale = _totals.Stale + 1 };
                        state.Remove(e);
                    }
                }

                if (state.Entries.Count == 0 && unchecked(now - state.LastAskMs) > UnclaimedMs) (gone ??= []).Add(id);
            }

            if (gone is not null)
                foreach (Guid id in gone)
                {
                    _bots.Remove(id);
                    _line.Remove(id);
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
            "Playerbot path jobs: {Submitted} submitted, {Applied} applied, {Waits} waits, {Stale} stale, {Canceled} canceled, {Faulted} faulted, {Refused} held by the budget, {Escalations} escalated, {Deadline} at the deadline (last minute); {InFlight} in flight, {Line} in line",
            d.Submitted - b.Submitted, d.Applied - b.Applied, d.Waits - b.Waits, d.Stale - b.Stale, d.Canceled - b.Canceled, d.Faulted - b.Faulted,
            d.BudgetRefused - b.BudgetRefused, d.Escalations - b.Escalations, d.DeadlineHits - b.DeadlineHits, _active.Count, _line.Count);
    }

    // ---- sync deadline anomaly -------------------------------------------------------------------------------------------

    private readonly Dictionary<Guid, uint> _deadlineReported = [];

    /// <summary>A world-thread search outside any deferrable plan ended at the deadline: reported (once a minute per bot) so the callers still to migrate are listed.</summary>
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

        internal Entry? Find(PathOptions key, Vector3 to, int nodes)
        {
            foreach (Entry e in Entries)
            {
                if (e.To == to && e.MaxNodes >= nodes - NodeTolerance && e.Key.Equals(key)) return e;
            }

            return null;
        }

        internal void Add(Entry entry) => Entries.Add(entry);

        internal void Remove(Entry entry) => Entries.Remove(entry);
    }

    private sealed class Entry(NavJob job, PathOptions key, Vector3 to, Vector3 start, int maxNodes, Player player, Map map,
        ISharedTiledPathSearch pathfinder, object? dataset, long submittedWallMs, long waitStartWallMs)
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
        internal readonly long WaitStartWallMs = waitStartWallMs;
        internal int Failures;
        internal uint LastAskMs;
        internal bool LongWaitReported;
        internal bool AppliedOnce;
    }
}
