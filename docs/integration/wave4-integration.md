# Wave 4 integration (claude/vw5-integration)

Base: `origin/main` 7313b9e. Every branch below was merged with `git merge --no-ff`, one at a time, in this order. Nothing was merged
that was not a local branch with commits; no branch was empty or missing.

## What merged

| # | Branch | Merged | Notes |
|---|---|---|---|
| 1 | claude/fix-auction-recovery-wait | yes | test-only, no conflict |
| 2 | claude/fix-vendor-price-rounding | yes | no conflict |
| 3 | claude/fix-synthetic-startup-timeout | yes | startup wait 60 s |
| 4 | claude/cx-protocol-fixes | yes | AUTH_OK 10 bytes, SPELL_DELAYED full guid |
| 5 | claude/cx-sec-fixes | yes | see "RED proof" below |
| 6 | claude/cx-mounts-riding | yes | |
| 7 | claude/cx-stealth-detection | yes | |
| 8 | claude/cx-vendor-trainer-fidelity | yes | price rounding duplicate resolved to fix-vendor-price-rounding |
| 9 | claude/cx-raid-groups | yes | |
| 10 | claude/cx-taxi-fidelity | yes | |
| 11 | vw5-spell-modifier-engine | yes | |
| 12 | vw5-druid-forms | yes | uncommitted worktree edits committed first (c45ede9): `Forms:ResetFistAttackTimeOnFormLoss` default false |
| 13 | vw5-ranged-combat | yes | |
| 14 | vw5-crafting-professions | yes | |
| 15 | vw5-reputation-factions | yes | |
| 16 | vw5-honor-pvp-ranks | yes | worktree was clean at merge time |
| 17 | vw5-battlegrounds | yes | pure code (state machine, packets, queue); nothing constructs the manager yet |
| 18 | vw5-creature-movement-spawns | yes | |
| 19 | vw5-game-events-weather | yes | worktree was clean at merge time |
| 20 | vw5-quests-advanced | yes | |
| 21 | vw5-warlock-mage-utility | yes | invisibility part dropped (duplicate of the stealth lane) |
| 22 | vw5-aura-engine-completeness | yes | regen part unified |
| 23 | vw5-threat-and-aggro | yes | |
| 24 | vw5-graveyards-resurrection | yes | |
| 25 | vw5-hot-reload-everywhere | yes | |
| 26 | vw5-docs-wiki | yes | |

Integration-only commits on top: a LifePersistence test fix, the reputation x taxi test adaptation, the SpellScriptTests base case, the aura
support matrix rows, and the consistency commit (reload membership, config catalog, doc keys, regenerated `docs/reference/*`).

## Schema renumbering

Main tips were Characters 20 (petitions), World 20 (start actions), Auth 3. Auth did not change. Every component is contiguous and unique
(`DataModules.Compose` throws on a gap or duplicate; `IntegratedSchemaTests` lists every module and passes). Each new Characters module
implements `ICharacterDataCleanup`.

| Component | New version | Module | Lane's own number |
|---|---|---|---|
| Characters | 21 | CharacterBankSlotsDataModule (bank_bag_slots) | 21 (vendor-trainer) |
| Characters | 22 | CharacterTaxiFlightDataModule | 21 (taxi) |
| Characters | 23 | CharacterHonorDataModule | 21 |
| Characters | 24 | CreatureRespawnDataModule | 21 |
| Characters | 25 | GameEventStatusDataModule | 21 |
| World | 21 | CreatureNpcMetadataDataModule (vendor-trainer) | 21 |
| World | 22 | ReputationTemplatesWorldModule | 21 |
| World | 23 | CreatureMovementTemplateDataModule | 21 |
| World | 24 | CreatureSpawnEntryDataModule | 22 |
| World | 25 | GameEventDataModule | 21 |
| World | 26 | QuestAdvancedWorldModule | 21 |
| World | 27 | SpellThreatDataModule | 21 |
| World | 28 | GraveyardDataModule | 21 |

Tips after integration: **Characters 25, World 28, Auth 3**. The lane docs that quoted the old numbers were updated.

## Conflicts and resolutions

* **Price rounding (three implementations)**: fix-vendor-price-rounding, vendor-trainer-fidelity and the reputation lane's
  `ReputationPricing.Round`. All are `uint32(price * discount + 0.5f)` in single precision. Kept `ReputationPricing.Round` as the one helper
  (vendor `Discounted`, repair, taxi legs); the vendor tests of both sides are kept.
