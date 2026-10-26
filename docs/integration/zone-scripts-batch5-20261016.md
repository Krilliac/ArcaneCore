# Zone scripts, batch 5 — 2026-10-16

Lane `zone-scripts-batch5`, base `f5b8bb4e`. References (read-only, local): mangos-classic `src/game/AI/ScriptDevAI/scripts/` at `8ec338a170`
(`<refs-root>\mangos-classic`), data from ClassicDB `ClassicDB_1_12_1_z2815` at `ec4f596146` (`<refs-root>\classic-db`), spell ids checked against the 5875
client `Spell.dbc`. Structure as in [batch 4](zone-scripts-batch4-20261016.md): creature scripts are `CreatureAI` classes in
`src/ArcaneCore.Game/Creatures/Scripts/`, registered by creature entry in `CreatureAiFactory` (the entry-script table and, for an event quest,
`_entryScriptQuests`), and object scripts are `IGameObjectAi` classes in `src/ArcaneCore.World/GameObjects/` registered by
`WorldGameObjectScriptsFeature`.

Six scripts, each checked to have its ScriptName bound in z2815 (`creature_template.ScriptName` / `gameobject_template.ScriptName`), its creature
or object rows, its quests, items and spells in the data we import. Four are creature scripts (eight creature entries), two are object scripts.

## Ported

| Id | Script (ScriptName) | Reference | Port |
|---|---|---|---|
| Z1 | `mobs_spitelashes` (creatures 6190 warrior, 6193 screamer, 6194 serpent guard, 6195 siren, 6196 myrmidon; quest 9364) | azshara.cpp:37-192 (AI), :503-506 (register) | `SpitelashAI`, one class and an ability table per entry. Warrior: Disarm (6713) on the victim, first at 4 s then every 10 s. Screamer: Screech (3589) on herself, 7 s / 15 s. Serpent guard: Frost Shock (12548) on the victim, 7 s / 13 s. Siren: Renew (11640) on the friendly unit with the lowest health, 4 s / 7 s; Shoot (6660) on a random threat-list unit, 3 s / 9 s; Frost Shot (12551) on a random one, 7 s / 10 s; Frost Nova (11831) on herself, 10 s / 15 s. Myrmidon: Strike (11976) on the victim, 3 s / 7 s. A timer fires on the update after it ran out (`timer < diff`), the next timer in the table is not reached in the same update (the source breaks after one cast), and a cast the caster refuses leaves the timer expired. A player with quest 9364 (Fragmented Magic) incomplete who hits one with Polymorph (118 and ranks 12824-12826) starts a 5 s countdown that runs only in combat; at zero the naga casts Polymorph Backfire (28406, triggered, on itself) and is force-despawned. A second hit does not restart it. Evade restores every timer. |
| Z2 | `npc_rabid_bear` (creature 2164, quest 2118) | darkshore.cpp:544-655, :920-923 | `RabidBearAI`. Rabies (3150) on the top-threat unit every 12-18 s. A player's Bear Captured in Trap (9439) ends the fight (auras reset, combat stopped, timers reset), turns the bear into entry 11836 (`UpdateEntry`, the AI stays) and, with quest 2118 incomplete, gives the player the kill credit of 11836 and makes the bear follow them (`Motion.MoveFollow`); without the quest it is force-despawned after 1 s. Either way it leaves after 3 minutes out of combat, and a captured bear that sees Tharnariun Treetender (3701) within 10 yd (flat distance) is force-despawned after 1 s and stops. A respawn clears the capture and the despawn timer. |
| Z3 | `npc_feero_ironhand` (creature 4484, quest 976) | ashenvale.cpp:591-767, :825-829 | `FeeroIronhandAI`, an `EscortAI` on the 33 `script_waypoint` rows of z2815. Accepting 976: line `-1000771`, temporary faction 10 (restored at respawn), `ImmuneToNpc` cleared, start at a run. Point 15: line `-1000772` and four Dark Strand Assassins (3879) 30 yd away at angles 1.25, 1.25-pi/4, ... Point 21: line `-1000774` and three Forsaken Scouts (3893) at angles 0.95, 0.95-pi/3, ...; the second scout to arrive yells `-1000775`. Point 30: line `-1000777` and Balizar (3899, yells `-1000778`), Aligar (3898), Caedakar (3900) at their fixed spots. Every summon attacks him as it appears and is timed out-of-combat 20 s. The last of a wave to die makes him speak (`-1000773`, `-1000776`, `-1000780`); the counter is a byte as in the source. He yells `-1000779` once, when Balizar first hits him. Point 31: group event credit for 976. The counters reset on evade only while he is not escorting. A respawn gives him `ImmuneToNpc` back. |
| Z4 | `npc_morokk` (creature 4500, quest 1173) | dustwallow_marsh.cpp:41-163, :1227-1231 | `MorokkAI`, an `EscortAI` on the 8 `script_waypoint` rows. Accepting 1173 starts him at a run. At point 1 he pauses; paused and out of combat he challenges a living escort player within 70 yd (3D, plus both combat reaches): line `-1000499`, immunity to players off, aggressive, faction 168, attack. Paused with no player in range he walks on. At 30% health while paused: group event credit for 1173, faction 35, fight over, immune to players, passive, line `-1000500`, motion cleared, unpaused. Respawn sets faction 29. Reaching home while escorting without having lost fails the quest for the group and despawns him. |
| Z5 | `go_lightning` (GO 183356) | azshara.cpp:368-397, :518-521 | `LightningAi`: every 2-5 whole seconds (random, per object) the object plays custom animation 0 (`SMSG_GAMEOBJECT_CUSTOM_ANIM`, through `GameObjectMapSystem.SendCustomAnim`). The timer starts at the first update and is held while the object is not spawned. |
| Z6 | `go_covert_ops` (GO 19590, Venture Co. Wagon Trap) | stonetalon_mountains.cpp:104-213, :236-239 | `CovertOpsAi`. When the trap is sprung it collects every Deforester (3991), Logger (3989), Digger (3999), Overlord (4004) and Operator (3988) within 100 yd. 2 s later each one that is alive and out of combat stops and runs to the point 10 yd (plus both bounding radii) from the wagon on its side; 45 s after that each one still alive and out of combat walks again and evades, which sends it home. A respawn of the object starts the event over. |

