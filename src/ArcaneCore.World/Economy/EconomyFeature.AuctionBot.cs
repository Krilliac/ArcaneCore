using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The two auction movements of the auction house bot (<see cref="AuctionBotFeature"/>). The bot is seller 0, which no character has:
/// its listings expire into nothing (the item is destroyed with the auction, as the expiry sweep already does for a seller that no
/// longer exists) and a player who buys one gets the item while no proceeds are paid. Each movement is one economy transaction with
/// the caller's stable operation id, so a replay is AlreadyCommitted and never a second copy.
/// </summary>
public sealed partial class EconomyFeature
{
    /// <summary>The seller id of bot listings (cMaNGOS AuctionHouseBot auctions have owner 0).</summary>
    public const int BotSellerId = 0;

    private readonly Dictionary<uint, uint> _botListingsInFlight = [];

    /// <summary>The process-wide item GUID source, or null without the item feature.</summary>
    internal ItemGuidAllocator? ItemGuids => _items?.GuidAllocator;

    /// <summary>World thread: the published, unexpired, not settling auctions of a house.</summary>
    internal IEnumerable<AuctionView> OpenAuctions(uint houseId)
        => HouseAuctions(houseId, Now).Where(v => !_busyAuctions.Contains(v.Auction.Id));

    /// <summary>World thread: bot auctions in a house, published or still settling.</summary>
    internal int BotAuctionCount(uint houseId)
        => _auctions.Values.Count(v => v.Auction.HouseId == houseId && v.Auction.SellerId == BotSellerId) + _botListingsInFlight.Values.Count(h => h == houseId);

    /// <summary>
    /// World thread: list a brand-new item for the bot. The item is minted into escrow and the auction inserted in one transaction;
    /// <paramref name="finished"/> receives the outcome (NotStarted when the economy refused to start it).
    /// </summary>
    internal void StartBotListing(uint houseId, uint auctionId, ItemInstanceData item, uint startBid, uint buyout, long expireTime,
        Guid operationId, Action<EconomyOutcome> finished)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(finished);
        if (!Enabled || auctionId == 0 || startBid == 0 || startBid > AuctionHouseRules.MaxPrice || buyout > AuctionHouseRules.MaxPrice
            || (buyout != 0 && startBid > buyout) || _auctions.ContainsKey(auctionId))
        {
            finished(EconomyOutcome.NotStarted);
            return;
        }

