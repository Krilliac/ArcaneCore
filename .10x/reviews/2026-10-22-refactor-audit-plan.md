# ArcaneCore refactor plan (origin/main 8700e2f72)

This plan comes from reading code only. Nothing was built or run, and every statement below is static reasoning. Each item names its acceptance check. CI filters available: `tests/ci/{Game,World,Data,Rest}.slnf`. I assumed that Rest.slnf covers Kernel, Cryptography, Realm, Gateway, Atlas and MockClient tests; I did not open it to check. I made no file edits, following the computed task's read-only constraint. The user's optional "fix as you go" permission was not used.

---

## 1. Architecture overview

### Project graph (acyclic)
```
Kernel ── Cryptography ─┐
   │  ├── Protocol ─────┼── Game ── World.Contracts ── World(Exe) ── Playerbots (pack, copied beside World)
   │  └── Data ─────────┘                                │
   ├── Realm(Exe) → Kernel, Cryptography, Data           └── Gateway(Exe) → World (!)  [#184/#148]
Tools: Atlas → Game + tests/ThirdParty/DotRecast [#185]; Sniff → Protocol + tests/ThirdParty/WowWorldMessages
       MockClient → Data, Realm, World, Playerbots; AccountTool/DbUpgrade/SpellImport/ContentImporter → Data
```

