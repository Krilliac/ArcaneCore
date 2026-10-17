# Hot reload tiers (design)

Status: design, owner go on 2026-10-16 for tiers 1 to 3. Tier 1 is **built and wired** into the module lane
(spell scripts, instance scripts and EventAI handlers load, replace and unload through `.hotmodule`; see
`docs/areas/code-hot-reload.md`). A failure after validation is rolled back in every slot, and the last good version is restored (section 3.5).
RED proofs and a collected-unload proof exist for the new slots. Still open for Tier 1: the T1-live row (a real client on a dev
server). Tier 2a part 1 (contracts assembly and feature lifecycle seams, section 4.2) is **built** on `claude/hr2-lifecycle`;
Tier 2a part 2 (core `ManagedBotRegistry`, call-site inversion, the six bot features detachable, the core/pack
architecture test, section 4.5) is **built** on `claude/hr2-bots`; the rest of Tier 2 (options into the contracts
assembly, 2b to 2d) are planned. Tier 3 phase 3a (gateway passthrough) is **built and mock-client tested** on
`claude/hr2-gateway` (section 5.4, [gateway.md](../areas/gateway.md)); real-client acceptance and phases 3b/3c are open.
All three lanes are merged on `claude/hr2-integrate`.
Base: `claude/hot-reload-tiers` @ `8baad0b1`.

Goal: a hard restart only when it cannot be avoided. Content, configuration and code change on a running server, and
a change that cannot be applied in place moves players to a new process instead of disconnecting them. Each tier
below removes one class of restart. Every tier keeps the invariants in section 7; a tier that cannot keep them is not
shipped.

Related pages: [Live reload (`.reload`)](../areas/hot-reload.md), [Code hot reload](../areas/code-hot-reload.md),
[Hot swap announcements](../areas/hot-swap-announcements.md), [Clustering design](../CLUSTERING_DESIGN.md),
[Character fencing acceptance](../CHARACTER_FENCING_ACCEPTANCE.md), the ROADMAP row "Live hot reload".

How to read the claims: "built" means compiled and covered by committed tests (named); "from reading" means a static
reading of the code with nothing built or run. Line numbers are at `8baad0b1`.

## 1. Where we are (built and wired)

| Lane | What changes live | What still restarts | Page |
|---|---|---|---|
| Configuration (`.reload config`) | About 290 keys (a grep of the `Live*` factories in `src/ArcaneCore.World/Reload/WorldConfigKeys.cs`, approximate, most of them playerbot keys) | 8 restart-only entries (`Fixed` / `FixedListener`: `TickIntervalMs`, `World:Maps:DataDirectory`, `Port`, `BindAddress`, ...), reported as "can't be changed at reload" | [hot-reload.md](../areas/hot-reload.md) |
| Content (`.reload <table>`) | About 35 tables (spells, items, creatures, quests, NPC services, loot, game objects, teleports, area triggers, ...): built off the world thread, validated, committed at a tick boundary in a `ReloadTransaction` with undo steps | Map registry, terrain, collision, DBC-derived data, tables with no store (see the audit table) | [hot-reload.md](../areas/hot-reload.md) |
| HotCode development runner | Method bodies in the server's own assemblies (Debug, SDK, `dotnet watch`), plus new opcode handler groups, command groups and default map updaters through the additive registry refresh | Rude edits, static initializers, the feature set, DI, EF model, any Release build | [code-hot-reload.md](../areas/code-hot-reload.md) |
| Module lane (`.hotmodule`) | A collectible `AssemblyLoadContext` per module, Release-capable, SHA-256 allowlist: opcode handler groups, chat command groups, creature AI (`ICreatureAiGroup`), global script hooks, spell scripts, instance scripts and EventAI handlers; load, replace, unload, all at a tick boundary with full rollback | Map updaters, `IWorldFeature`s, `IDataModule`s, DI services, the server's own code | [code-hot-reload.md](../areas/code-hot-reload.md#module-lane-load-replace-and-unload-code-in-a-running-server) |

So today a restart is still forced by: any change to built-in code on a Release server, any new kind of module
contribution, a change to a whole subsystem such as Playerbots, and anything structural (features, DI, schema,
listener, runtime upgrade).

## 2. The tiers at a glance

| Tier | Removes the restart for | Mechanism | Status |
|---|---|---|---|
| 1 | Gameplay scripts that live outside the core: spell scripts, instance scripts, EventAI handlers, discovered types from several assemblies | Widen the module lane: every discovery site accepts a set of assemblies, and each script registry supports a validated, all-or-nothing swap | **Built and wired** into `ModuleHost`, with full rollback; T1-live not yet run |
| 2 | A whole subsystem's code, on Release: Playerbots first | Core plus reloadable packs: a contracts assembly, detachable features, one collectible context and one child DI container per pack | 2a parts 1 and 2 **built** (contracts assembly, lifecycle seams, `.feature`, bot registry, call-site inversion, detachable bot features; 4 allowlisted core types left, section 4.5); 2b to 2d planned |
| 3 | Everything else: core code, runtime upgrades, structural changes | A gateway process keeps the client sockets; a second World is warmed and players are handed over (blue/green) | 3a passthrough **built** (mock client only, off by default); 3b/3c planned |
| 4 | Nothing new for production | The Debug edit-and-continue runner stays a development tool | Built (dev-only), unchanged |

## 3. Tier 1: widen the module lane

### 3.1 Multi-assembly discovery (built on `claude/hr-discovery`, commit `6316539f`)

Every startup discovery site scanned exactly one assembly (`typeof(X).Assembly`). It now has an overload that takes
`CodeAssemblies` (`src/ArcaneCore.Kernel/Reflection/CodeAssemblies.cs`), an immutable set of built-in assemblies plus
packs:

- `CodeAssemblies.Of(builtIns)`, `WithPack(Assembly)`, `WithoutPack(simpleName)`, `GetLoadableTypes()`.
- Deterministic order: assembly simple name, then type full name, both ordinal. A second assembly with the same simple
  name is refused; a reloaded pack is removed with `WithoutPack` before the new build is added. Removing a built-in, or
  an empty set, is refused.
