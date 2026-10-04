namespace ArcaneCore.Kernel.Resilience;

/// <summary>How a <see cref="TimeoutPolicy"/> enforces its limit.</summary>
public enum TimeoutStrategy
{
    /// <summary>Cancel the operation's token and wait for it to observe the cancellation. Right for every honest async API.</summary>
    Cooperative = 0,

    /// <summary>
    /// Run the operation on the thread pool and stop waiting for it when the time is up (its token is cancelled too).
    /// For APIs that block or ignore their token (Microsoft.Data.Sqlite's async methods are synchronous underneath).
    /// The abandoned call keeps its thread until it ends; its eventual exception is observed and discarded.
    /// </summary>
    Pessimistic = 1,
}

/// <summary>
/// Bounds one call. <b>Allocation:</b> one <see cref="CancellationTokenSource"/> carrying the deadline timer per call,
/// plus a second, linked one when the caller's token can be cancelled (the price of a per-call deadline); pessimistic
/// mode adds a thread-pool work item and a <see cref="Task.WhenAny(Task[])"/>. Use it around I/O that already
/// allocates, never around a per-packet in-memory step. Immutable and thread-safe.
/// <para>
/// <b>Deadline versus disposal.</b> The deadline is <c>new CancellationTokenSource(Timeout, timeProvider)</c>, never a
/// separate timer whose callback calls <see cref="CancellationTokenSource.Cancel()"/>: a timer's <c>Dispose()</c> does
/// not wait for a callback the timer queue has already dequeued, so an operation completing at the deadline would let
/// that callback find a disposed source and throw on a timer thread, which aborts the process. The source's own timer
/// callback is the runtime's, written for exactly that race (a late firing against a disposed source is a no-op).
/// <c>TimeoutPolicyDeadlineRaceTests</c> pins this down.
/// </para>
/// </summary>
public sealed class TimeoutPolicy
{
    private readonly TimeProvider _time;

    public TimeoutPolicy(TimeSpan timeout, TimeProvider? timeProvider = null, TimeoutStrategy strategy = TimeoutStrategy.Cooperative)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "timeout must be positive and at most one day");
        }

        Timeout = timeout;
        Strategy = strategy;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Timeout { get; }

    public TimeoutStrategy Strategy { get; }

    /// <summary>Run <paramref name="operation"/>; throws <see cref="TimeoutRejectedException"/> when it outlives <see cref="Timeout"/>.</summary>
    public async ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var deadline = new CancellationTokenSource(Timeout, _time);
        using CancellationTokenSource? linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token)
            : null;
        CancellationToken token = linked?.Token ?? deadline.Token;
        try
        {
            if (Strategy == TimeoutStrategy.Cooperative)
            {
                return await operation(state, token).ConfigureAwait(false);
            }

            Task<TResult> work = Task.Run(() => operation(state, token).AsTask(), CancellationToken.None);
            Task expiry = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, _time, token);
            Task first = await Task.WhenAny(work, expiry).ConfigureAwait(false);
            if (first == work)
            {
                return await work.ConfigureAwait(false);
            }

            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutRejectedException(Timeout);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutRejectedException(Timeout);
        }
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
}
