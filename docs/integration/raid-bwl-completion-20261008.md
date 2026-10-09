# Blackwing Lair encounter extension (2026-10-08)

Base: `ddebfd52`. The coordinator owns review and integration. This worktree contains no commit, push, schema step, imported reference code or live server run.

Reference paths below are relative to the read-only `D:/refs/vmangos/src/scripts/eastern_kingdoms/burning_steppes/blackwing_lair/` (VM) and `D:/refs/mangos-classic/src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/blackwing_lair/` (MC). These are GPL repositories used for behavior and data facts only; no code was copied. The existing Broodlord, Firemaw and Flamegor scripts were kept.

| Encounter | Adapted behavior | Reference file and function |
|---|---|---|
| Razorgore | Grethok starts defense, the orb unlocks on his death and casts Possess on Razorgore, four generators send capped waves at 40 seconds then every 15 seconds, Destroy Egg counts each object once, the last egg releases control and enables phase-two Cleave/War Stomp/Conflagration/Fireball Volley, pre-phase-two lethal damage stops at one health, wipe clears defenders and resets eggs after 30 seconds. | MC `blackwing_lair.cpp` `OnCreatureEnterCombat`, `OnCreatureDeath`, `SetData64`, `Update`, `SetData`; MC `boss_razorgore.cpp` `boss_razorgoreAI::ReceiveAIEvent`, `ExecuteAction`, `JustPreventedDeath`, `DestroyEgg::OnEffectExecute`; VM `boss_razorgore.cpp` `SpellHitTarget`. |
| Vaelastrasz | Area trigger 3626 starts the intro; gossip advances the speech; aggro casts Essence of the Red. The tank and a random mana player receive their respective Burning Adrenaline spells at 45/15 seconds, alongside breath, nova, cleave and tail sweep. Health begins at 30 percent. | MC `boss_vaelastrasz.cpp` `BeginIntro`, `HandleIntro`, `BeginSpeech`, `HandleSpeech`, `Aggro`, `ExecuteAction`, `AreaTrigger_at_vaelastrasz`, `GossipSelect_boss_vaelastrasz`; VM `boss_vaelastrasz.cpp` text IDs. |
| Broodlord, Firemaw, Flamegor | Existing Wave 7 implementations and encounter slots retained. | [Wave 7 raid record](raid-bwl-zg-aq20-20261008.md). |
| Ebonroc | Shadow of Ebonroc on the victim at 45 seconds, then the shared drake Shadow Flame, Wing Buffet threat change and Thrash timers. | MC `boss_ebonroc.cpp` `boss_ebonrocAI` constructor/`ExecuteAction` and existing MC `boss_firemaw.cpp` common drake actions. |
| Chromaggus | Two different, saved breath families; 30/60-second initial breaths and 60-second repeats; vulnerability, Frenzy, 7-second affliction bursts, five-affliction mutation, Red-affliction death heal, one low-health enrage. Hourglass Sand removes Bronze and Bronze's periodic Time Stop has the reference 25 percent roll. | MC `boss_chromaggus.cpp` constructor/`ExecuteAction`; VM `boss_chromaggus.cpp` `Reset`/`UpdateAI`; VM `src/game/Spells/SpellAuras.cpp` periodic case 23170 and `SpellEffects.cpp` case 23645; MC `blackwing_lair.cpp` `InitiateBreath`. |
| Victor Nefarius/Nefarian | Gossip leads to intro, barrier and phase-one shadow spells. Two saved, distinct tunnel colors spawn drakonids every 6–7 seconds plus a chromatic add every 35 seconds. Forty-two drakonid deaths stop the spawners and schedule Nefarian after five seconds. Nefarian lands before combat, calls a class present in the raid, raises constructs from recorded bone objects below 20 percent, and completes slot 7 on death. | MC `boss_victor_nefarius.cpp` `DoStartIntro`, `JustDidDialogueStep`, `ExecuteAction`; VM `boss_victor_nefarius.cpp` `UpdateAI` spawn cadence; MC `blackwing_lair.cpp` `OnCreatureDeath`, `SetData`, `Update`, `InitiateDrakonid`; MC `boss_nefarian.cpp` `MovementInform`, `HandleAttackStart`, `ExecuteAction`; VM `boss_nefarian.cpp` `HandleClassCall`, `UpdateAI`. |
| Gates and persistence | 13-slot MC save order; completed encounter gates open on create and on death; Razorgore main gate and Nefarian gate close during their fights; interrupted fights reset on load; breath and tunnel selections survive the save string. | MC `blackwing_lair.h` slot order and `blackwing_lair.cpp` `SetData`, `OnObjectCreate`, `Load`; VM `instance_blackwing_lair.cpp` `OnObjectCreate` for Chromaggus' side gate. |

