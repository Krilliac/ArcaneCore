using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// Pins the effective default value and loot matrices of the auction house bot (the cMaNGOS ahbot.conf.dist.in defaults) through the
/// surfaces consumers use (<see cref="AuctionBotPricing.ClassValue"/> and <see cref="AuctionBotLootSources.Sources"/>), so they hold
/// whether the numbers come from the config strings or from the tuning rows.
/// </summary>
public sealed class AuctionBotTuningPinTests
{
    /// <summary>Percent of the vendor price by quality (rows) and item class 0..16 (columns).</summary>
    private static readonly uint[][] ExpectedValues =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [100, 100, 0, 100, 0, 100, 100, 100, 0, 100, 0, 100, 100, 100, 0, 100, 100],
        [200, 200, 200, 200, 200, 200, 200, 200, 0, 200, 0, 200, 200, 200, 0, 200, 200],
        [400, 400, 400, 400, 400, 400, 400, 400, 0, 400, 0, 400, 400, 400, 0, 400, 400],
        [800, 800, 800, 800, 800, 800, 800, 800, 0, 800, 0, 800, 800, 800, 0, 800, 800],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    ];

    private static readonly (string Source, int[] Config)[] ExpectedLoot =
    [
        ("creature normal", [30, 35, 8, 12]),
        ("creature elite", [30, 34, 1, 2]),
        ("creature rare elite", [-10, 2, 1, 1]),
        ("creature world boss", [-20, 1, 1, 1]),
        ("creature rare", [0, 10, 1, 1]),
        ("disenchant", [10, 12, 1, 1]),
        ("fishing", [3, 5, 30, 40]),
        ("gameobject", [13, 16, 7, 11]),
        ("skinning", [3, 5, 50, 50]),
    ];

    [Fact]
    public void Default_value_matrix_is_pinned_through_the_pricing()
    {
        var pricing = new AuctionBotPricing(new AuctionBotOptions());
        for (uint quality = 0; quality < AuctionBotOptions.QualityCount; quality++)
        {
            for (uint itemClass = 0; itemClass < AuctionBotOptions.ItemClassCount; itemClass++)
            {
                var template = new ItemTemplate { Entry = 1, Quality = quality, Class = itemClass };
                Assert.True(ExpectedValues[quality][itemClass] == pricing.ClassValue(template), $"quality {quality} class {itemClass}");
            }
        }
    }

    [Fact]
    public void Default_loot_matrix_is_pinned_through_the_loot_sources()
    {
        var sources = new AuctionBotLootSources(new LootContent([], []), _ => null, new AuctionBotOptions(), new Random(1));
        Assert.Equal(ExpectedLoot.Length, sources.Sources.Count);
        foreach ((string name, int[] config) in ExpectedLoot)
        {
            Assert.Equal(config, sources.Sources.Single(s => s.Name == name).Config);
        }
    }
}
