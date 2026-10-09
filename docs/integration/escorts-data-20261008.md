# Data-driven ScriptDev2 escorts (2026-10-08)

This lane adds eight source-checked escorts to the embedded `validated.json` catalog. The catalog dispatches quest acceptance, faction and NPC-immunity changes, waypoint text, summons, ambusher attacks, run changes and group event credit through `DataDrivenEscortAI`. `CreatureAiFactory` registers each entry before the normal AI selection. Existing Ruul Snowhoof remains hand ported.

All behavior below was reimplemented from facts in the read-only mangos-classic checkout (`8ec338a`); no third-party code was copied. Faction constants were checked against `src/game/AI/ScriptDevAI/ScriptDevAIMgr.h:42-56`. The path fixture is the 235 original `script_waypoint` rows for these eight entries from `ClassicDB_1_12_1_z2815.sql.gz`. The tests feed those rows to the actual escort path loader, follow each escort on a manual world clock, check waypoint speech, summon positions and quest credit, and check loss-of-player quest failure.

| Entry / quest | Reference functions | Verified action points | Checked path rows |
|---|---|---|---:|
| Deathstalker Erland 1978 / 435 | `eastern_kingdoms/silverpine_forest.cpp:59` `npc_deathstalker_erlandAI`, `:115` `QuestAccept_npc_deathstalker_erland` | 1, 14, 16, 17, 25, 27 text; 14 credit; 15/26 nearby NPC speech from Rane/Quinn; three random aggro lines | 27 |
| Professor Phizzlethorpe 2768 / 665 | `eastern_kingdoms/arathi_highlands.cpp:56` `npc_professor_phizzlethorpeAI`, `:103` `QuestAccept_npc_professor_phizzlethorpe` | 5, 6, 9, 11, 12, 20, 21 speech/emote text; 10 two attacking Vengeful Surges; 12 run; 21 credit; aggro line | 21 |
| Dalinda Malem 5644 / 1440 | `kalimdor/desolace.cpp:188` `npc_dalinda_malemAI`, `:218` `QuestAccept_npc_dalinda_malem` | 18 credit; initial kneel, stand at start | 19 |
| Gilthares Firebough 3465 / 898 | `kalimdor/the_barrens.cpp:59` `npc_giltharesAI`, `:128` `QuestAccept_npc_gilthares` | 17, 18, 19, 38, 48 text; 54 text and credit; area-gated aggro text and home stand | 54 |
| Therylune 3584 / 945 | `kalimdor/darkshore.cpp:493` `npc_theryluneAI`, `:528` `QuestAccept_npc_therylune` | 18 credit; 20 text and run | 21 |
| Paoka Swiftmountain 10427 / 4770 | `kalimdor/thousand_needles.cpp:221` `npc_paoka_swiftmountainAI`, `:263` `QuestAccept_npc_paoka_swiftmountain` | 16 text and three wyverns; 27 text; 28 credit | 28 |
| Lakota Windsong 10646 / 4904 | `kalimdor/thousand_needles.cpp:134` `npc_lakota_windsongAI`, `:187` `QuestAccept_npc_lakota_windsong` | 9, 15, 22 text and two attacking bandits each; 46 text and credit | 46 |
| Kaya Flathoof 11856 / 6523 | `kalimdor/stonetalon_mountains.cpp:48` `npc_kayaAI`, `:89` `QuestAccept_npc_kaya` | 17 text and three attacking Grimtotems; 19 text and credit | 19 |

## Corrections to the extracted specs

The original machine JSON remains outside this worktree and was treated as a proposal. Each field admitted to `validated.json` was checked against the listed C++ functions, `ScriptDevAIMgr.h`, and the ClassicDB path. The six selected JSON `faction_id` numbers were already corrected by the extraction job, so none required a numeric change; Erland does not change faction, and Phizzlethorpe uses the explicit faction 120. Its **notes** still contradicted those fields: Dalinda says faction 250 instead of 10; Gilthares says 775 instead of 232; Therylune says 774 instead of 231; Paoka says 250 instead of 232; Lakota says 0 instead of 232; Kaya says 250 instead of 775. The curated data uses the verified numbers and drops the stale notes.

The extracted actions also left execution details implicit. Erland's Rane and Quinn lines use those nearby creatures as speakers, and all his waypoint actions require the escort player still to exist. The source defines `SAY_PROGRESS` but never uses it, so it is absent from the curated spec. The curated spec adds `clearImmuneToNpc` for Dalinda, Gilthares, Therylune and Kaya (`Creature::SetFactionTemporary`, `src/game/Entities/Creature.cpp:2653`); Therylune's start-before-faction order and forced immunity on respawn; Gilthares's player-addressed speech, area 391/25% aggro, stand-up on quest accept and stand state on reaching home; Lakota's attacking summons; Kaya's corpse-timed summon mode; and Phizzlethorpe's out-of-combat-or-corpse summon mode and instant-respawn escort start. Paoka's wyverns are deliberately **not** directed to attack her, matching `npc_paoka_swiftmountainAI::DoSpawnWyvern`. `UNIT_STAND_STATE_KNEEL=8` and `STAND=0` were checked against the source enum and ArcaneCore's `StandState`.

