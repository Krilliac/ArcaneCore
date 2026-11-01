# World and zone script coverage recount by creature entry (2026-10-31)

ROADMAP item 9. This recount keys on creature entries, not script names: the cmangos ScriptDevAI registrations are mapped to the creature entries whose `creature_template.ScriptName` names them, and each entry is checked against what ArcaneCore registers. Clean-room: this document says which scripts exist and what kind of hooks they have; no reference code is reproduced. Behaviour for the missing entries is to be specified by a spec agent in later lanes.

## Sources and method

- Reference registrations: `src/game/AI/ScriptDevAI/scripts` in the cmangos mangos-classic checkout (cmangos classic; the brief names `src/scriptdev2`, which does not exist in this checkout, ScriptDevAI is its current location). Each `pNewScript->Name = "..."` block is one registration; its handler assignments (`GetAI`, `pGossipHello`, `pQuestAcceptNPC` ...) are recorded.
- Script name to entry: `creature_template.ScriptName` in `Full_DB/ClassicDB_1_12_1_z2815.sql.gz` from the classic-db checkout (the cmangos classic world database). Spawn counts are `creature` rows with that `id` in the same dump. A script name with no creature_template row (game object, item, area trigger, spell, event, map/instance and example scripts) is not a creature script and is excluded.
- ArcaneCore side (static scan; this lane runs no build or server): an entry counts as registered when (a) its numeric id appears on an entry-like line (Entry, Npc, Boss, Creature, Mob, Guard, Register, `const uint`, an indexer) in a C# file under `Creatures/Scripts`, `Creatures/AI`, `Instances/Scripts`, `Battlegrounds`, `Scripting` or `OutdoorPvP` of `src/ArcaneCore.Game` (lines about spells, quests, items, sounds and texts are skipped, and so are the generic engines `CreatureAI.cs`, `SmartScript.cs`, `EventAiEngine.cs`, `ArathiBasin.cs`, `SpellSystemCreatureCaster.cs`), (b) it is in `src/ArcaneCore.Game/Creatures/Scripts/Escorts/validated.json` (data-driven escorts, registered by the `CreatureAiFactory` constructor in `src/ArcaneCore.Game/Creatures/AI/CreatureAiServices.cs` through `EscortSpecCatalog`), or (c) the cmangos script name appears as a string literal in those directories (the guard names of `_factories` in `CreatureAiServices.cs`), or (d) a hand-checked mechanism outside those directories, listed here: `npc_war_effort` (the 33 war-effort NPCs have no script of their own; counter turn-ins run in the quest reward transaction, `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs`, `docs/integration/aq-war-effort-phases-20261010.md`), `npc_spirit_guide` (`src/ArcaneCore.Game/Battlegrounds/SpiritGuideService.cs`), `npc_silithus_boss` (Colossus AI in `src/ArcaneCore.World/WorldState/WarEffortFeature.cs`). The professions masters of `world/npc_professions.cpp` are found by (a) in `src/ArcaneCore.Game/Crafting/ProfessionSpecializationGossip.cs`.
- Caveats. The scan is an estimate in both directions: an id on an entry-like line for another purpose (a summon, a target filter) counts as registered although no AI is bound, and an entry registered through data other than the escort JSON (a DB `creature_ai_scripts` or `smart_scripts` row, a hot-loaded script) is reported missing. Read "registered" as an upper bound for C# registrations and "missing" as the candidate list that a spec agent confirms against `ScriptCatalog.Resolve` (`src/ArcaneCore.Game/Creatures/AI/ScriptCatalog.cs`). The ArcaneCore world database uses vmangos ScriptNames, which differ from cmangos names; that is why the join is by entry.
- Reproduce: a short script over the inputs above (parse the `creature_template` INSERTs for `Entry` and `ScriptName`, the `creature` INSERTs for spawn counts, regex the registrations). It is not committed because its inputs are reference checkouts outside the repo.

## Counts

- cmangos script registrations found in the C++ sources: 353.
- Creature scripts among them (the name is some creature_template.ScriptName): 237; the other 116 are game object, item, area trigger, spell, event, map and example scripts.
- Creature entries bound to those scripts: 328; registered in ArcaneCore: 324; missing: 4.
- Missing entries with at least one classic spawn: 1; with no spawn row: 3.

| Group | Entries | Registered | Missing |
|---|---|---|---|
| zone | 104 | 104 | 0 |
| world | 111 | 108 | 3 |
| battleground | 2 | 2 | 0 |
| instance | 111 | 110 | 1 |
| total | 328 | 324 | 4 |

Group comes from the reference file: a file directly under `eastern_kingdoms/` or `kalimdor/` is a zone script, a file in a subdirectory (a dungeon or raid) is instance, and `world/` and `battlegrounds/` are their own groups.

## Coverage by reference file

