namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Admits at most one log line per interval for one kind of event and counts what it suppressed,
/// so a flood of refused connections or malformed frames costs one line per interval instead of
/// one per packet (a logger on the hot path is itself a denial-of-service surface).
/// <para>
/// Thread-safe and allocation-free: <see cref="TryEnter"/> is two interlocked operations on fields
/// of this object; the caller formats the line (which boxes its arguments) only when admitted.
/// The clock is injectable for tests and defaults to <see cref="Environment.TickCount64"/>.
/// </para>
/// </summary>
public sealed class LogGate
{
    private readonly long _intervalMs;
    private readonly Func<long> _clock;
    private long _nextAllowedMs;
    private int _suppressed;

    public LogGate(TimeSpan interval, Func<long>? clock = null)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _intervalMs = (long)interval.TotalMilliseconds;
        _clock = clock ?? Clock.Milliseconds;
        _nextAllowedMs = long.MinValue;
    }

    /// <summary>Lines that were not admitted since the last admitted one (diagnostics).</summary>
    public int Suppressed => Volatile.Read(ref _suppressed);

    /// <summary>
    /// True when the caller may log now; <paramref name="suppressed"/> is how many calls were refused
    /// since the previous admitted line (print it so nothing is lost). False means stay silent.
    /// </summary>
    public bool TryEnter(out int suppressed)
    {
        long now = _clock();
        long next = Volatile.Read(ref _nextAllowedMs);
        if (now < next)
        {
            Interlocked.Increment(ref _suppressed);
            suppressed = 0;
            return false;
        }

        // Several threads may read the same stale 'next'; only the one whose exchange wins logs.
        if (Interlocked.CompareExchange(ref _nextAllowedMs, now + _intervalMs, next) != next)
        {
            Interlocked.Increment(ref _suppressed);
            suppressed = 0;
            return false;
        }

        suppressed = Interlocked.Exchange(ref _suppressed, 0);
        return true;
    }
}

/// <summary>The monotonic millisecond clock the protections share (never wall-clock: it must not jump).</summary>
public static class Clock
{
    /// <summary>A cached delegate for <see cref="Environment.TickCount64"/>, so no closure is allocated per use.</summary>
    public static readonly Func<long> Milliseconds = static () => Environment.TickCount64;
}
