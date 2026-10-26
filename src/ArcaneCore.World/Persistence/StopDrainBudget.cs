using System.Diagnostics;
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
/// The shutdown bound of a group of write queues, counted in <b>stall</b>, not in work: the in-flight time of store attempts that ended without a write landing,
/// accumulated since the last write that did land (or since <see cref="Begin"/>). Idle time between queues, non-database stop work and every successful write cost
/// nothing, and a success resets the accumulator. When <see cref="StalledMs"/> reaches <see cref="StallBudget"/> the lane is <see cref="Exhausted"/>: every later attempt on
/// it is refused before it starts (<see cref="Refuse"/>), the write stays retained with a <see cref="StopDrainExhaustedException"/> and the queue names it. A stuck drain
/// therefore costs at most the budget plus the one attempt that crossed it. Thread-safe; one lane may be shared by many queues (see <see cref="ShutdownDrain"/>).
/// </summary>
public sealed class StopDrainBudget
{
    /// <summary>The default stall budget: the characters database's <c>busy_timeout</c>, so a single locked attempt is enough to exhaust a lane.</summary>
    public static readonly TimeSpan DefaultStallBudget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private long _beginTimestamp; // 0: not started
    private long _stalledTicks; // in-flight ticks of attempts that ended without success, since the last success (or Begin)
    private int _inFlight; // attempts running now
    private long _inFlightSince; // Stopwatch timestamp from which the running attempts' time is not yet in _stalledTicks
    private int _succeeded;
    private int _failed;
    private int _refused;
    private StopDrainExhaustedException? _lastRefusal;

    /// <summary>How much stalled time exhausts the lane.</summary>
    public TimeSpan StallBudget { get; init; } = DefaultStallBudget;

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

            _beginTimestamp = Math.Max(1, Stopwatch.GetTimestamp());
            _stalledTicks = 0;
            _succeeded = 0;
            _failed = 0;
            _refused = 0;
            _lastRefusal = null;
            _inFlightSince = _beginTimestamp;
        }
    }

    /// <summary>The stalled time so far in milliseconds, including attempts still running; 0 when not started.</summary>
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
            _lastRefusal = new StopDrainExhaustedException(
                $"{Name}: stalled {StalledMsLocked():0} ms of {StallBudget.TotalMilliseconds:0} ms with no write landing; the write was not attempted and stays retained");
            return _lastRefusal;
        }
    }

    /// <summary>Brackets one store attempt. Disposing it without a verdict counts the attempt as failed (an exception escaped the try).</summary>
    public Attempt BeginAttempt()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_inFlight == 0)
            {
                _inFlightSince = now;
            }

            _inFlight++;
            return new Attempt(this, now);
        }
    }

    /// <summary>One-line summary for logs.</summary>
    public string Describe()
        => $"{Name}: {Succeeded} landed, {Failed} failed, {Refused} refused, stalled {StalledMs:0} ms of {StallBudget.TotalMilliseconds:0} ms";

    private bool ExhaustedLocked() => _beginTimestamp != 0 && StalledMsLocked() >= StallBudget.TotalMilliseconds;

    private double StalledMsLocked()
    {
        if (_beginTimestamp == 0)
        {
            return 0;
        }

        long ticks = _stalledTicks;
        if (_inFlight > 0)
        {
            ticks += Math.Max(0, Stopwatch.GetTimestamp() - Math.Max(_inFlightSince, _beginTimestamp));
        }

        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private void Finish(long start, bool landed)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            _inFlight--;
            if (landed)
            {
                _stalledTicks = 0;
                _succeeded++;
            }
            else
            {
                if (_beginTimestamp != 0)
                {
                    _stalledTicks += Math.Max(0, now - Math.Max(start, _beginTimestamp));
                }

                _failed++;
            }

            // Whatever is still running is accounted from here on; its earlier time is either reset (success) or already counted (failure overlap).
            _inFlightSince = now;
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

        /// <summary>The attempt ended without landing: its in-flight time becomes stalled time.</summary>
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