| Reference file (under `scripts/`) | Entries | Registered | Missing |
|---|---|---|---|
| `world/npcs_special.cpp` | 20 | 17 | 3 |
| `kalimdor/temple_of_ahnqiraj/boss_ouro.cpp` | 5 | 4 | 1 |
| `battlegrounds/battlegrounds.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/arathi_highlands.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/blackrock_depths/blackrock_depths.cpp` | 11 | 11 | 0 |
| `eastern_kingdoms/blackrock_depths/boss_ambassador_flamelash.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_depths/boss_emperor_dagran_thaurissan.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/blackrock_depths/boss_general_angerforge.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_depths/boss_high_interrogator_gerstahn.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_spire/boss_gyth.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_spire/boss_overlord_wyrmthalak.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_spire/boss_pyroguard_emberseer.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackrock_spire/instance_blackrock_spire.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_broodlord_lashlayer.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_chromaggus.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_ebonroc.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_firemaw.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_flamegor.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_nefarian.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_razorgore.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_vaelastrasz.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/blackwing_lair/boss_victor_nefarius.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/boss_kazzak.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/burning_steppes.cpp` | 3 | 3 | 0 |
| `eastern_kingdoms/deadmines/boss_mr_smite.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/eastern_plaguelands.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/gnomeregan/boss_thermaplugg.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/gnomeregan/gnomeregan.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/hinterlands.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/loch_modan.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_baron_geddon.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_garr.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/molten_core/boss_gehennas.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_golemagg.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/molten_core/boss_lucifron.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_magmadar.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_majordomo_executus.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_ragnaros.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_shazzrah.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/molten_core/boss_sulfuron_harbinger.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/redridge_mountains.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/scarlet_monastery/boss_arcanist_doan.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/scarlet_monastery/boss_herod.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/scarlet_monastery/boss_mograine_and_whitemane.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/scholomance/boss_darkmaster_gandling.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/scholomance/boss_jandice_barov.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/scholomance/scholomance.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/searing_gorge.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/silverpine_forest.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/stormwind_city.cpp` | 7 | 7 | 0 |
| `eastern_kingdoms/stranglethorn_vale.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/stratholme/boss_baroness_anastari.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/stratholme/boss_cannon_master_willey.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/stratholme/boss_dathrohan_balnazzar.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/stratholme/boss_maleki_the_pallid.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/stratholme/stratholmeScripts.cpp` | 4 | 4 | 0 |
| `eastern_kingdoms/sunken_temple/sunken_templeScripts.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/tirisfal_glades.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/uldaman/boss_archaedas.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/western_plaguelands.cpp` | 4 | 4 | 0 |
| `eastern_kingdoms/westfall.cpp` | 3 | 3 | 0 |
| `eastern_kingdoms/wetlands.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/zulgurub/boss_arlokk.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/zulgurub/boss_hakkar.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/zulgurub/boss_jeklik.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/zulgurub/boss_mandokir.cpp` | 2 | 2 | 0 |
| `eastern_kingdoms/zulgurub/boss_marli.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/zulgurub/boss_renataki.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/zulgurub/boss_thekal.cpp` | 3 | 3 | 0 |
| `eastern_kingdoms/zulgurub/boss_venoxis.cpp` | 1 | 1 | 0 |
| `eastern_kingdoms/zulgurub/zulgurubScripts.cpp` | 1 | 1 | 0 |
| `kalimdor/ashenvale.cpp` | 4 | 4 | 0 |
| `kalimdor/azshara.cpp` | 7 | 7 | 0 |
| `kalimdor/boss_azuregos.cpp` | 1 | 1 | 0 |
| `kalimdor/darkshore.cpp` | 8 | 8 | 0 |
| `kalimdor/desolace.cpp` | 9 | 9 | 0 |
| `kalimdor/dire_maul/dire_maul.cpp` | 1 | 1 | 0 |
| `kalimdor/durotar.cpp` | 1 | 1 | 0 |
| `kalimdor/dustwallow_marsh.cpp` | 5 | 5 | 0 |
| `kalimdor/felwood.cpp` | 4 | 4 | 0 |
| `kalimdor/feralas.cpp` | 4 | 4 | 0 |
| `kalimdor/maraudon/boss_noxxion.cpp` | 1 | 1 | 0 |
| `kalimdor/moonglade.cpp` | 2 | 2 | 0 |
| `kalimdor/onyxias_lair/boss_onyxia.cpp` | 1 | 1 | 0 |
| `kalimdor/orgrimmar.cpp` | 2 | 2 | 0 |
| `kalimdor/razorfen_downs/razorfen_downs.cpp` | 1 | 1 | 0 |
| `kalimdor/razorfen_kraul/razorfen_kraul.cpp` | 2 | 2 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_ayamiss.cpp` | 2 | 2 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_buru.cpp` | 2 | 2 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_kurinnaxx.cpp` | 1 | 1 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_moam.cpp` | 1 | 1 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_ossirian.cpp` | 1 | 1 | 0 |
| `kalimdor/ruins_of_ahnqiraj/boss_rajaxx.cpp` | 2 | 2 | 0 |
| `kalimdor/ruins_of_ahnqiraj/ruins_of_ahnqirajScripts.cpp` | 1 | 1 | 0 |
| `kalimdor/stonetalon_mountains.cpp` | 1 | 1 | 0 |
| `kalimdor/tanaris.cpp` | 3 | 3 | 0 |
| `kalimdor/teldrassil.cpp` | 1 | 1 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_cthun.cpp` | 3 | 3 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_fankriss.cpp` | 1 | 1 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_huhuran.cpp` | 1 | 1 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_sartura.cpp` | 1 | 1 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_twinemperors.cpp` | 2 | 2 | 0 |
| `kalimdor/temple_of_ahnqiraj/boss_viscidus.cpp` | 1 | 1 | 0 |
| `kalimdor/the_barrens.cpp` | 8 | 8 | 0 |
| `kalimdor/thousand_needles.cpp` | 4 | 4 | 0 |
| `kalimdor/ungoro_crater.cpp` | 5 | 5 | 0 |
| `kalimdor/wailing_caverns/wailing_cavernsScripts.cpp` | 1 | 1 | 0 |
| `kalimdor/winterspring.cpp` | 3 | 3 | 0 |
| `kalimdor/zulfarrak/boss_zumrah.cpp` | 1 | 1 | 0 |
| `world/bosses_emerald_dragons.cpp` | 5 | 5 | 0 |
| `world/guards.cpp` | 38 | 38 | 0 |
| `world/npc_professions.cpp` | 15 | 15 | 0 |
| `world/war_effort.cpp` | 33 | 33 | 0 |

