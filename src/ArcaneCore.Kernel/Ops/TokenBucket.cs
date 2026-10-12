namespace ArcaneCore.Kernel.Ops;

/// <summary>
/// A token bucket for log lines: <c>burst</c> lines at once, then <c>refillPerMinute</c> per minute. A refusal is counted and reported with
/// the next admitted line. Not thread-safe (the owner locks, or uses it from one thread); a value type so a per-session bucket costs no
/// allocation of its own. Must be built with the constructor (a default value has no burst).
/// </summary>
public struct TokenBucket
{
    private readonly double _capacity;
    private readonly double _perMs;
    private double _tokens;
    private long _lastMs;
    private long _suppressed;

    public TokenBucket(int burst, int refillPerMinute)
    {
        _capacity = Math.Max(1, burst);
        _perMs = Math.Max(1, refillPerMinute) / 60_000.0;
        _tokens = _capacity;
        _lastMs = long.MinValue;
        _suppressed = 0;
    }

    /// <summary>Lines refused since the last admitted one.</summary>
    public readonly long Suppressed => _suppressed;

    /// <summary>True when a line may be written at <paramref name="nowMs"/> (any monotonic millisecond clock); <paramref name="suppressed"/> is how many were refused since the previous admitted line.</summary>
    public bool TryTake(long nowMs, out long suppressed)
    {
        if (_lastMs != long.MinValue && nowMs > _lastMs)
        {
            _tokens = Math.Min(_capacity, _tokens + ((nowMs - _lastMs) * _perMs));
        }

        _lastMs = nowMs;
        if (_tokens >= 1.0)
        {
            _tokens -= 1.0;
            suppressed = _suppressed;
            _suppressed = 0;
            return true;
        }

        _suppressed++;
        suppressed = 0;
        return false;
    }
}
