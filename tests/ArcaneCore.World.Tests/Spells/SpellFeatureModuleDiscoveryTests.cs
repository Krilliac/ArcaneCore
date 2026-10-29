using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <see cref="SpellFeature"/> applies the handler modules of the whole code set at attach (pack-aware <see cref="SpellHandlerModules.Discover(CodeAssemblies)"/>),
/// not only the Game assembly's: a module in another assembly gets its handler installed, and the default set is the startup set of the world.
/// </summary>
public sealed class SpellFeatureModuleDiscoveryTests
{
    private static async Task<SpellSystem> AttachAsync(CodeAssemblies? moduleAssemblies)
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance, moduleAssemblies: moduleAssemblies))
            .AddSingleton(sp => new TeleportFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<TeleportFeature>.Instance))
            .BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        SpellFeature feature = services.GetRequiredService<SpellFeature>();
        feature.Attach(world);
        try
        {
            return feature.System;
        }
        finally
        {
            await feature.DisposeAsync();
        }
    }

    [Fact]
    public async Task Attach_AppliesAModuleOfAPackAssembly()
    {
        SpellSystem system = await AttachAsync(CodeAssemblies.Of(typeof(SpellSystem).Assembly).WithPack(typeof(PackProbeSpellModule).Assembly));

        Assert.True(system.HasEffectHandler(PackProbeSpellModule.ProbeEffect));
        Assert.Contains(typeof(PackProbeSpellModule), system.Modules);
    }

    [Fact]
    public async Task Attach_WithoutASet_UsesTheStartupSetOfTheWorld_AndAppliesTheModulesOfThatSet()
    {
        SpellSystem system = await AttachAsync(null);

        Assert.False(system.HasEffectHandler(PackProbeSpellModule.ProbeEffect)); // TestSupport is not in the startup set
        foreach (Type module in SpellHandlerModules.Discover(WorldCodeAssemblies.Startup))
        {
            Assert.Contains(module, system.Modules);
        }
    }
}
