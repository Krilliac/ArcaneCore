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

## Rework after review (2026-10-08)

Review findings and what was done. Spell targets were re-read from `D:/ArcaneCore-data/client-dbc-5875/Spell.dbc` and the ClassicDB z2815 `spell_script_target` rows (`(19832,1,12435)`, `(19873,0,177807)`, `(23642,1,13020)`, `(23362,0,179804)`).

| # | Finding | Result |
|---|---|---|
| 1 | Possess 19832 (target A 38), Destroy Egg 19873 (A 40 / A 46), Nefarius' Corruption 23642 (B 7) and Raise Drakonids 23362 (B 51) had no selector, so the orb chain could not work in production. | Fixed. New `BlackwingLairTargetModule` (an `ISpellHandlerModule`, discovered like `MoltenCoreTargetModule`) registers each with `RegisterSpellTargetSelector`: Possess takes the explicit Razorgore if in range, else the nearest; Destroy Egg puts the explicit or nearest ready egg within the 10 yd range into the cast's object target and destination; Corruption hits every Vaelastrasz in the 100 yd radius; Raise Drakonids' ACTIVATE_OBJECT has no unit (the instance raises the bones). `DestroyEggScript` acts on the DUMMY effect and refuses the cast with `BadTargets` when no egg is in range. The later effect-86 implementation also activates the selected egg as a goober on effect 0; the DUMMY then records its destruction. The BWL targeting tests still pass. |
| 2 | Class calls hit the whole raid, once per member of the class. | Fixed. `NefarianAI.ClassCall` casts the call once, self-centred (MC `ExecuteAction`). A new seam `SpellSystem.RegisterSpellTargetFilter` (applied after any selection, built-in or registered: MC `Spell::OnCheckTarget`) keeps only live players of the called class for 23397/23398/23401/23410/23414/23418/23425/23427/23436. Target B 15 is a built-in area, so a selector could not override it. |
| 3 | Nefarian's aggro set IN_PROGRESS, so a later drakonid death could re-arm SPECIAL and spawn a second Nefarian. | Fixed. `NefarianAI.OnAggro` no longer touches TYPE_NEFARIAN (MC has no Aggro hook). The instance already ignores a repeated value (`SetData` returns when the state is unchanged), which with SPECIAL kept is MC's "don't store the same thing twice". |
| 4 | Phase-one wipe runs `ResetRazorgore` twice. | Disproved. `BlackwingLairInstance.SetData` returns early when the stored value equals the new one, so the `OnReachedHome` FAIL after the `OnUpdate` FAIL does nothing. The Razorgore test now drives `OnReachedHome` after the wipe and asserts a single Fireball 23024; removing the guard makes it fail. |
| 5a | Razorgore's exit not shut during Vaelastrasz. | Fixed. On every TYPE_VAELASTRASZ change except SPECIAL, with Razorgore done, door 176965 is closed while Vaelastrasz is IN_PROGRESS and open otherwise. MC toggles the door on each such change; this sets the state the toggle sequence reaches and cannot drift after a reload. |
| 5b | Chromatic Mutation applied only 23174. | Fixed. 23175 and 23177 are added to the target too (MC `SpellEffects.cpp` case 23173 casts both on the player; vmangos adds them as auras). MC's `RemoveAllAuras` before the mutation is still narrowed to the five afflictions. |
| 5c | Drakonid Bones might never exist in production. | Confirmed and fixed. ClassicDB drakonid EventAI (`1426102`..`1430202`) casts 23363 on death, whose only effect is SUMMON_OBJECT_WILD 179804; the spell system has no such effect (noted in `BattlegroundWorldHost`). The instance now places the bones where each tracked drakonid dies (skipping a duplicate at the same spot) and clears its bone list on FAIL. |
| 5d | Victor's identity relied on Z > 430. | Fixed. The instance stores a Victor Nefarius that has no summoner (MC `!IsTemporarySummon()`); Vaelastrasz's intro now summons his Nefarius with himself as summoner. The instance's own post-wipe replacement has no summoner and is stored. |

Schema: unchanged (auth 5, characters 42, world 42). No reference code copied.

Tests. New `BlackwingLairTargetingTests` cast the spells through the real `SpellSystem` with their 5875 target types (no recording caster): the orb's Possess through the same `CastPlayerTargetSpell` wiring as `InstanceFeature`; Destroy Egg with no object target (nearest egg, then the next, then `BadTargets` for an egg 40 yd away); Corruption on Vaelastrasz only; one Wild Magic cast reaching both mages and not the warrior; Raise Drakonids with no "implicit target" report, and bones from a drakonid death raised into a construct. `RaidBossScriptTests` gained the Vaelastrasz door and Chromatic Mutation tests and extended the Razorgore, Nefarian, Victor drakonid and throne-Victor tests; the old explicit-object Destroy Egg test was removed.

Red proofs (each restored afterwards; the first attempt's patch did not compile and the run passed on the stale binary, so the binaries were checked by timestamp for the counted runs): module registration skipped, per-player class-call loop restored, companion auras removed, door block disabled, `SetData` repeat guard removed, Victor back to `Z > 430`, bones disabled — 11 tests failed (all five targeting tests, Chromatic Mutation, Victor drakonids, Nefarian, Razorgore wipe, throne Victor, door). `NefarianAI.OnAggro` override removed alone — the Victor drakonid test (state after landing) and the Nefarian test failed.

Verification (native, Release, `-m:1 -nodeReuse:false`, 0 warnings / 0 errors, 6.7 GB available before the builds):

| Run | Result |
|---|---|
| Game `BlackwingLairTargetingTests` + `RaidBossScriptTests` + `RaidHostTests` | 64 passed, 0 failed |
| Game full | 7,559 passed, 13 skipped, 0 failed |
| World full | 3,161 passed, 29 skipped, 0 failed |

Not run at that intake: MockClient and Data. Still not established: Possess under real client control (the charm/pet-bar path of a possessed boss in a 1.12.1 client), imported-world and multiplayer clears, the technicians' flight paths, the Scepter quest timer, and MC's Razorgore despawn-and-respawn on wipe. Spell effect 86 was added later; its remaining limits are in [the follow-up](activate-object-effect-20261009.md).
