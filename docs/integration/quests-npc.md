# Integration notes: Quests and NPC services (`feat/quests-npc`)

Work in progress. The PR is a draft until the area is complete. This file lists what the lead needs to merge it next to the other fleet branches.

## Schema versions claimed

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **v2** | `ArcaneCore.Data/Quests/QuestNpcWorldModule.cs` | `quest_template`, `creature_questrelation`, `creature_involvedrelation`, `npc_gossip`, `gossip_menu`, `gossip_menu_option`, `npc_text`, `npc_vendor`, `npc_trainer`, `taxi_nodes`, `taxi_path`, `race_taxi_start`, `points_of_interest` |
| characters | **v3** | `ArcaneCore.Data/Quests/QuestNpcCharactersModule.cs` | `character_queststatus`, `character_taxi` |

These are the next free numbers at the seam (world v1, characters v2). If the lead gives them to another branch, this branch renumbers. Each number is a single `SchemaVersion` property.

## Paths owned

- `src/ArcaneCore.Kernel/Quests|Npc/`: content records and store interfaces
- `src/ArcaneCore.Data/Quests|Npc/`: data modules and EF stores
- `src/ArcaneCore.Game/Quests|Npc/`: rules, engine, packets
- `src/ArcaneCore.World/Quests|Npc/`: handler groups and the world feature
- `tests/**/Quest*`, `tests/**/Npc*`

## Shared files edited

None. Everything plugs in through the seams in `docs/integration/seams.md`:
- handler groups (`IOpcodeHandlerGroup`)
- one `IWorldFeature`
- `PlayerLoggedIn` / `PlayerLoggingOut`
- two `IDataModule`s
- `IWorldTestServices`
