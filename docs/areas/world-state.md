# World state and exploration

Branch `claude/vw2-world-state-exploration`. Pure rules live in
`src/ArcaneCore.Game/WorldState/**`, world wiring in `src/ArcaneCore.World/WorldState/**`, tests in
`tests/ArcaneCore.{Game,World}.Tests/WorldState/`. Shared-file edits and merge notes:
[integration/world-state.md](../integration/world-state.md).

Standing rule: retail (vmangos 1.12.1) behaviour is the default. A deliberate departure sits
behind a config option that defaults to retail, or is listed under "Deviations" below.
References are read-only (`D:\refs\vmangos`, `mangos-classic`, `wow_messages`, `classic-db`);
nothing from them is copied into the repo.

## Delivered

### Zone and area tracking (`zone-area-tracking`)

- `ZoneAreaUpdater` is a `[DefaultMapUpdater(Order = 10)]`: every map tracks its players'
  zone and area. It ports the 1 s zone timer (vmangos `Player.cpp:1215-1236`, `ZONE_UPDATE_INTERVAL`
  at `:88`), `UpdateZone` (`:6586-6675`: early return on an unknown zone, the zone-entry work,
  then `UpdateArea`) and `UpdateArea` (`:6560-6584`). The timer is `0` until the first successful
  `UpdateZone`, a failed update does not reset it (retried next tick), exactly as in vmangos.
- The cached zone is independent of the persisted `Player.ZoneId`. A player entering a map
  (login, far teleport) is a fresh state in that map's updater, so the entry events fire again
  (vmangos `SendInitialPacketsAfterAddToMap`, `Player.cpp:19154-19159`). `Player.ZoneId` is
  written from the tracker on a real zone change, so /who, groups and saves follow the walked
  terrain instead of waiting for a client packet.
- `IPlayerLocationListener` (`OnZoneChanged`, `OnAreaChanged`, ordered by `Order`) is the seam
  for other lanes: rest in capitals/taverns, PvP-enforced areas, channels, zone-limited items,
  spell_area auras. `OnZoneChanged` carries the zone's `AreaTemplate` (`Flags`, `Team`);
  `AreaFlags` / `AreaTeams` are the vmangos `DBCEnums.h:46-67` values. Register through
  `WorldStateHooks.For(world).AddLocationListener(...)`; a world feature that implements the
  interface is picked up automatically by `ZoneAreaFeature`. A throwing listener does not stop the
  others (the first failure is rethrown afterwards and logged by `Map.Update`).
- `ZoneAreaFeature` (world feature) binds `World:Zones`, and is the first listener: a real zone
  change sends SMSG_INIT_WORLD_STATES. `LoginSequence.SendInitialPacketsAfterAddToMap` and
  CMSG_ZONEUPDATE call into it. SMSG_INIT_WORLD_STATES is sent once per real zone change, never
  for an area change.
- CMSG_ZONEUPDATE ignores the client value and derives the zone from terrain (vmangos
  `MiscHandler.cpp:381-386`).

Config (`World:Zones`):

| Key | Default | Meaning |
|---|---|---|
| `ClientZoneTrust` | `Auto` | `Never` = retail (always derived). `Always` = use the client / stored zone. `Auto` = the client / stored zone is used only while zones cannot be derived (no `area_template` rows or no terrain files). |

### Weather engine (`weather-core`, pure rules)

`WeatherState.ReGenerate` is a line-by-line port of vmangos `Weather::ReGenerate`
(`Weather.cpp:87-210`; mangos-classic's is identical) with float32 arithmetic and the C++
constants (`0.33333334f`, `0.6666667f`, `0.9999f`, `0.3333f`, `0.3334f`, `0.6667f`). Preserved
quirks, each pinned by a test: the "get fair" branch falls through into a fresh roll from the
chance table; the get-better / get-worse returns do not normalize (a grade can exceed 1 until a
packet is built, which normalizes like `SendWeatherUpdateToPlayer`); a zone without a
`game_weather` row stays fine; permanent weather never regenerates.
`WeatherSeasons.Of(local)` is `((tm_yday - 78 + 365) / 91) % 4` on the server-local day.
`ZoneWeatherChances.FromColumns` takes the 12 `game_weather` columns (spring, summer, fall,
winter by rain, snow, storm) and replaces a value above 100 with 25 (`Weather.cpp:480-495`).
Sounds are the 1.12 table (`Weather.cpp:40-52, 402-443`); SMSG_WEATHER is
`u32 type, f32 grade, u32 sound, u8 instant` (13 bytes; vmangos `Misc.cpp:404-427`, wow_messages
`smsg_weather.wowm`). The random source (`IWeatherRandom`) is injected; the clock is an argument.

Not wired yet: the per-map zone weather registry, the 10-minute interval timer, broadcast to the
zone, `.wchange`, and the `game_weather` table/importer. The engine is usable on its own by the
next slice. Verification note: the long-run test pins the port's own distribution (46.5 % of
regens change state, 47 % of time at grade 0.27 or more for a 20 % rain zone); it is a
self-consistency check against a second port, not a retail oracle.

