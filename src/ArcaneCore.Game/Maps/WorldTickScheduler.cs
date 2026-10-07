namespace ArcaneCore.Game.Maps;

/// <summary>
/// When the world thread starts its next tick. Pure arithmetic on millisecond readings of one monotonic clock, so the
/// policy is testable without sleeping.
/// </summary>
public sealed class WorldTickScheduler
{
    private readonly int _intervalMs;
    private long? _nextDueMs;

    public WorldTickScheduler(int intervalMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMs, 1);
        _intervalMs = intervalMs;
    }

    public int IntervalMs => _intervalMs;

    /// <summary>Ticks that started after their scheduled start.</summary>
    public long LateTicks { get; private set; }

    /// <summary>Scheduled starts given up because the loop fell a whole interval or more behind (not caught up).</summary>
    public long SkippedTicks { get; private set; }

    /// <summary>
    /// A tick that started at <paramref name="tickStartMs"/> finished at <paramref name="nowMs"/>: how long to wait
    /// before the next one.
    /// <para>
    /// Drift-compensated fixed cadence: ticks are due at start + k × interval, so a wait that oversleeps (the Windows
    /// timer wakes in ~15.6 ms steps) is made up by the next shorter wait instead of lowering the rate. A loop that
    /// falls a whole interval or more behind (a long tick, a GC or paging stall) does not catch up with a burst of
    /// back-to-back ticks — each tick already receives its real elapsed time as its diff — it gives the missed starts
    /// up (<see cref="SkippedTicks"/>) and restarts the cadence from now. That bounds the work after any stall to one
    /// immediate tick: no spiral of death.
    /// </para>
    /// </summary>
    public int NextWait(long tickStartMs, long nowMs)
    {
        if (_nextDueMs is not { } due)
        {
            due = tickStartMs;
        }
        else if (tickStartMs > due)
        {
            LateTicks++;
        }

        due += _intervalMs;
        long behind = nowMs - due;
        if (behind >= _intervalMs)
        {
            SkippedTicks += behind / _intervalMs;
            LateTicks++; // the next tick starts now, after the start it was originally due at
            _nextDueMs = nowMs;
            return 0;
        }

        _nextDueMs = due;
        return due > nowMs ? (int)(due - nowMs) : 0;
    }
}
