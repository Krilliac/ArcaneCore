# Zone scripts, batch 4 — 2026-10-16

Lane `zone-scripts-batch4`, base `d977435a`. References (read-only, local): mangos-classic `src/game/AI/ScriptDevAI/scripts/` at `8ec338a170`
(`<refs-root>\mangos-classic`), data from ClassicDB `ClassicDB_1_12_1_z2815` (`<refs-root>\classic-db`), spell ids checked against the 5875 client
`Spell.dbc`. Batches 1-3 (Lazy Peon, Galen, Miran, Keeshan, Kinelory, Defias Traitor, Yenniku, Kanati, OOX-09/HL, Foreman Klaven Mortwake,
Prospector Remtravel, the Barrens stealth-alert speakers) have no note of their own; this one follows their structure: creature scripts are
`CreatureAI` classes in `src/ArcaneCore.Game/Creatures/Scripts/`, registered by creature entry in `CreatureAiFactory` (the entry-script table
and, for an event quest, `_entryScriptQuests`), and object scripts are `IGameObjectAi` classes in `src/ArcaneCore.World/GameObjects/`
registered by `WorldGameObjectScriptsFeature`.

Seven scripts, each checked to have its ScriptName bound in z2815 (`creature_template.ScriptName` / `gameobject_template.ScriptName`), its
creature or object rows, its quests, items and spells in the data we import.

## Ported

| Id | Script (ScriptName) | Reference | Port |
|---|---|---|---|
| Z1 | `npc_calvin_montague` (creature 6784, quest 590) | tirisfal_glades.cpp:89-168 (AI), :170-179 (QuestAccept), :193-197 (register) | `CalvinMontagueAI`. Friendly, immune to players. Accepting 590 makes him hostile (faction 168, temporary: restored at combat stop and respawn) and he attacks. Under 15% he gives up: fight stops, faction back, passive; 5 s later line `-1000356`; 7.5 s later event credit for 590 and he drinks (2639, triggered); 7.5 s later he evades, which resets him. |
| Z2 | `npc_taskmaster_fizzule` (creature 7233) | the_barrens.cpp:144-272, :1105-1109 | `TaskmasterFizzuleAI`. A player's Flare (10113) once: immune to players, 120 s; twice: faction 35 until he is home; a Salute (text emote 78) at count 2: quest giver flag and a one-shot salute (66). Goblin Folly (10137) does all of it. After 120 s: flag and immunity off, count 0, evade home. While calmed his evade only drops auras and the fight. |
| Z3 | `mob_aquementas` (creature 9453) | tanaris.cpp:37-133, :411-414 | `AquementasAI`. Friendly (35) at every reset, elemental (91) after 10 s. Aggro yells `-1000168` and gives the pulling player (or the owner of the pet) the Silver Totem (11522) when they hold the Book of Aquor (11169), 11 Silvery Claws (11172) and the Irontree Heart (11173) and no totem. Frost Shock (15089) on the victim 1 s in, then every 15 s; Aqua Jet (13586) on himself 5 s in, then every 15 s. |
| Z4 | `npc_deathstalker_faerleia` (creature 2058, quest 452) | silverpine_forest.cpp:133-306, :315-319 | `DeathstalkerFaerleiaAI`. Accepting 452: line `-1000553`, quest giver flag off, 10 s, then four waves (Smithers / Thatcher + Hendricks / Wilhelm + Hartin + Higarth / Cooper + Brunswick + Lord Mayor Morrison), each only once the last is dead, corpse-timed 20 s summons that walk to a random point within 10 yd of (-410.69, 1498.04, 19.77). Each dead wave: she drinks (3359). After the last: line `-1000554` and group event credit. Her death sends the player SMSG_QUESTGIVER_QUEST_FAILED. |
| Z5 | `go_rune_of_the_defiler` (GO 153204, trap) | blasted_lands.cpp:32-44, :58-60 | `RuneOfTheDefilerAi`: the trap takes only a player holding a Ward of the Defiler (10757), through the existing `IGameObjectAi.AcceptsTrapTarget` seam. The trap's own spell 12885 is the engine's. |
| Z6 | `go_mausoleum_door` (GO 176594) | tirisfal_glades.cpp:47-60, :183-186 | `MausoleumDoorAi`: quest 1819 incomplete and a Mausoleum Trigger (104593) within 30 yd of the player: the trigger goes back to ready and Ulag the Cleaver (6390) is summoned at (2390.26, 336.47, 40.01) for 300 s (`TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN`). Never handles the use, so the door opens as usual. |
| Z7 | `go_mausoleum_trigger` (GO 104593) | tirisfal_glades.cpp:62-75, :188-191 | `MausoleumTriggerAi`: quest 1819 incomplete and the Doors (176594) within 30 yd: the trigger becomes Active and the door loses `GO_FLAG_INTERACT_COND`; the use is handled. Otherwise left to the object. |

Quest status for Z6/Z7 is `QuestNpcFeature.Services.StateOf(player)?.Quests.GetStatus(...)`, as `go_andorhal_tower` does. Z6/Z7 are registered
on the two continents like the other `go_scripts` (both objects stand on map 0).

