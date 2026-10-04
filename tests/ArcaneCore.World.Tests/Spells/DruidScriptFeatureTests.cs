using ArcaneCore.Game.Spells;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The world daemon installs the druid finisher scripts after the combo point feature.</summary>
public sealed class DruidScriptFeatureTests
{
    [Fact]
    public void IsDiscoveredAsAWorldFeature_AfterTheComboFeatureInAttachOrder()
    {
        Assert.Contains(typeof(DruidScriptFeature), WorldFeatures.FeatureTypes);
        Assert.True(Array.IndexOf(WorldFeatures.FeatureTypes.ToArray(), typeof(ComboFeature)) < Array.IndexOf(WorldFeatures.FeatureTypes.ToArray(), typeof(DruidScriptFeature)));
    }

    [Fact]
    public async Task Attach_RegistersTheScriptsAsAModifierAndAnObserver_AfterTheComboModifier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        services.AddSingleton<ComboFeature>();
        services.AddSingleton<DruidScriptFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);
        sp.GetRequiredService<ComboFeature>().Attach(world);

        var feature = sp.GetRequiredService<DruidScriptFeature>();
        feature.Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.NotNull(feature.Scripts);
        Assert.Contains(feature.Scripts, spells.ValueModifiers);
        Assert.Contains(feature.Scripts, spells.Observers);
        int combo = spells.ValueModifiers.ToList().FindIndex(m => m is ArcaneCore.Game.Combat.ComboValueModifier);
        Assert.True(combo >= 0 && combo < spells.ValueModifiers.ToList().IndexOf(feature.Scripts));
    }

    [Fact]
    public async Task Attach_WithoutTheComboFeatureAttached_FailsClearly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        services.AddSingleton<ComboFeature>();
        services.AddSingleton<DruidScriptFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<DruidScriptFeature>().Attach(world));
    }
}
