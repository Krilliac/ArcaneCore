# Integration notes: game-events-weather (wave 4)

Branch `claude/vw5-game-events-weather`. Area doc: [areas/game-events-weather.md](../areas/game-events-weather.md).
Every edit to a shared file is listed here so the integrator can resolve conflicts. Schema versions are single constants
(the integrator renumbers).

## Shared files edited (additive)

| File | Edit | Why |
|---|---|---|
| `src/ArcaneCore.Game/Maps/WorldRuntime.cs` | `public event Action<uint>? WorldTick` and one `Raise(WorldTick, diffMs, ...)` in `RunTick` after `RunCommands()` | A world-level tick seam for the game-event timer. If another wave-4 lane added an equivalent, keep one. |
| `src/ArcaneCore.Game/WorldState/WorldStateOptions.cs` | New members of `GameEventOptions` | `World:GameEvents` options. |
| `src/ArcaneCore.Game/WorldState/Events/GameEventSchedule.cs` | `ScheduleType` and `LinkedTo` init properties, `IsValid` also true for serverside, `boundary` parameter of `IsActive` | Dialect support. |
| `src/ArcaneCore.Game/WorldState/Weather/MapWeather.cs` | The `!Enabled` early-out of `Update` removed | vmangos keeps ticking existing zone weather (Map.cpp:1042). Test `Disabled_StillTicksTheZonesThatExist...` replaces `Disabled_NeverRegenerates`. |
| `src/ArcaneCore.Game/WorldState/Weather/WeatherChanceTable.cs` | `Snapshot()`, `Merge()` | `.reload game_weather` overlays. |
| `src/ArcaneCore.World/WorldState/WeatherFeature.cs` | `MergeChances` | Same. |
| `src/ArcaneCore.Data/World/WorldState/WorldStateDumpImporter.cs` | `WriteAsync` (CLI contract) | CLI import. |
| `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.cs` | Additive: read the world-state tables, one write call, two lines in the counts | CLI import. |

## Schema (renumber at merge)

| Component | Constant | Value here | Tables |
|---|---|---|---|
| World | `GameEventDataModule.Version` | 21 | `game_event`, `game_event_time`, `game_event_creature`, `game_event_gameobject`, `game_event_creature_data`, `game_event_quest`, `game_event_mail` |
| Characters | `GameEventStatusDataModule.Version` | 21 | `game_event_status` (global, documented no-op `ICharacterDataCleanup`) |

Tests use the constants. `IntegratedSchemaTests` has one tuple appended per module (merge conflict candidate: keep every lane's tuple).
`ContentImporterCli.cs` has further additive blocks (game-event importer: read, write call, count lines).

## New reloadable name

`game_weather` (`GameWeatherReloadable`). If the hot-reload-everywhere lane also registers that name, keep one (a duplicate
name fails startup).
