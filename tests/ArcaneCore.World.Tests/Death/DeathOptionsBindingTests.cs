using ArcaneCore.Game.Death;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>The World:Death options: retail defaults (vmangos mangosd.conf.dist), bound from configuration.</summary>
public sealed class DeathOptionsBindingTests
{
    [Fact]
    public void Defaults_AreTheRetailValues()
    {
        var options = new DeathOptions();

        Assert.True(options.CorpseReclaimDelayPvP);
        Assert.True(options.CorpseReclaimDelayPvE);
        Assert.False(options.GraveyardFallbackToDefaults); // the mangos-classic fallback is a deviation: off
        Assert.Equal(11, options.SicknessLevel);           // Death.SicknessLevel
        Assert.True(options.GhostFormAura);
    }

    [Fact]
    public async Task TheDaemon_BindsThemFromTheWorldDeathSection()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Death:GraveyardFallbackToDefaults"] = "true",
            ["World:Death:SicknessLevel"] = "-10",
            ["World:Death:GhostFormAura"] = "false",
            ["World:Death:CorpseReclaimDelayPvE"] = "false",
        }).Build();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(configuration));

        DeathOptions options = await host.OnWorldAsync(() => DeathHooks.For(host.World).Options);

        Assert.True(options.GraveyardFallbackToDefaults);
        Assert.Equal(-10, options.SicknessLevel);
        Assert.False(options.GhostFormAura);
        Assert.False(options.CorpseReclaimDelayPvE);
        Assert.True(options.CorpseReclaimDelayPvP); // untouched
    }
}
