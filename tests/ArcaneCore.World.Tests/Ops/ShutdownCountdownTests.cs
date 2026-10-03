using ArcaneCore.World.Ops.Lifecycle;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The shutdown timer without a clock: cadence and rules of vmangos World.cpp:2665-2767.</summary>
public sealed class ShutdownCountdownTests
{
    [Fact]
    public void OneHourCountdown_AnnouncesExactlyTheHandDerivedTimes()
    {
        // Derived by hand from World.cpp:2724-2750: the request itself (3600), every 5 min
        // below 30 min (1500..300), every minute below 5 min, every 5 s below 30 s, every second below 10 s.
        uint[] expected = [3600, 1500, 1200, 900, 600, 300, 240, 180, 120, 60, 25, 20, 15, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];
        var countdown = new ShutdownCountdown();
        var seen = new List<uint>();

        ServerMessage? first = countdown.Request(3600, ShutdownMask.None, 0, activeSessions: 5);
        Assert.Equal(ServerMessageType.ShutdownTime, first!.Value.Type);
        seen.Add(countdown.SecondsRemaining);
        while (countdown.IsPending)
        {
            if (countdown.Advance(1, 5) is not null)
            {
                seen.Add(countdown.SecondsRemaining);
            }
        }

        Assert.Equal(expected, seen);
        Assert.True(countdown.StopRequested);
        Assert.DoesNotContain(1800u, seen);
        Assert.DoesNotContain(2700u, seen);
    }

    [Fact]
    public void Request_AlwaysAnnounces_AndTheTextIsTheRetailTimeString()
    {
        var countdown = new ShutdownCountdown();
        ServerMessage message = countdown.Request(90, ShutdownMask.Restart, 2, 0)!.Value;
        Assert.Equal(new ServerMessage(ServerMessageType.RestartTime, "1 Minute 30 Seconds."), message);
    }

    [Fact]
    public void StalledWorld_SkipsValues_WhenMoreThanOneSecondElapsed()
    {
        var countdown = new ShutdownCountdown();
        countdown.Request(100, ShutdownMask.None, 0, 0);
        Assert.Null(countdown.Advance(33, 0)); // 67 is not an announcement value
        Assert.Equal(67u, countdown.SecondsRemaining);
        ServerMessage? message = countdown.Advance(7, 0); // 60
        Assert.Equal("1 Minute ", message!.Value.Text);
    }

    [Fact]
    public void DelayZero_StopsImmediately_UnlessIdleWithSessions()
    {
        var now = new ShutdownCountdown();
        Assert.Null(now.Request(0, ShutdownMask.None, 0, 3));
        Assert.True(now.StopRequested);

        var idle = new ShutdownCountdown();
        idle.Request(0, ShutdownMask.Idle, 0, 3);
        Assert.False(idle.StopRequested);
        Assert.Equal(1u, idle.SecondsRemaining);

        var idleEmpty = new ShutdownCountdown();
        idleEmpty.Request(0, ShutdownMask.Idle, 0, 0);
        Assert.True(idleEmpty.StopRequested);
    }

    [Fact]
    public void IdleMode_NeverAnnounces_AndPinsTheTimerWhileSessionsRemain()
    {
        var countdown = new ShutdownCountdown();
        Assert.Null(countdown.Request(30, ShutdownMask.Idle, 0, 2));
        Assert.Null(countdown.Advance(25, 2));
        Assert.Null(countdown.Advance(5, 2)); // due, but sessions remain
        Assert.False(countdown.StopRequested);
        Assert.Equal(1u, countdown.SecondsRemaining);
        Assert.Null(countdown.Advance(1, 2));
        Assert.False(countdown.StopRequested);
        Assert.Null(countdown.Advance(1, 0));
        Assert.True(countdown.StopRequested);
    }

    [Fact]
    public void Cancel_UsesTheMaskBeforeClearing_AndResetsTheExitCode()
    {
        var restart = new ShutdownCountdown();
        restart.Request(60, ShutdownMask.Restart, 2, 0);
        Assert.Equal(ServerMessageType.RestartCancelled, restart.Cancel()!.Value.Type);
        Assert.False(restart.IsPending);
        Assert.Equal(0, restart.ExitCode);
        Assert.Null(restart.Cancel()); // nothing left to cancel

        var shutdown = new ShutdownCountdown();
        shutdown.Request(60, ShutdownMask.None, 9, 0);
        Assert.Equal(ServerMessageType.ShutdownCancelled, shutdown.Cancel()!.Value.Type);
    }

    [Fact]
    public void RequestsAreIgnored_OnceStopIsPending()
    {
        var countdown = new ShutdownCountdown();
        countdown.Request(0, ShutdownMask.None, 5, 0);
        Assert.Null(countdown.Request(60, ShutdownMask.Restart, 2, 0));
        Assert.Equal(5, countdown.ExitCode);
        Assert.Null(countdown.Cancel());
    }

    [Fact]
    public void ExitCodeOver125_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ShutdownCountdown().Request(10, ShutdownMask.None, 126, 0));

    [Fact]
    public void ExactlyTwelveHours_IsNotAnnounced_AsInVmangos()
    {
        var countdown = new ShutdownCountdown();
        countdown.Request((12 * 3600) + 1, ShutdownMask.None, 0, 0);
        Assert.Null(countdown.Advance(1, 0)); // 12 h exactly: neither the < 12 h nor the > 12 h rule
    }

    [Theory]
    [InlineData(5ul, "5 Seconds.")]
    [InlineData(1ul, "1 Second.")]
    [InlineData(0ul, "0 Second.")]
    [InlineData(60ul, "1 Minute ")]
    [InlineData(61ul, "1 Minute 1 Second.")]
    [InlineData(90ul, "1 Minute 30 Seconds.")]
    [InlineData(120ul, "2 Minutes ")]
    [InlineData(3600ul, "1 Hour ")]
    [InlineData(7200ul, "2 Hours ")]
    [InlineData(3661ul, "1 Hour 1 Minute 1 Second.")]
    [InlineData(86400ul, "1 Day ")]
    [InlineData(90000ul, "1 Day 1 Hour ")]
    [InlineData(172800ul, "2 Days ")]
    public void TimeText_MatchesSecsToTimeString(ulong seconds, string expected)
        => Assert.Equal(expected, ServerTimeText.Format(seconds));
}