* **Taxi**: taxi-fidelity (resumable flights, per-leg charge) x mounts-riding (remove a spell mount before a scripted flight) x
  reputation (per-leg rounding) x honor (`PriceDiscount(..., taxi: true)`). Composed: mount removal runs after the first-leg charge,
  per-leg `legCosts` come from `ReputationPricing.Round`, the discount for the route uses the honor-aware overload. The reputation test that
  assumed all legs are charged at once now follows the per-leg flow (95 on launch, 52 at the hop change).
* **Invisibility (auras 18 and 19)**: both the stealth lane and the warlock-mage lane registered handlers (a duplicate registration throws at
  startup). Kept the stealth lane's (`InvisibilityAuras`, `StealthVisibilityRule.CanDetect`, `InvisibilityTests`). Dropped the warlock lane's
  `InvisibilityVisibilityRule`, `InvisibilityFeature` and its 12 tests; docs/areas/warlock-mage-utility.md says so.
* **Regeneration auras**: warlock-mage (`RegenModifiers`, includes polymorph and Demon Armor) and aura-engine (`RegenAuraRules`) both rewrote the
  player regen tick. Kept `RegenModifiers`; `IPowerAuraSource.GetAuras` (the aura lane's primitive) is now the one primitive and the
  other members are derived from it by default; an amplitude of 0 on a MOD_REGEN aura means 5000 ms (the aura lane's rule, vmangos
  HandleModRegen). `RegenAuraRules` was removed and its two direct tests re-expressed on `RegenModifiers`.
* **Aura remove mode**: `SpellAuraHolder.RemoveMode` (aura lane) supersedes the warlock lane's `RemovedByDeath` setter, which is now derived.
* **SpellInfo.IsPositive**: aura lane's exact `IsPositiveSpell` port replaces the heuristic (it contains the crafting lane's
  bandage/shield/mount MechanicImmunity rule).
* **ScriptEffect**: the druid lane's built-in `ScriptEffectModule` and the warlock lane's `SpellScriptDispatcher` compose (the dispatcher chains
  the previous handler). `SpellScriptTests` base case changed: a plain spell system no longer reports SCRIPT_EFFECT as unimplemented.
* **Spell casting (Prepare/AddCooldown/cast)**: ranged auto-repeat (`autoRepeatShot`), crafting (`castItem`, item cooldown category), spell
  mods (`ModScope`, cooldown mod) all compose; `AddCooldown` uses the picked item cooldown's category with the cooldown spell mod applied.
* **ReputationChanged**: reputation lane and quests-advanced both implemented `QuestNpcServices.ReputationChanged`; kept the quests-advanced
  (status filtered) version. The reputation feature calls it directly; the quests lane's `QuestReputationBinding` finds no
  `IReputationChangeSource` and logs one information line (see open questions).
* **Evade health snap**: creature-movement-spawns (`Creatures:Movement:EvadeRestoresFullHealth`) and threat (a top-level duplicate)
  added the same switch. One switch remains: `Creatures:Movement:EvadeRestoresFullHealth` (default false = retail). The threat lane's
  `EvadeResetsAuras` stays.
* **Threat**: the threat lane's `ThreatCalc` is used; its SPELLMOD_THREAT step now goes through the mod engine's `ModFloat` (charges).
  Stealth detection in aggro uses the stealth lane's `ICreatureVisibility`.
* **Spawns**: the game-event spawn gate runs first in `LoadSpawns`, then the spawn-entry alternatives.
* **Importer CLI**: game-event, world-state and graveyard importers all run (each reads the input dumps by column name).
* **Quest store**: `All` (existing) and `Templates` (hot reload) both kept.
* **Consistency tests that the lanes could not know about**: aura support matrix rows (15 aura types are now handlers), reload membership
  (`spell_threats` is in vmangos `reload all`; `creature_onkill_reputation`, `game_weather`, `reputation_reward_rate`,
  `reputation_spillover_template` are not, so `IncludedInAll` is false), config catalog summaries for `World:Honor:*`, doc key allow-list
  entries for keys read straight from `IConfiguration`, and the regenerated `docs/reference/{configuration,gm-commands,schema}.md`.

## Aura registration and crit mods

No aura type is registered twice (`SpellSystem.RegisterModules` would throw; the full World and Game suites compose every module and pass).
Aura 52 (MOD_CRIT_PERCENT) is registered by nobody: the aura lane keeps it "Referenced" (spell crit reads the total through
`GetTotalAuraModifier`, so spell crit is wired once). The character-sheet and melee/ranged crit field (`PlayerStatSystem.UpdateCritPercentage`
passes `flatMod = 0`) is still not wired; retail needs the weapon-dependent `_ApplyWeaponDependentAuraCritMod` path, which belongs to the
unscheduled aura-stat ledger. Open item.