- Fail closed: a type that does not load throws `CodeAssemblyLoadException` (with `AssemblyName` and `LoaderErrors`)
  instead of being silently dropped.
- Sites with the new overload: `WorldFeatures.DiscoverFeatureTypes`, `AssemblyDiscovery.FindTypes/CreateAll`
  (internal), `DataModules.Discover`, `DefaultMapUpdaters.DiscoverCandidate`, `PlayerbotScenarioCatalog.DiscoverShipped`,
  `QuestNpcServices.DiscoverAdapterModules`, `MovementObservers.DiscoverTypes`, `SpellHandlerModules.Discover`,
  `ConfigCheckDiscovery`, `WiringAudit.Of`.

Evidence (compiler and tests, lane report): build 0 warnings / 0 errors; `CodeAssembliesTests` 11/11,
`CodeAssemblyDiscoveryTests` 6/6 (each list asserted non-empty first, and the Game-only set gives exactly today's
lists), existing `DataModuleTests`, `SpellHandlerModuleTests` and World wiring tests green.

Not done (from reading): the startup sets are still computed once over one assembly, and nothing swaps a live registry
from a `CodeAssemblies` value; that is the integration step (3.5). The old `IEnumerable<Assembly>` overloads of
`ConfigCheckDiscovery` and `WiringAudit` still drop types that fail to load (`WiringAudit` deliberately: it is a
diagnostic that must not throw).

### 3.2 Swappable spell scripts (built on `claude/hr-spellscripts`, commit `39784189`)

`SpellScriptRegistry` (`src/ArcaneCore.Game/Spells/Scripts/`) holds one immutable snapshot behind a volatile
reference. API for the host: `CreateScripts(Assembly)`, `ValidateSwap(removed, added)` (null = valid),
`CommitSwap(removed, added)` (world thread, tick boundary; throws and changes nothing if invalid), `ScriptsChanged`,
`IsBuiltIn(spellId)`, `HotScripts`, live `ExecuteEffects`.

- Built-in scripts (passed to the constructor) cannot be claimed or removed by a module. A module replaces its own
  script by listing it in `removed` and the new one in `added`; removal matches by object reference, so the host keeps
  the instances it added.
- `SpellScriptDispatcher` subscribes to `ScriptsChanged` and chains execute effects a newly added script declares
  (only effects the world already handles, never twice). After removal the chained handler falls through to the
  wrapped handler, which is what a restart without the script would do.
- In-flight casts and auras: nothing caches a script, each hook looks it up when it fires, so the next hook of a cast
  that spans the swap runs the new script. Old code survives only in work it scheduled itself (timers, subscriptions,
  closures), which also keeps the old assembly loaded until it ends.

Evidence: build green; `SpellScriptHotSwapTests` 10/10; with the dispatcher's `ScriptsChanged` subscription disabled,
the chaining test failed (RED), restored and green again, so the hookup is load-bearing.

### 3.3 Instance scripts and EventAI handlers (built on `claude/hr-instanceai`, commit `5fa51d97`)

Both registries are their own holder: `InstanceScriptRegistry.Default` and `EventAiRegistry.Default` stay the same
instance and swap their internal table in one reference write, so `InstanceManager.Scripts`, every `EventAiEngine` and
`EventAiCoverage` see a commit without re-wiring.

- `InstanceScriptRegistry`: `DiscoverFactories(assemblies)` (throws on two scripts for one map), `ValidateSwap`,
  `CommitSwap(removeMapIds, add)`, `RunsCurrentScript(data)`, `IsBuiltIn(mapId)`, `ModuleMapIds`, `Generation`;
  `InstanceManager.RetiredScriptInstanceCount` (world thread). A swap affects instance maps created afterwards; a
  loaded instance keeps its `InstanceData`, because that object is the instance's progress.
- `EventAiRegistry`: `DiscoverHandlers(assemblies)` returning `EventAiHandlerSet`, `ValidateSwap`, `CommitSwap` for
  event and action types, `IsCurrent(handler)`, `IsBuiltInEvent/Action`, `ModuleEventTypes/ActionTypes`, `Generation`.
  Actions are looked up per action run, so an action swap is live at once. Event handlers are bound to a creature's
  event rows when the engine builds them, so an event-handler swap reaches a creature at its next respawn. The module
  lane now forces that respawn for affected database creatures, staggered (section 3.5).
- Same rule as spells: built-ins cannot be overridden or removed; a module replaces its own entry by removing and
  re-adding it in one swap.

Evidence: build green; the two new classes 10/10 alone, 42/42 together with the existing instance and EventAI tests.
RED proofs were done later (lane `claude/hr2-t1finish`). With the built-in remove check removed, `InstanceScriptSwapTests` failed.
With the built-in override check removed, `EventAiRegistrySwapTests` failed. Both were restored and green again. Creatures still
running a retired EventAI event handler are counted by `ModuleGameSeams.CountRetired`, which reads each creature's bound holders.

### 3.4 Not in Tier 1

Map-specific entry AI and the built-in dungeon boss scripts (consulted before module registrations), `IWorldFeature`s,
`IDataModule`s and DI services stay out. Features belong to Tier 2; tables need a schema story (section 9).

### 3.5 Integration step (built: `claude/hr2-t1finish`, commits `d32280b2` and `54937661`)

- Merge the three lane branches into `claude/hot-reload-tiers` and build them together (each built alone so far).
- `ModuleHost` builds one candidate per module covering all slots (opcodes, commands, creature AI, hooks, spell
  scripts, instance scripts, EventAI events and actions), validates every slot before touching any, and commits all
  slots in the same posted tick-boundary action. **Built:** a failure after validation in any slot is rolled back
  through an undo log (`SwapUndo`). The spell, instance and EventAI registries restore saved tables
  (`Save`/`Restore(Checkpoint)`); creature AI is undone by an inverse swap; hooks are restored to the saved list
  and order; opcodes go back to a copy of the old table. For a replace, the owner policy then unloads the previous
  version and restores the last good one from its kept bytes (failed, unloaded, restored). A throwing
  `ScriptsChanged` handler fails the swap. A registry that went away only rejects additions.
  Evidence: `ModuleRollbackTests` injects a fault after each slot for three fixture modules, plus a RED run with a
  rollback that does nothing.
