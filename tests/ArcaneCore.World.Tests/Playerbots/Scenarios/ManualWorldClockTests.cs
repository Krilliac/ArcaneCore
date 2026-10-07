using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>The manual world clock (WorldRuntime.UseManualClock) the scenario harness runs deterministic tests on.</summary>
public sealed class ManualWorldClockTests
{
    private static WorldRuntime Start(uint stepMs = 50)
    {
        var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NoSaves(),
            NullLogger<WorldRuntime>.Instance);
        world.UseManualClock(stepMs);
        world.Start();
        return world;
    }

    [Fact]
    public async Task GameTimeStandsStill_UntilAdvanced_ThenMovesInWholeSteps()
    {
        using WorldRuntime world = Start();
        var diffs = new List<uint>();
        world.Updated += diffs.Add;
        uint start = world.NowMs;
        await Task.Delay(100);
        Assert.Equal(start, await world.InvokeAsync(() => world.NowMs)); // wall time passed, game time did not

        await world.AdvanceClockAsync(175);
        Assert.Equal(start + 175, world.NowMs);
        Assert.Equal(175u, await world.InvokeAsync(() => (uint)diffs.Sum(d => (long)d)));
        Assert.All(diffs, diff => Assert.True(diff <= 50));
    }

    [Fact]
    public async Task AdvanceUntil_StopsOnTheFirstTickWhereTheConditionHolds()
    {
        using WorldRuntime world = Start();
        uint start = world.NowMs;
        Assert.True(await world.AdvanceClockUntilAsync(10_000, () => world.NowMs - start >= 120));
        Assert.Equal(start + 150, world.NowMs); // ticks of 50: 50, 100, 150

        uint before = world.NowMs;
        Assert.False(await world.AdvanceClockUntilAsync(300, () => false));
        Assert.Equal(before + 300, world.NowMs);
    }

    [Fact]
    public async Task AFailingCondition_FailsTheAdvance()
    {
        using WorldRuntime world = Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.AdvanceClockUntilAsync(1000, () => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void TheManualClock_IsChosenBeforeStart_AndOnlyThere()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NoSaves(), NullLogger<WorldRuntime>.Instance);
        Assert.Throws<InvalidOperationException>(() => { _ = world.AdvanceClockAsync(10); }); // thrown synchronously
        world.Start();
        Assert.Throws<InvalidOperationException>(() => world.UseManualClock());
        Assert.False(world.IsManualClock);
    }

    [Fact]
    public async Task TheScenarioTimeProvider_FollowsGameTime_AndJumps()
    {
        using WorldRuntime world = Start();
        var time = new ScenarioTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_000));
        ScenarioClock clock = ScenarioClock.Manual(world, time);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1_003, time.GetUtcNow().ToUnixTimeSeconds());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Advanced);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1_003 + 3600, time.GetUtcNow().ToUnixTimeSeconds());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Advanced);
    }

    private sealed class NoSaves : ICharacterSaveQueue
    {
        public void Enqueue(Kernel.Characters.CharacterState state)
        {
        }
    }
}