### Exploration rules (`exploration-core`, pure rules)

`ExploredZones` is the `PLAYER_EXPLORED_ZONES_1..64` bit math (64 words, `flag / 32`, bit
`flag % 32`, an unsigned shift so bit 31 is safe, flag `0xFFFF` skipped, offsets of 64 and above
rejected without throwing; vmangos `Player.cpp:6089-6204`). `ExplorationXp.Compute` is the
`Player.cpp:6171-6196` formula: no XP for area level 0 or at the maximum level; within five
levels the base XP of the area level; more than five levels below the area the base XP of
`level + 5`; more than five above, `base * percent / 100` (integer division first) with
`percent = clamp(100 - (diff - 5) * 5, 0, 100)`; the rate multiplies as a float32 and the result
truncates. `ExplorationBaseXpTable` is data (`exploration_basexp`); a level without a row is 0
(`ObjectMgr::GetBaseXP`, `:8516`) and an empty table gives 0 XP everywhere. SMSG_EXPLORATION_EXPERIENCE
is `u32 area, u32 xp` (`Misc.cpp:833-837`); vmangos sends it for every discovered area that has an
entry, even with 0 XP (mangos-classic only when the area level is above 0; vmangos, the primary,
is followed).

`ClassicDbFact` tests read `exploration_basexp` straight from the classic-db dump
(`D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz`, or `ARCANE_CLASSICDB_DUMP`) and are
reported as skipped with that reason when it is absent; no game data is committed. Verified there:
61 rows, level 60 = 660, and the XP examples in the tests.

Not wired yet: the explore check in the zone tracker, persistence of the words, the table
loader, `.explorecheat`/`.showarea`/`.hidearea`, and the first-login cinematic (a documented limit:
without a cinematic the first check runs right after the map add).

### Game time (`game-time`)

SMSG_LOGIN_SETTIMESPEED packs the SERVER's local time (vmangos `localtime_r` in
`Server/Packets/Misc.cpp:924-933`), not UTC: `GameTimePacker.Pack` keeps the bit layout
(`minute | hour<<6 | weekday<<11 | (day-1)<<14 | month0<<20 | (year-2000)<<24`, Sunday 0) and is
checked against the wow_messages vector `0x1673320A` (2022-08-13 Saturday 08:10). The timescale
stays 1/60 (`Player.cpp:19141-19145`). `WorldStateHooks.LocalNow()` is the one place "local time" is
decided; the clock is an injectable `IGameTime`.

| Key (`World:Time`) | Default | Meaning |
|---|---|---|
| `UseServerLocalTime` | `true` | `true` = retail (machine local time); `false` = UTC. |
| `TimeZoneId` | empty | Optional IANA/Windows zone id used as "server local time" (an unknown id fails startup). |

### World-state data (`world-state-data`)

World schema module `WorldStateDataModule` (constant `Version = 11`, the next free world version at
this base; the integrator renumbers) creates `game_weather` (zone plus the 12 chances, named like
the vmangos / classic-db columns) and `exploration_basexp` (level, base XP). `IWorldStateDataStore`
(Kernel) / `EfWorldStateDataStore` load them. `WorldStateDumpImporter` reads the two tables out of a
mysqldump-style SQL file BY COLUMN NAME from the dump's own `CREATE TABLE` (so reordered or extra
columns do not shift values), rejects anything malformed (non-numeric values, wrong column count,
duplicate keys, INSERT without CREATE) before touching the database, and `ImportAsync` replaces
both tables in one transaction. No data is bundled; point the importer at your own dump. Verified
against the real classic-db dump: 33 `game_weather` rows (zone 12 spring rain 20, zone 1377 spring
storm 20, zones 1/12/1377/3429 present) and 61 `exploration_basexp` rows (level 0..60, level 60 =
660); that test is skipped, with a visible reason, when the dump is not available.
No command-line importer entry point is delivered; the content-import-full lane owns
`tools/ArcaneCore.ContentImporter` and can call `Parse` / `ImportAsync`.

