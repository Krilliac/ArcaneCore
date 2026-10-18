# Zone scripts batch 21 (2026-10-17): Stranglethorn Fever, Gurubashi Arena Booty Run

## Witch Doctor Unbagwa (1449), quest 349 "Stranglethorn Fever"
- Port of vmangos `npc_witch_doctor_unbagwa` (stranglethorn_vale.cpp:203-356) as `WitchDoctorUnbagwaAI`, an entry script.
- cmangos runs the waves through spawn groups 18011-18013 and `OnCreatureGroupDespawn`. classic-db z2815 has none of those groups, so the self-contained vmangos script is the one ported.
- Turning the quest in starts the event. Unbagwa drops his quest giver flag and takes faction 113. Ten seconds after each wave is down, he summons the next one at (-13773.6, -3.9, 41.6): 3 Enraged Silverback Gorillas, then 4 plus Konda, then 5 plus Mokk the Savage. Each summon lasts 3 minutes out of combat and attacks him.
- He resets when the last wave dies, or when a living attacker despawns and no attackers are left.

## Short John Mithril (14508), game event 16 "Gurubashi Arena Booty Run"
- cmangos `world_map_eastern_kingdoms::OnEventHappened` starts his waypoint movement when event 16 starts. The run itself is DB data that z2815 lacks, so `ShortJohnMithrilAI` hardcodes it from vmangos 20170726102314_world.sql:
  - event_scripts 9530: yell bt 9730, exclamation emote, gossip and quest giver flags removed, walk after 3 s.
  - Points 1-14 down to the arena floor.
  - Script 1450816: run, yell bt 9731, Arena Treasure Chest 179697 for 10800 s.
  - Points 15-31 back to his ledge.
  - Script 1450833: flags restored.
- The chest goes beside point 14 (-13202.3, 276.6, 21.86) because vmangos gameobject guid 12029's position is not in this DB.
- New seam: `WorldMapScriptsFeature` is now an `IGameEventListener` on `GameEventFeature`. `WorldMapData.QueueGameEvent` queues each change, and `OnEventHappened` runs it at the map's next update on its own thread, which is safe with parallel map updates. Kalimdor's `Update` now calls the base.

## Tests
- Game.Tests `ZoneScriptBatch21|WorldMap|DaphneStilwell|CreatureAiServices|EntryScript|ScriptCoverage|ScriptName`: pass.
- World.Tests `WorldMapScripts|AreaTriggerEngine|GameEventFeature`: pass.
