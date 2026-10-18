# Zone scripts, batch 8 — 2026-10-17 (live sweep, `grok/live-sweep`)

References (read-only): mangos-classic `src/game/AI/ScriptDevAI/scripts/` at `8ec338a170`, data from ClassicDB z2815 (`script_waypoint`,
`waypoint_path`, `dbscripts_on_creature_movement`, `script_texts`). Same structure as [batch 7](zone-scripts-batch7-20261017.md). Each script was
first checked unported by creature entry (no entry script, no `validated.json` escort, no other reference).

## Ported

| Script (ScriptName) | Reference | Port |
|---|---|---|
| `npc_arei` (9598, quest 4261 "Ancient Spirit") | felwood.cpp:308-491 | `AreiAI` (`EscortAI`). Accept: start line, neutral passive escort faction 113 until respawn, run. First Irontree / first Toxic Horror fought: one line each. Point 37 (last): she holds, an Irontree Stomper (with her call) and two Wanderers come at her (TIMED_OOC_OR_DEAD 60 s). The last one down plays aEpilogDialogue (4 / 5 / 3 / 7 s; Arei Transform 14888 with the curse line), then the group event and a forced despawn 10 s later. Wither Strike every 3-6 s (first 1-4 s). |
| `npc_ogron` (4983, quest 1273 "Questioning Reethe") | dustwallow_marsh.cpp:164-466 | `OgronAI` (`EscortAI`). Accept: walk (instant respawn at the end), faction 290 + toggle immune to NPCs until respawn, start line. The first Paval Reethe (4980) he sees while escorting is kept. Points 10/11 lines, 12 pause; then one step every 5 s: the intro, Caldwell (5046), Hallan (5045) and two Skirmishers (5044) friendly (35) for 5 min (Caldwell walks up, the rest follow him), their lines, Hallan's Fake Shot (7105) at Reethe, the clean-up; they turn hostile (151) on Ogron. His evade while still paused completes it: group event, Reethe's last line, thanks, run back. |
| `npc_captured_arkonarin` (11016, quest 5203 "Rescue From Jaedenar") | felwood.cpp:164-306 | `CapturedArkonarinAI` (`EscortAI`). Kneels until the quest; accept: stand, faction 250 + toggle immune to NPCs until respawn, use the cage (176306) within 5 yd, walk waypoint_path 11016 (its movement scripts run through the escort). Point 1 broadcast line 6433 with a talk emote, Mortal Strike / Cleave from point 37, pause at 83 until the Spirit of Trey (11141) she summons dies, at 85 face the player, group event, immune to NPCs, and the quest is let go (new `EscortAI.QuestEnd`, cmangos npc_escortAI::QuestEnd). Aggro: one of three broadcast lines four times in five. |

### Notes

- `EscortAI.QuestEnd()` clears only the escort quest, so a later death fails nothing; the player and the walk stay (escort_ai.cpp:306-309).
- Ogron's soldiers get `Faction` 35 at summon and 151 at the attack exactly as `setFaction` (no temporary faction).
- The Arko'narin story between the script's points (the chest 176225, Strength of Arko'narin 18163, her update to 11018, Holy Fire, the
  Legionnaires 9862, the Spirit of Trey and the final despawn) is the DB's movement scripts on waypoint_path 11016, not this script.

## Not ported here (left on the list)

- `npc_cork_gizelton` / `npc_rigger_gizelton` (11625/11626, quests 5821/5943): the caravan needs creature-group despawn and fail hooks
  (`CreatureGroupMemberDied`, `GetCreatureGroup()->Despawn()`) and the handover from the escort path back to waypoint_path 19019 at a given
  point (`MoveWaypoint` + `SetNextWaypoint`), which the escort base does not offer yet.
- `npc_kitten` / `npc_corrupt_saber` (9937/10042, quest 4506): needs the owner-follow of a player's summoned kitten and Corrupted Saber
  Visual (16510) turning the creature into 10042 in place (SetEntry without UpdateEntry).

## Tests

`ZoneScriptBatch8Tests` (Game, 16): registration and event quests; Arei (accept; the ambush with her call and the hold at the last point; the
epilogue order, timings, transform cast, credit and despawn; the two aggro lines once each; Wither Strike); Ogron (accept; the whole questioning
with Reethe, the guests, Fake Shot, the fight, credit, thanks and the run back; without Reethe only his own lines); Arko'narin (kneel, accept,
cage, path; point 1 line, point 37, the pause until Trey dies, point 85 credit and immunity; no specials before point 37; Mortal Strike and
Cleave after it).
