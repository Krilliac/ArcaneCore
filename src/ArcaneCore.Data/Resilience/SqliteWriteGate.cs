using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// The in-process single-writer gate of one SQLite database file (<c>Database:Sqlite:SingleWriter</c>). SQLite admits one writer at a time even in WAL mode, and
/// a writer that finds the lock taken polls it from SQLite's busy handler (sleeps of 1-100 ms, no queue), so with a dozen background write queues on one
/// characters database the writers starve each other and some fail with "database is locked" although the total work fits easily
/// (live 2026-10-17: 959 locked lines in 24 h, a 1000-character save backlog). Every write transaction and every autocommit write statement that EF runs against the file
/// first takes this gate, so writers inside the process, blocking and async alike, queue in one arrival-order queue and are handed the lock the moment the previous one ends,
/// instead of racing for it (a waiter keeps its place across its periodic wake-ups). Readers never take it (WAL readers do not block the writer).
/// <para>
/// Bounded by progress, not by total elapsed time: an async waiter gives up when the gate has handed off to no one for its connection's write bound (every hand-off, to any
/// waiter, restarts that countdown) or, as a hard cap, after <see cref="HardCapMs"/> (<see cref="HardCapFactor"/> times the bound, never beyond <see cref="HardCapCeilingMs"/>
/// unless the bound itself is larger) of active waiting. A blocking waiter (<see cref="Enter"/>) keeps the single bound as its total. Time the whole process was suspended
/// (GC pauses only, <see cref="IWriteGateClock.PausedMs"/>) counts toward neither. A caller's own cancellation token or budget ends the wait earlier and always supersedes these caps.
/// It then fails with SQLite's own busy error (code 5), so callers that retain
/// and retry a locked write behave exactly as before, but the message names the holder and the real wait. A hold that outlives <see cref="SqliteSingleWriterOptions.MaxHoldMs"/> (a leaked transaction) is
/// released by the next waiter that finds it and logged; that only lets the next writer try SQLite itself, as it would without the gate.
/// </para>
/// One instance per normalized file path, shared process-wide (<see cref="For"/>). Thread-safe.
/// </summary>
public sealed class SqliteWriteGate
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SqliteWriteGate> Gates = new(PathComparer);
    private static readonly ConcurrentDictionary<string, string?> Keys = new(StringComparer.Ordinal);

    private readonly Lock _queueLock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private bool _held; // guarded by _queueLock
    private readonly Lock _stats = new();
    private Hold? _current;
    private int _waiting;

    // Interval statistics (reset by TakeStats).
    private long _acquired;
    private long _contended;
    private double _waitMsTotal;
    private double _waitMsMax;
    private double _holdMsMax;
    private string? _holdMaxOwner;
    private long _timeouts;
    private long _forced;

    // Lifetime totals.
    private long _acquiredTotal;
    private long _timeoutsTotal;
    private long _forcedTotal;

    /// <summary>An async waiter's hard cap is this multiple of its bound of active (non-suspended) time, however many hand-offs it watches go by (see <see cref="HardCapMs"/>). The cap is checked when the waiter looks (at least every poll); a grant already in hand at that look wins over the cap.</summary>
    internal const int HardCapFactor = 4;

    /// <summary>The hard cap never exceeds this many milliseconds, except that it is never below the bound itself (so a large fallback bound such as a 30 s Default Timeout is not multiplied).</summary>
    internal const int HardCapCeilingMs = 30_000;

    /// <summary>The total active milliseconds an async waiter with bound <paramref name="timeoutMs"/> may wait (the bound itself at least).</summary>
    internal static double HardCapMs(int timeoutMs) => Math.Max(timeoutMs, Math.Min((double)timeoutMs * HardCapFactor, HardCapCeilingMs));

    private readonly IWriteGateClock _clock;
    private long _handoffs; // written under _queueLock; counts every hand-off, so a waiter can tell that the queue moved
    private double _lastHandoffMs; // clock time of the last hand-off (under _queueLock), so idle time is measured from the hand-off, not from when a waiter noticed it
    private double _lastHandoffPausedMs; // PausedMs at that hand-off (under _queueLock)
    private double _pendingGrantSinceMs = double.NaN; // clock time of the last hand-off to a waiter that has not resumed yet; NaN: none

    private SqliteWriteGate(string path, IWriteGateClock clock)
    {
        Path = path;
        _clock = clock;
    }

    /// <summary>A gate that is not shared through <see cref="For"/> and runs on <paramref name="clock"/> (tests).</summary>
    internal static SqliteWriteGate CreateIsolated(string path, IWriteGateClock clock) => new(path, clock);

    /// <summary>The database file this gate serializes.</summary>
    public string Path { get; }

    /// <summary>Writers waiting for the gate right now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Lifetime number of holds granted.</summary>
    public long AcquiredTotal => Interlocked.Read(ref _acquiredTotal);

    /// <summary>Lifetime number of waits that gave up (reported to the caller as SQLite busy).</summary>
    public long TimeoutsTotal => Interlocked.Read(ref _timeoutsTotal);

    /// <summary>Lifetime number of holds released by a waiter because they outlived the max hold.</summary>
    public long ForcedTotal => Interlocked.Read(ref _forcedTotal);

    /// <summary>The current holder, if any.</summary>
    public Hold? Current => Volatile.Read(ref _current);

    /// <summary>
    /// The gate of the database behind <paramref name="connectionString"/>, or null when it is not a plain file-backed read-write SQLite database
    /// (memory, read-only, <c>file:</c> URI and empty data sources get no gate: their aliasing cannot be proven). Cached per connection string.
    /// </summary>
    public static SqliteWriteGate? For(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return null;
        string? key = Keys.GetOrAdd(connectionString, static value =>
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder(value);
                string source = builder.DataSource;
                if (string.IsNullOrWhiteSpace(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || builder.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly)
                    return null;
                return System.IO.Path.GetFullPath(source);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
        return key is null ? null : Gates.GetOrAdd(key, static path => new SqliteWriteGate(path, SystemWriteGateClock.Instance));
    }

    /// <summary>Every gate created in this process (diagnostics).</summary>
    public static IReadOnlyCollection<SqliteWriteGate> All => Gates.Values.ToArray();

    /// <summary>Takes the gate, waiting at most <paramref name="timeoutMs"/> of active time in all (the single bound, as before the progress rule); null on timeout. Blocks the calling thread.</summary>
    public Hold? Enter(int timeoutMs, string owner, int maxHoldMs) => TryEnter(timeoutMs, owner, maxHoldMs).Hold;

    /// <summary>Takes the gate, waiting until it has made no progress for <paramref name="timeoutMs"/>; null on timeout.</summary>
    public async ValueTask<Hold?> EnterAsync(int timeoutMs, string owner, int maxHoldMs, CancellationToken cancellationToken)
        => (await TryEnterAsync(timeoutMs, owner, maxHoldMs, cancellationToken).ConfigureAwait(false)).Hold;

    /// <summary>
    /// Like <see cref="Enter"/>, but also reports the wall time the wait really took (for the busy message). Blocks the calling thread, so its total wait stays the single
    /// bound: progress does not extend it (a blocked thread must not stay blocked four times longer than it used to).
    /// </summary>
    internal GateEntry TryEnter(int timeoutMs, string owner, int maxHoldMs)
    {
        double start = _clock.NowMs;
        if (TryTakeOrQueue(runContinuationsAsynchronously: false, out Waiter? waiter)) return Entered(Granted(start, owner, contended: false));
        try
        {
            ReleaseIfStuck(maxHoldMs);
            WaitProgress progress = NewProgress();
            double left = Advance(ref progress, timeoutMs, timeoutMs);
            while (left > 0)
            {
                // Wake at least every poll (1 s) to release a stuck hold, so a leaked transaction cannot block for longer than max hold plus a poll.
                // The waiter keeps its place in the queue across wake-ups.
                if (waiter!.Task.Wait(SliceMs(left))) return Entered(Granted(start, owner, contended: true));
                ReleaseIfStuck(maxHoldMs);
                left = Advance(ref progress, timeoutMs, timeoutMs);
            }

            // Out of patience: leave the queue, unless the hand-off got there first.
            return Withdraw(waiter!) == WaiterState.Granted ? Entered(Granted(start, owner, contended: true)) : TimedOut(start);
        }
        catch
        {
            if (Withdraw(waiter!) == WaiterState.Granted) HandOff();
            throw;
        }
    }

    /// <summary>Like <see cref="EnterAsync"/>, but also reports the wall time the wait really took (for the busy message).</summary>
    internal async ValueTask<GateEntry> TryEnterAsync(int timeoutMs, string owner, int maxHoldMs, CancellationToken cancellationToken)
    {
        double start = _clock.NowMs;
        if (TryTakeOrQueue(runContinuationsAsynchronously: true, out Waiter? waiter)) return Entered(Granted(start, owner, contended: false));
        try
        {
            ReleaseIfStuck(maxHoldMs);
            WaitProgress progress = NewProgress();
            double capMs = HardCapMs(timeoutMs);
            double left = Advance(ref progress, timeoutMs, capMs);
            while (left > 0)
            {
                // The slice ends without an exception: SuppressThrowing swallows the TimeoutException the bounded wait completes with (and a cancellation, which is
                // rethrown below), so an expected wake-up is not a first-chance exception per waiter per second. SuppressThrowing is rejected on Task<T>
                // (ArgumentOutOfRangeException), so the slice is held as the non-generic Task on purpose; the result is read from waiter.Task below.
                Task slice = waiter!.Task.WaitAsync(TimeSpan.FromMilliseconds(SliceMs(left)), cancellationToken);
                await slice.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (waiter.Task.IsCompleted) return Entered(Granted(start, owner, contended: true)); // only ever completed by a hand-off
                cancellationToken.ThrowIfCancellationRequested();

                ReleaseIfStuck(maxHoldMs);
                left = Advance(ref progress, timeoutMs, capMs);
            }

            return Withdraw(waiter!) == WaiterState.Granted ? Entered(Granted(start, owner, contended: true)) : TimedOut(start);
        }
        catch
        {
            // Canceled or faulted: leave the queue, and if the grant raced us, pass it on instead of orphaning it.
            if (Withdraw(waiter!) == WaiterState.Granted) HandOff();
            throw;
        }
    }

    /// <summary>The result of an entry attempt: the hold (null when the wait gave up) and the wall milliseconds it took, suspensions included.</summary>
    internal readonly record struct GateEntry(Hold? Hold, double WaitedMs);

    private static GateEntry Entered(Hold hold) => new(hold, hold.WaitedMs);

    private int SliceMs(double left) => (int)Math.Max(1, Math.Ceiling(Math.Min(left, _clock.PollMs)));

    /// <summary>
    /// One waiter's patience accounting (see <see cref="Advance"/>): the clock and suspension totals at the start of the wait and at the last hand-off it saw, so active time
    /// is always a difference of two cumulative readings (nothing is accumulated interval by interval, so a pause landing between two reads is never lost).
    /// Local to the waiting call; never shared.
    /// </summary>
    private struct WaitProgress
    {
        public double StartNow;
        public double StartPaused;
        public double ResetNow; // clock time of the last hand-off seen (the start of the wait until one is)
        public double ResetPaused;
        public long SeenHandoffs;
    }

    /// <summary>
    /// The baseline of a wait. The suspension total is read first and the clock after it: a pause that lands between the two reads is then in the baseline clock but not in the
    /// baseline total, which can only make the waiter patient for a moment longer. The other order would put the pause in the total only and charge it to the waiter for good.
    /// (The wait's reported start, for WaitedMs, is the separate reading taken before queueing.)
    /// </summary>
    private WaitProgress NewProgress()
    {
        double paused = _clock.PausedMs;
        double now = _clock.NowMs;
        long handoffs;
        lock (_queueLock) handoffs = _handoffs;
        return new WaitProgress { StartNow = now, StartPaused = paused, ResetNow = now, ResetPaused = paused, SeenHandoffs = handoffs };
    }

    /// <summary>
    /// Returns how many more active milliseconds the waiter may wait (0 or less: give up). Active time is elapsed wall time minus the process-wide suspension (GC pause) over the same
    /// span, both measured cumulatively. Idle time runs from the last hand-off (the moment it happened, not the moment this waiter noticed it); the active total since the start of the
    /// wait never restarts and is limited by <paramref name="capMs"/>, the hard cap.
    /// </summary>
    private double Advance(ref WaitProgress progress, int timeoutMs, double capMs)
    {
        // Clock first, then the suspension total: a pause that lands between the two reads is in the total but not yet in the clock, which makes active time look
        // briefly shorter and is made good by the next look (the cumulative difference then includes it in both), never lost.
        double now = _clock.NowMs;
        double paused = _clock.PausedMs;

        // The hand-off snapshot is taken after the clock, so a hand-off the waiter has been stalled behind is never missed by this look (one that happens after the clock
        // read has a later time than 'now'; its idle time is clamped to zero).
        long handoffs;
        double handoffNow;
        double handoffPaused;
        lock (_queueLock)
        {
            handoffs = _handoffs;
            handoffNow = _lastHandoffMs;
            handoffPaused = _lastHandoffPausedMs;
        }

        if (handoffs != progress.SeenHandoffs)
        {
            progress.SeenHandoffs = handoffs;
            progress.ResetNow = handoffNow;
            progress.ResetPaused = handoffPaused;
        }

        double active = Math.Max(0, now - progress.StartNow - (paused - progress.StartPaused));
        double idle = Math.Max(0, now - progress.ResetNow - (paused - progress.ResetPaused));
        return Math.Min(timeoutMs - idle, capMs - active);
    }

    /// <summary>
    /// Takes the gate at once when it is free and nobody is queued (true); otherwise appends a waiter at the tail of the arrival-order queue (false).
    /// </summary>
    private bool TryTakeOrQueue(bool runContinuationsAsynchronously, out Waiter? waiter)
    {
        lock (_queueLock)
        {
            if (!_held && _queue.Count == 0)
            {
                _held = true;
                waiter = null;
                return true;
            }

            waiter = new Waiter(runContinuationsAsynchronously);
            waiter.Node = _queue.AddLast(waiter); // State starts as Queued
            Interlocked.Increment(ref _waiting);
            return false;
        }
    }

    /// <summary>Gives the gate to the oldest queued waiter (it stays held), or marks it free when nobody waits.</summary>
    private void HandOff()
    {
        lock (_queueLock)
        {
            _handoffs++;
            _lastHandoffPausedMs = _clock.PausedMs; // suspension total first, then the clock (see NewProgress)
            _lastHandoffMs = _clock.NowMs;
            LinkedListNode<Waiter>? head = _queue.First;
            if (head is null)
            {
                _held = false;
                Volatile.Write(ref _pendingGrantSinceMs, double.NaN);
                return;
            }

            _queue.RemoveFirst();
            Waiter next = head.Value;
            next.Node = null;
            next.State = WaiterState.Granted;
            Interlocked.Decrement(ref _waiting);
            Volatile.Write(ref _pendingGrantSinceMs, _lastHandoffMs); // the gate is held by a waiter that has not resumed yet: Current stays null until it does
            next.TrySetResult(true);
        }
    }

    /// <summary>
    /// Takes <paramref name="waiter"/> out of the queue and returns its final state. <see cref="WaiterState.Granted"/>: the gate had already been handed to it (the caller
    /// owns it now and must use or pass it on). <see cref="WaiterState.Withdrawn"/>: it left the queue without ever being granted (also when it had already withdrawn, so a
    /// repeated call can never be mistaken for a grant).
    /// </summary>
    private WaiterState Withdraw(Waiter waiter)
    {
        lock (_queueLock)
        {
            if (waiter.State == WaiterState.Queued)
            {
                _queue.Remove(waiter.Node!);
                waiter.Node = null;
                waiter.State = WaiterState.Withdrawn;
                Interlocked.Decrement(ref _waiting);
            }

            return waiter.State;
        }
    }

    private enum WaiterState
    {
        Queued,
        Granted,
        Withdrawn,
    }

    private sealed class Waiter : TaskCompletionSource<bool>
    {
        // Async waiters resume on the pool, so the releaser (which holds the queue lock while it completes the task) never runs their continuation inline.
        // A blocking waiter has no continuation: it waits on the task itself and is released by the completion, but it still needs its own thread, which under
        // thread-pool starvation may not exist; the queue does not depend on it (the waiter's own timeout still applies).
        public Waiter(bool runContinuationsAsynchronously)
            : base(runContinuationsAsynchronously ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None)
        {
        }

        public LinkedListNode<Waiter>? Node;

        public WaiterState State; // guarded by _queueLock
    }

    /// <summary>
    /// The busy error a timed-out waiter reports: SQLITE_BUSY (5), so every existing "locked: retain and retry" path treats it as before.
    /// <paramref name="waitedMs"/> is the wall time the wait really took.
    /// </summary>
    public SqliteException Busy(double waitedMs)
    {
        Hold? holder = Current;
        string held;
        if (holder is not null)
        {
            held = $"held {holder.HeldMs.ToString("0", CultureInfo.InvariantCulture)} ms by {holder.Owner}";
        }
        else
        {
            // The gate was handed to a waiter whose continuation has not run yet (a stalled process or a starved pool): held, though nobody is published as the holder.
            double since = Volatile.Read(ref _pendingGrantSinceMs);
            held = double.IsNaN(since)
                ? "no current holder"
                : $"handed to a waiter {Math.Max(0, _clock.NowMs - since).ToString("0", CultureInfo.InvariantCulture)} ms ago, not yet resumed";
        }

        return new SqliteException($"SQLite Error 5: 'database is locked' (in-process write gate: waited {waitedMs.ToString("0", CultureInfo.InvariantCulture)} ms for {System.IO.Path.GetFileName(Path)}; {held}; {Waiting} waiting)", 5);
    }

    /// <summary>Returns and resets the interval statistics.</summary>
    public GateStats TakeStats()
    {
        lock (_stats)
        {
            var stats = new GateStats(Path, _acquired, _contended, _acquired == 0 ? 0 : _waitMsTotal / _acquired, _waitMsMax, _holdMsMax, _holdMaxOwner, _timeouts, _forced, Waiting,
                AcquiredTotal, TimeoutsTotal, ForcedTotal, HandlerFaultsTotal, FirstHandlerFault);
            _acquired = _contended = _timeouts = _forced = 0;
            _waitMsTotal = _waitMsMax = _holdMsMax = 0;
            _holdMaxOwner = null;
            return stats;
        }
    }

    private Hold Granted(double start, string owner, bool contended)
    {
        double waited = Math.Max(0, _clock.NowMs - start);
        var hold = new Hold(this, owner);
        Interlocked.Increment(ref _acquiredTotal);
        lock (_stats)
        {
            _acquired++;
            if (contended) _contended++;
            _waitMsTotal += waited;
            if (waited > _waitMsMax) _waitMsMax = waited;
        }

        hold.WaitedMs = waited;
        Volatile.Write(ref _current, hold); // last of the hold's own state: nothing after this can throw and leave an orphan hold published
        if (contended) Volatile.Write(ref _pendingGrantSinceMs, double.NaN); // published: the grant is taken up (a next hand-off racing this may be cleared early; cosmetic)
        return hold;
    }

    private GateEntry TimedOut(double start)
    {
        Interlocked.Increment(ref _timeoutsTotal);
        lock (_stats) _timeouts++;
        return new GateEntry(null, Math.Max(0, _clock.NowMs - start));
    }

    /// <summary>
    /// Releases the current hold when it is dead (its probe says the transaction it stood for is gone: the connection was closed or disposed, or SQLite is back in
    /// autocommit mode, e.g. a transaction rolled back by code that bypassed EF) or older than <paramref name="maxHoldMs"/> (0: never). True when one was released.
    /// </summary>
    internal bool ReleaseIfStuck(int maxHoldMs)
    {
        if (Current is not { } holder) return false;
        bool dead = holder.Probe is { } probe && SafeProbe(probe) == false;
        if (!dead && (maxHoldMs <= 0 || holder.HeldMs < maxHoldMs)) return false;
        if (!holder.MarkReleased()) return false;
        if (dead)
        {
            holder.Dead = true;
            Interlocked.Increment(ref _deadTotal);
            Exit(holder);
            NotifyHandler(DeadReleased, holder);
            return true;
        }

        holder.Forced = true;
        Interlocked.Increment(ref _forcedTotal);
        lock (_stats) _forced++;
        Exit(holder);
        NotifyHandler(StuckReleased, holder);
        return true;
    }

    /// <summary>
    /// Runs a release notification after the gate has been handed on. A throwing subscriber (the interceptor's logger) must not fail the waiter that happened to
    /// find the stuck hold, so the fault is swallowed and counted in <see cref="HandlerFaultsTotal"/>.
    /// </summary>
    private void NotifyHandler(Action<Hold>? handler, Hold holder)
    {
        if (handler is null) return;
        // Each subscriber runs on its own: one that throws must not suppress the ones after it (each interceptor subscribes separately).
        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<Hold>)subscriber)(holder);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _handlerFaultsTotal);
                Interlocked.CompareExchange(ref _firstHandlerFault, ex.GetType().Name + ": " + ex.Message, null);
            }
        }
    }

    private long _handlerFaultsTotal;
    private string? _firstHandlerFault;

    /// <summary>The type and message of the first release-notification fault since start, or null when none occurred.</summary>
    public string? FirstHandlerFault => Volatile.Read(ref _firstHandlerFault);

    /// <summary>Lifetime number of release notifications whose subscriber threw (swallowed so the waiter that ran them is not failed).</summary>
    public long HandlerFaultsTotal => Interlocked.Read(ref _handlerFaultsTotal);

    /// <summary>Raised (on the waiter's thread) when a hold older than the max hold was released; the interceptor logs it.</summary>
    internal event Action<Hold>? StuckReleased;

    /// <summary>Raised (on the waiter's thread) when a hold whose transaction had already ended was released; the interceptor logs it.</summary>
    internal event Action<Hold>? DeadReleased;

    private long _deadTotal;

    /// <summary>Lifetime number of holds released because their transaction had already ended without the interceptor seeing it.</summary>
    public long DeadTotal => Interlocked.Read(ref _deadTotal);

    private static bool SafeProbe(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return true; // cannot tell: keep it (the max hold still applies)
        }
    }

    private void Exit(Hold hold)
    {
        Interlocked.CompareExchange(ref _current, null, hold);
        double held = hold.HeldMs;
        lock (_stats)
        {
            if (held > _holdMsMax)
            {
                _holdMsMax = held;
                _holdMaxOwner = hold.Owner;
            }
        }

        HandOff();
    }

    /// <summary>One granted hold of the gate. Released at most once (<see cref="Release"/> is idempotent and thread-safe).</summary>
    public sealed class Hold
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _released;

        internal Hold(SqliteWriteGate gate, string owner)
        {
            Gate = gate;
            Owner = owner;
        }

        /// <summary>The gate this hold belongs to.</summary>
        public SqliteWriteGate Gate { get; }

        /// <summary>Who holds it: the context type and, when other writers were waiting, the calling method.</summary>
        public string Owner { get; internal set; }

        /// <summary>How long the holder waited for the gate.</summary>
        public double WaitedMs { get; internal set; }

        /// <summary>How long it has been held.</summary>
        public double HeldMs => Stopwatch.GetElapsedTime(_start).TotalMilliseconds;

        /// <summary>Whether a waiter released it because it outlived the max hold.</summary>
        public bool Forced { get; internal set; }

        /// <summary>Whether a waiter released it because its transaction had already ended.</summary>
        public bool Dead { get; internal set; }

        /// <summary>
        /// Set by the owner once the hold stands for an open transaction: returns false when that transaction is gone (connection closed or disposed, SQLite back in
        /// autocommit). Waiters call it from their own threads; it must only read state. Null: no liveness check (the max hold still applies).
        /// </summary>
        public Func<bool>? Probe { get; set; }

        /// <summary>Whether it has been released.</summary>
        public bool Released => Volatile.Read(ref _released) != 0;

        internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

        /// <summary>Releases the gate once; later calls (and calls after a forced release) do nothing.</summary>
        public void Release()
        {
            if (MarkReleased()) Gate.Exit(this);
        }
    }
}