### Weather in the world (`weather-runtime`)

`MapWeather` is a `[DefaultMapUpdater(Order = 20)]`, so every map (instances included) has its own
weather, mirroring vmangos `WeatherSystem` (`Weather.cpp:371-399`): a zone's `WeatherState` is
created fresh (fine) the first time a player enters it, regenerates every
`World:Weather:ChangeIntervalMs` (the `ShortIntervalTimer` semantics, `Timer.h:101-126`), and sends
`SMSG_WEATHER` to every player of the zone when a regeneration changed it. A regeneration that
changed the weather while nobody is in the zone drops the zone's state so the next visitor starts
fresh; an unchanged one keeps it (`Weather::Update` returning true). A zone without a
`game_weather` row never leaves fine and consumes no randomness. `WeatherFeature` is the zone-entry
listener (after the world states, as in `Player::UpdateZone`, `Player.cpp:6594-6604`), loads
`game_weather` through `IWorldStateDataStore` at startup (no store registered = no weather, logged),
and `ReplaceChances` swaps the table atomically; live zones read the current row at each
regeneration (the reload coordinator can call it, `.reload game_weather` at
`ServerCommands.cpp:1813`).

`.wchange <type 0..3> <grade>` (Administrator, the top tier here; vmangos `SEC_BASIC_ADMIN`,
`Chat.cpp:1281`) validates the type (`IsValidWeatherType`), clamps the grade to 0..1, sets the
caller's zone non-permanent and tells the zone's players unless nothing changed
(`ServerCommands.cpp:98-130`, `Map::SetWeather`). Disabled weather answers mangos_string 407
("Weather system disabled at server.").

| Key (`World:Weather`) | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | vmangos `ActivateWeather`. |
| `ChangeIntervalMs` | `600000` | vmangos `ChangeWeatherInterval`. |

Test harness note: `WorldTestHost` switches weather off by default so the extra SMSG_WEATHER after
a zone entry does not shift unrelated tests' packet sequences; weather tests switch it on. Not
delivered: the weather-dependent client sounds beyond the sound ids, per-instance zone scripts, and
`.reload game_weather` wiring (the reload coordinator is another lane).

### Explored zones across logins (`exploration-persistence`)

Characters schema module `ExploredZonesDataModule` (constant `Version = 14`, next free after the
loot state step at 13; the integrator renumbers) with one row per character in
`character_explored_zones` (the 64 words in the vmangos text form, `characters.explored_zones`
longtext, `sql/characters.sql:92`: decimal u32 values, one trailing space each). It implements
`ICharacterDataCleanup` (the row goes in the deletion transaction). `ExploredZonesText.Parse` is strict:
a wrong value count, a non-numeric or out-of-range token is an error, never a silent zero.
`ExploredZonesPersistence` (world feature + `ICharacterHooks`):

- Loads on the session task while the character loads (vmangos `Player.cpp:14648`) and fills
  `PLAYER_EXPLORED_ZONES_1..64` before the player reaches the world thread. Fail closed: a malformed
  row, or an earlier write for the character that is still not durable (retained by the queue), fails the
  login (CHAR_LOGIN_FAILED) instead of showing a re-explored map.
- Writes through `ExploredZonesWriteQueue` (one consumer, the reputation queue's retention rules):
  on every change (`IExploredZonesSink.Changed`, so a crash does not lose a discovery) and when the
  character leaves the world (`Player.cpp:16481`). Three attempts per write; a write that still fails is
  retained and retried by the next change, the login barrier, the logout retry and shutdown (which
  throws, loudly, if storage is still failing). Logout is never blocked.
- `OnCharacterCreatedAsync` clears a row left by a deleted character whose id is reused;
  `ExploredZonesDeleteHook` drains queued writes before deletion and forgets anything retained
  after it, so nothing can bring a deleted character's row back (the store also ignores writes for a
  character that no longer exists).
- With no `IExploredZonesStore` registered the words live in memory only.

### Live exploration (`exploration-runtime`)

