using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Three bots at one auction house: the outbid notification, the outbid ids of the bidder list and the cancel notice to the bidder
/// (vmangos AuctionHouseHandler.cpp: SendAuctionOutbiddedMail :148-170 sends SendAuctionBidderNotification(auction, false) before the
/// auction takes the new bid, so it names the old bidder and the old bid; the bidder-list task :665-679 lists the still existing auctions of the
/// client's outbid ids first; SendAuctionCancelledToBidderMail :173-195 sends SMSG_AUCTION_REMOVED_NOTIFICATION to the bidder).
/// </summary>
public sealed class AuctionOutbidScenarioTests
{
    [Fact]
    public async Task Outbid_NotifiesTheOldBidder_ListsTheOutbidAuction_AndCancelNotifiesTheBidder()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
            services.AddSingleton<IAuctioneerAccess>(new AnyAuctioneer()));
        await world.RunPassingAsync(new AuctionOutbidScenario());
    }

    internal sealed class AnyAuctioneer : IAuctioneerAccess
    {
        public AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer)
            => player.IsInWorld && auctioneer.Value == AuctionOutbidScenario.Auctioneer ? new AuctionHouseEntry(2, 15, 5) : null;
    }
}

internal sealed class AuctionOutbidScenario : IPlayerbotScenario
{
    public const ulong Auctioneer = 0xF130000000990030;
    private const uint StartBid = 100;

    public string Name => "auction-outbid";

    public string Description => "A seller lists cloth, Alpha bids, Beta outbids, Alpha is told and lists the auction, the seller cancels";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot seller = await context.LoginAsync("Scnseller");
        ScenarioBot alpha = await context.LoginAsync("Scnalpha");
        ScenarioBot beta = await context.LoginAsync("Scnbeta");
        uint auctionId = await context.StepAsync("the seller lists Linen Cloth", async () =>
        {
            await context.GiveMoneyAsync(seller, 1000);
            ObjectGuid cloth = await context.GiveItemAsync(seller, LinenCloth);
            long mark = seller.Mark();
            ScenarioContext.Expect(await seller.SendAsync(WorldOpcode.CmsgAuctionSellItem, ScenarioAuctionWire.Sell(Auctioneer, cloth.Value, StartBid, 0, 120)),
                "sell refused");
            AuctionCommandResultView started = await seller.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult,
                r => r.Action == AuctionAction.Started, mark);
            ScenarioContext.ExpectEqual(AuctionError.Ok, started.Error, "listing result");
            return started.AuctionId;
        });

        await BidAsync(context, alpha, auctionId, StartBid, "Alpha bids the start bid");
        uint outbid = StartBid + AuctionHouseRules.OutBid(StartBid);
        long alphaMark = alpha.Mark();
        await BidAsync(context, beta, auctionId, outbid, "Beta outbids Alpha");

        await context.StepAsync("Alpha is told it was outbid, with its own bid", async () =>
        {
            AuctionBidderNotificationView notice = await alpha.WaitForPacketAsync(WorldOpcode.SmsgAuctionBidderNotification,
                ScenarioAuctionWire.BidderNotification, n => n.AuctionId == auctionId, alphaMark);
            ScenarioContext.Expect(notice.Bid != 0, "the notification says won, not outbid");
            ScenarioContext.ExpectEqual(alpha.Guid.Value, notice.Bidder, "notified bidder (the outbid player)");
            ScenarioContext.ExpectEqual(StartBid, notice.Bid, "notified bid (the outbid bid)");
            ScenarioContext.ExpectEqual(AuctionHouseRules.OutBid(StartBid), notice.OutBid, "notified outbid step");
            ScenarioContext.ExpectEqual(LinenCloth, notice.ItemEntry, "notified item");
            ScenarioContext.ExpectEqual(2u, notice.HouseId, "notified house");
        });

        await context.StepAsync("Alpha's bidder list shows the auction it was outbid on", async () =>
        {
            AuctionListView list = await ListBidsAsync(alpha, auctionId);
            ScenarioContext.ExpectEqual(1, list.Rows.Count, "rows for Alpha");
            ScenarioContext.ExpectEqual(1u, list.Total, "total for Alpha");
            ScenarioContext.ExpectEqual(auctionId, list.Rows[0].AuctionId, "listed auction");
            ScenarioContext.ExpectEqual(beta.Guid.Value, list.Rows[0].Bidder, "listed highest bidder");
            ScenarioContext.ExpectEqual(outbid, list.Rows[0].Bid, "listed highest bid");
            AuctionListView none = await ListBidsAsync(alpha);
            ScenarioContext.ExpectEqual(0, none.Rows.Count, "rows for Alpha without the outbid ids");
            AuctionListView own = await ListBidsAsync(beta);
            ScenarioContext.ExpectEqual(1, own.Rows.Count, "rows for Beta, the highest bidder");
            AuctionListView unknown = await ListBidsAsync(alpha, 999_999);
            ScenarioContext.ExpectEqual(0, unknown.Rows.Count, "an unknown outbid id lists nothing");
        });

        await context.StepAsync("the seller cancels and Beta is told", async () =>
        {
            long betaMark = beta.Mark();
            long sellerMark = seller.Mark();
            ScenarioContext.Expect(await seller.SendAsync(WorldOpcode.CmsgAuctionRemoveItem, ScenarioAuctionWire.Remove(Auctioneer, auctionId)), "cancel refused");
            AuctionCommandResultView removed = await seller.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult,
                r => r.Action == AuctionAction.Removed, sellerMark);
            ScenarioContext.ExpectEqual(AuctionError.Ok, removed.Error, "cancel result");
            await beta.WaitForPacketAsync(WorldOpcode.SmsgAuctionRemovedNotification, static payload => payload, since: betaMark);
        });
    }

    private static Task BidAsync(ScenarioContext context, ScenarioBot bot, uint auctionId, uint price, string step) => context.StepAsync(step, async () =>
    {
        await context.GiveMoneyAsync(bot, 1000);
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionPlaceBid, ScenarioAuctionWire.PlaceBid(Auctioneer, auctionId, price)), "bid refused");
        AuctionCommandResultView result = await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionCommandResult, ScenarioAuctionWire.CommandResult,
            r => r.Action == AuctionAction.BidPlaced, mark);
        ScenarioContext.ExpectEqual(AuctionError.Ok, result.Error, $"{bot.Name} bid result");
    });

    private static async Task<AuctionListView> ListBidsAsync(ScenarioBot bot, params uint[] outbidIds)
    {
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAuctionListBidderItems, ScenarioAuctionWire.ListBidderItems(Auctioneer, 0, outbidIds)),
            "bidder list refused");
        return await bot.WaitForPacketAsync(WorldOpcode.SmsgAuctionBidderListResult, ScenarioAuctionWire.List, since: mark);
    }
}
