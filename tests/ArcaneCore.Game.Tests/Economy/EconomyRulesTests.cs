using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>Auction pricing, house selection, search and letter rules (vmangos behavior).</summary>
public sealed class EconomyRulesTests
{
    private static readonly AuctionHouseEntry Faction = new(AuctionHouseRules.AllianceHouse, 15, 5);
    private static readonly AuctionHouseEntry Neutral = new(AuctionHouseRules.NeutralHouse, 75, 15);

    [Theory]
    [InlineData(120u, 1u, 15u)]     // 2 h: one deposit unit
    [InlineData(480u, 4u, 60u)]     // 8 h: four units
    [InlineData(1440u, 12u, 180u)]  // 24 h: twelve units
    public void Deposit_ScalesWithDurationCountAndSellPrice(uint minutes, uint units, uint expected)
    {
        Assert.Equal(expected, AuctionHouseRules.Deposit(Faction, sellPrice: 100, count: 1, minutes));
        Assert.Equal(expected * 3, AuctionHouseRules.Deposit(Faction, 100, 3, minutes));
        Assert.Equal(units * 75, AuctionHouseRules.Deposit(Neutral, 100, 1, minutes));
    }

    [Fact]
    public void Deposit_HonorsTheMinimum_AndNeverOverflows()
    {
        Assert.Equal(10u, AuctionHouseRules.Deposit(Faction, 0, 1, 120, minimum: 10));
        Assert.Equal(uint.MaxValue, AuctionHouseRules.Deposit(Neutral, uint.MaxValue, 255, 1440));
    }

    [Fact]
    public void CutAndOutbid_FollowVmangos()
    {
        Assert.Equal(5u, AuctionHouseRules.Cut(Faction, 100));
        Assert.Equal(15u, AuctionHouseRules.Cut(Neutral, 100));
        Assert.Equal(0u, AuctionHouseRules.Cut(Faction, 19));
        Assert.Equal(1u, AuctionHouseRules.OutBid(1));          // at least one copper
        Assert.Equal(1u, AuctionHouseRules.OutBid(99));
        Assert.Equal(5u, AuctionHouseRules.OutBid(100));
        Assert.Equal(5u, AuctionHouseRules.OutBid(199));        // whole percents only
        Assert.Equal(50u, AuctionHouseRules.OutBid(1000));
    }

    [Fact]
    public void MinimumBid_IsStartBidWithoutBidder_ElseBidPlusOutbid()
    {
        var auction = new AuctionRecord { StartBid = 70 };
        Assert.Equal(70u, AuctionHouseRules.MinimumBid(auction));
        Assert.Equal(105u, AuctionHouseRules.MinimumBid(auction with { BidderId = 3, Bid = 100 }));
        Assert.Equal(uint.MaxValue, AuctionHouseRules.MinimumBid(auction with { BidderId = 3, Bid = uint.MaxValue }));
    }

    [Fact]
    public void HouseFor_GoblinFactionsAreNeutral_OthersFollowTheTeam()
    {
        var options = new EconomyOptions();
        Assert.Equal(AuctionHouseRules.NeutralHouse, AuctionHouseRules.HouseFor(options, 21, Team.Alliance)!.Id);
        Assert.Equal(AuctionHouseRules.NeutralHouse, AuctionHouseRules.HouseFor(options, 369, Team.Horde)!.Id);
        Assert.Equal(AuctionHouseRules.NeutralHouse, AuctionHouseRules.HouseFor(options, 577, Team.Horde)!.Id);
        Assert.Equal(AuctionHouseRules.AllianceHouse, AuctionHouseRules.HouseFor(options, 12, Team.Alliance)!.Id);
        Assert.Equal(AuctionHouseRules.HordeHouse, AuctionHouseRules.HouseFor(options, 29, Team.Horde)!.Id);
        options.AuctionHouses.Clear();
        Assert.Null(AuctionHouseRules.HouseFor(options, 12, Team.Alliance));
    }

