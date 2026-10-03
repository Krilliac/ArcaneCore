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

## Deviations from retail (all documented, none silent)

- `ClientZoneTrust=Auto` is a development-world allowance, not retail. Retail is `Never`.
  With `Auto`, a world that has both area data and terrain behaves exactly like retail.
- Fallback to the stored zone: a position that resolves to no zone before any zone was accepted
  (e.g. the start tile's terrain is missing) uses the character's stored zone for the entry
  events, so the client still gets its world states. vmangos would send nothing.
- A player that reaches a map without `SendInitialPacketsAfterAddToMap` (tests, other entry
  paths) gets its first `UpdateZone` on its first tick; vmangos relies on the explicit call.

## Not delivered (limits, not stubs)

Recorded so no one assumes them: PvP-enforced areas and the PvP flag freeze, rest-type changes
on zone change (owned by the death-persistence / stats lanes, which can use the listener),
`DismountCheck`, `UpdateAreaDependentAuras`, WMO area overrides, the first-login cinematic,
the 108 default world-state pairs (sniffed retail data; an operator-supplied file is the
planned route), game events, and weather runtime wiring (see the sections added as slices land).