- Unload: **built.** `ModuleHost.RetiredGameReferences` counts loaded instances still on a retired script and creatures
  with a retired EventAI event handler. A test proves the module context is collected after unload (WeakReference),
  stays alive while a real EventAI creature holds a retired handler, and is collected after that creature respawns.
  Changed EventAI event handlers now reach live database creatures through a staggered respawn (`EventAiRespawnPolicy`).
  Scheduled spell-script work stays invisible to any count (from reading).
- Swaps announced and audited exactly like today's module decisions.

## 4. Tier 2: core plus reloadable packs (Playerbots pilot)

Source: Tier 2 research (static reading, nothing built). Playerbots is about 106,000 lines in 316 files, larger than the
rest of `ArcaneCore.World` (about 92,000), so it is both the most useful and the hardest pilot.

### 4.1 Shape

- **Contracts assembly** `ArcaneCore.World.Contracts`, default context, versioned: feature lifecycle interfaces, the
  playerbot service interfaces (`IPlayerbotService`, `IPlayerbotTravel`, `IPlayerbotQaSink`, `IBotChatClient`, ...),
  **all options types** (`PlayerbotOptions` and its 13 nested types; config reload and `IOptions<T>` are keyed by type,
  so options must live outside the pack and be plain data), a managed-bot session seam replacing about 25 internal
  `WorldSession` members, and the few pure types core uses (explore atlas, stat weights, item score, talent builds).
- **Pack** (about 99% of the code): brains, planners, travel, combat, groups, battlegrounds, population, social, chat,
  QA, scenarios, commands. Loaded by an extended `ModuleHost`, its own discovery (via `CodeAssemblies`), and its **own
  child service provider**: Microsoft DI caches realized services by `Type` for the container's lifetime, so a pack type
  in the root container can never unload.
- The pack's assembly name must fall outside `ModuleLoadContext.SharedPrefixes` (`"ArcaneCore."` resolves to the
  default context), or the context gets an explicit pack list.
- About 20 core files reference Playerbots directly today (`WorldServiceCollectionExtensions`, `WorldHost` startup and
  shutdown, `ManagedWorldSession`, `CreatureWorldFeature`, two reloadables, `WorldConfigKeys`, GM boost, watchdog,
  metrics, diagnostics, teleport waiver lookup); each is inverted through a contract.
- Public seams, not `InternalsVisibleTo`: a pack compiled against changed internals fails at JIT time mid-tick rather
  than at load. A contracts version stamp is checked at load.

### 4.2 Lifecycle contract

`IDetachableWorldFeature : IWorldFeature` with `DetachKind` (`Pinned`, `Stateless`, `Handover`),
`QuiesceAsync` (off the world thread: stop loops and threads, await tasks, flush saves), `Detach(handover)` (world
thread, tick boundary: unsubscribe, clear static weak tables and `[ThreadStatic]`s, write a handover of core/contract
types only) and `AttachFrom(world, handover?)` (same tick, new pack; null = cold start). Old `Detach` and new
`AttachFrom` commit in one tick; if `AttachFrom` throws, the old pack is re-attached from the same handover. Core
features stay `Pinned`. A core-owned `FeatureSubscriptions` bag is the only way pack code subscribes to core events
(enforced by an architecture test).

**Built** (branch `claude/hr2-lifecycle`, commits `b493ed67`, `3a6a9ec7`; compiler and tests, see Evidence). One refinement
of the proposal: the bag is a parameter of `AttachFrom` (`AttachFrom(world, handover?, subscriptions)`), because the host
must own it to dispose it on detach. Public API:

- Assembly `ArcaneCore.World.Contracts` (new project, referenced by World, default context):
  - `ArcaneCore.World.Contracts.ContractsVersion`: `const int Current = 1`, `static bool IsCompatible(int builtAgainst)`
    (exact match).
  - `ArcaneCore.World.Features.IWorldFeature` (moved from World, namespace and members unchanged).
  - `ArcaneCore.World.Features.DetachKind { Pinned, Stateless, Handover }`.
  - `ArcaneCore.World.Features.IDetachableWorldFeature : IWorldFeature`: `DetachKind DetachKind { get; }`,
    `Task QuiesceAsync(CancellationToken)`, `void Detach(FeatureHandover handover)`,
    `void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)`; the default
    `IWorldFeature.Attach` is a cold `AttachFrom` with an unowned bag (for callers without the host).
  - `ArcaneCore.World.Features.FeatureHandover`: `int ContractsVersion`, `Set<T>(string key, T value)` (throws for a type,
    element type or generic argument from a collectible assembly), `bool TryGet<T>(string key, out T value)`, `Keys`, `Count`.
  - `ArcaneCore.World.Features.FeatureSubscriptions : IDisposable`: `FeatureSubscriptions(string owner)`, `Owner`, `Count`,
    `IsDisposed`, `Subscribe<THandler>(handler, add, remove)`, `Add(IDisposable registration)`, `OnPlayerLoggedIn`,
    `OnPlayerLoggingOut`, `OnUpdated`, `OnWorldTick`, `OnMapCreated`, `OnMapUnloading` (each `(WorldRuntime, handler)`),
    `Observe(SpellSystem, ISpellCastObserver)`, `Dispose()` (newest first, every undo runs, failures thrown together;
    subscribing after dispose throws `ObjectDisposedException` before touching the core object).
  - `ArcaneCore.World.Features.WorldLifecycleStage { AfterWorldStart, BeforeWorldStop }`,
    `IWorldLifecycleHooks.Register(stage, name, Func<CancellationToken, Task>) : IDisposable`,
    `ITeleportWaiverRegistry.Register(IAreaTriggerWaiver) : IDisposable`.
