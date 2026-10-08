# Taxi and flight paths (taxi-fidelity lane)

The world loads the developer's build-5875 `TaxiNodes.dbc`, `TaxiPath.dbc` and
`TaxiPathNode.dbc` when their `NpcServices` paths are configured. Nodes and paths
otherwise come from the imported world-content tables. Missing path-node rows
now refuse a flight; a straight line between node positions is not a taxi path.
The DBC widths and columns are from `D:\refs\vmangos\src\game\Database\DBCfmt.h:83-85`.

## Behavior and references

| Behavior | ArcaneCore owner | Read-only reference |
| --- | --- | --- |
| Race starting mask, eight-word known mask and first-visit discovery | `QuestNpcServices`, characters v5 taxi mask | `D:\refs\vmangos\src\game\Objects\PlayerTaxi.cpp:28-49`; `D:\refs\vmangos\src\game\Handlers\TaxiHandler.cpp:129-149` |
| `SMSG_SHOWTAXINODES` (show flag, master GUID, current node, eight mask words) | `NpcPackets` | `D:\refs\vmangos\src\game\Server\Packets\Taxi.cpp:67-82`; `D:\refs\wow_messages\wow_message_parser\wowm\world\movement\smsg\smsg_showtaxinodes.wowm:1-10` |
| Gossip flight-master option discovers a new node before opening the map | `QuestNpcServices.Gossip`, `.Travel` | `D:\refs\vmangos\src\game\Handlers\TaxiHandler.cpp:61-107` |
| Direct and express client fields; client-supplied total is ignored | `NpcServiceHandlers` | `D:\refs\vmangos\src\game\Server\Packets\Taxi.cpp:13-33`; `D:\refs\wow_messages\wow_message_parser\wowm\world\movement\cmsg\cmsg_activatetaxiexpress.wowm:1-13` |
| Route lookup, team mount, source distance, reputation discount | `QuestNpcServices.Travel`, `TaxiFlightSystem` | `D:\refs\vmangos\src\game\Objects\Player.cpp:17910-17944`, `:17960-17977`, `:18039-18060`; `D:\refs\vmangos\src\game\ObjectMgr.cpp:7391-7445` |
| Per-leg fare: `uint(cost * discount + 0.5f)`, whole-route affordability, first charge at departure, subsequent charges at path changes | `QuestNpcServices.Travel`, `TaxiFlightSystem` | `D:\refs\vmangos\src\game\Objects\Player.cpp:17963-17997`, `:18052-18090`; `D:\refs\vmangos\src\game\Objects\PlayerTaxi.cpp:126-136`; `D:\refs\vmangos\src\game\Movement\WaypointMovementGenerator.cpp:411-424` |
| Flying `SMSG_MONSTER_MOVE`, 32 yards/second, landing and dismount | `TaxiFlightSystem` | `D:\refs\vmangos\src\game\Movement\WaypointMovementGenerator.cpp:400-409`; `D:\refs\vmangos\src\game\Movement\spline\packet_builder.cpp:113-144` |
| Logout route and current-position resume | Characters v22 `character_taxi_flight`, `NpcServicesFeature`, `TaxiFlightSystem` | `D:\refs\vmangos\src\game\Objects\PlayerTaxi.cpp:67-109`; `D:\refs\vmangos\src\game\Objects\Player.cpp:18110-18167` |
| Reply-code values | `ActivateTaxiReply` | `D:\refs\wow_messages\wow_message_parser\wowm\world\movement\smsg\smsg_activatetaxireply.wowm:3-20` |

The flight owner checks the source and destination maps and requires at least two
waypoints per leg. The client gets a flying spline; map updates keep the server
position on the waypoint path. Logout stores the remaining nodes, path IDs and
already-discounted leg costs, marks the active leg as paid, and saves the current
position through the normal character snapshot. Login restarts that leg from the
nearest path segment without charging it twice. Landing or an interruption clears
the saved route. A death during flight returns the dead player to the departure
node of the current leg and removes the taxi mount and movement flags.

