# Dungeon scripts, batch 20 (2026-10-17): Eastern Kingdoms world map script and event_wildhammer_message

This batch ports `event_wildhammer_message`, together with the Eastern Kingdoms world map script that the event looks Falstad up in. The
event (scripted_event_id 8608) is sent when the Horde quest The Final Message to the Wildhammer (q7843) has its spear planted by the Aerie
Peak well. ArcaneCore had no handler for it, so planting the spear did nothing.

## Port

- **`EasternKingdomsWorldMapData`** (`Instances/Scripts/World`) ports cmangos `world_map_eastern_kingdoms`.
  - Like `KalimdorWorldMapData` on map 1, it is created by `WorldMapData.Create` for map 0, instance 0, and `WorldMapScriptsFeature`
    attaches it.
  - OnCreatureCreate stores the single creatures of world_eastern_kingdoms.h: Jonathan 466, Wrynn 1747, Bolvar 1748, Prestor 1749,
    Windsor 12580, Falstad Wildhammer 5635 and Short John Mithril 14508.
  - SetData does nothing, as in cmangos. `ScriptedInstance` needs at least one encounter slot, so the script has one that nothing uses.
    Without that slot the constructor throws, which would break the creation of map 0.
- **`event_wildhammer_message`** (hinterlands.cpp:363-374) is `EasternKingdomsWorldMapData.OnSpellEvent`.
  - The event always counts as handled, so no DB script runs (it has none).
  - When a player sends it, the player is flagged for PvP (`MapCombat.UpdatePvp(player, true)`), and Falstad, if stored, yells the alarm
    at that player.
  - The yell's cmangos id is creature_ai_texts -27, which ClassicDB z2815 does not have. The same line, "Invaders! Invaders! We're under
    attack, boys! Get 'em!", is broadcast_text 10009 (a yell), so that id is used instead.
- **`AreaTriggerEngineScriptsFeature.HiveTower`** now needs `KalimdorWorldMapData`, not any world map script. Without this change the
  Hive Tower trigger would also have run against the Eastern Kingdoms script.
- **`Map.RemoveUpdater`:** detaches a per-map system. The World tests use it to swap the Eastern Kingdoms script for the Kalimdor one on
  their map-0-only test world.

## Deviations

- **Aura removal skipped:** cmangos also calls `RemoveAurasWithInterruptFlags(AURA_INTERRUPT_FLAG_PVP_ACTIVE_CANCELS)`. ArcaneCore's 1.12
  interrupt bits (`AuraInterruptMask`) have no such bit, so no aura is removed.
- **Not ported from world_map_eastern_kingdoms:**
  - the Stranglethorn Fever wave groups and their world states;
  - Short John Mithril's waypoints at the Gurubashi Arena event;
  - the Scourge Invasion circles and the Necropolis Health store.

## Tests

- `EasternKingdomsWorldMapTests` (2 tests): the PvP flag and Falstad's yell, the case with no Falstad in storage, other sources and
  other events, and `ScriptedEvents.Start` reporting the event as handled.
- `AreaTriggerEngineScriptsFeatureTests.EasternKingdoms_GetsItsWorldMapScript_AndTheHiveTowerStaysKalimdors`: map 0 of the test host gets
  the script.
- Game filter `EasternKingdomsWorldMap|ZoneScriptBatch|ScriptedEvent|Instance`: 1082 passed.
- World filter `Instances|AreaTrigger|Condition|Docs|ScriptCatalog|ScriptedEvent|EventAi|HotCode|QuestScript`: 449 passed.