Synthetic tests in `RaidBossScriptTests` cover the added boss registration, pull/death, each encounter's wipe state, egg uniqueness and phase transition, possession target, Vaelastrasz intro/speech/adrenaline, Ebonroc cooldown/reset, Chromaggus distinct persistent breaths/enrage, Hourglass Sand, staged Victor gossip, 42 drakonid deaths and delayed Nefarian spawn, landing, class call, bone raise and death. The pre-change registration test failed for Vaelastrasz, Ebonroc, Chromaggus and Nefarian (4 failed / 4 passed).

## Files changed

- New `src/ArcaneCore.Game/Instances/Scripts/BlackwingLair/`: `BlackwingLairInstance.Events.cs`, `BlackwingLairGossip.cs`, `RazorgoreAI.cs`, `DestroyEggScript.cs`, `VaelastraszAI.cs`, `EbonrocAI.cs`, `ChromaggusAI.cs`, `BronzeAfflictionScript.cs`, `HourglassSandScript.cs`, `VictorNefariusAI.cs`, `NefarianAI.cs`.
- Existing raid scripts: `BlackwingLairInstance.cs` (13 states and gates), `Raids/RaidBossAI.cs` (entry registration). The delivered Broodlord, Firemaw, Flamegor and Shadow Flame files are unchanged.
- Small host seams: `Instances/Scripts/InstanceData.cs` and `Instances/InstanceManager.cs` plus `ArcaneCore.World/Instances/InstanceFeature.cs` (targeted player spell), `ArcaneCore.World/Instances/DungeonEventFeature.cs` (gossip registration).
- Tests: `tests/ArcaneCore.Game.Tests/Instances/RaidBossScriptTests.cs`, `tests/ArcaneCore.World.Tests/Instances/DungeonEventFeatureTests.cs`. Documentation: this file and `docs/areas/instances.md`.

## Content and acceptance limits

