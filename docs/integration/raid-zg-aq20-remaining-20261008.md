# ZG and AQ20 remaining boss scripts (2026-10-08)

This lane starts at `ddebfd52`. It adds C# encounter scripts and deterministic Game tests. It does not migrate a schema, import third-party code, start a server, or record a real-client clear. The already-delivered Kurinnaxx and Hakkar scripts remain the basis for those bosses.

## Behaviors and references

The following are behavior adaptations from read-only GPL references, with no GPL source copied. Paths are relative to `D:/refs/mangos-classic/src/game/AI/ScriptDevAI/scripts/` (MC) and `D:/refs/vmangos/src/scripts/` (VM). In the table, MC `.../zulgurub/` expands to `eastern_kingdoms/zulgurub/` and VM `.../zulgurub/` expands to `eastern_kingdoms/stranglethorn_vale/zulgurub/`. ClassicDB is `ClassicDB_1_12_1_z2815.sql.gz`.

| Encounter | Implemented | Reference file and function |
|---|---|---|
| Jeklik | Bat spells, troll transition below 50%, two bat riders below 35% with one delayed liquid-fire cast, rider cleanup | VM `eastern_kingdoms/stranglethorn_vale/zulgurub/boss_jeklik.cpp` `Reset`, `UpdateAI`; MC `eastern_kingdoms/zulgurub/boss_jeklik.cpp` `ExecuteAction` |
| Venoxis | Troll and snake rotations, threat reset at 50%, Parasitic Serpent below 25%, Frenzy below 20% | VM `.../zulgurub/boss_venoxis.cpp` `Reset`, `UpdateAI`; MC `.../zulgurub/boss_venoxis.cpp` `ExecuteAction`; ClassicDB spell list 1450703 |
| Mar'li | Troll/spider changes and threat reset, egg hatching and spawn of 15041, web/poison/spider spells, egg reset on wipe | VM `.../zulgurub/boss_marli.cpp` `Aggro`, `UpdateAI`; MC `.../zulgurub/boss_marli.cpp` `ExecuteAction` |
| Thekal, Lor'Khan, Zath | Lethal-hit fake death, one-shot 10-second resurrection (1 second for Thekal when both zealots are down), PreventRevive of both zealots, five-second uninteractible tiger transition, tiger vulnerability and spells, zealot despawn and emote on Thekal's death, zealots evading with Thekal | MC `.../zulgurub/boss_thekal.cpp` `boss_thekalBaseAI::JustPreventedDeath`, `Revive`, `PreventRevive`, `boss_thekalAI::OnFakeingDeath`, `OnRevive`, `CanPreventAddsResurrect`, `JustDied`, `EnterEvadeMode`, `ExecuteAction`; ClassicDB spell lists 1450901/2, 1134701, 1134801 |
| Arlokk | Gong summons her from ClassicDB event 9066; forcefield state, troll/panther forms, trigger-based prowlers, vanish, mark, cleanup | MC `.../zulgurub/boss_arlokk.cpp` `GOUse_go_gong_of_bethekk`, `boss_arlokkAI::Aggro`, `ExecuteAction`, `ArlokkVanish::OnEffectExecute`; `zulgurub.cpp` `SetData`; ClassicDB `dbscripts_on_event` 9066 and spell lists 1451501/2 |
| Jin'do | Brain Wash totem, healing ward, Hex, delusions, shade summon, banish | VM `.../zulgurub/boss_jindo.cpp` `Reset`, `UpdateAI` |
| Mandokir | Ohgan state and enrage on Ohgan's death, chained spirits, raptor summon, combat spells, move downstairs on SPECIAL | MC `.../zulgurub/boss_mandokir.cpp` `Aggro`, `JustDied`, `aSpirits`; `zulgurub.cpp` `SetData(TYPE_OHGAN)`; VM `.../zulgurub/boss_mandokir.cpp` `Reset`, `UpdateAI` |
| Gahz'ranka | Frost Breath, Massive Geyser and threat reset, Slam | VM `.../zulgurub/boss_gahzranka.cpp` `Reset`, `UpdateAI` |
| Edge of Madness | Gri'lek, Hazza'rah, Renataki and Wushoolay AIs; Hazza'rah's illusion follow-up spells | ClassicDB EventAI 1508201/2, 1508501/2 and spell lists 1508301, 1508401; MC `.../zulgurub/zulgurubScripts.cpp` `SummonNightmareIllusion`; VM `.../zulgurub/boss_renataki.cpp` `Reset`, `UpdateAI` |
| Rajaxx | Andorov spawn, gossip/start dialogue, seven captain waves, three-minute fallback or advance after each wave dies, Rajaxx attack, event fail when Rajaxx or a captain evades (Rajaxx's own pull does not start it), Andorov/Kaldorei combat spells | MC `kalimdor/ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp` `DoSpawnAndorovIfCan`, `DoSendNextArmyWave`, `OnCreatureEnterCombat`, `OnCreatureEvade`, `OnCreatureDeath`, `Update`; `boss_rajaxx.cpp` `GossipHello`, `GossipSelect`, `npc_general_andorovAI`, `npc_kaldorei_eliteAI`; ClassicDB EventAI 1534101-7 |
| Moam | Full-mana Arcane Eruption gate, Trample, Double Attack, Drain Mana, 90-second fiends/Energize, remove Energize after last fiend, aggro/mana-full/energizing emotes, mana zeroed and fiends despawned on reset | MC `kalimdor/ruins_of_ahnqiraj/boss_moam.cpp` `boss_moamAI::Reset`, `ExecuteAction`, `SummonManaFiendsMoam::OnEffectExecute` |
| Buru | Egg phase speed and target reset with the 1,000,000-threat random player and target emote (also on aggro), sub-20% transform and Creeping Plague, egg explosion distance damage and 15% boss damage, hatchling summon | MC `kalimdor/ruins_of_ahnqiraj/boss_buru.cpp` `boss_buruAI::ExecuteAction`, `DoAttackNewTarget`, `npc_buru_eggAI::SpellHitTarget`, `JustDied` |
| Ayamiss | Air start, ground transition at 70%, Frenzy below 20%, poison/stinger/swarmer/larva and ground attacks | MC `kalimdor/ruins_of_ahnqiraj/boss_ayamiss.cpp` `boss_ayamissAI::Reset`, `ExecuteAction`, `JustSummoned` |
| Ossirian | Supreme and combat spells, a pre-pull crystal, five more crystals ten seconds into the fight, one more per weakness, five random weakness spells, Supreme removal/recast delay, crystal consumption | MC `kalimdor/ruins_of_ahnqiraj/boss_ossirian.cpp` `boss_ossirianAI::Reset`, `RespawnFirstCrystal`, `DoSpawnNextCrystal`, `SpellHit`, `ExecuteAction(OSSIRIAN_INITIAL_SPAWN)`, `GOUse_go_ossirian_crystal` |

## Files changed

New ZG scripts: `PriestBossAis.cs`, `ThekalAis.cs`, `OtherBossAis.cs`, `EdgeOfMadnessAis.cs`, `ZulGurubObjects.cs`. New AQ20 scripts: `RemainingBossAis.cs`, `AndorovAI.cs`, `RaidSummonScripts.cs`, `RuinsOfAhnQirajEvents.cs`. Modified shared script files: `Classic/ZulGurubInstance.cs`, `Raids/RaidBossAI.cs`, `RuinsOfAhnQiraj/RuinsOfAhnQirajInstance.cs`, `ZulGurub/HakkarAI.cs`, `ZulGurub/ZulGurubPriestState.cs`. Tests: `tests/ArcaneCore.Game.Tests/Instances/RemainingRaidBossTests.cs`. Documentation: this file and `docs/areas/instances.md`.

## Data and verification boundary

A read-only streaming probe found all 35 referenced creature templates and four game-object templates (179985, 180497, 180526, 180619) in ClassicDB z2815. Jeklik, Venoxis, Mar'li, Thekal and the AQ20 bosses have static spawns; Arlokk, Gahz'ranka and Edge of Madness do not. AQ20 has 15590 crystal-trigger spawns, but no static 180619 crystal spawn; the instance now creates that object from a trigger. Edge of Madness game events 29-32 exist in ClassicDB. The operator's imported world database has not been checked or refreshed by this lane.

Focused tests cover AI selection and an opening spell for each new boss, priest and Thekal phases, real egg/rider/spirit spawns, scripted spell follow-ups, Andorov's event start, Rajaxx wave progression, Buru egg damage, Ayamiss flight, Moam's mana gate, and Ossirian's crystal and weakness. They use synthetic spell/content fixtures and direct world or AI ticks.

## Intake review (2026-10-08)

The coordinator's intake fixed these defects in the Codex diff:

- **Thekal:** a zealot rechecked its resurrection every tick, so both zealots stood back up as soon as Thekal left SPECIAL for the tiger phase. The timer is now one-shot as in `ACTION_RESSURECTION`, and Thekal cancels both zealots' timers (`CanPreventAddsResurrect`/`PreventRevive`). Thekal's death now despawns the zealots with their emote, his evade now evades them (a fake-dead zealot otherwise stayed down forever after a wipe), he rises after one second when both zealots are already down, and he is uninteractible without melee during the five-second tiger transition.
- **Rajaxx:** his own pull set the event IN_PROGRESS and launched all waves; the reference starts the waves only from Andorov. Rajaxx or a captain evading during the event now fails it.
- **Ossirian:** only one crystal ever stood at a time; the reference raises five more ten seconds into the fight and one per weakness.
- **Moam/Buru:** missing emotes, Moam's reset (mana and fiends), and Buru's 1,000,000-threat random-player target (also on aggro).

| Gate (intake, native) | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | 0 warnings, 0 errors |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~RemainingRaidBossTests` | 58 passed, 0 failed |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build` | 7,586 passed, 13 skipped, 0 failed |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build` | 3,161 passed, 29 skipped, 0 failed |

The new Thekal, Rajaxx and Ossirian tests and the Moam and Buru egg tests were each shown to fail with their fix broken.

## Remaining limits

- **Edge event day:** VM `instance_zulgurub.cpp::SetData(TYPE_RANDOM_BOSS)` maps active events 29-32 to the four bosses, but `SpawnRandomBoss` is disabled there. ArcaneCore has the event service and ClassicDB event rows, but this instance script has no injected active-event state or Edge altar summon entry point. The four combat AIs can run when spawned; automatic event-day selection is still absent.
- **Rajaxx waves:** the host does not import the reference's `AQ20_*` creature-group string IDs. This port groups a captain with warriors/needlers within 50 yards. Andorov and Kaldorei have combat AIs and their spawn/dialogue path, but full follower formation and friendly participation in each wave have not been proven.
- **Gahz'ranka:** fishing/lure summon is outside the instance script hooks used here. His combat AI is present for a spawned creature; a real lure-to-spawn path has not been verified.
- **Ayamiss:** the host cannot run the reference's database movement path for her landing. She moves toward home; larva fixate/feed and swarm formation need a live-content test.
- **Spell and visuals:** summoned adds, charm and aura behavior rely on imported Spell data and the existing spell host. Buru's egg applies the reference damage through `MapCombat` at death because the host excludes dead casters from `SpellHitTarget`; the triggered explosion/hatchling spells remain for visuals and summoning. Original client visuals, timing under casts and a multiplayer raid clear are unverified.
- **Jin'do:** the boss casts Brain Wash Totem, Powerful Healing Ward, Hex, Delusions, Shade and Banish, but the summoned shade (invisible, hittable only under Delusions, Shadow Shock) and the Brain Wash totem's mind control have no AIs yet.
- **Ossirian:** the Sand Vortex summons, the storm weather and the ten-second run-speed increase are not ported. Crystals can only be used while the encounter is in progress.
- ZG's retained ScriptDev2 eight-slot save format has no persisted slots for Jin'do, Gahz'ranka or Edge of Madness. Kurinnaxx/Hakkar wave-7 tests still cover their existing logic. No real-client or hosted-CI validation is claimed.