`NpcServices:TaxiNodesDbcPath`, `NpcServices:TaxiPathDbcPath`, and
`NpcServices:TaxiPathNodeDbcPath` are optional paths to developer-supplied files.
No client data is bundled. When the first two paths are absent, their imported
world tables remain the source: `taxi_nodes` and `taxi_path`, which the content
refresh (`tools/content/refresh-world-content.ps1`, `arcane-content-importer
refresh --dbc-dir`) fills from the client's `TaxiNodes.dbc` and `TaxiPath.dbc`
(85 nodes and 287 paths in build 5875). A world built before that refresh has both
tables empty and no flight master offers a flight
(docs/integration/transports-content-20261008.md). A missing `TaxiPathNode.dbc` makes paid flights
fail closed. The taxi mount uses `TaxiNodes.MountCreatureID[0]` for Horde and
`[1]` for Alliance (`D:\refs\vmangos\src\game\ObjectMgr.cpp:7415-7445`).

Characters schema v22 owns only `character_taxi_flight`; its module implements
`ICharacterDataCleanup`. The store is tested on SQLite locally and uses the
shared hosted provider matrix for MariaDB and PostgreSQL. No World schema change
is introduced. The earlier characters v5 known-node mask remains authoritative.

## Limits requiring client or data validation

* `CMSG_TAXICLEARALLNODES` is an invalid/unhandled opcode in vmangos
  (`D:\refs\vmangos\src\game\Server\Protocol\Opcodes.cpp:507`), so this lane does
  not let a client erase its discovered nodes. The existing reply paths follow
  vmangos: invalid source yields `NO_SUCH_PATH`; missing edge, unknown visited node
  or an already-active flight returns without a reply (`Player.cpp:17848-17858`,
  `:17969-17975`). Insufficient money and source distance do send their codes.
  These silent cases need a real 5875 capture if a different reply is desired.
* vmangos splices adjacent express paths with `taxi_path_transitions`
  (`D:\refs\vmangos\src\game\Objects\Player.cpp:17999-18034`). ArcaneCore
  launches one spline per leg and does not import that optional transition table.
  A real-client check is needed at a multi-leg join. The server follows straight
  segments while the client renders Catmull-Rom, so mid-flight positions can differ.
* vmangos selects and may gender-swap a creature display
  (`D:\refs\vmangos\src\game\ObjectMgr.cpp:7438-7449`). ArcaneCore currently
  uses the first nonzero creature-template display for the chosen team mount.
* The route write at logout and the character-position save use separate stores.
  They are not one database transaction. The logout save and the landing delete
  go through `TaxiFlightWriteQueue` (off the world thread, in order, one
  outstanding operation per character, a failed write retained until it is
  durable); the login barrier in `OnPlayerLoadingAsync` refuses the relog while
  the character's write is retained, and `StopAsync` drains the queue (throwing,
  naming the characters, when a write still cannot be persisted). When the save
  fails every attempt the queue falls back to what the synchronous save did: a
  snapshot of the character at the departure node of its leg is queued behind
  the logout snapshot through the character save queue and the route row is
  deleted (that delete is retained until durable), so the character row never
  stays mid-air without a route. The fallback is unavailable, and the save is
  simply retained, when the departure node is unknown or on another map. Retention
  is in process: a crash while a write is retained loses it (logged loudly), and
  a store that flips back up between the character barrier and the route barrier
  of one relog is a window a live-client recovery test still has to cover.
  Similarly, completed-flight route deletion and the final position save are
  separate operations.
* Cross-map TaxiPathNode flights and spell-triggered taxi rides are refused by
  this owner. The game data and a client capture are needed before implementing
  the teleport split that vmangos uses. The mount-aura removal and full casting
  interruption rules also remain with the spell owner.

## Verification

Release solution build: zero warnings, zero errors. Game (3660), Data (773, five
skipped), Cryptography, Realm and World suites pass on SQLite; the MockClient
self-test runs its 59 checks. The logout-save and login-resume wiring in
`NpcServicesFeature` is driven by `NpcServicesFeatureTaxiTests` (World tests):
the logout save and landing delete reaching the queue, resume after a relog, the
departure-node fallback and route clear when the store is down, the login barrier
refusing and then passing, a later logout retrying a retained write, and
`WorldFeatures.StopWorldFeaturesAsync` draining the queue; the queue itself has
`TaxiFlightWriteQueueTests`. No real-client capture or hosted MariaDB/PostgreSQL
run has occurred. The route row is deleted only for characters whose route was
persisted (loaded at login, or abandoned at a failed logout save), so an ordinary
landing issues no database call.