`ZoneAreaUpdater` carries the explore trigger of vmangos `Player::SetPosition` (`Player.cpp:5969-5985`):
the first sight of a player in a map (login, far teleport: the map add is a teleport there) and every
position change run the explore check at once, or after `World:Zones:RelocationCheckDelayMs`
(vmangos `Movement.RelocationVmapsCheckDelay`, default 0, at most 2000). `ExplorationService` is
the exploration half of `CheckAreaExploreAndOutdoor` (`:6089-6204`): a living player on a terrain cell with
an undiscovered flag sets the bit, gets `ExplorationXp` through the progression feature's
`IPlayerExperience` (SMSG_LOG_XPGAIN, no rested bonus), then SMSG_EXPLORATION_EXPERIENCE (area, xp),
always, even with 0 XP (area level 0, max level, or no `exploration_basexp` table). An unknown area
sets the bit, logs, and sends nothing; a flag of 64 words or more is logged and ignored. Every change goes to
`IExploredZonesSink` (persistence). `ExplorationFeature` binds `World:Exploration`, loads
`exploration_basexp` (one startup warning when it is empty, then 0 XP everywhere) and installs the
service. The explore check needs area data and terrain files (`IZoneLocator.CanDeriveZones`);
without them nothing is discovered (a development world).

| Key | Default | Meaning |
|---|---|---|
| `World:Exploration:RateXp` | `1.0` | vmangos `Rate.XP.Explore`. |
| `World:Exploration:CorrectExploreCheat` | `false` | `.explorecheat` as vmangos wrote it announces the SELECTED player but changes the ISSUER's fields, and `0` ORs in 0 (clears nothing). `true` applies it to the selected player and really clears on 0. |
| `World:Zones:RelocationCheckDelayMs` | `0` | vmangos `Movement.RelocationVmapsCheckDelay`. |

GM commands (Moderator, the nearest tier to vmangos `SEC_TICKETMASTER`): `.explorecheat 0|1`, `.showarea <areaId>`
(sets the bit of the area's explore flag on the selected player), `.hidearea <areaId>` (XORs it, as
vmangos does: hiding an unexplored area explores it). An unknown area id (vmangos `GetFlagById` = -1)
or a flag at word 64 or above answers mangos_string 115 "Incorrect values."; texts 116, 551-554, 560, 561
are the classic-db `mangos_string` rows.

Reference disagreement resolved toward the primary: vmangos sends the exploration packet for every
discovered area with an entry (mangos-classic only when the area level is above 0).

### World-state contents (`world-states-runtime`)

SMSG_INIT_WORLD_STATES now carries real pairs. `WorldStateRegistry` (`WorldStateHooks.WorldStates`) builds
the list for a zone entry: the operator's default pairs first, then each `IWorldStateProvider` in `Order`
(vmangos adds `def_world_states` before the zone script, battleground and war-effort states,
`Player.cpp:8156-8213`). A provider is asked on login and on every real zone change with the zone being
entered, never on an area change; battlegrounds, outdoor PvP, war-effort and invasion lanes register one
instead of editing this lane. Wire form: `u32 map, u32 zone, u16 count, (u32 state, i32 value)[]`
(`Server/Packets/Misc.cpp:1043-1060`, wow_messages `smsg_init_world_states.wowm` 1.12);
`WorldStatePackets.BuildUpdate` builds SMSG_UPDATE_WORLD_STATE (`u32 state, u32 value`, `Misc.cpp:1007-1024`).
`LoginPackets.BuildInitWorldStates(map, zone)` keeps its signature and sends an empty list.

