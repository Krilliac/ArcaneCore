namespace ArcaneCore.World.Persistence;

/// <summary>
/// Posts one sweep marker into a write queue's single consumer every interval while the queue has retained (failed, not yet durable)
/// writes. Never stacks: a second marker is not posted until the consumer reports the first one handled (<see cref="SweepFinished"/>).
/// The sweep itself runs on the consumer, so it is ordered with every other item of the queue.
/// Thread-safe. <see cref="StopAsync"/> is idempotent; nothing is posted after it returns.
/// </summary>
/// <param name="interval">The tick period (tests shorten it).</param>
/// <param name="hasRetained">True while the queue holds a retained write; called on the timer thread, must take the queue's own gate.</param>
/// <param name="post">Writes the sweep marker into the consumer's channel; false when the channel is closed.</param>
internal sealed class RetainedRetryTimer(TimeSpan interval, Func<bool> hasRetained, Func<bool> post)
{
    // Deliberately never disposed: a second StopAsync reads IsCancellationRequested.
    private readonly CancellationTokenSource _stop = new();
    private int _sweepQueued; // 1 while a marker is in the channel or being swept
    private Task? _loop;

    /// <summary>
    /// Start ticking. <see cref="Timeout.InfiniteTimeSpan"/> never ticks (tests drive sweeps with <see cref="TryPost"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The interval is under 1 ms (PeriodicTimer truncates to whole milliseconds) and is not <see cref="Timeout.InfiniteTimeSpan"/>, or exceeds what a timer accepts.</exception>
    public void Start()
    {
        if (interval != Timeout.InfiniteTimeSpan && (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "the retained-write retry interval must be positive or Timeout.InfiniteTimeSpan");
        }

        if (interval != Timeout.InfiniteTimeSpan)
        {
            _loop ??= Task.Run(RunAsync);
        }
    }

    /// <summary>Post a marker unless one is already pending; true when one was posted.</summary>
    public bool TryPost()
    {
        if (Interlocked.CompareExchange(ref _sweepQueued, 1, 0) != 0)
        {
            return false;
        }

        if (post())
        {
            return true;
        }

        Volatile.Write(ref _sweepQueued, 0);
        return false;
    }

    /// <summary>The consumer handled the marker; the next tick may post another.</summary>
    public void SweepFinished() => Volatile.Write(ref _sweepQueued, 0);

    public async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (hasRetained())
                {
                    TryPost();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
