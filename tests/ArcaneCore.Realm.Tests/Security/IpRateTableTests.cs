using System.Net;
using ArcaneCore.Kernel.Net;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// The bounded per-address token-bucket table: budgets, refill, idle eviction, a full table that
/// admits newcomers by forgetting (never refusing), key normalisation and the allocation-free fast path.
/// </summary>
public sealed class IpRateTableTests
{
    private static IpKey Ip(string text) => IpKey.From(IPAddress.Parse(text));

    private sealed class FakeClock
    {
        private long _now;

        public long Read() => Volatile.Read(ref _now);

        public void Advance(long ms) => Interlocked.Add(ref _now, ms);

        public void Advance(TimeSpan span) => Advance((long)span.TotalMilliseconds);
    }

    /// <summary>Connections: 3 then one per second. Failures: 2 then one per ten seconds.</summary>
    private static (IpRateTable Table, FakeClock Clock) Table(int capacity = 64, TimeSpan? idle = null)
    {
        var clock = new FakeClock();
        var table = new IpRateTable(capacity, idle ?? TimeSpan.FromMinutes(10), clock.Read);
        table.Configure(RateBucket.Connections, burst: 3, perMinute: 60);
        table.Configure(RateBucket.AuthFailures, burst: 2, perMinute: 6);
        return (table, clock);
    }

