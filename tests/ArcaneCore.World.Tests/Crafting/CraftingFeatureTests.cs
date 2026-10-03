using ArcaneCore.Game.Crafting;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>Crafting lane: the feature is discovered, installs the reagent pair once, and honours Crafting:Enabled.</summary>
public sealed class CraftingFeatureTests
{
    private static Action<IServiceCollection> Config(string? enabled) => services =>
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { [CraftingFeature.EnabledKey] = enabled })
            .Build());

    [Fact]
    public async Task TheFeatureIsDiscovered_AndInstallsTheReagentPairOnTheSpellSystem()
    {
        Assert.Contains(typeof(CraftingFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Config(null));

        (int checks, int takers) = await host.OnWorldAsync(() =>
        {
            var system = host.WorldServices.GetRequiredService<SpellFeature>().System;
            return (system.CastChecks.OfType<ReagentCastCheck>().Count(), system.CostTakers.OfType<ReagentCostTaker>().Count());
        });

        Assert.Equal(1, checks);
        Assert.Equal(1, takers);
    }

    [Fact]
    public async Task CraftingDisabled_InstallsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Config("false"));

        bool installed = await host.OnWorldAsync(() => ReagentRules.IsInstalled(host.WorldServices.GetRequiredService<SpellFeature>().System));

        Assert.False(installed);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("garbage", true)]
    public void IsEnabled_DefaultsToRetail(string? value, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [CraftingFeature.EnabledKey] = value }).Build();

        Assert.Equal(expected, CraftingFeature.IsEnabled(configuration));
    }
}
