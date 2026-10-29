using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Tuning;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>Rows are the source of the auction bot values and loot; an existing key still overrides its rows and is reported (spec 11).</summary>
public sealed class AuctionBotTuningSourceTests
{
    private static TuningCatalog CatalogWith(Func<TuningContent, TuningContent> change) => TuningCatalog.Build(change(TuningDefaults.Content));

    [Fact]
    public void Keys_default_to_empty_so_the_rows_are_the_source()
    {
        var options = new AuctionBotOptions();
        string[] keys =
        [
            options.ValuePoor, options.ValueNormal, options.ValueUncommon, options.ValueRare, options.ValueEpic, options.ValueLegendary, options.ValueArtifact,
            options.LootCreatureNormal, options.LootCreatureRare, options.LootCreatureElite, options.LootCreatureRareElite, options.LootCreatureWorldBoss,
            options.LootDisenchant, options.LootFishing, options.LootGameobject, options.LootSkinning,
        ];
        Assert.All(keys, k => Assert.Equal(string.Empty, k));
        Assert.Empty(options.DeprecatedOverrides());
    }

    [Fact]
    public void Changed_rows_reach_the_pricing_and_the_loot_sources()
    {
        TuningCatalog catalog = CatalogWith(c => c with
        {
            Values = [.. c.Values.Select(v => v.Quality == 3 && v.ItemClass == 2 ? v with { Percent = 450 } : v)],
            Loot = [.. c.Loot.Select(l => l.Source == "fishing" ? l with { MinTemplates = 1, MaxTemplates = 2, MinRolls = 3, MaxRolls = 4 } : l)],
        });

        var pricing = new AuctionBotPricing(new AuctionBotOptions(), null, catalog);
        Assert.Equal(450u, pricing.ClassValue(new ItemTemplate { Entry = 1, Quality = 3, Class = 2 }));
        var sources = new AuctionBotLootSources(new LootContent([], []), _ => null, new AuctionBotOptions(), new Random(1), catalog);
        Assert.Equal([1, 2, 3, 4], sources.Sources.Single(s => s.Name == "fishing").Config);
    }

    [Fact]
    public void A_key_set_to_the_shipped_value_is_reported_as_identical()
    {
        var options = new AuctionBotOptions { ValueNormal = "100,100,0,100,0,100,100,100,0,100,0,100,100,100,0,100,100", LootSkinning = " 3, 5,50,50" };

        Assert.Equal(
            [
                "AuctionHouseBot:ValueNormal is set to a value identical to the shipped auction_bot_value row; the key is deprecated, remove it.",
                "AuctionHouseBot:LootSkinning is set to a value identical to the shipped auction_bot_loot row; the key is deprecated, remove it.",
            ],
            options.DeprecatedOverrides());
    }

    [Fact]
    public void A_key_that_is_set_overrides_its_row_and_is_reported()
    {
        TuningCatalog catalog = CatalogWith(c => c);
        var options = new AuctionBotOptions { ValueRare = "1,2,3", LootFishing = "7,8,9,10" };

        uint[][] values = options.ParseValues(catalog);

        Assert.Equal([1u, 2u, 3u], values[3][..3]);
        Assert.Equal(0u, values[3][3]);
        Assert.Equal(catalog.ValueMatrix()[4], values[4]);
        Assert.Equal([7, 8, 9, 10], options.ParseLoot("fishing", catalog));
        Assert.Equal(catalog.LootConfig("skinning"), options.ParseLoot("skinning", catalog));
        Assert.Equal(
            [
                "AuctionHouseBot:ValueRare is set and overrides the auction_bot_value rows; the key is deprecated, edit the rows (.reload tuning) and remove it.",
                "AuctionHouseBot:LootFishing is set and overrides the auction_bot_loot rows; the key is deprecated, edit the rows (.reload tuning) and remove it.",
            ],
            options.DeprecatedOverrides());
    }
}
