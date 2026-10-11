# Playerbot economy: the faction auction house and the mailbox

How an autonomous managed playerbot uses the auction house and the mailbox the way a player does: it lists the loot it has no use for,
buys one gear upgrade within a budget, and collects what sold, expired or was won. Goals, movement and the rest of the bot are in
`docs/areas/playbots.md`; the auction house and mail themselves in `docs/areas/auction-house-bot.md` (the server's own seller-less
AH bot) and the economy feature. Configuration: `World:Playerbots:Economy` (every key live through `.reload config`).

References: vmangos `AuctionHouseHandler.cpp` (`HandleAuctionSellItem` :230-410, `HandleAuctionPlaceBid` :412-567,
`GetCheckedAuctionHouseForAuctioneer` :197-226), `AuctionHouseMgr.cpp` (`GetAuctionDeposit` :98-110, `SendAuctionSuccessfulMail`
:226 for the proceeds `bid + deposit - cut`, `GetAuctionHouseEntry` :580-614, `AuctionEntry::GetAuctionCut` :845-848),
`MailHandler.cpp` (`CheckMailBox` :65); the mangoszero auction house bot's `PricingStrategy::GetDefaultBuyPrice`
(`src/modules/Bots/ahbot/PricingStrategy.cpp:196-226`, `ApplyQualityMultiplier` :186-192, defaults of `ahbot.conf.dist.in:33-35`).
Neither vmangos nor mangoszero has a bot that sells to the house for itself, so the visit flow below is ArcaneCore's own; every rule it
must obey is the server's, enforced by the ordinary handlers.

## Pieces

| File (`src/ArcaneCore.World/Playerbots/Economy/`) | What it does |
|---|---|
| `PlayerbotEconomyOptions.cs` | `World:Playerbots:Economy`: `Enabled`, `ListChancePerVisit`, `MaxListingsPerBot`, `BuyUpgradeBudgetFraction`, `VisitIntervalMinutes`. |
| `PlayerbotEconomy.cs` | The world side, one per bot (owned by `PlayerbotBrain`): the visit as a state machine, one step per think. |
| `PlayerbotEconomyPlanner.cs` | The pure decisions: which items are listable, the listing slots, the visit budget, the price plan, the upgrade to buy, the parsing of an auction letter's subject, `PlayerbotRelistMemory`. |
| `PlayerbotAuctionPricing.cs` | The pure prices: reference price, vendor floor, undercut, start bid, deposit, whether a listing pays. |
| `PlayerbotEconomyWire.cs` | The client payloads (list, owner list, sell, bid, mail commands) and the decoders of the replies (`EconomyPackets` layouts); a short reply throws `FormatException` only. |
| `PlayerbotEconomyDestinations.cs` | Finding the faction auctioneer and the mailbox: live sightings first, then static spawn hints; the walker that goes there. |
| `PlayerbotGearScoreSeam.cs` | The one static delegate through which the bot values an item for its own slots (the progression scorer by default). |

## The visit

The brain asks `PlayerbotEconomy.Update` after the town goals (selling vendor trash, repair, ammunition) and before trainers; while a
visit runs the goal is reported as Vendor. A bot visits every `VisitIntervalMinutes`; its first visit is at a point of the first interval
chosen from its guid, so a fleet does not arrive together. A dropped visit (combat, death, a timeout, a fault) is retried after the
shorter of the interval and five minutes.

A visit begins only when the bot is in the world, alive, not a ghost, not in combat, not casting, the economy feature is on and the
settlement layer can act (`Settlements.CanAct`: no settlement of this bot pending, its character save neither held nor quarantined), and there is something to do: a listable item and
a successful listing roll, or a budget of at least 100 copper with a free bag slot (an upgrade to look for), or an unread auction letter
(`MSG_QUERY_NEXT_MAIL_TIME`). Otherwise the next visit is a full interval away. A visit lasts at most 300 s of world time.

1. **Travel.** The nearest faction auctioneer the bot can see (alive, friendly, not in combat, `NpcFlags.Auctioneer`). Out of reach it walks
   there with the ordinary navigation; with none in sight it walks to the nearest auctioneer spawn within 400 yards that its team may
   approach and that no live sighting covers yet. None found: the auction part is skipped and the mailbox may still be here.
2. **Owner list** (`CMSG_AUCTION_LIST_OWNER_ITEMS`). The total the house counts as the bot's own listings decides the free slots:
   `MaxListingsPerBot - total`. A list the bot cannot read lists nothing this visit (fail closed).
3. **Listing.** Items in the order of reference price times count, most valuable first. For each one the bot asks the house for every
   listing of that name (`CMSG_AUCTION_LIST_ITEMS`), plans a price, and sells (`CMSG_AUCTION_SELL_ITEM`, 8 hours) only if the plan pays
   and its deposit fits the budget left. It waits for the result (at most 30 s; the deadline drops the visit) before the next request.