- ClassicDB z2815 has the eight BWL boss creature templates, drakonid and bone-construct templates, the named gates, eggs, orb and bone game objects, and the selected spell IDs. It also has the ScriptDev2 `gossip_texts` rows `-3469000` through `-3469004` (the Codex draft of this record said they were missing; intake found them in the dump). The importer now carries them into `creature_ai_texts` (`CreatureDumpImporter.IsDungeonGossipText`, as for Gnomeregan's `-3090000`), and `BlackwingLairGossip` falls back to their z2815 English text for a world imported before that change, so both dialogue choices are reachable without a re-import.
- Phase-one defender and drakonid waves require their existing creature templates in the imported world. The code skips a missing template rather than creating replacement data.
- The new scripts use the existing spell, aura, charm, motion, melee, creature summon and text hosts. Their synthetic encounter tests do not establish retail spell-effect parity, imported-world behavior, multiplayer viability or a 1.12.1 client clear. In particular, the orb's Possess cast depends on the host's charm implementation and live spell content; the encounter tests verify the cast and target, not manual client control.
- Vaelastrasz's technicians' DB waypoint flight and the Scepter of the Shifting Sands quest timer are outside these boss tests. The existing 13-slot quest slot remains reserved; no quest result is claimed.

## Verification

The final build used `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -v:q` after a 5.12 GiB available-memory preflight: succeeded with zero warnings and zero errors. `Get-CimInstance Win32_OperatingSystem` was denied by the sandbox, so the preflight read `\Memory\Available MBytes` through `Get-Counter` before each build.

| Command | Result |
|---|---|
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter 'FullyQualifiedName~RaidBossScriptTests' -v:q` | 42 passed, 0 failed or skipped. |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build -v:q` | 7,550 passed, 13 skipped, 0 failed. |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build -v:q` | 3,160 passed, 29 skipped, 1 failed: untouched `PlayerbotGroupPlayerTests.ABotInABotLedGroup_StillAnswersAPlayersWhisper_AndTheGroupFormedThroughItsIntakeStays`. This run preceded the final Game-only temporary-egg reset correction. |
| Same World test alone, `--filter FullyQualifiedName~PlayerbotGroupPlayerTests.ABotInABotLedGroup_StillAnswersAPlayersWhisper_AndTheGroupFormedThroughItsIntakeStays -v:n` | Passed (1/1). Cause of the full-run failure not proven. |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~ArcaneCore.World.Tests.Instances' -v:q` | 18 passed, 0 failed or skipped, including BWL gossip registration. |

The uncommitted diff has no playerbot file changes. (Codex sandbox results above; intake results below supersede them.)

## Intake review (coordinator, 2026-10-08)

Fixes applied on top of the Codex diff:

- Gossip content: `gossip_texts` -3469000..-3469004 are in ClassicDB z2815; the importer now carries them and the gossip falls back to their z2815 text (see above). Previously both encounters' gossip would have shown no option in an imported world, leaving Vaelastrasz and Victor Nefarius unstartable.
- Only the throne-room Victor Nefarius (the one the instance stores, MC `OnCreatureCreate`) offers the encounter gossip; Vaelastrasz's intro summon could otherwise start the Nefarian event in Vaelastrasz's room.
- Vaelastrasz intro: the summoned Nefarius is despawned after 25 s (MC `TEMPSPAWN_TIMED_DESPAWN, 25000`), casts Nefarius' Corruption and Red Lightning on Vaelastrasz himself and says SAY_NEFARIUS_CORRUPT_1/2 (vmangos broadcast 9794/9844); the corruption aura is removed at step 3 (MC `HandleIntro`).
- Factions: Vaelastrasz (template faction 35) turns hostile 14 on the last speech line (MC `HandleSpeech`, `TEMPFACTION_RESTORE_RESPAWN`); Victor Nefarius (faction 35) turns 14 when the intro ends (vmangos `UpdateAI` FACTION_MONSTER) and gets his template faction back on reaching home (MC `TEMPFACTION_RESTORE_REACH_HOME`). The Codex tests used an always-hostile seam and could not see this.
- Victor spawns a chromatic drakonid at each tunnel every 35 s (vmangos `m_uiAddChromaSpawnTimer`), not one.
- Texts: Razorgore's wipe line is SAY_RAZORGORE_DEATH (broadcast 9591, MC `SetData` FAIL) rather than vmangos' unrelated SAY_FREE 7980; Nefarian's 5 % line is SAY_XHEALTH (-1469008) instead of a repeat of SAY_SHADOWFLAME 9974; Nefarian says SAY_DEATH (9971) on death.
- Tests added: importer carries the five BWL gossip rows and not their neighbours (plus real-dump assertions); fallback text and throne-only Victor gossip; Vaelastrasz intro Nefarius/corruption/despawn and hostile faction; Victor faction set and restored; Bronze Affliction Time Stop 1-in-4 gate.

Intake verification (native, Release, `-m:1 -nodeReuse:false`, 0 warnings / 0 errors):

| Run | Result |
|---|---|
| Game `RaidBossScriptTests` | 45 passed, 0 failed |
| Game full | 7,553 passed, 13 skipped, 0 failed |
| Data full | 1,340 passed, 15 skipped, 0 failed |
| Data `ScriptDev2DungeonContentTests` with `ARCANECORE_CLASSICDB_DUMP` = z2815 | 5 passed (real-dump fact included) |
| World full | 3,161 passed, 29 skipped, 0 failed |
| World `Instances` | 18 passed |

Red proofs (feature broken, test fails, feature restored): importer filter back to -3090000 only (2 Data tests fail); throne check removed (2 gossip tests fail); Vaelastrasz and Victor faction changes removed (Vaelastrasz intro test and Victor gossip test fail); duplicate eggs counted and drakonid threshold 41 (Razorgore and Victor drakonid tests fail); Bronze gate always passing (Bronze test fails).

Still not established: the orb's Possess under real client control, imported-world/multiplayer clears, the technicians' flight paths, the Scepter quest timer, and MC's Razorgore despawn-and-respawn on wipe (this port evades him home instead).
