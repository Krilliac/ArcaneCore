namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// A clock the test moves by hand. Timestamps are 100 ns ticks; <see cref="Advance"/> fires every timer whose due time
/// falls inside the step, in order, so <c>Task.Delay(…, provider, …)</c> and the timeout policy's deadline run under
/// test control without real waiting.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(GetTimestamp());

    /// <summary>Timers that still have a due time.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Move the clock forward, firing due timers in due order (a callback may arm another timer inside the step).</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, next.Due!.Value);
                next.Due = next.PeriodTicks > 0 ? next.Due + next.PeriodTicks : null;
            }

            next.Fire();
        }
    }

    /// <summary>Advance to the next due timer, if any; false when none is pending.</summary>
    public bool AdvanceToNextTimer()
    {
        long? due;
        lock (_gate)
        {
            due = _timers.Where(t => t.Due is not null).Min(t => t.Due);
        }

        if (due is null)
        {
            return false;
        }

        Advance(TimeSpan.FromTicks(Math.Max(0, due.Value - GetTimestamp())));
        return true;
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due;
        public long PeriodTicks;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(0, dueTime.Ticks);
                PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

/// <summary>Drives a fake clock until an asynchronous operation under it completes.</summary>
internal static class FakeClockRunner
{
    /// <summary>
    /// Advance <paramref name="clock"/> timer by timer (yielding between steps so continuations run) until
    /// <paramref name="task"/> completes; fails after <paramref name="maxSteps"/> steps or when nothing is pending.
    /// </summary>
    public static async Task RunUntilCompleteAsync(FakeTimeProvider clock, Task task, int maxSteps = 1000)
    {
        for (int step = 0; step < maxSteps && !task.IsCompleted; step++)
        {
            // Let the operation reach its next delay before the clock moves.
            for (int spin = 0; spin < 50 && !task.IsCompleted && clock.PendingTimers == 0; spin++)
            {
                await Task.Delay(1);
            }

            if (task.IsCompleted)
            {
                break;
            }

            if (!clock.AdvanceToNextTimer())
            {
                await Task.Delay(1);
            }
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
