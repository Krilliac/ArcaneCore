# Raid scripts: bounded encounter delivery (2026-10-08)

Base: `92966fdd`. Uncommitted lane diff; no schema migration, server startup, deployment, commit or push.
This delivers five boss AI implementations and their supporting scripts, **not three complete raids**.

## Implemented and provenance

Reference paths below are relative to the read-only `D:/refs/mangos-classic` (MC) or `D:/refs/vmangos` (VM) checkout.
The implementations are C# adaptations of the cited behavior. Where the references differ, the chosen source is explicit.

| Encounter or seam | Delivered behavior | Reference file and functions |
|---|---|---|
| Broodlord Lashlayer | Cleave, Knock Away, Blast Wave and Mortal Strike; retry failed casts; platform leash below Z 448.60; aggro/leash texts; each Knock Away hit halves that target's threat; encounter aggro/home/death state. | MC `src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/blackwing_lair/boss_broodlord_lashlayer.cpp`, `boss_broodlordAI` constructor, `ExecuteAction`, `Aggro`, `JustReachedHome`, `JustDied`, `OnLeash`. Threat reduction and positive broadcast IDs: VM `src/scripts/eastern_kingdoms/burning_steppes/blackwing_lair/boss_broodlord_lashlayer.cpp`, `SpellHitTarget`, enum. Uses MC timers/leash, not VM's nearby-trash suppression or recurring zone-combat pulse. |
| Firemaw and Flamegor | Shadow Flame, Wing Buffet, Thrash; Firemaw's five-second Flame Buffet; Flamegor's Frenzy and text; each Wing Buffet hit halves its target's threat; full aggro/home/death/reset hooks. | MC same BWL directory, `boss_firemaw.cpp` / `boss_flamegor.cpp`, constructors, `ExecuteAction`, `SpellHitTarget`, lifecycle methods. Frenzy broadcast 1191: VM same BWL directory, `boss_flamegor.cpp` enum. |
| Shadow Flame | Living targets without Onyxia Scale Cloak aura 22683 receive triggered 22682; initial cone damage stays in the normal spell effect. | VM `src/game/Spells/SpellEffects.cpp`, `Spell::EffectScriptEffect`, case 22539 (lines 3720-3743). Only spell 22539 is claimed here; the Nefarian introduction variants remain for his port. |
| BWL instance | 13-slot MC save layout; implemented encounter types 2, 3 and 5; interrupted fights reset on load; Broodlord door opens on completion and reload; suppression devices created after his death deactivate. Duplicate completion cannot toggle the door closed. | MC same BWL directory, `blackwing_lair.h`; `blackwing_lair.cpp`, `SetData`, `OnObjectCreate`, `Load`, `IsEncounterInProgress`. Unsupported encounter writes remain logged. |
| Kurinnaxx | Mortal Wound, Thrash, Wide Slash and Sand Trap; one successful enrage at <=30%, reset per pull; zone combat; aggro/home/death state; Ossirian's breach text when his creature is loaded. | MC `src/game/AI/ScriptDevAI/scripts/kalimdor/ruins_of_ahnqiraj/boss_kurinnaxx.cpp`, constructor / `ExecuteAction`; `ruins_of_ahnqiraj.cpp`, creature combat/evade/death hooks and `SetData`. Zone combat and positive text IDs: VM `src/scripts/kalimdor/silithus/ruins_of_ahnqiraj/boss_kurinnaxx.cpp`, `Aggro`, `JustDied`, enum. Uses MC cooldown ranges. |
| Sand Trap | Spell 26524 creates 180647 at a random living threat target; four-second forced activation of its template spell, charge consumption, five-second expiry fallback; missing template produces a warning and no fabricated object. | VM same AQ20 directory, `boss_kurinnaxx.cpp`, `KurinnaxxSandTrap::OnEffectExecute` and `UpdateAI` cleanup. MC `boss_kurinnaxxAI::JustSummoned(GameObject*)` / `TriggerTrap` supplies the four-second activation. This is an explicit combination: VM random-threat placement plus MC timed activation for the ClassicDB zero-radius trap. |
| Object-cast destination | A game-object spell using `TARGET_LOCATION_CASTER_DEST` uses the casting object's position rather than its owner's position. Required for Sand Trap's actual area damage. | VM `src/game/Spells/Spell.cpp`, `Spell::FillTargetMap`, `TARGET_LOCATION_CASTER_DEST` branch, lines 274-293 (`GetCastingObject`). Other target modes keep their existing destination behavior. |
| AQ20 instance | MC six-slot save layout; Kurinnaxx state, interrupted-state reset and encounter-in-progress query. | MC AQ20 `ruins_of_ahnqiraj.h` / `.cpp`, `GetData`, `SetData`, `Load`, `IsEncounterInProgress`. Andorov/Rajaxx follow-up is deferred. |
| Hakkar | Blood Siphon at 90 seconds; random Corrupted Blood; tank Insanity with captured threat restored after the aura ends (checks begin after four seconds); ten-minute Berserk; all five living-priest aspects; platform bounds 45.8-57.28; reset/respawn and aggro text. | VM `src/scripts/eastern_kingdoms/stranglethorn_vale/zulgurub/boss_hakkar.cpp`, `Reset`, `Aggro`, `UpdateAI`. Uses VM Berserk behavior, not MC's low-health cooldown removal. |
| Hakkar power and siphon | Initial Double Attack and one power stack per undefeated priest; priest completion removes one stack with aura amount recalculation; poisoned players cast 24323 back at Hakkar, others 24322. | MC `src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/zulgurub/boss_hakkar.cpp`, `Reset`, `InitiateHakkarPowerStacks`, `BloodSiphon::OnEffectExecute`, `HakkarPowerDown::OnEffectExecute`; `zulgurub.cpp`, `OnCreatureCreate`, `SetData`, `RemoveHakkarPowerStack`. Initialization and power-down use triggered casts to apply all stacks reliably within one world turn. |
| ZG state integration | Preserve the existing eight-slot save and Ohgan handling; record five priest states and their real deaths, idempotently reducing Hakkar power. No invented Hakkar encounter slot. | MC `zulgurub.h` type ordering and `zulgurub.cpp` methods above. Map kill notifications adapt the unported priest AIs' `JustDied -> SetData(DONE)` path; this does not supply their combat mechanics. |
| AI selection and timers | Raid entry scripts selected before template AIName, restricted to the corresponding instance script; explicitly registered map AIs retain precedence; charmed units/controlled pets excluded. World-diff timers retry unsuccessful casts and reset per pull. | VM `src/game/AI/CreatureAISelector.cpp`, `selectAI`; MC `src/game/AI/ScriptDevAI/base/CombatAI.cpp`, combat-action dispatch/reset. |

