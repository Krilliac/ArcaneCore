using System.Diagnostics;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>
/// The world thread's wait. Only properties that hold on any machine are asserted: a precise wait never returns before its due
/// time, and the stop signal ends it at once. How close to the due time it wakes is measured, not asserted
/// (<see cref="TickCadenceMeasurementTests"/>; docs/integration/perf-limits-20261008.md).
/// </summary>
public sealed class WorldTickWaiterTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(500L)]
    public void APreciseWait_NeverReturnsBeforeItsDueTime(long spinMicros)
    {
        using var stop = new ManualResetEventSlim(false);
        using var waiter = new WorldTickWaiter(WorldTickTimer.Precise, stop, spinMicros);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            Assert.Equal(WorldTickTimer.Precise, waiter.Mode); // the high-resolution timer exists on every supported Windows
        }

        var clock = Stopwatch.StartNew();
        for (int i = 1; i <= 10; i++)
        {
            long due = i * 7 * Stopwatch.Frequency / 1000 + Stopwatch.Frequency / 3000; // 7 ms steps off the millisecond grid
            waiter.WaitUntil(clock, due, legacyWaitMs: 7);
            Assert.True(clock.ElapsedTicks >= due, $"woke {(due - clock.ElapsedTicks) * 1_000_000 / Stopwatch.Frequency} us early");
        }
    }

    [Fact]
    public void TheStopSignal_EndsAPreciseWaitAtOnce()
    {
        using var stop = new ManualResetEventSlim(false);
        using var waiter = new WorldTickWaiter(WorldTickTimer.Precise, stop);
        var clock = Stopwatch.StartNew();
        using var timer = new Timer(_ => stop.Set(), null, 50, Timeout.Infinite);
        waiter.WaitUntil(clock, 60 * Stopwatch.Frequency, legacyWaitMs: 60_000); // a minute away
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"waited {clock.Elapsed}");
        Assert.True(stop.IsSet);
    }

    [Fact]
    public void ADueTimeAlreadyPassed_ReturnsAtOnce()
    {
        using var stop = new ManualResetEventSlim(false);
        using var waiter = new WorldTickWaiter(WorldTickTimer.Precise, stop);
        var clock = Stopwatch.StartNew();
        waiter.WaitUntil(clock, 0, legacyWaitMs: 0);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheLegacyWait_IsTheWholeMillisecondEventWait()
    {
        using var stop = new ManualResetEventSlim(true); // set: the event wait returns at once
        using var waiter = new WorldTickWaiter(WorldTickTimer.Legacy, stop);
        Assert.Equal(WorldTickTimer.Legacy, waiter.Mode);
        var clock = Stopwatch.StartNew();
        waiter.WaitUntil(clock, 60 * Stopwatch.Frequency, legacyWaitMs: 60_000);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTickWaiter(WorldTickTimer.Precise, stop, -1));
    }
}
