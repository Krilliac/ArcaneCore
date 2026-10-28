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

| File (`src/ArcaneCore.Playerbots/Economy/`) | What it does |
|---|---|
| `PlayerbotBudget.cs`, `PlayerbotServiceIndex.cs`, `PlayerbotRidingTrips.cs`, `PlayerbotMount.cs` (folder `Playerbots/Budget/`) | The budget, the shared trainer/vendor index, the riding and mount-vendor trips, and mounting (`World:Playerbots:Budget`, `World:Playerbots:Mounts`). |
| `PlayerbotEconomyOptions.cs` | `World:Playerbots:Economy`: `Enabled`, `ListChancePerVisit`, `MaxListingsPerBot`, `BuyUpgradeBudgetFraction`, `VisitIntervalMinutes`, `BankDeposit`, `BuyBankSlots`, `BuyReagents`, `ReagentSellMultiple`, `ReagentAllowanceCopper`. |
| `PlayerbotBank.cs` (folder `Playerbots/`) | The bank errand while the bot stays at its capital home: deposit goods it does not need, buy a bank bag slot when the bank is nearly full. |
| `PlayerbotEconomy.cs` | The world side, one per bot (owned by `PlayerbotBrain`): the visit as a state machine, one step per think. |
| `PlayerbotEconomyPlanner.cs` | The pure decisions: which items are listable, the listing slots, the visit budget, the price plan, the upgrade to buy, the reagent to buy, the parsing of an auction letter's subject, `PlayerbotRelistMemory`, `PlayerbotBoughtMemory`. |
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
a successful listing roll, or a budget of at least 100 copper with a free bag slot (an upgrade to look for), or a reagent a wanted recipe uses that the bot carries fewer of
than one craft needs (with `BuyReagents` on, a budget of at least 100 copper and a free bag slot; an entry bought and not yet collected from its letter, or one a vendor stocks, is not a shortfall), or an unread auction letter
(`MSG_QUERY_NEXT_MAIL_TIME`). Otherwise the next visit is a full interval away. A visit lasts at most 300 s of world time.

1. **Travel.** The nearest faction auctioneer the bot can see (alive, friendly, not in combat, `NpcFlags.Auctioneer`). Out of reach it walks
   there with the ordinary navigation; with none in sight it walks to the nearest auctioneer spawn within 400 yards that its team may
   approach and that no live sighting covers yet. None found: the auction part is skipped and the mailbox may still be here.
2. **Owner list** (`CMSG_AUCTION_LIST_OWNER_ITEMS`). The total the house counts as the bot's own listings decides the free slots:
   `MaxListingsPerBot - total`. A list the bot cannot read lists nothing this visit (fail closed).
3. **Listing.** Items in the order of reference price times count, most valuable first. For each one the bot asks the house for every
   listing of that name (`CMSG_AUCTION_LIST_ITEMS`), plans a price, and sells (`CMSG_AUCTION_SELL_ITEM`, 8 hours) only if the plan pays
   and its deposit fits the budget left. It waits for the result (at most 30 s; the deadline drops the visit) before the next request.
4. **Reagents** (only with `BuyReagents` on, a budget of at least 100 copper and a free bag slot). The reagents of the recipes the bot wants to craft (the same
   spellbook read as the vendor supply needs, `PlayerbotVendorSupplyNeeds.Reagents`: the largest count one craft uses) that it carries
   fewer of than one craft uses are its shortfalls, except an entry it bought and has not yet collected and an entry a vendor stocks (see below). For each, in item id
   order starting at a position that rotates with the visit number (so later entries get their turn) and at most four searches a visit, one a think, it asks the
   house for the listings of the item's name and class (`CMSG_AUCTION_LIST_ITEMS`). Of the rows it can pay (a buyout, not its own, within
   the budget left, the money in hand and what the budget leaves spendable, not refused lately, not a vendor-stocked item) whose count is at
   most `ReagentOverbuyFactor` (2) times the shortfall and whose buyout per unit, rounded up, is at most the item's vendor sell price times
   `ReagentSellMultiple` plus `ReagentAllowanceCopper` (`PlayerbotEconomyPlanner.ReagentUnitCap`; an item with no vendor sell price is never bought), it takes the one that covers the shortfall first; among covering rows the lower total
   buyout, then the lower price per unit (compared exactly), then the lower auction id; among rows that do not cover the shortfall the
   lowest price per unit, then the lower buyout, then the lower auction id. It buys it out with a bid equal to the buyout. At most two
   purchases a visit and two failed tries; a bid waits for its result like an upgrade's, then the visit returns to this phase. The
   purchase is noted in `PlayerbotBoughtMemory`. `BuyReagents` is read again at this step, so a live reload that turns it off ends the
   phase at once, mid-visit.
