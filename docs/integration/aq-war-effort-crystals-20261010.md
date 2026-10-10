# AQ war effort: crystal event, Colossus events and later vmangos updates (2026-10-10)

Branch `grok/aq-war-effort-crystals`, stacked on #43 (`grok/aq-war-effort-saurfang`, not yet merged), base `integrate/wave17`.
No schema change.

## Findings

**Crystal event (vmangos game_event 65, EVENT_WAR_EFFORT_WORLD_CRYSTALS): not imported, on purpose.** vmangos runs it in stages 9-11
(the ten-hour war): 235 creatures (Qiraji at the crystals, the three Colossi, 15341) and 20 crystals (180810/180811). ArcaneCore
already starts ClassicDB event 123 ("AQ War Effort Phase 4 10 Hour War") for the same window. That event spawns its own layout
of the same content: 726 creatures, including the Colossi, the crystal Qiraji, Saurfang, 55 Cenarion Hold infantry and the three
Colossus researchers, plus 41 crystals (180810). None of the 226 vmangos event-65 creatures has a ClassicDB twin within 5 yd, and
none of the 20 crystals does. The two are different layouts of one event, so importing event 65 on top would double the war.

**Colossus events 62-64: already covered.** vmangos `npc_colossus::JustDied` sets `VAR_WE_HIVE_REWARD` (not saved), and
`UpdateHiveColossusEvents` starts event 62/63/64, which spawns the researcher for that Colossus. In ClassicDB the researchers
(15797-15799) stand in event 123 for the whole war, and their quests (8857-8859) are tied by `game_event_quest` to events 125-127.
ArcaneCore starts those events from the saved Colossus death flags (wave 16). So the quest unlocks on the kill, as in vmangos.
The NPCs are visible before the kill, and the flag survives a restart (vmangos deliberately drops it on a crash).

**#43 doubled part of the war.** During the ten-hour war, event 123's Silithus Saurfang and infantry stood next to #43's.

## What changed

- **Adopting ClassicDB event 123's war layout.** When event 123's Silithus Saurfang is in the world, he gets the war script and
  the summoned Saurfang leaves. When its Cenarion Hold infantry is present, the vmangos Ironforge/Orgrimmar infantry of days 1-5 step
  aside. The ClassicDB infantry get the formation script, so they turn for the attack and follow Saurfang in the final battle. The
  Orgrimmar Saurfang and the Silithus entries elsewhere keep their own AI.
- **Later vmangos migrations applied to `WarEffortTroopCatalog`.** I checked every vmangos core `sql/migrations` file after the
  2021-06-14 dump (1164 files) for events 54-65 and their spawn guids:
  - `20230125042234`, `20230314125943`: sniffed positions for 14 troops (priestesses, riflemen, marksmen, a gate Anubisath).
  - `20230316080310`: custom entry 987000 removed, and 14720 got `npc_aqwar_saurfang`. That matches what #43 already did.
  - `20231111021653`: Saurfang's war-room spot.
  - `20241226150330`: the three catapults were re-spawned as 8152-8154 (still days 4/5/3), and a fourth catapult (8155) was added on
    day 2. Two steam tanks moved. 23 Alliance first-aid crates (180714) in Ironforge were added to day 5.
  - `20241228161610`: the war map and the banner moved.
  - The other hits were the resource-pile positions of event 22. ArcaneCore's piles come from classic-db, so those don't apply.
  - Nothing changed events 59-65 or their spawns.
- The day objects now spawn on both maps (the crates are in Ironforge).

## Tests

There are 3 more tests in `WarEffortSceneTests` (15 in total): the migrations in the catalog, day objects by map including the
Ironforge crates and their removal, and adopting the ClassicDB war layout (no second Saurfang, no doubled infantry, the database
soldier turning for the attack, the Orgrimmar Saurfang untouched). `ArcaneCore.World.Tests` passes in Release
(3354 passed, 46 skipped).

## Follow-up: Colossus researchers hidden until the kill (`grok/aq-war-effort-researchers`, stacked on #45)

This matches vmangos events 62-64. Researchers 15798 (Ashi), 15799 (Regal) and 15797 (Zora) appear only once their Colossus is
killed. Their ClassicDB event-123 spawns are kept off the map as script-only spawns, and each one is brought in by
`SpawnScripted` once the saved Colossus death flag is set during the ten-hour war. `SpawnScripted` keeps the spawn's own gating,
so a researcher still needs event 123 to be running. He leaves when the war ends. Because the flags are saved, a restart brings
back the researchers whose Colossus is already dead (vmangos forgets them on a crash). The crystal layout is unchanged. Test:
`EachColossusResearcherAppearsOnlyOnceHisColossusIsDead`.

## Colossus script (vmangos npc_colossus)

`SilithusBossAi` now follows vmangos silithus.cpp `npc_colossusAI`: the spawn line (11424-11426), Colossal Smash (26167) 60 s into
the fight and then alternately 10 s and 60 s after each successful cast, the text emotes "Colossus begins to cast Colossus Smash"
at the cast and "Colossus lets loose a massive attack" 5 s later, and the Nostalrius evade that neither heals nor sends the Colossus
home (threat dropped, stays in place, timers kept). Limit: vmangos also strips all auras on that evade; ArcaneCore's AI services have
no remove-all-auras call, so auras stay.
