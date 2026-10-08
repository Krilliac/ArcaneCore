# Transports: ships and zeppelins

Status: wave-2 lane K (`claude/w2-transports`), behind `World:Transports:Enabled` (default false) until the content is
present. WoW 1.12.1 (5875). Primary reference: vmangos `0e3ff01` `src/game/Transports/Transport.cpp`,
`TransportMgr.cpp`, the transport parts of `Maps/Map.cpp`, `Handlers/MovementHandler.cpp` and `Objects/Player.cpp`.
Re-implemented from behaviour; no code was copied.

## Delivered

| Behaviour | ArcaneCore owner | Reference |
| --- | --- | --- |
| A route per `gameobject_template` type 15 (MO_TRANSPORT) row: data0 TaxiPath id, data1 speed (yd/s), data2 acceleration (yd/s²) | `TransportTemplateBuilder` | `TransportMgr::LoadTransportTemplates`, `GenerateWaypoints` (TransportMgr.cpp:48-354); `GameObjectDefines.h:385-395` |
| Key frames: path nodes 1 .. n-2, a teleport frame before action flag 1 or a map change (the node after it skipped), the last frame always teleports | `TransportTemplateBuilder` | TransportMgr.cpp:127-152, :169 |
| One Catmull-Rom spline per stretch between teleports, three chords per segment, lengths summed in double | `TransportSpline` | `Movement/spline/spline.cpp`, `spline.impl.h` |
| Stop distances, accelerate/cruise/brake times, arrival and departure times (single precision), Feathermoon/Teldrassil refresh frame 12 | `TransportTemplateBuilder` | TransportMgr.cpp:171-353 |
| Period override: `transports` (entry, build, name, period), newest build at or below 5875 | `TransportWorldDataModule` (world 41), `TransportPeriods.Select` | TransportMgr.cpp:62-80; `sql/migrations/20250530110153_world.sql` |
| The ship: GUID `0x1FC0 << 48 \| entry`, GAMEOBJECT fields from the template, created at the first frame of the map it spawns on | `ShipTransport` | `ShipTransport::Create` (Transport.cpp:51-100); `TransportMgr::CreateTransport` |
| Motion on the world clock: path progress = time since creation + start frame arrival; stops, departures, frame-by-frame advance; position every 50 ms from `CalculateSegmentPos` and the frame's spline; facing = tangent + pi | `ShipTransport.Update` | `ShipTransport::Update`, `CalculateSegmentPos` (Transport.cpp:316-413) |
| Continent routes spawn once, with the first map they touch (Install creates those maps, as vmangos has its continents loaded); a route on one instanceable map spawns in each instance | `TransportSystem` | `TransportMgr::SpawnTransportsOnMap` (TransportMgr.cpp:413-427), Map.cpp:176 |
| Ships are not grid objects: every player of the map has them. Map entry sends every ship but the player's own before the player's own create block (`IMapUpdater.OnPlayerAdding`); a player aboard gets its own ship as the first block of its self packet, which then has the has-transport byte set (`IMapUpdater.OnWritingSelf`, `UpdateData.HasTransport`); leaving the map sends out-of-range for all but the player's own ship; a ship arriving or leaving is created / removed for everyone not aboard; the has-transport byte is set on the ship packets | `TransportSystem`, `TransportPackets` | `Map::SendInitTransports`, `SendRemoveTransports`, `SendInitSelf` (Map.cpp:1690-1750), `GenericTransport::SendCreateUpdateToMap` / `SendOutOfRangeUpdateToMap` (Transport.cpp:549-579) |
| Create block: update flags TRANSPORT \| ALL \| HAS_POSITION, position 0, 0, 0 plus facing, then the path progress | `UpdateBlockWriter` | `GameObject::GetStationaryX/O` (GameObject.h:234-237), `Object::BuildMovementUpdate` (Object.cpp:544-598) |
| Passengers: offset kept in the movement block; moved with the ship (`CalculatePassengerPosition`); boarding computes the offset from the world position when the unit changed ships | `ShipTransport` | `GenericTransport::AddPassenger`, `RemovePassenger`, `UpdatePassengerPosition`, `CalculatePassengerPosition/Offset` (Transport.cpp:202-547) |
| Client movement: aboard, the world position comes from the ship and the client's offset; ONTRANSPORT with a ship GUID boards (an unknown GUID boards nothing) and marks the player "just boarded"; a movement aboard clears that mark; without the flag the player leaves | `TransportMovementObserver` | `HandleMoverRelocation` (MovementHandler.cpp:1075-1102) |
| A CMSG_MOVE_TIME_SKIPPED while the player is still "just boarded" (after boarding, before any movement aboard) re-sends the ship to the player instead of being relayed; any other time skip is relayed as usual | `MovementHandlers`, `TransportSystem.TakeJustBoarded` | MovementHandler.cpp:1001-1010, :1083, :1089 |
| Map change at a dock: the ship leaves the old map's players and arrives for the new map's; creatures are left behind (evade, or back to owner / spawn); players are revived, freed of fear and confusion, taken out of combat and carried: same map = relocation, other map = `TeleportTo(.., NotLeaveTransport)` | `TransportSystem.TeleportTransport`, `TransportFeature` | `ShipTransport::TeleportTransport` (Transport.cpp:120-200) |
| Far teleport aboard: always a far teleport, SMSG_TRANSFER_PENDING with transport entry and old map, SMSG_NEW_WORLD with the offset, the worldport ack places the player at its offset from where the ship is now | `TeleportService` | Player.cpp:1868-1896, :2068-2072, :2113-2118; MovementHandler.cpp:106-110 |
| An ordinary teleport, a logout and a spirit released aboard leave the ship | `TeleportService`, `TransportSystem`, `GraveyardRepopService` | Player.cpp:1868-1872, :5010-5016 |
| Duels: NOT_ON_TRANSPORT unless both stand on the same ship; a duel requested aboard is bound to the ship and leaving it is leaving the duel area (10 s to come back) | `DuelService`, `DuelInfo.TransportGuid` | Spell.cpp:6195-6196; SpellEffects.cpp:4750-4755; `Player::CheckDuelDistance` (Player.cpp:6685-6689) |
| A character saved aboard: the ship and the offset are stored with it and it comes back aboard at login (following the ship to the other continent if it sailed there); a ship that is gone or an offset over 250 yards sends it to its bind point | `CharacterTransportDataModule` (characters 40), `Player.LoginTransportSeat`, `TransportSystem.RestoreSeat` | Player::SaveToDB (Player.cpp:16427-16434), Player::LoadFromDB (:14733, :14794-14838) |

