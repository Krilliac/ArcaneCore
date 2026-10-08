# ScriptDev2 dungeon lane handoff (2026-10-08)

Worktree `codex/w2-sd2-low`, base `92966fdd`. No commit, push, stash, configuration change, database write or server launch.

## Ported behaviour and provenance

| Map | Behaviour | mangos-classic ScriptDev2 reference |
|---|---|---|
| 36 Deadmines | Boss-death doors and script-only patrol spawns, cannon and its delayed ironclad door/alarms/guards, Mr. Smite's 66/33 percent stomp, chest run, equipment and spells, Fortune Awaits chest | `deadmines/instance_deadmines.cpp`: `OnPlayerEnter`, `OnCreatureDeath`, `SetData`, `Update`, `Spawn*DeadminesPatrol`; `deadmines/deadmines.cpp`: `GOUse_go_defias_cannon`; `deadmines/boss_mr_smite.cpp`: `boss_mr_smiteAI::UpdateAI`, `MovementInform`, `PhaseEquip*`. Equipment displays and byte fields use ClassicDB z2815 `item_template` rows 2179, 2183, 10756 and `creature_equip_template` row 646, with `Creature::SetVirtualItem` in mangos-classic `Entities/Creature.cpp:2747-2777`. |
| 43 Wailing Caverns | Fanglord intro speech, Disciple gossip/escort and its three stops, ritual summons, Mutanus completion, Naralex awakening, hover/follow exit, Fortune Awaits chest | `wailing_caverns/wailing_caverns.cpp`: `OnPlayerEnter`, `OnCreatureCreate`, `SetData`, `DespawnAll`; `wailing_caverns/wailing_cavernsScripts.cpp`: `npc_disciple_of_naralexAI::WaypointReached`, `SummonedCreatureJustDied`, `UpdateEscortAI`, `GossipHello_npc_disciple_of_naralex`, `GossipSelect_npc_disciple_of_naralex`. |
| 33 Shadowfang Keep | Prisoner gossip/escorts, Rethilgore speech, Vincent intro and staged defeat, Fenrus Arugal projection/dialogue/voidwalkers, Nandos pack trigger, Arugal combat AI, voidwalker formation/death count | `shadowfang_keep/instance_shadowfang_keep.cpp`: `OnCreatureCreate`, `OnCreatureDeath`, `DoSpeech`, `SetData`, `JustDidDialogueStep`, `Update`; `shadowfang_keep/shadowfang_keep.cpp`: `npc_shadowfang_prisonerAI`, `GossipHello/Select_npc_shadowfang_prisoner`, `mob_arugal_voidwalkerAI`, `boss_arugalAI`, `npc_deathstalker_vincentAI`. |
| 48 Blackfathom Deeps | Kelris-gated fires, four delayed waves and portal door, Fathom Stone's one Baron Aquanis summon/death state | `blackfathom_deeps/instance_blackfathom_deeps.cpp`: `DoSpawnMobs`, `SetData`, `OnCreatureDeath`, `IsWaveEventFinished`, `Update`, `GOUse_go_fire_of_akumai`, `GOUse_go_fathom_stone`; coordinates and counts from `blackfathom_deeps.h`. |
| 389 Ragefire Chasm | No ScriptDev2 script to port | No `ragefire_chasm` file or `AddSC_ragefire` in `src/game/AI/ScriptDevAI/scripts`; ClassicDB z2815 `instance_template` row 389 has empty ScriptName. No code or vacuous behaviour test was added. |

ClassicDB z2815 `script_texts` now imports into the existing creature text catalog; `script_waypoint` imports under path `0x80000000 | PathId`, keeping escort paths apart from ordinary movement. Script gossip labels `-3033000` and `-3043000` are from mangos-classic `sql/scriptdev2/scriptdev2.sql`; ClassicDB has no `script_gossip` table. No rows or waypoint coordinates were invented.

## Changed paths

- Added: `src/ArcaneCore.Game/Instances/Scripts/Deadmines/{DeadminesInstance,MrSmiteAi}.cs`, `BlackfathomDeeps/BlackfathomDeepsInstance.Events.cs`, `ShadowfangKeep/{ArugalAi,DeathstalkerVincentAi,ShadowfangKeepInstance.Events,ShadowfangPrisonerAi,ShadowfangPrisonerGossip}.cs`, `WailingCaverns/{DiscipleOfNaralexAi,DiscipleOfNaralexGossip,WailingCavernsInstance.Events}.cs`, and `src/ArcaneCore.Game/Creatures/CreatureMapSystem.{ScriptHover,ScriptSpawns}.cs`.
- Modified: `src/ArcaneCore.Game/Instances/Scripts/Classic/{BlackfathomDeepsInstance,ShadowfangKeepInstance,WailingCavernsInstance}.cs`, `Instances/Scripts/InstanceData.cs`, `Instances/InstanceManager.cs`, `Creatures/AI/EscortAI.cs`, `Creatures/CreatureMapSystem.{Evade,Lifecycle}.cs`, `Npc/QuestNpcServices.Gossip.cs`; `src/ArcaneCore.World/Instances/InstanceFeature.cs`, `Npc/QuestNpcFeature.cs`; `src/ArcaneCore.Data/World/Creatures/CreatureDumpImporter.cs`, `Content/Import/{Cli/ContentImporterCli,Spec/ContentTableSpecs}.cs`.
- Tests: added `tests/ArcaneCore.Data.Tests/ScriptDevTextImportTests.cs`, `tests/ArcaneCore.Game.Tests/Instances/{DungeonScriptTestKit,DeadminesScriptTests,BlackfathomDeepsScriptTests,ShadowfangKeepScriptTests,WailingCavernsScriptTests}.cs`; modified `tests/ArcaneCore.Data.Tests/CreatureBehaviourImportTests.cs` and `tests/ArcaneCore.Game.Tests/Instances/InstanceScriptTests.cs`.
- Docs: `docs/areas/{content-import,creature-ai,instances}.md` and this handoff.

## Verification

Before implementation, focused dungeon and import tests failed. Final `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors. Full Release `--no-build` suites: Game 7,097 passed / 11 skipped; World 2,522 passed / 6 skipped; Data 1,293 passed / 10 skipped. The opt-in `RealClassicDbDump_ImportsAllAiRows` probe against `D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz` passed (1/1). `git diff --check` exited 0. Free RAM was checked before every build (Get-Counter fallback: CIM access was denied by the sandbox).

## Remaining data and limits

- ClassicDB has no `script_waypoint` rows for Disciple of Naralex (Entry 3678, PathId 3678). The escort code and final ritual were tested with synthetic path points, but the live content refresh needs those sourced waypoint rows. The refreshed world database must also ingest the now-supported `script_texts` and Shadowfang prisoner `script_waypoint` rows.
- ScriptDev2's `UnarmedWoodcutter` and `ForsakenSkill` aura hooks are not wired: ArcaneCore has no per-spell aura-script registry. Mr. Smite's weapon displays use the three source item rows directly until a general creature-equipment loader exists.
- `InstanceSave.Data` remains Wave 4's in-memory-only encounter string across a process restart; this lane took no schema version. The Blackfathom right-side wave X coordinates are the ScriptDev2 values near `-867`, unverified against a client. No real-client acceptance run was performed.