- `ArcaneCore.Game.Spells.SpellSystem.UnregisterObserver(ISpellCastObserver) : bool` (copy-on-write).
- Assembly `ArcaneCore.World`, namespace `ArcaneCore.World.Features`:
  - `FeatureHost(IHotCodeWorld? poster, Func<HotCodeAudit?>? audit, Func<IHotSwapReporter?>? reporter, ILogger? logger,
    TimeSpan? commitTimeout, TimeSpan? quiesceTimeout, Func<DateTimeOffset>? clock)` (all optional);
    `AttachAll(WorldRuntime, IEnumerable<IWorldFeature>)` (once, at start), `List()`, `Find(name)`, `IsDetached(feature)`,
    `DetachAsync(name, HotSwapOrigin?)`, `AttachAsync(name, HotSwapOrigin?)`, `CycleAsync(name, HotSwapOrigin?)` (each
    `Task<FeatureResult>`); `FeatureState { Attached, Quiesced, Detached, Faulted }`, `FeatureStatus`, `FeatureResult(Ok,
    Detail, HotSwapResult)`. Registered as a singleton by `AddWorldFeatures`; `AttachWorldFeatures` goes through it and
    `StopWorldFeaturesAsync` skips detached and faulted features (their quiesce drained them; neither holds a subscription).
  - `WorldLifecycleHooks : IWorldLifecycleHooks` with `RunAsync(stage, ct)` and `Registered(stage)`; internal
    `RegisterLast(stage, name, hook)` for a core hook that runs after every ordinary hook of its stage (the managed-bot
    orphan sweep, so it follows the playerbot shutdown whatever order features attached in);
    `TeleportWaiverRegistry : IWorldFeature, IAreaTriggerWaiver, ITeleportWaiverRegistry` (discovered like any feature,
    so `TeleportHandlers` consults it unchanged); both registered by `AddWorldFeatures` with their interfaces.
  - `FeatureCommands` (`.feature list|detach|attach`, `RootName = "feature"`), added by `AddFeatureCommands()` inside the
    `World:HotCode:Modules:Enabled` block of `AddWorldDaemon`.
- `HotSwapLane.Feature` (reporter text "Server feature '<name>' <action>").

Semantics: commits run through `WorldCommit` (posted, deadline, never applied late). A failed `Detach` or unsubscribe is
rolled back in the same tick; a failed `AttachFrom` undoes its subscriptions and leaves the feature detached with its
handover; a failed quiesce detaches nothing (detach and re-attach in one tick restart the feature: the feature's `Detach`
may refuse while work the quiesce did not drain still runs, which the host rolls back, and its `AttachFrom` keeps, never
duplicates, a service the quiesce did not stop; `ManagedPlayerbotFeature` does both, see 4.5); a commit that misses
its deadline leaves the feature `Quiesced` (attached), and `.feature attach` re-attaches it; only a rollback that throws
leaves it `Faulted` (out of the world, handover kept). A `Stateless` feature is re-attached with a null handover.

Evidence (compiler and tests): `dotnet build ArcaneCore.slnx -c Debug` 0 warnings / 0 errors; `FeatureLifecycleTests`
14/14 and `FeatureSeamTests` 8/8 (a probe feature with an `Updated` handler, a login handler and a spell observer: gone
after detach, back exactly once after attach); `CommandReferenceTests` 7/7 with the regenerated `gm-commands.md`; a
related regression set (Docs, HotCode, Teleport, CharacterDelete, WiringAudit, ServerLifecycle filters) 506 passed,
1 skipped. RED runs, each restored and rebuilt green: bag disposal skipped (13 tests fail), `UnregisterObserver` not
removing (11 fail), rollback, pinned refusal and waiver union disabled together (exactly those 5 tests fail), attach
failure not undoing its subscriptions (its 1 test fails).

Not built yet after part 2 (section 4.5): the architecture test against a bare `+=` from pack code (the part 2 test checks
type references, not how a subscription is made), a pack-load check of `ContractsVersion`, the 49 static
`ConditionalWeakTable`s and the other blockers of 4.3. Part 2 made `WorldHost` run `WorldLifecycleHooks` and
`PlayerbotGroupCoordinator` register its waiver.

### 4.3 Known unload blockers (from reading)

49 static `ConditionalWeakTable`s keyed by long-lived core objects (each must be cleared on detach), core events with
missing unsubscribes (`GroupManager.LeaderChanged`, `ItemCountChanged`, `MapCombat.UnitKilled`), `SpellSystem` with no
`UnregisterObserver`, an `ILoggerProvider` (`PlayerbotQaLogTap`) inside the core logger factory, a static `Meter`,
`PlayerbotAnomalyHistory.Shared` subscribed to `AnomalyLedger.Shared` and never disposed, dedicated chat threads and a
memory-mapped GGUF model, fire-and-forget tasks, and core statics calling pack statics (`PlayerbotMount`,
`PlayerbotPacketIntake`). `ManagedPlayerbotFeature`'s only exit today logs every bot out.

### 4.4 Phases

- **2a, contracts and lifecycle, no code move (about 2 to 3 engineer-weeks):** contracts assembly; the about 20 core
  call sites inverted; lifecycle interfaces, `FeatureSubscriptions`, `SpellSystem.UnregisterObserver`, a generic
  startup/shutdown hook, a waiver registry; a core `ManagedBotRegistry` split out of `ManagedPlayerbotFeature._active`;
  Detach/AttachFrom on the 6 bot features while they are still inside World, tested by detach and re-attach in process.
  *Status:* part 1 **built** (contracts assembly, lifecycle interfaces, `FeatureSubscriptions`,
  `SpellSystem.UnregisterObserver`, hook registry, waiver registry, `FeatureHost` and `.feature`, tested with a probe
  feature, section 4.2); part 2 **built** (hooks run by `WorldHost`, `ManagedBotRegistry`, call sites inverted except
  the 4 allowlisted core types, Detach/AttachFrom on the 6 bot features, the architecture test, section 4.5). Still
  planned in 2a: the options types into the contracts assembly (clears 3 of the 4 allowlist entries) and
  `ClusterCommands` onto the registry (the 4th).
- **2b, move the code (about 3 to 5 weeks):** Playerbots into its own project, the compiler's CS0122/CS0246 list as the
  authoritative coupling inventory, re-point the 449 test files, still loaded in the **default** context and soaked.
  Useful on its own as modularization.
- **2c, swap (about 3 to 4 weeks plus soak):** collectible load through `ModuleHost` (allowlist, audit, tick-boundary
  commit, rollback to the old pack). v1 is a **cold swap**: quiesce, log bots out through the existing path, unload,
  load, restore bots from the database; bots are absent for seconds, players unaffected. A CI unload test loads the
  pack, runs ticks with bots, swaps, collects and asserts the old context is gone. `.botpack status|swap`.
