using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The failure a write is retained with when the stop drain refused to start its attempt. It is never a verdict on the write: no queue may abandon, convert or drop a
/// write because of it (see <see cref="StopDrainBudget.IsNoVerdict"/> and <c>TaxiFlightWriteQueue</c>). It derives from <see cref="TimeoutException"/> so existing
/// callers that treat a retained failure as a timeout keep working.
/// </summary>
public sealed class StopDrainExhaustedException(string message) : TimeoutException(message);

/// <summary>Counts the attempts a write actually made (for the anomaly detail); passed by reference into the attempt loop. Single writer: the loop that owns the write.</summary>
internal sealed class WriteAttemptCount
{
    /// <summary>Attempts started so far.</summary>
    internal int Made;
}

/// <summary>
/// The shutdown bound of a group of write queues, counted in <b>stall</b>, not in work: the wall time covered by store attempts that <b>ended</b> without a write landing,
/// measured from the later of <see cref="Begin"/> and the last write that did land. An attempt still running counts nothing until its verdict (it may be a slow write that
/// lands), overlapping failed attempts count their wall time once, and retry backoff (outside the attempt), idle time between queues, non-database stop work and every
/// successful write cost nothing; a success resets the accumulator. When <see cref="StalledMs"/> reaches <see cref="StallBudget"/> the lane is <see cref="Exhausted"/>: every
/// later attempt on it is refused before it starts (<see cref="Refuse"/>), the write stays retained with a <see cref="StopDrainExhaustedException"/> and the queue names it.
/// A stuck drain therefore costs at most the budget plus the one attempt that crossed it. Thread-safe; one lane may be shared by many queues (see <see cref="ShutdownDrain"/>).
/// </summary>
public sealed class StopDrainBudget
{
    /// <summary>The default stall budget: two of the characters database's <c>busy_timeout</c>s (5 s each), so a lane is exhausted after two locked attempts that landed nothing.</summary>
    public static readonly TimeSpan DefaultStallBudget = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private long _beginTimestamp; // 0: not started
    private long _stalledTicks; // wall ticks covered by attempts that ended without success, since the last success (or Begin); a union, not a sum
    private long _coveredUntil; // Time timestamp up to which failed coverage is already in _stalledTicks
    private int _inFlight; // attempts running now (diagnostics only: never consulted by Refuse or Exhausted)
    private int _succeeded;
    private int _failed;
    private int _refused;
    private StopDrainExhaustedException? _lastRefusal;
    private string? _suppressedReason;

    /// <summary>How much stalled time exhausts the lane.</summary>
    public TimeSpan StallBudget { get; init; } = DefaultStallBudget;

    /// <summary>
    /// The clock the attempts are timed by (the system clock). Tests step a manual one: on the wall clock a loaded runner's sleeps overshoot,
    /// and a 60 ms attempt measured long enough to exhaust a 100 ms budget on its own.
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Names the lane in refusals and in <see cref="Describe"/>.</summary>
    public string Name { get; init; } = "stop drain";

    /// <summary>True once <see cref="Begin"/> was called.</summary>
    public bool Started
    {
        get
        {
            lock (_gate)
            {
                return _beginTimestamp != 0;
            }
        }
    }

    /// <summary>Starts the lane (the first call only): stamps the time and zeroes the counters, so attempts that failed during normal running never pre-exhaust it.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_beginTimestamp != 0)
            {
                return;
            }