## Intake review corrections

The intake re-read all eight functions against mangos-classic `8ec338a` and found four behaviours the curated specs still lost:

- **Gilthares never spoke without his player.** `npc_giltharesAI::WaypointReached` returns when `GetPlayerForEscort()` is null, like Erland and Phizzlethorpe, but his spec lacked `requirePlayerAtWaypoint`.
- **Player-addressed lines were said to nobody.** Every `toPlayer` line in these scripts is either under `if (Player* pPlayer = GetPlayerForEscort())` (Therylune point 20) or after the early return. Without the player, the line is now skipped, and the non-text actions on the same point still run (Therylune still calls `SetRun`).
- **Aggro text targets.** Erland and Gilthares call `DoScriptText(..., m_creature, pWho)`. Phizzlethorpe calls `DoScriptText(SAY_AGGRO, m_creature)` with no target. The aggro spec now has `toTarget`, and Phizzlethorpe no longer names the aggressor.
- **Paoka and Lakota are defensive.** Both constructors call `SetReactState(REACT_DEFENSIVE)` (`thousand_needles.cpp:138`, `:225`). cmangos keeps react state on the AI across respawns. This server re-reads it from the template at respawn, so `reactState` is applied at spawn and again at every respawn.

The loader now rejects unknown JSON members, so a misspelt field fails the load instead of silently reading as false. It also rejects undefined react states. The two new summon timers cite `TemporarySpawn.cpp` (`TIMED_OOC_OR_CORPSE` at lines 107-127; `CORPSE_TIMED` at lines 67-83 and 281-282) and share the attack-on-summon code with `SummonAt`. One limit carries over from existing code: `TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN` (Paoka's and Lakota's summons) keeps a dead summon until normal corpse decay, matching `TemporarySpawn.cpp:129-149`.

## Second review corrections

A second review re-read the cited C++ and found three more departures, now fixed:

- **An out-of-combat-or-corpse summon goes at death.** `TEMPSPAWN_TIMED_OOC_OR_CORPSE_DESPAWN` (`TemporarySpawn.cpp:107-127`) opens with `if (IsDead()) { UnSummon(); return; }`, and `Unit::IsDead` (`Unit.h:1817`) is true for `CORPSE` as well as `DEAD`. The first version kept counting the lifetime down on the corpse. Phizzlethorpe's Vengeful Surges (2776, 600000 ms) are in combat until they die, so each corpse stayed lootable for up to ten minutes. `SummonTimer.OutOfCombatOrCorpse` now despawns the summon on the first update after its death. The test asserts that the surge is gone 100 ms after the kill, with half of its lifetime still left.
- **Only a living Rane or Quinn answers Erland.** `GetClosestCreatureWithEntry` defaults to `onlyAlive = true` (`sc_grid_searchers.h:37`; `silverpine_forest.cpp:80`, `:93`), but `CreaturesOfEntryInRange` also returns corpses. `say_nearby` now takes the nearest living speaker. A test kills both speakers just before Erland reaches them. It checks that their corpses are still in range on arrival and that neither line is spoken.
- **Quest-accept order.** `QuestAccept_npc_gilthares` stands him up itself (`the_barrens.cpp:133`, before `Start`), so a refused `Start` still stands him up. The spec now uses `acceptStandState` instead of the `JustStartedEscort` `startStandState`, which is still Dalinda's (`desolace.cpp:198-201`). Lakota and Paoka say their start text before `SetFactionTemporary` (`thousand_needles.cpp:191-192`, `:267-268`), and `startTextBeforeFaction` restores that order. The loader refuses `startTextBeforeFaction` without a start text or together with `factionAfterStart`. The text-then-faction order is not visible to a client within one update. It is kept only so that the code matches the source.

## Limits and remaining work

The index has 57 extracted specs. Eight are newly ported here; Ruul Snowhoof (12818) was already hand ported. The other 48 are not ported by this lane. All 49 remaining index entries (including Ruul) carry at least one `non_data_items` flag, but that automated classification has not been source checked for those entries and is not a count of required handwritten implementations. In particular, Corporal Keeshan's code credits at point 69 while z2815 provides only points 1-54; it needs a matching path before it can pass the requested real-row test.

The eight scripts' 46 negative text IDs and six ambusher creature templates are present in z2815. A world still needs its normal ClassicDB content refresh for those rows and the `script_waypoint` paths. The tests synthesize text/template content and use the actual extracted path rows; they are loopback tests, not a real 1.12.1 client run. No schema change is needed.
