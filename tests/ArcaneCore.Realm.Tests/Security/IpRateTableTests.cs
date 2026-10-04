using System.Net;
using ArcaneCore.Kernel.Net;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// The bounded per-address token-bucket table: budgets, refill, idle eviction, saturation (fail
/// closed), key normalisation and the allocation-free fast path.
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

    /// <summary>Insert distinct addresses until the table holds exactly its capacity.</summary>
    private static List<IpKey> Fill(IpRateTable table, string prefix)
    {
        var tracked = new List<IpKey>();
        for (int i = 1; table.Count < table.Capacity; i++)
        {
            Assert.True(i < 100_000, "could not fill the table");
            IpKey key = Ip($"{prefix}.{i / 256}.{i % 256}");
            if (table.TryTake(key, RateBucket.Connections) == RateVerdict.Allowed)
            {
                tracked.Add(key);
            }
        }

        return tracked;
    }

    [Fact]
    public void FullTable_RefusesNewcomers_UntilAnEntryIdlesOut_ThenEvictsIt()
    {
        (IpRateTable table, FakeClock clock) = Table(capacity: 16, idle: TimeSpan.FromMinutes(5));
        Assert.Equal(16, table.Capacity);
        List<IpKey> tracked = Fill(table, "10.1");
        Assert.Equal(16, tracked.Count);

        // No slot is idle: a newcomer is refused, fail closed, and nobody tracked is forgotten.
        IpKey newcomer = Ip("10.2.0.1");
        long evictions = table.Evictions;
        Assert.Equal(RateVerdict.Saturated, table.TryTake(newcomer, RateBucket.Connections));
        Assert.Equal(RateVerdict.Saturated, table.Peek(newcomer, RateBucket.AuthFailures));
        Assert.Equal(evictions, table.Evictions);
        foreach (IpKey key in tracked)
        {
            Assert.Equal(2.0, table.TokensOf(key, RateBucket.Connections));
        }

        // A limited address stays limited after the refusal (its budget was not reset by the newcomer).
        IpKey limited = tracked[0];
        table.TryTake(limited, RateBucket.Connections);
        table.TryTake(limited, RateBucket.Connections);
        Assert.Equal(RateVerdict.Limited, table.TryTake(limited, RateBucket.Connections));

        // Once the others idle out (the limited one was touched just now), a newcomer takes an idle slot.
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RateVerdict.Allowed, table.TryTake(newcomer, RateBucket.Connections));
        Assert.Equal(evictions + 1, table.Evictions);
        Assert.Equal(16, table.Count);
        Assert.Equal(RateVerdict.Allowed, table.TryTake(newcomer, RateBucket.Connections)); // its own slot now
        Assert.Equal(evictions + 1, table.Evictions);
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
        int tracked = 0;
        int saturated = 0;
        foreach (IpKey key in keys)
        {
            if (table.TryTake(key, RateBucket.Connections) == RateVerdict.Saturated)
            {
                saturated++;
            }
            else
            {
                tracked++;
            }
        }

        Assert.True(saturated > 0, $"200 addresses must not fit {table.Capacity} slots (tracked {tracked}, count {table.Count})");

        // The addresses that were refused come last in the array; walk it backwards so they probe
        // first and find every slot idle (a busy address refreshed in the same round is never evicted).
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
        Assert.True(table.Evictions > 0, $"no evictions: count {table.Count}, capacity {table.Capacity}, tracked {tracked}, saturated {saturated}");
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
