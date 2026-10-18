# Zone scripts, batch 16 (2026-10-17)

Ports mangos-classic ScriptDev2 `npc_cork_gizelton` and `npc_rigger_gizelton` (kalimdor/desolace.cpp:418-591): the Gizelton caravan
escorts in Desolace. Both NPCs had no script in ArcaneCore before, so neither "Bodyguard for Hire" (5821) nor "Gizelton Caravan" (5943)
could be completed.

- `Creatures/Scripts/CorkGizeltonAI.cs` holds `CorkGizeltonAI` (11625, an `EscortAI`) and `RiggerGizeltonAI` (11626, a `ScriptedAI`).
  Both are registered in `CreatureAiServices`, and both quests are listed as scripted event quests.
- `Creatures/CreatureMapSystem.ScriptedWaypoints.cs` adds `ResumeWaypointPath(creature, pathId, pointId)`. It mirrors cmangos
  `MoveWaypoint(path, PATH_FROM_WAYPOINT_PATH)` plus `SetNextWaypoint(point)`. It also updates a formation leader's formation movement,
  as a dbscript MOVEMENT step does.

## Behaviour

classic-db z2815 carries the data the cmangos script expects:
- Spawn group 19019, the caravan: Cork, Rigger and two Caravan Kodos.
- Relay 1162501, which builds the formation and puts Cork on waypoint_path 19019.
- The escort paths waypoint_path 11625 (19 points) and 11626 (21 points).
- Their movement scripts, dbscripts_on_creature_movement 1162501-1162504 and 1162601-1162604. These carry the warnings, the Kolkar and
  Burning Blade ambushes, the faction restore and the run on.
- The quest start scripts for 5821 and 5943, which give the other Gizelton the escort faction.

The script does the following:
- Taking 5821 from Cork starts his escort on path 11625.
- Taking 5943 from Rigger finds the nearest living Cork within 100 yd and starts his escort on path 11626.
- At point 19 (north escort) or point 21 (south escort), the escort player's group gets the quest event, if the player still has the quest
  incomplete. Cork ends the escort and the caravan goes on with its loop at a run, from point 33 or point 100 of path 19019.
- If a caravan member dies during the escort, the quest fails. The failure lands 60 s later, or as soon as no caravan member is still
  fighting. Both quests fail for the group, through GroupEventFailHappens, and the whole caravan despawns. The spawn group respawns it.

## Deviations

- **No CreatureGroupMemberDied hook:** ArcaneCore has no such AI hook. When an escort starts, Cork notes the living Rigger and Kodos within
  40 yd and watches them every update.
- **Rigger escort quest:** the source passes Cork's stale `m_questForEscort` to the Rigger escort. Here that escort carries 5943, so
  Cork's own death, or the escort player getting out of range, fails the quest that was actually taken.
- **Resume timing:** the move back onto the loop happens on the next update rather than inside the arrival callback.
- **Unchanged from the source:** a player who walks away is handled by the EscortAI range check. It fails the quest and despawns only
  Cork; the formation then picks a new leader.

## Not ported (checked)

- **vmangos version:** vmangos uses a different caravan model, with a summoned caravan, script_waypoint 281 points and hardcoded ambusher
  coordinates. The classic-db data ArcaneCore loads follows the cmangos model, so the cmangos script was ported.
- **`event_wildhammer_message` (hinterlands.cpp):** still needs an Eastern Kingdoms world map script that stores Falstad Wildhammer.
