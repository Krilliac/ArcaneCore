using ArcaneCore.Game.Death;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Death;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class DeathFeatureTests
{
    private static WorldRuntime NewWorld()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static ServiceProvider WithConfig(params (string Key, string Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        return new ServiceCollection().AddSingleton(config).BuildServiceProvider();
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(DeathFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public void Attach_WithoutConfiguration_RegistersRetailDefaults()
    {
        using WorldRuntime world = NewWorld();
        using ServiceProvider sp = new ServiceCollection().BuildServiceProvider();
        new DeathFeature(sp, NullLogger<DeathFeature>.Instance).Attach(world);
        DeathHooks hooks = DeathHooks.For(world);
        Assert.NotSame(DeathHooks.Default, hooks);
        Assert.True(hooks.Options.CorpseReclaimDelayPvP);
        Assert.True(hooks.Options.CorpseReclaimDelayPvE);
    }

    [Fact]
    public void Attach_BindsTheWorldDeathSection()
    {
        using WorldRuntime world = NewWorld();
        using ServiceProvider sp = WithConfig(("World:Death:CorpseReclaimDelayPvP", "false"));
        new DeathFeature(sp, NullLogger<DeathFeature>.Instance).Attach(world);
        DeathHooks hooks = DeathHooks.For(world);
        Assert.False(hooks.Options.CorpseReclaimDelayPvP);
        Assert.True(hooks.Options.CorpseReclaimDelayPvE);
    }

    [Fact]
    public void Attach_DoesNotReplaceHooksRegisteredFirst_AndAttachingTwiceThrows()
    {
        using WorldRuntime world = NewWorld();
        using ServiceProvider sp = new ServiceCollection().BuildServiceProvider();
        var other = new DeathHooks(new DeathOptions(), DeathClock.System);
        DeathHooks.Register(world, other);
        var feature = new DeathFeature(sp, NullLogger<DeathFeature>.Instance);
        feature.Attach(world);
        Assert.Same(other, DeathHooks.For(world));
        Assert.Throws<InvalidOperationException>(() => feature.Attach(world));
    }
}
