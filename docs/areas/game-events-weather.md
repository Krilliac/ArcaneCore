# Game events, day/night and weather (wave 4)

Branch `claude/vw5-game-events-weather` (base `claude/vw4-integration` 7313b9e). Pure rules live in
`src/ArcaneCore.Game/WorldState/**`, world wiring in `src/ArcaneCore.World/WorldState/**`, data in
`src/ArcaneCore.Data/**`. Shared-file edits and merge notes: [integration/game-events-weather.md](../integration/game-events-weather.md).
The wave-2 world-state lane's doc ([world-state.md](world-state.md)) describes the clock, the weather engine and the
event schedule maths this lane builds on.

Standing rule: retail (vmangos 1.12.1, then mangos-classic) behaviour is the default. Every deliberate departure sits
behind a `World:GameEvents` option that defaults to retail, or is listed under "Deviations". References are read-only
(`D:\refs\vmangos`, `mangos-classic`, `wow_messages`, `classic-db`); nothing is copied into the repository, and the
classic-db checks are skipped, visibly, when the dump is absent.

## Delivered

### World:GameEvents options and the world tick (`options-and-world-tick`)

`GameEventOptions` gains `Enabled`, `Dialect`, `StartBoundary`, `DateTimeInterpretation`, `YearlyRebase`,
`RestoreServersideEvents`, `Announce` and `AllowReload` (table below). `WorldRuntime.WorldTick(uint diffMs)` is a new
event raised once per tick on the world thread after the posted commands and before the maps update (vmangos runs
`sGameEventMgr.Update` from `World::Update`, World.cpp:2106-2111, with the next-event delay as its interval). A throwing
handler is logged and does not stop the others.

| Key (`World:GameEvents`) | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Whether the event service runs. |
| `Dialect` | `Auto` | `CMangos` (classic-db: `schedule_type`, `linkedTo`, `game_event_time`) or `VMangos` (`start_time`, `end_time`, `hardcoded`, `disabled` on `game_event`); `Auto` decides from the table's columns. |
| `LeapDayMode` | `DateStable` | Existing (wave 3): `VmangosLiteral` reproduces the vmangos leap-day loop. |
| `StartBoundary` | `Auto` | `Inclusive` is vmangos `start <= now` (GameEventMgr.cpp:42), `Exclusive` mangos-classic `start < now` (:39); `Auto` follows the dialect. |
| `DateTimeInterpretation` | `Wall` | `Wall`: a zone-less `start_time` is the wall clock in the game zone, daylight saving included (vmangos MySQL `UNIX_TIMESTAMP`, GameEventMgr.cpp:183). `StandardTime`: never daylight saving (mangos-classic `mktime` with `tm_isdst` 0, Field.cpp:24-31). |
| `YearlyRebase` | `SpanNewYear` | See "Computed schedules". `MangosLiteral` is the mangos-classic code. |
| `RestoreServersideEvents` | `false` | Retail restores nothing for serverside events (mangos-classic GameEventMgr.cpp:634-690). |
| `Announce` | `false` | vmangos `Event.Announce` (GameEventMgr.cpp:788-789). |
| `AllowReload` | `false` | Registers `.reload game_event` (retail has none; also needs `HotReload:Commands`). |

### Schedule dialects and computed schedules (`game-event-schedule-dialects`)

Pure maths in `Game/WorldState/Events`, every function taking the time and the zone as arguments.

- `GameEventDefinition` gains `ScheduleType` (default `Date`) and `LinkedTo`; `IsValid` follows mangos-classic
  (a serverside event of any length is valid, GameEventMgr.h:64) and vmangos (length above 0).
  `GameEventSchedule.IsActive` takes a start boundary (`Inclusive` default, as before).
- `GameEventValidation.BuildCMangos` is the loader of mangos-classic (GameEventMgr.cpp:107-226): serverside events get
  `FAR_FUTURE` (4102444800, 2100-01-01), an occurence of 0 disables the event (start = `FAR_FUTURE`, occurence = length),
  a length of 0 or an occurence below the length makes it unusable, a `game_event_time` row on a non-date schedule is
  ignored, and a date event without a time row keeps the default start 1 / end 0 and never runs (classic-db event 85).
  `BuildVMangos` is the vmangos loader (:183-259): length 0 is invalid, an invalid patch range resets to 0..10, an event
  outside patch 10 is disabled. `ResolveLinks` clears a `linkedTo` that names a missing or invalid event (:184-191).
  Every rejected row adds an issue line and never throws.