Defaults: vmangos hard-codes 108 sniffed pairs (`Player.cpp:8041-8213`, "TODO: Determine what these
values mean"). That table is GPL data and is not copied here; `World:WorldStates:DefaultsPath` points at an
operator-supplied JSON file `[[state, value], ...]` (parsed strictly: numbers only, u32 state, i32 value, no
duplicates; a missing or malformed file stops startup). Without a file the list is provider-only, like
mangos-classic. Documented limit, not a stub.

### PvP-enforced areas (`pvp-area-state`)

`PvpAreaRules.IsEnforced` is the `Player::UpdateZone` switch (`Player.cpp:6612-6629`): an Alliance- or Horde-owned
zone (`AreaTeams` 2 / 4) is enforced on players of the other team on a PvP realm or in its capital (`AreaFlags.Capital`
0x100); an unowned zone on a PvP realm or in a battleground; any other team value (6) never. `PvpAreaTracker` (registered
by `PvpAreaFeature`) applies it on a real zone change: the per-player enforced flag (`PvpAreaState.IsInEnforcedArea`),
`UpdatePvP(true)` when enforced, and on `FfaPvp` realms FFA toggled with the PvP flag (not for GMs, not while
resting; `:6631-6633`). On an area change an arena area (`AreaFlags.Arena` 0x80) sets FFA for non-GMs, and any other
area clears it unless the realm is `FfaPvp` (`UpdateArea`, `:6566-6580`). The PvP flag timer freezes while the
player is in an enforced area (the one additive term in `MapCombat.Death.cs`, `Player.cpp:17199-17207`), so
the flag outlasts the 300 s timer inside a hostile capital and the countdown starts on leaving.

| Key | Default | Meaning |
|---|---|---|
| `World:Zones:PvpRealmMode` | `Normal` | `Normal`, `Pvp` (vmangos PVP / RPPVP) or `FfaPvp`. The world daemon has no realm type of its own, so it is configured here. |

Not delivered (limits): battlegrounds (`inBattleground` is false), taxi flights (the `!IsTaxiFlying()` guard), capture points
and flag carriers (the other two terms of the vmangos timer freeze), and the capital rest type, which the rest lane
takes from `OnZoneChanged`'s zone entry and `PvpAreaState` (`CAPITAL && !enforced`, `:6639`).

### Game event schedule maths (`game-event-schedule-core`, pure)

`GameEventSchedule` ports the pure maths of vmangos `GameEventMgr`: `IsActive` is `CheckOneGameEvent`
(`GameEventMgr.cpp:38-44`: inside `[start, end)` and `(now - start - leapDays * DAY) % (occurence * 60) < length * 60`)
and `NextCheckSeconds` is `NextCheck` (`:46-70`: one day when outdated, the delay to the start, the end of the
running occurrence or the start of the next, clipped at the end). No clock: the time is an argument.
`GameEventDefinition` carries the `game_event` columns (`GameEventMgr.h:43-60`); a length of 0 is invalid
(`isValid`), and an occurence of 0, which vmangos would divide by, never runs here.

Leap days (`LeapDays`, `:241-251`) apply only to events that recur every 365 days (525600 minutes) and last less. The
vmangos loop adds one leap day per leap YEAR in `[start year, current year)`: it counts the start year's own Feb 29th even
when that precedes the start, and never the current year's, so a yearly event that starts in a leap year begins one day
late in 2021-2023, 2025-2027 and so on (a midsummer start of 2020-06-21 20:00Z is inactive at 2026-06-21 20:01Z; worked by
hand from the C++ and pinned in `GameEventScheduleTests`). Retail holidays keep their calendar date, so
`World:GameEvents:LeapDayMode` defaults to `DateStable` (count only the Feb 29ths in `[start, now)`); `VmangosLiteral`
reproduces the vmangos loop. mangos-classic has no leap handling at all (a day early after every Feb 29th).

Not delivered, and recorded as such: the event service (start/stop with overwrite, linked events, `Update`'s next delay,
resume after restart), the `game_event_status` table, the `.event` and `.lookup event` commands, the listener contract for spawns,
quests and mails, and the cmangos `schedule_type` dialect that classic-db uses (types 1, 11 yearly, 12 lunar new year,
13 Easter, the Darkmoon types 2-9; the lunar type needs an unverified moon-phase port). They depend on
content-import-full's `game_event*` tables and spawn gate, which that lane owns.

## Deviations from retail (all documented, none silent)

- `ClientZoneTrust=Auto` is a development-world allowance, not retail. Retail is `Never`.
  With `Auto`, a world that has both area data and terrain behaves exactly like retail.
- Fallback to the stored zone: a position that resolves to no zone before any zone was accepted
  (e.g. the start tile's terrain is missing) uses the character's stored zone for the entry
  events, so the client still gets its world states. vmangos would send nothing.
- A player that reaches a map without `SendInitialPacketsAfterAddToMap` (tests, other entry
  paths) gets its first `UpdateZone` on its first tick; vmangos relies on the explicit call.
- The explore check needs area data and terrain (`CanDeriveZones`); a development world without them discovers nothing
  (vmangos always has both).
- `.wchange` needs Administrator (vmangos `SEC_BASIC_ADMIN`, a tier ArcaneCore's four levels do not have); `.explorecheat`,
  `.showarea`, `.hidearea` need Moderator (vmangos `SEC_TICKETMASTER`).
- `.explorecheat` follows the vmangos bug by default (`World:Exploration:CorrectExploreCheat=false`).
- Yearly event leap days default to date-stable holidays instead of the vmangos loop (`World:GameEvents:LeapDayMode`).
- The world daemon reads the realm's PvP kind from `World:Zones:PvpRealmMode`; vmangos reads its `GameType`.
- Weather is cut down to what 1.12 sends: `GetWeatherState` (the client-version-gated state log) is not ported.

## Not delivered (limits, not stubs)

Recorded so no one assumes them:

- Game events beyond the schedule maths: the service, `game_event_status`, `.event` / `.lookup event`, the cmangos
  `schedule_type` dialect (yearly, lunar new year, Easter, Darkmoon), and every consumer (spawn gate, quests, mails). Owned
  with the `game_event*` tables by content-import-full; adapt `GameEventSchedule` there.
- Rest: the capital / tavern rest type, `PLAYER_FLAGS_RESTING`, the inn tick and offline accrual belong to the
  death-persistence and stats-combat lanes; they should consume `IPlayerLocationListener.OnZoneChanged` (zone entry carries
  `Flags` and `Team`) and `PvpAreaState` (`CAPITAL && !enforced`) instead of polling zones.
- Zone-entry consumers owned elsewhere: `UpdateLocalChannels`, `DestroyZoneLimitedItem`, `UpdateZoneDependentAuras` /
  `UpdateAreaDependentAuras` (spell_area), `DismountCheck`, zone scripts, group/guild zone refresh beyond `Player.ZoneId`.
- PvP: battlegrounds, taxi flights, capture points and flag carriers (see the PvP section).
- WMO sub-area names and the `IsOutdoors` flag: `TerrainInfo.GetAreaFlag` returns the tile cell value only (no WMO
  override, no per-map fallback flag), and `CollisionContracts.ModelAreaInfo.MogpExterior` tests MOGP flag `0x8` where vmangos
  and mangos-classic test `0x8000` (`GridMap.cpp:875-878`, `:887-889`). Changing it needs a real extracted vmap sample to
  confirm which bit the files carry; that sample was not available, so the constant is untouched (pathfinding-collision lane).
- The first-login cinematic and "explore after the cinematic ends": there is no cinematic data, so the first explore check
  runs right after the map add (same XP and packet, no camera sequence).
- The 108 default world-state pairs (sniffed retail data under GPL; supply a file).
- A command-line importer entry point for `game_weather` / `exploration_basexp` (the dump parser and `ImportAsync` exist;
  content-import-full owns the tool), `.reload game_weather`, hot-reload coordinator wiring.
- A MockClient scenario and a real-client run (see the checklist).

## Reference table

| Feature | Reference |
|---|---|
| Weather engine, sounds, packet, chance loader | vmangos `src/game/Weather.cpp:40-52, 55-84, 87-210, 212-267, 304-310, 371-399, 402-443, 446-505`; wow_messages `smsg_weather.wowm` |
| Zone / area tracking | vmangos `Player.cpp:1215-1236, 6560-6675, 19154-19159`; `MiscHandler.cpp:381-386` |
| Game time | vmangos `Server/Packets/Misc.cpp:924-944`, `Player.cpp:19141-19145`; wow_messages `smsg_login_settimespeed.wowm` |
| Exploration | vmangos `Player.cpp:5969-5985, 6089-6204, 14648, 16481, 17059-17065`, `ObjectMgr.cpp:8516`, `Misc.cpp:833-837`, `CharacterCommands.cpp:640-679, 764-830`; classic-db `exploration_basexp` |
| World states | vmangos `Player.cpp:8156-8213`, `Misc.cpp:1007-1060`; wow_messages `smsg_init_world_states.wowm`, `smsg_update_world_state.wowm` |
| PvP areas | vmangos `Player.cpp:6566-6636, 17199-17207, 18568-18590`, `DBCEnums.h:46-67`, `World.h:802-803` |
| Game events | vmangos `GameEventMgr.cpp:38-70, 241-251`, `GameEventMgr.h:30-60` |

## Real-client checklist (not run)

1. Log in at the start of a new character: the map overlay of the start area reveals, one "Discovered" line appears with XP,
   no repeat after /reload and relog.
2. Walk into another zone: the zone name flashes, weather appears (rain, snow or sandstorm visuals and sound), and the
   `.wchange 1 0.5` / `.wchange 0 0` GM commands change it for everyone in the zone only.
3. Day/night follows the server's local clock after login (`World:Time`).
4. Enter an enemy capital: the PvP flag appears and stays beyond five minutes while inside; it drops five minutes after leaving.
5. `.explorecheat 1` reveals every zone; `.showarea` / `.hidearea` change single overlays.
6. A world-state provider (battleground / war effort lanes) shows its UI after a zone change.