4. **Buying** (only when the budget allows). One page of usable green-or-better weapons, one of armor; among the rows the bot can pay
   (a buyout, not its own, within the budget left and the money in hand, not refused lately) the one whose item gains the most for its
   slot, then the lower buyout, then the lower auction id; nothing gaining more than `PlayerbotItemScore.MinimumGain` is bought. It
   places a bid equal to the buyout (`CMSG_AUCTION_PLACE_BID`), which buys the auction out. At most one purchase per visit and two tries.
5. **Mailbox.** The letters (`CMSG_GET_MAIL_LIST`): see below. At most 20 letters per visit.

Listing deposits and the buyout both count against the visit budget: `BuyUpgradeBudgetFraction` of the money held when the visit began.

### What the bot lists and buys

Listed: an unbound or bind-on-equip green, blue or epic weapon or armor that is no upgrade for the bot (the gain of
`PlayerbotGearScoreSeam` is not above `MinimumGain`), and any Trade Goods item above poor quality. Never listed: poor and white items
(the vendor trash goals sell those), items that cannot leave the bot (`CanTransferOut`: bound, conjured, timed), food, drink and potions
the bot uses and items an open quest asks for, quest items and reagents, bags and ammunition, quest starters, profession tools, and
items with no vendor price. An entry whose listings expired twice is given up (`PlayerbotRelistMemory`, 64 entries, in memory; a sale
forgets the entry).

Bought: only an item that is an upgrade for one of the bot's own slots, by the same score the equipment goal wears by, so it is worn
once collected. The bot never buys to resell.

## Rules of the house the bot obeys

All of these are checked by the server's handlers whatever the bot does; the bot plans within them so that it does not lose money to
them or send what the house answers with silence.

* **Deposit** (`GetAuctionDeposit`): `sellPrice x count x (duration / 120 min)` times the house's deposit percent, at least
  `AuctionDepositMin`, times `AuctionRateDeposit`. The bot computes the same figure (`AuctionHouseRules.Deposit`) before it sells and
  checks the money taken afterwards: a different amount means the pricing assumption failed and it lists nothing more this visit.
* **Cut and proceeds**: a sale pays `bid + deposit - cut` (`SendAuctionSuccessfulMail`), the cut being the house's cut percent of the
  bid (`GetAuctionCut`). An expiry loses the deposit and returns the item by mail. A listing is made only if the deposit lost on one
  expiry stays below what a sale leaves over vendoring the stack (`buyout - cut - vendor price`), and the price never falls below the
  vendor floor after the cut.
* **Start bid not above the buyout** (`HandleAuctionSellItem` :242-246 refuses a bid above the buyout): the start bid is 80% of the
  buyout, at least 1 copper, never above it. No price above 2,000,000,000 copper (:236).
* **Silent refusals**: a bid the player cannot pay is answered with nothing (`HandleAuctionPlaceBid` :498-503), so the bot checks its
  money before it sends, and a request that creates no pending settlement and no result is treated as refused: that auction is left
  alone for 10 minutes.
* **Own auctions** (:459-471): the bot skips rows whose owner is itself.
* **Duration**: 8 hours, one of the three the client offers (2, 8, 24 hours).

### The price

The reference price of one unit follows the mangoszero auction house bot (`GetDefaultBuyPrice`): the larger of the vendor sell price and
a quarter of the buy price, raised to `DefaultMinPrice x level^2 / 10` (`DefaultMinPrice` 20, level the larger of item level and
required level) and to 100 copper, times the square root of the quality for any quality above poor (`ApplyQualityMultiplier`). The
quest-item branch of mangoszero is not needed: quest items are never listed. An item with no vendor sell price has reference 0 and is
never listed.

The asking price per unit is 1% (at least one copper) under the lowest other seller's buyout per unit for the same item (the bot's own
listings, bid-only auctions and other items matching the name do not count), else the reference price; never below the vendor floor
`ceil(sell x 100 / (100 - cut%)) + 1`, never above twice the reference, and the total never above the client price limit. A floor above
twice the reference means the item is not listed.

### The faction house only

The bot uses only the auction house of its own faction: `AuctionHouseRules.HouseFor` of the auctioneer's faction and the bot's team, and
never the neutral (goblin) house (`EconomyOptions.NeutralAuctioneerFactions`). A neutral auctioneer is not a destination, not a hint
and not used when the bot happens to stand beside one. The server's own handlers decide which house an auctioneer serves
(`IAuctioneerAccess`); the bot asks them rather than assuming.

## The mailbox

