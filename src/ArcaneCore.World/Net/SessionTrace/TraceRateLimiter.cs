namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// A token bucket over a monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/> units). It starts full, refills linearly at
/// <c>perSecond</c> tokens per second, never holds more than <c>burst</c>, and ignores a timestamp that goes backwards. Tokens are kept as a
/// whole number scaled by the timer frequency, so a refill loses no fraction of a token to rounding. Not thread-safe: the owner locks.
/// </summary>
internal sealed class TraceRateLimiter
{
    private readonly long _perSecond;
    private readonly long _ticksPerSecond;
    private readonly long _capacity;
    private long _scaledTokens;
    private long _last;

    /// <param name="perSecond">Tokens added per second; at least 1.</param>
    /// <param name="burst">Bucket capacity in tokens, full at the start; at least 1.</param>
    /// <param name="startTimestamp">The timestamp the bucket was created at.</param>
    /// <param name="ticksPerSecond">The frequency of the timestamps; at least 1.</param>
    public TraceRateLimiter(int perSecond, int burst, long startTimestamp, long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _perSecond = perSecond;
        _ticksPerSecond = ticksPerSecond;
        _capacity = burst * ticksPerSecond;
        _scaledTokens = _capacity;
        _last = startTimestamp;
    }

    /// <summary>Takes one token at timestamp <paramref name="now"/>; false when the bucket is empty.</summary>
    public bool TryTake(long now)
    {
        long elapsed = now - _last;
        if (elapsed > 0)
        {
            _last = now;
            long missing = _capacity - _scaledTokens;

            // elapsed * perSecond is below the capacity whenever this branch is not taken, so it cannot overflow; a gap that
            // refills the whole bucket never multiplies.
            _scaledTokens = elapsed > missing / _perSecond ? _capacity : _scaledTokens + (elapsed * _perSecond);
        }

        if (_scaledTokens < _ticksPerSecond)
        {
            return false;
        }

        _scaledTokens -= _ticksPerSecond;
        return true;
    }
}
