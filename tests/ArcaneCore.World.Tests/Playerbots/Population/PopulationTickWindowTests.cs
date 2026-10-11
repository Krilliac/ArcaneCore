using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationTickWindowTests
{
    [Fact]
    public void P95_IsTheNearestRankOfTheWindow()
    {
        var window = new WorldTickWindow();
        // 1..100 ms, added out of order: the 95th of 100 sorted values is 95 ms.
        foreach (int ms in Enumerable.Range(1, 100).OrderBy(i => (i * 37) % 101)) window.Add(ms * 1000L);
        Assert.Equal(100, window.Samples);
        Assert.Equal(95.0, window.P95Ms(100));
        // A window of the last 20 ticks only: the values added last decide.
        var tail = new WorldTickWindow();
        for (int i = 0; i < 100; i++) tail.Add(1000);
        for (int i = 0; i < 20; i++) tail.Add(50_000);
        Assert.Equal(50.0, tail.P95Ms(20));
        Assert.Equal(50.0, tail.P95Ms(30)); // 10 fast and 20 slow: rank 29 of 30 is slow
        Assert.Equal(50.0, tail.P95Ms(1000)); // all 120 ticks: 20 slow is 17%, so the 95th is slow
        Assert.Equal(1.0, new Func<double>(() => { var w = new WorldTickWindow(); for (int i = 0; i < 97; i++) w.Add(1000); for (int i = 0; i < 3; i++) w.Add(50_000); return w.P95Ms(100); })());
    }

    [Fact]
    public void Reset_ClearsTheSamples_SoTheNextDecisionWaitsForAFullWindow()
    {
        var window = new WorldTickWindow();
        for (int i = 0; i < 300; i++) window.Add(90_000);
        Assert.Equal(90.0, window.P95Ms(200));
        window.Reset();
        Assert.Equal(0, window.Samples);
        Assert.Equal(0.0, window.P95Ms(200));
        window.Add(2000);
        Assert.Equal(1, window.Samples);
        Assert.Equal(2.0, window.P95Ms(200)); // the slow ticks from before the reset no longer count

        PlayerbotPopulationOptions options = new();
        Assert.Equal("settling", PopulationBudgetController.Decide(window.Samples, window.P95Ms(200), 0, 0, 10, options, windows: 0).State);
    }

    [Fact]
    public void TheRingKeepsOnlyTheLastWindowTicks()
    {
        var window = new WorldTickWindow();
        for (int i = 0; i < WorldTickWindow.Capacity; i++) window.Add(80_000);
        for (int i = 0; i < 4000; i++) window.Add(5_000); // overwrites all but 96 of the slow ones
        Assert.Equal(WorldTickWindow.Capacity, window.Samples);
        Assert.Equal(5.0, window.P95Ms(200));
        Assert.Equal(5.0, window.P95Ms(WorldTickWindow.Capacity)); // only 96 slow ticks are left of 4096 (2.3%): not the 95th
        for (int i = 0; i < 200; i++) window.Add(70_000);
        Assert.Equal(70.0, window.P95Ms(200));
        window.Add(-5);
        Assert.Equal(70.0, window.P95Ms(200)); // a negative duration is recorded as 0
    }
}
