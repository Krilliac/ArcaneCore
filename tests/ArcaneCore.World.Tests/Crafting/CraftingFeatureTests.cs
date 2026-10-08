using ArcaneCore.Game.Crafting;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Npc;
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

        (int checks, bool tradeFilter) = await host.OnWorldAsync(() =>
        {
            var system = host.WorldServices.GetRequiredService<SpellFeature>().System;
            return (system.CastChecks.OfType<ReagentCastCheck>().Count(), system.ReagentTradeFilter is not null);
        });

        // The reagents are taken by the cast itself (SpellSystem.StageCastReagents); the feature adds the tool check and the trade filter.
        Assert.Equal(1, checks);
        Assert.True(tradeFilter);
        Assert.Equal(1, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.Observers.OfType<FirstAidObserver>().Count()));
        Assert.True(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.CreateItem)));
        Assert.Contains(await host.OnWorldAsync(() =>
            ArcaneCore.World.Tests.Npc.GossipScriptLayers.Of(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.GossipScript)),
            script => script is ProfessionSpecializationGossip);
    }

    [Fact]
    public async Task CraftingDisabled_InstallsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Config("false"));

        bool installed = await host.OnWorldAsync(() => ReagentRules.IsInstalled(host.WorldServices.GetRequiredService<SpellFeature>().System));

        Assert.False(installed);
        Assert.False(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.CreateItem)));
        Assert.IsNotType<ProfessionSpecializationGossip>(await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.GossipScript));
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
