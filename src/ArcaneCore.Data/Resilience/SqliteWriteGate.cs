using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// The in-process single-writer gate of one SQLite database file (<c>Database:Sqlite:SingleWriter</c>). SQLite admits one writer at a time even in WAL mode, and
/// a writer that finds the lock taken polls it from SQLite's busy handler (sleeps of 1-100 ms, no queue), so with a dozen background write queues on one
/// characters database the writers starve each other and some fail with "database is locked" although the total work fits easily
/// (live 2026-10-17: 959 locked lines in 24 h, a 1000-character save backlog). Every write transaction and every autocommit write statement that EF runs against the file
/// first takes this gate, so writers inside the process queue in arrival order on an async semaphore and are handed the lock the moment the previous one ends,
/// instead of racing for it. Readers never take it (WAL readers do not block the writer).
/// <para>
/// Bounded like the lock it replaces: a waiter gives up after its connection's write bound and fails with SQLite's own busy error (code 5), so callers that retain
/// and retry a locked write behave exactly as before, but the message names the holder. A hold that outlives <see cref="SqliteSingleWriterOptions.MaxHoldMs"/> (a leaked transaction) is
/// released by the next waiter that finds it and logged; that only lets the next writer try SQLite itself, as it would without the gate.
/// </para>
/// One instance per normalized file path, shared process-wide (<see cref="For"/>). Thread-safe.
/// </summary>
public sealed class SqliteWriteGate
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SqliteWriteGate> Gates = new(PathComparer);
    private static readonly ConcurrentDictionary<string, string?> Keys = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _semaphore = new(1, 1);
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

    private SqliteWriteGate(string path) => Path = path;

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
        return key is null ? null : Gates.GetOrAdd(key, static path => new SqliteWriteGate(path));
    }

    /// <summary>Every gate created in this process (diagnostics).</summary>
    public static IReadOnlyCollection<SqliteWriteGate> All => Gates.Values.ToArray();

    /// <summary>Takes the gate, waiting at most <paramref name="timeoutMs"/>; null on timeout. Blocks the calling thread.</summary>
    public Hold? Enter(int timeoutMs, string owner, int maxHoldMs)
    {
        long start = Stopwatch.GetTimestamp();
        if (_semaphore.Wait(0)) return Granted(start, owner, contended: false);
        Interlocked.Increment(ref _waiting);
        try
        {
            if (ReleaseIfStuck(maxHoldMs) && _semaphore.Wait(0)) return Granted(start, owner, contended: true);
            int remaining = Remaining(start, timeoutMs);
            while (remaining > 0)
            {
                // Wake at least every second to release a stuck hold, so a leaked transaction cannot block for longer than max hold plus a second.
                if (_semaphore.Wait(Math.Min(remaining, 1000))) return Granted(start, owner, contended: true);
                ReleaseIfStuck(maxHoldMs);
                remaining = Remaining(start, timeoutMs);
            }

            return TimedOut();
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Takes the gate, waiting at most <paramref name="timeoutMs"/>; null on timeout.</summary>
    public async ValueTask<Hold?> EnterAsync(int timeoutMs, string owner, int maxHoldMs, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        if (_semaphore.Wait(0)) return Granted(start, owner, contended: false);
        Interlocked.Increment(ref _waiting);
        try
        {
            if (ReleaseIfStuck(maxHoldMs) && _semaphore.Wait(0)) return Granted(start, owner, contended: true);
            int remaining = Remaining(start, timeoutMs);
            if (remaining <= 0) return TimedOut();

            // One queued wait for the whole bound: re-queueing every second (as the blocking Enter does) sends the waiter to the back of the semaphore's FIFO queue,
            // so a queue deeper than a second of holds starves its oldest writers until they time out. A side tick wakes us to release a stuck hold meanwhile.
            Task<bool> wait = _semaphore.WaitAsync(remaining, cancellationToken);
            using var tick = new CancellationTokenSource();
            try
            {
                while (!wait.IsCompleted)
                {
                    if (await Task.WhenAny(wait, Task.Delay(1000, tick.Token)).ConfigureAwait(false) == wait) break;
                    ReleaseIfStuck(maxHoldMs);
                }
            }
            finally
            {
                tick.Cancel();
            }

            return await wait.ConfigureAwait(false) ? Granted(start, owner, contended: true) : TimedOut();
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>The busy error a timed-out waiter reports: SQLITE_BUSY (5), so every existing "locked: retain and retry" path treats it as before.</summary>
    public SqliteException Busy(int waitedMs)
    {
        Hold? holder = Current;
        string held = holder is null
            ? "no current holder"
            : $"held {holder.HeldMs:0} ms by {holder.Owner}";
        return new SqliteException($"SQLite Error 5: 'database is locked' (in-process write gate: waited {waitedMs} ms for {System.IO.Path.GetFileName(Path)}; {held}; {Waiting} waiting)", 5);
    }

    /// <summary>Returns and resets the interval statistics.</summary>
    public GateStats TakeStats()
    {
        lock (_stats)
        {
            var stats = new GateStats(Path, _acquired, _contended, _acquired == 0 ? 0 : _waitMsTotal / _acquired, _waitMsMax, _holdMsMax, _holdMaxOwner, _timeouts, _forced, Waiting,
                AcquiredTotal, TimeoutsTotal, ForcedTotal);
            _acquired = _contended = _timeouts = _forced = 0;
            _waitMsTotal = _waitMsMax = _holdMsMax = 0;
            _holdMaxOwner = null;
            return stats;
        }
    }

    private static int Remaining(long start, int timeoutMs)
        => timeoutMs - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private Hold Granted(long start, string owner, bool contended)
    {
        double waited = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var hold = new Hold(this, owner);
        Volatile.Write(ref _current, hold);
        Interlocked.Increment(ref _acquiredTotal);
        lock (_stats)
        {
            _acquired++;
            if (contended) _contended++;
            _waitMsTotal += waited;
            if (waited > _waitMsMax) _waitMsMax = waited;
        }

        hold.WaitedMs = waited;
        return hold;
    }

    private Hold? TimedOut()
    {
        Interlocked.Increment(ref _timeoutsTotal);
        lock (_stats) _timeouts++;
        return null;
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
            DeadReleased?.Invoke(holder);
            return true;
        }

        holder.Forced = true;
        Interlocked.Increment(ref _forcedTotal);
        lock (_stats) _forced++;
        Exit(holder);
        StuckReleased?.Invoke(holder);
        return true;
    }

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

        _semaphore.Release();
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
    long AcquiredTotal, long TimeoutsTotal, long ForcedTotal);
