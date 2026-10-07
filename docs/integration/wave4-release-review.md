# Wave 4 release review (claude/vw5-integration, head 7bcbc95 vs origin/main 7313b9e)

Read-only review. Nothing was built, run or edited except this file. Every finding is from reading the merged tree and, where noted, the vmangos
sources under D:\refs\vmangos. Provider (MariaDB / PostgreSQL) behaviour is judged from the code and EF/driver semantics, not exercised.
"Static reasoning" below is never claimed to equal a compiler or test result.

## Verdict: APPROVE WITH FIXES

No blocker. The integrator's resolutions of the duplicate/overlap cases are sound, the schema numbering is contiguous and unique, the security
posture (HotCode guard, `HotReload:Commands`, ban and inbound paths, the five sec-fixes) is unchanged, and the dead battleground half is truly
unreachable. Two items should be fixed or corrected before release (F1, F2); the rest are low severity.

## What was verified clean

**Duplicate / overlap resolutions**
- **Invisibility 18/19.** Only `StealthFeature` (`src/ArcaneCore.World/Stealth/StealthFeature.cs:37-38`) registers `ModInvisibility` and
  `ModInvisibilityDetection`; `InvisibilityVisibilityRule`, `InvisibilityFeature` and the warlock lane's duplicate tests are gone from the tree
  (grep of src and tests). `CanDetect` (`Stealth/InvisibilityAuras.cs:63-101`) is the same mask / level algorithm as the dropped rule plus the
  world-boss clause the dropped rule lacked. The dropped rule's Hunter's Mark clause and the non-hostile group-mate clause (same group, raid or
  team by `GroupVisibilityMode`) are both present in `StealthVisibilityRule.CanSee` (`:57-68`). One clause was lost, see F5.
- **Regen auras.** `RegenAuraRules` is gone; `RegenModifiers` (`Combat/Power/RegenModifiers.cs`) is the single tick function; the amplitude-0 means
  5000 ms rule is in `HealthPerTick` (`:68-75`, vmangos HandleModRegen). `IPowerAuraSource.GetAuras` is the primitive (`Combat/Power/CombatOptions.cs:107`)
  and `SpellSystemPowerAuras` overrides `GetAuras`, `GetTotalAuraModifier(ByMisc)`, `GetRegenAuras` and `IsPolymorphed` (`:293-390`), so the
  default-interface fallbacks (`=> []`) are never what production uses. `RegenAuraTests` drives the real 2 s tick through `FakeAuras`.
- **Evade.** One switch, `Creatures:Movement:EvadeRestoresFullHealth`, default false (`CreatureOptions.Movement.cs:53`); the old threat-lane
  top-level key is gone from src, docs and appsettings. `CreatureMapSystem.Evade.cs:53-60` honours it; `EvadeHealthTests` and
  `EvadeFidelityTests` assert both directions on the real regen tick.
- **ReputationChanged.** One implementation (`Npc/QuestNpcServices.Reputation.cs:14`), called directly by `ReputationFeature.OnReputationChanged`
  (`World/Reputation/ReputationFeature.cs:178-179`). `ReputationService` does not implement `IReputationChangeSource`, so `QuestReputationBinding`
  never subscribes and cannot double-deliver; the only effect is the one startup information line the integrator already noted.
- **Price rounding.** `ReputationPricing.Round` (`Reputation/ReputationPricing.cs:11-19`) is `(uint)((float)price * discount + 0.5f)`, clamped.
  Its callers are every discounted amount: vendor list (`QuestNpcServices.Vendor.cs:297`), vendor buy (`:118`, `BuyPrice * count`, vmangos
  Player.cpp:18445 multiplies before the discount), repair (`InventoryItemService.cs:105`), trainer list and buy (`Trainer.cs:59,110`), each taxi leg
  (`Travel.cs:194`). I compared each with vmangos (ItemHandler.cpp:763, NPCHandler.cpp:309, Player.cpp:17977/17997/18445): same rounding, same
  per-amount (never per-sum) placement. `HonorPriceDiscount.Apply` reproduces the step-by-step float subtraction of
  `Player::GetReputationPriceDiscount` (Player.cpp:19470-19510), including the taxi 0.05 / 0.05 branches and the ten-faction list.
