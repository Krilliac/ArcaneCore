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

### Game-event data (`game-event-data`)

- **World schema** `GameEventDataModule` (constant `Version = 21`, the next free world version at this base; the integrator
  renumbers): `game_event`, `game_event_time`, `game_event_creature`, `game_event_gameobject`, `game_event_creature_data`,
  `game_event_quest`, `game_event_mail`. `game_event` is a superset of both dialects (mangos-classic `schedule_type`, `linkedTo`;
  vmangos `start_time`, `end_time`, `hardcoded`, `disabled`, `patch_min`, `patch_max`); dates are the source text
  (`yyyy-MM-dd HH:mm:ss`, no provider datetime type, no zone: the service decides how to read them). Event numbers are `int`
  in the tables (the sources are smallint, and some providers have no unsigned small integer); the sign convention is kept
  (positive spawns during the event, negative removes during it). Keys: `(guid, event)` for the spawn and creature-data tables,
  `(quest, event)`, `(event, race_mask, quest)` for mails. All identifiers are lower snake case. The `CreateTableChange` steps
  are re-runnable, so a MariaDB start that died halfway (DDL is not transactional) converges on the next start (tested by
  recreating that state).
- **Characters schema** `GameEventStatusDataModule` (`Version = 21`): `game_event_status` (one `event` column, vmangos
  `characters.sql:499-502`). It is global, not per character, so its `ICharacterDataCleanup` is a documented no-op (the
  guard test requires every characters module to have one). `EfGameEventStatusStore.ReplaceActiveAsync(set)` deletes all and
  inserts the set in one transaction (a failing insert keeps the old set); calls are serialised in the process by a semaphore
  rather than a database advisory lock (Npgsql pooling returns the same physical connection to concurrent callers, so advisory
  locks would be re-entrant; MariaDB repeatable read would fail one of two interleaved replaces on the key). This is a
  deliberate storage deviation from vmangos, which inserts per start and deletes per stop and truncates at start; the
  observable result is the same set.
- **Importer** `GameEventDumpImporter` (library and `arcane-content-importer import`): both dialects by column name from the
  dump (reordered or extra columns do not shift values); vmangos `display_id` is the model id; vmangos rows are patch
  versioned, so `game_event_creature_data` keeps the highest `patch` not above 10 per `(guid, event)` and `game_event_quest`
  skips `patch_min` above 10 (skipped rows are counted in the report). A missing key column, a value count that differs from the
  column count, a non-numeric value or an unterminated statement rejects the whole dump before anything is written; later
  rows replace earlier ones with the same key; `--replace` empties the seven tables first; without it an existing key fails the
  write and changes nothing. Rows are NOT checked against `creature` / `gameobject`: classic-db has 33 creature and 1126
  gameobject event rows whose spawn is not in the dump (1095 of them on Noblegarden, event 9); they are imported, counted by
  the test, and skipped and logged by the world loader. Verified against the real classic-db dump: 67 events (26 serverside,
  36 date, 3 yearly, 1 lunar, 1 Easter), 38 `game_event_time` rows, 3219 creature rows (108 negative), 12274 gameobject rows
  (2 negative), 977 creature-data rows, 61 quests, 1 mail.
- Tests: every table round-trips on the provider matrix (SQLite locally; MariaDB and PostgreSQL only on hosted CI, where they
  are the only place provider semantics are exercised), including signed events and date text; duplicate keys are refused;
  the status store replaces atomically and serialises two concurrent replaces. This machine ran SQLite only.

### Game-event service (`game-event-service`)

- `GameEventService` (`Game/WorldState/Events`) is the state machine of `GameEventMgr`: `Update` (vmangos GameEventMgr.cpp:708-763,
  mangos-classic :669-712), `StartEvent` / `StopEvent` with overwrite (:72-113), `EnableEvent` (:115-152), `Initialize`
  (:673-696). One shell serves both dialects; the rules that exist in one reference only are guarded by the dialect:
  vmangos skips `hardcoded` events (an `IWorldEventHandler` runs them and sets the next-update delay) and has no `linkedTo`;
  mangos-classic starts a linked event only while its parent is active, skips serverside events after the first pass, and
  recomputes its computed schedules (here once per local calendar day instead of at the weekly reset, which ArcaneCore does
  not have; `WeeklyReset` is a recorded limit, not an option that cannot work).
- `Update` returns the delay in ms: the smallest `NextCheck` (and any hardcoded handler's delay) plus one second, at most a
  day plus one second. On the first pass an INACTIVE event spawns its negative-listed objects (`SpawnEvent(-id)`), never
  again (the `m_IsGameEventsInit` quirk).
- A start runs the effect phases in vmangos' order for every registered `IGameEventEffects` (spawn +id, unspawn -id,
  creature data, event quests; a stop reverses the signs), then tells the `IGameEventListener`s (`OnEventChanged(id, active,
  resume)`, mangos-classic `OnEventHappened`). A throwing effect or listener is logged and does not stop the others or the
  start. `IGameEventStatusSink` receives the running set after every change; `Initialize` first reports the empty set (vmangos
  TRUNCATEs `game_event_status` after reading it), then events that were running resume (`resume = true`, listeners see it).
- **Manual start/stop arithmetic**: `StartEvent(overwrite)` sets the start to now and, if the end is not after it, the end to start
  plus the length; both references add the length as SECONDS (`start + length`) although it is minutes everywhere else, which
  would end a manually started event after `length` seconds. Minutes are used. Under the mangos-classic boundary (`start < now`)
  the manual start is dated one second back so an update in the same second does not stop the event just started.
  `StopEvent(overwrite)` back-dates the start by the length so the schedule does not restart it.
- `GameEventLoader` builds the definitions: dialect (`Auto`: vmangos when any row carries its own `start_time`), start
  boundary (`Inclusive` vmangos, `Exclusive` mangos-classic), `Wall`/`StandardTime` date reading, the validation rules, and
  the per-event row lists with rows that name a missing event or event 0 dropped (each an issue line). An unreadable date
  (`0000-00-00 00:00:00`) is reported and the event never runs (it does not default to 1970).
- `GameEventFeature` (world feature): binds `World:GameEvents`, loads the tables and `game_event_status`, and drives the service
  from `WorldRuntime.WorldTick`: the first tick initialises, later ticks accumulate until the delay the last update asked for.
  It is the `IGameEventState` consumers ask (`IsActiveEvent`, `IsActiveHoliday`, `ActiveEvents`). `GameEventStatusWriter` stores
  the running set off the world thread: the latest set wins, writes never overlap, a failure is logged and retained for the next
  change or the shutdown flush, and never reaches the world thread. `Announce` sends mangos_string 4
  (`|cffff0000[Event Message]: <description>|r`) to every player when the option is on.
- `RestoreServersideEvents` (default false): with it, serverside events recorded in `game_event_status` are re-applied with
  resume at start-up and kept out of the first pass that would stop them. Retail restores nothing.
- Event mails (`SendEventMails`) are not delivered (see limits).

## Not delivered (limits)

Recorded as slices are completed; see the final section.

## Provenance and citations

Every rule cites the vmangos or mangos-classic file and line in the code comment that implements it. The lunar and Easter
oracles in the tests are real calendar facts and an independent algorithm, not a second copy of the same port. No code or
data of the references is in this repository.
