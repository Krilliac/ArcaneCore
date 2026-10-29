# Area: Tuning tables

Plain data tables that hold numbers the server used to carry in config strings or in code: the auction house bot's value and loot
matrices, and the bots' transport networks (ship routes, lift regions, the Deeprun Tram). They are data, not rules: no events, no actions
(design: `docs/superpowers/specs/2026-10-28-rule-engine-design.md`, phase P5). World schema step 63
([schema reference](../reference/schema.md)).

## Tables

| Table | Key | Columns | Read by |
|---|---|---|---|
| `auction_bot_value` | `quality` 0-6, `item_class` 0-16 | `percent`: price as a percent of the vendor price (0 = never sold or bought) | the [auction house bot](auction-house-bot.md) pricing |
| `auction_bot_loot` | `source` (`creature normal`, `creature elite`, `creature rare elite`, `creature world boss`, `creature rare`, `disenchant`, `fishing`, `gameobject`, `skinning`) | `min_templates`, `max_templates`, `min_rolls`, `max_rolls` | the bot's loot sources |
| `playerbot_ship_route` | `entry` (a `gameobject_template` entry) | `means` 2 boat / 3 zeppelin, `access` 0 both / 1 alliance / 2 horde, `name` | which transports bots may ride |
| `playerbot_elevator_region` | `id` (read in id order) | `map_id`, `x`, `y` (the lift's centre), `low_zone`, `low_area`, `high_zone`, `high_area` (AreaTable ids, area 0 = any), `mesa_top`, `vault_low` (0/1), `name` | the bot lift network |
| `playerbot_tram_network` | `map_id` | `pair_yards`: how close an exit's landing must be to an entrance to be its way back | the Deeprun Tram network |

The shipped rows are the cMaNGOS `ahbot.conf.dist.in` defaults and the retail 1.12 transport geometry, number for number. A missing
auction value cell is 0, a missing loot source is off, a ship entry or lift without a row is not used, and without a tram row no tram
network is built.

## Where the rows come from

- At world start an **empty** table is seeded with the shipped rows (the same rows as `sql/content/world/<provider>/0003_tuning_tables.sql`,
  which can also be applied by hand; see [shipped content SQL](../../sql/content/README.md)). A table that holds any row is left as the
  operator has it, so edit the rows to tune; they survive upgrades.
- Every row is validated when it is loaded (quality and class in range, known loot source, min at most max, known ship means and access,
  finite lift centre with a zone for both levels, a positive tram distance, no duplicate keys). At start a bad row is logged and skipped;
  `.reload tuning` is stricter, like `.reload rules`: any bad row rejects the whole reload and the active rows stay.

## Reload

`.reload tuning` (also part of `.reload all`) reads and validates the tables off the world thread and swaps the catalog in between ticks.
The auction bot rebuilds its pool and loot sources on its next action; ship routes, lift regions and the tram network are read from the new
catalog on the next bot plan. `HotReload:EmptyTables = KeepLoaded` keeps the loaded rows only when every tuning table reads back empty; a single emptied table still applies. Otherwise a reload that reads a table back empty applies and switches that behaviour off (the success message names the empty tables); this lasts only until the next restart, which seeds an empty table again.

## Config keys during the move

The `AuctionHouseBot` value and loot keys (`ValuePoor` .. `ValueArtifact`, `LootCreatureNormal` .. `LootSkinning`) used to be the source of
these numbers. They are deprecated and default to empty, which takes the rows. A key that is set still overrides its row for the quality or
loot source it names, and the world logs a warning at start naming the key and the table it shadows (a key whose value equals the shipped row is reported as identical: remove it). A set key shadows the rows, so `.reload tuning` does not change what it covers. An explicit empty value (`ValueNormal = ""`) means "use the rows", the same as leaving the key out. The keys are removed in a later
release (docs and `DocKeyAuditTests` updated with them).
