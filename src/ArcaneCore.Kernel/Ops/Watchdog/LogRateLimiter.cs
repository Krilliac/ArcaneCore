namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// Admits at most one log line per interval and counts the ones it suppressed, so a storm of
/// overruns is one warning that says "and 412 more", not 412 warnings. Owned and used by one
/// thread (the watchdog thread); it is a struct with no allocation.
/// </summary>
public struct LogRateLimiter(long intervalMicros)
{
    private long _lastMicros = long.MinValue;
    private long _suppressed;

    /// <summary>Lines refused since the last admitted one.</summary>
    public long Suppressed => _suppressed;

    /// <summary>True when a line may be logged now; the suppressed count is reset and returned in <paramref name="suppressed"/>.</summary>
    public bool TryAcquire(long nowMicros, out long suppressed)
    {
        if (_lastMicros == long.MinValue || nowMicros - _lastMicros >= intervalMicros)
        {
            _lastMicros = nowMicros;
            suppressed = _suppressed;
            _suppressed = 0;
            return true;
        }

        _suppressed++;
        suppressed = 0;
        return false;
    }

    /// <summary>Forget the last admission so the next <see cref="TryAcquire"/> succeeds.</summary>
    public void Reset()
    {
        _lastMicros = long.MinValue;
        _suppressed = 0;
    }
}
