using ArcaneCore.Game.Death;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

internal sealed class FixedDeathClock(long unixSeconds) : DeathClock
{
    public long Now { get; set; } = unixSeconds;

    public override long UnixSeconds => Now;
}

/// <summary>The per-world death seam registry and its vmangos-default options.</summary>
public sealed class DeathHooksTests
{
    [Fact]
    public void Options_DefaultToVmangosValues()
    {
        // mangosd.conf.dist.in:2850-2851 (Death.CorpseReclaimDelay.PvP / .PvE = 1)
        var options = new DeathOptions();
        Assert.True(options.CorpseReclaimDelayPvP);
        Assert.True(options.CorpseReclaimDelayPvE);
        Assert.Equal("World:Death", DeathOptions.SectionName);
    }

    [Fact]
    public void For_WithNothingRegistered_ReturnsTheDefaultWithRetailOptionsAndTheSystemClock()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        DeathHooks hooks = DeathHooks.For(world);
        Assert.Same(DeathHooks.Default, hooks);
        Assert.True(hooks.Options.CorpseReclaimDelayPvE);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long now = hooks.Clock.UnixSeconds;
        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(now, before, after);
    }

    [Fact]
    public void Register_AppliesToThatWorldOnly()
    {
        using WorldRuntime a = TestWorld.CreateRuntime();
        using WorldRuntime b = TestWorld.CreateRuntime();
        var custom = new DeathHooks(new DeathOptions { CorpseReclaimDelayPvE = false }, new FixedDeathClock(1234));
        DeathHooks.Register(a, custom);
        Assert.Same(custom, DeathHooks.For(a));
        Assert.Equal(1234, DeathHooks.For(a).Clock.UnixSeconds);
        Assert.Same(DeathHooks.Default, DeathHooks.For(b));
    }

    [Fact]
    public void TryRegister_FirstWins_AndNeverRegistersTheDefault()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var first = new DeathHooks(new DeathOptions(), new FixedDeathClock(1));
        var second = new DeathHooks(new DeathOptions(), new FixedDeathClock(2));
        Assert.False(DeathHooks.TryRegister(world, DeathHooks.Default));
        Assert.True(DeathHooks.TryRegister(world, first));
        Assert.False(DeathHooks.TryRegister(world, second));
        Assert.Same(first, DeathHooks.For(world));
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new DeathHooks(null!, DeathClock.System));
        Assert.Throws<ArgumentNullException>(() => new DeathHooks(new DeathOptions(), null!));
    }
}