            _beginTimestamp = Math.Max(1, Time.GetTimestamp());
            _stalledTicks = 0;
            _succeeded = 0;
            _failed = 0;
            _refused = 0;
            _lastRefusal = null;
            _coveredUntil = _beginTimestamp;
        }
    }

    /// <summary>
    /// Why every attempt on this lane is refused from now on (null: not suppressed). Set by <see cref="Suppress"/> when the world must stop without writing: the characters database is
    /// being written by another world, so the shutdown saves would land in its data.
    /// </summary>
    public string? SuppressedReason
    {
        get
        {
            lock (_gate)
            {
                return _suppressedReason;
            }
        }
    }

    /// <summary>
    /// Refuses every later attempt on this lane, whether or not it was <see cref="Begin"/>-started: the write stays retained with a <see cref="StopDrainExhaustedException"/> naming
    /// <paramref name="reason"/>, and <see cref="Exhausted"/> is true. Queues refuse every attempt on a suppressed lane, whether or not they are stopping, so nothing more is written through them. The first reason stays.
    /// </summary>
    public void Suppress(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _suppressedReason ??= reason;
        }
    }

    /// <summary>True once <see cref="Suppress"/> was called. Queues refuse every attempt on a suppressed lane, stopping or not (see <see cref="Refuse"/>).</summary>
    public bool IsSuppressed => SuppressedReason is not null;

    /// <summary>The stalled time so far in milliseconds, counting completed failed attempts only; 0 when not started.</summary>
    public double StalledMs
    {
        get
        {
            lock (_gate)
            {
                return StalledMsLocked();
            }
        }
    }

    /// <summary>True when started and <see cref="StalledMs"/> has reached <see cref="StallBudget"/>.</summary>
    public bool Exhausted
    {
        get
        {
            lock (_gate)
            {
                return ExhaustedLocked();
            }
        }
    }

    /// <summary>Attempts that landed since <see cref="Begin"/>.</summary>
    public int Succeeded
    {
        get
        {
            lock (_gate)
            {
                return _succeeded;
            }
        }
    }

    /// <summary>Attempts that ended without landing since <see cref="Begin"/>.</summary>
    public int Failed
    {
        get
        {
            lock (_gate)
            {
                return _failed;
            }
        }
    }

    /// <summary>Attempts running right now. Diagnostics only: it never decides a refusal.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>Attempts refused before they started since <see cref="Begin"/>.</summary>
    public int Refused
    {
        get
        {
            lock (_gate)
            {
                return _refused;
            }
        }
    }

    /// <summary>The most recent refusal (null when none): what the queues' stop reports record as the fault.</summary>
    public StopDrainExhaustedException? LastRefusal
    {
        get
        {
            lock (_gate)
            {
                return _lastRefusal;
            }
        }
    }

    /// <summary>True when a failure says nothing about the write itself, so no queue may abandon or convert the write because of it: the file is held by another writer, the stop drain refused the attempt, or the character is owned elsewhere.</summary>
    internal static bool IsNoVerdict(Exception ex)
        => DatabaseTransience.IsBusy(ex) || ex is StopDrainExhaustedException || ex is CharacterOwnershipRejectedException;

    /// <summary>Null when an attempt may start. Otherwise counts a refusal and returns the exception to retain the write with.</summary>
    public StopDrainExhaustedException? Refuse()
    {
        lock (_gate)
        {
            if (!ExhaustedLocked())
            {
                return null;
            }

            _refused++;
            _lastRefusal = new StopDrainExhaustedException(_suppressedReason is not null
                ? $"{Name}: writes are suppressed ({_suppressedReason}); the write was not attempted and stays retained"
                : $"{Name}: stalled {StalledMsLocked():0} ms of {StallBudget.TotalMilliseconds:0} ms with no write landing; the write was not attempted and stays retained");
            return _lastRefusal;
        }
    }

    /// <summary>Brackets one store attempt. Disposing it without a verdict counts the attempt as failed (an exception escaped the try).</summary>
    public Attempt BeginAttempt()
    {
        lock (_gate)
        {
            _inFlight++;
            return new Attempt(this, Time.GetTimestamp());
        }
    }

    /// <summary>One-line summary for logs.</summary>
    public string Describe()
        => $"{Name}: {Succeeded} landed, {Failed} failed, {Refused} refused, {InFlight} in flight, stalled {StalledMs:0} ms of {StallBudget.TotalMilliseconds:0} ms";

    private bool ExhaustedLocked() => _suppressedReason is not null || (_beginTimestamp != 0 && StalledMsLocked() >= StallBudget.TotalMilliseconds);

    private double StalledMsLocked() => _beginTimestamp == 0 ? 0 : _stalledTicks * 1000.0 / Time.TimestampFrequency; // completed failures only: a running attempt is unknown until its verdict

    private void Finish(long start, bool landed)
    {
        lock (_gate)
        {
            long now = Time.GetTimestamp();
            _inFlight--;
            if (landed)
            {
                _stalledTicks = 0;
                _coveredUntil = now;
                _succeeded++;
            }
            else
            {
                if (_beginTimestamp != 0)
                {
                    // Only the part of this attempt after Begin, after the last landing and not yet covered by another failed attempt counts: overlapping failures cover the same wall time once.
                    long from = Math.Max(start, Math.Max(_beginTimestamp, _coveredUntil));
                    if (now > from)
                    {
                        _stalledTicks += now - from;
                    }

                    if (now > _coveredUntil)
                    {
                        _coveredUntil = now;
                    }
                }

                _failed++;
            }
        }
    }

    /// <summary>One store attempt in flight. Both verdicts are idempotent: the first one wins.</summary>
    public sealed class Attempt : IDisposable
    {
        private readonly StopDrainBudget _owner;
        private readonly long _start;
        private int _done;

        internal Attempt(StopDrainBudget owner, long start)
        {
            _owner = owner;
            _start = start;
        }

        /// <summary>The write landed: resets the lane's stalled time.</summary>
        public void Succeeded()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _owner.Finish(_start, landed: true);
            }
        }

        /// <summary>The attempt ended without landing: the wall time it covered becomes stalled time.</summary>
        public void Failed()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                _owner.Finish(_start, landed: false);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Failed();
    }
}
