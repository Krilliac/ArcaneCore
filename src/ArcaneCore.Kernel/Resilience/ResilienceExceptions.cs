namespace ArcaneCore.Kernel.Resilience;

/// <summary>
/// Base of every refusal a resilience primitive produces instead of running (or finishing) an operation:
/// an open circuit, a full bulkhead, a timed-out call, or a dependency the guard classified as unavailable.
/// Callers that fail closed catch this one type and answer the client with "busy", never with a stack trace.
/// </summary>
public abstract class ResilienceException : Exception
{
    protected ResilienceException(string message)
        : base(message)
    {
    }

    protected ResilienceException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>The circuit is open (or half-open with its probe already in flight): the call was refused without running.</summary>
public sealed class CircuitOpenException(string circuitName, TimeSpan retryAfter)
    : ResilienceException($"circuit '{circuitName}' is open; retry after {retryAfter.TotalMilliseconds:0} ms")
{
    /// <summary>The <see cref="CircuitBreakerOptions.Name"/> of the breaker that refused.</summary>
    public string CircuitName { get; } = circuitName;

    /// <summary>How long until the breaker lets a probe through (zero when a probe is already running).</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>Every slot and every queue position of the bulkhead is taken: the call was refused without waiting.</summary>
public sealed class BulkheadRejectedException(string bulkheadName, int maxConcurrency, int maxQueue)
    : ResilienceException($"bulkhead '{bulkheadName}' is full ({maxConcurrency} running, {maxQueue} queued)")
{
    public string BulkheadName { get; } = bulkheadName;
}

/// <summary>The operation did not finish within the policy's timeout; it was cancelled (cooperative) or abandoned (pessimistic).</summary>
public sealed class TimeoutRejectedException(TimeSpan timeout)
    : ResilienceException($"operation did not finish within {timeout.TotalMilliseconds:0} ms")
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>
/// A guarded dependency failed with an error its classifier calls transient (connection refused, server gone,
/// lock wait, I/O timeout). The original error is <see cref="Exception.InnerException"/>; the message never carries a
/// connection string because provider messages do not, and callers log it as a reason, not as a stack.
/// </summary>
public sealed class DependencyUnavailableException(string dependency, Exception inner)
    : ResilienceException($"{dependency} unavailable: {inner.GetType().Name}: {inner.Message}", inner)
{
    /// <summary>What was unavailable, e.g. "Auth database".</summary>
    public string Dependency { get; } = dependency;
}
