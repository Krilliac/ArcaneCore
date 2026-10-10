# AQ war effort: real Silithus schedule, transport-day troops and Saurfang's combat (2026-10-10)

Branch `grok/aq-war-effort-saurfang`, stacked on #40 (`grok/aq-war-effort-scenes`), base `integrate/wave17`. No schema change.

## Source data

- vmangos world database: `github.com/brotalnia/database` `world_full_14_june_2021.7z` (the newest full dump there).
  `game_event` 54-58 are "AQ War Troop Silithus (NPC & GO) DAY 1-5", 59 "Cenarion Hold attack", 60 "Troop Silithus 3",
  61 "Final Battle (NPC)". Every one is `hardcoded = 1` with no start time: the core starts them.
- The schedule is in vmangos core `src/game/HardcodedEvents.cpp` `WarEffortEvent::Update` and `warEffortStageEvents`,
  with the times in `HardcodedEvents.h`: one stage (one day) per transport day, each adding the next day event, all five kept
  through the gong wait and the war; `WAR_EFFORT_CH_ATTACK_TIME` = 4 h after the gong, `WAR_EFFORT_FINAL_BATTLE_TIME` = 4 h
  after that, the war ends `WAR_EFFORT_GONG_DURATION` (10 h) after the ring. Event 60 is in no stage, so it is not used.

## What changed

- **Schedule** (`WarEffortSceneSchedule`): the Cenarion Hold attack and the final battle now sit **in the ten-hour war**
  (+4 h and +8 h from the war start), not in the last hours of the transport as #40 assumed. `TransportDaysActive` gives the
  day events 54-58. All of it comes from the saved phase and deadline, so a restart resumes the right stage.
- **Troops** (`WarEffortTroopCatalog`, generated from the dump): 98 creatures and 8 game objects (steam tanks, catapults, the
  war map) for events 54-58, and the 8 Qiraji of event 61 at the Scarab Wall. ClassicDB has every template but none of these
  spawns. The feature summons and removes them as the days and the stage change; a dead troop comes back once its corpse is gone.
- **Formations** (`TroopAi`, vmangos `npc_infantrymanAI` and its scripts for entries 15861, 15853, 15855, 15634): when the attack
  starts each soldier takes a quarter turn about its unit's origin (Ironforge clockwise, Orgrimmar and the riflemen
  counter-clockwise), the priestesses line up on their ten points, and they stand in their ready emote. From the final battle on
  they follow Saurfang at their offset (the priestesses mounted, dismounting on aggro). A soldier dying within 30 yd of Saurfang
  in combat gives him Vengeance (26331) with the emote and a line, once per aura.
- **Saurfang** (`SaurfangWarAi`, vmangos `npc_aqwar_saurfangAI`): there from transport day 1 in the war-room spot of vmangos
  spawn 113001, faction 777 (Might of Kalimdor); at his wave post during the attack; in the final battle mounted (10278) and
  immune while he speaks, then the world broadcast and the ride. Combat: Saurfang's Rage (26341) and its line on entering combat
  plus one of the nine aggro lines, Mortal Strike (24573), Cleave (16044), Charge (15749) and Terrifying Roar (14100) on the
  source timers (first 1-15 / 3-9 / 0-4 / 4-12 s, then 11-20 / 9-21 / 4-12 / 30-40 s, re-armed only on a successful cast), a
  10% kill line, the victory line on the evade after the last wave. He dismounts to fight, pauses the ride, remounts and goes on
  from the next point when home, and dismounts at the gate.

## Limits

- vmangos's Silithus Saurfang is a custom entry (987000, level 62, 2.1M health). ClassicDB has no such template, so the scene uses
  14720 with the faction set by the script; his stats are 14720's.
- The source reads map heights for the turned positions; ArcaneCore keeps the spawn's z.
- The cavalrymen, marksmen, archmage, generals and the other unscripted entries are static spawns (vmangos gives one cavalryman
  a waypoint path, which is not imported).
- Troops come back when the corpse is gone, not on the 25 s spawn timer; event 59's trigger creature (21010, a vmangos custom
  template) is replaced by the feature itself.
- Like the source's restart, a Saurfang summoned in mid-ride starts the path again from the first point.
- The 2021 dump predates later vmangos migrations; nothing newer was found for these events.

## Tests

`WarEffortSceneTests` (12): stage times from the war start, day events, catalog counts, wave timing, speech timing, troops and
Saurfang by day and removed when the war is over, a dead troop coming back, waves after a restart and the last-wave flag,
formations turning and following (and an unscripted entry staying unscripted), speech resumed mounted and immune and the full ride
with the dismount, combat (unmount, paused ride, timer ranges, the victory line once, remount and resume), and the Orgrimmar
Saurfang and a stray grunt keeping their own AI. `ArcaneCore.World.Tests` all pass in Release (3351 passed, 46 skipped).