## Prioritized missing list

Priority rules for the clean-room spec-to-implementer lanes:

- P1: open-world zone, `world/` or battleground script with a classic spawn and a quest or gossip hook. A quest or service that a player meets in the world is broken or flat without it.
- P2: open-world script with a classic spawn and a combat or AI hook only.
- P3: dungeon or raid script with a classic spawn. The dungeon lanes (`dungeon-coverage-20261011.md`, `scriptdev2-dungeons-20261008.md`, batches 17-20) own these, so batch them per instance.
- P4: no spawn row in the classic dump (summoned or event-only); after the spawned ones.
- Order within a priority: spawn count descending, then entry. Hooks column: `Q` quest hook, `G` gossip hook, `AI` creature AI.

### P1 quest and gossip NPCs of the open world (0)

None.

### P2 open-world creature AIs (1)

| Entry | Name | cmangos ScriptName | Hooks | Spawns | Reference file |
|---|---|---|---|---|---|
| 620 | Chicken | `npc_chicken_cluck` | AI | 111 | `world/npcs_special.cpp` |

### P3 dungeon and raid scripts (0)

None.

### P4 no spawn row (3)

| Entry | Name | cmangos ScriptName | Hooks | Spawns | Reference file |
|---|---|---|---|---|---|
| 2674 | Advanced Target Dummy | `npc_advanced_target_dummy` | AI | 0 | `world/npcs_special.cpp` |
| 15717 | Ouro Trigger | `npc_ouro_trigger` | AI | 0 | `kalimdor/temple_of_ahnqiraj/boss_ouro.cpp` (note: the Ouro boss AI in OuroAIs.cs:80-84 already stores and steers the summoned trigger; the trigger itself has no AI, so only add trigger-side behaviour) |
| 16697 | Void Zone | `npc_aoe_damage_trigger` | AI | 0 | `world/npcs_special.cpp` |

## Registered entries (evidence)

First ArcaneCore file that carries the entry id, the script name or the escort JSON, under the caveats above.

