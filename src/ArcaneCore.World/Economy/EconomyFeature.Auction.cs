using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature
{
    /// <summary>Most expired auctions settled per sweep.</summary>
    public const int ExpiryBatch = 50;

    private readonly Dictionary<uint, AuctionView> _auctions = [];
    private readonly HashSet<uint> _busyAuctions = [];

    /// <summary>
    /// Listings accepted by <see cref="SellItem"/> whose settlement has not finished: their house and the seller's
    /// account. Each holds an account-limit slot until its outcome is known, as vmangos adds the auction to the house
    /// inside the handler (AuctionHouseHandler.cpp:396); otherwise another character of the account,
    /// logged in while the listing settles, passes a check against the published auctions only.
    /// </summary>
    private readonly Dictionary<uint, (uint HouseId, int AccountId)> _listingsInFlight = [];

    /// <summary>
    /// Auctions whose last expiry settlement was refused (a conflict, a missing seller row): when to
    /// try them again, so a poisoned auction cannot starve later ones. In memory only.
    /// </summary>
    private readonly Dictionary<uint, (int Failures, long NotBefore)> _expiryBackoff = [];

    private const long ExpiryBackoffBaseSeconds = 60;
    private const long ExpiryBackoffMaxSeconds = 86_400;

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

    /// <summary>
    /// CMSG_AUCTION_LIST_BIDDER_ITEMS: first the auctions of <paramref name="outbidIds"/> (the client asks again for the auctions it was outbid on)
    /// that still exist in this house, in the client's order, then the auctions the player is the highest bidder on; the page and the total count
    /// both lists (vmangos AuctionHouseClientQueryTask, AuctionHouseHandler.cpp:665-679, then AuctionHouseObject::BuildListBidderItems,
    /// AuctionHouseMgr.cpp:679-693). An id that is not an auction of this house is skipped.
    /// </summary>
    public void ListBidderAuctions(WorldSession session, Player player, ObjectGuid auctioneer, uint listFrom, IReadOnlyList<uint>? outbidIds = null)
    {
        if (AuctioneerAccess.FindHouse(player, auctioneer) is not { } house)
        {
            return;
        }

        List<AuctionView> open = [.. HouseAuctions(house.Id, Now)];
        int me = IdOf(player);
        IEnumerable<AuctionView> outbid = [];
        if (outbidIds is { Count: > 0 })
        {
            Dictionary<uint, AuctionView> byId = open.ToDictionary(v => v.Auction.Id);
            outbid = outbidIds.Select(id => byId.GetValueOrDefault(id)).OfType<AuctionView>();
        }

        SendPage(session, WorldOpcode.SmsgAuctionBidderListResult, outbid.Concat(open.Where(v => v.Auction.BidderId == me)), listFrom);
    }

    /// <summary>CMSG_AUCTION_SELL_ITEM (vmangos HandleAuctionSellItem).</summary>
    public void SellItem(WorldSession session, Player player, ObjectGuid auctioneer, ObjectGuid itemGuid, uint bid, uint buyout, uint minutes)
    {
        // vmangos HandleAuctionSellItem (AuctionHouseHandler.cpp:230-345) in its order: silent drop of a zero bid or
        // duration, the 2,000,000,000 client limit, bid above buyout, the house, the account limit, the duration, the item.
        if (bid == 0 || minutes == 0)
        {
            return;
        }

        void Fail(AuctionError error, InventoryResult inventory = InventoryResult.Ok) =>
            session.Send(WorldOpcode.SmsgAuctionCommandResult, EconomyPackets.AuctionCommandResult(0, AuctionAction.Started, error, inventory));

        if (bid > AuctionHouseRules.MaxPrice || buyout > AuctionHouseRules.MaxPrice)
        {
            Fail(AuctionError.NotEnoughMoney);
            return;
        }

        if (buyout != 0 && bid > buyout)
        {
            Fail(AuctionError.HigherBid);
            return;
        }

        if (AuctioneerAccess.FindHouse(player, auctioneer) is not { } house)
        {
            Fail(AuctionError.Database);
            return;
        }

        uint limit = Options.AuctionAccountConcurrentLimit;
        if (limit != 0 && AccountAuctionCount(house.Id, session.AccountId) >= limit)
        {
            session.Send(WorldOpcode.SmsgMessagechat,
                ArcaneCore.World.Packets.ChatPackets.BuildSystemMessage("You have reached the limit of active auctions on your account."));
            Fail(AuctionError.Database);
            return;
        }

        if (!Enabled || !AuctionHouseRules.DurationsMinutes.Contains(minutes))
        {
            Fail(AuctionError.Database);
            return;
        }

        if (itemGuid.IsEmpty)
        {
            Fail(AuctionError.ItemNotFound);
            return;
        }

        // A missing, bank, untradable, conjured or timed item all answer INVENTORY / EQUIP_ERR_ITEM_NOT_FOUND (:314-346).
        if (player.Inventory.GetItemByGuid(itemGuid) is not { } item || player.Inventory.CanTransferOut(item) != InventoryResult.Ok)
        {
            Fail(AuctionError.Inventory, InventoryResult.ItemNotFound);
            return;
        }

        uint deposit = AuctionHouseRules.Deposit(house, item.Template.SellPrice, item.Count, minutes, Options.AuctionDepositMin, Options.AuctionRateDeposit);
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
            ExpireTime = Now + (uint)(minutes * 60 * Options.AuctionRateTime),
            Deposit = deposit,
        };
        _listingsInFlight[auction.Id] = (house.Id, session.AccountId);
        RunAuctionOperation([actor], [new EscrowFromInventory(IdOf(player), data), new InsertAuction(auction)], auction.Id, outcome =>
        {
            // After: published below. Unknown: the auction is reserved for recovery, which keeps counting it.
            _listingsInFlight.Remove(auction.Id);
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
        if (Options.AuctionSilentRefusals)
        {
            // vmangos AuctionHouseHandler.cpp:498-503: the whole price must be in hand and the refusal gets no answer
            // (the 1.12 client checks its own money first).
            if (price > player.Money)
            {
                return;
            }
        }
        else if (player.Money < cost)
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
                // vmangos SendAuctionOutbiddedMail (AuctionHouseHandler.cpp:148-170, called at :513 and :535) notifies before the auction
                // takes the new bid: the packet carries the outbid player's own GUID, its bid and that bid's outbid step.
                OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionBidderNotification,
                    EconomyPackets.BidderNotification(auction, won: false, view.Item.RandomPropertyId));
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
        // An expired auction belongs to the expiry sweep, which returns the item as Expired. Letting
        // the seller cancel it first would race the sweep and hide a poisoned expiry behind a cancel.
        if (!_auctions.TryGetValue(auctionId, out AuctionView? view) || view.Auction.HouseId != house.Id
            || view.Auction.SellerId != IdOf(player) || view.Auction.ExpireTime <= now || _busyAuctions.Contains(auctionId))
        {
            Fail(AuctionError.Database);
            return;
        }

        AuctionRecord auction = view.Auction;
        uint cut = auction.BidderId != 0 ? AuctionHouseRules.Cut(house, auction.Bid, Options.AuctionRateCut) : 0;
        if (player.Money < cut)
        {
            // vmangos AuctionHouseHandler.cpp:592-594: "maybe message needed", but none is sent.
            if (!Options.AuctionSilentRefusals)
            {
                Fail(AuctionError.NotEnoughMoney);
            }

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
                // vmangos SendAuctionCancelledToBidderMail (AuctionHouseHandler.cpp:173-195): an online bidder is told the auction was removed
                // (ERR_AUCTION_REMOVED_S) next to the letter that returns the bid.
                if (auction.BidderId != 0)
                {
                    OnlinePlayer(auction.BidderId)?.Session.Send(WorldOpcode.SmsgAuctionRemovedNotification,
                        EconomyPackets.RemovedNotification(auction.ItemEntry, view.Item.RandomPropertyId));
                }

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
        foreach (uint gone in _expiryBackoff.Keys.Where(id => !_auctions.ContainsKey(id)).ToList())
        {
            _expiryBackoff.Remove(gone);
        }

        foreach (AuctionView view in _auctions.Values.Where(v => v.Auction.ExpireTime <= now && !_busyAuctions.Contains(v.Auction.Id)
                && !(_expiryBackoff.TryGetValue(v.Auction.Id, out var backoff) && backoff.NotBefore > now))
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
            bool refused = false;
            RunAuctionOperation([], changes, auction.Id, outcome =>
            {
                if (outcome == EconomyOutcome.NotStarted)
                {
                    refused = true;
                    return;
                }

                if (outcome == EconomyOutcome.Before)
                {
                    int failures = _expiryBackoff.TryGetValue(auction.Id, out var previous) ? previous.Failures + 1 : 1;
                    long wait = Math.Min(ExpiryBackoffBaseSeconds << Math.Min(failures - 1, 11), ExpiryBackoffMaxSeconds);
                    _expiryBackoff[auction.Id] = (failures, Now + wait);
                    if (failures == 1)
                    {
                        _logger.LogWarning("expiry of auction {Auction} was refused; it is retried with backoff (next in {Seconds} s)", auction.Id, wait);
                    }

                    return;
                }

                if (outcome != EconomyOutcome.After)
                {
                    return;
                }

                _expiryBackoff.Remove(auction.Id);
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
            if (refused)
            {
                // No settlement slot (the 16-operation cap) or shutdown: the rest of the batch would be
                // refused the same way and log a warning each. The next sweep continues.
                break;
            }
        }
    }

    /// <summary>The won letter (item to the buyer) and the successful-sale letter (bid + deposit − cut to a living seller).</summary>
    private IEnumerable<MailView> SaleLetters(AuctionRecord auction, ItemInstanceData item, AuctionHouseEntry house, long now)
    {
        yield return AuctionMail(auction, auction.BidderId, AuctionMailAction.Won, now, item: item);
        if (CharacterExists(auction.SellerId))
        {
            uint cut = AuctionHouseRules.Cut(house, auction.Bid, Options.AuctionRateCut);
            if ((ulong)cut > (ulong)auction.Bid + auction.Deposit)
            {
                _logger.LogWarning("auction {Auction}: cut {Cut} of house {House} (cut percent {CutPercent}, rate {Rate}) exceeds bid {Bid} + deposit {Deposit}; the seller is paid nothing",
                    auction.Id, cut, house.Id, house.CutPercent, Options.AuctionRateCut, auction.Bid, auction.Deposit);
            }

            uint proceeds = AuctionHouseRules.Proceeds(auction.Bid, auction.Deposit, cut);
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

    internal void RunAuctionOperation(IReadOnlyList<EconomyActor> actors, IReadOnlyList<EconomyChange> changes, uint auctionId,
        Action<EconomyOutcome> finished, Guid? operationId = null)
    {
        if (!_busyAuctions.Add(auctionId))
        {
            finished(EconomyOutcome.NotStarted);
            return;
        }

        bool started = Start(actors, changes, outcome =>
        {
            if (outcome == EconomyOutcome.Unknown || (outcome == EconomyOutcome.Before && ChangesExpectedAuction(changes, auctionId)))
            {
                // Unknown: the commit may or may not have happened. Before on a same-ID update/delete:
                // the row no longer matches the cached Expected (another writer, or a participant
                // mismatch), so the cache is stale. Either way the ID stays reserved until a fresh
                // authoritative read repairs it.
                QuarantineAuction(auctionId, changes);
            }
            else
            {
                _busyAuctions.Remove(auctionId);
            }
            finished(outcome);
        }, operationId);
        if (!started)
        {
            _busyAuctions.Remove(auctionId);
            finished(EconomyOutcome.NotStarted);
        }
    }

    /// <summary>Whether the changes expect to find (update or delete) the auction row the cache holds for this ID.</summary>
    private static bool ChangesExpectedAuction(IReadOnlyList<EconomyChange> changes, uint auctionId)
        => changes.Any(c => c is UpdateAuction u && u.Expected.Id == auctionId || c is DeleteAuction d && d.Expected.Id == auctionId);

    /// <summary>
    /// World thread: the auctions an account holds in a house for its optional limit (vmangos
    /// AuctionHouseObject::GetAccountAuctionCount): published ones, listings still settling, and reserved
    /// ones missing from the cache (an insert whose outcome is unknown, a row whose escrow does not match),
    /// whose row may exist until recovery decides.
    /// </summary>
    private int AccountAuctionCount(uint houseId, int accountId)
    {
        bool OwnedBy(int sellerId) => _directory?.Find(sellerId) is { } owner && owner.AccountId == accountId;
        int published = _auctions.Values.Count(v => v.Auction.HouseId == houseId && OwnedBy(v.Auction.SellerId));
        int settling = _listingsInFlight.Values.Count(l => l.HouseId == houseId && l.AccountId == accountId);
        int reserved = _auctionRecoveries.Count(pair => !_auctions.ContainsKey(pair.Key) && !_listingsInFlight.ContainsKey(pair.Key)
            && pair.Value.Affected.Any(row => row.Id == pair.Key && row.HouseId == houseId && OwnedBy(row.SellerId)));
        return published + settling + reserved;
    }

    private IEnumerable<AuctionView> HouseAuctions(uint houseId, long now)
        => _auctions.Values.Where(v => v.Auction.HouseId == houseId && v.Auction.ExpireTime > now
            && !_auctionRecoveries.ContainsKey(v.Auction.Id)).OrderBy(v => v.Auction.Id);

    private void SendPage(WorldSession session, WorldOpcode opcode, IEnumerable<AuctionView> auctions, uint listFrom)
    {
        List<AuctionView> all = [.. auctions];
        int from = (int)Math.Min(listFrom, (uint)all.Count);
        session.Send(opcode, EconomyPackets.AuctionList([.. all.Skip(from).Take(AuctionHouseRules.PageSize)], all.Count, Now));
    }
}