- `GameEventCalendar.Compute` does schedule types 11, 12 and 13 (mangos-classic `ComputeEventStartAndEndTime`,
  GameEventMgr.cpp:1210-1324), and `GaussEaster` is the `gaussEaster` port with its float arithmetic and corner cases
  (:1162-1208): equal to the true Easter for 2006-2099 (tested against an independent algorithm), pinned
  2024-2028 = 03-31, 04-20, 04-05, 03-28, 04-16, one day off in 2100.
- **Yearly (type 11)**: start and end move to the current year. Default `SpanNewYear`: a window that crosses New Year is
  not cut at the table's end date (the end is never before the window's own end), and in January last year's window is
  used while it is still open, so Feast of Winter Veil (Dec 16 23:00 + 19 days) runs to January 4th. `MangosLiteral` is
  the mangos-classic code, which gives it the table's end date (December 31st) and no window at all in January.
- **Lunar new year (type 12)** (`LunarPhase`): an independent implementation (Meeus, "Astronomical Algorithms",
  chapters 25 and 49: new moons and the sun's longitude, UTC+8), by the rule of the Chinese calendar: the month containing the
  winter solstice (by China date) is the eleventh month, and the new year is the start of the month two lunations later, or three
  when the first or second month after it has no major solar term (a leap month 11 or 12). mangos-classic's
  "second new moon after December 21st" is a month early in years such as 2015; this one matches the real calendar for
  every year 2000-2040 (tested, including 2033-34). The start is that date at local midnight, the end 21 days later.
  New moons are good to a few minutes (the planetary terms are omitted).
- **Easter (type 13)**: Easter Sunday at local midnight (year from the UTC clock, as `gmtime`), 7 days.
- **Darkmoon Faire (types 2-10)**: not implemented. classic-db has no row that uses them; such a row is disabled with an
  issue line, never stubbed.
- `GameEventCalendar.ReadWallTime` reads a zone-less datetime as `Wall` or `StandardTime`; `LocalToInstant` moves a
  time that does not exist (the spring-forward hour) forward by an hour.
- The recurrence arithmetic counts elapsed seconds from the start (both references), so a daily event such as classic-db
  event 400 (DayTime 7AM to 8PM) is anchored to its first instant: in a daylight-saving zone its window is one wall-clock
  hour later in summer than in winter. Pinned by a test.

### Weather content path and clock pins (`world-state-import-wiring`)

- `arcane-content-importer import` now imports `game_weather` and `exploration_basexp` (same `--replace` and `--dry-run`
  contract as the other importers, one savepoint inside the run's transaction; a malformed row is rejected before the database is
  touched, exit code 1). Until now only the library importer existed, so an imported world had no weather chances and every zone stayed fine.
  Verified against the real classic-db dump (33 and 61 rows).
- `.reload game_weather` (`GameWeatherReloadable`): vmangos `HandleReloadGameWeather` (ServerCommands.cpp:1813-1818)
  re-runs `LoadWeatherZoneChances`, which writes `mWeatherZoneMap[zone]` and never clears the map (Weather.cpp:446-505). So
  the reload overlays the table: a listed zone takes its row, a chance above 100 becomes 25 and is reported, an empty table
  changes nothing, and a zone no longer in the table keeps its old row until restart (reported). A row with the wrong
  column count rejects the reload naming the zone. `HotReload:EmptyTables` has no effect here (retail keeps).
- `MapWeatherExtensions` (`map.SetWeather(zone, type, grade, permanent)`, `map.GetWeather(zone)`): vmangos `Map::SetWeather`
  for scripts and EventAI (the Scourge invasion and Ossirian call sites pass `permanent = true`).
- **Behaviour fixed** (was a deviation): `MapWeather.Update` no longer skips ticking when `World:Weather:Enabled` is false.
  vmangos guards only the creation of weather (zone entry, Player.cpp:6600; `.wchange`, ServerCommands.cpp:101), and
  `Map::UpdateWeathers` / `Weather::Update` read no switch (Map.cpp:1042, Weather.cpp:67-84), so a zone that a script
  created keeps regenerating. With weather disabled and no script nothing is created, so nothing changes in practice.
- Clock pins (characterization tests, they pass on the code as it was): a far teleport resends SMSG_LOGIN_SETTIMESPEED
  computed anew in the configured zone; the packed fields around a daylight-saving edge are wall-clock fields (an hour that
  does not exist never appears).

## Not delivered (limits)

Recorded as slices are completed; see the final section.

## Provenance and citations

Every rule cites the vmangos or mangos-classic file and line in the code comment that implements it. The lunar and Easter
oracles in the tests are real calendar facts and an independent algorithm, not a second copy of the same port. No code or
data of the references is in this repository.
