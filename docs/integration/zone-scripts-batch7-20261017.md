# Zone scripts, batch 7 — 2026-10-17 (live sweep, `grok/live-sweep`)

References (read-only): mangos-classic `src/game/AI/ScriptDevAI/scripts/` at `8ec338a170`, data from ClassicDB z2815 (`script_waypoint`,
`script_texts`, `scripted_areatrigger`). Structure as in [batch 6](zone-scripts-batch6-20261016.md): creature scripts are `CreatureAI` classes in
`src/ArcaneCore.Game/Creatures/Scripts/`, registered by entry in `CreatureAiFactory`, event quests in `_entryScriptQuests`.

Each script below was checked unported by its creature entry first (no entry-script, no `EscortSpecCatalog` row, no other reference).

## Ported

| Script (ScriptName) | Reference | Port |
|---|---|---|
| `npc_tooga` (5955, quest 1560 "Tooga's Quest") | tanaris.cpp:251-407 | `ToogaAI` (`FollowerAI`). Follows on accept (faction 290); worried lines 1 in 51 each per 5 s; Torta (6015) within interaction distance: group credit when incomplete, the six-line reunion 5 s apart after 1 s, then he walks into the water (point 1) and is done. A dead or missing Torta ends it. |
| `npc_kindal_moonweaver` (7956, quest 2969 "Freedom for All Creatures") | feralas.cpp:440-572 | `KindalMoonweaverAI` (`FollowerAI`). Accept: start line, stand, not immune to NPCs, follow (faction 231). Polls the Kalimdor map script for `TYPE_FREEDOM_CREATURES` = DONE, then resets it, credits the group when incomplete, thanks the player and 5 s later despawns and respawns. Fight: Shoot / Multi-Shot at 5-30 yd, aggro line 1 in 4, flee once at 15%. |
| `npc_captured_sprite_darter` (7997) | feralas.cpp:343-397 | `CapturedSpriteDarterAI` (`EscortAI`). Each reset picks one of its four `script_waypoint` paths (new `EscortAI.EscortWaypointPath`, mangos-classic `SetEscortWaypoints`); at point 8 it sets `TYPE_FREEDOM_CREATURES` = SPECIAL and despawns. Mana Burn on a random mana user of its threat list. |
| `go_cage_door` (143979) + `world_map_kalimdor` TYPE_FREEDOM_CREATURES | feralas.cpp:400-437, world_kalimdor.cpp:375-383 | `KalimdorWorldMapData.OnGameObjectUse` / `SetData`. The use restarts the count, frees the darters within 20 yd of the door (faction 495 until respawn, escort at a run), summons a Grimtotem Raider (7725) at the player, and the nearest living Kindal within 20 yd calls it out and attacks it. Six SPECIALs make DONE. |
| `npc_twiggy_flathead` + `at_twiggy_flathead` (6248, trigger 522, quest 1719 "The Affray") | the_barrens.cpp:273-505 | `TwiggyFlatheadAI`, the trigger in `AreaTriggerScriptsFeature`. Six challengers (6240) appear friendly 1 s after the start; one at a time turns hostile (14), roars and attacks every 25 s (15 s after a death, with Twiggy's "down" line); Big Will (6238) runs in 5 s after the sixth and turns hostile (32) at his point with his line; his death ends it. The player dead or gone fails it and despawns the summons. |
| `npc_wizzlecranks_shredder` (3439, quest 863 "The Escape") | the_barrens.cpp:509-660 | `WizzlecranksShredderAI` (`EscortAI`). Accept: start line, Ratchet faction 637 until respawn, run. Point 1 / leaving 10 / leaving 19 lines, walk from 10, run from 19; two Southsea Mercenaries (3282, 2 min out-of-combat lifetime) attack it at 18; during point 25's 40 s wait three lines 5 s apart, then the group event and Pilot Wizzlecrank (3451, 3 min) while the shredder lies down. |

### Documented deviations

- **Kindal: the 5 s despawn uses `<=`.** The source's `if (m_despawnTimer) { if (m_despawnTimer < diff) ... }` stops at exactly 0 and never sends
  her back; fixed-step updates hit 0 every time.
- **Kindal: the completion poll** keeps the source's behaviour of never re-arming its 1 s timer (it polls every update after the first second);
  the Multi-Shot branch re-arms the Shoot timer (8-11 s) as the source does.
- **Kindal flees** with `MoveFleeing(null, 7000)`, the engine's SetInPanic with CONFIG_UINT32_CREATURE_FAMILY_FLEE_DELAY's default.
- **Twiggy keeps the base victim update** (as Regthar in batch 6), and Big Will's `SummonedMovementInform` is read from him each update (within a
  yard of point 7, not fighting), as Daphne's raiders are.
- **Shredder: Pilot Wizzlecrank appears a yard in front of the shredder** (cmangos' all-zero summon position is a close point beside it).

## Tests

`ZoneScriptBatch7Tests` (Game, 29): registration and event quests; Tooga (accept, worried lines only, the reunion with credit, order, speakers and
timers, the water and despawn, no credit without the quest, a dead Torta, out of reach); Kindal (accept, the six-darter credit, reset and return,
no credit without the quest, ignored while not following); the map script count; the darters (all four paths picked, point 8 counted and
despawned, Mana Burn timing and target); the cage door (count restart, darters in range freed with the faction and the escort, the far one not,
the raider at its spot, Kindal's line and attack; another object not taken); Twiggy (trigger only with the quest, not twice, not dead or far; six
friendly in their spots; the first at 5 s; the next 15 s after a death; Big Will hostile at his point with his line, his death ends it; the
player's death fails it); the shredder (accept; the whole run with walk/run switches, lines in order, the ambush, credit, pilot and dead stand
state; the mercenaries attack the shredder).
