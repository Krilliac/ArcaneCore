using ArcaneCore.Game.Entities;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
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
}