## Files

New implementation files are under `src/ArcaneCore.Game/Instances/Scripts/`:

- `BlackwingLair/`: `BlackwingLairInstance.cs`, `BroodlordAI.cs`, `DrakeAI.cs`, `ShadowFlameScript.cs`.
- `RuinsOfAhnQiraj/`: `RuinsOfAhnQirajInstance.cs`, `KurinnaxxAI.cs`, `KurinnaxxSandTrapScript.cs`.
- `ZulGurub/`: `HakkarAI.cs`, `HakkarSpellScripts.cs`, `ZulGurubPriestState.cs` (partial of the existing Classic namespace instance).
- `Raids/RaidBossAI.cs`: shared timer/lifecycle adapter and entry factory.

Small shared edits: `Creatures/CreatureMapSystem.Host.cs` (factory call),
`GameObjects/GameObjectMapSystem.Types.cs` (validated scripted trap-use entry point),
`Instances/Scripts/Classic/ZulGurubInstance.cs` (partial class and priest-state delegation),
new `Spells/SpellSystem.ScriptAuraStacks.cs` (normal one-stack removal; kept out of the class-scripts lane's `ScriptSeams.cs`),
`Spells/SpellSystem.Objects.cs` (object destination).

Tests: new `tests/ArcaneCore.Game.Tests/Instances/RaidBossScriptTests.cs`; updated registry expectation in
`InstanceScriptTests.cs` to include maps 469 and 509. Area documentation links this report.

## Validation

RAM preflight used Windows `GlobalMemoryStatusEx` because the prescribed `Get-CimInstance` call was denied by the sandbox.
Every build was serialized and started only with at least 3 GiB free RAM. A 2.67 GiB reading deferred a build.

Red evidence:

- Original production code: 8 new raid tests failed (maps 469/509 lacked instance scripts), after a clean build.
- Shadow Flame addition: 1 failed / 12 passed; unprotected target lacked 22682 before the script.
- Hakkar addition: 4 failed / 13 passed before its AI and spell scripts.
- Object destination regression, with explicit hostile test relations and successful trap cast: player remained at 60 health instead of 53 before the correction (`raid-tests-target-red.log`). An earlier draft of this test lacked hostile relations; it is not the regression proof.

Final verification (local evidence retained in ignored `artifacts/raid-bwl-zg-aq20/`):

| Command | Result | Evidence |
|---|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | Success; 0 warnings, 0 errors; 1:48.56 | `raid-build-final.log` |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter 'FullyQualifiedName~RaidBossScriptTests\|FullyQualifiedName~InstanceScriptTests\|FullyQualifiedName~GameObjectCastTests'` | 40 passed, 0 failed, 0 skipped | `raid-tests-focused.log` |
| `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build` | 7,097 passed, 0 failed, 11 skipped; 7,108 total | `raid-tests-game-final.log` |
| `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build` | 2,522 passed, 0 failed, 6 skipped; 2,528 total | `raid-tests-world-full.log` |
| `git diff --check` | Passed for tracked changes | Final working-tree check |

`RaidBossScriptTests` adds 19 cases covering AI selection and kill-state transitions, timer boundaries, threat reduction, enrage/reset,
platform leash, door persistence/idempotence, trap creation/activation/charge removal/missing content, actual trap-centered damage,
Shadow Flame cloak protection, Hakkar priest aspects/power stack removal, Insanity threat restoration and both siphon spell choices.

The first full Game run had 7,096 passed / 1 failed / 11 skipped: the hygiene scanner tried to read its own exclusively opened root-level
log (`raid-tests-game-full.log`). Moving all generated evidence to the already-ignored `artifacts/` directory fixed the test environment;
no production code or test assertion was changed for that rerun. The final full run above passed. Skips are environment-gated tests.

Tests advance world/AI/object clocks directly; no wall-clock waits or quiet-window assertions. They use synthetic spell effects and world
objects, with content IDs/layouts checked against references. No multiplayer playerbot scenario or real-client raid clear was run.

## Content and limits

A read-only streaming probe of `D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz` confirmed:

- All five creature templates: 12017, 11983, 11981, 15348, 14834.
- Door 179365 and Sand Trap 180647. The trap has radius 0, spell 25656 and one charge; timed activation is necessary.
- Texts 9967, 9968, 1191, 2384, 11720 and 10447.
- All 31 referenced spell IDs. Spell 26524 has `MaxAffectedTargets=1`; 25656 uses destination-area targeting, damage,
  hit-chance reduction and silence effects. No replacement rows or schema versions were invented.

These rows must be present in the operator's imported runtime stores. The probe does not establish their live import or prove
all original spell effects through a client. `Spell.dbc` was absent from the supplied effective-DBC directory; spell shape checks used
ClassicDB and MC `sql/base/dbc/original_data/Spell.sql` instead. Game spell/aura, charm, melee, visibility, movement and text behavior
continues to use the existing engine. Sound playback and `%s` formatting remain whatever the existing text host provides.

Instance save strings still have the pre-existing restart-persistence limitation described in wave 4; no characters schema version
was allocated. Hakkar has no additional saved state in the retained MC ZG layout. Ossirian's announcement needs his creature loaded.
No live data was written and no live server ports were used.

## Remaining

- **BWL:** Razorgore/eggs/orb/defenders; Vaelastrasz dialogue and Burning Adrenaline; Ebonroc; Chromaggus breaths/afflictions;
  Victor Nefarius, Nefarian's class calls, drakonids and bone constructs; their instance gates, random selections and quest events.
- **ZG:** Jeklik, Venoxis, Marli, Thekal/Lor'khan/Zath and Arlokk combat AIs and event objects; Jin'do; Mandokir/Ohgan/spirit behavior;
  Gahz'ranka/fishing summon; Edge of Madness bosses; area-trigger dialogue. Priest death bookkeeping alone is not a priest boss port.
- **AQ20:** Andorov and Rajaxx waves; Moam; Buru/eggs; Ayamiss; Ossirian crystals/vulnerabilities; remaining instance events and summons.
- Full raid progression, playerbot multiplayer scenarios, original-spell end-to-end acceptance and real-client testing remain unproven.

Do not describe the three maps as fully playable raids based on this diff.
