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

/// <summary>S09: the world daemon installs combo points on the spell system.</summary>
public sealed class ComboFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider empty)
    {
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(ComboFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_InstallsTheComboPieces_OnTheSpellSystem()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new ComboFeature(sp);

        feature.Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.NotNull(feature.Service);
        Assert.Single(spells.CastChecks.OfType<ComboPointCastCheck>());
        Assert.Single(spells.ValueModifiers.OfType<ComboValueModifier>());
        Assert.Single(spells.Observers.OfType<ComboFinishObserver>());
        Assert.True(spells.HasEffectHandler(SpellEffectName.AddComboPoints));
    }
}
