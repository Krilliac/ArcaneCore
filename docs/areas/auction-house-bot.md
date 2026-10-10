# Auction house bot

`AuctionBotFeature` (src/ArcaneCore.World/Economy/AuctionBotFeature.cs) lists and buys on the auction houses. It ports cMaNGOS
AuctionHouseBot (src/game/AuctionHouseBot/AuctionHouseBot.cpp, ahbot.conf.dist.in) and adds the custody ideas of MaNGOS Zero
(src/game/AuctionHouseBot/CustodyLedger.h, CustodyService, CustodyReconciler). It is **off by default**: set
`AuctionHouseBot:Enabled` to `true`. Every key is listed in [the configuration reference](../reference/configuration.md#auctionhousebot)
and checked by `check-config` (`AuctionBotConfigChecks`).

## Behaviour

- **Cycle.** Every `UpdateIntervalSeconds` the bot takes one action. It runs the sell pass over each house in `Houses`, then the buy
  pass over each house, as cMaNGOS `Update` does with `m_houseAction`. Each pass first rolls `SellChance` or `BuyChance`.
- **Seller.** It draws `TemplatesPerSellMin..Max` templates from the item pool. It keeps every white, 1 in 2 greens, 1 in 4 blues and
  1 in 8 purples, and never draws poor items. Each kept draw gets a stack of `StackPercentMin..Max` percent of the stack size, and
  each entry's total is split into full stacks. The buyout is the value with variance times the count. The starting bid is
  `BidMinPercent..BidMaxPercent` of the buyout, and the duration is `TimeMinHours..TimeMaxHours`. A house never holds more than
  `MaxAuctionsPerHouse` bot auctions.
- **Item pool.** cMaNGOS draws items from loot tables. ArcaneCore draws from every item template that passes the cMaNGOS filters:
  no bind-on-pickup or quest items, no lootable or conjured items, and a non-zero `Value<Quality>` percent for the item's class. It
  also applies `MaxRequiredLevel`, the item-level cap (`MaxRequiredLevel` + 5 below 60) and `Blacklist`.
- **Pricing.** Pricing follows cMaNGOS `CalculateBuyoutPrice`. The base is the vendor buy price. If there is none, or it is more
  than 5 times the sell price, the base is the sell price times 4 (white) or 5. The base is then multiplied by the quality/class
  percent, or by 100 % for items a vendor sells (`VendorValue`). Variance follows `ValueWithVariance`.
- **Buyer.** The buyer buys out a player auction when its buyout is below the bot's value times the stack times `BuyValuePercent`.
  cMaNGOS reads `Buy.Value` but never applies it; ArcaneCore applies it. A previous bidder gets the outbid refund letter. A living
  seller gets the normal successful-sale letter. cMaNGOS also places plain bids; ArcaneCore's buyer does not, because a bot bid has
  no character to refund.
- **Seller 0.** Bot listings belong to seller 0. A player who buys one receives the item and nobody is paid. An expired bot listing
  is destroyed with its item, with no letter.

## Custody (no duplication, no loss)

- **Minting.** The only way the bot creates an item is `MintEscrowItem`. That change is part of the same transaction as the
  item's `InsertAuction`. The store refuses a GUID that already exists, and its integrity pass refuses an ownerless item that does
  not have exactly one reference.
- **Ledger rows.** Each listing and buyout first reserves a row in `AuctionBotCustodyLedger`. Each row has an idempotency key:
  `ahbot:list:<auction>` for a listing, `ahbot:buy:<auction>:<attempt>` for a buyout. The economy operation id is derived from that
  key, so a replayed transaction comes back AlreadyCommitted.
- **One live row.** An item GUID is held by at most one live listing row. An auction is held by at most one live or committed
  buyout row.
- **Unknown outcomes.** A row whose outcome is unknown stays Reserved and keeps counting against the budgets. The reconciler asks
  the store (`IsCommittedAsync`) whether the operation committed and settles the row with the answer.
- **Daily budgets.** `DailyItemBudget` and `DailyBuyBudgetCopper` cap what the bot can add to the economy in one UTC day.
- **Audit.** After every action the ledger is audited. A broken invariant stops the bot until restart.

## Gaps

- The ledger lives in memory. After a restart the durable auction rows and `economy_operation` are the truth, but an Unknown row
  from the previous run is not re-checked.
- There is no `ahbot_items` table of per-item overrides (value, add chance, amounts), only `Blacklist`. There are no loot-table
  sources, no dynamic max level, no GM `.ahbot` commands, no bidding, and no random properties on minted items.
