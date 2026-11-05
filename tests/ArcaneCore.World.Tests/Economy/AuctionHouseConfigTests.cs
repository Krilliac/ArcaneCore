using ArcaneCore.Game.Economy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

/// <summary>
/// What the configuration binder really does with <c>Economy:AuctionHouses</c>. The entry type is a positional record, so the binder builds it through its
/// constructor, and <c>EconomyFeature.Attach</c> calls plain <c>Bind</c> (no <c>ErrorOnUnknownConfiguration</c>).
/// </summary>
public sealed class AuctionHouseConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] keys)
        => new ConfigurationBuilder().AddInMemoryCollection(keys.Select(k => new KeyValuePair<string, string?>(k.Key, k.Value))).Build();

    /// <summary>The proof behind the page: a missing constructor field does not throw and does not leave zero; the whole entry is dropped.</summary>
    [Theory]
    [InlineData("Id")]
    [InlineData("DepositPercent")]
    [InlineData("CutPercent")]
    public void Bind_EntryMissingAField_IsDroppedWithoutAnError(string missing)
    {
        (string Key, string Value)[] keys = [.. new[] { ("Id", "9"), ("DepositPercent", "1"), ("CutPercent", "2") }
            .Where(k => k.Item1 != missing).Select(k => ("AuctionHouses:0:" + k.Item1, k.Item2))];
        var list = new List<AuctionHouseEntry>();
        Config(keys).GetSection("AuctionHouses").Bind(list);
        Assert.Empty(list);

        var options = new EconomyOptions();
        int defaults = options.AuctionHouses.Count;
        Config([.. keys.Select(k => ("Economy:" + k.Key, k.Value))]).GetSection("Economy").Bind(options);
        Assert.Equal(defaults, options.AuctionHouses.Count);
    }

    [Fact]
    public void Bind_CompleteEntry_IsAppendedAfterTheDefaultEntries()
    {
        var options = new EconomyOptions();
        Config(("Economy:AuctionHouses:0:Id", "7"), ("Economy:AuctionHouses:0:DepositPercent", "1"), ("Economy:AuctionHouses:0:CutPercent", "2")).GetSection("Economy").Bind(options);
        Assert.Equal(4, options.AuctionHouses.Count);
        Assert.Equal(new AuctionHouseEntry(7, 1, 2), options.AuctionHouses[^1]);
    }
}