## RED proof of the cx-sec-fixes tests (mutate, run, restore)

| Test | Production change reverted | Result |
|---|---|---|
| Load_CapsAnOversizedPersistedSignatureList_AtTheClientMaximum_AndPersistsTheTrim | the `Signatures.Count >= ClientMaxSignatures` clause removed | failed (1 of 1), passes restored |
| InsertMail_WithRecipientCap_RechecksTheBoxInsideTheCommit (SQLite) | commit-time recount disabled (`RecipientCap > 100000000`) | failed (1 of 1), passes restored |
| BanList_StopsAtMaxListedEntries_AndSaysSo | `Capped` never truncates | failed (1 of 1), passes restored |

At integration, setting the `MaxListedEntries` default to 0 did **not** fail the ban test (the test set the option to 2 itself) and only `.banlist ip` was covered. The release-review fixes pin the default (200) with a default-host test and add cap tests for `.baninfo account`, `.banlist account` and `.banlist character`. The mail test ran on SQLite only.

## Deviations (from retail or vmangos) found across the lanes

Config-gated and default-off or retail: `Forms:ResetFistAttackTimeOnFormLoss` (off), `Creatures:Movement:EvadeRestoresFullHealth` (off),
`Combat:CastingConsumesSwing` (off), `Combat:CastResetsMeleeSwing` (retail on), honor `MaintenanceMode` Startup (Live is opt-in),
`Reputation:SendForcedReactions` (off), `World:GameEvents:ManualStartLengthUnit` (Seconds), `Creatures:ImplicitEventAi` (off), `Auras:ProcEngineBreaksDamageAuras` (off: no proc engine exists; Wyvern Sting and Prowl are exempted by spell id instead).

Not switchable (flagged for a decision; none is default-on non-retail by intent, but these are unswitched deviations):

* Pet and totem casts never spend the owner's spell-mod charges (retail leaves a stuck -1 mod); `PassiveReapply` depth cap of 2.
* `EffectHealthLeech` and the periodic leech tick treat an unset multiple value as 1.0 (vmangos heals 0).
* `SpellInfo.GetCastTime` ignores the all-zero cast time row, so Throw and Multi-Shot miss the +500 ms ranged slot time.
* With `Auras:ProcEngineBreaksDamageAuras=false` (no proc engine exists) the damage break still removes auras that carry proc flags so Polymorph, Sap, Gouge and Freezing Trap stay breakable; Wyvern Sting and Prowl (`SpellSystem.DamageBreakExemptSpells`) are spared, as vmangos does through `checkProcFlags`. Set the key once a proc engine exists.
* Enchant spells fail closed with no `SpellItemEnchantment.dbc` or `Enchanting:Enabled=false` (no retail counterpart; safe direction).
* Reputation: hardened packet length checks, kill credit by killer's group (no loot-tap primitive), forced reactions not sent by default.
* Honor: tap model simplified, Live mode limits (documented in docs/areas/honor.md).
* Battlegrounds: `SMSG_BATTLEFIELD_STATUS` status is a u32 (vmangos and mangos-classic) while wow_messages says u8; no winner for a premature
  finish with no winner; only the code half is delivered (S3, S5, S6, S7 not built).
* Combat: float32 attack time truncation reproduced on purpose.

Config-defaulted-on behaviours worth a second look because their default is "on": `Creatures:Respawn:DrawDelayAtLoad` (true),
`Creatures:Respawn:AlternateEntries` (true), `Creatures:Respawn:Persist` (true). The lane documents them as the retail behaviours.

## Open questions (lanes' plus integration)

* Aura 52 crit percent on the character sheet and weapon-dependent crit mods (above).
* `QuestReputationBinding` logs "No reputation change source is registered" at startup because the reputation lane calls the quest service
  directly; either register `ReputationService` as the source and drop the direct call, or remove the binding.
