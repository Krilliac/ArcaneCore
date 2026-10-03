using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Diminishing;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.SpellRules;

/// <summary>The spell-rule world features attach after the spell feature and wire their seams.</summary>
public sealed class SpellRulesFeatureTests
{
    [Fact]
    public void EverySpellRulesFeature_AttachesAfterTheSpellFeature()
    {
        IReadOnlyList<Type> features = WorldFeatures.FeatureTypes;
        int spell = features.ToList().IndexOf(typeof(SpellFeature));
        List<Type> rules = [.. features.Where(t => t.Name.StartsWith("SpellRules", StringComparison.Ordinal))];

        Assert.True(spell >= 0);
        Assert.NotEmpty(rules);
        Assert.All(rules, t => Assert.True(features.ToList().IndexOf(t) > spell, $"{t.Name} must attach after SpellFeature"));
    }

    [Fact]
    public async Task CcFeature_InstallsTheLootReleaseSeam_AndReleasingWithoutALootWindowIsHarmless()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CCPLAYER", "Ccplayer");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ccplayer")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            Assert.NotNull(spells.System.ReleaseLoot);
            spells.System.ReleaseLoot!(player);
        });
    }

    [Fact]
    public void BindOptions_ReadsTheSpellRulesSection_AndKeepsTheRetailDefaultsOtherwise()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SpellRules:MagicHitFloorPercent"] = "1",
            ["SpellRules:WorldBossLevelDiff"] = "2",
            ["SpellRules:IgnoreHolyResistance"] = "true",
            ["SpellRules:DiminishingReturns"] = "false",
            ["SpellRules:DiminishingResetMs"] = "9000",
        }).Build();

        SpellRuleOptions bound = SpellRulesCoreFeature.BindOptions(configuration);
        SpellRuleOptions defaults = SpellRulesCoreFeature.BindOptions(null);

        Assert.Equal((1f, 2, true, false, 9000u), (bound.MagicHitFloorPercent, bound.WorldBossLevelDiff, bound.IgnoreHolyResistance, bound.DiminishingReturns, bound.DiminishingResetMs));
        Assert.Equal((22f, 3, false, true, 15_000u), (defaults.MagicHitFloorPercent, defaults.WorldBossLevelDiff, defaults.IgnoreHolyResistance, defaults.DiminishingReturns, defaults.DiminishingResetMs));
        Assert.False(defaults.CreatureSpellCrit);
    }

    [Fact]
    public async Task CoreFeature_InstallsTheRetailRulesAndTheApplicationRules()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("RULESPLAYER", "Rulesplayer");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Rulesplayer")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            var vanilla = Assert.IsType<VanillaSpellCombatRules>(spells.System.CombatRules);
            Assert.Equal(22f, vanilla.Options.MagicHitFloorPercent);
            Assert.Equal(2, spells.System.ApplicationRules.Count);
            Assert.IsType<MechanicResistRule>(spells.System.ApplicationRules[0]);
            Assert.IsType<DiminishingRule>(spells.System.ApplicationRules[1]);
        });
    }
}
