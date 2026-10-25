namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Token buckets per key (docs/areas/atlas-actions.md, "Rate limits"): each key holds up to <c>burst</c> tokens and gets <c>perSecond</c> back
/// every second; an action takes one. Time comes from the injected <see cref="TimeProvider"/> (<c>GetTimestamp</c>), so tests wind it by hand.
/// Thread-safe; one lock, held for a dictionary lookup and a few multiplications. Idle full buckets are dropped when the table grows.
/// </summary>
internal sealed class MapFeedActionRateLimit(TimeProvider time)
{
    private const int PruneAbove = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>
    /// Take a token for <paramref name="key"/>. True when there was one; false with <paramref name="retryMs"/> (at least 1) set to the
    /// milliseconds until the next token otherwise.
    /// </summary>
    public bool TryTake(string key, int perSecond, int burst, out int retryMs)
    {
        long now = time.GetTimestamp();
        double frequency = time.TimestampFrequency;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                if (_buckets.Count >= PruneAbove)
                {
                    Prune(now, frequency, perSecond, burst);
                }

                bucket = new Bucket { Tokens = burst, Stamp = now };
                _buckets[key] = bucket;
            }
            else
            {
                double elapsed = Math.Max(0, now - bucket.Stamp) / frequency;
                bucket.Tokens = Math.Min(burst, bucket.Tokens + (elapsed * perSecond));
                bucket.Stamp = now;
            }

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                retryMs = 0;
                return true;
            }

            retryMs = (int)Math.Clamp(Math.Ceiling((1.0 - bucket.Tokens) / perSecond * 1000.0), 1, int.MaxValue);
            return false;
        }
    }

    private void Prune(long now, double frequency, int perSecond, int burst)
    {
        List<string>? idle = null;
        foreach ((string key, Bucket bucket) in _buckets)
        {
            double elapsed = Math.Max(0, now - bucket.Stamp) / frequency;
            if (bucket.Tokens + (elapsed * perSecond) >= burst)
            {
                (idle ??= []).Add(key);
            }
        }

        if (idle is not null)
        {
            foreach (string key in idle)
            {
                _buckets.Remove(key);
            }
        }
    }

    private sealed class Bucket
    {
        public double Tokens;

        public long Stamp;
    }
}
