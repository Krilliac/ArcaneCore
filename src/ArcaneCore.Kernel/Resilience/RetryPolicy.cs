namespace ArcaneCore.Kernel.Resilience;

/// <summary>How the exponential delay is randomised.</summary>
public enum RetryJitter
{
    /// <summary>Exactly the exponential delay (fixed polling).</summary>
    None = 0,

    /// <summary>"Full jitter" (AWS Architecture Blog, 2015): uniform in [0, exponential delay]. Spreads a herd of retriers.</summary>
    Full = 1,
}

/// <summary>Bounded attempts with exponential backoff.</summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first; 1 means no retry.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay after the first failure; doubles after each further failure.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Cap on the exponential delay before jitter.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    public RetryJitter Jitter { get; set; } = RetryJitter.Full;

    /// <summary>Overall budget measured from the first attempt; zero means attempts alone bound the retry. A retry whose delay would cross it is not made.</summary>
    public TimeSpan MaxTotalDuration { get; set; } = TimeSpan.Zero;

    /// <summary>The first problem with these options, or null when they are usable.</summary>
    public string? Validate()
    {
        if (MaxAttempts < 1)
        {
            return "MaxAttempts must be at least 1";
        }

        if (BaseDelay < TimeSpan.Zero || BaseDelay > TimeSpan.FromHours(1))
        {
            return "BaseDelay must be 0..1 hour";
        }

        if (MaxDelay < BaseDelay || MaxDelay > TimeSpan.FromHours(1))
        {
            return "MaxDelay must be at least BaseDelay and at most 1 hour";
        }

        if (MaxTotalDuration < TimeSpan.Zero || MaxTotalDuration > TimeSpan.FromDays(1))
        {
            return "MaxTotalDuration must be 0 (unbounded) or up to one day";
        }

        return null;
    }
}

/// <summary>One retry decision, delivered to the retry callback before the delay.</summary>
/// <param name="Attempt">The attempt (1-based) that just failed.</param>
/// <param name="Delay">How long the policy waits before the next attempt.</param>
/// <param name="Exception">The failure (null for <see cref="RetryPolicy.ExecuteUntilAsync{TState}"/>, which retries on a false result).</param>
public readonly record struct RetryAttempt(int Attempt, TimeSpan Delay, Exception? Exception);

/// <summary>
/// Retries a transient failure a bounded number of times with exponential backoff and full jitter.
/// <para>
/// <b>Ownership and threads.</b> Immutable after construction; one instance serves any number of concurrent callers.
/// <b>Allocation.</b> Nothing per call beyond what the wrapped operation and <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// allocate while waiting; the random sample comes from <see cref="Random.Shared"/>. A first attempt that succeeds
/// synchronously completes synchronously.
/// </para>
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<Exception, bool> _isTransient;
    private readonly Action<RetryAttempt>? _onRetry;
    private readonly Func<double> _sample;

    private static readonly Func<double> SharedSample = static () => Random.Shared.NextDouble();

    /// <param name="options">Attempts and delays; validated here.</param>
    /// <param name="timeProvider">Clock for the delays and the total budget.</param>
    /// <param name="isTransient">Which exceptions are retried; <see cref="TransientFailure.IsTransient"/> by default. Anything else propagates at once.</param>
    /// <param name="onRetry">Called before each wait (logging).</param>
    /// <param name="sample">Source of uniform [0,1) samples for the jitter; tests inject a fixed one.</param>
    public RetryPolicy(
        RetryOptions options,
        TimeProvider? timeProvider = null,
        Func<Exception, bool>? isTransient = null,
        Action<RetryAttempt>? onRetry = null,
        Func<double>? sample = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _isTransient = isTransient ?? TransientFailure.Classifier;
        _onRetry = onRetry;
        _sample = sample ?? SharedSample;
    }

    public RetryOptions Options => _options;

    /// <summary>
    /// The wait after the <paramref name="attempt"/>-th failure (1-based): <c>min(MaxDelay, BaseDelay · 2^(attempt-1))</c>,
    /// multiplied by <paramref name="sample"/> under <see cref="RetryJitter.Full"/>. Pure, so the jitter bounds are testable.
    /// </summary>
    public static TimeSpan ComputeDelay(RetryOptions options, int attempt, double sample)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        int exponent = Math.Min(attempt - 1, 30); // 2^30 · 1 ms is already ~12 days; MaxDelay caps it anyway
        double exponential = Math.Min(options.MaxDelay.TotalMilliseconds, options.BaseDelay.TotalMilliseconds * (1L << exponent));
        double jittered = options.Jitter == RetryJitter.Full ? exponential * Math.Clamp(sample, 0, 1) : exponential;
        return TimeSpan.FromMilliseconds(jittered);
    }

    /// <summary>
    /// Run <paramref name="operation"/>, retrying transient failures. The last failure propagates unchanged. A first
    /// attempt that completes synchronously is returned as is (no state machine, no allocation).
    /// </summary>
    public ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        long started = _time.GetTimestamp();
        ValueTask<TResult> first = Attempt(operation, state, cancellationToken);
        return first.IsCompletedSuccessfully ? first : ContinueAsync(first, operation, state, started, cancellationToken);
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

    private static ValueTask<TResult> Attempt<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken)
    {
        try
        {
            return operation(state, cancellationToken);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<TResult>(ex); // a synchronous throw is an attempt that failed, like any other
        }
    }

    private async ValueTask<TResult> ContinueAsync<TState, TResult>(
        ValueTask<TResult> pending,
        Func<TState, CancellationToken, ValueTask<TResult>> operation,
        TState state,
        long started,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _options.MaxAttempts && !cancellationToken.IsCancellationRequested && _isTransient(ex))
            {
                TimeSpan delay = ComputeDelay(_options, attempt, _sample());
                if (!WithinBudget(started, delay))
                {
                    throw;
                }

                _onRetry?.Invoke(new RetryAttempt(attempt, delay, ex));
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }

            pending = Attempt(operation, state, cancellationToken);
        }
    }

    /// <summary>
    /// Poll: run <paramref name="tryOnce"/> until it returns true. Returns false when the attempts or the total budget
    /// run out (the last poll is made right at the budget's end). Exceptions are not retried here; they propagate.
    /// This is the shape of a lock wait: the schema lock polls <c>GET_LOCK</c> this way.
    /// </summary>
    public async ValueTask<bool> ExecuteUntilAsync<TState>(
        Func<TState, CancellationToken, ValueTask<bool>> tryOnce, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tryOnce);
        long started = _time.GetTimestamp();
        for (int attempt = 1; ; attempt++)
        {
            if (await tryOnce(state, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (attempt >= _options.MaxAttempts)
            {
                return false;
            }

            TimeSpan delay = ComputeDelay(_options, attempt, _sample());
            if (_options.MaxTotalDuration > TimeSpan.Zero)
            {
                TimeSpan remaining = _options.MaxTotalDuration - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                if (delay > remaining)
                {
                    delay = remaining; // the last poll happens at the deadline, not after it
                }
            }

            _onRetry?.Invoke(new RetryAttempt(attempt, delay, null));
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool WithinBudget(long started, TimeSpan delay)
        => _options.MaxTotalDuration == TimeSpan.Zero || _time.GetElapsedTime(started) + delay <= _options.MaxTotalDuration;
}
