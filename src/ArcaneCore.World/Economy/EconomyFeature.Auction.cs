using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature
{
    /// <summary>Most expired auctions settled per sweep.</summary>
    public const int ExpiryBatch = 50;

    private readonly Dictionary<uint, AuctionView> _auctions = [];
    private readonly HashSet<uint> _busyAuctions = [];

    /// <summary>The cached auctions (tests and GM tools).</summary>
    public IReadOnlyCollection<AuctionView> Auctions => _auctions.Values;

    /// <summary>MSG_AUCTION_HELLO: open the auction window of the auctioneer's house.</summary>
    public void AuctionHello(WorldSession session, Player player, ObjectGuid auctioneer)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is { } house)
        {
            session.Send(WorldOpcode.MsgAuctionHello, EconomyPackets.AuctionHello(auctioneer.Value, house.Id));
        }
    }

    /// <summary>CMSG_AUCTION_LIST_ITEMS (vmangos AuctionHouseObject::BuildListAuctionItems).</summary>
    public void ListAuctions(WorldSession session, Player player, ObjectGuid auctioneer, AuctionQuery query)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is not { } house)
        {
            return;
        }

        long now = Now;
        Dictionary<uint, AuctionView> byId = HouseAuctions(house.Id, now).ToDictionary(v => v.Auction.Id);
        (IReadOnlyList<AuctionRecord> page, int total) = AuctionSearch.Run(byId.Values.Select(v => v.Auction), query, Templates.Find,
            t => t.RequiredLevel <= player.Level);
        session.Send(WorldOpcode.SmsgAuctionListResult, EconomyPackets.AuctionList([.. page.Select(a => byId[a.Id])], total, now));
    }

    /// <summary>CMSG_AUCTION_LIST_OWNER_ITEMS: the player's own listings in this house.</summary>
    public void ListOwnerAuctions(WorldSession session, Player player, ObjectGuid auctioneer, uint listFrom)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is { } house)
        {
            SendPage(session, WorldOpcode.SmsgAuctionOwnerListResult, HouseAuctions(house.Id, Now).Where(v => v.Auction.SellerId == IdOf(player)), listFrom);
        }
    }

    /// <summary>CMSG_AUCTION_LIST_BIDDER_ITEMS: auctions the player is the highest bidder on.</summary>
    public void ListBidderAuctions(WorldSession session, Player player, ObjectGuid auctioneer, uint listFrom)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is { } house)
        {
            SendPage(session, WorldOpcode.SmsgAuctionBidderListResult, HouseAuctions(house.Id, Now).Where(v => v.Auction.BidderId == IdOf(player)), listFrom);
        }
    }

    /// <summary>CMSG_AUCTION_SELL_ITEM (vmangos HandleAuctionSellItem).</summary>
    public void SellItem(WorldSession session, Player player, ObjectGuid auctioneer, ObjectGuid itemGuid, uint bid, uint buyout, uint minutes)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is not { } house || bid == 0)
        {
            return;
        }

        void Fail(AuctionError error, InventoryResult inventory = InventoryResult.Ok) =>
            session.Send(WorldOpcode.SmsgAuctionCommandResult, EconomyPackets.AuctionCommandResult(0, AuctionAction.Started, error, inventory));

        if (!Enabled || !AuctionHouseRules.DurationsMinutes.Contains(minutes))
        {
            Fail(AuctionError.Database);
            return;
        }

        if (bid > EconomyOptions.MaxMoney || buyout > EconomyOptions.MaxMoney)
        {
            Fail(AuctionError.NotEnoughMoney);
            return;
        }

        if (buyout != 0 && bid > buyout)
        {
            Fail(AuctionError.HigherBid);
            return;
        }

        if (player.Inventory.GetItemByGuid(itemGuid) is not { } item || player.Inventory.CanTransferOut(item) != InventoryResult.Ok)
        {
            Fail(AuctionError.ItemNotFound);
            return;
        }

        uint deposit = AuctionHouseRules.Deposit(house, item.Template.SellPrice, item.Count, minutes, Options.AuctionDepositMin);
        if (player.Money < deposit)
        {
            Fail(AuctionError.NotEnoughMoney);
            return;
        }

        InventoryResult staged = player.Inventory.TryStageEconomyTransfer([item.Guid], [], out EconomyInventoryStage? stage);
        if (staged != InventoryResult.Ok)
        {
            Fail(AuctionError.Inventory, staged);
            return;
        }

        if (Settlements.CreateActor(session, player, stage!, player.Money - deposit) is not { } actor)
        {
            Fail(AuctionError.Database);
            return;
        }

        ItemInstanceData data = stage!.RemovedData.Single();
        var auction = new AuctionRecord
        {
            Id = NextAuctionId(),
            HouseId = house.Id,
            ItemGuid = data.Guid,
            ItemEntry = data.Entry,
            ItemCount = data.Count,
            SellerId = IdOf(player),
            StartBid = bid,
            Buyout = buyout,
            ExpireTime = Now + (minutes * 60L),
            Deposit = deposit,
        };
        bool started = Start([actor], [new EscrowFromInventory(IdOf(player), data), new InsertAuction(auction)], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                _auctions[auction.Id] = new AuctionView(auction, data);
                session.Send(WorldOpcode.SmsgAuctionCommandResult,
                    EconomyPackets.AuctionCommandResult(auction.Id, AuctionAction.Started, AuctionError.Ok));
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                Fail(AuctionError.Database);
            }
        });
        if (!started)
        {
            Fail(AuctionError.Database);
        }
    }

    /// <summary>CMSG_AUCTION_PLACE_BID (vmangos HandleAuctionPlaceBid): a bid, or a buyout that ends the auction.</summary>
    public void PlaceBid(WorldSession session, Player player, ObjectGuid auctioneer, uint auctionId, uint price)
    {
        if (auctionId == 0 || price == 0 || AuctioneerAccess.FindHouse(player, auctioneer) is not { } house)
        {
            return;
        }

        void Fail(AuctionError error, AuctionRecord? current = null) => session.Send(WorldOpcode.SmsgAuctionCommandResult,
            EconomyPackets.AuctionCommandResult(auctionId, AuctionAction.BidPlaced, error, auction: current));

        long now = Now;
        if (!_auctions.TryGetValue(auctionId, out AuctionView? view) || view.Auction.HouseId != house.Id
            || view.Auction.ExpireTime <= now || _busyAuctions.Contains(auctionId))
        {
            Fail(AuctionError.ItemNotFound);
            return;
        }

        AuctionRecord auction = view.Auction;
        int me = IdOf(player);
        if (auction.SellerId == me || (_directory?.Find(auction.SellerId) is { } seller && seller.AccountId == session.AccountId))
        {
            Fail(AuctionError.BidOwn);
            return;
        }

        bool buyout = auction.Buyout > 0 && price >= auction.Buyout;
        if (buyout)
        {
            price = auction.Buyout;
        }
        else if (price < auction.StartBid)
        {
            Fail(AuctionError.BidIncrement);
            return;
        }
        else if (auction.BidderId != 0 && price <= auction.Bid)
        {
            Fail(AuctionError.HigherBid, auction);
            return;
        }
        else if (auction.BidderId != 0 && price < AuctionHouseRules.MinimumBid(auction))
        {
            Fail(AuctionError.BidIncrement);
            return;
        }

        uint cost = auction.BidderId == me ? price - auction.Bid : price;
        if (player.Money < cost)
        {
            Fail(AuctionError.NotEnoughMoney);
            return;
        }

        if (player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage) != InventoryResult.Ok
            || Settlements.CreateActor(session, player, stage!, player.Money - cost) is not { } actor)
        {
            Fail(AuctionError.Database);
            return;
        }

        var changes = new List<EconomyChange>();
        var letters = new List<MailView>();
        if (auction.BidderId != 0 && auction.BidderId != me && CharacterExists(auction.BidderId))
        {
            letters.Add(AuctionMail(auction, auction.BidderId, AuctionMailAction.Outbidded, now, money: auction.Bid));
        }

        AuctionRecord updated = auction with { BidderId = me, Bid = price };
        if (buyout)
        {
            changes.Add(new DeleteAuction(auction));
            letters.AddRange(SaleLetters(updated, view.Item, house, now));
        }
        else
        {
            changes.Add(new UpdateAuction(auction, updated));
        }

        changes.AddRange(letters.Select(l => new InsertMail(l.Mail, LetterBody(l))));
        RunAuctionOperation([actor], changes, auctionId, outcome =>
        {
            if (outcome != EconomyOutcome.After)
            {
                if (outcome != EconomyOutcome.Unknown)
                {
                    Fail(AuctionError.Database);
                }

                return;
            }

            if (buyout)
            {
                _auctions.Remove(auctionId);
            }
            else
            {
                _auctions[auctionId] = view with { Auction = updated };
            }

            session.Send(WorldOpcode.SmsgAuctionCommandResult,
                EconomyPackets.AuctionCommandResult(auctionId, AuctionAction.BidPlaced, AuctionError.Ok, auction: updated));
            if (auction.BidderId != 0 && auction.BidderId != me)
            {
                OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionBidderNotification,
                    EconomyPackets.BidderNotification(updated, won: false, view.Item.RandomPropertyId));
            }

            if (buyout)
            {
                session.Send(WorldOpcode.SmsgAuctionBidderNotification, EconomyPackets.BidderNotification(updated, won: true, view.Item.RandomPropertyId));
            }

            OnlinePlayer(auction.SellerId)?.Session.Send(WorldOpcode.SmsgAuctionOwnerNotification,
                EconomyPackets.OwnerNotification(updated, sold: buyout, view.Item.RandomPropertyId));
            DeliverAll(letters);
        });
    }

    /// <summary>CMSG_AUCTION_REMOVE_ITEM (vmangos HandleAuctionRemoveItem): the seller pays the cut if bid on; the item comes back by mail.</summary>
    public void CancelAuction(WorldSession session, Player player, ObjectGuid auctioneer, uint auctionId)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is not { } house)
        {
            return;
        }

        void Fail(AuctionError error) => session.Send(WorldOpcode.SmsgAuctionCommandResult,
            EconomyPackets.AuctionCommandResult(auctionId, AuctionAction.Removed, error));

        long now = Now;
        if (!_auctions.TryGetValue(auctionId, out AuctionView? view) || view.Auction.HouseId != house.Id
            || view.Auction.SellerId != IdOf(player) || _busyAuctions.Contains(auctionId))
        {
            Fail(AuctionError.Database);
            return;
        }

        AuctionRecord auction = view.Auction;
        uint cut = auction.BidderId != 0 ? AuctionHouseRules.Cut(house, auction.Bid) : 0;
        if (player.Money < cut)
        {
            Fail(AuctionError.NotEnoughMoney);
            return;
        }

        if (player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage) != InventoryResult.Ok
            || Settlements.CreateActor(session, player, stage!, player.Money - cut) is not { } actor)
        {
            Fail(AuctionError.Database);
            return;
        }

        var letters = new List<MailView>();
        if (auction.BidderId != 0 && CharacterExists(auction.BidderId))
        {
            letters.Add(AuctionMail(auction, auction.BidderId, AuctionMailAction.CancelledToBidder, now, money: auction.Bid));
        }

        letters.Add(AuctionMail(auction, auction.SellerId, AuctionMailAction.Canceled, now, item: view.Item));
        var changes = new List<EconomyChange> { new DeleteAuction(auction) };
        changes.AddRange(letters.Select(l => new InsertMail(l.Mail, LetterBody(l))));
        RunAuctionOperation([actor], changes, auctionId, outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                _auctions.Remove(auctionId);
                session.Send(WorldOpcode.SmsgAuctionCommandResult, EconomyPackets.AuctionCommandResult(auctionId, AuctionAction.Removed, AuctionError.Ok));
                DeliverAll(letters);
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                Fail(AuctionError.Database);
            }
        });
    }

    /// <summary>World thread: settle auctions whose time ran out (vmangos AuctionHouseObject::Update).</summary>
    private void ExpireAuctions()
    {
        long now = Now;
        foreach (AuctionView view in _auctions.Values.Where(v => v.Auction.ExpireTime <= now && !_busyAuctions.Contains(v.Auction.Id))
            .OrderBy(v => v.Auction.ExpireTime).Take(ExpiryBatch).ToList())
        {
            AuctionRecord auction = view.Auction;
            AuctionHouseEntry house = Options.AuctionHouses.FirstOrDefault(h => h.Id == auction.HouseId) ?? new AuctionHouseEntry(auction.HouseId, 0, 0);
            bool sold = auction.BidderId != 0 && CharacterExists(auction.BidderId);
            var letters = new List<MailView>();
            var changes = new List<EconomyChange> { new DeleteAuction(auction) };
            if (sold)
            {
                letters.AddRange(SaleLetters(auction, view.Item, house, now));
            }
            else if (CharacterExists(auction.SellerId))
            {
                letters.Add(AuctionMail(auction, auction.SellerId, AuctionMailAction.Expired, now, item: view.Item));
            }
            else
            {
                changes.Add(new DeleteEscrowItem(auction.ItemGuid));
            }

            changes.AddRange(letters.Select(l => new InsertMail(l.Mail, LetterBody(l))));
            RunAuctionOperation([], changes, auction.Id, outcome =>
            {
                if (outcome != EconomyOutcome.After)
                {
                    return;
                }

                _auctions.Remove(auction.Id);
                if (sold)
                {
                    OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionBidderNotification,
                        EconomyPackets.BidderNotification(auction, won: true, view.Item.RandomPropertyId));
                    OnlinePlayer(auction.SellerId)?.Session.Send(WorldOpcode.SmsgAuctionOwnerNotification,
                        EconomyPackets.OwnerNotification(auction, sold: true, view.Item.RandomPropertyId));
                }
                else
                {
                    OnlinePlayer(auction.SellerId)?.Session.Send(WorldOpcode.SmsgAuctionRemovedNotification,
                        EconomyPackets.RemovedNotification(auction.ItemEntry, view.Item.RandomPropertyId));
                }

                DeliverAll(letters);
            });
        }
    }

    /// <summary>The won letter (item to the buyer) and the successful-sale letter (bid + deposit − cut to a living seller).</summary>
    private IEnumerable<MailView> SaleLetters(AuctionRecord auction, ItemInstanceData item, AuctionHouseEntry house, long now)
    {
        yield return AuctionMail(auction, auction.BidderId, AuctionMailAction.Won, now, item: item);
        if (CharacterExists(auction.SellerId))
        {
            uint cut = AuctionHouseRules.Cut(house, auction.Bid);
            uint proceeds = (uint)Math.Min(EconomyOptions.MaxMoney, (long)auction.Bid + auction.Deposit - cut);
            yield return AuctionMail(auction, auction.SellerId, AuctionMailAction.Successful, now, money: proceeds, cut: cut);
        }
    }

    /// <summary>
    /// An auction house letter (vmangos AuctionHouseMgr): subject "entry:0:action"; the won and
    /// successful letters carry the "%16X:bid:buyout[:deposit:cut]" body the client parses.
    /// </summary>
    private MailView AuctionMail(AuctionRecord auction, int receiverId, AuctionMailAction action, long now,
        uint money = 0, ItemInstanceData? item = null, uint cut = 0)
    {
        string? body = action switch
        {
            AuctionMailAction.Won => $"{ObjectGuid.Player((uint)auction.SellerId).Value:X16}:{auction.Bid}:{auction.Buyout}",
            AuctionMailAction.Successful => $"{ObjectGuid.Player((uint)auction.BidderId).Value:X16}:{auction.Bid}:{auction.Buyout}:{auction.Deposit}:{cut}",
            _ => null,
        };
        MailRecord mail = MailRules.AuctionLetter(NextMailId(), auction.HouseId, receiverId,
            MailRules.AuctionSubject(auction.ItemEntry, action), now, Options, money, item?.Guid ?? 0, item?.Entry ?? 0)
            with { ItemTextId = body is null ? 0 : NextTextId() };
        return new MailView(mail, item) { Body = body };
    }

    private static string? LetterBody(MailView view) => view.Body;

    private void DeliverAll(IEnumerable<MailView> letters)
    {
        foreach (MailView letter in letters)
        {
            Deliver(letter);
        }
    }

    private void RunAuctionOperation(IReadOnlyList<EconomyActor> actors, IReadOnlyList<EconomyChange> changes, uint auctionId,
        Action<EconomyOutcome> finished)
    {
        if (!_busyAuctions.Add(auctionId))
        {
            finished(EconomyOutcome.NotStarted);
            return;
        }

        bool started = Start(actors, changes, outcome =>
        {
            _busyAuctions.Remove(auctionId);
            finished(outcome);
        });
        if (!started)
        {
            _busyAuctions.Remove(auctionId);
            finished(EconomyOutcome.NotStarted);
        }
    }

    private IEnumerable<AuctionView> HouseAuctions(uint houseId, long now)
        => _auctions.Values.Where(v => v.Auction.HouseId == houseId && v.Auction.ExpireTime > now).OrderBy(v => v.Auction.Id);

    private void SendPage(WorldSession session, WorldOpcode opcode, IEnumerable<AuctionView> auctions, uint listFrom)
    {
        List<AuctionView> all = [.. auctions];
        int from = (int)Math.Min(listFrom, (uint)all.Count);
        session.Send(opcode, EconomyPackets.AuctionList([.. all.Skip(from).Take(AuctionHouseRules.PageSize)], all.Count, Now));
    }
}
