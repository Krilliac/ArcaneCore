# Integration notes: battlegrounds (wave 4)

Lane `battlegrounds`, branch `claude/vw5-battlegrounds`, base 7313b9e (`claude/vw4-integration`). Area doc:
[docs/areas/battlegrounds.md](../areas/battlegrounds.md).

## Shared files edited

None. Everything is new files:

- `src/ArcaneCore.Game/Battlegrounds/*.cs` (types, ports, template, options, scores, `Battleground`, `WarsongGulch`, `BattlegroundPackets`,
  `BattlegroundQueue`, `BattlegroundManager`)
- `tests/ArcaneCore.Game.Tests/Battlegrounds/*.cs`
- `docs/areas/battlegrounds.md`, this file

## Schema

No schema module, no table, no store. There is no version number to renumber and no provider-specific behaviour: nothing in this lane ran only on
SQLite because nothing in it touches a database. The slices that would (S3 content tables, S5 `character_battleground_data`) were not delivered;
see the area doc for their provider notes.

## What the integrator needs to know

- The framework is inert until the lifecycle slice (S6) lands: nothing constructs a `BattlegroundManager`, so no opcode behaviour changes.
  `CmsgBattlefieldStatus` is still answered by `InactiveQueueHandlers` (empty) and must stay there until S6 retargets it together with
  `InactiveQueueHandlerTests` and `InactiveQueueWireTests`.
- Name clashes to watch when merging with other wave-4 lanes: `ArcaneCore.Game.Battlegrounds` namespace, `BattlegroundType`/`BattlegroundStatus`/
  `BattlegroundWinner` (the honor, reputation and graveyard lanes may want the same words), `BattlegroundOptions` (config section
  `Battleground`, no binding exists yet).
- Seams another lane should implement when it merges, each with an inert default today: `IBattlegroundSpellPort` (aura-engine-completeness),
  `IBattlegroundHonorSink` + `IHonorRankSource` (honor-pvp-ranks), `IBattlegroundReputationSink` (reputation-factions), `IBattlegroundCalendar`
  (game-events-weather), `IBattlegroundHost.EventStateChanged/OpenDoors/DespawnDoors` (creature-movement-spawns loader),
  `IBattlegroundHost.ReturnToStartIfFar` and the graveyard id from `Battleground.ClosestGraveyard` (graveyards-resurrection),
  `IBattlegroundManagerHost.AllocateInstanceId` (the shared instance id generator in `InstanceManager`).
- Verification state: see the final report of the lane (build, full suite, MockClient self-test numbers).

## Wave 2 (lane `battlegrounds`, branch `claude/w2-battlegrounds`, base 2ca2f4e1)

New files: `src/ArcaneCore.Game/Battlegrounds/{ArathiBasin,AlteracValley,Battleground.Objectives}.cs`, `src/ArcaneCore.Kernel/WorldData/BattlegroundData.cs`,
`src/ArcaneCore.Data/World/Battlegrounds/BattlegroundWorldDataModule.cs`, `src/ArcaneCore.Data/Content/Import/Mappers/BattlegroundDumpImporter.cs`,
`src/ArcaneCore.Game/WorldState/Events/IWrappingSpawnGate.cs`, `src/ArcaneCore.World/Battlegrounds/*.cs`,
`src/ArcaneCore.World/Playerbots/Scenarios/ScenarioBattlegrounds.cs` and their tests.

Shared files edited (small, each a seam):

- `Game/Instances/InstanceManager.cs`: `BattlegroundMaps` (every resolver call for a battleground map is delegated) and `AllocateInstanceId`.
- `Game/Teleport/TeleportService.cs`: `BattlegroundEntryAllowed` replaces the blanket refusal of battleground maps.
- `Game/Combat/MapCombat.Death.cs` and `Game/Death/DeathSeams.cs`: `IBattlegroundPresence.OnSpiritReleased` before the ghost form.
- `Game/Channels/Channel.cs`: the WorldDefense gate only with an honor rank source (`WorldDefenseRankTests`, `ChannelManagerTests` changed with it).
- `Game/GameObjects/GameObjectDefines.cs`: `GameObjectType.FlagDrop = 26`.
- `World/WorldState/GameEventSpawnFeature.cs`: a gate that is an `IWrappingSpawnGate` keeps its place and gets the game-event gate as `Inner`.
- `World/Handlers/InactiveQueueHandlers.cs`: CMSG_BATTLEFIELD_STATUS answers through `BattlegroundFeature.SendStatusReports`
  (`InactiveQueueHandlerTests` now expects the battleground queue actions to be registered).
- `Data/Content/Import/Cli/ContentImporterCli.cs`: reads, writes and counts the four battleground tables.
- `tests/.../ScenarioTestWorld.cs`: the `StartAsync(Action<IServiceCollection>? configure)` hook, byte-identical to the gameobjects lane's.

Schema (as integrated, docs/integration/wave2-20261007.md "Schema map"): world 40 (`BattlegroundWorldDataModule`) and characters 39
(`CharacterBattlegroundDataModule`, `character_battleground_data`). The lane branch carried the plan's numbers, world 44 and characters 40, with
empty placeholder steps (`BattlegroundLaneSchemaGap38`-`43`, `BattlegroundLaneCharactersGap35`-`39`) holding the gaps open; the 2026-10-07
integration deleted every placeholder and renumbered the real modules down, so no database ever held the plan numbers.

Overlaps to resolve at integration:

- The gameobjects lane adds `IGameObjectFlagStands` and a `FlagStand` arm in `GameObjectMapSystem.Use`. This lane registers its own use handlers
  for `FlagStand` and `FlagDrop` on each battleground map (`RegisterUseHandler` wins over the switch), so both compile and the battleground
  handler runs. To use the shared seam instead, let `MatchRuntime` implement `IGameObjectFlagStands` and drop the `FlagStand` registration.
- The ops-social lane owns battleground chat; nothing here depends on it (the system messages go through `SMSG_MESSAGECHAT` with the
  `CHAT_MSG_BG_SYSTEM_*` types).