## Configuration and data

`World:Transports:Enabled` (false) is the master switch; `World:Transports:Entries` restricts the routes to a list of
entries (empty: all). Both are restart-only and listed in the [configuration reference](../reference/configuration.md).
Off, nothing is built and a client that claims to stand on a transport is treated as before (the flag is stored, nothing
boards).

The routes need data the repository does not ship:

* `gameobject_template` rows of type 15 (classic-db and vmangos both have them: the eight vanilla boats and zeppelins,
  among them 20808, 164871, 175080, 176231, 176244, 176310, 176495, 177233).
* A build-5875 `TaxiPathNode.dbc`, the same file the flight paths use (`NpcServices:TaxiPathNodeDbcPath`).
* Optionally the vmangos `transports` rows (period overrides) in world schema 41. Without them every route keeps the period
  computed from its path; vmangos says that computation is "not perfect", so the client and the server can drift on a long
  route until the overrides are imported. The content importer does not read this table yet.

Routes that cannot be built (no path, zero speed or acceleration, a path too short for a spline, a multi-map route through an
instanceable map, a map without a `map_template` row) are logged and refused; the server starts without them.

## Limits

* **Elevators and trams** (type 11, vmangos `ElevatorTransport`; `GameObjectMapSystem.Elevators.cs`) do not need `World:Transports:Enabled`:
  the object is created as a transport (HIGHGUID_TRANSPORT, UPDATEFLAG_TRANSPORT, `transport.pause` in GAMEOBJECT_LEVEL, the start state from
  `startOpen`, GO_FLAG_TRANSPORT and GO_FLAG_NODESPAWN, GameObject.cpp:207, 244-250) and its create block carries the milliseconds into its
  TransportAnimation.dbc cycle (time since the object was made modulo the entry's last TimeSeg), from which the client animates it. The DBC
  comes from `GameObjects:TransportAnimationDbcPath` (build 5875, `TransportAnimationDbcReader`); without it the progress stays 0, as in vmangos
  without the data. The server keeps the object at its spawn position: `ElevatorPosition` computes where the animation puts it (the node
  offset times the rotation matrix of GAMEOBJECT_ROTATION, Y negated, plus the spawn, Transport.cpp:403-427) but nothing boards an elevator
  here, so a player on one keeps the position its client reports.
* **Continent instancing**: vmangos can run several continent instances (`GetContinentInstanceId`); this base has one, so a
  continent ship sails instance 0 only.
* **The login seat** is applied when the player enters its saved map. A ship on the same map is boarded before the
  player's own create block, so that block carries the ship (as in vmangos). A ship on the other continent is not: the
  player enters its saved map on land (its self create has no ONTRANSPORT and names no ship), then boards and is
  far-teleported to the ship right after the first map update; a missing ship sends it to its bind point at the same point.
  vmangos decides both before the map is entered (Player::LoadFromDB moves the character to the ship's map), so a 1.12
  client there never sees the saved map at all; here it loads the saved map briefly first. A crash loses nothing beyond the
  last autosave, which stores the seat as well.
* **Other passengers in the self packet**: vmangos `SendInitSelf` also puts the other visible passengers of the player's
  ship into the self packet; here they come with the ordinary visibility pass right after (has-transport 0).
* **Creatures aboard**: the API (`AddPassenger`, `AddFollower`, `RemoveFollower`) exists and creatures move with the ship,
  but nothing boards them yet: pets follow their owner's transport in vmangos' follow movement generator
  (TargetedMovementGenerator.cpp:586-594), which the pets lane owns, and creature spawns on transports are not in 1.12 data.
  A pet whose owner rides along is taken off the ship at a map change and left to the pet system's far-teleport handling.
* **Mac client quirk** (MovementHandler.cpp:211-216, Mac clients ignore movement from the transport object) is not copied.
* **`IsOutdoorOnTransport`** (mount checks aboard by ship display id, Player.cpp:6058-6100) and the dismount check on
  boarding (`Player::SetTransport` -> `DismountCheck`) are not implemented.
* **Wire checks**: the layouts follow vmangos and gtker/wow_messages (`smsg_transfer_pending.wowm`); no 1.12.1 client
  capture aboard a ship was available. The movement block keeps the vmangos/cmangos transport layout (see `MovementInfo`).

## Tests

* `tests/ArcaneCore.Game.Tests/Transports/`: route building on synthetic straight paths with hand-worked frame times
  (`TransportTemplateBuilderTests`), motion, map-wide sending, passengers and the map change (`ShipTransportTests`), boarding
  through the movement observers (`TransportBoardingTests`), the stored seat (`TransportSeatTests`);
  `Duel/DuelTransportTests` for the duel rules.
* `tests/ArcaneCore.Data.Tests/Transports/`: world 41 and characters 40 on every available provider.
* `tests/ArcaneCore.World.Tests/Transports/TransportWorldTests`: the gate, route building and periods, the ship before the
  player's own create block at login, boarding over loopback, the time-skip re-send (and no re-send once the player moved
  aboard: the time skip is relayed), logout aboard and relog.
* `tests/ArcaneCore.World.Tests/Playerbots/Scenarios/TransportScenarioTests`: two scripted bots board a ferry, duel aboard
  while it sails 80 yards from the flag (no out-of-bounds), and the duel ends fled 10 s after one steps off; a bot rides a
  ship through its map change and arrives aboard on map 1.
