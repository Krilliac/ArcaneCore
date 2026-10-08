using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>
/// The world loop's schedule on a simulated clock: tick work takes some milliseconds, and every wait oversleeps (a
/// Windows wait wakes on the ~15.6 ms system timer, so a 47 ms wait often takes 62).
/// </summary>
public sealed class WorldTickSchedulerTests
{
    private const int Interval = 50;

    private static List<long> Simulate(WorldTickScheduler scheduler, int ticks, Func<int, long> workMs, Func<int, int> oversleepMs)
    {
        var starts = new List<long>(ticks);
        long now = 0;
        for (int i = 0; i < ticks; i++)
        {
            long start = now;
            starts.Add(start);
            now += workMs(i);
            int wait = scheduler.NextWait(start, now);
            if (wait > 0) now += wait + oversleepMs(i);
        }

        return starts;
    }

    [Fact]
    public void OversleptWaits_DoNotDriftTheRateBelowTheTarget()
    {
        var scheduler = new WorldTickScheduler(Interval);
        List<long> starts = Simulate(scheduler, 400, _ => 3, _ => 12);
        double meanPeriod = (starts[^1] - starts[0]) / (double)(starts.Count - 1);
        Assert.InRange(meanPeriod, Interval - 0.5, Interval + 0.5); // 20 ticks/s, not 16
        for (int i = 0; i < starts.Count; i++)
            Assert.InRange(starts[i] - (long)i * Interval, 0, Interval); // never a growing backlog
    }

    [Fact]
    public void JitteryWaits_AverageToTheTarget()
    {
        var random = new Random(5875);
        var scheduler = new WorldTickScheduler(Interval);
        List<long> starts = Simulate(scheduler, 2000, _ => random.Next(1, 20), _ => random.Next(0, 16));
        double meanPeriod = (starts[^1] - starts[0]) / (double)(starts.Count - 1);
        Assert.InRange(meanPeriod, Interval - 0.5, Interval + 0.5);
    }

    [Fact]
    public void ALongTick_IsNotCaughtUpInABurst_AndIsCounted()
    {
        var scheduler = new WorldTickScheduler(Interval);
        List<long> starts = Simulate(scheduler, 40, i => i == 10 ? 400 : 3, _ => 0);
        // No spiral: after the 400 ms tick the loop starts at most one tick at once, then returns to the cadence.
        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        Assert.Equal(400, gaps[10]);
        Assert.True(gaps[11] <= Interval);
        Assert.All(gaps.Skip(12), gap => Assert.Equal(Interval, gap));
        Assert.True(scheduler.LateTicks >= 1);
        Assert.InRange(scheduler.SkippedTicks, 6, 8); // the ~7 starts that fell inside the long tick
    }

    [Fact]
    public void AnIntervalShorterThanTheTimerGranularity_AlternatesOversleptWaitsWithImmediateTicks()
    {
        // A 5 ms tick on the ~15.6 ms Windows timer (the world test host): every wait wakes 15 ms later, a whole interval behind, so the
        // missed start is given up and the start due now runs at once. Frames split evenly between ~15 ms and ~0 ms: their median says
        // nothing about the cadence, their mean is the rate the timer allows (what TickWatchdogFeatureTests asserts).
        var scheduler = new WorldTickScheduler(5);
        var starts = new List<long>();
        long now = 0;
        for (int i = 0; i < 100; i++)
        {
            starts.Add(now);
            if (scheduler.NextWait(now, now) > 0) now += 15;
        }

        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        Assert.All(gaps, gap => Assert.True(gap is 0 or 15, $"gap {gap}"));
        Assert.InRange(gaps.Count(gap => gap == 0), 49, 50);
        Assert.InRange(gaps.Average(), 7.4, 7.6); // 15 ms per two ticks
    }

    [Fact]
    public void AnOnTimeLoop_HasNoLateOrSkippedTicks()
    {
        var scheduler = new WorldTickScheduler(Interval);
        List<long> starts = Simulate(scheduler, 100, _ => 5, _ => 0);
        Assert.All(starts.Zip(starts.Skip(1), (a, b) => b - a), gap => Assert.Equal(Interval, gap));
        Assert.Equal(0, scheduler.LateTicks);
        Assert.Equal(0, scheduler.SkippedTicks);
    }
}
