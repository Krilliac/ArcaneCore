using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Ranged;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Ranged;

/// <summary>The "Ranged" configuration section: retail defaults, binding, and delivery to the spell system.</summary>
public sealed class RangedFeatureTests
{
    [Fact]
    public void WithoutConfiguration_EverythingIsRetail()
    {
        RangedOptions options = RangedFeature.Bind(null);

        Assert.Equal(AmmoMode.Retail, options.Ammo.Mode);
        Assert.Equal(RangeLeewayMode.Retail, options.Range.Leeway);
    }

    [Fact]
    public void TheSectionOverridesTheDefaults_ByName()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ranged:Ammo:Mode"] = "Infinite",
            ["Ranged:Range:Leeway"] = "None",
        }).Build();

        RangedOptions options = RangedFeature.Bind(configuration);

        Assert.Equal(AmmoMode.Infinite, options.Ammo.Mode);
        Assert.Equal(RangeLeewayMode.None, options.Range.Leeway);
    }

    [Fact]
    public async Task TheSpellSystemUsesTheFeaturesOptions()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        RangedFeature feature = host.WorldServices.GetRequiredService<RangedFeature>();
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();

        Assert.Same(feature.Options, spells.System.RangedOptions);
        Assert.Equal(AmmoMode.Retail, spells.System.RangedOptions.Ammo.Mode);
        Assert.True(spells.System.HasAuraHandler(AuraType.TrackCreatures));
        Assert.True(spells.System.HasAuraHandler(AuraType.ModStalked));
    }
}
