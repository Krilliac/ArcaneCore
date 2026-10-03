using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The world daemon installs the form and mount cast checks.</summary>
public sealed class FormMountCheckFeatureTests
{
    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(FormMountCheckFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_InstallsTheThreeChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);

        new FormMountCheckFeature(sp).Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.Single(spells.CastChecks.OfType<MountFormCastCheck>());
        Assert.Single(spells.CastChecks.OfType<DismountOnCastCheck>());
        Assert.Single(spells.CastChecks.OfType<ShiftedOrMountedTargetCastCheck>());
    }
}
