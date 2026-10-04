using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The <c>Auras</c> options section: retail defaults, and the feature attaches after the spell feature that creates the system.</summary>
public sealed class AuraOptionsBindingTests
{
    [Fact]
    public void BindOptions_KeepsTheRetailDefaults_AndReadsTheSection()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auras:PeriodicCatchUp"] = "true",
        }).Build();

        Assert.False(SpellRulesAuraEngineFeature.BindOptions(null).PeriodicCatchUp);
        Assert.False(new AuraOptions().PeriodicCatchUp);
        Assert.True(SpellRulesAuraEngineFeature.BindOptions(configuration).PeriodicCatchUp);
    }

    [Fact]
    public void TheAuraFeature_AttachesAfterTheSpellFeature()
    {
        List<Type> features = [.. WorldFeatures.FeatureTypes];

        Assert.True(features.IndexOf(typeof(SpellRulesAuraEngineFeature)) > features.IndexOf(typeof(SpellFeature)));
        Assert.True(features.IndexOf(typeof(SpellFeature)) >= 0);
    }
}