- **2d, warm handover (optional, 3 to 5 weeks):** sessions and players stay logged in through the core registry and
  brains are rebuilt in place, gated on bots being quiescent (not on a ship, taxi, elevator, battleground or dungeon
  run). Only if the seconds of bot absence matter in practice.

Total to a reliable cold swap: about 8 to 12 engineer-weeks. Expect 1 to 3 rounds of "the old context did not unload"
found only with a heap dump.

### 4.5 Tier 2a part 2: bot registry, inverted call sites, detachable bot features (built on `claude/hr2-bots`)

**Built** (branch `claude/hr2-bots`, based on `claude/hr2-lifecycle` @ `8727d55d`; compiler and tests, see Evidence). The
bot features are still inside `ArcaneCore.World` (2b moves them); what changed is that the core no longer needs them
and that each one can leave and rejoin a running world with bots online.

Core-owned state (outlives a detach or a pack swap):

- `ArcaneCore.World.Bots.ManagedBotRegistry` (World, a pinned discovered feature): `ManagedBot(record, scope, session)`
  per logged-in bot (`BotId`, volatile `Record`, `Scope`, `Session`, `Name`, `Running`); `Bots`, `Count`,
  `RunningCount`, `RunningNames()`, `TryAdd`, `TryRemove(bot)` (that entry only), `TryGet`; the `SplitBrain` latch; three
  one-slot seams, each claimed by the pack through its `FeatureSubscriptions` bag so a detach empties it:
  `UseSessionPolicy` / `SessionPolicy`, `UseCastRefusals` / `CastRefusals`, `UseHangSink` / `HangSink` (a second claim
  throws; a stale registration's disposal never clears a newer one). At `BeforeWorldStop` (after the playerbot shutdown
  hook: it is registered with `RegisterLast`, so it runs after every ordinary `BeforeWorldStop` hook whatever order the
features attached in) and at `StopAsync` it closes and flushes any bot still logged in (only possible when the
  bot feature was detached) and leaves its row as it is, so the next start restores it.
- Contracts (`ArcaneCore.World.Contracts`, namespace `ArcaneCore.World.Bots`): `IManagedBotSessionPolicy { Lean,
  KeepsPacket(opcode), BeforeAction(session, player, opcode) }`, `IBotCastRefusalObserver.OnCastRefused`,
  `IBotTickHangSink { Warmup, ReportHang(phase, work, message, occurrences) }`; `PlayerbotStatus`,
  `PlayerbotOperationResult`, `IPlayerbotService` moved here. `ArcaneCore.World.Features.IWorldServiceModule.Register(
  services, configuration)`, discovered by `AddWorldDaemon` (`WorldServiceCollectionExtensions.ServiceModules`).
- `ArcaneCore.World.Bots.InstanceEncounterCatalog` (pinned): the `instance_encounter` rows; `.reload instance_encounter`
  swaps it instead of the group coordinator, which reads it.
- Pure types moved out of the pack: `PlayerbotExploreAtlas` to `ArcaneCore.World.Bots`; `PlayerbotItemScore`,
  `PlayerbotStatWeights`, `PlayerbotWeaponStyle`, `PlayerbotTalentBuild(s)` to `ArcaneCore.World.Bots.Progression`.

Core call sites inverted: `WorldHost` runs `WorldLifecycleHooks` (`AfterWorldStart` after `world.Start()`,
`BeforeWorldStop` before `world.Stop()`; one failure is rethrown as itself, a cancellation as
`OperationCanceledException`); the playerbot pack registers `playerbots.restore` and `playerbots.shutdown`.
`AddWorldDaemon` no longer lists bot services (`PlayerbotServiceModule` does, same registrations, same order).
`ManagedWorldSession` reads the session policy per use (no policy, i.e. pack detached: the full client path, always
correct, only dearer). `SpellHandlers`, `TickWatchdogFeature`, `ServerInfoDiagnostics` and `WorldMetricsFeature` go
through the registry; `AnomalySummaryFeature` no longer touches `PlayerbotAnomalyHistory` (the pack does).

Detachable bot features (`IDetachableWorldFeature`, every subscription through the bag):

| Feature | Kind | Quiesce | Handover / rebuilt on attach |
|---|---|---|---|
| `ManagedPlayerbotFeature` | Handover | refuse new operations (`playerbots-detaching`), wait for the last checkpoint and the operation in flight, stop policy engine, navigation executor, chat | hands over per-bot set-asides, scripted controllers, shared set-asides, homes, start-up set-asides, fault quarantine, conflict ids, clock, status lines; rebuilds a brain and party AI for every registry entry |
| `PlayerbotPopulationFeature` | Handover | no new pass, wait for the pass and the terrain lookup | creation times, started/parked times, clock; a rush in progress is stopped |
| `PlayerbotGroupCoordinator` | Stateless | nothing to stop | groups finished (`coordinator-detached`), waiver registered through `ITeleportWaiverRegistry` |
| `PlayerbotBattlegroundCoordinator` | Stateless | nothing to stop | bots in a match leave it, queue state cleared |
| `PlayerbotSocialFeature` | Stateless | start no new registry refresh (a flag the next attach clears), one world-thread hop, wait for the refresh in flight | conversations and chatter stopped, map and guild subscriptions undone |
| `PlayerbotQaFeature` | Stateless | cancel the background pass and wait for it and the publish as long as the host allows (a pass still running fails the quiesce, it is not abandoned), then save and export | findings reloaded from the store file; hang sink and cast observer re-registered; a new pass starts only after the old one ended |