Quest status for Z1/Z2 is a new seam, `IScriptQuestEvents.IsQuestIncomplete` (a default interface member returning false), implemented in
`ScriptQuestEvents` as `QuestNpcServices.StateOf(player)?.Quests.GetStatus(...) == QuestStatus.Incomplete`, and reached by scripts through
`CreatureMapSystem.QuestIncomplete`. Without the quest feature it is false.

### Documented deviations

- **Spitelash timers are walked in table order.** The source keeps them in an `unordered_map`, whose order is unspecified; the table order is the order the source lists them in. The random target is chosen among the living threat-list units in line of sight (the source's `SelectAttackingTarget(RANDOM)` plus `IsWithinLOSInMap`), and a cast at a target out of the spell's range is refused, which leaves the timer expired as any refused cast does.
- **Rabid bear: the Treetender despawn is requested once.** The source calls `ForcedDespawn(1000)` from `MoveInLineOfSight`, which cmangos only calls on movement events and whose `ForcedDespawn` queues an extra event, so the first fires after a second. Here the engine reports a creature in sight on every update and `CreatureMapSystem.ForcedDespawn` restarts its delay on each call, so the bear would never leave. The script remembers that it asked (cleared at respawn). Found by a test: `TheTreetenderBeingSeenOnEveryUpdateDoesNotPostponeTheDespawn`.
- **Feero: the ambush fan points are the ground height under the point** (`CreatureMapSystem.GroundZ`, made public for this), and `GetNearPoint`'s collision search, which can move the point, is not ported. His `AttackedBy` override has no base reaction, as in the source: a retaliation comes only from the threat the attackers put on him.
- **`TEMPFACTION_TOGGLE_IMMUNE_TO_NPC`** is not applied by the engine (`SetFactionTemporary` takes only `RestoreRespawn`), so Feero clears `ImmuneToNpc` by hand at the quest start and gets it back from the template in `JustRespawned`.
- **`CombatStop(true)` for Morokk** is `StopCombatInPlace` plus `ResetAurasOnEvade`, the same pair the other scripts use.
- **`go_covert_ops` polls instead of `OnLootStateChange`.** No such hook exists; the object's update sees `LootState == JustDeactivated` while it is still spawned (the update runs before the despawn the same state causes) and acts once per spawn. The contact point's height is the ground under it, without `GetNearPoint`'s collision search. A worker found by an earlier springing is kept, as in the source, but listed once.
- **Rabid bear: the Rabies target is the top-threat living unit, unvalidated.** The source (darkshore.cpp:640-641) calls `SelectAttackingTarget(ATTACKING_TOPAGGRO, 0, SPELL_RABIES)`, which skips threat entries that fail `MeetsSelectAttackingRequirement` (immunity to the spell, line of sight, spell range; Unit.cpp:12086-12201) and casts nothing when none pass. The engine has no spell-target-validity check to reuse for that, so the bear casts at the first living threat entry and the cast itself refuses an invalid target (the timer is rerolled either way, as in the source).
- **Morokk: `Motion.Clear()` is `Clear(true)`.** The source (dustwallow_marsh.cpp:137) calls `GetMotionMaster()->Clear(false)` (keep the movement generators' reset-to-idle behaviour off); our `Motion.Clear()` has no such parameter and behaves like `Clear(true)`, which also resets to the default idle generator.
- **`GroundZ` is now `public`** (it was `internal`) because the World assembly needs it for the wagon.

## Skipped, and why

| Script | Reason |
|---|---|
| `npc_furbolg` group, `go_furbolg_food` / `event_purify_food` (Azshara/Winterspring furbolgs, felwood.cpp and the event script) | The furbolg AI is driven by the food object and a script event; the pair needs an event-script bridge from the object to the creature group. |
| `go_flagongut_fossil` | Not in z2815: no `gameobject_template` row carries the ScriptName. |
| `npc_felhound_tracker` (Felwood) | A pet AI (`CreatureAI` of a controlled creature); pet AIs have no script seam. |
| Maws, `event_arcanite_buoy` (azshara.cpp) | Event script plus a conditional summon from a spell target; the event-script id is not reachable from a game object here. |
| `go_cage_door`, `go_omen_cluster` | Read and write per-map script data (`ScriptedMap`), for which there is no per-map script data. |
| `npc_ogron` (Dustwallow Marsh) | A long multi-stage escort with summoned waves, deferred as large; a batch of its own. |
| `npc_arei`, `npc_tooga`, `npc_wizzlecranks_shredder`, `npc_twiggy_flathead`, `npc_gizelton_caravan`, `npc_arko_narin` | Each needs a seam still missing (a multi-creature caravan, a vehicle-like follow, a DB path, an entry change with respawn data); the earlier batches' reasons apply. |
| The standing items of [batch 4](zone-scripts-batch4-20261016.md) | Unchanged: no seam appeared for them (`SetCombatOnlyStealth`, `ScriptedMap` data, the spell-script entry rewrites). |

## Tests

- `ZoneScriptBatch5Tests` (Game, 62): registration of the eight entries and the two event quests listed as scripted; Spitelash (each entry's
  ability, first timer, cooldown and target, no timers before the fight, a refused cast retried each update, evade restoring the timers, the siren's
  four abilities with Renew only on a wounded friend, random targets among the living fighters, all four Polymorph ranks, ignored for no quest / a
  creature / another spell, the 5 s countdown only in combat, a second hit not restarting it, the backfire cast and despawn, a refused backfire
  retried); the bear (Rabies 12-18 s, capture with and without quest 2118, non-player and other spells ignored, 3 minutes, the countdown held in
  combat, the Treetender at 9 yd, beyond 10 yd, flat distance, not postponed by repeated sightings, respawn reset); Feero (accept and ignored quests,
  the walk, respawn restoring immunity, the three ambushes' counts, positions and lines, 20 s timed despawn, last-of-wave lines, the yell once and only
  for Balizar, no base reaction, credit at point 31 with and without a player, the counter reset only when not escorting, the byte wrap); Morokk
  (accept, the point-1 pause and challenge, beyond 70 yd and the reach boundary, 30% with its group credit, no-op when not paused, the walk on,
  both `OnReachedHome` branches, respawn faction, dead player). The two escorts walk the real z2815 rows.
- `ZoneGameObjectScriptBatch5Tests` (World, 15): the lightning timer range, the animation packet (guid, animation 0), held while unspawned, one timer
  per object; the wagon (inactive until sprung, the 2 s / 45 s boundaries, contact point reached, skips for range / dead / fighting / other entries,
  one reaction per spawn, restart on respawn, unspawned wagon ignored); the feature registering both; the lightning driven by the map update;
  `ScriptQuestEvents.IsQuestIncomplete` false without the quest feature.

## Verification

- Build: `dotnet build ArcaneCore.slnx -m:2`, 0 warnings, 0 errors. Filtered tests `ZoneScript|ZoneGameObjectScript|CreatureAiFactory|Docs`: Game 122, World 121, all passing.
- RED: with five behaviours reverted (the bear's once-only Treetender despawn, Morokk's 30% to 40%, the first ambush fan step pi/4 to pi/3, the wagon's
  45 s return to 30 s, the Spitelash countdown 5000 to 4000) the pinning tests failed: `TheTreetenderBeingSeenOnEveryUpdateDoesNotPostponeTheDespawn`
  with the two Treetender tests that run the map, `Morokk_BeatenTo30PercentHeGivesTheGroupCreditAndRunsAway`,
  `Feero_TheFirstAmbushIsFourAssassinsInAFanThirtyYardsAway_AllAttackingHim`, `ASprungWagonCallsEveryAliveWorkerAfterTwoSeconds_AndSendsThemHomeFortyFiveSecondsLater`
  and the five countdown tests. One more test, `TheBearCastsRabiesOnItsTopThreatEveryTwelveToEighteenSeconds`, failed in that run and I could not tie it
  to any of the five changes; it passed in the unreverted tree in nine consecutive runs.