* Per-entry `.reload creature_template <entry>` is not supported (no entry-aware contract on `IContentReloadable`).
* Spell mods: should pet/totem casts spend charges, should `PassiveReapply` be uncapped, should the leech multiplier follow vmangos?
* Ranged: SpellRange.dbc numbers unverified, projectile flight time (S09) not built, ON_EQUIP engine owner, real-client playtest.
* Battlegrounds S3/S5/S6/S7 need the integrator's schema numbers (now free: Characters 26+, World 29+) and a composite map resolver.
* Reputation: wire widths (u16 versus u32 slots, forced reactions) need a client capture; forced reactions across login unproven.
* Graveyards: durability exemption `NO_DURABILITY_LOSS` not modelled; resurrection request packet layout vs real client unverified.
* Warlock-mage: `PaysPerSecondCost` is a heuristic verified on Health Funnel only. (The dropped invisibility rule's group-mate and Hunter's Mark clauses were compared against `StealthVisibilityRule` at release review and are present; its owner/charmer clause was missing and is now restored.)
* Aura lane: real Spell.dbc flags of Polymorph, Sap, Gouge, Freezing Trap, Wyvern Sting, Prowl need checking.
* Hot reload: `LootService.Content`/`Generator` are plain fields swapped on the world thread; make them volatile if a session thread reads them.
* Threat: `Player.SetGameMaster` does not call `CombatStopWithPets`; no taxi producer for the offline threat state.
* druid-forms: the Transform guard in shapeshift display code has no test.

## Release notes

* **Command abbreviations changed.** The chat command table resolves an abbreviation to the first command (in table order) that starts with it.
  The new top-level names `.event`, `.honor`, `.character`, `.deplenish`, `.replenish`, `.neargrave` and `.wchange` take over short prefixes that used
  to reach other commands: `.e` now means `.event` (it was `.explorecheat`), `.h` now means `.honor` (it was `.help`), and `.n` / `.ne` resolve differently
  too. Use the full command name in scripts and macros. vmangos resolves abbreviations by the same rule.
* **`.reload` abbreviations became ambiguous.** `.reload spell` (`spell_template` and `spell_threats`) and `.reload creature` (`creature_template`,
  `creature_loot_template`, `creature_onkill_reputation`) now match more than one reloadable and are refused rather than guessed; type enough of the
  name (`.reload spell_te`, `.reload creature_te`).
* **Developer databases that applied a lane branch will not match.** Five lanes (taxi, honor, creature respawn, game events, reputation templates)
  each allocated schema version 21 for their own tables; the integrated numbers are Characters 21-25 and World 21-28 (table above). A database that
  applied one of those lane branches records 21 for a different step and will not line up. Recreate it, or run the upgrader against a copy and review
  the drift report. A release database upgraded from main (Characters 20, World 20) is not affected.
* **New switch:** `Auras:ProcEngineBreaksDamageAuras` (default false). **New GM-visible default:** `Bans:MaxListedEntries` = 200 (0 restores the
  unbounded retail listing).

## Verification (local, Release, through the throttle `dn.ps1`)

Build: 0 warnings, 0 errors (`ArcaneCore.slnx`, `-m:1`).

Final head, one pass per project, no failures:

| Project | Total | Passed | Skipped |
|---|---|---|---|
| Cryptography.Tests | 8017 | 8017 | 0 |
| Data.Tests | 934 | 925 | 9 (environment-gated real-data probes, see below) |
| Game.Tests | 5347 | 5347 | 0 |
| MockClient.Tests | 196 | 196 | 0 |
| Realm.Tests | 37 | 37 | 0 |
| World.Tests | 1684 | 1683 | 1 |

Data.Tests skips are all environment-gated facts, none is provider-only when a MariaDB or PostgreSQL server is configured. Eight need the classic-db dump, under
three different variable names (`ARCANECORE_CLASSICDB_DUMP` for `CreatureMovementTemplateTests`, `CreatureBehaviourImportTests`, `GameObjectSpawnDataTests` and
`CreatureSpawnEntryTests`; `ARCANE_CLASSICDB_DUMP`, the spelling of `ClassicDbDumpFactAttribute` in `tests/ArcaneCore.Data.Tests/WorldState`, which falls back to
`D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz` when unset, for `WeatherImportCliTests`, `GameEventImporterTests` and `WorldStateDataTests`;
`ARCANECORE_CLASSIC_DB` for `TotemSpellDataTests`) and four need a build-5875 DBC directory (`ARCANECORE_TEST_DBC_DIR`: `SkillDbcReaderTests`,
`ShapeshiftFormDbcTests`, `EnchantDbcReaderTests`, and `CharacterAppearanceDbcReaderTests` from the wave-4 optional-dbcs lane). `DataTestsSkipAttributionTests` checks this paragraph against the attributes. One more, the read-committed bid/deletion theory in
`EconomyCharacterDeletionRaceTests`, skips only when neither `ARCANECORE_TEST_MARIADB` nor `ARCANECORE_TEST_POSTGRES` is set. With no variables set a run skips 13 (12 before the optional-dbcs lane); with the
two database variables and no dump or DBC (the CI configuration in `.github/workflows/ci.yml`) it skips 12 (11 before). The 9 above is neither, so some of the dump or DBC variables
were set on the machine that produced it; which ones is UNVERIFIED (the run was not logged). The CI skip count was not read from a CI log.

