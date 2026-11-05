using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Ops.Cli;
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

    [Theory]
    [InlineData(AuctionHouseRules.AllianceHouse, 12u, Team.Alliance)]
    [InlineData(AuctionHouseRules.HordeHouse, 29u, Team.Horde)]
    [InlineData(AuctionHouseRules.NeutralHouse, 21u, Team.Alliance)]
    public void House_ConfiguredEntryForADefaultHouse_WinsOverTheDefault(uint house, uint auctioneerFaction, Team team)
    {
        var options = new EconomyOptions();
        AuctionHouseEntry before = AuctionHouseRules.House(options, house)!;
        Config(("Economy:AuctionHouses:0:Id", house.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Economy:AuctionHouses:0:DepositPercent", "1"), ("Economy:AuctionHouses:0:CutPercent", "2"))
            .GetSection("Economy").Bind(options);

        var configured = new AuctionHouseEntry(house, 1, 2);
        Assert.NotEqual(before, configured);
        Assert.Equal(configured, AuctionHouseRules.House(options, house));
        Assert.Equal(configured, AuctionHouseRules.HouseFor(options, auctioneerFaction, team));
    }

    [Fact]
    public void House_WithNothingConfigured_IsTheDefaultEntry_AndAnUnknownIdIsNull()
    {
        var options = new EconomyOptions();
        Assert.Equal(new AuctionHouseEntry(AuctionHouseRules.NeutralHouse, 25, 15), AuctionHouseRules.House(options, AuctionHouseRules.NeutralHouse));
        Assert.Null(AuctionHouseRules.House(options, 99));
    }

    [Fact]
    public void Check_NoEntriesOrCompleteEntries_ReportsNothing()
    {
        Assert.Empty(new EconomyConfigChecks().Check(Config()));
        Assert.Empty(new EconomyConfigChecks().Check(Config(("Economy:AuctionHouses:0:Id", "7"), ("Economy:AuctionHouses:0:DepositPercent", "1"), ("Economy:AuctionHouses:0:CutPercent", "2"))));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("DepositPercent")]
    [InlineData("CutPercent")]
    public void Check_EntryMissingAField_IsAnErrorNamingTheKey(string missing)
    {
        (string Key, string Value)[] keys = [.. new[] { "Id", "DepositPercent", "CutPercent" }.Where(f => f != missing).Select(f => ("Economy:AuctionHouses:3:" + f, "5"))];
        ConfigIssue issue = Assert.Single(new EconomyConfigChecks().Check(Config(keys)));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal("Economy:AuctionHouses:3:" + missing, issue.Key);
    }

    [Theory]
    [InlineData("many")]
    [InlineData("-1")]
    [InlineData("4294967296")]
    public void Check_FieldTheBinderCannotRead_IsAnError(string text)
    {
        ConfigIssue issue = Assert.Single(new EconomyConfigChecks().Check(Config(("Economy:AuctionHouses:0:Id", "7"), ("Economy:AuctionHouses:0:DepositPercent", text), ("Economy:AuctionHouses:0:CutPercent", "2"))));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal("Economy:AuctionHouses:0:DepositPercent", issue.Key);

        var list = new List<AuctionHouseEntry>();
        Config(("AuctionHouses:0:Id", "7"), ("AuctionHouses:0:DepositPercent", text), ("AuctionHouses:0:CutPercent", "2")).GetSection("AuctionHouses").Bind(list);
        Assert.Empty(list);
    }

    /// <summary>The check is on the daemon's list, so the report that fails the start (Program.cs, exit 78) carries the error.</summary>
    [Fact]
    public void Check_RunsInTheStartupReport_AndMakesItInvalid()
    {
        Assert.Contains(typeof(EconomyConfigChecks), ConfigCheckDiscovery.ConcreteTypes(OpsCli.CheckAssemblies));
        ConfigReport report = OpsCli.Validate(Config(("Economy:AuctionHouses:0:Id", "7")));
        Assert.True(report.IsInvalid);
        Assert.Contains(report.Issues, i => i.Severity == ConfigSeverity.Error && i.Key == "Economy:AuctionHouses:0:CutPercent");
    }
}