    [Fact]
    public void Search_FiltersAndPages()
    {
        ItemTemplate[] templates =
        [
            new() { Entry = 1, Name = "Linen Cloth", Class = 7, SubClass = 0, Quality = 1, RequiredLevel = 0 },
            new() { Entry = 2, Name = "Wool Cloth", Class = 7, SubClass = 0, Quality = 1, RequiredLevel = 0 },
            new() { Entry = 3, Name = "Blade of Cloth", Class = 2, SubClass = 7, Quality = 3, RequiredLevel = 20, InventoryType = 13 },
        ];
        ItemTemplate? Find(uint e) => templates.FirstOrDefault(t => t.Entry == e);
        List<AuctionRecord> auctions = [.. Enumerable.Range(1, 120).Select(i => new AuctionRecord { Id = (uint)i, ItemEntry = (uint)((i % 3) + 1) })];
        auctions.Add(new AuctionRecord { Id = 500, ItemEntry = 999 }); // unknown template: never listed
        AuctionQuery all = new(0, "", 0, 0, AuctionSearch.Any, AuctionSearch.Any, AuctionSearch.Any, AuctionSearch.Any, false);

        (IReadOnlyList<AuctionRecord> page, int total) = AuctionSearch.Run(auctions, all, Find);
        Assert.Equal(120, total);
        Assert.Equal(AuctionHouseRules.PageSize, page.Count);
        Assert.Equal(1u, page[0].Id);
        (page, total) = AuctionSearch.Run(auctions, all with { ListFrom = 100 }, Find);
        Assert.Equal((20, 120), (page.Count, total));
        (page, _) = AuctionSearch.Run(auctions, all with { ListFrom = 1000 }, Find);
        Assert.Empty(page);

        Assert.Equal(40, AuctionSearch.Run(auctions, all with { Name = "wool" }, Find).Total);
        Assert.Equal(80, AuctionSearch.Run(auctions, all with { ItemClass = 7 }, Find).Total);
        Assert.Equal(40, AuctionSearch.Run(auctions, all with { Quality = 2 }, Find).Total);
        Assert.Equal(40, AuctionSearch.Run(auctions, all with { LevelMin = 10 }, Find).Total);
        Assert.Equal(80, AuctionSearch.Run(auctions, all with { LevelMax = 10 }, Find).Total);
        Assert.Equal(40, AuctionSearch.Run(auctions, all with { InventoryType = 13 }, Find).Total);
        Assert.Equal(80, AuctionSearch.Run(auctions, all with { Usable = true }, Find, t => t.RequiredLevel <= 10).Total);
    }

    [Fact]
    public void Letters_ReturnSwapsPartiesAndClearsCod_OnlyForUnreturnedPlayerMail()
    {
        var options = new EconomyOptions();
        var mail = new MailRecord
        {
            Id = 4, MessageType = MailMessageType.Normal, SenderId = 7, ReceiverId = 9, ItemGuid = 50, ItemEntry = 117,
            Money = 10, Cod = 99, Checked = MailCheckMask.Read | MailCheckMask.HasBody, ItemTextId = 3, DeliverTime = 1, ExpireTime = 2,
        };
        Assert.True(MailRules.CanReturn(mail));
        MailRecord returned = MailRules.Returned(mail, 11, 1000, options);
        Assert.Equal((11u, 9u, 7, 0u, 10u, 50u, 3u), (returned.Id, returned.SenderId, returned.ReceiverId, returned.Cod, returned.Money, returned.ItemGuid, returned.ItemTextId));
        Assert.Equal(MailCheckMask.Returned | MailCheckMask.HasBody, returned.Checked);
        Assert.Equal(1000 + (30 * MailRules.SecondsPerDay), returned.ExpireTime);
        Assert.False(MailRules.CanReturn(returned));
        Assert.False(MailRules.CanReturn(mail with { MessageType = MailMessageType.Auction }));
        Assert.Equal("117:0:1", MailRules.AuctionSubject(117, AuctionMailAction.Won));
        Assert.Equal(1.5f, MailRules.DaysLeft(mail with { ExpireTime = 1000 + (MailRules.SecondsPerDay * 3 / 2) }, 1000));

        MailRecord auction = MailRules.AuctionLetter(5, AuctionHouseRules.HordeHouse, 9, "1:0:3", 1000, options, money: 7, itemGuid: 8, itemEntry: 1);
        Assert.Equal((MailMessageType.Auction, MailStationery.Auction, 6u, MailCheckMask.Copied), (auction.MessageType, auction.Stationery, auction.SenderId, auction.Checked));
    }

    [Fact]
    public void Trade_RangeIsThreeDimensional_AndMapBound()
    {
        (Player a, _) = ItemTestData.CreatePlayer(1, 0, 0);
        (Player b, _) = ItemTestData.CreatePlayer(2, 11, 0);
        Assert.True(TradeSession.InRange(a, b));
        b.Relocate(11, 0, 2, 0, 0);
        Assert.False(TradeSession.InRange(a, b));

        var trade = new TradeSession(a, b);
        Assert.Same(trade.Initiator, trade.SideOf(a));
        Assert.Same(trade.Target, trade.OtherSide(a));
        trade.Initiator.Accepted = trade.Target.Accepted = true;
        trade.ClearAccepted();
        Assert.False(trade.Initiator.Accepted || trade.Target.Accepted);
        trade.Initiator[TradeRules.NonTradedSlot] = ObjectGuid.Item(9);
        trade.Initiator[0] = ObjectGuid.Item(8);
        Assert.Equal([ObjectGuid.Item(8)], trade.Initiator.TradedItems);
        Assert.Equal(TradeRules.NonTradedSlot, trade.Initiator.SlotOf(ObjectGuid.Item(9)));
    }
}
