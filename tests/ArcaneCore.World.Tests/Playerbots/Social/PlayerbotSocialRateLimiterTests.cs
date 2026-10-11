using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The sliding-window and speaker-gap limits of guild chat and emotes, on world time in milliseconds.</summary>
public sealed class PlayerbotSocialRateLimiterTests
{
    private const long Hour = PlayerbotSocialRateLimiter<int>.HourMs;
    private const long Minute = 60_000;

    [Fact]
    public void Window_AllowsAtMostTheCapPerHour_ThenFreesTheOldestSlot()
    {
        var limiter = new PlayerbotSocialRateLimiter<int>();
        Assert.Equal(3_600_000, Hour);

        Assert.True(limiter.TryTake(1, 3, 0));
        Assert.True(limiter.TryTake(1, 3, 10 * Minute));
        Assert.True(limiter.TryTake(1, 3, 20 * Minute));
        Assert.Equal(3, limiter.Used(1, 30 * Minute));
        Assert.False(limiter.HasRoom(1, 3, 30 * Minute));
        Assert.False(limiter.TryTake(1, 3, 30 * Minute));
        Assert.Equal(3, limiter.Used(1, 30 * Minute)); // a refused take uses nothing

        // The oldest slot (t = 0) leaves the window exactly one hour later, not a millisecond before.
        Assert.False(limiter.TryTake(1, 3, Hour - 1));
        Assert.True(limiter.HasRoom(1, 3, Hour)); // asking takes nothing
        Assert.Equal(3, limiter.Used(1, Hour - 1));
        Assert.Equal(2, limiter.Used(1, Hour));
        Assert.True(limiter.TryTake(1, 3, Hour));
        Assert.False(limiter.TryTake(1, 3, Hour + 1));

        // The next slot to free is the one taken at ten minutes.
        Assert.False(limiter.HasRoom(1, 3, Hour + 10 * Minute - 1));
        Assert.True(limiter.HasRoom(1, 3, Hour + 10 * Minute));

        // Another key has a window of its own; a smaller cap than the slots in use refuses at once.
        Assert.True(limiter.TryTake(2, 3, Hour));
        Assert.Equal(1, limiter.Used(2, Hour));
        Assert.False(limiter.HasRoom(1, 2, Hour + 1));

        // A shorter window is the caller's choice (the emote limits use 60 s).
        var minutely = new PlayerbotSocialRateLimiter<int>();
        Assert.True(minutely.TryTake(0, 2, 0, windowMs: Minute));
        Assert.True(minutely.TryTake(0, 2, 1000, windowMs: Minute));
        Assert.False(minutely.TryTake(0, 2, Minute - 1, windowMs: Minute));
        Assert.True(minutely.TryTake(0, 2, Minute, windowMs: Minute));
    }

    [Fact]
    public void Window_Sweep_DropsIdleKeys_AndForgetDropsOne()
    {
        var limiter = new PlayerbotSocialRateLimiter<int>();
        limiter.TryTake(1, 5, 0);
        limiter.TryTake(2, 5, Hour - 5);
        limiter.MarkSpoke(1, 0);
        limiter.Sweep(Hour);
        Assert.Equal(0, limiter.Used(1, Hour));
        Assert.Equal(1, limiter.Used(2, Hour));
        Assert.True(limiter.GapElapsed(1, 5 * Hour, Hour)); // the speaker record was older than the window and is gone (else the 5 h gap would still hold)

        limiter.Forget(2);
        Assert.Equal(0, limiter.Used(2, Hour));
        Assert.True(limiter.HasRoom(2, 1, Hour));
    }

    [Fact]
    public void ZeroCap_AllowsNothing()
    {
        var limiter = new PlayerbotSocialRateLimiter<string>();
        Assert.False(limiter.HasRoom("guild", 0, 0));
        Assert.False(limiter.TryTake("guild", 0, 0));
        Assert.False(limiter.TryTake("guild", -1, 0)); // a negative cap is no cap to fill
        Assert.Equal(0, limiter.Used("guild", 0));

        // Lowering a cap below what is already used takes effect at once, and raising it again gives the room back.
        Assert.True(limiter.TryTake("guild", 2, 0));
        Assert.True(limiter.TryTake("guild", 2, 1));
        Assert.False(limiter.TryTake("guild", 0, 2));
        Assert.False(limiter.HasRoom("guild", 1, 2));
        Assert.True(limiter.HasRoom("guild", 3, 2));
    }

    [Fact]
    public void SpeakerGap_HoldsBackASecondLineFromTheSameBot()
    {
        var limiter = new PlayerbotSocialRateLimiter<Guid>();
        Guid bot = Guid.NewGuid(), other = Guid.NewGuid();
        const long gap = 120_000;

        Assert.True(limiter.GapElapsed(bot, gap, 0)); // never spoke
        limiter.MarkSpoke(bot, 1000);
        Assert.False(limiter.GapElapsed(bot, gap, 1000));
        Assert.False(limiter.GapElapsed(bot, gap, 1000 + gap - 1));
        Assert.True(limiter.GapElapsed(bot, gap, 1000 + gap));
        Assert.True(limiter.GapElapsed(other, gap, 1001)); // another speaker is not held back
        Assert.True(limiter.GapElapsed(bot, 0, 1000)); // a zero gap never holds anyone

        // Asking records nothing; marking again restarts the gap.
        limiter.MarkSpoke(bot, 1000 + gap);
        Assert.False(limiter.GapElapsed(bot, gap, 1000 + gap + 1));

        // The sweep forgets a speaker whose gap is over, so a long session does not grow the table.
        limiter.Sweep(1000 + 2 * gap, gap);
        Assert.True(limiter.GapElapsed(bot, gap, 1000 + 2 * gap));
    }
}
