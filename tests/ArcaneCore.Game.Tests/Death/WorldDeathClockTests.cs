using ArcaneCore.Game.Death;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>The death clock follows world time on the manual test clock, and is the machine clock otherwise.</summary>
public sealed class WorldDeathClockTests
{
    [Fact]
    public async Task OnTheManualClock_UnixSecondsFollowTheWorldClock_NotTheWall()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.UseManualClock();
        world.Start();
        try
        {
            var clock = new WorldDeathClock(world);
            long first = clock.UnixSeconds;
            Assert.InRange(first, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2);

            await world.AdvanceClockAsync(45_000);

            Assert.Equal(first + 45, clock.UnixSeconds);
            Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - first, 0, 30); // the wall barely moved
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public void OnTheRealClock_UnixSecondsAreTheMachineClock()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long read = new WorldDeathClock(world).UnixSeconds;
        Assert.InRange(read, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void DeathHooksFor_AManualClockWorldWithNothingRegistered_GetsTheWorldFollowingClock_OtherwiseTheDefault()
    {
        using WorldRuntime real = TestWorld.CreateRuntime();
        Assert.Same(DeathHooks.Default, DeathHooks.For(real));

        using WorldRuntime manual = TestWorld.CreateRuntime();
        manual.UseManualClock();
        DeathHooks hooks = DeathHooks.For(manual);
        Assert.IsType<WorldDeathClock>(hooks.Clock);
        Assert.Same(hooks, DeathHooks.For(manual));

        var own = new DeathHooks(new DeathOptions(), DeathClock.System);
        DeathHooks.Register(manual, own);
        Assert.Same(own, DeathHooks.For(manual));
    }
}
