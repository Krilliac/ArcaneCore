namespace ArcaneCore.Kernel.Net;

/// <summary>What <see cref="OpcodeRateLimiter.OnPacket"/> decided for one packet.</summary>
public enum OpcodeRateVerdict
{
    /// <summary>Within every budget: handle the packet.</summary>
    Allowed,

    /// <summary>The opcode's bucket is empty or the connection is over its per-second cap: drop this packet, keep the connection.</summary>
    Dropped,

    /// <summary>The connection sent more packets in one second than the flood cap: close it.</summary>
    Flood,
}

/// <summary>
/// The per-connection packet budgets of a world session (docs/ops/netguard.md, Net:Protection:World*), ported from the
/// gateway <c>RateLimiter</c> of the MaNGOS Zero anticheat fork (src/gateway/EdgeChecks.h): one token bucket per opcode
/// (capacity <see cref="Burst"/>, refilled at <see cref="RefillPerSecond"/>), plus a counter over a one-second window that
/// drops packets above <see cref="PacketsPerSecond"/> and reports a flood above <see cref="FloodPacketsPerSecond"/>.
/// <para>
/// Pure and deterministic: the caller passes the receive time in milliseconds (monotonic), so tests drive it with a
/// synthetic clock. Not thread-safe: one instance belongs to one connection's read loop. Buckets are created on first use
/// and there are at most 65536 opcodes, so the memory of one connection is bounded. A 0 disables the matching budget.
/// </para>
/// </summary>
public sealed class OpcodeRateLimiter
{
    private readonly Dictionary<ushort, Bucket> _buckets = [];
    private long _windowStartMs;
    private int _windowCount;
    private bool _primed;

    /// <param name="burst">Tokens per opcode bucket (its capacity); 0 disables the per-opcode buckets.</param>
    /// <param name="refillPerSecond">Tokens an opcode bucket regains per second.</param>
    /// <param name="packetsPerSecond">Packets of any opcode per one-second window before the rest of the window is dropped; 0 disables.</param>
    /// <param name="floodPacketsPerSecond">Packets per one-second window that end the connection; 0 disables.</param>
    public OpcodeRateLimiter(int burst, double refillPerSecond, int packetsPerSecond, int floodPacketsPerSecond)
    {
        Burst = Math.Max(0, burst);
        RefillPerSecond = Math.Max(0, refillPerSecond);
        PacketsPerSecond = Math.Max(0, packetsPerSecond);
        FloodPacketsPerSecond = Math.Max(0, floodPacketsPerSecond);
    }

    public int Burst { get; }

    public double RefillPerSecond { get; }

    public int PacketsPerSecond { get; }

    public int FloodPacketsPerSecond { get; }

    /// <summary>Whether any budget is on (a limiter with every budget at 0 allows everything).</summary>
    public bool IsEnabled => Burst > 0 || PacketsPerSecond > 0 || FloodPacketsPerSecond > 0;

    /// <summary>Opcodes with a bucket so far (diagnostics and tests).</summary>
    public int TrackedOpcodes => _buckets.Count;

    /// <summary>Account for one packet of <paramref name="opcode"/> received at <paramref name="nowMs"/>.</summary>
    public OpcodeRateVerdict OnPacket(ushort opcode, long nowMs)
    {
        if (!_primed || nowMs - _windowStartMs >= 1000 || nowMs < _windowStartMs)
        {
            _windowStartMs = nowMs;
            _windowCount = 0;
            _primed = true;
        }

        _windowCount++;
        if (FloodPacketsPerSecond > 0 && _windowCount > FloodPacketsPerSecond)
        {
            return OpcodeRateVerdict.Flood;
        }

        bool overGlobal = PacketsPerSecond > 0 && _windowCount > PacketsPerSecond;
        bool bucketOk = true;
        if (Burst > 0)
        {
            if (!_buckets.TryGetValue(opcode, out Bucket? bucket))
            {
                bucket = new Bucket(Burst, nowMs);
                _buckets[opcode] = bucket;
            }

            bucketOk = bucket.TryTake(nowMs, Burst, RefillPerSecond);
        }

        return overGlobal || !bucketOk ? OpcodeRateVerdict.Dropped : OpcodeRateVerdict.Allowed;
    }

    /// <summary>A classic token bucket, refilled lazily from the supplied clock (the fork's <c>TokenBucket</c>).</summary>
    private sealed class Bucket(int capacity, long nowMs)
    {
        private double _tokens = capacity;
        private long _lastMs = nowMs;

        public bool TryTake(long nowMs, int capacity, double refillPerSecond)
        {
            if (nowMs > _lastMs)
            {
                _tokens = Math.Min(capacity, _tokens + ((nowMs - _lastMs) / 1000.0 * refillPerSecond));
                _lastMs = nowMs;
            }

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return true;
            }

            return false;
        }
    }
}
