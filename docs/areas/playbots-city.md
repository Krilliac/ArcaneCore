# Playerbot capital city trips

How an autonomous managed playerbot makes a trip to its faction's capital city to use what the field does not have: repair and vendors,
the class trainer, the auction house and mailbox, the bank and the innkeeper. Goals, movement and the rest of the bot are in
`docs/areas/playbots.md`; the auction house is `docs/areas/playbots-economy.md`. Configuration: `World:Playerbots:City`.
Code: `src/ArcaneCore.Playerbots/City/`, options in `src/ArcaneCore.World.Contracts/Playerbots/City/`.

**State of this change.** This page covers the foundation that has landed: the capital catalog, the locator it is built with, the trip
limiter and the options. The trip goal, the itinerary stages (vendor, auction house, bank, innkeeper), the hearthstone leg and the brain
wiring land with the capital goals lane; the bank and hearthstone code that already exists on the base (`PlayerbotBank`,
`PlayerbotHearthstone`, `World:Playerbots:Economy:BankDeposit`, `World:Playerbots:Economy:BuyBankSlots`) is reused there, so `City` has no bank keys of its own.

## The capital catalog (`PlayerbotCapitalCatalog`, `PlayerbotCapitals`)

Nothing about a city is written down. A capital is a zone the area table flags `AreaFlags.Capital` (the flag the rest rule reads,
`RestService.OnZoneEntered`). For each capital map the catalog takes the creature spawns whose template has a service npc flag
(auctioneer, banker, innkeeper, trainer, vendor, repair, flight master), asks the locator which zone each stands in, and keeps the ones
in a capital zone within 700 yards of the middle of its auctioneers and bankers (a capital needs one of them). Class trainers
(`trainer_type` 0) and profession trainers are kept apart, with the class a class trainer teaches. Mailboxes are the mailbox game object
spawns within 220 yards of those auctioneers and bankers. Each capital records its map, zone, name, centre, radius (the farthest service
spawn, held to 60..700 yards), an **anchor** (the auctioneer or banker nearest the centre, where a trip walks to), and the teams it is
friendly to: the share of its auctioneers, bankers and innkeepers not hostile to a Human (Alliance) or Orc (Horde) faction template is at
least half; without faction templates, the area's own team.

The build runs once on a pool thread and is posted back to the world thread; it is rebuilt when the creature definitions, area table,
game objects or faction templates change. Until it is in, and when the world has no terrain data, the catalog is absent and no trip can
start. No think scans spawns: the catalog is immutable and shared by every bot. Which individual NPC a bot may use is still decided by
the live reaction rules and the interaction checks of the handlers.

## The locator (`PlayerbotCapitalLocator`)

Ironforge and Undercity are built as one big model: the terrain's area flags there say Dun Morogh and Tirisfal Glades, and only the WMO
group a position stands in says which city it is (vmangos `TerrainInfo::GetZoneAndAreaId` with vmaps). The locator therefore asks, for
a position, first the vmaps (`ILineOfSight.TryGetAreaInfo`, through a private `VMapManager`, never the world's) for the WMO group,
found from 1 yard above the position and rejected when terrain rises to the point (`TerrainInfo.WmoGroupCounts`, the predicate
`TerrainInfo.TryGetWmoGroup` uses: a spawn on the ground over a building is not in it), maps (root id, name set, group id) to an AreaTable
id with the world's `TerrainManager.WmoAreas` (`wmo_area_template`, the client's `WMOAreaTable.dbc`; the retail rules: exact key, no
fallback, the first row of a triple wins and a row with area 0 still shadows the second), and otherwise falls back to the terrain's area
flag. A tile without terrain data answers (0, 0) unless a WMO group is found. Terrain tiles are read once each through
`TerrainManager.ReadTileDetached` (pooled read, no tile cache; a missing tile is `TerrainTile.Empty`, unknown) and kept in a 24 tile
cache; vmap tiles in a window of 6. Without vmaps or `wmo_area_template` rows the answer is terrain only and a warning is logged: those
two cities are not found. It has no key of its own: the rows come from the world database (`docs/areas/content-import.md`).

## The limiter (`PlayerbotCityLimiter`)

A world-wide brake, one instance per world (a weak side table), world thread only: at most `MaxTripsPerCapital` trips under way to one
capital and at most `MaxStartsPerMinute` starting for it in any minute, so a restart or a level-up wave does not send every bot to the
same auctioneer. A trip holds a slot until its controller releases it; a slot whose holder never did (a bot that logged out mid-trip)
expires after `TripCapMs` (25 minutes, the longest a trip can last). Time is the world clock in milliseconds and the arithmetic is
wrap-safe.

## Configuration (`World:Playerbots:City`)

Every key is live through `.reload config`.

| Key | Default | |
|---|---|---|
| `Enabled` | true | off: no trip starts |
| `VisitIntervalMinutes` | 90 | 1..10080; the periodic trip |
| `MinGoldForAuctionTrip` | 50000 | copper, 0..2000000000 |
| `BindInCapital` | **false** | make the capital's inn the bot's home on a trip |
| `MaxTripYards` | 6000 | straight line on the bot's map |
| `MinLevel` | 10 | 1..60 |
| `MaxTripsPerCapital` | 6 | 1..1000, trips under way to one capital |
| `MaxStartsPerMinute` | 4 | 1..600, starts for one capital per minute |

**Why `BindInCapital` is off.** Players bind where they quest. A bot bound in a capital that stalls or is stranded is teleported to its
bind point where nothing of its level lives. Turn it on when you want the hearthstone to lead to the services.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/City/PlayerbotCapitalCatalogTests.cs`: catalog derivation, friendly teams, cancellation, option
validation and the limiter (caps, release, expiry, per-minute window, clock wrap, a holder asking again). `PlayerbotCapitalLocatorTests.cs`:
`Locate` over a synthetic terrain tile and a fake model service (terrain only, a missing tile, a WMO group over terrain, the terrain
rejection, no area row, window eviction). `PlayerbotCapitalsTests.cs`: the background build, the post back, and a stale build's discard.
All unproven until the integration gate.
