# AuctionHouseBot options

43 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `AuctionHouseBot`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `BidMaxPercent` | uint | `90` | a whole number, 0 to 4294967295 | World | start | Highest starting bid as a percent of the buyout (cMaNGOS AuctionHouseBot.Bid.Max, 0-100, default 90). |
| `BidMinPercent` | uint | `75` | a whole number, 0 to 4294967295 | World | start | Lowest starting bid as a percent of the buyout (cMaNGOS AuctionHouseBot.Bid.Min, 0-100, default 75). |
| `Bidding` | bool | `true` | true \| false | World | start | Whether the buyer also bids (cMaNGOS AuctionBotBuyer: an auction whose buyout is above the bot's value, but whose next bid is below it, gets that next bid). Default true. A bot bid stands with no bidding character; if it wins, the item leaves the economy and the seller is paid as for any sale. |
| `Blacklist` | string | `(empty)` | text | World | start | Item entries the bot never lists or buys, comma-separated (cMaNGOS ahbot_items rows with value 0). |
| `BuyChance` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Percent chance a buy action buys anything (cMaNGOS AuctionHouseBot.Chance.Buy, 0-100, default 10). |
| `BuyValuePercent` | uint | `80` | a whole number, 0 to 4294967295 | World | start | The most the buyer pays, as a percent of the bot's own value of the item (cMaNGOS AuctionHouseBot.Buy.Value, 0-200, default 80). |
| `DailyBuyBudgetCopper` | uint | `10000000` | a whole number, 0 to 4294967295 | World | start | Custody limit: most copper the buyer may pay out per UTC day, all houses together (MaNGOS Zero custody; 0 = the buyer never buys). |
| `DailyItemBudget` | uint | `5000` | a whole number, 0 to 4294967295 | World | start | Custody limit: most items the bot may create into the houses per UTC day, all houses together (MaNGOS Zero custody; 0 = none). |
| `DynamicIgnoreGmAccounts` | bool | `false` | true \| false | World | start | With `DynamicMaxRequiredLevel`, leave characters of staff accounts (security above player) out of the highest-level count (cMaNGOS AuctionHouseBot.Level.IgnoreGmAccounts, default false). When only staff characters are online the highest of their levels is the fallback, as in cMaNGOS. |
| `DynamicMaxRequiredLevel` | bool | `false` | true \| false | World | start | Take the required level cap from the highest level of the characters online (cMaNGOS AuctionHouseBot.Level.DynamicMaxRequired, default false). With nobody online the cap in force stays; `MaxRequiredLevel` is the start value. |
| `DynamicRefreshMinutes` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Minutes between refreshes of the dynamic level cap (cMaNGOS AuctionHouseBot.Level.DynamicRefreshInterval, at least 1, default 10). |
| `Enabled` | bool | `false` | true \| false | World | start | Run the bot (default false). With false nothing is listed, bought or minted. |
| `Houses` | string | `2,6,7` | text | World | start | The auction houses the bot serves, comma-separated AuctionHouse.dbc ids (2 Alliance, 6 Horde, 7 neutral; cMaNGOS serves all three). |
| `LootCreatureElite` | string | `(empty)` | text | World | start | Deprecated override of the "creature elite" row (rank 1, elite creatures); see `LootCreatureNormal`. |
| `LootCreatureNormal` | string | `(empty)` | text | World | start | Deprecated override of the `auction_bot_loot` row "creature normal" (cMaNGOS AuctionHouseBot.Loot.*): "minTemplates, maxTemplates, minRolls, maxRolls". Each sell pass draws between min and max loot tables of the source (a negative min makes an empty draw more likely) and rolls each one minRolls..maxRolls times; every item that drops is added to the listing pool. "0,0,0,0" turns a source off. Empty (the default) takes the rows; a value overrides them and logs a warning at start. The key goes in a later release. |
| `LootCreatureRare` | string | `(empty)` | text | World | start | Deprecated override of the "creature rare" row (rank 4, rare creatures); see `LootCreatureNormal`. |
| `LootCreatureRareElite` | string | `(empty)` | text | World | start | Deprecated override of the "creature rare elite" row (rank 2); see `LootCreatureNormal`. |
| `LootCreatureWorldBoss` | string | `(empty)` | text | World | start | Deprecated override of the "creature world boss" row (rank 3); see `LootCreatureNormal`. |
| `LootDisenchant` | string | `(empty)` | text | World | start | Deprecated override of the "disenchant" row (disenchant_loot_template); see `LootCreatureNormal`. |
| `LootFishing` | string | `(empty)` | text | World | start | Deprecated override of the "fishing" row (fishing_loot_template); see `LootCreatureNormal`. |
| `LootGameobject` | string | `(empty)` | text | World | start | Deprecated override of the "gameobject" row (the gameobject_loot_template tables of objects that are spawned and respawn, as cMaNGOS); see `LootCreatureNormal`. |
| `LootSkinning` | string | `(empty)` | text | World | start | Deprecated override of the "skinning" row (skinning_loot_template); see `LootCreatureNormal`. |
| `MaxAuctionsPerHouse` | uint | `2000` | a whole number, 0 to 4294967295 | World | start | Most bot auctions open in one house at once; a sell action never lists past it (ArcaneCore guard, 0 = no listing). |
| `MaxItemLevel` | uint | `0` | a whole number, 0 to 4294967295 | World | start | Highest item level of a listed item (ArcaneCore, 0-1000). 0 (the default) derives it as cMaNGOS does: no cap when the required level cap is 60 or more, else that cap + 5. |
| `MaxRequiredLevel` | uint | `60` | a whole number, 0 to 4294967295 | World | start | Highest required level of a listed item (cMaNGOS AuctionHouseBot.Level.MaxRequired, 1-255, default 60). Below 60 the item level is capped at this + 5. |
| `RandomProperties` | bool | `true` | true \| false | World | start | Roll random properties ("of the Bear") for minted items whose template has them (cMaNGOS Item::CreateItem). Default true. |
| `SellChance` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Percent chance a sell action lists anything (cMaNGOS AuctionHouseBot.Chance.Sell, 0-100, default 10). |
| `StackPercentMax` | uint | `50` | a whole number, 0 to 4294967295 | World | start | Largest stack as a percent of the item's stack size (cMaNGOS Items.Profession fourth value: 50). |
| `StackPercentMin` | uint | `0` | a whole number, 0 to 4294967295 | World | start | Smallest stack as a percent of the item's stack size (cMaNGOS Items.Profession third value: 0; at least one item). |
| `TemplatesPerSellMax` | int | `90` | a whole number, -2147483648 to 2147483647 | World | start | Most item templates drawn per sell action (cMaNGOS AuctionHouseBot.Items.Profession second value: 90); at most 10000. |
| `TemplatesPerSellMin` | int | `80` | a whole number, -2147483648 to 2147483647 | World | start | Fewest item templates drawn per sell action (cMaNGOS AuctionHouseBot.Items.Profession first value: 80); at least -10000 (a negative value makes an empty draw likelier). |
| `TimeMaxHours` | uint | `24` | a whole number, 0 to 4294967295 | World | start | Longest listing in hours (cMaNGOS AuctionHouseBot.Time.Max, 1-72, default 24). |
| `TimeMinHours` | uint | `2` | a whole number, 0 to 4294967295 | World | start | Shortest listing in hours (cMaNGOS AuctionHouseBot.Time.Min, 1-72, default 2). |
| `UpdateIntervalSeconds` | uint | `20` | a whole number, 0 to 4294967295 | World | start | Seconds between bot actions. Each action handles one house, alternating the sell then the buy pass over the houses (cMaNGOS AuctionHouseBot::Update m_houseAction). |
| `ValueArtifact` | string | `(empty)` | text | World | start | Deprecated override of the quality 6 (artifact) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Artifact). |
| `ValueEpic` | string | `(empty)` | text | World | start | Deprecated override of the quality 4 (epic) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Epic). |
| `ValueLegendary` | string | `(empty)` | text | World | start | Deprecated override of the quality 5 (legendary) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Legendary). |
| `ValueNormal` | string | `(empty)` | text | World | start | Deprecated override of the quality 1 (common, white) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Normal). |
| `ValuePoor` | string | `(empty)` | text | World | start | Deprecated override of the `auction_bot_value` rows (quality 0, poor): price of poor items per item class as a percent of the vendor price, 17 comma-separated values (cMaNGOS AuctionHouseBot.Value.Poor; 0 = never sold or bought). Empty (the default) takes the rows; a value overrides them and logs a warning at start. The key goes in a later release. |
| `ValueRare` | string | `(empty)` | text | World | start | Deprecated override of the quality 3 (rare, blue) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Rare). |
| `ValueUncommon` | string | `(empty)` | text | World | start | Deprecated override of the quality 2 (uncommon, green) `auction_bot_value` rows; see `ValuePoor` (cMaNGOS AuctionHouseBot.Value.Uncommon). |
| `ValueVariance` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Random price spread in percent (cMaNGOS AuctionHouseBot.Value.Variance, 0-100, default 10). |
| `VendorValue` | bool | `true` | true \| false | World | start | Price an item that a vendor sells at 100 % of the vendor price, so it cannot be bought and resold for profit (cMaNGOS AuctionHouseBot.Value.Vendor, default true). |
