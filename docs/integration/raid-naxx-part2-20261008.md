# Naxxramas part 2, map 533 (2026-10-08)

This lane adds the Military and Construct quarters, Sapphiron and Kel'Thuzad to the
16-slot ScriptDev2 save layout. Slots 0-5 are reserved for the Arachnid and Plague
lane; slots 6-14 use the values in mangos-classic
`src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/naxxramas/naxxramas.h`
(`TYPE_RAZUVIOUS` through `TYPE_KELTHUZAD`). The instance class is partial so the
coordinator can combine the separate wing implementation without renumbering or
replacing these slots. No database schema version was taken.

## Source and behavior

The references below were read as behavior and data sources only. No GPL source
code was copied.

| Encounter | Implemented behavior | Reference function |
|---|---|---|
| Razuvious | Four understudies from imported templates/positions if not already spawned; Unbalancing Strike, Disrupting Shout, Hopeless, stair leash. Possession bar has Taunt and Shield Wall; the existing control service applies Mind Exhaustion at release. | vmangos `src/scripts/eastern_kingdoms/eastern_plaguelands/naxxramas/boss_razuvious.cpp` `RespawnAdds`, `Aggro`, `UpdateAI`, `JustDied`; vmangos `src/game/Spells/SpellAuras.cpp` `HandleModPossess` (16803); vmangos `sql/migrations/20260607172947_world.sql` `creature_charm_spells` rows for 16803; mangos-classic `boss_razuvious.cpp` `SpellHit`. |
| Gothik | Balcony summons at 4/49/104 seconds, ground transition at 4:34, side teleports every 20 seconds; the central gate opens after the fourth teleport or at 30% health (teleporting then stops) and the entry gate stays shut until the encounter ends; spectral conversion at imported dead-side trigger, reset cleanup. | mangos-classic `boss_gothik.cpp` `Aggro`, `StartTraineeSummons`, `StartKnightSummons`, `StartRiderSummons`, `HandleGroundPhase`, `HandleOpenGates`, `ExecuteAction(GOTHIK_STOP_TELE)`; vmangos `boss_gothik.cpp` `UpdateAI` (fourth teleport / 30%); `naxxramas.cpp` `IsInRightSideGothikArea`, `SetData`. |
| Four Horsemen | Pulling one horseman pulls the other three; per-horseman marks whose second and later stacks deal 28836 (250/1000/3000, then 1000 per stack), specials, shield walls at 50/20%, spirits, four distinct deaths for shared completion, chest/portal progression. | mangos-classic `boss_four_horsemen.cpp` four boss `ExecuteAction`/`JustDied` methods, `HorsemenMark::OnApply`, and `naxxramas.cpp` `SetData(TYPE_FOUR_HORSEMEN)`; vmangos `boss_four_horsemen.cpp` `boss_four_horsemen_shared::Aggro`/`SpellHitTarget`, `boss_lady_blaumeuxAI::UpdateAI`, `boss_highlord_mograineAI::Aggro`, `boss_thane_korthazzAI::UpdateAI`, `boss_sir_zeliekAI::UpdateAI`. |
| Patchwerk | Hateful Strike every 1.2 seconds on the highest-current-health non-tank player among the first four players in melee reach on the threat list, tank fallback, 5% enrage, seven-minute berserk and later Slimebolt. | vmangos `boss_patchwerk.cpp` `DoHatefulStrike`, `Aggro`, `UpdateAI`; mangos-classic `boss_patchwerk.cpp` `ExecuteAction`, `HatefulStrikePrimer`. |
| Grobbulus | Mutating Injection, expiry/dispel burst and Poison Cloud, Fallout Slime per Slime Spray player hit, moving Poison Clouds, ranged Slime Stream and berserk. | vmangos `boss_grobbulus.cpp` `DoCastMutagenInjection`, `SpellHitTarget`, `UpdateAI`; mangos-classic `boss_grobbulus.cpp` `MutatingInjection::OnApply`. |
| Gluth | Mortal Wound, Decimate at 105 seconds then 100-110 seconds, Frenzy, roar, berserk; nearby world triggers summon Zombie Chow, with trigger auras cleared at death/home. Decimate leaves engaged players and nearby zombies at 5% HP. | mangos-classic `boss_gluth.cpp` `Aggro`, `ExecuteAction`, `StopSummoning`, `Decimate::OnEffectExecute`; vmangos `boss_gluth.cpp` `UpdateAI`. |
| Thaddius | Stalagg/Feugen enter together, fake death with a ten-second pairing window, revive on timeout, Tesla overload after both and a 14-second delay, magnetic-pull jump spell on the opposite tanks, Polarity Shift and charges, 13-yard same-charge buffs/opposite-charge pulses, Chain Lightning, out-of-range Ball Lightning and berserk. | mangos-classic `boss_thaddius.cpp` `boss_thaddiusAddsAI::Aggro`, `Revive`, `ExecuteAction`, `PolarityShift::OnEffectExecute`, `ThaddiusCharge::OnPeriodicTrigger`; vmangos `boss_thaddius.cpp` `DoMagneticPull`, `StartPhase2`, `UpdateAI`. |
| Sapphiron | Birth object summons after 22 seconds at the reference position; Frost Aura, ground spells, 46-second air transition, five Icebolts with Ice Block/immunity, Frost Breath, landing and berserk. | mangos-classic `naxxramas.cpp` `instance_naxxramas::Update` and `boss_sapphiron.cpp` `GOUse_go_sapphiron_birth`, `boss_sapphironAI::ExecuteAction`, `IceBolt::OnEffectExecute`, `PeriodicIceBolt::OnPeriodicTrigger`; vmangos `boss_sapphiron.cpp` air events. |
| Kel'Thuzad | Area trigger 4112 starts the five-minute phase-one channel and add aura timeline; at 5:25 phase two begins. Phase-two timers are vmangos's: Frostbolt 10 s then 5-7 s, Shadow Fissure 14 s then 10-20 s, Detonate Mana 20 s then 20-25 s, Frostbolt volley 30 s then 15-17 s, Frost Blast 50 s then 30-60 s (Fissure, Blast and volley kept 5-8 s apart; Fissure and Blast skip the top-threat target), Chains 60 s then 60-75 s with a threat reset. Below 40% the guardian aura and windows begin for 55 seconds. Four stunned guardians trigger shackle clearing. | mangos-classic `naxxramas.cpp` `DoHandleAreaTrigger`, `Update` and `boss_kelthuzad.cpp` `SpellHit`, `UpdateSummoning`, `StartPhase2`, `ExecuteAction`, `ChainsKelThuzad::OnEffectExecute`, `FrostBlast::OnPeriodicTrigger`; vmangos `boss_kelthuzad.cpp` `UpdateP2P3`, `DoChains` (mangos-classic uses a creature spell list for phase two). |
| Progression | Military/Construct doors, wing eyes/portals, Sapphiron waterfall, Kel'Thuzad door and guardian windows, and trigger 4156's four-wing Frostwyrm requirement. | mangos-classic `naxxramas.cpp` `OnObjectCreate`, `SetData`, `DoHandleAreaTrigger`. |

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
central gate opens (`HandleOpenGates`). The independent part-1 state class
must be merged with this lane's partial class and its reserved slots 0-5.

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