### Documented deviations

- **Calvin's 15% rule is the combat engine's invincibility threshold** (as Bartleby and Dashel), set from the start to the highest health strictly under 15% (`ceil(15% of max) - 1`), so a hit landing exactly on 15% does not trigger, as `(health - damage) * 100 / max < 15` (:111) does not. A hit that would pass it leaves him on that health rather than on 1 hp. The credit goes to his victim at that moment; the source credits the dealer of the clamped hit. The retail rule is not unreachable: `MapCombat.DamageTaken` (Combat/MapCombat.DamageEvents.cs) carries the attacker. It is a map-wide event that instance scripts subscribe to for the life of the instance, and creature AIs have no removal hook to unsubscribe from, so a per-creature subscription would leak on every respawn or grid reload. A per-creature DamageTaken seam on `CreatureAI` is the clean way to close this. A non-player victim gives no credit, as in the source. `ZoneScriptBatch4Tests` has the hit come from a second player so the test pins the victim rule.
- **Real data makes the mausoleum flag clear a no-op.** On z2815 the Doors (176594) carry no `GO_FLAG_INTERACT_COND` and the trigger itself carries flags 4, so clearing the flag on the door changes nothing; this matches the source.
- **Fizzule's calmed evade** uses `CreatureMapSystem.ResetAurasOnEvade` (the `ICreatureAuraReset.ResetAuras` an engine evade runs), so a player's non-permanent buff survives, like `RemoveAllAurasOnEvade`.

## Skipped, and why

| Script | Reason |
|---|---|
| `go_marshal_haggards_chest` (elwynn_forest.cpp) | Not in z2815: no `gameobject_template` row carries the ScriptName, so nothing in our data would run it (same as `go_bubbly_fissure` in the scourge note). |
| `npc_corrupt_saber` + `npc_kitten` (felwood.cpp) | A pair: Winna's kitten is a `FollowerAI` whose entry is rewritten by spell script 16510 (`CorruptedSaberVisual`: SetEntry, display, name, scale on the caster). No spell effect here rewrites a creature's identity in place; the roadmap already lists the Felwood kitten (quest 4506) as open DB-side work. |
| `npc_aged_dying_ancient_kodo` (desolace.cpp) | Needs the 18153 spell script (`UpdateEntry` to the tamed kodo, 18172/18377 buffs, follow), `TalkedToCreature` objective credit and gossip menu forwarding together. |
| `mob_colonel_kurzen` (stranglethorn_vale.cpp) | `SetCombatOnlyStealth`: no combat-only stealth exists in the creature system. |
| `npc_kindal_moonweaver` (feralas.cpp) | Reads `TYPE_FREEDOM_CREATURES` from the Feralas map script (`ScriptedMap` data, set by other Feralas scripts); there is no per-map script data for it. |
| `npc_artorius`, `npc_klinfran` (winterspring.cpp, burning_steppes.cpp) | Transform (`UpdateEntry`) with `NearTeleportTo`, saved respawn times and a cleaner summon that inherits the threat list; larger than the others and left for a batch of their own. |
| Raid, instance and Scourge Invasion scripts in the same unported list | Not zone scripts; covered by their own lanes. |

## Tests

- `ZoneScriptBatch4Tests` (Game, 29): registration of the four entries, the two event quests listed as scripted, Calvin (start state, accept,
  the whole give-up / line / credit + drink / reset sequence, step timers), Fizzule (flare steps, non-player casts ignored, salute at count 2
  only, Folly, 120 s expiry, calmed evade), Aquementas (faction switch at 10 s, yell and totem, no totem with ten claws / no book / no heart /
  totem held, Frost Shock and Aqua Jet timers), Faerleia (accept, four waves each only after the last died, the final credit and line,
  summons walking toward the meeting place, death sends the failed packet, death without an event sends nothing).
- `ZoneGameObjectScriptTests` (World, 8): the rune with and without a Ward, the door with quest 1819 (trigger reset, Ulag at the spawn point,
  the use not handled), without the quest, and with no trigger within 30 yd; the trigger with the quest (Active, only `InteractCond` cleared from
  the door, use handled), without it, and with no door within 30 yd; the feature registering all three.

## Verification

- Build: `dotnet build ArcaneCore.slnx -m:2`, 0 warnings, 0 errors.
- RED: with three behaviours reverted in the scripts (Calvin's `QuestEventHappened` removed, Aquementas never switching to faction 91, the
  trigger not clearing the door flag) exactly the three tests that pin them failed: `Calvin_BeatenUnder15Percent_GivesUpSpeaksCreditsDrinksAndResets`
  (expected the event credit `(player, 590)`, got none), `Aquementas_IsFriendlyAtFirstAndAnElementalTenSecondsLater` (expected faction 91, got 35),
  `TheTriggerWithQuest1819IncompletePressesItselfAndUnlocksTheDoor` (expected `InteractCond` cleared, still set); all other new tests passed. The scripts
  were then restored and all 37 new tests pass.