        var auction = new AuctionRecord
        {
            Id = auctionId,
            HouseId = houseId,
            ItemGuid = item.Guid,
            ItemEntry = item.Entry,
            ItemCount = item.Count,
            SellerId = BotSellerId,
            StartBid = startBid,
            Buyout = buyout,
            ExpireTime = expireTime,
        };
        _botListingsInFlight[auctionId] = houseId;
        RunAuctionOperation([], [new MintEscrowItem(item), new InsertAuction(auction)], auctionId, outcome =>
        {
            _botListingsInFlight.Remove(auctionId);
            if (outcome == EconomyOutcome.After)
            {
                _auctions[auctionId] = new AuctionView(auction, item);
            }

            finished(outcome);
        }, operationId);
    }

    /// <summary>
    /// World thread: the bot buys out a player's auction at exactly its buyout. The auction and its escrow item are deleted (the bot
    /// keeps nothing), a previous bidder gets the outbid refund letter, and a living seller the successful-sale letter
    /// (buyout + deposit − cut), as a player buyout pays them.
    /// </summary>
    internal void StartBotBuyout(uint auctionId, uint price, Guid operationId, Action<EconomyOutcome> finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        long now = Now;
        if (!Enabled || !_auctions.TryGetValue(auctionId, out AuctionView? view) || _busyAuctions.Contains(auctionId)
            || _auctionRecoveries.ContainsKey(auctionId) || view.Auction.ExpireTime <= now || view.Auction.SellerId == BotSellerId
            || view.Auction.Buyout == 0 || view.Auction.Buyout != price)
        {
            finished(EconomyOutcome.NotStarted);
            return;
        }

        AuctionRecord auction = view.Auction;
        AuctionHouseEntry house = Options.AuctionHouses.FirstOrDefault(h => h.Id == auction.HouseId) ?? new AuctionHouseEntry(auction.HouseId, 0, 0);
        AuctionRecord sold = auction with { BidderId = BotSellerId, Bid = price };
        var letters = new List<MailView>();
        if (auction.BidderId != 0 && CharacterExists(auction.BidderId))
        {
            letters.Add(AuctionMail(auction, auction.BidderId, AuctionMailAction.Outbidded, now, money: auction.Bid));
        }

        if (CharacterExists(auction.SellerId))
        {
            uint cut = AuctionHouseRules.Cut(house, sold.Bid, Options.AuctionRateCut);
            letters.Add(AuctionMail(sold, auction.SellerId, AuctionMailAction.Successful, now,
                money: AuctionHouseRules.Proceeds(sold.Bid, sold.Deposit, cut), cut: cut));
        }

        var changes = new List<EconomyChange> { new DeleteAuction(auction), new DeleteEscrowItem(auction.ItemGuid) };
        changes.AddRange(letters.Select(l => new InsertMail(l.Mail, LetterBody(l))));
        RunAuctionOperation([], changes, auctionId, outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                _auctions.Remove(auctionId);
                if (auction.BidderId != 0)
                {
                    OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionBidderNotification,
                        EconomyPackets.BidderNotification(auction, won: false, view.Item.RandomPropertyId));
                }

                OnlinePlayer(auction.SellerId)?.Session.Send(WorldOpcode.SmsgAuctionOwnerNotification,
                    EconomyPackets.OwnerNotification(sold, sold: true, view.Item.RandomPropertyId));
                DeliverAll(letters);
            }

            finished(outcome);
        }, operationId);
    }

    /// <summary>
    /// World thread, raised after commit when a bot bid stops standing: <c>won</c> true when the auction expired on it (the bot took the
    /// item), false when a player outbid or bought it out or the seller cancelled (no copper left the bot).
    /// </summary>
    internal event Action<uint, bool>? BotBidSettled;

    /// <summary>A bot bid: the auction has a bid but no bidding character (cMaNGOS AuctionBotBuyer bids with bidder 0).</summary>
    internal static bool HasBotBid(AuctionRecord auction) => auction.BidderId == 0 && auction.Bid > 0;

    /// <summary>Whether anyone (a character or the bot) bid on the auction.</summary>
    internal static bool HasAnyBid(AuctionRecord auction) => auction.BidderId != 0 || auction.Bid > 0;

    private void RaiseBotBidSettled(uint auctionId, bool won) => BotBidSettled?.Invoke(auctionId, won);

    /// <summary>World thread: never hand out an auction id at or below <paramref name="value"/> (the bot ledger's highest key).</summary>
    internal void RaiseAuctionIdTo(uint value) => RaiseTo(ref _lastAuctionId, value);

    /// <summary>
    /// World thread: the bot bids <paramref name="price"/> on a player's auction (cMaNGOS AuctionBotBuyer::BuyEntry bid branch). The
    /// auction keeps its item; the bid stands with bidder 0. A previous bidding character gets the outbid refund letter. The price must
    /// beat the current bid by the outbid step (or meet the start bid) and stay under the buyout.
    /// </summary>
    internal void StartBotBid(uint auctionId, uint price, Guid operationId, Action<EconomyOutcome> finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        long now = Now;
        if (!Enabled || !_auctions.TryGetValue(auctionId, out AuctionView? view) || _busyAuctions.Contains(auctionId)
            || _auctionRecoveries.ContainsKey(auctionId) || view.Auction.ExpireTime <= now || view.Auction.SellerId == BotSellerId
            || HasBotBid(view.Auction) || price == 0 || price < view.Auction.StartBid
            || (HasAnyBid(view.Auction) && price < AuctionHouseRules.MinimumBid(view.Auction))
            || (view.Auction.Buyout != 0 && price >= view.Auction.Buyout))
        {
            finished(EconomyOutcome.NotStarted);
            return;
        }

        AuctionRecord auction = view.Auction;
        AuctionRecord updated = auction with { BidderId = BotSellerId, Bid = price };
        var letters = new List<MailView>();
        if (auction.BidderId != 0 && CharacterExists(auction.BidderId))
        {
            letters.Add(AuctionMail(auction, auction.BidderId, AuctionMailAction.Outbidded, now, money: auction.Bid));
        }

        var changes = new List<EconomyChange> { new UpdateAuction(auction, updated) };
        changes.AddRange(letters.Select(l => new InsertMail(l.Mail, LetterBody(l))));
        RunAuctionOperation([], changes, auctionId, outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                _auctions[auctionId] = view with { Auction = updated };
                if (auction.BidderId != 0)
                {
                    OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionBidderNotification,
                        EconomyPackets.BidderNotification(auction, won: false, view.Item.RandomPropertyId));
                }

                OnlinePlayer(auction.SellerId)?.Session.Send(WorldOpcode.SmsgAuctionOwnerNotification,
                    EconomyPackets.OwnerNotification(updated, sold: false, view.Item.RandomPropertyId));
                DeliverAll(letters);
            }

            finished(outcome);
        }, operationId);
    }

    /// <summary>World thread: the published bot auctions of a house.</summary>
    internal IEnumerable<AuctionRecord> BotAuctions(uint houseId)
        => _auctions.Values.Where(v => v.Auction.HouseId == houseId && v.Auction.SellerId == BotSellerId).Select(v => v.Auction);

    /// <summary>World thread: whether an auction has a transaction in flight or a recovery pending.</summary>
    internal bool IsAuctionBusy(uint auctionId) => _busyAuctions.Contains(auctionId) || _auctionRecoveries.ContainsKey(auctionId);

    /// <summary>
    /// World thread: cMaNGOS Rebuild's expiry step: every bot auction without a bid (every one with <paramref name="all"/>) gets its
    /// expire time set to now, in one transaction each, so the next expiry sweep settles it (destroyed, or sold to a player bidder).
    /// Returns how many were started.
    /// </summary>
    internal int ExpireBotAuctions(bool all)
    {
        long now = Now;
        int started = 0;
        foreach (AuctionView view in _auctions.Values.Where(v => v.Auction.SellerId == BotSellerId && v.Auction.ExpireTime > now
            && (all || !HasAnyBid(v.Auction)) && !IsAuctionBusy(v.Auction.Id)).ToList())
        {
            AuctionRecord auction = view.Auction;
            AuctionRecord expiring = auction with { ExpireTime = now };
            bool refused = false;
            RunAuctionOperation([], [new UpdateAuction(auction, expiring)], auction.Id, outcome =>
            {
                refused = outcome == EconomyOutcome.NotStarted;
                if (outcome == EconomyOutcome.After && _auctions.TryGetValue(auction.Id, out AuctionView? current))
                {
                    _auctions[auction.Id] = current with { Auction = expiring };
                }
            });
            if (refused)
            {
                break;
            }

            started++;
        }

        return started;
    }

    /// <summary>World thread: the auction, or null when it is not published (gone, or never committed).</summary>
    internal AuctionRecord? FindAuction(uint auctionId) => _auctions.TryGetValue(auctionId, out AuctionView? view) ? view.Auction : null;

    /// <summary>Off the world thread: whether an economy operation committed; the answer (null when unreadable) arrives on the world thread.</summary>
    internal void ReadOperationCommitted(Guid operationId, Action<bool?> done)
        => Read((store, ct) => store.IsCommittedAsync(operationId, ct), committed => done(committed), () => done(null));
}