Resilience theory timing (`SchemaStartupResilienceTests.InterruptedFreshCreate_ResumesOnRestart`, all three providers, the one filter, Debug, `--no-build`,
MariaDB 10.11 and PostgreSQL 16 on the same 4-CPU Linux box, which other jobs were loading at 3-7 the whole time, so every number below is noisy). Before
(serial faults, every database kept until the class ends) versus after (`ARCANECORE_TEST_FAULT_PARALLELISM` 4, each database dropped when its fault is
checked), per case:

| Case | Before | After |
|---|---|---|
| MariaDB world | 48 s, 1 m 18 s | 52 s, 28 s |
| MariaDB characters | 20 s, 28 s | 31 s |
| PostgreSQL world | 1 m 12 s, 1 m 28 s | 43 s |
| PostgreSQL characters | 36 s, 37 s | 29 s |
| SQLite world | 13 s, 12 s | 18 s |
| SQLite characters | 3 s, 4 s | 8 s, 7 s |
| Whole filter, wall | 243 s (includes a first build), 293 s | 196 s |

Two "before" runs; one complete "after" run plus the first cases of a second that had not finished when this was written (`ARCANECORE_TEST_FAULT_PARALLELISM`
is new; both "after" runs are the branch at its default of 4). The gain is real on
PostgreSQL, unclear on MariaDB (52 s then 28 s against 48 s and 78 s; the machine's load moves these numbers more than the change does) and negative on
SQLite (four writers of four files on one disk). The cost that dominates is unchanged: a fault after DDL #k replays k
statements, then the restart plans and replays the rest, so a fresh create with N DDL statements costs about N squared statements per provider; parallelism
only overlaps the round trips. The full class still takes many minutes with both servers configured. Measurements of other widths were started but not
finished before this was written; they are UNVERIFIED and the default stays 4.

The `FATAL:  database "arcane_t_..." does not exist` lines in the PostgreSQL service-container log on CI are not failures. Every database this project
creates is named `arcane_t_` plus 16 hex digits, and the bootstrapper (`SchemaBootstrapper.EnsureAsync`) asks EF's relational database creator whether the
database exists before creating it; on PostgreSQL that probe connects to the named database, the server refuses the connection with SQLSTATE 3D000 and
logs it at FATAL severity, and the creator reports "does not exist". One line per probe: the first bootstrap of every test database, the explicit
`ExistsAsync` of `ExistingEmptyDatabase_StartsLikeAFreshOne`, and `EnsureDeletedAsync` when a database is dropped. On this machine the local server log
held 5597 such lines after the Data test runs, every one of them for an `arcane_t_` database and none at another severity. A test run with no other
output from the container is healthy; a line naming another database would not be.

MockClient self-test: outcome passed, `checkCount` 59, 59 checks passed, 0 failed.

Flakes and failures seen during integration (every attempt):

* `LifePersistenceTests.HealthPowerAndExperience_SurviveARelog` failed once in a full World run (rage 37 decays to 0 during a slow relog; it
  depends on out-of-combat decay timing). Alone x3: 5 of 5 each. Fixed (stored rage 900); the same flake was reported by the graveyards lane.
* `QuestInteractionWorldTests.SocketInvalidInteractions_CannotAcceptOrReadDetails(guard: "visibility")` failed once in a full World run
  (the test removes the creature from `VisibleObjects` and the map's own visibility tick can re-add it before the requests are processed).
  Alone x3: 30 of 30 each. **Not fixed** (a race between the test and the map tick); it is pre-existing test design, not a product defect.
* No MockClient or AuctionRecovery timeout occurred in the final full runs (the 60 s startup wait from fix-synthetic-startup-timeout is in).
  The lanes' reports of 10-20 s MockClient timeouts under load were not reproduced here, so nothing was masked and nothing was proven about them.
* Intermediate failures that were integration defects, not flakes, all fixed: `SpellScriptTests` base case, aura support matrix (3 rounds),
  `NpcTravelServiceTests` per-leg rounding, reload membership, doc/config/schema golden pages.

Not verified locally: MariaDB and PostgreSQL provider theories (they run only on hosted CI; locally SQLite only, 9 Data tests skipped).
No real-client playtest. Static reasoning is not claimed to be equivalent to the tests above.
