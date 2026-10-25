namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// Token buckets keyed by a string (an address key, an account name, a session ref). Each bucket refills at <c>ratePerSecond</c> up to <c>burst</c>;
/// <see cref="TryTake"/> spends tokens or says how long to wait. Memory is bounded: past <c>maxKeys</c> the full (idle) buckets go first, then the
/// least recently used. Thread-safe (one lock; every operation is O(1) amortized apart from the occasional prune). Time comes from the
/// <see cref="TimeProvider"/>, so tests control it.
/// </summary>
internal sealed class KeyedTokenBuckets(double ratePerSecond, double burst, TimeProvider time, int maxKeys = 50_000)
{
    private sealed class Bucket
    {
        public double Tokens;
        public long Stamp;
    }

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly double _rate = ratePerSecond > 0 ? ratePerSecond : throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
    private readonly double _burst = burst >= 1 ? burst : throw new ArgumentOutOfRangeException(nameof(burst));

    /// <summary>Buckets held now (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>Spends <paramref name="cost"/> tokens of <paramref name="key"/>'s bucket; false (and the wait until it would succeed) when it is empty.</summary>
    public bool TryTake(string key, out TimeSpan retryAfter, double cost = 1)
    {
        long now = time.GetTimestamp();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                if (_buckets.Count >= maxKeys)
                {
                    Prune(now);
                }

                bucket = new Bucket { Tokens = _burst, Stamp = now };
                _buckets[key] = bucket;
            }
            else
            {
                double elapsed = (now - bucket.Stamp) / (double)time.TimestampFrequency;
                bucket.Tokens = Math.Min(_burst, bucket.Tokens + (elapsed * _rate));
                bucket.Stamp = now;
            }

            if (bucket.Tokens >= cost)
            {
                bucket.Tokens -= cost;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((cost - bucket.Tokens) / _rate);
            return false;
        }
    }

    /// <summary>Spends one token; the wait is not needed.</summary>
    public bool TryTake(string key) => TryTake(key, out _);

    /// <summary>Forgets a key (a closed socket's bucket).</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            _buckets.Remove(key);
        }
    }

    private void Prune(long now)
    {
        List<string> idle = [.. _buckets.Where(p => p.Value.Tokens + ((now - p.Value.Stamp) / (double)time.TimestampFrequency * _rate) >= _burst).Select(p => p.Key)];
        foreach (string key in idle)
        {
            _buckets.Remove(key);
        }

        if (_buckets.Count >= maxKeys)
        {
            foreach (string key in _buckets.OrderBy(p => p.Value.Stamp).Take(Math.Max(1, maxKeys / 10)).Select(p => p.Key).ToList())
            {
                _buckets.Remove(key);
            }
        }
    }
}

/// <summary>
/// The abuse limits of the public listener (design 0.8, 0.4 step 6), one object so every limit uses the same clock. All per-address keys are
/// <see cref="PublicAddresses.Key"/> (IPv6 by /64).
/// </summary>
internal sealed class PublicRateLimits
{
    private readonly KeyedTokenBuckets _http;
    private readonly KeyedTokenBuckets _loginIp;
    private readonly KeyedTokenBuckets _loginAccount;
    private readonly KeyedTokenBuckets _socketIn;
    private readonly KeyedTokenBuckets _bytes;
    private readonly KeyedTokenBuckets _worldSession;
    private readonly KeyedTokenBuckets _worldIp;
    private readonly KeyedTokenBuckets _worldGlobal;
    private readonly KeyedTokenBuckets _infoSession;

    public PublicRateLimits(PublicOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = new KeyedTokenBuckets(20, 40, time);
        _loginIp = new KeyedTokenBuckets(options.Login.PerIpPerMinute / 60.0, options.Login.PerIpPerMinute, time);
        _loginAccount = new KeyedTokenBuckets(options.Login.PerAccountPer15Min / 900.0, options.Login.PerAccountPer15Min, time);
        _socketIn = new KeyedTokenBuckets(10, 10, time);
        _bytes = new KeyedTokenBuckets(options.Guest.MaxBytesPerSecond, options.Guest.MaxBytesPerSecond, time);
        _worldSession = new KeyedTokenBuckets(5, 5, time);
        _worldIp = new KeyedTokenBuckets(5, 5, time);
        _worldGlobal = new KeyedTokenBuckets(options.Guest.WorldCallsPerSecond, options.Guest.WorldCallsPerSecond, time, maxKeys: 4);
        _infoSession = new KeyedTokenBuckets(2, 2, time);
    }

    /// <summary>One HTTP request from an address (20 per second, burst 40).</summary>
    public bool AllowHttp(string ipKey) => _http.TryTake(ipKey);

    /// <summary>A login attempt: the address bucket and the upper-cased name bucket must both have a token; the larger wait is returned.</summary>
    public bool AllowLogin(string ipKey, string account, out TimeSpan retryAfter)
    {
        bool ip = _loginIp.TryTake(ipKey, out TimeSpan ipWait);
        bool name = _loginAccount.TryTake(account.Trim().ToUpperInvariant(), out TimeSpan nameWait);
        retryAfter = ipWait > nameWait ? ipWait : nameWait;
        return ip && name;
    }

    /// <summary>One message in on a socket (10 per second).</summary>
    public bool AllowSocketMessage(string socketKey) => _socketIn.TryTake(socketKey);

    /// <summary>Forgets a closed socket's message bucket.</summary>
    public void ForgetSocket(string socketKey) => _socketIn.Remove(socketKey);

    /// <summary>Spends <paramref name="bytes"/> of the address's byte budget (<c>Guest:MaxBytesPerSecond</c>, one second of burst); false means drop the frame.</summary>
    public bool AllowBytes(string ipKey, int bytes) => _bytes.TryTake(ipKey, out _, bytes);

    /// <summary>
    /// A call that touches the world (<c>live.find</c>, <c>pick.public</c>): per session and per address 5 per second and globally
    /// <c>Guest:WorldCallsPerSecond</c>; the strictest wins. All three buckets are charged even when one refuses, so a refused caller does not get free retries.
    /// </summary>
    public bool AllowWorldCall(string sessionRef, string ipKey)
    {
        bool session = _worldSession.TryTake(sessionRef);
        bool ip = _worldIp.TryTake(ipKey);
        bool global = _worldGlobal.TryTake("*");
        return session && ip && global;
    }

    /// <summary><c>info.public</c> asks: 2 per second per session on top of <see cref="AllowWorldCall"/>.</summary>
    public bool AllowInfo(string sessionRef) => _infoSession.TryTake(sessionRef);
}
