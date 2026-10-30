using ArcaneCore.World.Ops.Accounts;
using ArcaneCore.World.Tests.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Accounts;

/// <summary>The web login's failure bookkeeping: per (account, network) lockout, the soft all-network ceiling, bounded memory.</summary>
public sealed class WebLoginLockoutTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.200", "203.0.113.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("", "unknown")]
    [InlineData("not an address", "unknown")]
    [InlineData(null, "unknown")]
    public void TheNetwork_IsTheIpv4Slash24_OrTheIpv6Slash64(string? ip, string expected)
        => Assert.Equal(expected, WebLoginLockout.NetworkOf(ip));

    [Fact]
    public void FiveFailuresFromOneNetwork_LockThatAccountForThatNetworkOnly()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time);
        for (int i = 0; i < WebLoginLockout.NetworkLimit - 1; i++)
        {
            lockout.RecordFailure("staff", "203.0.113.0/24");
        }

        Assert.False(lockout.IsLocked("STAFF", "203.0.113.0/24"));
        lockout.RecordFailure("Staff", "203.0.113.0/24"); // the name is keyed case-insensitively

        Assert.True(lockout.IsLocked("STAFF", "203.0.113.0/24"));
        Assert.False(lockout.IsLocked("STAFF", "198.51.100.0/24"));
        Assert.False(lockout.IsLocked("OTHER", "203.0.113.0/24"));
    }

    [Fact]
    public void ALockout_EndsWhenTheFailuresAgeOut()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time);
        for (int i = 0; i < WebLoginLockout.NetworkLimit; i++)
        {
            lockout.RecordFailure("STAFF", "n");
        }

        time.Advance(WebLoginLockout.Window - TimeSpan.FromSeconds(1));
        Assert.True(lockout.IsLocked("STAFF", "n"));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(lockout.IsLocked("STAFF", "n"));
    }

    [Fact]
    public void ASuccess_ClearsThatNetworksFailures()
    {
        var lockout = new WebLoginLockout(new FeedTestClock());
        for (int i = 0; i < 4; i++)
        {
            lockout.RecordFailure("STAFF", "n");
        }

        lockout.RecordSuccess("STAFF", "n");
        lockout.RecordFailure("STAFF", "n");

        Assert.False(lockout.IsLocked("STAFF", "n"));
    }

    [Fact]
    public void ThirtyFailuresFromAllNetworks_AddADelay_NotARefusal()
    {
        var lockout = new WebLoginLockout(new FeedTestClock());
        for (int i = 0; i < WebLoginLockout.GlobalSoftLimit - 1; i++)
        {
            lockout.RecordFailure("STAFF", "net" + i);
        }

        Assert.Equal(TimeSpan.Zero, lockout.DelayFor("STAFF"));
        lockout.RecordFailure("STAFF", "another");

        Assert.Equal(WebLoginLockout.SoftDelay, lockout.DelayFor("STAFF"));
        Assert.False(lockout.IsLocked("STAFF", "a-new-network"));
        Assert.Equal(TimeSpan.Zero, lockout.DelayFor("OTHER"));
    }

    [Fact]
    public void FloodingFreshNames_DoesNotEvictALockedAccount()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time, maxTracked: 64);
        for (int i = 0; i < WebLoginLockout.NetworkLimit; i++)
        {
            lockout.RecordFailure("staff", "attacker");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(lockout.IsLocked("staff", "attacker"));

        for (int i = 0; i < 1000; i++)
        {
            lockout.RecordFailure("invented" + i, "flood");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(lockout.IsLocked("staff", "attacker"), "a flood of fresh names reset a standing lockout");
        Assert.True(lockout.Tracked <= 64, $"{lockout.Tracked} keys tracked");
    }

    [Fact]
    public void FloodingFreshNames_DoesNotEraseAPartialCount()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time, maxTracked: 64);
        for (int i = 0; i < WebLoginLockout.NetworkLimit - 1; i++)
        {
            lockout.RecordFailure("staff", "attacker");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        for (int i = 0; i < 1000; i++)
        {
            lockout.RecordFailure("invented" + i, "flood");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        lockout.RecordFailure("staff", "attacker");

        Assert.True(lockout.IsLocked("staff", "attacker"), "the flood forgot the earlier failures");
    }

    [Fact]
    public void ATableFullOfLockedKeys_StillBoundsMemory_AndNewNamesAreDropped()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time, maxTracked: 16);
        for (int name = 0; name < 20; name++)
        {
            for (int i = 0; i < WebLoginLockout.NetworkLimit; i++)
            {
                lockout.RecordFailure("locked" + name, "net");
                time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }

        Assert.True(lockout.Tracked <= 16, $"{lockout.Tracked} keys tracked");
        Assert.True(lockout.IsLocked("locked0", "net"));
    }

    [Fact]
    public void MemoryIsBounded_ByDroppingTheOldestKeys()
    {
        var time = new FeedTestClock();
        var lockout = new WebLoginLockout(time, maxTracked: 64);
        for (int i = 0; i < 1000; i++)
        {
            lockout.RecordFailure("name" + i, "net");
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(lockout.Tracked <= 64, $"{lockout.Tracked} keys tracked");
    }
}
