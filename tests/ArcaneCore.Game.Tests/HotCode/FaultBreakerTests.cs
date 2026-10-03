using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.HotCode;

/// <summary>
/// The map-updater fault breaker (World:MaxConsecutiveUpdaterFaults, set by the dev runner): an
/// updater that throws every tick is skipped after N consecutive failures while the others keep
/// running; a failure now and then never trips it; the default (0) changes nothing.
/// </summary>
public sealed class FaultBreakerTests
{
    private sealed class Flaky(Func<int, bool> fails) : IMapUpdater
    {
        public int Calls { get; private set; }

        public int Removed { get; private set; }

        public void Update(Map map, uint diffMs)
        {
            Calls++;
            if (fails(Calls))
            {
                throw new InvalidOperationException($"boom {Calls}");
            }
        }

        public void OnPlayerRemoved(Map map, Player player) => Removed++;
    }

    private sealed class Counter : IMapUpdater
    {
        public int Calls { get; private set; }

        public void Update(Map map, uint diffMs) => Calls++;

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private static (WorldRuntime World, Map Map) CreateMap(int limit)
    {
        var world = new WorldRuntime(
            new WorldRuntimeOptions { MaxConsecutiveUpdaterFaults = limit },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        return (world, world.GetMap(0));
    }

    private static void Ticks(WorldRuntime world, int count)
    {
        for (int i = 0; i < count; i++)
        {
            world.RunTick(50);
        }
    }

    [Fact]
    public void AnUpdaterThatThrowsEveryTick_IsSkippedAfterTheLimit_WhileOthersKeepRunning()
    {
        (WorldRuntime world, Map map) = CreateMap(limit: 3);
        var bad = new Flaky(_ => true);
        var good = new Counter();
        map.AddUpdater(bad);
        map.AddUpdater(good);

        Ticks(world, 10);

        Assert.Equal(3, bad.Calls);
        Assert.Equal(10, good.Calls);
        Assert.Equal([bad], map.IsolatedUpdaters);
    }

    [Fact]
    public void FailuresThatAreNotConsecutive_NeverTripTheBreaker()
    {
        (WorldRuntime world, Map map) = CreateMap(limit: 3);
        var flaky = new Flaky(call => call % 3 != 0); // fail, fail, ok, fail, fail, ok ...
        map.AddUpdater(flaky);

        Ticks(world, 30);

        Assert.Equal(30, flaky.Calls);
        Assert.Empty(map.IsolatedUpdaters);
    }

    [Fact]
    public void ByDefault_AThrowingUpdaterIsCalledEveryTick_AsBefore()
    {
        (WorldRuntime world, Map map) = CreateMap(limit: 0);
        var bad = new Flaky(_ => true);
        map.AddUpdater(bad);

        Ticks(world, 200);

        Assert.Equal(200, bad.Calls);
        Assert.Empty(map.IsolatedUpdaters);
    }

    [Fact]
    public void ClearingTheFaults_GivesAnIsolatedUpdaterAnotherChance()
    {
        (WorldRuntime world, Map map) = CreateMap(limit: 2);
        bool fixedByTheEdit = false;
        var updater = new Flaky(_ => !fixedByTheEdit);
        map.AddUpdater(updater);

        Ticks(world, 5);
        Assert.Equal(2, updater.Calls);
        Assert.Single(map.IsolatedUpdaters);

        fixedByTheEdit = true; // a code edit repaired the body
        map.ClearUpdaterFaults();
        Ticks(world, 5);

        Assert.Equal(7, updater.Calls);
        Assert.Empty(map.IsolatedUpdaters);
    }

    [Fact]
    public void AnIsolatedUpdater_StillHearsThatAPlayerLeft()
    {
        (WorldRuntime world, Map map) = CreateMap(limit: 1);
        var bad = new Flaky(_ => true);
        map.AddUpdater(bad);
        Ticks(world, 2);
        Assert.Single(map.IsolatedUpdaters);

        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        world.LogoutPlayer(player);

        Assert.Equal(1, bad.Removed);
    }
}