5. **Buying** (only when the budget allows). One page of usable green-or-better weapons, one of armor; among the rows the bot can pay
   (a buyout, not its own, within the budget left and the money in hand, not refused lately) the one whose item gains the most for its
   slot, then the lower buyout, then the lower auction id; nothing gaining more than `PlayerbotItemScore.MinimumGain` is bought. It
   places a bid equal to the buyout (`CMSG_AUCTION_PLACE_BID`), which buys the auction out. At most one purchase per visit and two tries.
6. **Mailbox.** The letters (`CMSG_GET_MAIL_LIST`): see below. At most 20 letters per visit.

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

Reagents are bought only to be used. The listing reservation also covers every reagent of a recipe the bot wants (whatever it carries,
with `BuyReagents` on or off) and every reagent entry the bot bought at the house in the last 24 hours (gear upgrades are not recorded; `PlayerbotBoughtMemory`: 64 entries, in
memory, the oldest purchase goes first, lost on restart), so there is no buy-then-relist loop. The won item comes to the bot by mail,
which the same visit collects. Each purchase also adds one to the entry's awaiting count, and each "won" auction letter the mail step takes the item
of lowers it by one (`NoteCollected`, never below 0); while the count is above 0, the entry is not a reagent shortfall, so a letter the visit did not collect (no mailbox in sight,
walk failures, visit cap) does not make the next visit buy the same reagent again. The count ends with the 24 hour hold, so a lost letter
cannot block buying forever.

Anti-exploit rules for reagents. Vendor-stocked items are never bought at the house: the index of the world data
(`PlayerbotServiceIndex.IsVendorStocked`) holds every item any vendor with a spawn sells, and a vendor's goods (thread, flux, salt,
vials) cost no more than the vendor's price, so a player listing them above it would otherwise farm gold from the
bots. A vendor-stocked entry is not a shortfall for the house, so no visit starts for it. Without the index (no creature content or
spell table loaded), or with an index that lists no vendor-stocked item, every reagent counts as vendor stocked: the bot buys no
reagents at the house (fail closed). A row is eligible only
when its count is at most twice the shortfall, so a shortfall of 1 never buys a stack of 20 whose surplus is then held back from
listing. The per-unit cap follows the vendor sell price, not the listing reference: `SellPrice * ReagentSellMultiple + ReagentAllowanceCopper`
copper (defaults 4 and 10), so a gathered material is never bought far above what the vendor pays for it; an item with no vendor sell price is never
bought. Example from the classic-db reference data: Linen Cloth sells for 13 copper (cap 62 copper at the defaults, item level 5), Silk Cloth
for 150 (610, item level 30), Mageweave Cloth for 250 (1010, item level 40) and Runecloth for 400 (1610, item level 50). The visit budget
still applies.

A bid whose settlement ends without a result packet, or that settles at once without leaving a pending settlement, is judged by the
money: if the bot paid, the buyout counts as bought (a reagent is noted in the bought memory, a gear bid counts as a purchase); if it paid
nothing, the auction is refused for a while. A reagent bid
still unsettled at the operation deadline is noted as bought before the visit is dropped, so an unknown outcome cannot make the next
visit buy the same reagent again.

## The bank

