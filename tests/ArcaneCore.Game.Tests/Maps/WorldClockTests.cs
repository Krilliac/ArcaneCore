using System.Diagnostics;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>The world's game clock: tick-start snapshots on the real clock, exact diffs on the stepped and manual clocks, game UTC, wrapping.</summary>
public sealed class WorldClockTests
{
    [Fact]
    public void RealClock_NowMs_IsConstantWithinATick_AndEqualsTheTickStart()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        uint first = 0, second = 0;
        long elapsedInside = 0;
        world.Post(() =>
        {
            first = world.NowMs;
            var spin = Stopwatch.StartNew();
            while (spin.ElapsedMilliseconds < 5)
            {
                // wall time must pass inside the tick without moving the snapshot
            }

            second = world.NowMs;
            elapsedInside = world.Clock.ElapsedMs;
        });

        world.RunTick(0);

        Assert.Equal(WorldClockMode.Realtime, world.Clock.Mode);
        Assert.Equal(first, second);
        Assert.Equal(unchecked((uint)elapsedInside), first);
        Assert.Equal(first, world.Clock.NowMs);
        Assert.Equal(1, world.Clock.TickNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(elapsedInside), world.Uptime);
    }

    [Fact]
    public void SteppedClock_DirectRunTick_AdvancesNowMsAndUptimeByExactlyTheDiff()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.UseSteppedClock(startAtMs: 1000);
        Assert.True(world.Clock.IsDeterministic);
        Assert.Equal(1000u, world.NowMs);

        world.RunTick(50);
        Assert.Equal(1050u, world.NowMs);
        world.RunTick(0);
        Assert.Equal(1050u, world.NowMs);
        world.RunTick(33);

        Assert.Equal(1083u, world.NowMs);
        Assert.Equal(TimeSpan.FromMilliseconds(1083), world.Uptime);
        Assert.Equal(3, world.Clock.TickNumber);
        Assert.Throws<InvalidOperationException>(world.Start);
    }

    [Fact]
    public void ManualClock_UtcNow_FollowsGameTimeFromItsAnchor()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var anchor = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        world.UseManualClockFrom(5000, anchor);
        Assert.Equal(anchor, world.Clock.UtcNow);

        world.RunTick(1500);

        Assert.Equal(anchor.AddMilliseconds(1500), world.Clock.UtcNow);
        Assert.Equal(anchor.ToUnixTimeSeconds() + 1, world.Clock.UnixSeconds);
        Assert.Equal(anchor.ToUnixTimeMilliseconds() + 1500, world.Clock.UnixMilliseconds);
        Assert.Equal(6500u, world.NowMs);
    }

    [Fact]
    public void RealClock_UtcNow_StaysWithinOneSecondOfTheSystemClock()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Assert.True(Math.Abs((world.Clock.UtcNow - DateTimeOffset.UtcNow).TotalSeconds) < 1);

        world.RunTick(50);

        Assert.True(Math.Abs((world.Clock.UtcNow - DateTimeOffset.UtcNow).TotalSeconds) < 1);
    }

    [Fact]
    public void NowMs_WrapsLikeGetMSTime_ElapsedMsDoesNot()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.UseSteppedClock(startAtMs: uint.MaxValue - 10L);
        Assert.Equal(uint.MaxValue - 10u, world.NowMs);

        world.RunTick(20);

        Assert.Equal(9u, world.NowMs);
        Assert.Equal(uint.MaxValue - 10L + 20, world.Clock.ElapsedMs);
        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 10L + 20), world.Uptime);
    }

    [Fact]
    public void GameTimeProvider_GetElapsedTime_MeasuresGameTime()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var anchor = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        world.UseSteppedClock(0, anchor);
        var time = new GameTimeProvider(world.Clock);

        long start = time.GetTimestamp();
        world.RunTick(2500);

        Assert.Equal(TimeSpan.FromMilliseconds(2500), time.GetElapsedTime(start));
        Assert.Equal(1000, time.TimestampFrequency);
        Assert.Equal(anchor.AddMilliseconds(2500), time.GetUtcNow());
        Assert.Throws<NotSupportedException>(() => time.CreateTimer(_ => { }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }
}
