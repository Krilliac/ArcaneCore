# Integration notes: Quests and NPC services (`feat/quests-npc`)

Work in progress. The PR is a draft until the area is complete. This file lists what the lead needs to merge it next to the other fleet branches.

## Assigned schema versions

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **v6** | `ArcaneCore.Data/Quests/QuestNpcWorldModule.cs` | `quest_template`, `creature_questrelation`, `creature_involvedrelation`, `npc_gossip`, `gossip_menu`, `gossip_menu_option`, `npc_text`, `npc_vendor`, `npc_trainer`, `taxi_nodes`, `taxi_path`, `race_taxi_start`, `points_of_interest` |
| characters | **v5** | `ArcaneCore.Data/Quests/QuestNpcCharactersModule.cs` | `character_queststatus`, `character_taxi` |

Assigned in the [2026-10-03 integration candidate](fleet-20261003.md). The source
branch originally requested world v2 and characters v3.

## Paths owned

- `src/ArcaneCore.Kernel/Quests|Npc/`: content records and store interfaces
- `src/ArcaneCore.Data/Quests|Npc/`: data modules and EF stores
- `src/ArcaneCore.Game/Quests|Npc/`: rules, engine, packets
- `src/ArcaneCore.World/Quests|Npc/`: handler groups and the world feature
- `tests/**/Quest*`, `tests/**/Npc*`

## Shared files edited

The source snapshot contains Game/data services. The following planned daemon
seams are still pending; they are not registered by this integration:
- handler groups (`IOpcodeHandlerGroup`)
- one `IWorldFeature`
- `PlayerLoggedIn` / `PlayerLoggingOut`
- two `IDataModule`s
- `IWorldTestServices`
