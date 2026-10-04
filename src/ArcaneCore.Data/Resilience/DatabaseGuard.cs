using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// The adapter every database caller goes through to get fail-closed behaviour: bulkhead → circuit breaker → per-call
/// timeout (<see cref="Policy"/> order), one pipeline per logical database, built once from <c>Resilience:Database</c>.
/// <list type="bullet">
/// <item>A call the pipeline refuses (open circuit, full bulkhead) or cuts off (timeout) throws a <see cref="ResilienceException"/>.</item>
/// <item>A call the store fails with a <see cref="DatabaseTransience">transient</see> error counts against the circuit and is
/// rethrown as <see cref="DependencyUnavailableException"/> (also a <see cref="ResilienceException"/>), so the caller has one type to
/// catch for "the database is the problem".</item>
/// <item>Any other exception (constraint, domain rule, bad query) passes through untouched and leaves the circuit alone.</item>
/// </list>
/// <para>
/// <b>Threads and allocation.</b> Singleton, thread-safe, immutable after construction. The fast path adds no
/// allocation of its own to the store call (the pipeline threads a struct frame through cached static lambdas);
/// with a timeout configured, each call allocates the deadline's token source and timer, see <see cref="TimeoutPolicy"/>.
/// The World write queues keep their own ordering and retention; their migration is described in docs/ops/resilience.md.
/// </para>
/// </summary>
public sealed class DatabaseGuard
{
    private readonly ResiliencePipeline[] _pipelines;
    private readonly RetryPolicy _bootstrapRetry;
    private readonly ILogger<DatabaseGuard> _logger;

    public DatabaseGuard(IOptions<ResilienceOptions> options, DatabaseCircuits circuits, ILogger<DatabaseGuard> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(circuits);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        DatabaseResilienceOptions settings = options.Value.Database;
        Enabled = settings.Enabled;
        Circuits = circuits;
        QueryTimeout = TimeSpan.FromMilliseconds(settings.QueryTimeoutMs);

        DatabaseComponent[] components = Enum.GetValues<DatabaseComponent>();
        _pipelines = new ResiliencePipeline[components.Length];
        foreach (DatabaseComponent component in components)
        {
            string name = DatabaseCircuits.Describe(component);
            PolicyBuilder builder = Policy.Builder(name).WithCircuitBreaker(circuits.For(component));
            if (settings.Bulkhead.ToOptions(name) is { } bulkhead)
            {
                builder.WithBulkhead(new Bulkhead(bulkhead));
            }

            if (settings.QueryTimeoutMs > 0)
            {
                builder.WithTimeout(new TimeoutPolicy(QueryTimeout, timeProvider));
            }

            _pipelines[(int)component] = builder.Build();
        }

        _bootstrapRetry = new RetryPolicy(settings.Bootstrap.ToOptions(), timeProvider, DatabaseTransience.Classifier, OnBootstrapRetry);
    }

    /// <summary><c>Resilience:Database:Enabled</c>; when false every call goes straight to the store.</summary>
    public bool Enabled { get; }

    public DatabaseCircuits Circuits { get; }

    /// <summary>The per-call deadline (zero when disabled).</summary>
    public TimeSpan QueryTimeout { get; }

    /// <summary>The pipeline of one logical database (for callers that compose further, e.g. a queue adding its own retry).</summary>
    public ResiliencePipeline PipelineFor(DatabaseComponent component) => _pipelines[(int)component];

    /// <summary>Run one store call against <paramref name="component"/> under its pipeline.</summary>
    public ValueTask<TResult> ExecuteAsync<TState, TResult>(
        DatabaseComponent component, Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Enabled ? GuardedAsync(component, operation, state, cancellationToken) : operation(state, cancellationToken);
    }

    /// <summary><see cref="ExecuteAsync{TState, TResult}"/> for a call without a result.</summary>
    public async ValueTask ExecuteAsync<TState>(
        DatabaseComponent component, Func<TState, CancellationToken, ValueTask> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync(
            component,
            static async (s, ct) =>
            {
                await s.Operation(s.State, ct).ConfigureAwait(false);
                return true;
            },
            (Operation: operation, State: state),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Run the start-up schema bootstrap, retrying a transient failure (server not up yet, connection refused) with
    /// the <c>Resilience:Database:Bootstrap</c> backoff. A schema refusal is not transient and is never retried. The
    /// last failure propagates for <see cref="Schema.Upgrade.DatabaseStartup"/> to turn into exit code 6.
    /// </summary>
    public Task BootstrapAsync(Func<Task> initialize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        if (!Enabled)
        {
            return initialize();
        }

        return _bootstrapRetry.ExecuteAsync(static (init, _) => new ValueTask(init()), initialize, cancellationToken).AsTask();
    }

    private ValueTask<TResult> GuardedAsync<TState, TResult>(
        DatabaseComponent component, Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken)
    {
        ValueTask<TResult> pending = _pipelines[(int)component].ExecuteAsync(operation, state, cancellationToken);
        return pending.IsCompletedSuccessfully ? pending : TranslateAsync(component, pending, cancellationToken);
    }

    private static async ValueTask<TResult> TranslateAsync<TResult>(DatabaseComponent component, ValueTask<TResult> pending, CancellationToken cancellationToken)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ResilienceException && !cancellationToken.IsCancellationRequested && DatabaseTransience.IsTransient(ex))
        {
            throw new DependencyUnavailableException(DatabaseCircuits.Describe(component), ex);
        }
    }

    private void OnBootstrapRetry(RetryAttempt attempt)
        => _logger.LogWarning(
            "database bootstrap attempt {Attempt} of {MaxAttempts} failed ({Reason}); retrying in {DelayMs} ms",
            attempt.Attempt, _bootstrapRetry.Options.MaxAttempts, DatabaseCircuits.Reason(attempt.Exception), attempt.Delay.TotalMilliseconds);
}