While a bot stays at its capital home (`TownStay.Capital`, town progression), the brain asks `PlayerbotBank.Update` after the auction
visit. At most once a minute it checks whether a visit has work: something to deposit (trade goods and reagents no spell it knows uses,
gems, recipes, stackable curios; never gear, consumables, ammunition, bags, quest items, grays, the Hearthstone or anything the town
controller protects) or a bank slot worth buying. A visit walks to a banker of the capital (one in sight first, else the nearest
approachable banker spawn within 400 yards of the home's centre), opens the bank with `CMSG_BANKER_ACTIVATE`, deposits one item per
think with `CMSG_AUTOBANK_ITEM` (at most 16 per visit; a refused deposit is not repeated) and may buy one slot with `CMSG_BUY_BANK_SLOT`.
A visit lasts at most 180 s; the next is at least 30 minutes later. The goal is reported as Vendor, so a stall sets the banker aside.

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

## Budget: what a bot saves for

Every spend site asks the bot's `PlayerbotBudget` what it may spend; nothing is hard-coded: every cost is read from the world data
(`npc_trainer.spellcost`, `npc_vendor` + `item_template.buy_price`) through the same pricing the trainer window and vendor list use, with
the reputation and honor discount, and is cached in one shared `PlayerbotServiceIndex` per world (built once on the world thread, no
world-DB query per think). The lines, in priority order, and what each spend site may spend:

| Priority | Line | Reserved cost | Spend sites |
|---|---|---|---|
| 1 | Repair | repairing all worn gear, while it is badly worn | repairs are never blocked |
| 2 | Essentials | no line: a purchase may always cost up to `World:Playerbots:Budget:EssentialsAllowanceCopper` | food, drink, ammunition |
| 3 | Class training | the class spells learnable now plus those opening within `World:Playerbots:Budget:ClassLevelsAhead` levels, at the class trainer's prices | class training spends all the money the repair line leaves |
| 4 | Mount | the cheapest mount the bot can use, once it can ride and owns none | the mount purchase |
| 5 | Riding | the next riding tier's training plus the cheapest mount that tier lets the bot use | riding training |
| 6 | Professions | none: managed bots have no professions | not reserved |
| 7 | Discretionary | none | auction house purchases (`BuyUpgradeBudgetFraction` applies to what the lines above leave, but a purchase may always use up to `World:Playerbots:Budget:DiscretionaryFloorFraction` of the bot's money, so a reserve it cannot reach does not stop its buying for good). Auction listing deposits are not purchases and are never limited by a reserve |

A spend of one kind may cost the bot's money less the lines of the kinds before it, so saving for a mount never holds back class
training (class outranks riding) and class training's save-ahead keeps lower priorities from spending the money its next spells will need.
Selling greys and junk, and listing loot at the auction house, are never limited by a reserve. `.playerbot inspect` shows `BOTINSPECT budget=...`: money, the total reserved, the
part saved and free, the repair and class costs, the riding step (`none`, `distant`, `saving`, `train`, `buymount`, `done`) with its tier,
training and mount cost, vendor and trainer entries, and the next step.

## Mounts

A bot within `World:Playerbots:Mounts:SaveLevelsAhead` levels of the next riding tier's required level (the `npc_trainer.reqlevel` of the Mounts trainer
of its race) keeps that tier's training and the cheapest mount it can use reserved: a vendor item that requires a riding skill or whose
on-use spell applies the mounted aura (78), usable by its race and class, by the tier's level, and by the skill rank the tier gives (read
from the ranks the mounts ask for, ascending). When the level is reached and the spendable money covers both, the bot walks to a Mounts
trainer of its race (`PlayerbotRidingTrips`, goal `Mount`) and buys the riding spell through the ordinary trainer handler (`IsTrainerOf`:
its race, or Exalted); then, owning no mount, to the mount vendor for the cheapest mount it can use. Apprentice and Journeyman tiers use
the same logic, driven by the rows; a mount counts only if it fits the highest learned tier (a Journeyman bot still saves for the epic mount while it owns the old one). A trainer or vendor on another map than the bot's is no plan: with none on its map nothing is reserved. The rank ladder (which riding skill rank each tier gives) is read from the ranks the vendor mounts ask for, not from the skill the learned riding spell grants, so a data set whose mounts do not name riding-skill ranks falls back to "first tier only" for mounts with no skill. A bot on a leg of at least `World:Playerbots:Mounts:MountMinTravelYards` outdoors, out of combat, not in an instance or
battleground, not swimming or in a form that cannot ride (a warrior stance can), that knows a mount spell or carries a mount item, mounts through CMSG_CAST_SPELL /
CMSG_USE_ITEM (the server's `MountCastCheck` decides); a refusal backs it off 30 seconds, doubling to ten minutes. The server dismounts a
caster but refuses an attack from a mounted player and does not dismount for an NPC or a corpse, so the managed packets that talk to an
NPC, loot or attack take the mount off first (`PlayerbotMount.DismountForAction`).

## Configuration

`World:Playerbots:Economy`, bound into `PlayerbotOptions.Economy` and read at every decision; `.reload config` changes the live object.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no visit starts; a running one is dropped. |
| `ListChancePerVisit` | `50` | Percent (0..100) that a visit lists anything: one roll per visit. 0 lists nothing (the bot may still buy and collect). |
| `MaxListingsPerBot` | `5` | The most auctions one bot has listed at once (0..50), from the house's own owner list. |
| `BuyUpgradeBudgetFraction` | `0.25` | The share (0..1) of the money held at the start of a visit that the visit may spend in all, deposits and buyout. 0 spends nothing. |
| `VisitIntervalMinutes` | `30` | Minutes between two visits of one bot (1..1440). |
| `BankDeposit` | `true` | Deposit goods the bot does not need at a banker while it stays at its capital home. |
| `BuyBankSlots` | `true` | Buy the next bank bag slot when the bank has fewer than 5 free slots and the price is at most 40% of the bot's gold. |
| `BuyReagents` | `true` | Buy the reagents of wanted recipes the bot carries fewer of than one craft uses: at most 2 auctions a visit, never a vendor-stocked item, at most twice the shortfall in count, at most the per-unit cap `ReagentSellMultiple` times the vendor sell price plus `ReagentAllowanceCopper`, within the visit budget. Off: none is searched or bought, also when turned off mid-visit. |
| `ReagentSellMultiple` | `4` | The most a bot pays per unit for a reagent at the house, as a multiple (1..20) of the item's vendor sell price, plus `ReagentAllowanceCopper`; an item without a vendor sell price is never bought. |
| `ReagentAllowanceCopper` | `10` | Copper per unit (0..10000) added to `ReagentSellMultiple` times the vendor sell price in the reagent buy cap. |

`World:Playerbots:Budget` (live):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: nothing is reserved; every spend site may spend all the bot's money. |
| `ClassLevelsAhead` | `2` | Levels ahead of its own (0..10) for which the bot reserves class spells. |
| `EssentialsAllowanceCopper` | `10000` | Copper a food, drink or ammunition purchase may always cost, whatever is reserved. |
| `DiscretionaryFloorFraction` | `0.10` | The share (0..1) of its money a bot may always spend on auction purchases even when the reserves leave nothing; 0 lets the reserves take everything. |
| `RefreshSeconds` | `30` | Seconds between recomputations of a bot's reserves (1..3600); a level change recomputes at once. |

`World:Playerbots:Mounts` (live):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no riding reserve, no trips; a bot never mounts. |
| `SaveLevelsAhead` | `4` | A bot within this many levels (0..20) of the next riding tier's required level reserves its cost. |
| `MountMinTravelYards` | `80` | Walks of at least this many yards (1..100000) are ridden. |

The server's own economy options apply to the bot as to a player: `Economy:AuctionHouses` (deposit and cut percents),
`AuctionDepositMin`, `AuctionRateDeposit`, `AuctionRateCut`, `AuctionSilentRefusals`, `MailDeliveryDelaySeconds`. The reference list of
every key is `docs/reference/configuration.md`.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Economy/`: `PlayerbotAuctionPricingTests` (reference price, undercut, floor, deposit, whether a
listing pays), `PlayerbotEconomyPlannerTests` (what is listable, the listing slots and roll, the visit budget, the price plan, the
upgrade choice and its tie-breaks, the reagent shortfalls and choice with its per-unit cap (the vendor sell price rule and its options), the bought memory, the subject parser, the relist memory, the options' bounds), `PlayerbotEconomyWireTests` (every
decoder against the bytes `EconomyPackets` writes, and `FormatException` on malformed replies), and `PlayerbotEconomyWorldTests` (bots in a
real scenario world: a visit that lists up to the free slots, a sale and an expiry collected from the mailbox, COD and player letters left
alone, a full bag leaving an item letter in the box, and two bots racing for one auction, once a seller-0 listing, with the item existing
once and the AH bot's ledger audit clean; a visit that buys the in-cap reagent pair, passes over the over-cap pair and the single, and collects it in the same visit; a visit that buys nothing when the only covering row is over the cap; a bid that settles at once without a pending settlement and took the money, booked as bought (reagent and gear), and one that took none, refused; a visit that buys nothing for a vendor-stocked entry with an in-cap covering row listed; a visit that buys nothing when `BuyReagents` is turned off after the visit started; none bought with `BuyReagents` off; a needed reagent never listed).
