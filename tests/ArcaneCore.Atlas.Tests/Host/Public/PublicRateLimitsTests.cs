using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The abuse limits of design 0.8 and 0.4 step 6, on a clock the test moves.</summary>
public sealed class PublicRateLimitsTests
{
    [Fact]
    public void ABucket_RefillsWithTime_AndSaysHowLongToWait()
    {
        var time = new ManualTime();
        var buckets = new KeyedTokenBuckets(2, 4, time);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(buckets.TryTake("k"));
        }

        Assert.False(buckets.TryTake("k", out TimeSpan wait));
        Assert.Equal(TimeSpan.FromSeconds(0.5), wait);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(buckets.TryTake("k"));
        Assert.True(buckets.TryTake("k"));
        Assert.False(buckets.TryTake("k"));
        Assert.True(buckets.TryTake("other"));
    }

    [Fact]
    public void MemoryIsBounded()
    {
        var time = new ManualTime();
        var buckets = new KeyedTokenBuckets(1, 1, time, maxKeys: 100);
        for (int i = 0; i < 5000; i++)
        {
            buckets.TryTake("key" + i);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(buckets.Count <= 100, $"{buckets.Count} buckets");
    }

    [Fact]
    public void Http_IsTwentyPerSecondWithABurstOfForty_PerAddress()
    {
        var time = new ManualTime();
        var limits = new PublicRateLimits(new PublicOptions(), time);

        Assert.Equal(40, Enumerable.Range(0, 100).Count(_ => limits.AllowHttp("1.1.1.1")));
        Assert.True(limits.AllowHttp("2.2.2.2"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(20, Enumerable.Range(0, 100).Count(_ => limits.AllowHttp("1.1.1.1")));
    }

    [Fact]
    public void Login_IsLimitedPerAddress_AndPerUpperCasedName()
    {
        var time = new ManualTime();
        var options = new PublicOptions();
        var limits = new PublicRateLimits(options, time);

        for (int i = 0; i < options.Login.PerIpPerMinute; i++)
        {
            Assert.True(limits.AllowLogin("1.1.1.1", "name" + i, out _));
        }

        Assert.False(limits.AllowLogin("1.1.1.1", "another", out TimeSpan ipWait));
        Assert.True(ipWait > TimeSpan.Zero);

        for (int i = 0; i < options.Login.PerAccountPer15Min; i++)
        {
            Assert.True(limits.AllowLogin("9.9.9." + i, "Staff", out _));
        }

        Assert.False(limits.AllowLogin("7.7.7.7", "STAFF", out TimeSpan nameWait)); // same name, other case, new address
        Assert.True(nameWait > TimeSpan.FromSeconds(60));
        Assert.True(limits.AllowLogin("7.7.7.7", "someone-else", out _));
    }

    [Fact]
    public void Bytes_AreChargedPerAddress_AndRefillEachSecond()
    {
        var time = new ManualTime();
        var options = new PublicOptions { Guest = new PublicGuestOptions { MaxBytesPerSecond = 10_000 } };
        var limits = new PublicRateLimits(options, time);

        Assert.True(limits.AllowBytes("1.1.1.1", 6000));
        Assert.False(limits.AllowBytes("1.1.1.1", 6000));
        Assert.True(limits.AllowBytes("2.2.2.2", 6000));
        Assert.False(limits.AllowBytes("1.1.1.1", 20_000)); // a frame larger than a second's budget never passes
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limits.AllowBytes("1.1.1.1", 6000));
    }

    [Fact]
    public void WorldCalls_AreFivePerSecondPerSessionAndAddress_AndCappedGlobally()
    {
        var time = new ManualTime();
        var limits = new PublicRateLimits(new PublicOptions(), time);

        Assert.Equal(5, Enumerable.Range(0, 20).Count(_ => limits.AllowWorldCall("sessionA", "1.1.1.1")));
        Assert.False(limits.AllowWorldCall("sessionB", "1.1.1.1")); // another session, the same address

        var capped = new PublicRateLimits(new PublicOptions { Guest = new PublicGuestOptions { WorldCallsPerSecond = 8 } }, time);
        Assert.Equal(8, Enumerable.Range(0, 20).Count(i => capped.AllowWorldCall("s" + i, "10.0.0." + i)));
    }

    [Fact]
    public void SocketMessages_AreTenPerSecondPerSocket_AndInfoIsTwoPerSecondPerSession()
    {
        var time = new ManualTime();
        var limits = new PublicRateLimits(new PublicOptions(), time);

        Assert.Equal(10, Enumerable.Range(0, 50).Count(_ => limits.AllowSocketMessage("ws1")));
        Assert.True(limits.AllowSocketMessage("ws2"));
        Assert.Equal(2, Enumerable.Range(0, 50).Count(_ => limits.AllowInfo("sessionA")));
    }
}
