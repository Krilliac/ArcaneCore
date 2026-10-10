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
- **Item pool.** cMaNGOS draws its "profession" items from crafted items. ArcaneCore draws them from every item template that passes
  the cMaNGOS filters: no bind-on-pickup or quest items, no lootable or conjured items, and a non-zero `Value<Quality>` percent for
  the item's class. It also applies `MaxRequiredLevel`, the item-level cap (`MaxRequiredLevel` + 5 below 60) and `Blacklist`.
- **Loot sources.** Each sell pass also rolls loot tables (cMaNGOS `AddLootToItemMap`). There are nine sources: creature loot split by
  creature rank (`LootCreatureNormal`, `Elite`, `RareElite`, `WorldBoss`, `Rare`), plus `LootDisenchant`, `LootFishing`,
  `LootGameobject` and `LootSkinning`. Each setting is "minTables, maxTables, minRolls, maxRolls". Quest drops and rows behind a
  condition are skipped, because no player is looting. The drops join the item map and must pass the same filters.
- **`ahbot_items` (world schema 48).** Each row holds `item, value, add_chance, min_amount, max_amount`, as in cMaNGOS
  `ahbot_items` (which cMaNGOS keeps in the characters database):
  - `value` 0 means the bot never lists or buys the item. Any other value replaces the computed price per item.
  - `add_chance` > 0 replaces every other source: each pass lists `min_amount..max_amount` of the item with that percent chance,
    even if a filter would refuse it.
- **Random properties.** A minted item whose template has a `random_property` rolls one through the item random property source
  ("of the Bear"), and its enchantment slots are set (`RandomProperties`, default on).
- **Pricing.** Pricing follows cMaNGOS `CalculateBuyoutPrice`. The base is the vendor buy price. If there is none, or it is more
  than 5 times the sell price, the base is the sell price times 4 (white) or 5. The base is then multiplied by the quality/class
  percent, or by 100 % for items a vendor sells (`VendorValue`). Variance follows `ValueWithVariance`.
- **Buyer.** The buyer buys out a player auction when its buyout is below the bot's value times the stack times `BuyValuePercent`.
  cMaNGOS reads `Buy.Value` but never applies it; ArcaneCore applies it. A previous bidder gets the outbid refund letter. A living
  seller gets the normal successful-sale letter.
- **Bids** (`Bidding`, default on). When the buyout is above the bot's value but the next bid (current bid + outbid step, at
  least the start bid) is below it, the bot bids. A bot bid is stored with bidder 0 and a non-zero bid:
  - A player can outbid it as usual. No refund letter is sent, because the bot's copper never existed.
  - If the bid stands at expiry, the bot wins: the escrow item is destroyed and a living seller gets bid + deposit − cut.
  - The bot never bids on its own auctions or over its own standing bid. (cMaNGOS bids on its own auctions once a player has bid.)
- **Seller 0.** Bot listings belong to seller 0. A player who buys one receives the item and nobody is paid. An expired bot listing
  is destroyed with its item, with no letter.

## Custody (no duplication, no loss)

- **Minting.** The only way the bot creates an item is `MintEscrowItem`. That change is part of the same transaction as the
  item's `InsertAuction`. The store refuses a GUID that already exists, and its integrity pass refuses an ownerless item that does
  not have exactly one reference.
- **Ledger rows.** Each listing, buyout and bid first reserves a row in `AuctionBotCustodyLedger`. Each row has an idempotency key:
  `ahbot:list:<auction>`, `ahbot:buy:<auction>:<attempt>` or `ahbot:bid:<auction>:<attempt>`. The economy operation id is derived
  from that key, so a replayed transaction comes back AlreadyCommitted.
- **Durable ledger (characters schema 50, `ahbot_custody`).** A reserved row is written before its transaction starts. If that
  write fails, nothing starts and the row rolls back. Outcomes and prunes are written after the fact.
  - At startup the rows are loaded. Every row still Reserved, whatever happened to its process, is rechecked against
    `economy_operation` and settled.
  - The auction id allocator is raised above every id a row names, so a listing key is never reused.
  - A committed bid row stands until the auction settles. It is released (TerminalBack, budget freed) when a player outbids or
    buys out, or the seller cancels. After a restart, an auction that is gone counts as won.
- **One live row.** An item GUID is held by at most one live listing row. An auction is held by at most one live or committed
  buyout or bid row.
- **Unknown outcomes.** A row whose outcome is unknown stays Reserved and keeps counting against the budgets. The reconciler asks
  the store (`IsCommittedAsync`) whether the operation committed and settles the row with the answer.
- **Daily budgets.** `DailyItemBudget` and `DailyBuyBudgetCopper` cap what the bot can add to the economy in one UTC day.
- **Audit.** After every action the ledger is audited. A broken invariant stops the bot until restart.

## GM commands

All are `SEC_ADMINISTRATOR`, after cMaNGOS `ahbotCommandTable`. The root exists only when the bot is enabled.

- `.ahbot rebuild [all]`: expires the bot auctions that have no bid (with `all`, every bot auction; one with a player bid sells to
  that player), then runs sell passes to refill the houses.
- `.ahbot reload`: reloads the options (except `Enabled` and `UpdateIntervalSeconds`) and `ahbot_items`.
- `.ahbot status`: the bot's auctions per house and quality.
- `.ahbot item #item [$value [$addchance [$min [$max]]]] | #item reset`: shows or sets the item's `ahbot_items` row
  (cMaNGOS `SetItemData`: add chance capped at 100, an amount of 0 means the stack size).
- `.ahbot ledger` (ArcaneCore): the custody totals and the audit.

## Gaps

- Gameobject loot uses every `gameobject_loot_template` table. cMaNGOS only uses chests that are spawned and respawn.
- There is no dynamic max level and no `MaxItemLevel` option.
- After a seller is deleted, an auction with a standing bot bid is deleted with them. That bid row is then counted as spent.
- A rebuild's refill shares the economy's 16 transaction slots, so a large refill can take several ticks.
