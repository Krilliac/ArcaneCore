# Molten Core and Onyxia raid scripts

Worktree `codex/w2-raid-mc-ony`, base `92966fdd`. No schema version, commit, push, live server start or live database change.

## Implementation and provenance

All reference paths below are relative to `D:/refs/mangos-classic/src/game/AI/ScriptDevAI/scripts/` unless prefixed with vmangos. These are behavioral reimplementations using ArcaneCore's creature, spell, object and instance services.

| Encounter | Implemented behavior | Reference file and functions |
|---|---|---|
| Molten Core instance | ClassicDB encounter indices; DONE/SPECIAL save strings, in-progress reset on load; seven boss-gated rune douses, flame circles, Majordomo and eight guards; defeated Majordomo returns near Ragnaros | `eastern_kingdoms/molten_core/molten_core.cpp`, `instance_molten_core::SetData`, `OnObjectCreate`, `DoSpawnMajordomoIfCan`, `SpawnMajordomo`, `Load`; `molten_coreScripts.cpp`, `GOUse_go_molten_core_rune` |
| Lucifron | Doom at 10 s then 20 s, curse at 20 s then 15 s, random shadow shock at 6 s intervals; zone combat and encounter lifecycle | vmangos `src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_lucifron.cpp`, `boss_lucifronAI::Reset`, `Aggro`, `UpdateAI`, `JustDied` |
| Magmadar | Magma Spit, frenzy, panic and separate mana/non-mana lava bomb targets | `eastern_kingdoms/molten_core/boss_magmadar.cpp`, `boss_magmadarAI` constructor, `Reset`, `ExecuteAction` |
| Gehennas | Curse, rain of fire, random and victim shadow bolts | `eastern_kingdoms/molten_core/boss_gehennas.cpp`, constructor and `ExecuteAction` |
| Garr and Firesworn | Anti-magic pulse, shackles, six-minute eruption trigger, Separation Anxiety, add thrash/immolate, death eruption and Garr enrage | `eastern_kingdoms/molten_core/boss_garr.cpp`, constructors, `Reset`, `ExecuteAction`, `SpellHit`, `JustDied` |
| Baron Geddon | Inferno, ignite mana, living bomb; one Armageddon at <=2%, stopping movement and actual melee; Inferno's eight damage steps | `eastern_kingdoms/molten_core/boss_baron_geddon.cpp`, constructor and `ExecuteAction`; mangos-classic `Spells/SpellEffects.cpp`, `Spell::EffectDummy`, case 18947 |
| Shazzrah | Arcane explosion, curse, counterspell, magic grounding and gate; random-player teleport, threat reset and explosion | `eastern_kingdoms/molten_core/boss_shazzrah.cpp`, constructor, `ExecuteAction`, `ReceiveAIEvent`; vmangos corresponding `boss_shazzrahAI::UpdateAI` for direct near-teleport |
| Sulfuron and priests | Shout, random in-combat friendly missing Inspire, Hand, spear; priests' strike, greatest-absolute-health-deficit friendly heal, pain and immolate | `eastern_kingdoms/molten_core/boss_sulfuron_harbinger.cpp`, constructors and `ExecuteAction`; mangos-classic `AI/BaseAI/UnitAI.cpp`, `DoSelectLowestHpFriendly`, default `percent=false`; `include/sc_creature.cpp`, `DoFindFriendlyMissingBuff` |
| Golemagg and Core Ragers | Magma Splash, Double Attack, Trust, pyroblast, 3-second earthquakes below 10%; rager heal at <=50%, mangle, death suicide within 100 yards | `eastern_kingdoms/molten_core/boss_golemagg.cpp`, constructors, `Reset`, `ExecuteAction`, `JustDied`, `ReceiveAIEvent` |
| Majordomo | Death prevention, reflection shields, teleports, Aegis, add encouragement/poly immunity/champion; surrender after eight distinct deaths and return home, defeat speech, teleport, three-step gossip and timed Ragnaros introduction | `eastern_kingdoms/molten_core/boss_majordomo_executus.cpp`, `Aggro`, `ExecuteAction`, `SummonedCreatureJustDied`, `JustReachedHome`, `HandleOutro`, `StartSummonEvent`, `GossipHello_boss_majordomo`, `GossipSelect_boss_majordomo` |
| Ragnaros | Rooted combat, Wrath threat drop, mana-target Might, untanked Magma Blast, Lava Burst, 180-second combat/3-second submerge/90-second submerged/500-ms emerge sequence; Sons spell chain, last-son early emergence; introduction aggro gate | `eastern_kingdoms/molten_core/boss_ragnaros.cpp`, `Reset`, `ExecuteAction`, `HandlePhaseTransition`, `JustSummoned`, `SummonedCreatureJustDied`, `SpellHitTarget`, `HandleEnterCombat` |
| Onyxia | Ground spells; <=65% liftoff, eight-point flight circuit, directional breath with opposite destination on spell hit, fireballs, 20-whelp opening wave and later 4–10 waves; <=40% landing and fear; whelp spawner object and dead warder reset | `kalimdor/onyxias_lair/boss_onyxia.cpp`, constructor, `ExecuteAction`, `MovementInform`, `PhaseTransition`, `SummonWhelps`, `SpellHit`; `instance_onyxias_lair.cpp`, `OnObjectCreate`, `OnCreatureDeath`, `SetData` |

