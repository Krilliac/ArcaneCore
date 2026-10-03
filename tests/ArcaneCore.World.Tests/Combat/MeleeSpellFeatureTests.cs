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

/// <summary>S08b: the melee swing link, and the one combat environment the combat features of a world share.</summary>
public sealed class MeleeSpellFeatureTests
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
        => Assert.Contains(typeof(MeleeSpellFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_LinksTheSwingToTheSpellSystem_AndBindsTheBlockingOption()
    {
        await using ServiceProvider sp = Services(new KeyValuePair<string, string?>("Combat:MeleeCastingBlocksSwing", "false"));
        using WorldRuntime world = NewWorld(sp);

        new MeleeSpellFeature(sp, NullLogger<MeleeSpellFeature>.Instance).Attach(world);

        CombatEnvironment environment = CombatEnvironment.For(world);
        Assert.IsType<SpellSystemMeleeHooks>(environment.MeleeSpells);
        Assert.False(environment.Options.MeleeCastingBlocksSwing);
    }

    [Fact]
    public async Task TheCombatFeaturesOfAWorld_ShareOneEnvironmentAndOneSetOfOptions()
    {
        await using ServiceProvider sp = Services(new KeyValuePair<string, string?>("Combat:RateRageIncome", "3"), new KeyValuePair<string, string?>("Combat:StanceShiftKeepsSelfBuffs", "true"));
        using WorldRuntime world = NewWorld(sp);
        var power = new PowerFeature(sp, NullLogger<PowerFeature>.Instance);
        var melee = new MeleeSpellFeature(sp, NullLogger<MeleeSpellFeature>.Instance);
        var stances = new StanceFeature(sp, NullLogger<StanceFeature>.Instance);

        power.Attach(world);
        melee.Attach(world);
        stances.Attach(world);

        CombatEnvironment environment = CombatEnvironment.For(world);
        Assert.NotNull(environment.Auras);
        Assert.NotNull(environment.MeleeSpells);
        Assert.Same(environment.Options, power.Options);
        Assert.Same(environment.Options, melee.Options);
        Assert.Same(environment.Options, stances.Options);
        Assert.Equal(3.0f, environment.Options.RateRageIncome);
        Assert.True(environment.Options.StanceShiftKeepsSelfBuffs);
    }

    [Fact]
    public void TheDefaultEnvironment_IsReadOnlyAndCannotBeRegistered()
    {
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.Auras = null);
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.MeleeSpells = null);
        Assert.True(CombatEnvironment.Default.Options.MeleeCastingBlocksSwing);
    }
}
