namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// A clock that moves only when the test calls <see cref="Advance"/>, firing every timer that falls due inside the step.
/// Used to drive the guard's query deadline (<see cref="Kernel.Resilience.TimeoutPolicy"/> arms it through
/// <see cref="TimeProvider.CreateTimer"/>) without depending on how fast a loaded machine schedules real timers.
/// </summary>
internal sealed class HandDrivenClock : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<HandTimer> _timers = [];
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
        var timer = new HandTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Move the clock forward, firing due timers in due order.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            HandTimer? next;
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

    private sealed class HandTimer(HandDrivenClock owner, TimerCallback callback, object? state) : ITimer
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
