using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Combat;

/// <summary>S03: the world daemon binds the <c>Combat</c> rates, supplies combat's aura source and registers the refund.</summary>
public sealed class PowerFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider empty)
    {
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static ServiceProvider Services(params KeyValuePair<string, string?>[] settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(PowerFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_WithoutConfiguration_RegistersTheRetailRates_TheAuraSource_AndTheRefundObserver()
    {
        await using ServiceProvider sp = Services();
        using WorldRuntime world = NewWorld(sp);

        new PowerFeature(sp, NullLogger<PowerFeature>.Instance).Attach(world);

        CombatEnvironment environment = CombatEnvironment.For(world);
        Assert.Equal(1.0f, environment.Options.RateRageIncome);
        Assert.Equal(1.0f, environment.Options.RateRageLoss);
        Assert.Equal(1.0f, environment.Options.RateEnergy);
        Assert.Equal(1.0f, environment.Options.RateMana);
        Assert.IsType<SpellSystemPowerAuras>(environment.Auras);
        Assert.Single(sp.GetRequiredService<SpellFeature>().System.Observers.OfType<PowerRefundObserver>());
    }

    [Fact]
    public async Task Attach_BindsTheCombatSection_AndReplacesNegativeLossAndManaRates()
    {
        await using ServiceProvider sp = Services(
            new("Combat:RateRageIncome", "2.5"),
            new("Combat:RateRageLoss", "-1"),
            new("Combat:RateEnergy", "3"),
            new("Combat:RateMana", "0.5"));
        using WorldRuntime world = NewWorld(sp);

        new PowerFeature(sp, NullLogger<PowerFeature>.Instance).Attach(world);

        CombatOptions options = CombatEnvironment.For(world).Options;
        Assert.Equal(2.5f, options.RateRageIncome);
        Assert.Equal(1.0f, options.RateRageLoss);   // negative: vmangos setConfigPos falls back to the default
        Assert.Equal(3.0f, options.RateEnergy);
        Assert.Equal(0.5f, options.RateMana);
    }

    [Fact]
    public async Task WorldsWithoutTheFeature_KeepTheDefaultEnvironment()
    {
        await using ServiceProvider sp = Services();
        using WorldRuntime world = NewWorld(sp);

        Assert.Same(CombatEnvironment.Default, CombatEnvironment.For(world));
    }
}