A quiesce of `ManagedPlayerbotFeature` that fails (an operation such as a fleet reset, a start or a slow checkpoint holds
the operation lock past the host's quiesce timeout) is undone safely (fix cycle 1 of the tier 2 review): `Detach`
refuses, changing nothing, while the operation lock is held, so the host rolls back and the running operation is not
raced; `AttachFrom` keeps the policy engine, navigation executor and chat services that are still running instead of
starting a second set beside them (the quiesce stops them only after the lock wait). The stop of those services takes
each field before stopping it, so an attach overlapping a quiesce the host gave up on never ends with two of a service
(it can end with none of one until the next attach; the bot code treats a missing one as off). A detach that races an
operation queued on the lock just as the quiesce released it is refused the same way (rare; retry).

Architecture test: `tests/ArcaneCore.World.Tests/Architecture/PlayerbotCoreBoundaryTests` reads the World assembly's IL
(`System.Reflection.Metadata`: signatures, attributes, constraints, locals, catch types, every IL token) and fails if a
core type (outermost type outside `ArcaneCore.World.Playerbots`) references a pack type and is not on the allowlist,
or if an allowlist entry no longer references the pack (the list only shrinks). A positive control
(`ScannerFindsKnownReferences`) proves the scanner sees references at all. Before this lane it listed 17 core types.
**Allowlist left (4)**:

- `ArcaneCore.World.Cluster.Tooling.ClusterCommands`: `GetRunningBotNames` reads `ManagedPlayerbotFeature` (Cluster/**
  belongs to another lane; fix: `ManagedBotRegistry.RunningNames()`).
- `ArcaneCore.World.Reload.ConfigContentReloadable`, `ArcaneCore.World.Reload.WorldConfigKeys`,
  `ArcaneCore.World.Reload.WorldConfigView`: the playerbot options types. They move to the contracts assembly as plain
  data (about 20 files, 4.1), which clears all three.

Behaviour with nothing detached (from reading, plus the regression run below): the same registrations, the same restore
and shutdown order, the same lean session path (the policy reads the same options object), the same QA hang and cast
reports. Two observable differences: `.server info` counts running bots from the registry (logged in and running) instead
of the last published status lines, which lag by up to one publish; and the watchdog's alert subscriber is installed
even without an announcer (a production host always has one, so production is unchanged).

Limits (from reading): handover values are still World-assembly types (`PlayerbotSetAside`, `PlayerbotHome`, the private
`Quarantine`); `FeatureHandover` accepts them only because World is not collectible, so 2b must move them to contracts
before a collectible pack. While the bot feature is detached the bots stand idle in the world (no brain), operator
commands answer `playerbots-detaching`, and the population makes no pass. A bot in a battleground is taken out of its
match by a battleground coordinator detach. No 2d gate on bot quiescence (ships, taxis) yet: a cycle mid-flight
rebuilds the brain mid-flight.

Evidence (compiler and tests): `dotnet build ArcaneCore.slnx -c Debug` 0 warnings / 0 errors.
`PlayerbotFeatureDetachTests` (4), `ManagedBotRegistryTests` (2), `PlayerbotCoreBoundaryTests` (2) and
`TickWatchdogFeatureTests` (24) 32/32. The cycle test cycles all six features with three bots online and asserts: the
same `ManagedBot` entries and sessions still `InWorld`, rows still `Running`, scripted controllers kept, new brains,
the same subscription count per feature, the population clock advancing exactly with the world (one handler), a changed
goal written by the next checkpoint, and a stop going through the rebuilt feature. Regression filter (Playerbot, Reload,
ServerInfo, Metrics, WorldHost, Lean, FeatureLifecycle, FeatureSeam, Anomaly, Watchdog, Spell, Boost, Wiring, Teleport):
5331 passed, 22 failed, 30 skipped of 5383; the same 21 fail on the base commit `8727d55d` built and run the same way
(real-terrain, battleground, guild chat, bank and config-classification tests that fail on this machine either way), and
the 22nd (`PlayerbotCombatTelemetryTests.AFight_IsWrittenWithItsDecisionsAndOutcome_WhenTheNextFightStarts`) passed on
a rerun, alone.

Fix cycle 1 (review of tier 2; compiler and tests): `dotnet build ArcaneCore.slnx -c Debug` 0 warnings / 0 errors;
`PlayerbotFeatureDetachTests`, `ManagedBotRegistryTests`, `FeatureLifecycleTests`, `FeatureSeamTests` and
`PlayerbotCoreBoundaryTests` 33/33. New: `AQuiesceThatTimesOutBehindARunningOperation_DetachesNothing_AndLeavesOneOfEachService`
(the real feature with policy on, the operation lock held past a 300 ms quiesce timeout: the detach fails as "nothing
was detached", the same brain, chat, in-process provider and policy engine instances afterwards, then a cycle starts one
new set), `ALastHook_RunsAfterEveryOrdinaryHook_RegisteredBeforeOrAfterIt` and
`TheOrphanSweep_RunsAfterThePlayerbotShutdown_WhateverOrderTheHooksWereRegisteredIn`. RED runs, each restored and rebuilt
green: `AttachFrom` recreating its services (the quiesce test fails on the chat instance), `Detach` not refusing under
the lock (the quiesce test fails on the brain instance), `RegisterLast` registering an ordinary hook (the last-hook test
fails; the in-host orphan test still passes, as registration order happens to be right there today). The social flag
and the bot QA quiesce change have no dedicated test (from reading; the cycle tests still pass).

RED runs of part 2, each restored and rebuilt green: the bot feature not adding to the registry (the 3 tests with bots across a detach fail), no brain rebuilt on attach (the cycle and detach tests fail), the population's update handler added with a bare `+=` instead of the bag (the cycle test fails), the session policy not registered (the detach test fails), bot QA's hang sink not registered (7 watchdog QA tests and the QA cycle test fail), and `WorldMetricsFeature` naming `ManagedPlayerbotFeature` again (the boundary test fails). Against the base commit the boundary test listed 17 core types.

## 5. Tier 3: gateway plus blue/green handoff

Source: Tier 3 research (static reading, no real client used).

### 5.1 What exists (from reading)

| Piece | Status |
|---|---|
| `PlayerTransferCoordinator` (6 stages, abort before commit, roll-forward after) | **Test-only**: no production `ITransferSourceWorker` / `ITransferTargetWorker` / `ITransferGatewayRouter` |
| `ITransferDecisionStore` | In-memory only; no durable journal |
| `PlayerTransferBundle` | Record only; nothing captures or installs it; lacks group, pet, threat, cast, reputation, skills, talents, binds |
| `GatewayWorkerTransport` (envelope with generation, epoch, sequence; loopback TCP) | Hosted only by tests; not connected to `OpcodeTable` or `WorldSession` |
| `ClusterNodeBridgeFeature` | A monitor with a probe character; routes no traffic |
| Character fencing (epoch compare-and-swap, save-time token check), durable ID ranges | **Production-wired, opt-in** (`Cluster:Fencing:Enabled`, off by default) |
| `CharacterPersistenceBarrier.TryFlushCharacterAsync` | Production; the flush primitive for a handoff |
| `ManagedWorldSession` | Production socketless session; the template for a gateway-backed session |

### 5.2 Topology decision

A new small **`ArcaneCore.Gateway`** process (option b) owns the client TCP socket, the auth handshake, the live
`WorldHeaderCrypt` (its whole rolling state is four bytes, but exactly one writer may own it), PING/PONG, Warden, the
one-session-per-account claim and a bounded inbound buffer. It forwards plaintext opcode plus payload to a World over
loopback. This is the documented M15.1 target (`CLUSTERING_DESIGN.md`). Rejected: the Realm hosting it (Realm restarts
for patches too) and handing the socket and cipher between Worlds (`DuplicateAndClose`), which the clustering design
forbids ("never serialize a socket ... or cipher state into a handoff"), is Windows-only and cannot roll back.

### 5.3 Phases

- **3a, gateway passthrough (about 3 to 5 engineer-weeks plus real-client acceptance):** the gateway executable; auth
  moved out of `WorldSession.HandleAuthSessionAsync`; a `RemoteWorldSession` on the World side fed from
  `GatewayWorkerTransport`; client address forwarded for per-IP guards; `Combined` mode unchanged. No handoff yet; a
  gateway restart still drops clients, a World restart still drops them too.
- **Spike first (one day):** does the 1.12.1 client accept `SMSG_NEW_WORLD` to the **same** map? The codebase never
  sends it (`TeleportService.TeleportTo` routes same-map moves to the near path). The answer decides 3b's design.
- **3b, save-and-reload handoff with a loading screen (about 4 to 8 weeks after 3a):** World **standby mode** (load
  content, maps and nav; skip the writer lock, ownership claim, save queue, bot restore and schema DDL; internal port
  only); production transfer workers at whole-World granularity; a durable decision store in the characters database;
  fencing mandatory; an operator command. Drain protocol: warm B and pass the readiness probe and manifest checks; stop
  admission; freeze A at a tick boundary (gateway buffers input, answers PING); forced logout-equivalent save of every
  player including the spell-state save that today runs only on `PlayerLoggingOut`, then flush barriers and queues
  (any failure aborts); **epoch takeover by B is the point of no return**; B loads players, the gateway switches routes
  and synthesizes `SMSG_TRANSFER_PENDING` / `SMSG_NEW_WORLD`, and the client's `MSG_MOVE_WORLDPORT_ACK` reaches B;
  retire A. Before the epoch bump, rollback is: cancel the staged world-port, unfreeze A, kill B. After it, roll forward
  only; if B dies, clients reconnect normally.
- **3c, in-memory snapshot (3 to 6+ engineer-months, only if 3b's loss contract proves unacceptable):** capture and
  install `PlayerTransferBundle` across every feature, optional map snapshots so mobs do not reset, optional in-place
  switch with no loading screen. This is essentially clustering milestone M15.3.

**Loss contract for 3b** (what a player notices): a loading screen; the cast in progress, GCD, combat and threat,
open trade, loot, duel and similar transients are cancelled; engaged creatures reset; chat channel membership needs
a rejoin. Far better than a disconnect, not invisible. SQLite deployments probably cannot run it (single-writer lock);
MariaDB or PostgreSQL is assumed.

### 5.4 Phase 3a as built (`claude/hr2-gateway`)

Built and covered by `tests/ArcaneCore.Gateway.Tests` (22 tests: wire, link envelope, and a mock-client login, character
create, enter-world and ping through the gateway against the synthetic realm and world). Operator page:
[gateway.md](../areas/gateway.md).

- `ArcaneCore.Gateway` executable: client socket, auth challenge and digest check against the realm-stored session key
  (the same refusals as `WorldSession.HandleAuthSessionAsync`, which stays in place for `Combined` mode), header cipher,
  PING/PONG, per-account claim, outbound bound, `NetGuard` admission, link reconnect with backoff.
- World worker mode (`Cluster:Worker:Enabled`, default `false`; with it off nothing is registered): a remote-mode
  `WorldSession` per client (`WorldSession.Remote.cs`, one branch in `WorldSession.Send`), `RemoteWorldSession`,
  `GatewayWorkerEndpoint`.
- Session link on the `GatewayWorkerTransport` framing (new frame types 11 to 17): HMAC challenge on a shared secret, then
  the manifest check; every packet in the generation/epoch/sequence envelope, with stale or foreign packets dropped and
  counted on both ends. The client's `ip:port` is forwarded and becomes the worker session's `RemoteEndpoint`.
- Fix cycle 1 (review of tier 3): both ends refuse to start with a link secret shorter than 16 characters on **every**
  bind address (an empty secret on loopback let any local process register as a gateway and open a session for any
  account); the gateway takes a session's generation and queues its `SessionOpen` under one lock on the link
  (`GatewaySessionLinkClient.OpenAsync(Func<long>, Func<long, GatewaySessionOpenRequest>, sink, ct)`), so concurrent
  logins are no longer refused as stale. Compiler and tests: 0 warnings / 0 errors; `ArcaneCore.Gateway.Tests` 26/26
  (new: empty and 15-character secrets refused on loopback by both ends; an open whose generation was taken first is
  queued first; a burst of 64 concurrent opens all accepted). RED runs: the strength check reduced to a null check (the
  three secret tests fail), the open lock removed (the ordering test fails, and 14 of the 64 burst opens are refused).

Differences from 5.2 and what is left of 3a:

- **Warden runs on the worker**, not the gateway: its opcodes are forwarded unchanged. Moving it to the gateway is needed
  before 3b (a handoff must not restart the Warden exchange).
- **No real-client acceptance yet** (the 3a exit criterion in section 9).
- Manifest schema and content versions are operator labels, not read from the databases.
- The link protocol lives in `ArcaneCore.World`; the gateway references the whole World assembly until a shared protocol
  assembly is split out.
- The gateway's `Gateway:*` keys are not in the configuration catalog (no `ConfigDaemon` value, no `.conf.dist`, no
  `--check-config`) and it exports no metrics.
- The gateway's session generation and `SessionRegistry`'s own generation are independent counters.
- The worker is not authenticated to the gateway, and the link has neither encryption nor a per-frame MAC (gateway.md).
  Also open from the tier 3 review: no timeout on the gateway's wait for the worker's open reply, an owner-epoch change
  drops packets without closing the session, unauthenticated connections to the worker port are not capped, one link
  queue is shared by every client, and Warden timing includes link jitter.
- The 5.1 rows are unchanged otherwise: `GatewayWorkerTransport` is now used in production by worker mode, the transfer
  coordinator is still test-only.

## 6. Tier 4: edit and continue stays dev-only

The `dotnet watch` runner patches a Debug process: no tick atomicity, no rollback, rude edits, unmeasured Debug tick
cost, server code with the SDK on the box. None of that is acceptable on a production server, and no tier turns it
into a production path. It remains the fastest loop for a developer, gated by `HotCodeGuard` exactly as today.

## 7. Invariants (every tier)

1. **Immutable snapshots.** A live registry or store is replaced as a whole reference, never mutated in place; a
   reader sees the old snapshot or the new one.
2. **Commit on the world thread at a tick boundary.** Candidates are built and validated off the world thread; the
   swap runs through `WorldRuntime.Post` before any map update. A commit the world thread does not pick up within its
   deadline is cancelled for good.
3. **All or nothing.** Every slot of a candidate is validated before any is touched; a rejected or failed swap leaves
   live state exactly as it was (Tier 1 module commits, Tier 2 detach/attach with re-attach on failure, Tier 3 abort
   before the epoch bump).
4. **Built-ins are not overridable.** A module or pack adds and replaces only what it owns; replacing built-in code
   is a restart (Tiers 1 and 2) or a process handoff (Tier 3).
5. **Security model unchanged.** Off by default; SHA-256 allowlist; Administrator-only commands with no path or code
   argument; every decision audited and announced; Production needs explicit opt-in. A pack is a module with more
   surface, not a new trust level. The Tier 3 gateway holds session keys, so it is security-critical code with the
   smallest possible surface.
6. **Vanilla fidelity.** With every lane off, the server behaves exactly as a build-5875 retail server; no tier
   changes packets, timings or rules a client can see except the documented Tier 3 handoff (a loading screen, a
   retail packet sequence).
7. **Honest unload.** A retired context that is still referenced is counted and shown; the host never forces a
   collection, and a leak is a bug to fix, not a state to hide.

## 8. What still needs a restart, per tier

| After | Still a restart (or, after Tier 3, a handoff) |
|---|---|
| Tier 1 | Built-in code on Release; features, DI, `IDataModule`s/schema, map-specific entry AI and built-in boss scripts; listener, tick interval, the 8 restart-only config keys; an event-handler change reaching a live creature in combat or a temporary creature (database creatures are respawned, staggered); a loaded instance keeps its old script until it unloads |
| Tier 2 | Everything above except the pack's own code and features; contract changes (options shapes, service interfaces) restart, because the contracts assembly lives in the default context; pinned core features |
| Tier 3b | Nothing forces a disconnect except a gateway update, a SQLite deployment, or a crash; a handoff still costs a loading screen and the 3b loss contract |
| Tier 4 | Unchanged (dev-only) |

## 9. Rollout phases and exit criteria

| Phase | Exit criteria |
|---|---|
| T1-int: merge lanes, wire `ModuleHost` (**met** on `claude/hr2-t1finish`, except the `.hotmodule reload` command path, tested through `ModuleHost`, not the chat handler) | Combined build 0 warnings; filtered tests green with RED proofs for every new seam (including the instance/EventAI tests that have none yet); a fixture module that adds, replaces and removes a spell script, an instance script and an EventAI action through `.hotmodule reload`, with the unload proven collected; a post-validation failure rolls every slot back |
| T1-live | A module with spell scripts reloaded on a dev server while a real client casts; announcement and audit lines present |
| 2a | Bot features detach and re-attach in process with bots online; no Playerbots type referenced from core (architecture test) |
| 2b | Playerbots in its own project, default context, soaked with the usual bot population and no behavioural regression in the playerbot QA reports |
| 2c | CI unload test green (old context collected after swap), cold swap on a dev server with hundreds of bots and no player disconnect |
| Spike | Same-map `SMSG_NEW_WORLD` result on a real 1.12.1 client, written down |
| 3a | Real client and mock client play through the gateway; per-IP guards see the client address; `Combined` unchanged |
| 3b | Handoff of a populated dev server with fencing on: no disconnect, freeze time measured and bounded, abort tested at every stage before the epoch bump, roll-forward tested after it |

## 10. Open risks

- **Partial module commits.** Resolved: a failure after validation is rolled back. The remaining case is an undo step
  that itself throws ("ROLLBACK INCOMPLETE"); it is reported, and only a restart then restores a known state.
- **Leaks found only at runtime.** Unload leaks are invisible statically; every tier needs the weak-reference probe
  and, for Tier 2, a heap-dump round.
- **Version skew.** A module or pack built against another server build fails at JIT time, not at load. A contracts
  version stamp (Tier 2) and the manifest check (Tier 3) catch the known cases only.
- **Playerbots is bigger than the core it plugs into** and reaches into session internals; the contract surface will
  keep growing.
- **Same-map `SMSG_NEW_WORLD` is unverified.** If the client rejects it, 3b needs a detour (an intermediate map or the
  in-place sequence, which is riskier).
- **Freeze length** grows with players times flush latency (barrier default 15 s at the extreme); not measured.
- **Process-wide caches** (spellbook cache, guilds, auctions) must be loaded by the standby World after the old one's
  final flush, or reloaded at cutover.
- **Interaction with M15 clustering.** Tier 3 reuses M15 contracts; the two plans must not diverge (one gateway, one
  transfer coordinator).
- **Stale source docs.** `docs/integration/cluster-transfer-state.md` is partly stale (several "memory-only" rows now
  have stores); re-verify before relying on it.
