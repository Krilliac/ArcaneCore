using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting.Modules;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Scripting;
using ArcaneCore.World.Scripting.Modules.DuelReset;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Scripting;

/// <summary>Module discovery, the Modules:&lt;Name&gt;:Enabled switch and .reload config (docs/integration/script-hooks.md).</summary>
public sealed class ScriptHooksFeatureTests
{
    private static (ServiceProvider Services, WorldRuntime World) NewWorld(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return (sp, new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance));
    }

    [Fact]
    public void FeatureAndDuelResetModuleAreDiscovered()
    {
        Assert.Contains(typeof(ScriptHooksFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(DuelResetModule), ScriptHooksFeature.ModuleTypes);
    }

    [Fact]
    public async Task ByDefault_NoModuleLoads_AndNoHookIsRegistered()
    {
        (ServiceProvider sp, WorldRuntime world) = NewWorld([]);
        await using (sp)
        using (world)
        {
            var feature = new ScriptHooksFeature(sp);
            feature.Attach(world);

            Assert.Empty(feature.Loaded);
            Assert.Empty(world.Scripts.Registered);
        }
    }

    [Fact]
    public async Task Enabled_LoadsDuelReset_WithItsSettings_AndReloadAppliesNewOnes()
    {
        (ServiceProvider sp, WorldRuntime world) = NewWorld(new()
        {
            ["Modules:DuelReset:Enabled"] = "true",
            ["Modules:DuelReset:CooldownAge"] = "45",
            ["Modules:DuelReset:Zones"] = "",
        });
        await using (sp)
        using (world)
        {
            var feature = new ScriptHooksFeature(sp);
            feature.Attach(world);

            DuelResetModule module = Assert.IsType<DuelResetModule>(Assert.Single(feature.Loaded));
            DuelResetScript script = Assert.IsType<DuelResetScript>(Assert.Single(world.Scripts.Registered));
            Assert.Same(module.Script, script);
            Assert.Equal(45u, script.Settings.CooldownAge);
            Assert.True(script.Settings.HealthMana);

            IConfiguration reloaded = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:DuelReset:Enabled"] = "true",
                ["Modules:DuelReset:HealthMana"] = "false",
            }).Build();
            feature.ReloadConfig(reloaded);

            Assert.False(script.Settings.HealthMana);
            Assert.Equal(30u, script.Settings.CooldownAge);
        }
    }
}
