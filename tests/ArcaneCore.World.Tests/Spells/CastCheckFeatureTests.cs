using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>S16: the world daemon installs the generic cast checks.</summary>
public sealed class CastCheckFeatureTests
{
    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(CastCheckFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_InstallsTheFourGeneralChecks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);

        new CastCheckFeature(sp).Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.Single(spells.CastChecks.OfType<StandingCastCheck>());
        Assert.Single(spells.CastChecks.OfType<CombatRestrictionCastCheck>());
        Assert.Single(spells.CastChecks.OfType<StealthCastCheck>());
        Assert.Single(spells.CastChecks.OfType<FacingCastCheck>());
    }
}
