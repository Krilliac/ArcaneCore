using System.Runtime.CompilerServices;

namespace ArcaneCore.Kernel.Resilience;

/// <summary>The state of a <see cref="CircuitBreaker"/>.</summary>
public enum CircuitState
{
    /// <summary>Calls flow; failures are counted.</summary>
    Closed = 0,

    /// <summary>Calls are refused until <see cref="CircuitBreakerOptions.OpenDuration"/> has passed.</summary>
    Open = 1,

    /// <summary>Up to <see cref="CircuitBreakerOptions.HalfOpenMaxProbes"/> calls are let through to test the dependency.</summary>
    HalfOpen = 2,

    /// <summary>Opened by hand (<see cref="CircuitBreaker.Isolate"/>); only <see cref="CircuitBreaker.Reset"/> closes it.</summary>
    Isolated = 3,
}

/// <summary>One transition of a breaker, delivered to the state-change callback outside the breaker's lock.</summary>
/// <param name="Name">The breaker's name.</param>
/// <param name="From">The state before.</param>
/// <param name="To">The state after.</param>
/// <param name="Cause">The failure that caused an opening, when one did.</param>
public readonly record struct CircuitStateChange(string Name, CircuitState From, CircuitState To, Exception? Cause);

/// <summary>
/// A closed / open / half-open circuit breaker.
/// <para>
/// <b>Ownership and threads.</b> One instance guards one dependency and is shared by every caller of it; all members
/// are safe to call from any thread. State lives behind one uncontended <see cref="Lock"/> taken for a few dozen
/// instructions per call (no I/O, no callbacks under it). The state-change callback runs on the thread that caused
/// the transition, after the lock is released, so it may log or call back into the breaker.
/// </para>
/// <para>
/// <b>Allocation.</b> The fast path (closed, the call succeeds) allocates nothing: the sliding window is a fixed
/// array built in the constructor, time comes from <see cref="TimeProvider.GetTimestamp"/>, and the
/// <c>Execute</c> overloads take a state argument so callers pass <c>static</c> lambdas. The
/// <see cref="ValueTask"/> overloads allocate only when the wrapped operation itself completes asynchronously
/// (the compiler's state-machine box), exactly as the operation would without the breaker. A refusal allocates its
/// <see cref="CircuitOpenException"/>; callers that must not throw use <see cref="TryEnter(out TimeSpan)"/>,
/// <see cref="OnSuccess"/> and <see cref="OnFailure"/> directly.
/// </para>
/// </summary>
public sealed class CircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<Exception, bool> _isFailure;
    private readonly Action<CircuitStateChange>? _onStateChange;
    private readonly Lock _gate = new();
    private readonly Bucket[] _buckets;
    private readonly long _bucketTicks;
    private readonly long _windowTicks;

    private CircuitState _state;
    private int _consecutiveFailures;
    private int _probesInFlight;
    private long _openedAt;
    private long _rejected;

    /// <param name="options">Thresholds and durations; validated here, a bad value throws <see cref="ArgumentException"/>.</param>
    /// <param name="timeProvider">The clock; tests pass a fake, production uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="isFailure">Which exceptions count as failures; by default every exception except the caller's own cancellation. A non-failure exception passes through and leaves the counters alone.</param>
    /// <param name="onStateChange">Called after every transition, outside the lock.</param>
    public CircuitBreaker(
        CircuitBreakerOptions options,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isFailure = null,
        Action<CircuitStateChange>? onStateChange = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _isFailure = isFailure ?? DefaultIsFailure;
        _onStateChange = onStateChange;
        _buckets = new Bucket[options.SamplingBuckets];
        _windowTicks = ToTimestampTicks(options.SamplingWindow);
        _bucketTicks = Math.Max(1, _windowTicks / options.SamplingBuckets);
    }

    private static readonly Func<Exception, bool> DefaultIsFailure = static ex => ex is not OperationCanceledException;

    public string Name => _options.Name;

    public CircuitBreakerOptions Options => _options;

    /// <summary>The current state (a snapshot; another thread may change it right after).</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Calls refused because the circuit was open, half-open with its probes busy, or isolated.</summary>
    public long Rejected => Volatile.Read(ref _rejected);

    /// <summary>Consecutive failures seen since the last success while closed (diagnostics).</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    // ---- low-level protocol (no exceptions, no allocation) --------------------------------------------------------

    /// <summary>
    /// Ask to run a call. True: run it and report exactly one of <see cref="OnSuccess"/> or <see cref="OnFailure"/>
    /// (or <see cref="OnNeutral"/> when the outcome says nothing about the dependency). False: refused;
    /// <paramref name="retryAfter"/> says how long until a probe is allowed (zero when one is already running).
    /// </summary>
    public bool TryEnter(out TimeSpan retryAfter)
    {
        CircuitStateChange? change = null;
        bool admitted;
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    retryAfter = TimeSpan.Zero;
                    admitted = true;
                    break;

                case CircuitState.Open:
                    TimeSpan open = _time.GetElapsedTime(_openedAt);
                    if (open >= _options.OpenDuration)
                    {
                        change = Transition(CircuitState.HalfOpen, null);
                        _probesInFlight = 1;
                        retryAfter = TimeSpan.Zero;
                        admitted = true;
                    }
                    else
                    {
                        retryAfter = _options.OpenDuration - open;
                        admitted = false;
                    }

                    break;

                case CircuitState.HalfOpen:
                    retryAfter = TimeSpan.Zero;
                    if (_probesInFlight < _options.HalfOpenMaxProbes)
                    {
                        _probesInFlight++;
                        admitted = true;
                    }
                    else
                    {
                        admitted = false;
                    }

                    break;

                default: // Isolated
                    retryAfter = Timeout.InfiniteTimeSpan;
                    admitted = false;
                    break;
            }

            if (!admitted)
            {
                _rejected++;
            }
        }

        Notify(change);
        return admitted;
    }

    /// <summary><see cref="TryEnter(out TimeSpan)"/> without the wait hint.</summary>
    public bool TryEnter() => TryEnter(out _);

    /// <summary>The call admitted by <see cref="TryEnter()"/> succeeded.</summary>
    public void OnSuccess()
    {
        CircuitStateChange? change = null;
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    _consecutiveFailures = 0;
                    Record(success: true);
                    break;

                case CircuitState.HalfOpen:
                    // The probe proved the dependency is back: close and forget the old failures.
                    _probesInFlight--;
                    ResetCounters();
                    change = Transition(CircuitState.Closed, null);
                    break;

                // Open / Isolated: a straggler that was admitted before the circuit opened; nothing to learn.
            }
        }

        Notify(change);
    }

    /// <summary>The call admitted by <see cref="TryEnter()"/> failed with <paramref name="exception"/>.</summary>
    public void OnFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!_isFailure(exception))
        {
            OnNeutral();
            return;
        }

        CircuitStateChange? change = null;
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    _consecutiveFailures++;
                    Record(success: false);
                    if (ShouldTrip())
                    {
                        _openedAt = _time.GetTimestamp();
                        change = Transition(CircuitState.Open, exception);
                    }

                    break;

                case CircuitState.HalfOpen:
                    // The probe failed: the dependency is still down, start another open period.
                    _probesInFlight--;
                    _openedAt = _time.GetTimestamp();
                    change = Transition(CircuitState.Open, exception);
                    break;
            }
        }

        Notify(change);
    }

    /// <summary>The admitted call ended without saying anything about the dependency (e.g. the caller cancelled it).</summary>
    public void OnNeutral()
    {
        lock (_gate)
        {
            if (_state == CircuitState.HalfOpen && _probesInFlight > 0)
            {
                _probesInFlight--;
            }
        }
    }

    /// <summary>Open by hand until <see cref="Reset"/> (an operator command, or a dependency known to be down).</summary>
    public void Isolate()
    {
        CircuitStateChange? change = null;
        lock (_gate)
        {
            if (_state != CircuitState.Isolated)
            {
                change = Transition(CircuitState.Isolated, null);
            }
        }

        Notify(change);
    }

    /// <summary>Close by hand and forget every failure.</summary>
    public void Reset()
    {
        CircuitStateChange? change = null;
        lock (_gate)
        {
            ResetCounters();
            if (_state != CircuitState.Closed)
            {
                change = Transition(CircuitState.Closed, null);
            }
        }

        Notify(change);
    }

    // ---- execute wrappers ------------------------------------------------------------------------------------------

    /// <summary>Run <paramref name="operation"/> under the breaker (synchronous). Throws <see cref="CircuitOpenException"/> when refused.</summary>
    public TResult Execute<TState, TResult>(Func<TState, TResult> operation, TState state)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!TryEnter(out TimeSpan retryAfter))
        {
            throw new CircuitOpenException(Name, retryAfter);
        }

        TResult result;
        try
        {
            result = operation(state);
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            throw;
        }

        OnSuccess();
        return result;
    }

    /// <summary>Run <paramref name="operation"/> under the breaker (synchronous, no result).</summary>
    public void Execute<TState>(Action<TState> operation, TState state)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!TryEnter(out TimeSpan retryAfter))
        {
            throw new CircuitOpenException(Name, retryAfter);
        }

        try
        {
            operation(state);
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            throw;
        }

        OnSuccess();
    }

    /// <summary>
    /// Run <paramref name="operation"/> under the breaker. A cancellation requested through
    /// <paramref name="cancellationToken"/> is neutral (neither success nor failure). A refusal is a faulted
    /// <see cref="ValueTask{TResult}"/> (<see cref="CircuitOpenException"/>), never a synchronous throw. An operation
    /// that completes synchronously is handled without a state machine, so that path allocates nothing.
    /// </summary>
    public ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!TryEnter(out TimeSpan retryAfter))
        {
            return ValueTask.FromException<TResult>(new CircuitOpenException(Name, retryAfter));
        }

        ValueTask<TResult> pending;
        try
        {
            pending = operation(state, cancellationToken);
        }
        catch (Exception ex)
        {
            Outcome(ex, cancellationToken);
            return ValueTask.FromException<TResult>(ex);
        }

        if (pending.IsCompletedSuccessfully)
        {
            OnSuccess();
            return pending;
        }

        return AwaitAsync(pending, cancellationToken);
    }

    /// <summary><see cref="ExecuteAsync{TState, TResult}"/> for an operation without a result.</summary>
    public ValueTask ExecuteAsync<TState>(
        Func<TState, CancellationToken, ValueTask> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValueTask<bool> run = ExecuteAsync(
            static (s, ct) => ValueTaskAdapters.ToBool(s.Operation(s.State, ct)), (Operation: operation, State: state), cancellationToken);
        return run.IsCompletedSuccessfully ? default : new ValueTask(run.AsTask());
    }

    private async ValueTask<TResult> AwaitAsync<TResult>(ValueTask<TResult> pending, CancellationToken cancellationToken)
    {
        TResult result;
        try
        {
            result = await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Outcome(ex, cancellationToken);
            throw;
        }

        OnSuccess();
        return result;
    }

    private void Outcome(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            OnNeutral();
        }
        else
        {
            OnFailure(exception);
        }
    }

    // ---- internals (all under _gate) -------------------------------------------------------------------------------

    private CircuitStateChange Transition(CircuitState to, Exception? cause)
    {
        CircuitState from = _state;
        _state = to;
        if (to != CircuitState.HalfOpen)
        {
            _probesInFlight = 0;
        }

        return new CircuitStateChange(Name, from, to, cause);
    }

    private void ResetCounters()
    {
        _consecutiveFailures = 0;
        _probesInFlight = 0;
        Array.Clear(_buckets);
    }

    private bool ShouldTrip()
    {
        if (_options.FailureThreshold > 0 && _consecutiveFailures >= _options.FailureThreshold)
        {
            return true;
        }

        if (_options.FailureRateThreshold <= 0)
        {
            return false;
        }

        long now = _time.GetTimestamp();
        long oldest = now - _windowTicks;
        int successes = 0;
        int failures = 0;
        foreach (ref readonly Bucket bucket in _buckets.AsSpan())
        {
            if (bucket.Used && bucket.Start > oldest)
            {
                successes += bucket.Successes;
                failures += bucket.Failures;
            }
        }

        int total = successes + failures;
        return total >= _options.MinimumThroughput && failures >= _options.FailureRateThreshold * total;
    }

    /// <summary>Count one outcome in the bucket of the current instant, recycling a bucket whose period has passed.</summary>
    private void Record(bool success)
    {
        long now = _time.GetTimestamp();
        long start = now - (now % _bucketTicks);
        ref Bucket bucket = ref _buckets[(int)((now / _bucketTicks) % _buckets.Length)];
        if (!bucket.Used || bucket.Start != start)
        {
            bucket = new Bucket { Used = true, Start = start };
        }

        if (success)
        {
            bucket.Successes++;
        }
        else
        {
            bucket.Failures++;
        }
    }

    private long ToTimestampTicks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Notify(CircuitStateChange? change)
    {
        if (change is { } c)
        {
            _onStateChange?.Invoke(c);
        }
    }

    private struct Bucket
    {
        public bool Used;
        public long Start;
        public int Successes;
        public int Failures;
    }
}
