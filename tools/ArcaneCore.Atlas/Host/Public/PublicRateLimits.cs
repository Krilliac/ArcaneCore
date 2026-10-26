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
    public bool TryTake(string key, out TimeSpan retryAfter, double cost = 1) => TryTake(key, cost, 0, out retryAfter);

    /// <summary>
    /// Spends <paramref name="cost"/> tokens only if the balance stays at or above <paramref name="floor"/> afterwards (a reserve other callers keep);
    /// otherwise spends nothing and says how long until it would succeed.
    /// </summary>
    public bool TryTake(string key, double cost, double floor, out TimeSpan retryAfter)
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

            if (bucket.Tokens - cost >= floor)
            {
                bucket.Tokens -= cost;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((cost + floor - bucket.Tokens) / _rate);
            return false;
        }
    }

    /// <summary>The tokens <paramref name="key"/> holds now (negative while in debt after <see cref="Spend"/>); a key never seen holds a full burst. Does not change the bucket.</summary>
    public double Balance(string key)
    {
        long now = time.GetTimestamp();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                return _burst;
            }

            double elapsed = (now - bucket.Stamp) / (double)time.TimestampFrequency;
            return Math.Min(_burst, bucket.Tokens + (elapsed * _rate));
        }
    }

    /// <summary>
    /// Spends <paramref name="cost"/> tokens even when the bucket holds fewer (the balance goes negative, at most one burst into debt, and refills with
    /// time): for small messages that must be sent, such as a status note, but still count against the budget.
    /// </summary>
    public void Spend(string key, double cost)
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

            bucket.Tokens = Math.Max(-_burst, bucket.Tokens - cost);
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

/// <summary>What the socket's message limits say about one inbound message.</summary>
internal enum SocketAdmission
{
    /// <summary>Within 10 per second.</summary>
    Allowed,

    /// <summary>Over the rate: answer with a (coalesced) error and do not process it.</summary>
    Refused,

    /// <summary>So far over the rate that the socket is closed.</summary>
    Flood,
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
    private readonly KeyedTokenBuckets _socketFlood;
    private readonly KeyedTokenBuckets _socketErrors;
    private readonly double _frameReserve;
    private readonly double _byteRate;
    private long _oversizedEssential;

    /// <summary>The largest message that is charged as essential (and so never refused for bytes).</summary>
    public const int MaxEssentialBytes = 2048;

    public PublicRateLimits(PublicOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _byteRate = options.Guest.MaxBytesPerSecond;
        _frameReserve = Math.Max(1024, options.Guest.MaxBytesPerSecond * 0.25);
        _socketFlood = new KeyedTokenBuckets(10, 100, time);
        _socketErrors = new KeyedTokenBuckets(2, 2, time);
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

    /// <summary>
    /// One message in on a socket: <see cref="SocketAdmission.Allowed"/> within 10 per second; past that <see cref="SocketAdmission.Refused"/> for up to
    /// 100 more messages (burst, then 10 per second); past that <see cref="SocketAdmission.Flood"/>, which closes the socket.
    /// </summary>
    public SocketAdmission AdmitSocketMessage(string socketKey)
    {
        if (_socketIn.TryTake(socketKey))
        {
            return SocketAdmission.Allowed;
        }

        return _socketFlood.TryTake(socketKey) ? SocketAdmission.Refused : SocketAdmission.Flood;
    }

    /// <summary>Whether this socket may be sent another error reply (2 per second); replies past that are suppressed.</summary>
    public bool AllowErrorReply(string socketKey) => _socketErrors.TryTake(socketKey);

    /// <summary>Forgets a closed socket's buckets.</summary>
    public void ForgetSocket(string socketKey)
    {
        _socketIn.Remove(socketKey);
        _socketFlood.Remove(socketKey);
        _socketErrors.Remove(socketKey);
    }

    /// <summary>Spends <paramref name="bytes"/> of the address's byte budget (<c>Guest:MaxBytesPerSecond</c>, one second of burst); false means drop the frame.</summary>
    public bool AllowBytes(string ipKey, int bytes) => _bytes.TryTake(ipKey, out _, bytes);

    /// <summary>A live frame: like <see cref="AllowBytes"/> but it never takes the budget below a reserve (a quarter of it, at least 1 KiB), so interactive answers still fit between frames.</summary>
    public bool AllowFrameBytes(string ipKey, int bytes) => _bytes.TryTake(ipKey, bytes, _frameReserve, out _);

    /// <summary>
    /// Charges an essential message (session routes, error bodies, hello, status notes, control messages) to the address's byte budget without ever
    /// refusing it; the budget can go into debt by up to one second's worth, so what follows is throttled instead. Each such message is at most
    /// <see cref="MaxEssentialBytes"/> and sits behind its own rate limit, which bounds the total. The full length is always charged; false means the
    /// message was over <see cref="MaxEssentialBytes"/> (counted in <see cref="OversizedEssential"/>) and the caller must not send it as built.
    /// </summary>
    public bool ChargeEssentialBytes(string ipKey, int bytes)
    {
        _bytes.Spend(ipKey, bytes); // the full length, whatever it is: an oversized message is paid for, not rounded down
        if (bytes <= MaxEssentialBytes)
        {
            return true;
        }

        Interlocked.Increment(ref _oversizedEssential);
        return false;
    }

    /// <summary>How many "essential" messages exceeded <see cref="MaxEssentialBytes"/> (each was charged in full and not sent as built); a non-zero value is a bug.</summary>
    public long OversizedEssential => Interlocked.Read(ref _oversizedEssential);

    /// <summary>The address's byte balance now (negative in debt); for tests.</summary>
    internal double ByteBalance(string ipKey) => _bytes.Balance(ipKey);

    /// <summary>True while the address's byte budget is in debt (an essential message was charged beyond what it held); a command must not run then.</summary>
    public bool InDebt(string ipKey, out TimeSpan retryAfter)
    {
        double balance = _bytes.Balance(ipKey);
        if (balance < 0)
        {
            retryAfter = TimeSpan.FromSeconds(-balance / _byteRate);
            return true;
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// A call that touches the world (<c>live.find</c>, <c>pick.public</c>): per session and per address 5 per second and globally
    /// <c>Guest:WorldCallsPerSecond</c>; the strictest wins. The session and address buckets are both charged (a refused caller gets no free retries there), but the
    /// global bucket is charged only for a call the other two let through: one flooding address must not drain the global budget and starve every other guest.
    /// </summary>
    public bool AllowWorldCall(string sessionRef, string ipKey)
    {
        bool session = _worldSession.TryTake(sessionRef);
        bool ip = _worldIp.TryTake(ipKey);
        return session && ip && _worldGlobal.TryTake("*");
    }

    /// <summary><c>info.public</c> asks: 2 per second per session on top of <see cref="AllowWorldCall"/>.</summary>
    public bool AllowInfo(string sessionRef) => _infoSession.TryTake(sessionRef);
}
