using System.Data.Common;
using System.Net.Sockets;

namespace ArcaneCore.Kernel.Resilience;

/// <summary>
/// The default answer to "is this error worth retrying, and should it count against a circuit?". Transient means
/// the dependency, not the request, failed: the same call may succeed a moment later. Business errors
/// (<see cref="InvalidOperationException"/>, an argument error, a domain exception) are never transient, and neither
/// is the caller's own cancellation. The Data layer extends this with provider-specific codes.
/// </summary>
public static class TransientFailure
{
    /// <summary>How far down the <see cref="Exception.InnerException"/> chain the classifier looks.</summary>
    public const int MaxDepth = 8;

    /// <summary>The cached delegate form of <see cref="IsTransient"/>, so hot-path callers do not allocate one per call.</summary>
    public static readonly Func<Exception, bool> Classifier = IsTransient;

    /// <summary>True when <paramref name="exception"/>, or any inner exception within <see cref="MaxDepth"/>, is a transient dependency failure.</summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            switch (current)
            {
                // Refusals a primitive produced: a timed-out call is the dependency's fault; an open circuit or
                // a full bulkhead is ours, and retrying into it would only defeat the fail-fast.
                case TimeoutRejectedException:
                case DependencyUnavailableException:
                    return true;
                case CircuitOpenException:
                case BulkheadRejectedException:
                    return false;

                // The caller's own cancellation is not a dependency failure.
                case OperationCanceledException:
                    return false;

                case TimeoutException:
                case SocketException:
                case IOException:
                    return true;

                // MySqlConnector and Npgsql set IsTransient for connection loss, deadlocks and lock waits.
                case DbException { IsTransient: true }:
                    return true;

                case AggregateException aggregate:
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (depth + 1 < MaxDepth && IsTransient(inner))
                        {
                            return true;
                        }
                    }

                    return false;
            }

            current = current.InnerException;
        }

        return false;
    }
}