- **Aura 52.** Registered by nobody and "Referenced" in `AuraSupportBaseline.cs:63`; spell crit reads `GetTotalAuraModifier(ModCritPercent)`
  once (`SpellCombatRules.cs:335`). No double counting. The melee/ranged character-sheet gap is the integrator's documented open item.
- **Aura / effect double registration.** I enumerated every `RegisterAura(AuraType.X` and `RegisterEffect(SpellEffectName.X` literal in src. The only
  repeated aura types are `TrackCreatures` and `TrackResources` (`Ranged/RangedHandlers.cs:12-13` and `Spells/Auras/VisualAuras.cs:22-24`), both pre-existing
  on main. `RegisterAura`/`RegisterEffect` replace silently (`SpellSystem.Auras.cs:27`); only `RegisterModules` throws, and only for module-to-module
  replacement, so a feature-level duplicate would not have been caught by it. There is none.
- **Reload names.** No two `IContentReloadable.Name` values collide; `ReloadAllMembershipTests` pins membership name by name.

**Schema**
- Characters 21 bank, 22 taxi flight, 23 honor, 24 creature respawn, 25 game event status; World 21 NPC metadata, 22 reputation templates,
  23 movement template, 24 spawn entry, 25 game event, 26 quest advanced, 27 spell threat, 28 graveyard; Auth 3 unchanged. Read from the `Version`
  constants. `IntegratedSchemaTests.FeatureModules_HaveAssignedVersions_AndDistinctTables` lists every module, asserts `Range(2, n)` steps, that the
  inline repair versions are not module versions and that CreateTable names are unique per component.
- `ICharacterDataCleanup`: all five new Characters modules implement it (bank no-op because the column dies with the row; respawn and game event
  status hold no per-character rows; taxi and honor delete). World modules hold no per-character rows.
- Provider semantics. The bootstrapper (`SchemaBootstrapper.ExecuteAsync`, `:344-410`) is per-statement, catalog-checked and re-runnable, so the
  MariaDB implicit-commit hazard is covered for every new `CreateTableChange` / `AddColumnChange`; there is no transactional DDL assumption in any
  new module. All new keys are numeric (no `longtext` key on MariaDB). The only new indexes are the two honor ones, default-named
  `IX_character_honor_cp_character_id_date` (39 chars) and `IX_character_honor_cp_date`, well under 63/64. No new retry strategy, so the
  `BeginTransactionAsync` calls in `EfCreatureRespawnStore`, `EfHonorStore` and `GameEventStatusDataModule` are legal. `ExecuteDelete` with a
  correlated `NOT EXISTS` on another table (`EfCreatureRespawnStore.cs:25-26`) is fine on MariaDB (restriction is same-table only). Chunked
  `Contains` lists in the honor store avoid parameter-limit problems.

**Security**
- `HotCodeGuard`, `HotCode*`, `HotReloadOptions` (`Commands` false, `HotReloadOptions.cs:21`), `ReloadCommands.cs`, `ReloadPolicy.cs`, `ReloadCoordinator.cs`
  and `Program.cs` are byte-identical to main; `appsettings.json` ships `HotReload:Commands=false`, `HotCode:Enabled=false`. **The hot-reload-everywhere
  lane in this tree contains no file watcher and no path input** (`FileSystemWatcher` appears nowhere in src); it adds DB-backed reloadables only. The
  one new switch, `IOptionalReloadable` (`Game/Reload/IOptionalReloadable.cs`), leaves `.reload game_event` unregistered unless
  `World:GameEvents:AllowReload` (default false) is set. Reloadables read the database through registered stores, never a user path.
- Sec-fixes: no src file touched by the five cx-sec-fixes commits (LootGenerator, GameObjectChairs, PetitionManager, EfEconomyStore, EconomyFeature.Mail,
  BanCommands/BanOptions/BanCommandText) has changed since those commits (`git diff <commit> HEAD -- file` empty for each). Still effective.