Broadcast IDs follow the corresponding vmangos boss files and mangos-classic `sql/scriptdev2/scriptdev2.sql`'s broadcast-ID column. Gossip text is from rows -3409000..-3409002. Lucifron deliberately uses vmangos timers; the remaining combat schedules use the listed mangos-classic scripts. Majordomo's availability follows mangos-classic's seven rune bosses (Lucifron has no rune). The reference itself labels several timings/flight coordinates approximate; this port does not present them as client-verified measurements.

The additional raid spell handlers implement `Spell::EffectDummy` cases 19411/20474 (lava bombs), 21108 (eight Sons summons), 21908 (Lava Burst randomizer) and 23138 (gate), plus `scripts/world/spell_scripts.cpp`, `HateToZero::OnEffectExecute` (20538). `MoltenCoreTargetModule` handles target modes 7 and 38 for the enumerated raid spells only, following `Spell::SetTargetMap`/`CheckScriptTargeting` and ClassicDB's actual target rows, including Golemagg's Trust (20553 -> 11672). The Separation Anxiety timer follows `SpellAuras.cpp`, `Aura::HandleAuraDummy` and `Aura::PeriodicTick` cases 21094/23487, using the spell's radius. Database destinations for Onyxia/Ragnaros spells fail closed if their position is absent.

Four guard links are taken from ClassicDB z2815 `creature_linking_template` (11661/11662/11672 flags 1031; 12099 flags 1543). Their boss aggro and wipe respawn are implemented locally. This is not a general creature-linking importer or formation implementation.

## Files and integration seams

- New implementations: `src/ArcaneCore.Game/Instances/Scripts/MoltenCore/` and `.../Onyxia/`; the shared combat-action base both raids use is `Instances/Scripts/RaidCreatureAI.cs`.
- `GameObjectMapSystem.Ai.cs` and `.cs`: a post-lock-validation script callback (`IGameObjectAi.OnUnlockedUse`, default false so existing object scripts keep their spell-path behaviour). An ordinary click cannot douse a rune.
- `NpcGossipScript.cs` and `QuestNpcServices.Gossip.cs`: creature-owned gossip takes precedence over the existing world fallback; replies can replace the menu's options.
- `SpellSystem.TargetSelectors.cs`: `RegisterSpellTargetSelector` - a spell-specific selector consulted before the registered fallback for otherwise unsupported target modes.
- Tests: `RaidRegistrationTests`, `RaidBossTests`, `RaidHostTests`, `Npc/CreatureGossipScriptTests` (host-level gossip precedence and menu replacement); the existing registry test now includes maps 249/409.

## Data and operational limits

The read-only z2815 scan found the boss/add templates, guard/healer/elite EventAI, rune 176956's lock 1459, egg 176511 and whelp-spawner 176510, the raid script-target rows and all eight Sons position rows 21110..21117. Following the trigger spells from the eight directional breath roots found 93 spell rows and 93 required position rows, none missing. Guards whose combat already lives in ClassicDB retain that EventAI. No synthetic content rows were added to production.

Production still needs the matching imported spell templates, target positions, creature/game-object templates and spawns, locks and broadcast text. The supplied `client-dbc-5875-effective` directory contains no Spell.dbc, SpellRadius.dbc, SpellDuration.dbc or SpellRange.dbc. Unit tests use synthetic spells; no complete imported-world encounter or real-client run is claimed.

Instance save strings have the existing host limitation: durable across map unload/recreation, not server restart. No characters schema reservation was provided, so this lane adds none. General creature linking/formations, encounter-entry lock enforcement, raid loot/client presentation acceptance and Quel'Serrar remain outside this port. Temporary encounter summons are cleaned up on wipe; no live server was started.

