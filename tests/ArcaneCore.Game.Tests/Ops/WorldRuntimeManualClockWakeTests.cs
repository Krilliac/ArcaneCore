using System.Diagnostics;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>
/// The manual clock (tests only) wakes the world thread when work arrives, so a round trip does not wait out the rest of a tick
/// interval; the real clock never wakes early (production tick cadence is unchanged by posted work).
/// </summary>
[Collection("World tick load")]
public sealed class WorldRuntimeManualClockWakeTests
{
    [Fact]
    public async Task ManualClock_InvokeAsyncRoundTrips_DoNotWaitOutTheTickInterval()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.TickIntervalMs = 50;
        world.UseManualClock();
        world.Start();
        try
        {
            await world.InvokeAsync(() => 0); // the thread is up and has settled into its idle wait
            var wall = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                await world.InvokeAsync(() => i);
            }

            wall.Stop();
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(2),
                $"200 manual-clock round trips took {wall.Elapsed.TotalMilliseconds:F0} ms (TickIntervalMs 50: sleeping the interval per round trip is ~10000 ms)");
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task ManualClock_AdvanceClock_DoesNotWaitOutTheTickInterval()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.TickIntervalMs = 50;
        world.UseManualClock();
        world.Start();
        try
        {
            await world.InvokeAsync(() => 0);
            var wall = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                await world.AdvanceClockAsync(50);
            }

            wall.Stop();
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(2), $"100 advances took {wall.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task RealClock_AStreamOfPosts_DoesNotRaiseTheTickRate()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.TickIntervalMs = 50;
        int ticks = 0;
        world.WorldTick += _ => Interlocked.Increment(ref ticks);
        world.Start();
        try
        {
            await world.InvokeAsync(() => 0);
            int before = Volatile.Read(ref ticks);
            var wall = Stopwatch.StartNew();
            int posted = 0;
            while (wall.ElapsedMilliseconds < 2000)
            {
                world.Post(() => Interlocked.Increment(ref posted));
                await Task.Delay(1);
            }

            double seconds = wall.Elapsed.TotalSeconds;
            double perSecond = (Volatile.Read(ref ticks) - before) / seconds;
            // 1000 / 50 = 20 ticks per second; every Post would add one if it woke the thread. No lower bound: a loaded runner drops ticks.
            Assert.True(perSecond <= 26, $"{perSecond:F1} ticks/s for {Volatile.Read(ref posted)} posts");
        }
        finally
        {
            world.Stop();
        }
    }
}
