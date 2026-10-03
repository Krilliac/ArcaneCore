using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Combat;

/// <summary>S07: the world daemon installs the aura state checks, the reactive observer and the per-map tick.</summary>
public sealed class ReactiveFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider empty)
    {
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(ReactiveFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_InstallsTheChecksAndTheObserver_AndNeedsTheComboFeatureOnlyWhenAMarkerIsUsed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        services.AddSingleton<ComboFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new ReactiveFeature(sp);

        feature.Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.Single(spells.CastChecks.OfType<CasterAuraStateCheck>());
        Assert.Single(spells.CastChecks.OfType<TargetHealthStateCheck>());
        Assert.Single(spells.Observers.OfType<ReactiveSpellObserver>());
        Assert.NotNull(feature.AuraStates);
        Assert.NotNull(feature.Reactives);

        // The combo point service is resolved at first use: attaching the combo feature afterwards is enough.
        sp.GetRequiredService<ComboFeature>().Attach(world);
        Assert.NotNull(sp.GetRequiredService<ComboFeature>().Service);
    }
}