## Validation

Registration RED: 2 failures before either instance script existed. Phase melee RED: 3 failures demonstrated that toggling the AI property did not stop an active host swing; the fix now uses `CreatureMapSystem.SetMelee`.

The prescribed CIM memory query was sandbox-denied; every subsequent build used Windows `GlobalMemoryStatusEx` and refused to start below 3 GiB. Final build preflight: 4.342 GiB free. MSBuild-backed formatting was also denied named-pipe access; folder-mode formatting succeeded on the explicitly named new C# files. Tests were not sandbox-blocked.

| Check | Passed | Failed | Skipped | Result |
|---|---:|---:|---:|---|
| Solution Release build | — | 0 errors | — | 0 warnings; 51.30 s |
| Final new raid tests | 49 | 0 | 0 | All new cases pass |
| Final full Game project | 7126 | 1 | 11 | Unchanged late-arrival test throws at line 364, below |
| Focused World seams | 250 | 0 | 1 | Instance, object, NPC and spell-script coverage |
| Final full World project | 2522 | 0 | 6 | Passed, 1 m 17 s |
| `git diff --check` | — | 0 | — | Clean; only Git's existing LF/CRLF notices |

Commands (run serially):

```text
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build -m:1 -nodeReuse:false --filter "FullyQualifiedName~RaidBossTests|FullyQualifiedName~RaidHostTests|FullyQualifiedName~RaidRegistrationTests"
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build -m:1 -nodeReuse:false
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build -m:1 -nodeReuse:false --filter "FullyQualifiedName~Instances|FullyQualifiedName~GameObjects|FullyQualifiedName~Npc|FullyQualifiedName~SpellScript"
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build -m:1 -nodeReuse:false
```

Failures are retained, not treated as fixed by rerunning:

- `InstanceManagerTests.PlayerCap_RefusesExtraPlayers_ExceptGameMasters_AndEjectsLateArrivals` intermittently finds `d.Map` null at line 364. It failed in the first and final full Game runs, passed in isolation and in intervening full runs. It exercises Deadmines map 36; neither that test nor its transfer implementation was changed. Cause remains unestablished. The final full Game project is **not green**.
- One intervening full World run had 2521 passed / 1 failed / 6 skipped: `ClassScriptScenarioTests.SealOfRighteousness_AndJudgement_HitTheDuelOpponent`, step 13, expected Judgement damage 15 and received 22. Earlier and final full World runs passed. No class-script or playerbot files were changed, and this intermittent assertion is not claimed fixed.
- The first full Game run also encountered a hygiene-scanner read error because PowerShell held the active root-level log open. Subsequent logs were placed in ignored build output; the hygiene test passed afterward. This was a validation-artifact issue, not a production change.

Evidence remains inside this worktree: `tests/ArcaneCore.Game.Tests/bin/Release/net10.0/raid-lane-evidence/` contains build, RED and audit logs; `raid-focused-post-review.log` and `raid-game-post-review.log` are beside that directory. World logs are under `tests/ArcaneCore.World.Tests/bin/Release/net10.0/`, including `raid-world-final.log` (the failing seal run) and `raid-world-post-review.log` (the final passing run). These build outputs are ignored by Git. Source and tests remain unstaged and uncommitted for coordinator review.

## Intake (coordinator review)

Review changes on top of the Codex diff: the shared `RaidCreatureAI` base moved out of the Molten Core folder (Onyxia no longer imports the MoltenCore namespace); the `SpellSystem` partial that lived under `Instances/Scripts/MoltenCore/` merged into `Spells/SpellSystem.TargetSelectors.cs`; `IGameObjectAi.OnUnlockedUse` now defaults to false instead of re-invoking `OnUse`, so the existing Alterac Valley beacon script does not change behaviour on the open-lock path; a vestigial unused test parameter was removed; `CreatureGossipScriptTests` added for the gossip host seam.

Native results after intake (Release, `-m:1 -nodeReuse:false`): solution build 0 warnings / 0 errors; raid + registry + gossip filter 67 passed / 0 failed; full Game project 7129 passed / 0 failed / 11 skipped (7140); full World project 2522 passed / 0 failed / 6 skipped (2528). RED proofs (feature briefly broken, rebuilt, then restored): creature gossip precedence, menu replacement, rune douse on the validated open-lock path, Ragnaros' 180-second submerge, Onyxia's 65% liftoff and Majordomo's eight-add surrender each failed their test.
