using System.Diagnostics;
using ArcaneCore.Kernel.Logging;

namespace ArcaneCore.Kernel.Net;

/// <summary>The outcome of asking the table for a token.</summary>
public enum RateVerdict
{
    /// <summary>A token was available (and, for a consuming call, taken).</summary>
    Allowed,

    /// <summary>The address has used its budget; the caller refuses the client.</summary>
    Limited,
}

/// <summary>Which of the two budgets an entry carries.</summary>
public enum RateBucket
{
    /// <summary>New connections.</summary>
    Connections = 0,

    /// <summary>Failed authentication attempts.</summary>
    AuthFailures = 1,
}

/// <summary>
/// A bounded table of per-address token buckets: a fixed array of slots (open addressing, linear
/// probing over a short window), each holding one <see cref="IpKey"/>, its last-seen time and two
/// token counts. Memory is allocated once in the constructor; a lookup, a take and an eviction
/// allocate nothing (the test suite pins that with GC.GetAllocatedBytesForCurrentThread).
/// <para>
/// Eviction: an address that has not been seen for the idle window gives its slot to a newcomer. If
/// every slot of a newcomer's probe window is busy and none is idle, the newcomer still gets a slot:
/// the table forgets the least recently seen address of the window, preferring one that carries no
/// limiting state (both buckets full after refill), and counts it in <see cref="ForcedEvictions"/>.
/// The table is a bounded measuring device that degrades to measuring less when it is full; it is
/// never the reason a connection is refused (the global connection cap is the fail-closed limit).
/// The cost is that an address can be forgotten while limited if its whole probe window is
/// crowded out by addresses seen more recently; see docs/ops/netguard.md.
/// </para>
/// <para>
/// Ownership and threading: one instance per listener, touched from the accept callback and from
/// session tasks, serialised by one lock (a few dozen nanoseconds; the callers are already on a
/// per-connection path). The clock is injectable and monotonic (<see cref="Clock.Milliseconds"/>).
/// </para>
/// </summary>
public sealed class IpRateTable
{
    /// <summary>Slots examined for one key before the table is called saturated for it.</summary>
    public const int ProbeWindow = 8;

    private readonly object _gate = new();
    private readonly Entry[] _slots;
    private readonly int _mask;
    private readonly long _idleMs;
    private readonly Func<long> _clock;
    private readonly Budget[] _budgets = new Budget[2];
    private int _count;
    private long _evictions;
    private long _forcedEvictions;

