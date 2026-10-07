namespace ArcaneCore.Kernel.Resilience;

/// <summary>
/// Composes the primitives into one <see cref="ResiliencePipeline"/>. The order is fixed and is the one that makes
/// each layer meaningful, outermost first: <b>bulkhead</b> (cap the callers before they wait on anything),
/// <b>retry</b> (each attempt goes through the breaker, so an open circuit ends the retries at once because
/// <see cref="CircuitOpenException"/> is not transient), <b>circuit breaker</b> (counts the per-attempt outcome),
/// <b>timeout</b> (bounds one attempt, and a timeout is a failure the breaker counts). Every layer is optional.
/// </summary>
public static class Policy
{
    public static PolicyBuilder Builder(string name) => new(name);
}

/// <summary>Fluent builder for a <see cref="ResiliencePipeline"/>; see <see cref="Policy"/> for the layer order.</summary>
public sealed class PolicyBuilder
{
    private readonly string _name;
    private Bulkhead? _bulkhead;
    private RetryPolicy? _retry;
    private CircuitBreaker? _breaker;
    private TimeoutPolicy? _timeout;

    internal PolicyBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    public PolicyBuilder WithBulkhead(Bulkhead bulkhead)
    {
        _bulkhead = bulkhead ?? throw new ArgumentNullException(nameof(bulkhead));
        return this;
    }

    public PolicyBuilder WithRetry(RetryPolicy retry)
    {
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        return this;
    }

    public PolicyBuilder WithCircuitBreaker(CircuitBreaker breaker)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        return this;
    }

    public PolicyBuilder WithTimeout(TimeoutPolicy timeout)
    {
        _timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        return this;
    }

    public ResiliencePipeline Build() => new(_name, _bulkhead, _retry, _breaker, _timeout);
}

/// <summary>
/// The composed policy. <b>Allocation:</b> the layers are threaded through one generic struct frame and cached
/// <c>static</c> lambdas, so a call that succeeds synchronously allocates nothing beyond what the configured layers
/// allocate themselves (a timeout layer allocates its deadline, see <see cref="TimeoutPolicy"/>). Immutable and thread-safe.
/// </summary>
public sealed class ResiliencePipeline
{
    private readonly Bulkhead? _bulkhead;
    private readonly RetryPolicy? _retry;
    private readonly CircuitBreaker? _breaker;
    private readonly TimeoutPolicy? _timeout;

    internal ResiliencePipeline(string name, Bulkhead? bulkhead, RetryPolicy? retry, CircuitBreaker? breaker, TimeoutPolicy? timeout)
    {
        Name = name;
        _bulkhead = bulkhead;
        _retry = retry;
        _breaker = breaker;
        _timeout = timeout;
    }

    public string Name { get; }

    public Bulkhead? Bulkhead => _bulkhead;

    public RetryPolicy? Retry => _retry;

    public CircuitBreaker? CircuitBreaker => _breaker;

    public TimeoutPolicy? Timeout => _timeout;

    /// <summary>Run <paramref name="operation"/> through every configured layer.</summary>
    public ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var frame = new Frame<TState, TResult>(this, operation, state);
        return _bulkhead is null
            ? RunRetry(frame, cancellationToken)
            : _bulkhead.ExecuteAsync(static (f, ct) => f.Pipeline.RunRetry(f, ct), frame, cancellationToken);
    }

    /// <summary><see cref="ExecuteAsync{TState, TResult}"/> for an operation without a result.</summary>
    public async ValueTask ExecuteAsync<TState>(
        Func<TState, CancellationToken, ValueTask> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(
            static async (s, ct) =>
            {
                await s.Operation(s.State, ct).ConfigureAwait(false);
                return true;
            },
            (Operation: operation, State: state),
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<TResult> RunRetry<TState, TResult>(Frame<TState, TResult> frame, CancellationToken ct)
        => _retry is null
            ? RunBreaker(frame, ct)
            : _retry.ExecuteAsync(static (f, c) => f.Pipeline.RunBreaker(f, c), frame, ct);

    private ValueTask<TResult> RunBreaker<TState, TResult>(Frame<TState, TResult> frame, CancellationToken ct)
        => _breaker is null
            ? RunTimeout(frame, ct)
            : _breaker.ExecuteAsync(static (f, c) => f.Pipeline.RunTimeout(f, c), frame, ct);

    private ValueTask<TResult> RunTimeout<TState, TResult>(Frame<TState, TResult> frame, CancellationToken ct)
        => _timeout is null
            ? frame.Operation(frame.State, ct)
            : _timeout.ExecuteAsync(static (f, c) => f.Operation(f.State, c), frame, ct);

    private readonly struct Frame<TState, TResult>(
        ResiliencePipeline pipeline, Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state)
    {
        public readonly ResiliencePipeline Pipeline = pipeline;
        public readonly Func<TState, CancellationToken, ValueTask<TResult>> Operation = operation;
        public readonly TState State = state;
    }
}