The bot handles only the auction house's letters without cash on delivery: a sale's money, an expired or cancelled listing's item, a
won auction's item. For each one it takes the money (`CMSG_MAIL_TAKE_MONEY`), then the item (`CMSG_MAIL_TAKE_ITEM`, only with a free bag
slot), then deletes the emptied letter (`CMSG_MAIL_DELETE`, its result is cosmetic and waited for 10 s). A letter it cannot empty (full
bags, a refused take) is left as it is and the bot moves on; it is tried again at the next visit.

A player's letter, a creature's or object's letter and any letter with cash on delivery are never touched: never read, taken, paid for,
returned or deleted. An unread letter of those kinds is not a reason for a visit for a day. An auction letter's subject
(`"entry:0:action"`) tells an expiry from a sale: an expiry counts against the item's entry in the relist memory, a sale forgets it.

The mailbox is found like the auctioneer (a spawned mailbox game object in sight, within interaction distance, the same rule as
`CheckMailBox`); the bot walks to it, or finishes the visit if there is none.

## Custody and no double spending

The bot cannot create money or items. Every action is a client packet through the world handlers (`WorldSession.TryManagedAction`);
the economy settlement layer applies each one as a single transaction.

* **A listing** moves the item from the bot's inventory into escrow, takes the deposit and inserts the auction in one commit. The bot
  re-checks that the queued item is still in its bags in the same amount before it sells.
* **A buyout** takes the money, deletes the auction (the delete carries the row it expects), and inserts the buyer's won letter with the
  escrow item and the seller's proceeds letter in one commit. The item leaves escrow only by being taken from that letter.
* **One operation at a time per bot.** `TryManagedAction` refuses a request while the bot has a settlement pending, and the visit waits for
  the result (or its 30 s deadline, which drops the visit) before it sends anything else.
* **One operation at a time per auction.** The economy keeps a busy set per auction id: a second bidder arriving while the first is
  committing is answered "item not found", and after the commit the auction is gone from the house. So two bots racing for one auction
  produce one buyout and one refusal; the refusal is remembered for 10 minutes. A seller-less listing of the server's AH bot (seller 0)
  is an ordinary row for this; it has no seller letter, and the item exists once, in the one won letter.
* **The AH bot's custody ledger** (`AuctionBotCustodyLedger`) records only what the AH bot itself creates and pays. A playerbot buying a
  seller-0 listing is a player buyout from the ledger's point of view, so the ledger's audit (one holder per item, one live buyout per
  auction, the daily budgets) is unaffected; a test pins this.
* **Unknown outcomes fail closed.** If a request ends without a result (the settlement ended, the packet was dropped, the deadline passed)
  the bot counts what left its purse against the visit budget, refuses that auction for 10 minutes and never repeats the request
  blindly; a settlement whose outcome is unknown stays with the settlement layer (the bot sends nothing while `Settlements.CanAct` is
  false: a settlement pending, or the character's save held or quarantined).
* **No guess at a reply.** A reply that cannot be decoded is treated as no answer: no listing without a readable owner list, no purchase
  without a readable page.

## Configuration

`World:Playerbots:Economy`, bound into `PlayerbotOptions.Economy` and read at every decision; `.reload config` changes the live object.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no visit starts; a running one is dropped. |
| `ListChancePerVisit` | `50` | Percent (0..100) that a visit lists anything: one roll per visit. 0 lists nothing (the bot may still buy and collect). |
| `MaxListingsPerBot` | `5` | The most auctions one bot has listed at once (0..50), from the house's own owner list. |
| `BuyUpgradeBudgetFraction` | `0.25` | The share (0..1) of the money held at the start of a visit that the visit may spend in all, deposits and buyout. 0 spends nothing. |
| `VisitIntervalMinutes` | `30` | Minutes between two visits of one bot (1..1440). |

The server's own economy options apply to the bot as to a player: `Economy:AuctionHouses` (deposit and cut percents),
`AuctionDepositMin`, `AuctionRateDeposit`, `AuctionRateCut`, `AuctionSilentRefusals`, `MailDeliveryDelaySeconds`. The reference list of
every key is `docs/reference/configuration.md`.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Economy/`: `PlayerbotAuctionPricingTests` (reference price, undercut, floor, deposit, whether a
listing pays), `PlayerbotEconomyPlannerTests` (what is listable, the listing slots and roll, the visit budget, the price plan, the
upgrade choice and its tie-breaks, the subject parser, the relist memory, the options' bounds), `PlayerbotEconomyWireTests` (every
decoder against the bytes `EconomyPackets` writes, and `FormatException` on malformed replies), and `PlayerbotEconomyWorldTests` (bots in a
real scenario world: a visit that lists up to the free slots, a sale and an expiry collected from the mailbox, COD and player letters left
alone, a full bag leaving an item letter in the box, and two bots racing for one auction, once a seller-0 listing, with the item existing
once and the AH bot's ledger audit clean).