    /// <param name="capacity">Most addresses tracked; rounded up to a power of two (at least 16).</param>
    /// <param name="idleEviction">After this long without a touch a slot may be reused.</param>
    /// <param name="clock">Monotonic milliseconds; tests inject a fake.</param>
    public IpRateTable(int capacity, TimeSpan idleEviction, Func<long>? clock = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (idleEviction <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleEviction));
        }

        int size = (int)Math.Max(16, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity));
        _slots = new Entry[size];
        _mask = size - 1;
        _idleMs = (long)idleEviction.TotalMilliseconds;
        _clock = clock ?? Clock.Milliseconds;
    }

    /// <summary>Slots in the table (the rounded capacity).</summary>
    public int Capacity => _slots.Length;

    /// <summary>Addresses currently tracked.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Idle slots handed to a different address so far (diagnostics and tests).</summary>
    public long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>
    /// Busy (not idle) slots handed to a newcomer because its whole probe window was full: the table
    /// forgot an address to keep admitting. Diagnostics; <c>NetGuard</c> logs one rate-limited line per interval.
    /// </summary>
    public long ForcedEvictions => Interlocked.Read(ref _forcedEvictions);

    /// <summary>
    /// Set a bucket's token-bucket parameters: <paramref name="burst"/> tokens at most, refilled at
    /// <paramref name="perMinute"/>. A burst of 0 disables the bucket (every take is allowed and nothing is recorded).
    /// </summary>
    public void Configure(RateBucket bucket, int burst, int perMinute)
    {
        if (burst < 0 || perMinute < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burst));
        }

        lock (_gate)
        {
            _budgets[(int)bucket] = new Budget(burst, perMinute / 60000.0);
        }
    }

    /// <summary>True when <see cref="Configure"/> gave the bucket a non-zero burst.</summary>
    public bool IsEnabled(RateBucket bucket)
    {
        lock (_gate)
        {
            return _budgets[(int)bucket].Burst > 0;
        }
    }

    /// <summary>Take one token from the address's bucket, creating the entry if needed.</summary>
    public RateVerdict TryTake(in IpKey key, RateBucket bucket) => Touch(key, bucket, consume: true);

    /// <summary>
    /// Would a take succeed? Creates or refreshes the entry (so an address that keeps asking is
    /// never forgotten) but consumes nothing. Used before a costly step whose failure is what gets
    /// charged (<see cref="TryTake"/> afterwards).
    /// </summary>
    public RateVerdict Peek(in IpKey key, RateBucket bucket) => Touch(key, bucket, consume: false);

    /// <summary>Tokens left in the address's bucket, or the full burst for an unknown address (tests, diagnostics).</summary>
    public double TokensOf(in IpKey key, RateBucket bucket)
    {
        lock (_gate)
        {
            Budget budget = _budgets[(int)bucket];
            int index = Find(key);
            if (index < 0)
            {
                return budget.Burst;
            }

            ref Entry entry = ref _slots[index];
            return Math.Min(budget.Burst, entry.Tokens(bucket) + (_clock() - entry.LastSeenMs) * budget.PerMs);
        }
    }

    private RateVerdict Touch(in IpKey key, RateBucket bucket, bool consume)
    {
        lock (_gate)
        {
            Budget budget = _budgets[(int)bucket];
            if (budget.Burst == 0)
            {
                return RateVerdict.Allowed;
            }

            long now = _clock();
            int index = FindOrInsert(key, now);
            ref Entry entry = ref _slots[index];
            Debug.Assert(entry.Used && entry.Key == key, "FindOrInsert must hand back the slot of the key");

            // Refill both buckets from the time elapsed since the entry was last touched.
            long elapsed = now - entry.LastSeenMs;
            if (elapsed > 0)
            {
                Budget other = _budgets[1 - (int)bucket];
                entry.SetTokens(bucket, Math.Min(budget.Burst, entry.Tokens(bucket) + elapsed * budget.PerMs));
                entry.SetTokens((RateBucket)(1 - (int)bucket), Math.Min(other.Burst, entry.Tokens((RateBucket)(1 - (int)bucket)) + elapsed * other.PerMs));
                entry.LastSeenMs = now;
            }

            double tokens = entry.Tokens(bucket);
            if (tokens < 1.0)
            {
                return RateVerdict.Limited;
            }

            if (consume)
            {
                entry.SetTokens(bucket, tokens - 1.0);
            }

            return RateVerdict.Allowed;
        }
    }

    /// <summary>The slot holding <paramref name="key"/>, or -1. Caller holds the lock.</summary>
    private int Find(in IpKey key)
    {
        int start = key.Mix() & _mask;
        for (int i = 0; i < ProbeWindow; i++)
        {
            int index = (start + i) & _mask;
            ref Entry slot = ref _slots[index];
            if (slot.Used && slot.Key == key)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The slot of <paramref name="key"/>, inserting it into its probe window when absent: a free slot
    /// first, then the least recently seen idle slot, then (a full window) the least recently seen
    /// slot that carries no limiting state, then the least recently seen slot of all. Always returns
    /// a slot: a full table never refuses a newcomer. Caller holds the lock.
    /// </summary>
    private int FindOrInsert(in IpKey key, long now)
    {
        int start = key.Mix() & _mask;
        int free = -1;
        int idle = -1;
        long idleSeen = long.MaxValue;
        int unlimited = -1;
        long unlimitedSeen = long.MaxValue;
        int oldest = -1;
        long oldestSeen = long.MaxValue;
        for (int i = 0; i < ProbeWindow; i++)
        {
            int index = (start + i) & _mask;
            ref Entry slot = ref _slots[index];
            if (!slot.Used)
            {
                if (free < 0)
                {
                    free = index;
                }

                continue;
            }

            if (slot.Key == key)
            {
                return index;
            }

            if (free >= 0)
            {
                continue; // a free slot wins; no eviction candidate is needed
            }

            // Prefer the least recently seen idle slot so a near-full table rotates fairly.
            if (now - slot.LastSeenMs >= _idleMs)
            {
                if (slot.LastSeenMs < idleSeen)
                {
                    idle = index;
                    idleSeen = slot.LastSeenMs;
                }

                continue;
            }

            if (slot.LastSeenMs < oldestSeen)
            {
                oldest = index;
                oldestSeen = slot.LastSeenMs;
            }

            // A busy slot whose buckets are both full carries no limiting state: forgetting it loses nothing.
            if (slot.LastSeenMs < unlimitedSeen && !HasLimitingState(in slot, now))
            {
                unlimited = index;
                unlimitedSeen = slot.LastSeenMs;
            }
        }

        int target;
        if (free >= 0)
        {
            target = free;
            _count++;
        }
        else if (idle >= 0)
        {
            target = idle;
            Interlocked.Increment(ref _evictions);
        }
        else
        {
            // The window is full of addresses seen within the idle window: measure less rather than refuse.
            target = unlimited >= 0 ? unlimited : oldest;
            Debug.Assert(target >= 0, "a probe window of used slots has a least recently seen one");
            Interlocked.Increment(ref _forcedEvictions);
        }

        ref Entry entry = ref _slots[target];
        entry.Used = true;
        entry.Key = key;
        entry.LastSeenMs = now;
        entry.Tokens0 = _budgets[0].Burst;
        entry.Tokens1 = _budgets[1].Burst;
        return target;
    }

    /// <summary>True when either enabled bucket of <paramref name="entry"/> would still be below its burst after refilling to <paramref name="now"/>.</summary>
    private bool HasLimitingState(in Entry entry, long now)
    {
        long elapsed = now - entry.LastSeenMs;
        Budget connections = _budgets[0];
        if (connections.Burst > 0 && entry.Tokens0 + elapsed * connections.PerMs < connections.Burst)
        {
            return true;
        }

        Budget failures = _budgets[1];
        return failures.Burst > 0 && entry.Tokens1 + elapsed * failures.PerMs < failures.Burst;
    }

    private readonly record struct Budget(int Burst, double PerMs);

    /// <summary>One slot: 16 bytes of key, the last touch, two token counts and the used flag (48 bytes with padding).</summary>
    private struct Entry
    {
        public IpKey Key;
        public long LastSeenMs;
        public double Tokens0;
        public double Tokens1;
        public bool Used;

        public readonly double Tokens(RateBucket bucket) => bucket == RateBucket.Connections ? Tokens0 : Tokens1;

        public void SetTokens(RateBucket bucket, double value)
        {
            if (bucket == RateBucket.Connections)
            {
                Tokens0 = value;
            }
            else
            {
                Tokens1 = value;
            }
        }
    }
}