- `WorldSession.cs` has a single 7-line change (AUTH_OK is ten bytes), matching vmangos World.cpp:324-333; the handshake test pins the exact bytes.
  Pre-auth ban, status and IP checks are untouched.
- New inbound opcodes: CMSG_USE_ITEM, CMSG_INSPECT, MSG_INSPECT_HONOR_STATS, CMSG_RESURRECT_RESPONSE, CMSG_SELF_RES, CMSG_PUSHQUESTTOPARTY,
  CMSG_QUEST_CONFIRM_ACCEPT, MSG_QUEST_PUSH_RESULT, CMSG_CANCEL_AUTO_REPEAT_SPELL. Each is registered once (no `OpcodeTable` duplicate). Inspect
  follows vmangos MiscHandler.cpp:943-975 line for line (distance, `IsValidAttackTarget`, selection set first). Quest share validates exact payload
  lengths. CMSG_USE_ITEM validates item presence, spell index, trigger type, equip-only, `CanUseItem`, trade window, combat, shapeshift; a short or
  over-reading payload is the same pattern as CMSG_CAST_SPELL. Taxi express caps the node count and checks `payload.Length == 16 + 4*count`
  (`NpcServiceHandlers.cs:170-174`).
- GM command levels (generated `docs/reference/gm-commands.md`): all state-changing new commands are Administrator (`.event start|stop|enable|disable`,
  `.honor add|addkill|setrp|reset`, `.modify honor|rep`) or GameMaster (`.deplenish`, `.replenish`, `.neargrave`, `.revive`, `.gocorpse`); read-only ones
  are lower. `.reload` and `.hotcode` rows are "development only" and absent from the default table. No exact-name collision: no new top-level name is
  a longer name that precedes an existing shorter exact command in table order (checked against `CommandTable.Find`, first-prefix-wins).
- Battlegrounds: no type in `src/ArcaneCore.World`, `.Protocol` or `.Kernel` references `Battleground*` apart from chat enums and the
  `.go` "cannot teleport to a battleground map" refusal; nothing constructs `BattlegroundManager`, no handler is registered, no options section is bound.
  Dead code, unreachable from a client, cannot throw at startup. `IsBattleground` map checks in `MapCombat.DeathEffects.cs:23` and
  `GraveyardRepopService.cs:109` read `MapType` only.
- Scratch / secrets: no non-src/tests/docs/tools files added; no `.log/.tmp/.bak/.dll/.db`; no `NotImplementedException`, `TODO`, `FIXME`, credential or
  `Console.Write` in added lines. `Auth:AutocreateAccounts` appears only in the generated config catalog, default false (`AuthOptions.cs:19`).
