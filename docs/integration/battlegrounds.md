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