### Layers and seams
- **Kernel**: host infrastructure (FaultLedger/AnomalyLedger, logging, watchdog, net admission, resilience, config), plus about 77 `I*Store` contracts and immutable `*Catalog` content.
- **Data**: EF Core persistence (80 `Ef*Store`, 117 `IDataModule`), importers, DBC readers, and two CLIs (content-importer, db-upgrade). CLIs living inside the library is a smell (#126).
- **Game**: one 1,363-file assembly. Feature folders talk through seam interfaces plus `ConditionalWeakTable` locators. There are namespace-level cycles inside the assembly: Spells with Combat, Crafting, Creatures, Death, Entities, GameObjects, Honor, Instances, Items, Locomotion, Maps, Pets, Progression, Ranged, Reputation, Skills, Stats, Totems (#9, #29, #59).
- **World**: the host. It has about 162 `*Feature` classes, `*Commands` GM groups, per-character `*WriteQueue` persistence, and Cluster/Gateway link code.
- **Playerbots**: a pack in namespace `ArcaneCore.World.Playerbots`. World refers to it through `InternalsVisibleTo` and loads it at runtime after a post-build copy (#187, #191).

### Key subsystems and god types
| Type | Size | Finding |
|---|---|---|
| SpellSystem | 89 partials, ~14k lines | #6, #7, #211 |
| CreatureMapSystem | 43–45 partials, ~10–11k lines | #17 |
| QuestNpcServices | 23 partials, 5.7k lines | #18 |
| GameObjectMapSystem / LootService | 4.6k / 1.8k lines | #85, #57 |
| MapCombat / PlayerInventory / SummonService | ~4k / 4.5k / 2.8k lines | #57 |
| Map / WorldRuntime | 2,020 / 1,436 lines | #39, #40 |
| InstanceManager | 1,943 lines | #30 |
| LogonSession | 1,279 lines | #142 |
| PlayerbotBrain / ManagedPlayerbotFeature | 3.8k / 3.5k lines | #138 |
| ContentImporterCli | 2.3k lines | #126 |

### Cross-cutting conventions (measured)
- Error idiom is `catch when (ex is not OutOfMemoryException)` followed by one `LogFault`. **Superseded (#1618):** `LogFault` already records to the `FaultLedger`, so a same-key `Record` + `LogFault` pair double-counts; the original "both writes on purpose" rule (#43, #55) no longer applies. A separate `Record` is allowed only under a different, deliberately distinct key.
- vmangos `file:line` citations are everywhere. Charter §1 freezes the wire format.

---

## 2. Naming standard (derived from the dominant measured convention)

| Concern | Standard | Evidence / count | Exceptions to keep |
|---|---|---|---|
| Private instance fields | `_camelCase` | 6,393 | none (fix LogonServer.cs:31 `patches`) #216 |
| Private static readonly | **PascalCase** (447 vs 188 `s_`) | #216 | `t_` for `[ThreadStatic]` |
| Persistence | `I*Store` (Kernel) / `Ef*Store` (Data) | 183 / 0 `Repository` | — |
| Config | `*Options` | 194 | — |
| Data schema plugin | `*DataModule` (`*WorldDataModule`, `*CharactersDataModule`) | 107 vs 9 outliers #128 | — |
| Opcode handler classes | `*Handlers` (static) | 58 | — |
| World feature root | `*Feature` | 162 | — |
| GM command group | `*Commands` | 92 | — |
| Content reload hook | `*ContentReloadable` (leaf); abstract base `*Reloadable` | ~30 / 8 #100 | the abstract bases |
| Creature/boss AI type | **`AI`** suffix (259 vs 70 `Ai`) | #35 | config key `Creatures:EventAi`, folders `EventAi`/`SmartAi` stay as they are |
| Spell script | `*Script` = `ISpellScript` only | #12 | do not reuse `ProcScript`, because it collides with existing names |
| Orchestrator | `Manager` owns entity collections, `Service` is a rule engine. Document only, no mass rename | #79 | — |
| Map-attached updater | `<Subject>MapSystem` for stateful updaters, `<Subject>Updater` for thin adapters. Document only | #61 | names are matched as strings in MapUpdateDiagnostics.cs:237 |
| Async | `Async` suffix on Task/ValueTask methods | 1,926 vs 28 outliers #218 | BotControlVerbs verb table; `ToBool`/`RetainedFailureTask` adapters |
| Abbreviations | `Id`, `Guid`, `Npc` | 9,336 / 5,045 / 4,009 | — |
| Namespace vs folder | match the folder. **Documented exception:** feature leaf folders may collapse into the feature namespace (Spells/Auras→Spells, Creatures/AI→Creatures). `Console` folders use `ServerConsole` | ~80% match #214, #10, #26, #49 | SpellSystem partials must stay in `ArcaneCore.Game.Spells` |
| Files | one primary public type per file, partials use `Owner.Feature.cs` | #215, #11 | multi-type script bundles (`*Scripts.cs`) and DTO contract files |
| Tests | class `*Tests`, methods `Subject_Behaviour`; helpers `<Area>TestKit` (static), `<Area>Fixture` (xUnit fixtures only) | 2,512 / 16,864 #180 | hot-module `Fixture.cs` |

Encode the static-field rule, IDE0130 collapse exceptions and IDE0011 in `.editorconfig` at **suggestion** severity first. Raising severity under warnings-as-errors breaks every file that does not comply yet.

---

## 3. Bugs to fix first

Each fix gets a reproducing test written first (test-author), then the fix (implementer), then a build-runner run of the named slnf.

| Id | Location | Fix | Suite |
|---|---|---|---|
| **#152** | Gateway/GatewayClientConnection.cs:288-289 (and the sibling in World/Net/WorldSession.cs:1035,1040) | Pass `token` to `GetActiveAccountBanAsync`/`GetActiveIpBanAsync`. The World sibling needs a token parameter on `IsBannedAsync`. It must not fail open. | Rest, World |
| **#116** | Kernel/Accounts/AccountSessionClaim.cs:185-192 | Wrap each subscriber in try/catch `when not OOM` and record under a stable site `accountclaims.subscriber`. Notification must stay outside the slot lock (re-entrancy). Test: first subscriber throws, the second is still called, Claim returns. | Rest |
| **#131** | Data/Loot/EfLootStateStore.cs:119-134 | Skip OCE/OOM before both FaultLedger and AnomalyLedger in the commit catch. Leave the rollback catch filter as OOM-only, as the siblings do. | Data |
| **#93** | Game/GameObjects/GameObjectMapSystem.cs:188-199, 1891-1900 | After `ReplaceContent` (this also covers the rollback path) and after a loot-content reload hook, re-run `ConfigureQuestFlags` and force one `GameobjectDynFlags` update when the filter changes. Drop the stale `_questFlagsSent` entries. | Game, World |
| **#118** | Kernel/Net/AcceptLoop.cs:26,50-53 | Use a per-call local `LogGate`. Log `suppressed + 1` so the count meaning is unchanged. | Rest |
| **#64** | Game/Items/ItemMaintenance.cs:124-157 | Move the misplaced XML doc. Clear `t_timed` on every exit path (try/finally) after the Tracked diff and the time-update sends. Keep the `ticking` snapshot, which the code relies on for re-entrancy. | Game |
| **#15** | Game/Spells/SpellSystem.cs:319-331 | Report the outer UpdateAuras fault with `spell: null`. Keep the phase string `"UpdateAuras"`. A per-source catch in UpdateAreaAuras changes control flow and is **deferred** to a sign-off. | Game |
| **#168** | Atlas/Core/Services.cs:142-185; Reference/Client/Models/ClientModelCache.cs:168,248 | Count bytes under the LRU gate only while the Lazy is still current (the ParseCache pattern). Clear and Evict must share that gate. Test: a gated `ILayerSource` with Invalidate called during a build. | Rest |
| **#208** | Data/Content/Import/Cli/RepositoryPathGuard.cs:17-81 | Resolve links (file and directory, with a link budget) and run `git -C <realRoot> check-ignore <realPath>`. Also covers the BackupAdvisor.cs:119 caller. Allowed-path semantics change, so test both directions with junctions and skip where unsupported. | Data |
| **#169** | tools/ops/deploy/deploy-live.py:452-462, deploy-post-checks.py:101-112 | **Needs an operator decision** on which lines are blocking. Replace exact counts with prefix regexes. Gate both scripts identically from one shared list in `tools/ops/` (not `tools/ops/deploy`, because tests run `python -I`). Move `--set` validation before step 1. | World (DeployScriptSafetyTests, PacketToolsOptionsTests) |
| **#219** | Playerbots/Qa/PlayerbotQaGhCliClient.cs:65-93 | On cap overflow, Kill the process immediately (WhenAny on the readers). Observe both reader tasks on every catch path because CrashHandler watches unobserved tasks. Surface the cap error as non-transient. Check the CreateIssue double-create risk. Real-process test with a child writing >8 MiB. | World |
| **#140** | Playerbots/Chat/Inference/InProcessBotChatClient.cs:80-81, 300, 362-368 | Check `_disposed` at the end of `Load` under `_gate` and dispose a late engine there. Re-check in `GenerateAsync` after `EnsureLoaded`. Do not run a 20 s Dispose on a thread-pool continuation. | World |
| **#209** | World/Ops/Control/WorldControlPipe.cs:569-583; RemoteAdminServer.cs:622-629 | Allow `maxBytes + 1` pending bytes, and strip only a `\r` immediately before `\n`. Keep the in-progress bound and the IAC/NUL filtering order. | World |
| **#170** | scripts/dev-runner.ps1:188-320 | Escape `'` in every inlined path (`$run`, `$project`, `$tee`, `$allowlist`). Keep the embedded `"` around the project path. Do not require RunRoot to exist (New-Item creates it today). | manual dev-runner smoke |
| **#221 / #154** | Realm/Protocol/RealmListWriter.cs:21,46 | Add `ThrowIfGreaterThan` guards (count ≤ 255, body ≤ ushort). Valid inputs are byte-identical. Test with 256 realms. The 0-character count is a **known gap** that needs a characters-DB source. Document it at LogonSession.cs:1197 and do not "fix" it here. | Rest |
| **#210** | World/Program.cs:37,88 | Replace the literal `78` with `ExitCodes.InvalidConfiguration`. | World |
| **#183** | tests/ArcaneCore.Realm.Tests/Watchdog/HangStackCaptureTests.cs:102-103 | Use a Guid-suffixed variable name and restore it in finally. | Rest |
| **#220** | Kernel/Characters/IdRangeReservation.cs:315,372,377 | Delete all three `shouldPrefetch` lines together (CS0219 otherwise). Dead code, not a runtime bug. | Rest |
| **#52** | Game/Teleport/TeleportService.cs:85-198 | **Decision needed.** Either correct the doc to say Far/Arriving stalls are report-only (behaviour-neutral, recommended), or add a recovery that calls `EndTransit` and tolerates a late ack. | Game |
| **#53** | Game/Maps/Map.cs:407-468 | **Decision needed** (rollback or continue; whether hook faults count toward the breaker). First step: a reproducing test with a throwing IMapUpdater, plus `RemoveFromWorld` in a finally on the remove path only. | Game |
| **#132** | Data/Content/Spells/DbcFile.cs:92-97 | Return `""` for offset 0 **only when the string block is empty**. Leave GetStringStrict and Parse alone. This is a deliberate small divergence from the cmangos assert, so record it. | Data |

---

## 4. Refactor waves

Every item runs through implementer, then build-runner (full solution build with warnings-as-errors, plus the named slnf), then reviewer. Before any rename or move, grep src, tests, docs, tools and `.github` for the old name and path, **excluding `.claude/worktrees/`**, which holds stale copies.

### Wave 0 — guards and decisions (no behaviour change, enables later waves)
| Item | Findings | Files | Change | Hazards | Acceptance |
|---|---|---|---|---|---|
| 0a | #214, #10, #26, #49, #216, #28 | `.editorconfig`, a new `docs/conventions.md` | Record the §2 table. Add the IDE0130 collapse exception and the static-PascalCase rule, at **suggestion** severity. | The new rule must take precedence over the catch-all field rule (`required_modifiers = static, readonly`). Exempt consts and `t_`. | Full build green, zero diff in .cs files |
| 0b | #9 | `tests/ArcaneCore.World.Tests/Architecture/` | Namespace-edge freeze test built from IL type references (not `using` lines), with the current Spells↔{Combat,Creatures,Pets,Items,Instances} edges as the allowlist. | Must catch `global::` references. | World.slnf; check it fails when a probe edge is injected |

### Wave 1 — mechanical, low-risk de-duplication (disjoint files, run in parallel)
| Item | Findings | Owner files | Change | Hazards | Acceptance |
|---|---|---|---|---|---|
| 1a | #16, #83 | Creatures/CreatureMapSystem.cs, GameObjects/GameObjectMapSystem*.cs, Maps/Grid/GridDefines.cs | Delete `Creatures.GridCoord`, the duplicate constants and `ComputeGrid`; use `Maps.Grid` everywhere. | Reflection: PlayerbotSiegeWorldTests.cs:389 `UnloadGrid`. ToString changes to `[X,Y]`. CreatureTests.cs:168. No `[Obsolete]` forwarder (CS0618). | Game.slnf and World.slnf (the siege test must actually run, so check the test count is > 0) |
| 1b | #19 | Npc/NpcServiceContracts.cs, Npc/InventoryItemService.cs, QuestNpcServices.{Vendor,VendorQuote,Interaction,Progression}.cs, tests QuestIntegrationTests.cs | Delete `Npc.InventoryResult` and use `Items.InventoryResult`. | The nested test class named `Items` shadows the namespace, so fully qualify it there. | Game.slnf |
| 1c | #47 | Entities/WorldObject.cs, Player.cs, SpellSystem.Objects.cs, World/OutdoorPvP/OutdoorPvPWorldHost.cs | Add `WorldObject.DefaultBoundingRadius` with the exact literal; **delete** Player's const (an alias would raise CS0108); keep the other two as aliases. | Bit-exact float. | Game, World |
| 1d | #66, #70, #69 | Game/Entities (new `TeamRules`), Social/SocialContext.cs, Battlegrounds/*, OutdoorPvP/CapturePoint.cs, World RaceClassRules/EconomyFeature, Playerbots PopulationBandPlanner | One `Team TeamOf(Race)` and one `int Index(this Team)`. Keep the byte wrappers and `BattlegroundConstants.TeamIndex` as forwarders. Battleground `IsFinite` becomes a method on the template, with each site keeping its own message and throw/return. | `PlayerbotAlteracValleyTactics.TeamIndex` is an unrelated property: no blind replace. Use methods, not properties, on the record struct (ToString). | Game, World |
| 1e | #87 | Progression/ExperienceFormulas.cs, Stats/StatFormulas.cs, PlayerProgression.cs, StatAuras.cs, Playerbots/Risk/PlayerbotSickness.cs | A uint wrapper in StatFormulas; delete the ExperienceFormulas copies after adding an equivalence test over 0..65535. | The cref in PlayerbotSickness.cs:63 (CS1574). Move the tests, don't drop them. | Game, World |
| 1f | #95 | Kernel/Accounts/BanTime.cs (new long overload), World ChatText/GmDuration/GameEventCommands/BanCommandText/ServerTimeText, Game ChatRestrictionService | One long-form `SecsToTimeString` **in Kernel** (Game cannot see World). Callers forward to it, and the output stays byte-identical. | Many tests call the copies by name, so repoint them rather than delete. | Rest, Game, World |
| 1g | #3 | Spells/Casters/Drain/{CasterPeriodicPackets,DrainAuras}.cs, Procs/BuiltInProcHandlers.cs→Procs/ProcPackets.cs | **First** pin a golden 42-byte array test, then delete `BuildPeriodicSpellDamageLog`. Move ProcPackets to its own file in the same namespace. Leave BuildManaLeechLog alone. | AuraSupportBaseline.cs:75 string, docs/areas/aura-engine.md, casters.md. | Game (DrainAuraTests) |
| 1h | #34, #48, #63, #23 | InstanceManager.cs; Map.cs; MapCombat.Durability.cs; QuestNpcServices.{Vendor,Budget,VendorQuote}.cs | Private helpers only: dictionary-detach helpers (no persistence, no ForgetIfUnused), `VisibilityQueryRadius` plus a span-based `SendToObservers`, `WearRandomArmorPiece` kept inside MapCombat, `MeetsVendorReputation` (**not** applied to Vendor.cs:367). | RNG draw order; float addition order unchanged. | Game |
| 1i | #109, #110 | Kernel Configuration/Validation, Ops/Metrics, Ops/Watchdog, Logging/LogSink.cs | Internal `ConfigChecks` helper with the message and fix text as parameters, in text and typed forms. Drop notices built in the base class. | Message text is asserted (ExceptionVisibilityTests.cs:148). | Rest |
| 1j | #104, #105 | World/Gm/Args/CommandArgs.cs, AlterCommands.cs, ObjectToolsCommands.cs; 31 files with the stray using | `TryParseEnumName<T>` covering the name branch only, preserving the current int-guard behaviour. Put the `2147483648` behaviour change in a separate tested commit. `dotnet format --diagnostics IDE0065 --include <31 files>`. | Keep the numeric branches at each site. | World |
| 1k | #135, #136, #134 | Playerbots (new internal `PlayerbotGeometry`), ~14 files | Shared `IsFinite`/`Flat`, plus **two** distance flavours (raw, and motion-aware for BattlegroundAI). `OneString` made internal. | Method-group call sites need lambdas. `ElevatorNetwork.Flat` is used by tests. | World |

### Wave 2 — parameterised shared helpers (medium risk, characterisation tests first)
| Item | Findings | Files | Change | Hazards | Acceptance |
|---|---|---|---|---|---|
| 2a | #82, #1, #20, #56, #33 | new `Entities/WorldGeometry.cs`, then site by site | Add `DistanceSq3D`, `CenterDistance3D`, `DistanceMinusRadii`, `Distance2D` and `IsWithinDist(range, inclusive, radii, mapCheck: None/Id/Ref)`. **Do not add `WorldObject.DistanceTo`** (CS0108 against GameObject.DistanceTo). Migrate one folder per PR, keeping `<`/`<=`, radii and map check unchanged at each site. Fold in `EventAiSearch.DistanceSquared` and `LootService.Distance3D`. | Method-group `OrderBy(Dist)`. Keep squared comparisons squared. The Insignia bones change is a separate reference-checked change. | Game per PR, plus a boundary test per variant |
| 2b | #0 | Spells/Rules/SpellRounding.cs + 14 sites | Add `DitherSingle` (NextSingle), `DitherFloor` (no sign handling) and a clamped uint variant. Map each site to the variant matching its current draw, sign and overflow behaviour, including the "skip draw when ≤0 / whole" sites. Only identical-behaviour substitutions. | Seeded-RNG stream; TalentProcSupport public int return type. | Game (seeded tests must not change) |
| 2c | #2 | SpellSystem.cs, MapCombat.cs, Auras/{Stat,PercentStat,AttackSpeed,CombatStat}Auras.cs, RangedSpellFacts.cs | SpellSystem.Get/SetPower delegates to MapCombat and **keeps the null guard**. One `IsWandUser`. `AppliedLedger<T>` with Record/TryGet/Take, a readonly static, one instance per module. | DuelResetScript does not clamp, so leave it. AttackSpeed stores the post-haste value. | Game, World |
| 2d | #33, #22 | Instances/Scripts (new `ScriptTimers`), Creatures/AI/CreatureAI.cs | `ScriptTimers.Due` (A/B semantics), `DueLatched` (Anubisath), `DueRetry` (DragonsOfNightmare/Azuregos). Delete every per-class copy **in the same commit** (CS0108). Variant C (ZG trash) keeps its own name until reference-verified. Fix only the 5 hard-coded `RandomInt(0,N)` index sites. | Merging Tick semantics would disarm failed casts. | Game |
| 2e | #21 | Creatures/AI/DialogueRunner.cs, InDreams/NightmareManifests/Ranshalla/ReginaldWindsor AI | Runner with positional int keys, a -1 sentinel, commit-before-callback, and callback gated on `System`. Each AI keeps its `DialogueStep` property. Exclude AndorovAI. | Tick placement in each update stays the same. | Game (DataDrivenEscortTests) |
| 2f | #32 | Instances/Scripts/{Raids/RaidBossAI,RaidCreatureAI,DungeonBossAis}.cs | Static `CombatActionHelpers` (Below, HealthBelowPct kept distinct, SetMeleeEnabled). Random-target pickers take explicit filter and null-policy parameters. Do not unify Reset/Evade/Cast defaults. | BugTrioAI RandomTarget overload; ActionTimer is protected API. | Game (RaidBossScriptTests, ScriptCoverageTests) |
| 2g | #119, #123, #122 | Data/Content/Import/Core/ImportTransaction.cs, 5 importers, 3 Ef stores, new `DumpRowReader` | `RunAsync(label, savepointPrefix)` with work delegates that toggle AutoDetectChanges themselves. Pre-transaction steps (FinishTemplates, ResolveSpawnData) stay outside. Rollback helper with per-site keys, severity and filter; the caller keeps the `throw;`. DumpRowReader takes NumberStyles and an exception factory as parameters. | Exit-code routing depends on exception type (ImportSchemaException vs InvalidDataException). | Data |
| 2h | #121, #124 | Data DBC readers; move DbcFile/DbcTable to `ArcaneCore.Data.ClientData` | `RequireFields(exact)`, `RequireOneOf`, `RequireAtLeast`. Keep each reader's message wording. Move namespaces and fix usings, including the `Spells.DbcFile` cref (CS1574). | Usings in about 68 files across tests, World and tools. Keep the MaxRecords checks. | Data, World, Rest |
| 2i | #44, #45 | Transports/*, Locomotion/Knockback/* | `TransportAnomalies.Report(..)` taking a latch selector (not `ref` to a property), keeping the decimal `Guid.Low` subject and each site's own Suppress/latch/reset gating. Merge only the byte-identical `AngleFromTo`/`BearingFromTo` (keep the null guards). Transport NormalizeOrientation stays distinct. | Tests capture `(guid {low})`. | Game (TransportAnomalyTests, LocomotionAnomalyTests) |
| 2j | #94 | World/Persistence (new `CharacterKeyedWriteQueue<TValue,TStore>`), Rest/ExploredZones first, Taxi later | A base class with strategy hooks for retry delay (constant/linear), busy-skip of the ledger, ownership-rejection mode (break/return/throw) and per-queue ledger keys. **Do not** generalise the public KeyedStoreWriteQueue. Concrete sealed names stay because DI and CharacterPersistenceBarrier use `nameof`. | Literal `queue=RestWriteQueue` strings must not come from `GetType().Name`. | World (per-queue tests before and after) |
| 2k | #157 (after #168) | Atlas Core (new `CostBoundedLazyCache`) | Built on the ParseCache model, with hooks for keep/evict-timing/token retry. LoadCache keeps eviction on insert. ParseCache wraps the cache (public versus internal visibility). | Status output byte counts change only by the bug fix. | Rest |
| 2l | #143, #195 | Cryptography (new `WowAuthDigest`, `Le`), Protocol (new `WorldFrame` header parse/build, size consts) | Pure digest and header helpers only. The I/O loops stay. EncryptHeader stays under each caller's `_sendLock`. WorldSession keeps forwarding consts. Account evaluation stays per caller. | Constants 0x2800 and ushort.Max-2 are exact. Tests keep their own independent digest oracle. | World, Rest |
| 2m | #67 | Game (new `InstanceAttachments<TKey,TValue>`) | GetOrCreate/Attach/Find/Remove, with wrapper types for delegate or interface values. Keep the old public names as forwarders. Convert one lane per PR. | Create-on-first-use vs throw vs clear-on-null semantics stay per site. | Game, World |

### Wave 3 — file/partial moves (behaviour-neutral, one commit each so blame-ignore-revs works)
| Item | Findings | Files | Change | Hazards | Acceptance |
|---|---|---|---|---|---|
| 3a | #39 | Map.cs → Map.{Visibility,Broadcast,Gating,Updaters,Membership,Update}.cs | Pure partial split. Fields with dependent initialisers stay with the constructor. | docs/ops/exception-audit.md is generated, so rerun audit.py. | Game, World |
| 3b | #40 | WorldRuntime.{ManualClock,Perf,Events,Autosave}.cs | Partial split. `RunTick` keeps its name (HangStackCaptureTests string). WorldTickWaiter stays put. | — | Game, Rest |
| 3c | #30 | InstanceManager.{Resolver,Binds,RaidSchedule,ScriptHost,MapState}.cs; InstanceLookups.cs→InstanceManager.Lookups.cs | **Partials only**, no collaborator classes, because state ownership and reset ordering are shared. | `InstanceDataOf` and the internal seams used through IVT. | Game, World |
| 3d | #6 (stage 1), #7 | SpellSystem.Cooldowns.cs, SpellSystem.UnitMath.cs; Cast() extraction | Move members between partials (no new types). Then extract the private Cast helpers in the documented order: modWindow stays in Cast, procOutcomes is captured at the same point, unitTarget is passed after line 680, and the fault label stays "Cast". | `_procDepth` reflection in SpellAnomalyTests.cs:538,592 is not touched in this stage. | Game (full), World |
| 3e | #25, #215, #11, #97, #76, #80, #101 | CreatureAnomalies.cs partials → Creature.Anomalies.cs/CreatureMapSystem.Anomalies.cs; PersistentPetService.cs→SummonService.PersistentPets.cs; ShutdownFeature.cs→ServerStats.cs; World CharacterSpellDataModule.cs→WorldCorpseSpellTargetResolver.cs; Casting/SpellTargetingSeams.cs→SpellCorpseTargets.cs and the root one →SpellTargetRelations.cs; GatewayWorkerTransport.cs split (keep framing helpers on LoopbackGatewayWorkerHost, enum values unchanged); SocialContext/ReputationMath splits; SilithusCatalog.cs | git mv plus partial or type split, with namespaces unchanged. | Update docs and path strings (TalentEffectCoverage.cs:169, devskim paths, zone-script batch docs). Do not rename `*Scripts.cs` bundles. The OnceFlag abstraction is not part of this item. | per-project slnf |
| 3f | #41 | Maps/ClientRejection.cs → Game/ClientErrors/ (stays in Game) | Namespace move with 3 using updates; docs/ops/observability.md. | Not into World (Game.Tests would lose access). | Game, World |
| 3g | #36 | Raids/BlackwingLair.cs → BlackwingLair/DrakeAI.cs constants, plus InstanceScriptedAis.cs:28 | Fold the constants, repoint the test, delete the file. No blind replace of `11981`. | — | Game |
| 3h | #111 (part), #149 (part) | Kernel `ServerConsoleTitle.TryBeep` behind a `CrashAlert` delegate; Realm resilience wiring | Diagnostics no longer names Ops. Move `InnerStoreKey` to `GuardedCalls` with the value unchanged. Leave the `Microsoft.Extensions.DependencyInjection` extension namespaces alone (repo convention). | Using updates in about 5 test files. | Rest |

### Wave 4 — test infrastructure
| Item | Findings | Files | Change | Hazards | Acceptance |
|---|---|---|---|---|---|
| 4a | #177, #171-#174, #196, #197 | new `tests/ArcaneCore.TestSupport` (IsTestProject=false, xunit.assert only) | Home for NavMeshFixture, VMapFixture, CodexLineDatabase **with its embedded .sql** (it uses GetExecutingAssembly), AllocationProbe, NullPlayerSession/RecordingPlayerSession (with FakeSession members), NullSaveQueue (with an `Instance`), CapturingLogger (generic, provider and Kernel-prefixed forms), ManualTimeProvider, TempDirectory (ClearAllPools opt-in, Atlas repo guard kept), RepositorySource (nullable and throwing forms). Remove the `Compile Include Link` items. | Add the project to slnx, add packages.lock.json, update the contributing.md restore recipe (PackageLockFileTests). Possibly new IVT entries. Migrate copies file by file and keep behaviour-specific doubles (BotSession, ThrowingSession, Batch4-8 casters). | all four slnf; per-project test counts unchanged |
| 4b | #182, #179, #180, #181 | test namespaces and helpers | Fix the namespace-to-folder mismatches **except** ServerConsole (System.Console clash) and GridTerrain (documented filter). Use `Combat` test files in a non-colliding namespace (`.CombatCore`). Rename helpers to TestKit/Fixture. **Keep the batch numbers** in zone-script test names, since docs link to them. | devskim path suppressions; FQN filters would silently match 0 tests. | per slnf, with counts compared before and after |

### Wave 5 — build and graph hygiene
| Item | Findings | Change | Hazards | Acceptance |
|---|---|---|---|---|
| 5a | #189 | Directory.Packages.props (CPM). Explicit `ManagePackageVersionsCentrally=false` for hotcode-spike, hotmodule fixtures, ThirdParty and diag probes. **No** blanket tests/Directory.Build.props. | Regenerate every packages.lock.json in one commit (CI uses locked mode). Keep the Kernel `Aliases=LoggingPkg`. | clean `dotnet restore --locked-mode`, all slnf |
| 5b | #186 | Move the three `[assembly: InternalsVisibleTo]` attributes into csproj files. Delete the duplicate MockClient attribute. | Exact assembly names. Re-run the ReadsDifferently scan and compare Unresolved counts. | full build, World |
| 5c | #188 | Policy decision only: keep explicit references for direct use. No edits. | — | — |

---

## 5. Explicitly deferred

| Finding | Reason |
|---|---|
| #17 CreatureMapSystem, #85 GameObjectMapSystem/LootService, #57 MapCombat/PlayerInventory collaborator extraction, #211 general partial-class decomposition | Collaborator extraction splits shared private state. Reflection reaches internals (`_respawnAt`, `UnloadGrid`). MapUpdateDiagnostics matches type names as strings. The `_useHandlers` override precedence and `ReferenceEquals(ViewerFieldFilter, this)` identity checks are also at stake. Only partial-file splits (Wave 3) are safe now. Revisit one cluster at a time with characterisation tests. |
| #6 stage 2, #9 project split, #29 moving AI bases out of `Instances.Scripts.Raids` | Hundreds of files, a hot-module public surface (`InstanceData`), and FullName-ordered discovery. Wave 0b freezes the edges instead. |
| #18 QuestNpcServices split | The only safe part is **deleting** the duplicate direct Anachronos call (QuestNpcServices.Interaction.cs:197-200). Its start then depends on DungeonScriptFeature being attached, so it needs a new AcceptLoop-path test first. Treat that as a separate small bug-style item. The facade split is deferred. |
| #10, #26, #49, #102, #191 namespace realignment (Spells, Creatures, Locomotion, Contracts, Playerbots) | SpellSystem partials pin the root namespace. EventAi/handler/module discovery is ordered by FullName. The Playerbots QA log tap matches a category string (PlayerbotQaLogTap.cs:22) and would silently stop matching. Documented as convention exceptions in Wave 0a. |
| #35 AI/Ai rename, #128 DataModule rename, #61, #79, #100, #12, #166, #167 suffix renames | Display, ScriptCatalog and tooling strings (fidelity_review.py:65), the schema.md golden file, MapUpdateDiagnostics string switch, and module/handler order by FullName. Low value next to the churn. Documented in §2 instead. |
| #8 class-script folder reorganisation | Moving WarriorScriptsModule reorders value modifiers and observers (sorted by FullName). The racials also need a home. Needs a full run and a decision. |
| #184/#148 Gateway→World split | Build5875Protocol depends on Game. Cluster/Gateway folders mix worker-side types. IVT runs in both directions. The handoff file needs splitting. A planned tier change; schedule it as its own migration. |
| #187 pack-copy build target | Docker, redeploy and RedeployPlan.ps1 string-match the target name. docker-smoke already covers the absent-pack case. Wait for tier 2c. |
| #185 tests/ThirdParty relocation | Doc gates and PacketShapeTableTests hard-code the path. Changing the devskim ignore-glob would turn on scanning of vendored code. Low value. |
| #138, #139 Playerbots god classes and folders | The ReadsDifferentlyBaseline path keys, the Atlas bookmark ref and the nested exception name have to stay. Low value until tier 2c. |
| #142 LogonSession extraction | FaultLedger signature IDs come from frame type names, so moving code splits fault history. The logger category and three Data-key consumers also matter. Consider only the shared-constant part (LogonFaults keys). |
| #144, #194 Realm PacketWriter merge | Different constructor contract (a test asserts it), ASCII vs UTF-8 encoding, and the Protocol "world-only" layering. |
| #146, #160, #162, #164, #126 CLI and tool consolidation | Operator-facing message and exit-code contracts are parsed by scripts (run-gm-commands.ps1, dev-runner.ps1). spell-import also seeds data and uses host config. Needs per-tool redesign plus in-process tests first. |
| #159, #163 deploy script restructuring | Live-server path with string-shape tests. Only the shared expected-lines module goes into the #169 bug fix. |
| #108, #106, #115, #90, #88, #65-type small primitive merges (token buckets, ledger entry core, Lock conversion, RNG unification, class masks) | The edge semantics differ (clock-backwards handling, NoteSynthetic, Monitor.Wait in QueuedLineWriter, inclusive vs exclusive RNG bounds with 419 seeded tests, bit-31 masks). The behaviour-preservation proof costs more than the value. |
| #43, #55 fault-boundary helpers | The double-record (dotted key plus dashed tag) is observable, and MapUpdateScheduler deliberately has no filter. A helper would mostly reshuffle code. |
| #196-#207 remaining cross-project items (#198-#207 except the bugs) | Fold into Wave 4 or 2l where they overlap. The rest (#206 same-name types, #207 0xF130 literals, which are const-pattern bound) are low value. |

---

Files referenced: `tests\ci\Game.slnf`, `tests\ci\World.slnf`, `tests\ci\Data.slnf`, `tests\ci\Rest.slnf`. All other paths are relative to `src` unless they start with `tests/` or `tools/`.