/// <summary>Interval statistics of one <see cref="SqliteWriteGate"/> (see <see cref="SqliteWriteGate.TakeStats"/>).</summary>
public readonly record struct GateStats(
    string Path, long Acquired, long Contended, double WaitMsMean, double WaitMsMax, double HoldMsMax, string? HoldMaxOwner, long Timeouts, long Forced, int Waiting,
    long AcquiredTotal, long TimeoutsTotal, long ForcedTotal, long HandlerFaultsTotal, string? FirstHandlerFault);

/// <summary>The time sources of <see cref="SqliteWriteGate"/> waits; replaced in tests so a wait can be driven without real time.</summary>
internal interface IWriteGateClock
{
    /// <summary>Monotonic wall-clock milliseconds.</summary>
    double NowMs { get; }

    /// <summary>Cumulative milliseconds the whole process has been suspended (GC pauses only; no other stall is excluded). Time that advances here does not count against a waiter.</summary>
    double PausedMs { get; }

    /// <summary>The longest a waiter sleeps before it looks at the clocks and for a stuck hold.</summary>
    int PollMs { get; }
}

/// <summary>Stopwatch time, <see cref="GC.GetTotalPauseDuration"/> as the suspension total, a 1 s poll.</summary>
internal sealed class SystemWriteGateClock : IWriteGateClock
{
    public static readonly SystemWriteGateClock Instance = new();

    public double NowMs => Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    public double PausedMs => GC.GetTotalPauseDuration().TotalMilliseconds;

    public int PollMs => 1000;
}
