using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The world daemon installs the form-bound passive gate and feeds it the combat environment's form table.</summary>
public sealed class PassiveFormGateFeatureTests
{
    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(PassiveFormGateFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_InstallsExactlyOneGate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);

        new PassiveFormGateFeature(sp).Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.Single(spells.CastChecks.OfType<PassiveFormCastCheck>());
        CombatEnvironment.GetOrCreate(world, () => new CombatOptions()).ShapeshiftForms = ShapeshiftFormCatalog.Retail;   // the lookup is lazy: no ordering with the stance feature
    }
}