- Quest-advanced x game-event reload (the integrator's open question): already handled in code, see F3.
- Crafting recipe learning: matches vmangos (`EffectLearnSpell` has no known-spell refusal, SpellEffects.cpp:2435-2454); `SpellSystem.Effects.cs:309-318`.

## Findings

Severity: Medium = fix or correct before release; Low = fix soon, no release risk; Info = no action required.

### F1 (Medium) `ProcEngineBreaksDamageAuras` is not a configuration switch, and the integration report says it is
`src/ArcaneCore.Game/Spells/AuraInterrupt/SpellSystem.AuraInterrupt.cs:34` (property, default false); consumed at `SpellSystem.Combat.cs:132,143`;
claim at `docs/integration/wave4-integration.md:124-126` ("Config-gated and default-off or retail: ... `ProcEngineBreaksDamageAuras` (off)").

grep finds the name only in `SpellSystem.AuraInterrupt.cs`, `SpellSystem.Combat.cs`, one test and two docs: nothing binds it to `Auras:*` or any
`IConfiguration` key, so an operator cannot change it. At the default, the damage break ignores `ProcFlags`, so Wyvern Sting and Prowl break on the
damage they themselves cause (the property's own comment: "Wyvern Sting and Prowl then break on their own hit"). That is a visible non-retail
hunter/druid behaviour that ships on by construction. The doc comments on the property are also mis-attached: two consecutive `<summary>` blocks
(`:15-33`) sit on the property; the `RemoveAurasWithInterruptFlags` summary describing `checkProcFlags` belongs to the method below it.
Fix: either bind it (`Auras:ProcEngineBreaksDamageAuras`) and add the key to the config catalog, or correct the integration report to say "hard-coded,
not switchable". Consider exempting Wyvern Sting (24131/24134-24137) and Prowl by spell id as the smaller, retail-safe default.

### F2 (Medium) A new GM-visible default deviation is only half pinned: `Bans:MaxListedEntries = 200`
`src/ArcaneCore.World/Bans/BanOptions.cs:57`. Retail prints everything; the default of 200 truncates `.baninfo`/`.banlist` replies. It is gated (0 restores
retail) and documented. No test pins the default of 200 (the integrator found that setting the default to 0 does not fail the ban test because the test
sets the option itself), and only `.banlist ip` is covered; `.baninfo`, `.banlist account` and `.banlist character` capping have no test
(`tests/ArcaneCore.World.Tests/Bans/BanCommandTests.cs`). The cap is the only guard against an unbounded reply and a per-account history query
loop for `.banlist character`; add a default-value test and one test for each other listing before relying on it.

### F3 (Low, doc) The "Quest IsActive vs quest_template hot reload" open question is already handled in code
`docs/integration/wave4-integration.md` open-question bullet 3. `GameEventQuestFeature.Attach` subscribes `world.WorldTick += _ => Sync()`
(`World/WorldState/GameEventQuestFeature.cs:34`), and `GameEventQuests.Resync` re-applies every listed quest's event state whenever the live `QuestStore`
instance changes (`Game/WorldState/Events/GameEventQuests.cs:44-66`, `ReferenceEquals(store, _applied)`). A `.reload quest_template` swaps the store
(`QuestContentReloadable.cs` commit step), so the next world tick restores the event-driven state. Residual: for the remainder of the tick in which
the swap committed (the reload commit runs on the world thread, the Sync on the same tick's `WorldTick` event) an event-hidden quest is briefly
active. Remove or rewrite the bullet and add a test (`QuestReloadTests` + an active game event) that proves it; none exists today.

### F4 (Low) Synchronous database calls on the world thread in taxi persistence
`src/ArcaneCore.World/Npc/NpcServicesFeature.cs:222` (`SaveAsync(...).GetAwaiter().GetResult()` in `OnPlayerLoggingOut`) and `:262`
(`DeleteAsync(...).GetAwaiter().GetResult()` in `ClearSavedRoute`, reached from `OnFlightEnded` and on resume). Both run from the world tick, so a slow
characters database stalls every map for the duration of one flight-end or logout of a flying player. Rare (only players on a taxi), and the pattern
exists elsewhere in the tree for start-up loads, but these two are per-gameplay-event. Route them through the same write-queue shape as
`CreatureRespawnQueue` or `HonorWriteQueue` when convenient. Not a correctness risk: a failed save falls back to the departure node (`:228-243`).

### F5 (Low) The owner / charmer "always sees" clause of the dropped invisibility rule is not in the kept rule
`StealthVisibilityRule.CanSee` (`src/ArcaneCore.Game/Stealth/StealthVisibilityRule.cs:36-100`) has no `CharmerOrOwnerGuid == viewer.Guid` early return;
the dropped `InvisibilityVisibilityRule` had it (git: claude/vw5-warlock-mage-utility:src/ArcaneCore.Game/Stealth/InvisibilityVisibilityRule.cs).
Consequence: an invisible pet or charmed unit is hidden from its own owner unless the owner holds a matching detection aura. vmangos
`IsVisibleForOrDetect` returns true for the owner/charmer before the invisibility test. The integrator flagged "compare" without doing it; the result
is: Hunter's Mark and group-mate clauses present, owner clause missing. Add the clause (one `if` before the Hunter's Mark check) with a test.

### F6 (Low) Operator-visible abbreviation changes from new command names
`.reload spell` and `.reload creature` are now ambiguous (unique-prefix rule in `Reload/ReloadCommands.cs:54`: `spell_template` vs `spell_threats`;
`creature_template` vs `creature_loot_template` vs `creature_onkill_reputation`), refused rather than mis-routed; `ReloadCommandTests` was edited to
`.reload spell_te` to match. At the chat command table (`Commands/CommandTable.cs:329-352`, first prefix in table order wins, default
`ExactNameFirst=false`) the new top-level `.event`, `.honor`, `.character`, `.deplenish`, `.replenish`, `.neargrave`, `.wchange` take over the one- and
two-letter abbreviations that previously reached lower commands (`.e` was `.explorecheat`, `.h` was `.help`, `.n`/`.ne`). A Player-level `.h` now
resolves to `.honor` and answers "unavailable". vmangos has the same rule, so this is retail-faithful; worth a release note.

### F7 (Low) Wall-clock-dependent tests (CI flake risk)
- `tests/ArcaneCore.World.Tests/Honor/HonorWorldTests.cs:24`, `HonorEndToEndTests.cs:26`, `HonorMaintenanceRunnerTests.cs` (via `UtcNow` + `GameDay`):
  the test computes today's game day and maintenance weekday from the real clock while the code under test computes its own; a run that crosses
  00:00 UTC between arrange and act fails once a day, and tests whose expectation depends on the weekday are only correct because the helper mirrors the
  production function. Inject a fixed `TimeProvider` as `HonorMaintenanceRunner.RunDueAsync(..., TimeProvider)` already allows.
- `GameEventCommandTests.cs:23`, `GameEventSpawnWorldTests.cs:53`: event windows built from `DateTimeOffset.UtcNow`; safe within a minute, fragile
  across a day rollover or DST-less "Wall" interpretation boundary.
- `HonorMaintenanceRunnerTests.cs:113` (`Task.Delay(200)` then `Assert.False(run.IsCompleted)`): fails only if production loses the gate, so it is
  a valid RED, but it is a fixed sleep.
- `InspectHandlerTests.cs:44-46`: `GC.Collect(); WaitForPendingFinalizers(); Task.Delay(300)` is a workaround for `WorldTestClient` being
  finalised (socket closed, player logged out) while a test runs. It masks a test-infra defect rather than fixing it; any other suite that drops a
  `WorldTestClient` reference mid-test has the same latent flake.
- `GameEventReloadTests.cs:122-124`: poll loop 2000 x `Task.Delay(5)` waiting for the first world tick (10 s ceiling; fine).
- Pre-existing, **not fixed by the integrator and seen once in a full run**: `QuestInteractionWorldTests.SocketInvalidInteractions_CannotAcceptOrReadDetails(guard: "visibility")`
  races the map's own visibility tick (integration report, "Flakes"). It will fail on hosted CI sometimes. Make the test stop the visibility
  updater or assert after a barrier tick.

### F8 (Low) Sampled tests: five checked for "still passes if production reverted"
Sampled by reading the test against the code path it claims to pin; none was mutated (read-only review). Results:
1. `ReputationPricingTests` (25 x 0.9 -> 23, 55 x 0.95 -> 52): fails if the helper reverts to floor or ceil. Real.
2. `EvadeHealthTests` / `EvadeFidelityTests`: assert the health is untouched at evade, then exactly `hurt + max/3` at the first regen tick; fail if the
   snap returns. Real. Default-false binding is asserted twice (`CreatureOptionsBindingTests.cs:65`).
3. `RegenAuraTests`: drives `world.RunTick` through the 2 s regen with a fake aura source; fails if `RegenModifiers` or the amplitude-0 rule changes. Real.
4. `WorldHandshakeTests` AUTH_OK: pins the full ten bytes. Real.
5. `HonorMaintenanceRunnerTests.The_store_transaction_waits_for_a_login...`: fails if `WeekGate` is removed. Real, but see F7.
Weak spot found by reading, not by sampling: no test pins `Bans:MaxListedEntries` default (F2) and none pins the F3 reload-versus-event interplay.
The integration report's own admission that the mail-cap test ran on SQLite only still stands (provider theories for the commit-time recount are untested).

### F9 (Low) Documentation consistency
- `docs/areas/warlock-mage-utility.md:194` still allocates "Characters 21 / World 21" to the unstarted pet-store slices; those numbers are taken (the report
  says the next free are Characters 26 and World 29). Update the line so a later lane does not re-allocate 21.
- `docs/integration/wave4-integration.md` "Deviations" first paragraph lists `ProcEngineBreaksDamageAuras` as config-gated (F1).
- Lane documents `docs/integration/npc-services.md:10-11,94,131` and `docs/areas/talents.md:85` quote "World v21 / Characters v21" without saying these are
  the renumbered values; they agree with the tree (bank 21, NPC metadata 21), so no change is required, only awareness: the **same number 21 was
  allocated by five lanes** and a developer database that applied a lane branch (taxi, honor, respawn, game-event or reputation lane at "21") will not
  match the integrated 21. That cannot occur on a release database; flag it in the upgrade notes for dev machines.
- Typo, not a defect: `World/WorldState/GameEventReloadable.cs` XML comment "loader''s" (doubled apostrophe).
- The generated `docs/reference/{configuration,gm-commands,schema}.md` agree with the code for every key and command I spot-checked
  (`Bans:MaxListedEntries` 200, `Creatures:Respawn:Persist` true, `HotReload:Commands` false, `World:GameEvents:AllowReload` false,
  `World:Honor:MaintenanceMode` Startup, schema tips 25 / 28 / 3). I did not rerun the generator.

### F10 (Info) Default-ON behaviours that are not retail, or not config-gated (item 4)
None is a retail-unsafe default except F1. Complete list found in the diff, with the answer to "is the default at least retail-safe":

| Behaviour | Location | Gated? | Retail-safe default? |
|---|---|---|---|
| `Bans:MaxListedEntries` = 200 | `World/Bans/BanOptions.cs:57` | yes (0 = retail) | deliberate deviation, harmless (F2) |
| `ProcEngineBreaksDamageAuras` false | `Game/Spells/AuraInterrupt/SpellSystem.AuraInterrupt.cs:34` | **no** | **no**: Wyvern Sting / Prowl break on own hit (F1) |
| Pet/totem casts never spend owner spell-mod charges; `PassiveReapply` depth cap 2 | `Game/Spells/Mods/PetTotemModOwner.cs`, `PassiveReapply.cs` | no | safe direction (retail leaves a stuck -1 mod) |
| Leech multiplier unset = 1.0 (vmangos 0) | `Game/Spells/SpellSystem.Effects.cs` health-leech path | no | safe direction (heals, never harms) |
| `SpellInfo.GetCastTime` ignores the all-zero cast-time row, so Throw / Multi-Shot miss the +500 ms ranged slot | `Game/Spells/SpellInfo*.cs` | no | slightly faster than retail for two spells, cosmetic |
| Respawn: `DrawDelayAtLoad`, `AlternateEntries`, `Persist`, `SaveImmediately` all true | `Game/Creatures/CreatureOptions.Respawn.cs:20,33,39,46` | yes | retail (vmangos citations in the option docs); `Persist` + `SaveImmediately` means one write per spawned-creature death through `CreatureRespawnQueue` (unbounded channel, `World/Creatures/CreatureRespawnQueue.cs:22-23`, one consumer; a dead database queues without limit, retries 3 x then drops) |
| `Creatures:StealthAlertEnabled` true, `EvadeResetsAuras` true | `CreatureOptions.Stealth.cs:8`, `CreatureOptions.Evade.cs:10` | yes | retail |
| `Spells:Mods:*` all true (HardcodedWardMods, CustomCharges, ReapplyPassives, InstantCastKeepsFlatCastTimeCharge, SendClientModifiers, OwnerModsForPetsAndTotems) | `Game/Spells/Mods/SpellModOptions.cs:15-58` | yes (kill switch `Enabled`) | retail; aura 107/108 are live for the first time, a broad behaviour change by design |
| `Reputation:SpilloverEnabled`, `CombatReactions` true | `World/Reputation/ReputationOptions.cs:28,34` | yes | retail; inert with empty tables / no Faction.dbc (`ReputationCombatFeature.cs:50-70`) |
| `World:Honor:Enabled` true, `MaintenanceMode` Startup, `DishonorableKills` true | `Game/Honor/HonorOptions.cs`, `World/Honor/HonorSettings.cs` | yes | retail; first start with no `honor_maintenance` row seeds from `LastMaintenanceDay` (`HonorMaintenanceRunner.InitialState`), no backlog storm |
| `World:GameEvents:Enabled` true, `Dialect` Auto | `Game/WorldState/WorldStateOptions.cs:128,134` | yes | retail; empty `game_event` table does nothing |
| Self-cast now tests spell immunity (Recently Bandaged) | `Game/Spells/SpellSystem.cs:377-381` | no | matches vmangos SpellCaster.cpp:175-180 |
| Graveyard trip is scheduled behind pending movement changes instead of immediate | `Game/Combat/MapCombat.Death.cs:99-123` | no | matches vmangos Player.cpp:1329-1334; a client that never acks the water-walk order is never repopped (same as vmangos) |
| CMSG_USE_ITEM live, AUTH_OK 10 bytes, CMSG_INSPECT live | see Security | no | retail |

### F11 (Info) Correctness hot spots: reviewed, no defect found
- `SpellSystem.cs` merge: the cast pipeline composes in vmangos order (CheckCast -> mod-scope created after the first check -> cast time under the
  mod window -> second cost read spending charges -> interrupt -> `AddCooldown` (item category, cooldown mod) -> `TakePower` skipped for item casts ->
  `TakeCosts` (reagents) -> `TakeAmmo` -> hits with the self-cast immunity test -> `SealModScope` for channels -> `ApplySpellThreat` per target outcome
  -> melee-swing reset -> `TakeCastItem`). The `SpellModWindow` is a `using` local, so an exception cannot leave a window open
  (`SpellSystem.cs:352`). `Cancel` in the Preparing state spends no charge because the window closed at the end of `Prepare`. `CancelAura` moved
  wholesale to `SpellSystem.CancelAura.cs` with the aura lane's extra rules and is not duplicated.
- Taxi composition: first leg charged, then mount removal (mounts lane), per-leg `legCosts` from `ReputationPricing.Round` with the honor-aware
  discount; `total` is compared with `player.Money` before launch and each later leg is charged at its transition. Node count and payload length are capped.
- DI: no duplicate service registrations (all new `AddServices` are single-type scoped stores). Feature attach order relies on full type-name order
  for the combat hooks (`ReputationCombatFeature` after `WorldCombatHooksFeature`); the feature checks what is registered and logs a warning if the
  hooks are not the faction hooks, so a future feature that registers hooks earlier silently disables reputation reactions (log line only).
- Enchanting: unconfigured or disabled leaves enchant casts refused and consuming nothing (`EnchantItemSpells.InstallUnavailable`); the DBC reader
  is strict about layout; a configured but unreadable file refuses start-up as documented.
- Honor, graveyard, game-event and spell-threat features run their store loads with `GetAwaiter().GetResult()` at attach (start-up only).

## Recommended fix list before release (smallest set)
1. F1: bind or correct `ProcEngineBreaksDamageAuras`; ideally exempt Wyvern Sting and Prowl.
2. F2: add the default-value test and the three missing ban-listing cap tests.
3. F3: fix the stale open-question bullet and add the pinning test (cheap).
4. F5: restore the owner clause with a test.
5. F7: fix the unfixed QuestInteraction visibility race, inject time into the honor and game-event tests.
6. F9: update the stale schema allocation line in `docs/areas/warlock-mage-utility.md:194`.

## Not verified here
Hosted-CI provider runs (MariaDB, PostgreSQL), the real-client playtest, the mail-cap recount on any non-SQLite provider, whether the generated
reference pages regenerate byte-identically, and any runtime behaviour: this review is static.