| Entry | cmangos ScriptName | ArcaneCore file |
|---|---|---|
| 68 | `guard_stormwind` | `Creatures/AI/CreatureAiServices.cs` |
| 349 | `npc_corporal_keeshan` | `Creatures/Scripts/Escorts/validated.json` |
| 467 | `npc_defias_traitor` | `Creatures/Scripts/DefiasTraitorAI.cs` |
| 646 | `boss_mr_smite` | `Instances/Scripts/Deadmines/DeadminesInstance.cs` |
| 727 | `guard_dunmorogh` | `Creatures/Guards/GuardPostTable.cs` |
| 1379 | `npc_miran` | `Creatures/AI/CreatureAiServices.cs:276` (MiranAI) |
| 1423 | `guard_elwynnforest` | `Creatures/AI/CreatureAiServices.cs` |
| 1735 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1738 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1742 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1743 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1744 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1745 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1746 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 1749 | `npc_lady_katrana_prestor` | `Creatures/Scripts/KatranaPrestorGossip.cs` |
| 1756 | `guard_stormwind` | `Creatures/Scripts/ReginaldWindsorAI.cs` |
| 1840 | `npc_isillien` | `Creatures/Scripts/InDreamsAI.cs` |
| 1842 | `npc_taelan_fordring` | `Creatures/Scripts/InDreamsAI.cs` |
| 1853 | `boss_darkmaster_gandling` | `Instances/Scripts/DungeonBossAis.cs` |
| 1976 | `guard_stormwind` | `Creatures/Scripts/ReginaldWindsorAI.cs` |
| 1978 | `npc_deathstalker_erland` | `Creatures/Scripts/Escorts/validated.json` |
| 2058 | `npc_deathstalker_faerleia` | `Creatures/Scripts/DeathstalkerFaerleiaAI.cs` |
| 2164 | `npc_rabid_bear` | `Creatures/Scripts/RabidBearAI.cs` |
| 2168 | `npc_blackwood_warrior` | `Creatures/Scripts/CorruptedFurbolgAI.cs` |
| 2169 | `npc_blackwood_totemic` | `Creatures/Scripts/CorruptedFurbolgAI.cs` |
| 2209 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 2210 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 2530 | `mob_yenniku` | `Creatures/Scripts/YennikuAI.cs` |
| 2713 | `npc_kinelory` | `Creatures/Scripts/Escorts/validated.json` |
| 2748 | `boss_archaedas` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 2768 | `npc_professor_phizzlethorpe` | `Creatures/Scripts/Escorts/validated.json` |
| 2917 | `npc_prospector_remtravel` | `Creatures/Scripts/ProspectorRemtravelAI.cs` |
| 3084 | `guard_bluffwatcher` | `Creatures/Guards/GuardPostTable.cs` |
| 3212 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3215 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3217 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3218 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3219 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3220 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3221 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3222 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3223 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3224 | `guard_mulgore` | `Creatures/AI/CreatureAiServices.cs` |
| 3296 | `guard_orgrimmar` | `Creatures/Guards/GuardPostTable.cs` |
| 3389 | `npc_regthar_deathgate` | `Creatures/Scripts/RegtharDeathgateAI.cs` |
| 3439 | `npc_wizzlecranks_shredder` | `Creatures/Scripts/WizzlecranksShredderAI.cs` |
| 3465 | `npc_gilthares` | `Creatures/Scripts/Escorts/validated.json` |
| 3502 | `guard_contested` | `Creatures/Guards/GuardPostTable.cs` |
| 3568 | `npc_mist` | `Creatures/Scripts/FollowerQuestsAI.cs` |
| 3571 | `guard_teldrassil` | `Creatures/Guards/GuardPostTable.cs` |
| 3584 | `npc_therylune` | `Creatures/Scripts/Escorts/validated.json` |
| 3678 | `npc_disciple_of_naralex` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 3692 | `npc_volcor` | `Creatures/Scripts/VolcorAI.cs` |
| 3975 | `boss_herod` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 3976 | `boss_scarlet_commander_mograine` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 3977 | `boss_high_inquisitor_whitemane` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 4262 | `guard_darnassus` | `Creatures/Guards/GuardPostTable.cs` |
| 4484 | `npc_feero_ironhand` | `Creatures/Scripts/FeeroIronhandAI.cs` |
| 4500 | `npc_morokk` | `Creatures/AI/CreatureAiServices.cs:297` (MorokkAI) |
| 4508 | `npc_willix_the_importer` | `Instances/Scripts/Classic/RazorfenKraulInstance.cs` |
| 4624 | `guard_contested` | `Creatures/Guards/GuardPostTable.cs` |
| 4700 | `npc_aged_dying_ancient_kodo` | `Creatures/Scripts/AgedDyingAncientKodoAI.cs` |
| 4701 | `npc_aged_dying_ancient_kodo` | `Creatures/Scripts/AgedDyingAncientKodoAI.cs` |
| 4702 | `npc_aged_dying_ancient_kodo` | `Creatures/Scripts/AgedDyingAncientKodoAI.cs` |
| 4781 | `npc_snufflenose_gopher` | `Instances/Scripts/Classic/RazorfenKraulInstance.cs` |
| 4880 | `npc_stinky_ignatz` | `Creatures/Scripts/StinkyIgnatzAI.cs` |
| 4961 | `npc_dashel_stonefist` | `Creatures/Scripts/DashelStonefistAI.cs` |
| 4962 | `npc_tapoke_slim_jahn` | `Creatures/Scripts/TapokeSlimJahnAI.cs` |
| 4963 | `npc_mikhail` | `Creatures/Scripts/TapokeSlimJahnAI.cs` |
| 4966 | `npc_private_hendel` | `Creatures/Scripts/PrivateHendelAI.cs` |
| 4983 | `npc_ogron` | `Creatures/Scripts/OgronAI.cs` |
| 5090 | `npc_theramore_spar_controller` | `Creatures/Scripts/TheramoreSparControllerAI.cs` |
| 5164 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 5595 | `guard_ironforge` | `Creatures/Guards/GuardPostTable.cs` |
| 5624 | `guard_undercity` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 5644 | `npc_dalinda_malem` | `Creatures/Scripts/Escorts/validated.json` |
| 5725 | `guard_tirisfal` | `Creatures/AI/CreatureAiServices.cs` |
| 5764 | `npc_guardian` | `Creatures/Scripts/GuardianOfBlizzardAI.cs` |
| 5953 | `guard_durotar` | `Creatures/Guards/GuardPostTable.cs` |
| 5955 | `npc_tooga` | `Creatures/Scripts/ToogaAI.cs` |
| 6090 | `npc_bartleby` | `Creatures/Scripts/BartlebyAI.cs` |
| 6109 | `boss_azuregos` | `Creatures/Scripts/WorldBosses/AzuregosKazzakAI.cs` |
| 6172 | `npc_redemption_target` | `Creatures/Scripts/FaulkAI.cs` |
| 6177 | `npc_redemption_target` | `Creatures/Scripts/FaulkAI.cs` |
| 6182 | `npc_daphne_stilwell` | `Creatures/Scripts/DaphneStilwellAI.cs` |
| 6190 | `mobs_spitelashes` | `Creatures/Scripts/SpitelashAI.cs` |
| 6193 | `mobs_spitelashes` | `Creatures/Scripts/SpitelashAI.cs` |
| 6194 | `mobs_spitelashes` | `Creatures/Scripts/SpitelashAI.cs` |
| 6195 | `mobs_spitelashes` | `Creatures/Scripts/SpitelashAI.cs` |
| 6196 | `mobs_spitelashes` | `Creatures/Scripts/SpitelashAI.cs` |
| 6248 | `npc_twiggy_flathead` | `Creatures/Scripts/TwiggyFlatheadAI.cs` |
| 6487 | `boss_arcanist_doan` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 6575 | `mob_scarlet_trainee` | `Instances/Scripts/ScarletMonastery/DoanAndHerodAi.cs` |
| 6626 | `npc_plucky_johnson` | `Creatures/Scripts/KalimdorMiscAI.cs` |
| 6669 | `npc_threshwackonator` | `Creatures/Scripts/FollowerQuestsAI.cs` |
| 6784 | `npc_calvin_montague` | `Creatures/Scripts/CalvinMontagueAI.cs` |
| 7053 | `npc_foreman_klaven_mortwake` | `Creatures/Scripts/ForemanKlavenMortwakeAI.cs` |
| 7233 | `npc_taskmaster_fizzule` | `Creatures/Scripts/TaskmasterFizzuleAI.cs` |
| 7271 | `boss_zumrah` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 7287 | `npc_foreman_silixiz` | `Creatures/Scripts/BarrensStealthAlertAI.cs` |
| 7288 | `npc_gallywix` | `Creatures/Scripts/BarrensStealthAlertAI.cs` |
| 7307 | `npc_venture_co_lookout` | `Creatures/Scripts/BarrensStealthAlertAI.cs` |
| 7774 | `npc_shay_leafrunner` | `Creatures/Scripts/FollowerQuestsAI.cs` |
| 7780 | `npc_rinji` | `Creatures/Scripts/RinjiAI.cs` |
| 7784 | `npc_oox17tn` | `Creatures/Scripts/Escorts/validated.json` |
| 7800 | `boss_thermaplugg` | `Instances/Scripts/Gnomeregan/GnomereganInstance.cs` |
| 7806 | `npc_00x09hl` | `Creatures/Scripts/Oox09AI.cs` |
| 7807 | `npc_oox22fe` | `Creatures/Scripts/Escorts/validated.json` |
| 7850 | `npc_kernobee` | `Instances/Scripts/Gnomeregan/GnomereganInstance.cs` |
| 7866 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7867 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7868 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7869 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7870 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7871 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7956 | `npc_kindal_moonweaver` | `Creatures/Scripts/FreedomForAllCreaturesAI.cs` |
| 7997 | `npc_captured_sprite_darter` | `Creatures/Scripts/FreedomForAllCreaturesAI.cs` |
| 7998 | `npc_blastmaster_emi_shortfuse` | `Instances/Scripts/Gnomeregan/EmiShortfuseAi.cs` |
| 8284 | `npc_dorius_stonetender` | `Creatures/Scripts/Escorts/validated.json` |
| 8516 | `npc_belnistrasz` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 8668 | `npc_felhound_tracker` | `Creatures/Scripts/FelhoundTrackerAi.cs` |
| 8929 | `boss_moira_bronzebeard` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 8982 | `npc_ironhand_guardian` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.JailBreak.cs` |
| 9018 | `boss_high_interrogator_gerstahn` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9019 | `boss_emperor_dagran_thaurissan` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9022 | `npc_dughal_stormwing` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.JailBreak.cs` |
| 9023 | `npc_marshal_windsor` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.JailBreak.cs` |
| 9033 | `boss_general_angerforge` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9039 | `boss_doomrel` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9156 | `boss_ambassador_flamelash` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9453 | `mob_aquementas` | `Creatures/Scripts/AquementasAI.cs` |
| 9460 | `guard_contested` | `Creatures/Guards/GuardPostTable.cs` |
| 9499 | `boss_plugger_spazzring` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9500 | `npc_mistress_nagmara` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9502 | `npc_phalanx` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9503 | `npc_rocknot` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 9520 | `npc_grark_lorkrub` | `Creatures/Scripts/GrarkLorkrubAI.cs` |
| 9537 | `npc_hurley_blackbreath` | `Instances/Scripts/BlackrockDepths/BarRiotAI.cs` |
| 9568 | `boss_overlord_wyrmthalak` | `Instances/Scripts/DungeonBossAis.cs` |
| 9598 | `npc_arei` | `Creatures/Scripts/AreiAI.cs` |
| 9623 | `npc_ame01` | `Creatures/Scripts/AMe01AI.cs` |
| 9679 | `npc_tobias_seecher` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.JailBreak.cs` |
| 9816 | `boss_pyroguard_emberseer` | `Instances/Scripts/BlackrockSpire/BlackrockSpireInstance.cs` |
| 9937 | `npc_kitten` | `Creatures/Scripts/FelwoodKittenAI.cs` |
| 9999 | `npc_ringo` | `Creatures/Scripts/RingoAI.cs` |
| 10042 | `npc_corrupt_saber` | `Creatures/Scripts/FelwoodKittenAI.cs` |
| 10096 | `npc_grimstone` | `Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs` |
| 10162 | `boss_victor_nefarius` | `Instances/Scripts/BlackrockSpire/BlackrockSpireInstance.cs` |
| 10184 | `boss_onyxia` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 10300 | `npc_ranshalla` | `Creatures/Scripts/RanshallaAI.cs` |
| 10339 | `boss_gyth` | `Instances/Scripts/BlackrockSpire/BlackrockSpireInstance.cs` |
| 10384 | `mobs_spectral_ghostly_citizen` | `Instances/Scripts/DungeonBossAis.cs` |
| 10385 | `mobs_spectral_ghostly_citizen` | `Instances/Scripts/DungeonBossAis.cs` |
| 10427 | `npc_paoka_swiftmountain` | `Creatures/Scripts/Escorts/validated.json` |
| 10436 | `boss_baroness_anastari` | `Instances/Scripts/DungeonBossAis.cs` |
| 10438 | `boss_maleki_the_pallid` | `Instances/Scripts/DungeonBossAis.cs` |
| 10498 | `npc_spectral_tutor` | `Instances/Scripts/DungeonBossAis.cs` |
| 10503 | `boss_jandice_barov` | `Instances/Scripts/DungeonBossAis.cs` |
| 10556 | `npc_lazy_peon` | `Creatures/Scripts/LazyPeonAI.cs` |
| 10638 | `npc_kanati` | `Creatures/AI/CreatureAiServices.cs:279` (KanatiAI) |
| 10646 | `npc_lakota_windsong` | `Creatures/Scripts/Escorts/validated.json` |
| 10683 | `npc_rookery_hatcher` | `Instances/Scripts/BlackrockSpire/BlackrockSpireInstance.cs` |
| 10812 | `boss_dathrohan_balnazzar` | `Instances/Scripts/DungeonBossAis.cs` |
| 10917 | `npc_aurius` | `Instances/Scripts/Stratholme/StratholmeInstance.cs` |
| 10997 | `boss_cannon_master_willey` | `Instances/Scripts/DungeonBossAis.cs` |
| 11016 | `npc_captured_arkonarin` | `Creatures/Scripts/CapturedArkonarinAI.cs` |
| 11122 | `mob_restless_soul` | `Creatures/Scripts/RestlessSoulAI.cs` |
| 11145 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11146 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11152 | `npc_the_scourge_cauldron` | `Creatures/Scripts/ScourgeCauldronAI.cs` |
| 11176 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11177 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11178 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11190 | `guard_contested` | `Creatures/Guards/GuardPostTable.cs` |
| 11191 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11192 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11193 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11218 | `npc_kerlonian` | `Creatures/Scripts/KerlonianAI.cs` |
| 11347 | `mob_zealot_lorkhan` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11348 | `mob_zealot_zath` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11359 | `npc_soulflayer` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11382 | `boss_mandokir` | `Instances/Scripts/Classic/ZulGurubInstance.cs` |
| 11502 | `boss_ragnaros` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11560 | `npc_magrami_spectre` | `Creatures/Scripts/KalimdorMiscAI.cs` |
| 11583 | `boss_nefarian` | `Instances/Scripts/BlackwingLair/BlackwingLairInstance.Events.cs` |
| 11625 | `npc_cork_gizelton` | `Creatures/Scripts/CorkGizeltonAI.cs` |
| 11626 | `npc_rigger_gizelton` | `Creatures/Scripts/CorkGizeltonAI.cs` |
| 11627 | `npc_aged_dying_ancient_kodo` | `Creatures/Scripts/AgedDyingAncientKodoAI.cs` |
| 11662 | `mob_flamewaker_priest` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11672 | `mob_core_rager` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11832 | `npc_keeper_remulos` | `Creatures/Scripts/NightmareManifestsAI.cs` |
| 11856 | `npc_kaya` | `Creatures/Scripts/Escorts/validated.json` |
| 11981 | `boss_flamegor` | `Instances/Scripts/BlackwingLair/DrakeAI.cs` |
| 11982 | `boss_magmadar` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 11983 | `boss_firemaw` | `Instances/Scripts/BlackwingLair/DrakeAI.cs` |
| 11988 | `boss_golemagg` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12017 | `boss_broodlord` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12018 | `boss_majordomo` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12056 | `boss_baron_geddon` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12057 | `boss_garr` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12098 | `boss_sulfuron` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12099 | `mob_firesworn` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12118 | `boss_lucifron` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12126 | `npc_tirion_fordring` | `Creatures/Scripts/InDreamsAI.cs` |
| 12259 | `boss_gehennas` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12264 | `boss_shazzrah` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 12277 | `npc_melizza_brimbuzzle` | `Creatures/Scripts/MelizzaBrimbuzzleAI.cs` |
| 12397 | `boss_kazzak` | `Creatures/Scripts/WorldBosses/AzuregosKazzakAI.cs` |
| 12423 | `npc_garments_of_quests` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 12427 | `npc_garments_of_quests` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 12428 | `npc_garments_of_quests` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 12429 | `npc_garments_of_quests` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 12430 | `npc_garments_of_quests` | `Creatures/Scripts/GarmentsOfQuestsAI.cs` |
| 12435 | `boss_razorgore` | `Instances/Scripts/BlackwingLair/BlackwingLairInstance.Events.cs` |
| 12580 | `npc_reginald_windsor` | `Creatures/Scripts/ReginaldWindsorAI.cs` |
| 12717 | `npc_muglash` | `Creatures/Scripts/MuglashAI.cs` |
| 12818 | `npc_ruul_snowhoof` | `Creatures/Scripts/RuulSnowhoofAI.cs` |
| 12858 | `npc_torek` | `Creatures/Scripts/Escorts/validated.json` |
| 12920 | `npc_doctor` | `Creatures/Scripts/TriageAI.cs` |
| 12923 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12924 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12925 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12936 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12937 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12938 | `npc_injured_patient` | `Creatures/AI/CreatureAiServices.cs` |
| 12939 | `npc_doctor` | `Creatures/Scripts/TriageAI.cs` |
| 13020 | `boss_vaelastrasz` | `Instances/Scripts/BlackwingLair/BlackwingLairGossip.cs` |
| 13076 | `guard_dunmorogh` | `Creatures/AI/CreatureAiServices.cs` |
| 13116 | `npc_spirit_guide` | `Battlegrounds/SpiritGuideService.cs (spirit heal channel; the guide has no AI script)` |
| 13117 | `npc_spirit_guide` | `Battlegrounds/SpiritGuideService.cs (spirit heal channel; the guide has no AI script)` |
| 13282 | `boss_noxxion` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14020 | `boss_chromaggus` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14392 | `npc_overlord_runthalak` | `Creatures/Scripts/RallyingCryGossip.cs` |
| 14394 | `npc_major_mattingly` | `Creatures/Scripts/RallyingCryGossip.cs` |
| 14449 | `npc_blackwing_orb` | `Instances/Scripts/BlackwingLair/BlackwingOrbAI.cs` |
| 14494 | `npc_eris_havenfire` | `Creatures/Scripts/ErisHavenfireAI.cs` |
| 14501 | `npc_warlock_mount_ritual` | `Instances/Scripts/DireMaul/DireMaulDreadsteed.cs` |
| 14503 | `npc_the_cleaner` | `Creatures/Scripts/StaveOfTheAncientsAI.cs` |
| 14507 | `boss_venoxis` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14509 | `boss_thekal` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14510 | `boss_marli` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14515 | `boss_arlokk` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14517 | `boss_jeklik` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14527 | `npc_simone_the_inconspicuous` | `Creatures/Scripts/SimoneAI.cs` |
| 14529 | `npc_klinfran` | `Creatures/Scripts/StaveOfTheAncientsAI.cs` |
| 14531 | `npc_artorius` | `Creatures/Scripts/StaveOfTheAncientsAI.cs` |
| 14533 | `npc_simone_seductress` | `Creatures/Scripts/SimoneAI.cs` |
| 14534 | `npc_klinfran` | `Creatures/Scripts/StaveOfTheAncientsAI.cs` |
| 14535 | `npc_artorius` | `Creatures/Scripts/StaveOfTheAncientsAI.cs` |
| 14538 | `npc_precious_the_devourer` | `Creatures/Scripts/SimoneAI.cs` |
| 14601 | `boss_ebonroc` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14720 | `npc_high_overlord_saurfang` | `Creatures/Scripts/RallyingCryGossip.cs` |
| 14721 | `npc_field_marshal_afrasiabi` | `Creatures/Scripts/RallyingCryGossip.cs` |
| 14750 | `npc_gurubashi_bat_rider` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14834 | `boss_hakkar` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 14887 | `boss_ysondre` | `Creatures/Scripts/WorldBosses/DragonsOfNightmareAI.cs` |
| 14888 | `boss_lethon` | `Creatures/Scripts/WorldBosses/DragonsOfNightmareAI.cs` |
| 14889 | `boss_emeriss` | `Creatures/Scripts/WorldBosses/DragonsOfNightmareAI.cs` |
| 14890 | `boss_taerar` | `Creatures/Scripts/WorldBosses/DragonsOfNightmareAI.cs` |
| 14988 | `mob_ohgan` | `Instances/Scripts/ZulGurub/OtherBossAis.cs` |
| 15084 | `boss_renataki` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15101 | `npc_zulian_prowler` | `Instances/Scripts/ZulGurub/ZulGurubTrashAis.cs` |
| 15184 | `guard_contested` | `Creatures/AI/CreatureAiServices.cs` |
| 15261 | `npc_spirit_shade` | `Creatures/Scripts/WorldBosses/DragonsOfNightmareAI.cs` |
| 15275 | `boss_veknilash` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15276 | `boss_veklor` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15299 | `boss_viscidus` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15339 | `boss_ossirian` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15340 | `boss_moam` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15348 | `boss_kurinnaxx` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15355 | `mob_anubisath_guardian` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15362 | `npc_malfurion_stormrage` | `Instances/Scripts/SunkenTemple/SunkenTempleInstance.Malfurion.cs` |
| 15369 | `boss_ayamiss` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15370 | `boss_buru` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15383 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15431 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15432 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15434 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15437 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15445 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15446 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15448 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15450 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15451 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15452 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15453 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15455 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15456 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15457 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15459 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15460 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15469 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15471 | `npc_general_andorov` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15473 | `npc_kaldorei_elite` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15477 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15491 | `boss_eranikus` | `Creatures/Scripts/NightmareManifestsAI.cs` |
| 15508 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15509 | `boss_huhuran` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15510 | `boss_fankriss` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15512 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15514 | `npc_buru_egg` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15515 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15516 | `boss_sartura` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15517 | `boss_ouro` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15522 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15525 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15528 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15529 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15532 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15533 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15534 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15535 | `npc_war_effort` | `src/ArcaneCore.Kernel/WorldData/WorldState/WarEffortCatalog.cs (counter turn-ins run in the quest reward transaction, no NPC script; see aq-war-effort-phases-20261010.md)` |
| 15555 | `npc_hive_zara_larva` | `Instances/Scripts/RuinsOfAhnQiraj/RemainingBossAis.cs` |
| 15571 | `boss_maws` | `Creatures/Scripts/MawsAI.cs` |
| 15589 | `boss_eye_of_cthun` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15712 | `npc_ouro_mound` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15718 | `npc_ouro_scarab` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15727 | `boss_cthun` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15728 | `mob_giant_claw_tentacle` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 15740 | `npc_silithus_boss` | `src/ArcaneCore.World/WorldState/WarEffortFeature.cs (the Colossus AI, found by reading the source)` |
| 15741 | `npc_silithus_boss` | `src/ArcaneCore.World/WorldState/WarEffortFeature.cs (the Colossus AI, found by reading the source)` |
| 15742 | `npc_silithus_boss` | `src/ArcaneCore.World/WorldState/WarEffortFeature.cs (the Colossus AI, found by reading the source)` |
| 15957 | `npc_ouro_spawner` | `Instances/Scripts/InstanceScriptedAis.cs` |
| 17804 | `npc_squire_rowe` | `Creatures/Scripts/SquireRoweAI.cs` |

## Registered with weak evidence (15)

The id is found only outside the script directories proper (the generic AI layer, the guard or crafting tables, the battleground root files). These are the likeliest false "registered"; a spec agent should confirm them with `ScriptCatalog.Resolve` first.

| Entry | cmangos ScriptName | ArcaneCore files |
|---|---|---|
| 5164 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7866 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7867 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7868 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7869 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7870 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 7871 | `npc_prof_leather` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11145 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11146 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11176 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11177 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11178 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11191 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11192 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |
| 11193 | `npc_prof_blacksmith` | `Crafting/ProfessionSpecializationGossip.cs` |

## Notes for the follow-up lanes

- Confirm each chosen entry against the running catalog (`ScriptCatalog.Resolve`) before specifying it: a false "registered" hides a missing script, a false "missing" wastes a lane.
- Some cmangos scripts are covered in vmangos terms by `creature_ai_scripts` rows (vmangos checkout) that already run in `CreatureEventAI`; check the entry there before porting C++ behaviour.
- The hooks column is what the reference registers, not what a quest needs: read the quest chain in the reference database when specifying.
