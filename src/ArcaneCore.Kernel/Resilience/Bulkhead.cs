namespace ArcaneCore.Kernel.Resilience;

/// <summary>Concurrency and queue caps of a <see cref="Bulkhead"/>.</summary>
public sealed class BulkheadOptions
{
    public string Name { get; set; } = "bulkhead";

    /// <summary>Calls allowed to run at once.</summary>
    public int MaxConcurrency { get; set; } = 32;

    /// <summary>Calls allowed to wait for a slot; 0 rejects as soon as every slot is taken.</summary>
    public int MaxQueue { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Name must not be empty";
        }

        if (MaxConcurrency < 1)
        {
            return "MaxConcurrency must be at least 1";
        }

        if (MaxQueue < 0)
        {
            return "MaxQueue must be 0 or positive";
        }

        return null;
    }
}

/// <summary>
/// Caps how many calls run at once against one dependency, with a bounded queue in front; anything beyond the queue is
/// refused immediately with <see cref="BulkheadRejectedException"/>, so a slow dependency cannot absorb every thread.
/// <para>
/// <b>Ownership and threads.</b> One instance per dependency, shared; every member is thread-safe. The slots are a
/// <see cref="SemaphoreSlim"/>; the queue depth is an interlocked counter checked before anyone waits.
/// <b>Allocation.</b> The fast path (a slot is free) is <see cref="SemaphoreSlim.Wait(int)"/> with a zero timeout:
/// no allocation and no await. Only a call that has to queue allocates the semaphore's wait task.
/// </para>
/// </summary>
public sealed class Bulkhead : IDisposable
{
    private readonly BulkheadOptions _options;
    private readonly SemaphoreSlim _slots;
    private int _queued;
    private long _rejected;

    public Bulkhead(BulkheadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        _options = options;
        _slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
    }

    public string Name => _options.Name;

    public BulkheadOptions Options => _options;

    /// <summary>Calls running now.</summary>
    public int Running => _options.MaxConcurrency - _slots.CurrentCount;

    /// <summary>Calls waiting for a slot now.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>Calls refused because both the slots and the queue were full.</summary>
    public long Rejected => Volatile.Read(ref _rejected);

    /// <summary>Take a slot without waiting. True: run and then call <see cref="Exit"/>. False: no slot is free (the queue is not consulted).</summary>
    public bool TryEnter() => _slots.Wait(0);

    /// <summary>
    /// Take a slot, waiting in the queue when none is free. Faults with <see cref="BulkheadRejectedException"/> at once
    /// when the queue is full too (no waiting, no synchronous throw). Pair with <see cref="Exit"/>.
    /// </summary>
    public ValueTask EnterAsync(CancellationToken cancellationToken = default)
    {
        if (_slots.Wait(0))
        {
            return default;
        }

        if (Interlocked.Increment(ref _queued) > _options.MaxQueue)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _rejected);
            return ValueTask.FromException(new BulkheadRejectedException(Name, _options.MaxConcurrency, _options.MaxQueue));
        }

        return new ValueTask(WaitQueuedAsync(cancellationToken));
    }

    private async Task WaitQueuedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>Give the slot back. Exactly once per successful <see cref="TryEnter"/> or <see cref="EnterAsync"/>.</summary>
    public void Exit() => _slots.Release();

    /// <summary>Run <paramref name="operation"/> inside the bulkhead. A free slot and a synchronous completion allocate nothing.</summary>
    public ValueTask<TResult> ExecuteAsync<TState, TResult>(
        Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValueTask entered = EnterAsync(cancellationToken);
        if (!entered.IsCompletedSuccessfully)
        {
            return RunAfterAsync(entered, operation, state, cancellationToken);
        }

        ValueTask<TResult> pending;
        try
        {
            pending = operation(state, cancellationToken);
        }
        catch (Exception ex)
        {
            Exit();
            return ValueTask.FromException<TResult>(ex);
        }

        if (pending.IsCompletedSuccessfully)
        {
            Exit();
            return pending;
        }

        return ExitAfterAsync(pending);
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

    private async ValueTask<TResult> RunAfterAsync<TState, TResult>(
        ValueTask entered, Func<TState, CancellationToken, ValueTask<TResult>> operation, TState state, CancellationToken cancellationToken)
    {
        await entered.ConfigureAwait(false); // faults with the rejection, or waits in the queue
        try
        {
            return await operation(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private async ValueTask<TResult> ExitAfterAsync<TResult>(ValueTask<TResult> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    public void Dispose() => _slots.Dispose();
}