    [Fact]
    public void Burst_ThenLimited_ThenRefilledAtTheConfiguredRate()
    {
        (IpRateTable table, FakeClock clock) = Table();
        IpKey ip = Ip("10.0.0.1");

        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections));
        Assert.Equal(RateVerdict.Limited, table.TryTake(ip, RateBucket.Connections));
        Assert.Equal(1, table.Count);

        clock.Advance(999);
        Assert.Equal(RateVerdict.Limited, table.TryTake(ip, RateBucket.Connections));
        clock.Advance(1);
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections));
        Assert.Equal(RateVerdict.Limited, table.TryTake(ip, RateBucket.Connections));

        // A long pause refills to the burst and no further.
        clock.Advance(60_000);
        Assert.Equal(3.0, table.TokensOf(ip, RateBucket.Connections));
    }

    [Fact]
    public void TheTwoBuckets_AreIndependent_AndRefillTogether()
    {
        (IpRateTable table, FakeClock clock) = Table();
        IpKey ip = Ip("10.0.0.2");

        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.AuthFailures));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.AuthFailures));
        Assert.Equal(RateVerdict.Limited, table.TryTake(ip, RateBucket.AuthFailures));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections)); // untouched by the other bucket

        clock.Advance(10_000); // one failure token back, and the connection bucket is full again
        Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.AuthFailures));
        Assert.Equal(RateVerdict.Limited, table.TryTake(ip, RateBucket.AuthFailures));
        Assert.Equal(3.0, table.TokensOf(ip, RateBucket.Connections));
    }

    [Fact]
    public void Peek_ReportsTheVerdict_WithoutConsuming()
    {
        (IpRateTable table, _) = Table();
        IpKey ip = Ip("10.0.0.3");
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(RateVerdict.Allowed, table.Peek(ip, RateBucket.AuthFailures));
        }

        Assert.Equal(2.0, table.TokensOf(ip, RateBucket.AuthFailures));
        table.TryTake(ip, RateBucket.AuthFailures);
        table.TryTake(ip, RateBucket.AuthFailures);
        Assert.Equal(RateVerdict.Limited, table.Peek(ip, RateBucket.AuthFailures));
    }

    [Fact]
    public void DisabledBucket_AllowsEverything_AndTracksNothing()
    {
        var table = new IpRateTable(16, TimeSpan.FromMinutes(1));
        IpKey ip = Ip("10.0.0.4");
        Assert.False(table.IsEnabled(RateBucket.Connections));
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(RateVerdict.Allowed, table.TryTake(ip, RateBucket.Connections));
        }

        Assert.Equal(0, table.Count);
    }

    /// <summary>
    /// Insert distinct addresses until the table holds exactly its capacity, through free slots only:
    /// a candidate whose probe window happens to be full already would force an eviction, and the
    /// tests here need to know exactly who is tracked, so that is asserted not to happen.
    /// </summary>
    private static List<IpKey> Fill(IpRateTable table, string prefix, IEnumerable<IpKey>? alreadyTracked = null)
    {
        var tracked = new List<IpKey>();
        long forced = table.ForcedEvictions;
        foreach (IPAddress address in TableFiller.FreeSlotAddresses(table.Capacity, prefix, alreadyTracked))
        {
            IpKey key = IpKey.From(address);
            int before = table.Count;
            Assert.Equal(RateVerdict.Allowed, table.TryTake(key, RateBucket.Connections));
            Assert.Equal(before + 1, table.Count);
            tracked.Add(key);
        }

        Assert.Equal(table.Capacity, table.Count);
        Assert.Equal(forced, table.ForcedEvictions);
        return tracked;
    }

    /// <summary>
    /// The regression for the lockout lever: a full probe window (every slot busy within the idle
    /// window) must still admit a newcomer. The table forgets its least recently seen address and
    /// counts a forced eviction; it never refuses. Before the fix the newcomer was refused
    /// (<c>Saturated</c>) until a slot idled out, ten minutes later by default.
    /// </summary>
    [Fact]
    public void FullTable_AdmitsANewcomer_ByForgettingTheLeastRecentlySeenAddress()
    {
        (IpRateTable table, FakeClock clock) = Table(capacity: 16, idle: TimeSpan.FromMinutes(5));
        Assert.Equal(16, table.Capacity);
        List<IpKey> tracked = Fill(table, "10.1");
        Assert.Equal(16, tracked.Count);
        foreach (IpKey key in tracked)
        {
            // Every tracked address is being limited (one token taken, none refilled: the clock did not move).
            Assert.Equal(2.0, table.TokensOf(key, RateBucket.Connections));
        }

        // No slot is free or idle, every slot carries limiting state: the newcomer is admitted anyway,
        // one address is forgotten, nothing is refused, the table stays bounded.
        IpKey newcomer = Ip("10.2.0.1");
        long idleEvictions = table.Evictions;
        Assert.Equal(RateVerdict.Allowed, table.TryTake(newcomer, RateBucket.Connections));
        Assert.Equal(1, table.ForcedEvictions);
        Assert.Equal(idleEvictions, table.Evictions);
        Assert.Equal(16, table.Count);
        Assert.Equal(2.0, table.TokensOf(newcomer, RateBucket.Connections));
        Assert.Equal(15, tracked.Count(key => table.TokensOf(key, RateBucket.Connections) == 2.0)); // exactly one forgotten

        // Its own slot from now on: no further eviction for the same address, on either bucket.
        Assert.Equal(RateVerdict.Allowed, table.TryTake(newcomer, RateBucket.Connections));
        Assert.Equal(RateVerdict.Allowed, table.Peek(newcomer, RateBucket.AuthFailures));
        Assert.Equal(1, table.ForcedEvictions);

        // A second newcomer whose window is also full: admitted the same way (an idle slot still wins once one exists).
        IpKey second = Ip("10.2.0.2");
        Assert.Equal(RateVerdict.Allowed, table.Peek(second, RateBucket.AuthFailures));
        Assert.Equal(2, table.ForcedEvictions);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(Ip("10.2.0.3"), RateBucket.Connections));
        Assert.Equal(2, table.ForcedEvictions);
        Assert.Equal(idleEvictions + 1, table.Evictions);
    }

    /// <summary>
    /// Which address a full window forgets: one that carries no limiting state (both buckets full
    /// after refill) before the least recently seen one, so a limited address keeps its budget as
    /// long as any address in the window is not being limited.
    /// </summary>
    [Fact]
    public void FullTable_PrefersToForgetAnAddressThatIsNotBeingLimited_OverTheLeastRecentlySeen()
    {
        (IpRateTable table, FakeClock clock) = Table(capacity: 16, idle: TimeSpan.FromMinutes(5));

        // The limited address goes in first (into an empty table its slot is the start of its own window)
        // and spends its whole burst at t = 0: the least recently seen entry of the table from now on.
        IpKey limited = Ip("10.4.0.1");
        int limitedSlot = limited.Mix() & (table.Capacity - 1);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RateVerdict.Allowed, table.TryTake(limited, RateBucket.Connections));
        }

        Assert.Equal(RateVerdict.Limited, table.TryTake(limited, RateBucket.Connections));

        clock.Advance(10);
        List<IpKey> others = Fill(table, "10.5", [limited]);
        Assert.Equal(15, others.Count);

        // Two seconds later the others have refilled to the full burst (no limiting state); the limited
        // one has 2 of 3 back and is still being limited.
        clock.Advance(1990);
        Assert.Equal(2.0, table.TokensOf(limited, RateBucket.Connections));

        // A newcomer whose probe window starts at the limited address's slot: the LRU rule alone would forget
        // the limited address; the table forgets one of the unlimited ones instead.
        IpKey newcomer = default;
        bool found = false;
        for (int i = 1; i < 4096 && !found; i++)
        {
            IpKey candidate = Ip($"10.6.{i / 256}.{i % 256}");
            if ((candidate.Mix() & (table.Capacity - 1)) == limitedSlot)
            {
                newcomer = candidate;
                found = true;
            }
        }

        Assert.True(found, "no candidate address hashes to the limited address's window");
        Assert.Equal(RateVerdict.Allowed, table.TryTake(newcomer, RateBucket.Connections));
        Assert.Equal(1, table.ForcedEvictions);
        Assert.Equal(2.0, table.TokensOf(limited, RateBucket.Connections)); // still tracked, still limited
        Assert.Equal(RateVerdict.Allowed, table.TryTake(limited, RateBucket.Connections));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(limited, RateBucket.Connections));
        Assert.Equal(RateVerdict.Limited, table.TryTake(limited, RateBucket.Connections));

        // Once every address in the window is being limited, the least recently seen one goes.
        (IpRateTable all, _) = Table(capacity: 16, idle: TimeSpan.FromMinutes(5));
        foreach (IpKey key in Fill(all, "10.7"))
        {
            all.TryTake(key, RateBucket.Connections);
            all.TryTake(key, RateBucket.Connections);
            Assert.Equal(RateVerdict.Limited, all.TryTake(key, RateBucket.Connections));
        }

        Assert.Equal(RateVerdict.Allowed, all.TryTake(Ip("10.8.0.1"), RateBucket.Connections));
        Assert.Equal(1, all.ForcedEvictions);
    }

    [Fact]
    public void Ipv4AndItsMappedIpv6Form_AreOneKey_AndKeysHashWell()
    {
        Assert.Equal(Ip("192.168.1.9"), IpKey.From(IPAddress.Parse("192.168.1.9").MapToIPv6()));
        Assert.NotEqual(Ip("192.168.1.9"), Ip("192.168.1.10"));
        Assert.NotEqual(Ip("::1"), Ip("127.0.0.1"));
        Assert.True(IpKey.TryParse("10.0.0.1:3724", out IpKey fromEndpoint));
        Assert.Equal(Ip("10.0.0.1"), fromEndpoint);
        Assert.True(IpKey.TryParse("[fe80::1]:8085", out IpKey v6));
        Assert.Equal(Ip("fe80::1"), v6);
        Assert.False(IpKey.TryParse("test", out _));
        Assert.False(IpKey.TryParse(string.Empty, out _));

        // Sequential IPv4 addresses must not collide into the same few probe windows.
        var seen = new HashSet<int>();
        for (int i = 0; i < 4096; i++)
        {
            seen.Add(Ip($"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}").Mix() & 4095);
        }

        Assert.True(seen.Count > 2500, $"only {seen.Count} distinct buckets of 4096 for 4096 sequential addresses");
    }

    [Fact]
    public void Take_Peek_AndEvict_DoNotAllocate()
    {
        (IpRateTable table, FakeClock clock) = Table(capacity: 64);
        IpKey[] keys = [.. Enumerable.Range(1, 200).Select(i => Ip($"10.9.{i / 256}.{i % 256}"))];
        foreach (IpKey key in keys)
        {
            Assert.Equal(RateVerdict.Allowed, table.TryTake(key, RateBucket.Connections)); // never refused for lack of a slot
        }

        Assert.Equal(table.Capacity, table.Count);
        Assert.True(table.ForcedEvictions > 0, $"200 addresses must not fit {table.Capacity} slots without forgetting some (count {table.Count})");
        long forcedAfterFill = table.ForcedEvictions;

        // Walk the array backwards so the most recently inserted addresses probe first and find every
        // slot idle (a busy address refreshed in the same round is never evicted); the rest of each
        // round runs the forced path over a table that is full again. Both allocate nothing.
        Array.Reverse(keys);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 50; round++)
        {
            clock.Advance(TimeSpan.FromMinutes(11)); // every slot idles out: inserts evict
            foreach (IpKey key in keys)
            {
                table.TryTake(key, RateBucket.Connections);
                table.Peek(key, RateBucket.AuthFailures);
                table.TryTake(key, RateBucket.AuthFailures);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(table.Evictions > 0, $"no idle evictions: count {table.Count}, capacity {table.Capacity}");
        Assert.True(table.ForcedEvictions > forcedAfterFill, $"no forced evictions in the rounds: count {table.Count}, capacity {table.Capacity}");
    }

    [Fact]
    public void FromAddress_DoesNotAllocate()
    {
        var address = IPAddress.Parse("203.0.113.7");
        IpKey.From(address);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            IpKey.From(address);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void BadConstruction_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpRateTable(16, TimeSpan.Zero));
        var table = new IpRateTable(16, TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Configure(RateBucket.Connections, -1, 1));
    }
}
