using System.Diagnostics;
using ArcaneCore.World.Playerbots;
using Xunit;
using ThinkLane = ArcaneCore.World.Playerbots.PlayerbotThinkRotation.ThinkLane;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class ManagedPlayerbotPerfTests
{
    private struct ArrayLaneDriver(ThinkLane[] lanes, int priorityBudget, int mainBudget, double stepMs)
        : PlayerbotThinkRotation.IThinkLaneDriver
    {
        public readonly List<(int Index, ThinkLane Lane)> Visited = [];
        public int PriorityRemaining = priorityBudget;
        public int MainRemaining = mainBudget;
        public double ClockMs;

        public ThinkLane Classify(int index) => lanes[index];

        public bool Visit(int index, ThinkLane lane)
        {
            if (lane == ThinkLane.Priority)
            {
                if (PriorityRemaining <= 0) return false;
                PriorityRemaining--;
            }
            else
            {
                if (MainRemaining <= 0) return false;
                MainRemaining--;
            }

            Visited.Add((index, lane));
            ClockMs += stepMs;
            return true;
        }

        public double ElapsedMs() => ClockMs;
    }

    [Fact]
    public void StructDriver_MatchesDelegateTickLanes_AcrossMultipleTicks()
    {
        const int bots = 64;
        var lanes = new ThinkLane[bots];
        for (int i = 0; i < bots; i++)
            lanes[i] = i % 5 == 0 ? ThinkLane.Priority : ThinkLane.Normal;

        var delegateRotation = new PlayerbotThinkRotation();
        var structRotation = new PlayerbotThinkRotation();

        for (int tick = 0; tick < 20; tick++)
        {
            int priorityRem = 4;
            int mainRem = 7;
            double clock = 0;
            var delegateVisits = new List<(int Index, ThinkLane Lane)>();

            int expectedThinks = delegateRotation.TickLanes(
                bots,
                budgetMs: 8.0,
                priorityShare: 0.5,
                classify: i => lanes[i],
                visit: (i, lane) =>
                {
                    if (lane == ThinkLane.Priority)
                    {
                        if (priorityRem <= 0) return false;
                        priorityRem--;
                    }
                    else
                    {
                        if (mainRem <= 0) return false;
                        mainRem--;
                    }

                    delegateVisits.Add((i, lane));
                    clock += 0.75;
                    return true;
                },
                elapsedMs: () => clock,
                priorityScale: 0.8);

            var driver = new ArrayLaneDriver(lanes, priorityBudget: 4, mainBudget: 7, stepMs: 0.75);
            int actualThinks = structRotation.TickLanes(bots, budgetMs: 8.0, priorityShare: 0.5, ref driver, priorityScale: 0.8);

            Assert.Equal(expectedThinks, actualThinks);
            Assert.Equal(delegateVisits, driver.Visited);
            Assert.Equal(delegateRotation.Cursor, structRotation.Cursor);
            Assert.Equal(delegateRotation.LastPriorityVisits, structRotation.LastPriorityVisits);
            Assert.Equal(delegateRotation.LastTickTimeLimited, structRotation.LastTickTimeLimited);
            Assert.Equal(delegateRotation.LastPriorityScale, structRotation.LastPriorityScale);
        }
    }

    [Fact]
    public void StampBuffer_IsReusedWhenBotCountShrinksAndRegrows()
    {
        var rotation = new PlayerbotThinkRotation();
        var lanes = new ThinkLane[4500];
        lanes[0] = ThinkLane.Priority;
        var driver = new ArrayLaneDriver(lanes, priorityBudget: 10, mainBudget: 10, stepMs: 0.1);

        rotation.TickLanes(4500, budgetMs: 8.0, priorityShare: 0.5, ref driver);
        int initialCapacity = rotation.StampCapacity;
        Assert.Equal(4500, initialCapacity);

        // Shrinking and regrowing within the high-water mark must not reallocate or corrupt stamp tracking.
        driver = new ArrayLaneDriver(lanes, priorityBudget: 10, mainBudget: 10, stepMs: 0.1);
        rotation.TickLanes(4499, budgetMs: 8.0, priorityShare: 0.5, ref driver);
        Assert.Equal(initialCapacity, rotation.StampCapacity);

        driver = new ArrayLaneDriver(lanes, priorityBudget: 10, mainBudget: 10, stepMs: 0.1);
        rotation.TickLanes(4500, budgetMs: 8.0, priorityShare: 0.5, ref driver);
        Assert.Equal(initialCapacity, rotation.StampCapacity);
    }

    [Fact]
    public void EffectiveStarvationBound_Prevents4500BotCoverageInflation()
    {
        const int bots = 4500;
        const uint configuredBoundMs = 10_000;
        uint effectiveBoundMs = PlayerbotThinkStarvation.EffectiveBoundMs(configuredBoundMs, bots);
        Assert.Equal(45_000u, effectiveBoundMs);

        // At 4,500 bots with 0.2 ms per visit on a 50 ms tick, unscaled 10 s bound demanded 9.0 ms floor;
        // scaled 45 s bound demands 2.0 ms floor, respecting the 1,000-bot design scaling.
        double unscaledCoverage = PlayerbotThinkBudget.CoverageMs(bots, tickMs: 50, boundMs: configuredBoundMs, visitMs: 0.2);
        double scaledCoverage = PlayerbotThinkBudget.CoverageMs(bots, tickMs: 50, boundMs: effectiveBoundMs, visitMs: 0.2);
        Assert.Equal(9.0, unscaledCoverage, precision: 3);
        Assert.Equal(2.0, scaledCoverage, precision: 3);

        // PriorityScale at 20 s silence is clamped to 0.2 under unscaled 10 s bound, but remains 1.0 under 45 s effective bound.
        Assert.Equal(PlayerbotThinkStarvation.MinPriorityScale, PlayerbotThinkStarvation.PriorityScale(20_000, configuredBoundMs), precision: 3);
        Assert.Equal(1.0, PlayerbotThinkStarvation.PriorityScale(20_000, effectiveBoundMs), precision: 3);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Rotation4500Bots_Completes200TicksWellUnderOneTickBudget()
    {
        const int bots = 4500;
        var lanes = new ThinkLane[bots];
        for (int i = 0; i < bots; i++)
            lanes[i] = i % 10 == 0 ? ThinkLane.Priority : ThinkLane.Normal;

        var rotation = new PlayerbotThinkRotation();
        long start = Stopwatch.GetTimestamp();
        int totalThinks = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            var driver = new ArrayLaneDriver(lanes, priorityBudget: 33, mainBudget: 15, stepMs: 0.1);
            totalThinks += rotation.TickLanes(bots, budgetMs: 8.0, priorityShare: 0.7, ref driver);
        }

        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.Equal(200 * 48, totalThinks);
        Assert.True(elapsedMs < 50.0, $"Expected 200 rotation ticks over 4,500 bots to take < 50 ms, took {elapsedMs:F2} ms");
    }
}
