using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.World.Crafting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>Crafting lane: the enchanting feature loads its catalog, attaches the engine to a loading player and honours its switches.</summary>
public sealed class EnchantingFeatureTests
{
    private static EnchantCatalog Catalog() => new([new SpellItemEnchantment(7, [5, 0, 0], [3, 0, 0], [4, 0, 0], "Test", 0, 0)]);

    private static Action<IServiceCollection> Services(EnchantCatalog? catalog, string? enabled = null) => services =>
    {
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["Enchanting:Enabled"] = enabled })
            .Build());
        if (catalog is not null)
        {
            services.AddSingleton(catalog);
        }
    };

    [Fact]
    public async Task TheFeatureIsDiscovered_WithACatalog_ItIsActive_AndALoadingPlayerGetsTheEngine()
    {
        Assert.Contains(typeof(EnchantingFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(Catalog()));
        EnchantingFeature feature = host.WorldServices.GetRequiredService<EnchantingFeature>();

        await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT1", "Disenchanter");

        Assert.True(feature.IsActive);
        Assert.Equal(1, feature.Catalog.Count);
        Assert.True(await host.PlayerStateAsync("Disenchanter", p => p.Enchantments is not null));
    }

    [Fact]
    public async Task WithoutACatalog_TheFeatureIsInactive_AndNoPlayerGetsAnEngine()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(null));

        await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT2", "Plainwalker");

        Assert.False(host.WorldServices.GetRequiredService<EnchantingFeature>().IsActive);
        Assert.True(await host.PlayerStateAsync("Plainwalker", p => p.Enchantments is null));
    }

    [Fact]
    public async Task Disabled_TheFeatureLoadsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(Catalog(), enabled: "false"));

        Assert.False(host.WorldServices.GetRequiredService<EnchantingFeature>().IsActive);
        await Task.CompletedTask;
    }

    [Fact]
    public void AConfiguredButMissingDbc_RefusesStartup()
    {
        Action<IServiceCollection> services = s => s.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Enchanting:SpellItemEnchantmentDbcPath"] = Path.Combine(Path.GetTempPath(), "missing-enchant.dbc") })
            .Build());

        Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: services));
    }
}
