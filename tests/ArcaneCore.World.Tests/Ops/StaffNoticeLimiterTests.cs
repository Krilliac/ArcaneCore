using ArcaneCore.World.Ops.Announce;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The per-key cooldown of the direct Warden and anti-cheat staff lines (<see cref="StaffNoticeLimiter"/>).</summary>
public sealed class StaffNoticeLimiterTests
{
    private long _now = 5_000_000;

    private StaffNoticeLimiter Limiter() => new(() => _now);

    [Fact]
    public void TheFirstNoticePasses_RepeatsInTheWindowAreCounted_AndTheCountIsReportedOnce()
    {
        StaffNoticeLimiter limiter = Limiter();
        Assert.True(limiter.TryPass("7:Kick", "account A (7): kick", 30, out int before));
        Assert.Equal(0, before);
        for (int i = 0; i < 7; i++)
        {
            Assert.False(limiter.TryPass("7:Kick", "account A (7): kick", 30, out _));
        }

        _now += 29_999;
        Assert.Empty(limiter.TakeExpired()); // still inside the window
        _now += 1;
        SuppressedNotices held = Assert.Single(limiter.TakeExpired());
        Assert.Equal(new SuppressedNotices("account A (7): kick", 7, 30), held);
        Assert.Equal("account A (7): kick: 7 more suppressed in the last 30 s", StaffNoticeLimiter.SummaryFor(held));
        Assert.Empty(limiter.TakeExpired());
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void ANoticeAfterTheWindow_CarriesTheHeldCount_WhenTheTickHasNotReportedIt()
    {
        StaffNoticeLimiter limiter = Limiter();
        limiter.TryPass("7:Ban", "x", 30, out _);
        limiter.TryPass("7:Ban", "x", 30, out _);
        limiter.TryPass("7:Ban", "x", 30, out _);
        _now += 30_000;
        Assert.True(limiter.TryPass("7:Ban", "x", 30, out int suppressed));
        Assert.Equal(2, suppressed);
        Assert.Equal("; 2 more suppressed in the last 30 s", StaffNoticeLimiter.SuffixFor(suppressed, 30));
        Assert.Equal(string.Empty, StaffNoticeLimiter.SuffixFor(0, 30));
        Assert.False(limiter.TryPass("7:Ban", "x", 30, out _)); // a new window opened
    }

    [Fact]
    public void KeysAreIndependent_AndACooldownOfZeroPassesEverything()
    {
        StaffNoticeLimiter limiter = Limiter();
        Assert.True(limiter.TryPass("7:Kick", "a", 30, out _));
        Assert.True(limiter.TryPass("8:Kick", "b", 30, out _)); // another account
        Assert.True(limiter.TryPass("7:Ban", "c", 30, out _));  // another action
        Assert.False(limiter.TryPass("7:Kick", "a", 30, out _));
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryPass("9:Kick", "d", 0, out _));
        }
    }

    [Fact]
    public void AFloodOfDistinctKeys_IsBounded_AndCountedUnderTheOverflowKey()
    {
        StaffNoticeLimiter limiter = Limiter();
        for (int i = 0; i < StaffNoticeLimiter.MaxKeys; i++)
        {
            Assert.True(limiter.TryPass($"{i}:Kick", $"account {i}", 30, out _));
        }

        Assert.False(limiter.TryPass("new:Kick", "new", 30, out _));
        Assert.False(limiter.TryPass("newer:Kick", "newer", 30, out _));
        Assert.Equal(StaffNoticeLimiter.MaxKeys + 1, limiter.Count);

        _now += 30_000;
        SuppressedNotices overflow = Assert.Single(limiter.TakeExpired());
        Assert.Equal(new SuppressedNotices("other accounts", 2, 30), overflow);
        Assert.Equal(0, limiter.Count);
    }
}
