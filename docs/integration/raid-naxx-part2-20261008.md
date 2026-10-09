# Naxxramas part 2, map 533 (2026-10-08)

This lane adds the Military and Construct quarters, Sapphiron and Kel'Thuzad to the
ScriptDev2 save layout of map 533. Slots 6-14 use the values in mangos-classic
`src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/naxxramas/naxxramas.h`
(`TYPE_RAZUVIOUS` through `TYPE_KELTHUZAD`). Since the rework the branch contains lane
raid-naxx-1 (merged at 776dc041): one `partial` `NaxxramasInstance` with 15 slots (types
0-14; mangos' `MAX_ENCOUNTER` 16 leaves slot 15 unused), part 1's
`NaxxramasInstance.cs` calling this lane's `NaxxramasInstance.PartTwo.cs` hooks. Merge
order: raid-naxx-1 first, then this branch (or this branch alone, which carries both).
No database schema version was taken.

## Source and behavior

The references below were read as behavior and data sources only. No GPL source
code was copied.

| Encounter | Implemented behavior | Reference function |
|---|---|---|
| Razuvious | Four understudies from imported templates/positions if not already spawned; Unbalancing Strike, Disrupting Shout, Hopeless, stair leash. Possession bar has Taunt and Shield Wall; the existing control service applies Mind Exhaustion at release. | vmangos `src/scripts/eastern_kingdoms/eastern_plaguelands/naxxramas/boss_razuvious.cpp` `RespawnAdds`, `Aggro`, `UpdateAI`, `JustDied`; vmangos `src/game/Spells/SpellAuras.cpp` `HandleModPossess` (16803); vmangos `sql/migrations/20260607172947_world.sql` `creature_charm_spells` rows for 16803; mangos-classic `boss_razuvious.cpp` `SpellHit`. |
| Gothik | Balcony summons at 4/49/104 seconds, ground transition at 4:34, side teleports every 20 seconds; the central gate opens after the fourth teleport or at 30% health (teleporting then stops) and the entry gate stays shut until the encounter ends; spectral conversion at imported dead-side trigger, reset cleanup. | mangos-classic `boss_gothik.cpp` `Aggro`, `StartTraineeSummons`, `StartKnightSummons`, `StartRiderSummons`, `HandleGroundPhase`, `HandleOpenGates`, `ExecuteAction(GOTHIK_STOP_TELE)`; vmangos `boss_gothik.cpp` `UpdateAI` (fourth teleport / 30%); `naxxramas.cpp` `IsInRightSideGothikArea`, `SetData`. |
| Four Horsemen | Pulling one horseman pulls the other three; marks at 20 s then every 12 s, each successful mark halving every threat entry; mark stacks past the first deal 28836 (250/1000/3000, then 1000 per stack); specials first at 12 s (Blaumeux, Zeliek) or 30 s (Korth'azz); shield walls at 50/20%, spirits, four distinct deaths for shared completion, chest progression. A wipe (FAIL) respawns the dead horsemen and sends the living ones home; a dead horseman created before the encounter is done respawns. | mangos-classic `boss_four_horsemen.cpp` four boss `ExecuteAction`/`JustDied` methods, `HorsemenMark::OnApply`, and `naxxramas.cpp` `SetData(TYPE_FOUR_HORSEMEN)`; vmangos `boss_four_horsemen.cpp` `boss_four_horsemen_shared::Aggro`/`Reset`/`SpellHitTarget`/`UpdateAI`, `instance_naxxramas::SetData(TYPE_FOUR_HORSEMEN, FAIL)`/`OnCreatureCreate`, `boss_lady_blaumeuxAI::UpdateAI`, `boss_highlord_mograineAI::Aggro`, `boss_thane_korthazzAI::UpdateAI`, `boss_sir_zeliekAI::UpdateAI`. |
| Patchwerk | Hateful Strike every 1.2 seconds on the highest-current-health non-tank player among the first four players in melee reach (`MapCombat.CanReachWithMeleeSpellAttack`: both combat reaches + 4/3, at least 5 yd, 2D) on the threat list, tank fallback, 5% enrage, seven-minute berserk and later Slimebolt. | vmangos `boss_patchwerk.cpp` `DoHatefulStrike`, `Aggro`, `UpdateAI`; mangos-classic `boss_patchwerk.cpp` `ExecuteAction`, `HatefulStrikePrimer`. |
| Grobbulus | Mutating Injection, expiry/dispel burst and Poison Cloud, Fallout Slime per Slime Spray player hit, moving Poison Clouds, ranged Slime Stream and berserk. | vmangos `boss_grobbulus.cpp` `DoCastMutagenInjection`, `SpellHitTarget`, `UpdateAI`; mangos-classic `boss_grobbulus.cpp` `MutatingInjection::OnApply`. |
| Gluth | Mortal Wound, Decimate at 105 seconds then 100-110 seconds, Frenzy, roar, berserk; nearby world triggers summon Zombie Chow, with trigger auras cleared at death/home. Decimate leaves engaged players and nearby zombies at 5% HP. | mangos-classic `boss_gluth.cpp` `Aggro`, `ExecuteAction`, `StopSummoning`, `Decimate::OnEffectExecute`; vmangos `boss_gluth.cpp` `UpdateAI`. |
| Thaddius | Stalagg/Feugen enter together, fake death with a ten-second pairing window, revive on timeout, Tesla overload after both and a 14-second delay, magnetic-pull jump spell on the opposite tanks, Polarity Shift and charges, 13-yard same-charge buffs/opposite-charge pulses, Chain Lightning, out-of-range Ball Lightning and berserk. | mangos-classic `boss_thaddius.cpp` `boss_thaddiusAddsAI::Aggro`, `Revive`, `ExecuteAction`, `PolarityShift::OnEffectExecute`, `ThaddiusCharge::OnPeriodicTrigger`; vmangos `boss_thaddius.cpp` `DoMagneticPull`, `StartPhase2`, `UpdateAI`. |
| Sapphiron | Birth object summons after 22 seconds at the reference position; Frost Aura, ground spells, 46-second air transition, five Icebolts with Ice Block/immunity, Frost Breath, landing and berserk. | mangos-classic `naxxramas.cpp` `instance_naxxramas::Update` and `boss_sapphiron.cpp` `GOUse_go_sapphiron_birth`, `boss_sapphironAI::ExecuteAction`, `IceBolt::OnEffectExecute`, `PeriodicIceBolt::OnPeriodicTrigger`; vmangos `boss_sapphiron.cpp` air events. |
| Kel'Thuzad | Area trigger 4112 (living non-GM players only) sets the encounter in progress, which starts the five-minute phase-one channel and add aura timeline; he stays out of combat, immune and unselectable, with no victim, chase or swing, and the encounter fails when no living player is left. At 5:25 phase two makes him attackable, chasing and swinging, in combat with the zone. Phase-two timers are vmangos's: Frostbolt 10 s then 5-7 s, Shadow Fissure 14 s then 10-20 s, Detonate Mana 20 s then 20-25 s, Frostbolt volley 30 s then 15-17 s, Frost Blast 50 s then 30-60 s (Fissure, Blast and volley kept 5-8 s apart; Fissure and Blast skip the top-threat target), Chains 60 s then 60-75 s with a threat reset. Below 40% the guardian aura and windows begin for 55 seconds. Four stunned guardians trigger shackle clearing. | mangos-classic `naxxramas.cpp` `DoHandleAreaTrigger`, `Update` and `boss_kelthuzad.cpp` `SpellHit`, `UpdateSummoning`, `StartPhase2`, `ExecuteAction`, `ChainsKelThuzad::OnEffectExecute`, `FrostBlast::OnPeriodicTrigger`; vmangos `boss_kelthuzad.cpp` `UpdateP2P3`, `DoChains` (mangos-classic uses a creature spell list for phase two). |
| Progression | Military/Construct doors, the Four Horsemen chest, Sapphiron waterfall, Kel'Thuzad door and guardian windows. The wing-end portals and eye ramps of slots 8 and 12 and the 4156 Frostwyrm gate (`BlocksAreaTriggerTeleport`, vetoing ClassicDB's `areatrigger_teleport` row until the four wings are done) are part 1's code. | mangos-classic `naxxramas.cpp` `OnObjectCreate`, `SetData`, `DoHandleAreaTrigger`, `AreaTrigger_at_naxxramas`. |

## Content audit

An exact-table scan of `ClassicDB_1_12_1_z2815.sql.gz` found all 32 checked
creature templates and all 20 checked game-object templates. Map 533 has four
imported Death Knight Understudy spawns and thirteen sub-boss trigger spawns; the
main bosses except Sapphiron have one spawn each. Sapphiron is created by birth
object 181356, matching the ScriptDev2 use script. The imported world must carry
the relevant game objects, spells and creature templates. If a template/trigger is
missing, the code does not fabricate a substitute.

ClassicDB's 16803 template has `SpellList=0` and no charm-spell rows. The two
understudy bar spells are an explicit map-533 shim from vmangos
`sql/migrations/20260607172947_world.sql` (`creature_charm_spells` rows 16803,
29060/29061). General creature charm spell-list content still needs an importer
and source rows; this slice does not create a new schema version.

## Verification and remaining acceptance

The deterministic Game tests cover AI registration and death credit, signature
timers, the Gothik side conversion and gate phase, shared Horsemen credit,
Construct fake death/Tesla timing, Grobbulus aura removal, Gluth Decimate,
Sapphiron air/birth and Kel'Thuzad phases. These fixtures use synthetic creature
templates and a fake caster; they do not prove that every imported Spell.dbc
effect, world-spawn grouping, client packet, movement or forty-player interaction
behaves as intended. The full imported-world and real-client raid clear are still
required before calling Naxxramas complete in operation.

Known fidelity limits to review in the combined lane: the core does not expose
ScriptDev2 spell-list switching for the Horsemen or Sapphiron; the direct ability
timers here stand in for those lists. Gothik spectral transfer uses the nearest
loaded dead-side trigger rather than the original anchor-spell travel chain.
Gluth's player Decimate is applied to engaged players by script rather than
through the DBC spell target filter. Kel'Thuzad shackle clearing uses the
guardian's stunned unit flag rather than ScriptDev2's guardian periodic event.
Guardian add counts and Thaddius opposite-charge damage need imported-world
verification: the charge tick casts the 28062/28085 pulse once per opposite-charge player
in 13 yards instead of letting the trigger spell run once with ScriptDev2's
`ThaddiusChargeDamage::OnCheckTarget` filter, which is only equivalent if the pulse is a
single-target spell. Sapphiron's Icebolt immunity (31800) and Ice Block summon (28535)
are cast by Sapphiron on the target rather than by the target on itself
(`PeriodicIceBolt::OnPeriodicTrigger`), so the block's position depends on the imported
spell's summon target. Gothik's adds do not get ScriptDev2's cross-side threat when the
central gate opens (`HandleOpenGates`), and his ground phase does not switch to
ScriptDev2's ranged caster mode (`SetRangedMode(true, 100, TYPE_FULL_CASTER)`).
Sapphiron lifts off where he stands (no `MovePoint` to the lift-off position, no hover
flag). A reviving Stalagg/Feugen chases at once instead of after SD2's 1.5 s hold.

Codex's local verification on this worktree (before intake fixes):

| Command | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | Passed, 0 warnings, 0 errors. |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~NaxxramasPartTwoTests` | 40 passed. |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --no-restore -v:q` | 7,608 passed, 15 skipped. |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --no-restore -v:q --logger 'trx;LogFileName=world-naxx-final.trx'` | 3,231 passed, 31 skipped. |

One earlier unlogged full World run against the same production code ended with one
unidentified failure (3,230 passed, 31 skipped). The subsequent captured full
run passed; the earlier failure was not classified, so the final green run is
evidence of the captured run only.

## Intake review (2026-10-08)

The intake fixed: the Gothik central gate opened at the 4:34 landing instead of after the
fourth teleport or at 30%, and the entry gate reopened during that phase; the Four
Horsemen did not pull each other and their marks dealt no stack damage; Kel'Thuzad's
phase-two timers were not from either reference; Razuvious' understudy-taunt yell drew
from the wrong text ids (one was an aggro line); Patchwerk's Hateful Strike counted the
first four threat entries before filtering to players in melee reach. The Thaddius
ten-second window test was strengthened so it fails if the first add is not revived.

## Rework after the intake review (2026-10-08)

| # | Review finding | Resolution |
|---|---|---|
| 1 | Gothik (balcony), Kel'Thuzad (phase 1), Sapphiron (air) and fake-dead Stalagg/Feugen only set `MeleeEnabled`, so the swing started by `CreatureMapSystem.AttackStart` and the chase kept running. | Fixed. `RaidBossAI.SetMeleeEnabled`/`SetCombatMovement` act on the live state (`CreatureMapSystem.SetMelee`, `ApplyCombatMovement`); every phase switch uses them. Tests check `CombatMovement`, `MeleeEnabled`, `Combat.IsMeleeAttacking` and the motion generator. |
| 2 | Trigger 4112 called `AttackStart`, so Kel'Thuzad entered combat in phase 1. | Fixed. The trigger only sets IN_PROGRESS; `KelThuzadAI.BeginPhaseOne` runs the timeline out of combat and `AttackStart` is refused until phase 2. |
| 3 | Four Horsemen FAIL left dead horsemen dead; a restart mid-fight soft-locked the encounter. | Fixed as vmangos does it: FAIL respawns the dead and evades the living; `OnCreatureCreate` queues a respawn of a dead horseman while the encounter is not done. The death set stays in memory, like vmangos' counter, because every horseman is alive again after a reload. |
| 4 | `ThaddiusAddAI.OnAttackedBy` never called base. | Fixed: base runs unless the add is faking death. |
| 5 | Fixed 5-yard reach for Hateful Strike, Slime Stream and Ball Lightning. | Fixed: `MapCombat.CanReachWithMeleeSpellAttack` (new) for Hateful Strike, `CanReachWithMeleeAutoAttack` for the other two; Slime Stream in reach now restarts at vmangos' 1.5 s. |
| 6 | Add/add conflicts with raid-naxx-1 and opposite 4156 handling. | Fixed by merging raid-naxx-1 (776dc041, which had itself moved 4156 to a database-row veto) into this branch: one partial class, part 1's `InstanceData.BlocksAreaTriggerTeleport` gate and `WingsCleared`; this lane's `FrostwyrmUnlocked` and its `InstanceFeature` special case removed; this lane's spell scripts renamed to `NaxxramasPartTwoSpellScripts.cs`. Part 1's `LaterWingGates_AndHeiganExitDoor_KeepTheirDatabaseState` now expects the Gothik gate to shut during Gothik's own fight. |
| 7 | No game-master or alive check on triggers 4112/4113. | Fixed (`AreaTrigger_at_naxxramas`). |
| 8 | No test of the 4156 gate wiring; FakeCaster-only boss tests. | Part 1's `NaxxramasFrostwyrmGateTests` covers `InstanceFeature.Check`; `NaxxramasPartTwoGateTests` adds the Horsemen-death path to the gate and the 4112 listener path. New Game tests cover movement, swing, retaliation, threat and reach. Spell effects still run against FakeCaster (see limits above). |
| 9 | Horsemen mark at 12 s, no threat cut, early specials; Razuvious silent. | Fixed (vmangos timers and the 50% threat cut; Razuvious aggro 13075/13076/13078/13080, slay 13081 with a 10 s cooldown, death 13079). |
| 10 | No schema version taken. | Unchanged: still none. |

Each new behaviour test was proven load-bearing by reverting its fix and watching only
that test fail (10 mutations, 10 single failures).

Verification on this worktree after the rework and merge:

| Command | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | 0 warnings, 0 errors (output DLLs checked fresh). |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build` | 7,665 passed, 15 skipped, 0 failed. |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build` | 3,232 passed, 31 skipped, 3 failed under load (`PlayerbotGroupPlayerTests.WithInvitePlayers_AGroupOneShort_*`, `CharacterSaveQueueTests.FailedOptionalDataIsMergedIntoTheNewerSnapshotBeforeSaving` timeout, `LiveFxCommandTests.Lookup_IsBoundedTo20Lines_AndCountsTheRest`); all three passed on rerun, none touches Naxxramas. |
