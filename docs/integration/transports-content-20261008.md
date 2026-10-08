# Ships, zeppelins and flight tables in the content refresh (wave 4, transports-content lane, 2026-10-08)

Branch `claude/w4-transports-content` (base `08ea5ff6`). Logs: `D:/ArcaneCore-lanes/_logs/w4-transports-content/`. No schema version
was taken (world 44 was reserved for this lane and is left free): every table written already exists at world 41.

## Where the live world stood

A backup-API copy of the live world (`playable-content-20261005/complete-r5/world.db`, world 41, after the wave-3 refresh) already has
what the ships need: the nine `gameobject_template` rows of type 15 (from the original import) and the nine `transports` periods (from
the wave-3 refresh); the live profile sets `NpcServices:TaxiPathNodeDbcPath`. Only `World:Transports:Enabled` is off ("Transports are
disabled" in the live log). On that copy, with the switch on, the world builds all nine routes and every ship sails between its ports
(`spike-real-ships.log`).

The same copy has `taxi_nodes` and `taxi_path` empty, and the live profile sets neither `NpcServices:TaxiNodesDbcPath` nor
`TaxiPathDbcPath`: no flight master on the live server knows a single node, so nobody can fly
(`red-real-flight-unrefreshed.log`: "0 taxi nodes").

## What changed

* `arcane-content-importer refresh` writes the dump's ship templates (`gameobject_template` type 15, mapped as the full object import maps
  them; every other object template is left alone), fills `taxi_nodes` and `taxi_path` from `TaxiNodes.dbc` and `TaxiPath.dbc` in
  `--dbc-dir`, and, with `TaxiPathNode.dbc` there, reports as a `check:` line every ship the world would refuse at start (speed or
  acceleration 0, a path under three nodes, a map without a `map_template` row). A missing DBC leaves its table alone with a warning.
  Tests: `RefreshCliTests` (RED `red-refresh.log`: 3 of 14 failed; GREEN `green-refresh.log`: 14 of 14).
* `tools/content/refresh-world-content.ps1` needs and checks five DBCs (adds `TaxiNodes.dbc`, `TaxiPath.dbc`, `TaxiPathNode.dbc`).
  `D:\refs\client-dbc-5875-effective` now holds them, extracted read-only from the 1.12.1 client with the same mpqcli v0.11.0 (TaxiNodes
  from patch-2.MPQ, TaxiPath and TaxiPathNode from patch.MPQ; identical to the copies in `playable-content-20261005\DBFilesClient-5875`),
  with their lines added to `SHA256SUMS` and `README.txt`.
* The shipped scenario `ship` (`ShipCrossingScenario`, docs/areas/playbots.md): a bot boards the Ratchet - Booty Bay boat at a port,
  crosses to the other continent aboard and steps off at the other port.
* `TransportSystem` logs a ship's map change at Debug (`Transport 20808 sailed from map 0 to map 1 with 0 passenger(s)`).
* `World:Transports:Enabled` is documented as the deploy's switch (docs/reference/configuration.md, docs/areas/transports.md); its default
  stays false.

## Evidence

* **Refresh on a live copy** (`refresh-live-copy-1.log`, the script itself under Windows PowerShell): five DBCs "matches SHA256SUMS",
  backup line, "world schema 41", the wave-3 tables as before plus `gameobject_template (type 15) 9`, `taxi_nodes 85`, `taxi_path 287`,
  "TaxiPathNode.dbc: 9582 row(s) on 288 path(s)", "refresh: every checked reference resolves", exit 0.
* **The ships on that copy, in process** (`RealTransportContentTests`, env-gated by `ARCANECORE_TEST_WORLD_DB`,
  `ARCANECORE_TEST_DBC_DIR`, optional `ARCANECORE_TEST_TERRAIN_DIR`; `scenario-2.log`): the world starts with the switch on, builds the
  nine routes, and with game time advanced tick by tick on the manual clock every continent ship waits at each of its ports (at the
  port's TaxiPathNode position, on its map) and the five crossings change maps (for example 20808 0 -> 1 at 161 s of game time).
* **A bot on a real boat** (same file): the `ship` scenario on that copy and the live terrain (`D:\ArcaneCore-data\terrain-5875`): the
  bot boards at the Eastern Kingdoms port, SMSG_TRANSFER_PENDING names 20808 and map 0, SMSG_NEW_WORLD carries the deck offset, the bot
  arrives on map 1 aboard, is at its deck offset when the boat lies at Ratchet, and steps off within 30 yards of the port (224 s game).
* **Flights** (`real-flight-2.log`): on the refreshed copy the world loads 85 nodes; a player started on the Stormwind - Ironforge path
  (13, 50 copper, mount 541) lands 0.2 yd from Ironforge after 246 s of game time. On the unrefreshed copy the same test fails with
  "0 taxi nodes".
* **The real servers on copies** (`rehearsal/run1/`, `rehearse.py` in `D:/ArcaneCore-lanes/w4-transports-content-data/rehearsal/`):
  live `auth.db`, `characters.db` and `world.db` copied with the SQLite backup API from read-only connections; the world copy refreshed by
  the script (exit 0, same counts); the live `appsettings.json` with only the database paths, World port 18085, Auth port 13724, the realm
  seed address, `World:Transports:Enabled = true` and Debug logging for `ArcaneCore.World.Transports` changed. This lane's Realm and World:
  "Transports: 9 routes, 9 ships sailing", the four enabled bots started, listening after 9 s; every crossing's first map change in the
  log within 204 s of real time (175080, 176310, 20808, 176231, 164871). No ERROR line; the warnings are the known 56 graveyard zones and
  the instance-map teleports of "Data still needed" (wave 3).

## Deploy (live)

1. Run the content refresh with this lane's importer, as the wave-3 step 5 (`refresh-world-content.ps1 -WorldDatabase <live world.db>
   -DbcDirectory D:\refs\client-dbc-5875-effective ...`, world server stopped). Expected now: five "matches SHA256SUMS", plus
   `gameobject_template (type 15)  9`, `taxi_nodes  85`, `taxi_path  287`, "TaxiPathNode.dbc: 9582 row(s) on 288 path(s)" and
   "refresh: every checked reference resolves".
2. In the profile's `appsettings.json` set `World:Transports:Enabled` to `true` (the deploy script's `--transports`). Keep
   `NpcServices:TaxiPathNodeDbcPath` (already set; `D:\refs\client-dbc-5875-effective\TaxiPathNode.dbc` is the same file).
3. Restart the World. Expect `Transports: 9 routes, 9 ships sailing` in `world.log`. Optional: `"ArcaneCore.World.Transports": "Debug"`
   under `Logging:LogLevel` shows each map change; `.playerbot scenario run ship` (Administrator, with
   `World:Playerbots:Scenarios:Enabled` and `MaxDurationSeconds` 600) sends a bot across on the Booty Bay boat.

Flight masters start offering flights after step 1 (no configuration needed). Rollback: the refresh backup and `appsettings.before.json`,
as in wave 3.
