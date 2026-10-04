using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.SpellMods;

/// <summary>The spell-modifier world feature: option binding, discovery order, and the installed engine.</summary>
public sealed class SpellModFeatureTests
{
    [Fact]
    public void BindOptions_ReadsTheSection_AndKeepsTheRetailDefaultsOtherwise()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spells:Mods:Enabled"] = "false",
            ["Spells:Mods:HardcodedWardMods"] = "false",
            ["Spells:Mods:CustomCharges"] = "false",
            ["Spells:Mods:ReapplyPassives"] = "false",
            ["Spells:Mods:InstantCastKeepsFlatCastTimeCharge"] = "false",
            ["Spells:Mods:SendClientModifiers"] = "false",
            ["Spells:Mods:ClassMaskFile"] = "masks.txt",
        }).Build();
        var bound = new SpellModOptions();
        var defaults = new SpellModOptions();

        SpellModFeature.BindOptions(configuration, bound);
        SpellModFeature.BindOptions(null, defaults);

        Assert.Equal((false, false, false, false, false, false, "masks.txt"),
            (bound.Enabled, bound.HardcodedWardMods, bound.CustomCharges, bound.ReapplyPassives, bound.InstantCastKeepsFlatCastTimeCharge, bound.SendClientModifiers, bound.ClassMaskFile));
        Assert.Equal((true, true, true, true, true, true, (string?)null),
            (defaults.Enabled, defaults.HardcodedWardMods, defaults.CustomCharges, defaults.ReapplyPassives, defaults.InstantCastKeepsFlatCastTimeCharge, defaults.SendClientModifiers, defaults.ClassMaskFile));
    }

    private static (ServiceProvider Provider, WorldRuntime World) Host(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<SpellFeature>();
        ServiceProvider provider = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        return (provider, world);
    }

    [Fact]
    public async Task Attach_BindsTheSection_AndLoadsTheMaskFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-mods-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# test\n77 0 0x0000010000000001\n");
        try
        {
            (ServiceProvider provider, WorldRuntime world) = Host(new()
            {
                ["Spells:Mods:ClassMaskFile"] = path,
                ["Spells:Mods:SendClientModifiers"] = "false",
            });
            await using (provider)
            using (world)
            {
                new SpellModFeature(provider, NullLogger<SpellModFeature>.Instance).Attach(world);

                ISpellModEngine engine = provider.GetRequiredService<SpellFeature>().System.Mods;
                Assert.False(engine.Options.SendClientModifiers);
                Assert.Equal(0x0000010000000001UL, engine.MaskSource!.TryGetMask(77, 0));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Attach_FailsStartup_OnAMalformedOrMissingMaskFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-mods-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "77 0 not-a-mask\n");
        try
        {
            (ServiceProvider provider, WorldRuntime world) = Host(new() { ["Spells:Mods:ClassMaskFile"] = path });
            await using (provider)
            using (world)
            {
                Assert.Throws<InvalidDataException>(() => new SpellModFeature(provider, NullLogger<SpellModFeature>.Instance).Attach(world));
            }

            (ServiceProvider provider2, WorldRuntime world2) = Host(new() { ["Spells:Mods:ClassMaskFile"] = path + ".missing" });
            await using (provider2)
            using (world2)
            {
                Assert.Throws<FileNotFoundException>(() => new SpellModFeature(provider2, NullLogger<SpellModFeature>.Instance).Attach(world2));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Attach_WithoutAMaskFile_KeepsTheDbcMasks()
    {
        (ServiceProvider provider, WorldRuntime world) = Host([]);
        await using (provider)
        using (world)
        {
            new SpellModFeature(provider, NullLogger<SpellModFeature>.Instance).Attach(world);

            Assert.Null(provider.GetRequiredService<SpellFeature>().System.Mods.MaskSource);
        }
    }

    [Fact]
    public void TheFeature_AttachesAfterTheSpellFeature()
    {
        List<Type> features = [.. WorldFeatures.FeatureTypes];

        Assert.True(features.IndexOf(typeof(SpellModFeature)) > features.IndexOf(typeof(SpellFeature)));
    }

    [Fact]
    public async Task TheWorldHost_RunsTheEngine_OnTheSpellSystem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MODPLAYER", "Modplayer");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Modplayer")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            Assert.Same(spells.System.Mods, spells.System.SpellModifiers);
            Assert.True(spells.System.HasAuraHandler(AuraType.AddFlatModifier));
            Assert.True(spells.System.HasAuraHandler(AuraType.AddPctModifier));
            Assert.True(spells.System.Mods.Options.Enabled);
        });
    }
}
