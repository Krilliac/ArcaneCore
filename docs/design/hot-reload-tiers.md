# Hot reload tiers (design)

Status: design, owner go on 2026-10-16 for tiers 1 to 3. Tier 1 is **built and wired** into the module lane
(spell scripts, instance scripts and EventAI handlers load, replace and unload through `.hotmodule`; see
`docs/areas/code-hot-reload.md`). Still open for Tier 1: rollback of a post-validation failure (section 3.5), RED proofs
for the instance/EventAI registry tests, and a collected-unload proof for the new slots. Tier 2a part 1 (contracts
assembly and feature lifecycle seams, section 4.2) is **built** on `claude/hr2-lifecycle`; the rest of Tier 2 and Tier 3
are planned.
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
| Module lane (`.hotmodule`) | A collectible `AssemblyLoadContext` per module, Release-capable, SHA-256 allowlist: opcode handler groups, chat command groups, creature AI (`ICreatureAiGroup`) and global script hooks; load, replace, unload, all at a tick boundary | Spell scripts, instance scripts, EventAI handlers, map updaters, `IWorldFeature`s, `IDataModule`s, DI services, the server's own code | [code-hot-reload.md](../areas/code-hot-reload.md#module-lane-load-replace-and-unload-code-in-a-running-server) |

So today a restart is still forced by: any change to built-in code on a Release server, any new kind of module
contribution, a change to a whole subsystem such as Playerbots, and anything structural (features, DI, schema,
listener, runtime upgrade).

## 2. The tiers at a glance

| Tier | Removes the restart for | Mechanism | Status |
|---|---|---|---|
| 1 | Gameplay scripts that live outside the core: spell scripts, instance scripts, EventAI handlers, discovered types from several assemblies | Widen the module lane: every discovery site accepts a set of assemblies, and each script registry supports a validated, all-or-nothing swap | **Built and wired** into `ModuleHost` (rollback of a post-validation failure still open) |
| 2 | A whole subsystem's code, on Release: Playerbots first | Core plus reloadable packs: a contracts assembly, detachable features, one collectible context and one child DI container per pack | 2a part 1 **built** (contracts assembly, lifecycle seams, `.feature`); call-site inversion, bot features and 2b to 2d planned |
| 3 | Everything else: core code, runtime upgrades, structural changes | A gateway process keeps the client sockets; a second World is warmed and players are handed over (blue/green) | Planned (research done) |
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
  event rows when the engine builds them, so an event-handler swap reaches a creature at its next respawn.
- Same rule as spells: built-ins cannot be overridden or removed; a module replaces its own entry by removing and
  re-adding it in one swap.

Evidence: build green; the two new classes 10/10 alone, 42/42 together with the existing instance and EventAI tests.
**No RED run** was done for these, so the tests are not yet proven to fail without the change. There is no counter of
creatures still running a retired EventAI event handler (it would need `EventAiEngine` changes).

### 3.4 Not in Tier 1

Map-specific entry AI and the built-in dungeon boss scripts (consulted before module registrations), `IWorldFeature`s,
`IDataModule`s and DI services stay out. Features belong to Tier 2; tables need a schema story (section 9).

### 3.5 Integration step (wired; rollback still open)

- Merge the three lane branches into `claude/hot-reload-tiers` and build them together (each built alone so far).
- `ModuleHost` builds one candidate per module covering all slots (opcodes, commands, creature AI, hooks, spell
  scripts, instance scripts, EventAI events and actions), validates every slot before touching any, and commits all
  slots in the same posted tick-boundary action. Today a failure after validation leaves a module "only partly
  applied"; with more slots that case must become a real rollback (the `ReloadTransaction` undo-step pattern from the
  content lane), not a documented limit.
- Unload: `RetiredStillReferenced` must include the new reasons a context stays alive (a loaded instance still on a
  retired script, a creature with a retired event handler, scheduled spell-script work).
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
    `StopWorldFeaturesAsync` skips detached features (their quiesce drained them).
  - `WorldLifecycleHooks : IWorldLifecycleHooks` with `RunAsync(stage, ct)` and `Registered(stage)`;
    `TeleportWaiverRegistry : IWorldFeature, IAreaTriggerWaiver, ITeleportWaiverRegistry` (discovered like any feature,
    so `TeleportHandlers` consults it unchanged); both registered by `AddWorldFeatures` with their interfaces.
  - `FeatureCommands` (`.feature list|detach|attach`, `RootName = "feature"`), added by `AddFeatureCommands()` inside the
    `World:HotCode:Modules:Enabled` block of `AddWorldDaemon`.
- `HotSwapLane.Feature` (reporter text "Server feature '<name>' <action>").

Semantics: commits run through `WorldCommit` (posted, deadline, never applied late). A failed `Detach` or unsubscribe is
rolled back in the same tick; a failed `AttachFrom` undoes its subscriptions and leaves the feature detached with its
handover; a failed quiesce detaches nothing (detach and re-attach in one tick restart the feature); a commit that misses
its deadline leaves the feature `Quiesced` (attached), and `.feature attach` re-attaches it; only a rollback that throws
leaves it `Faulted` (out of the world, handover kept). A `Stateless` feature is re-attached with a null handover.

Evidence (compiler and tests): `dotnet build ArcaneCore.slnx -c Debug` 0 warnings / 0 errors; `FeatureLifecycleTests`
14/14 and `FeatureSeamTests` 8/8 (a probe feature with an `Updated` handler, a login handler and a spell observer: gone
after detach, back exactly once after attach); `CommandReferenceTests` 7/7 with the regenerated `gm-commands.md`; a
related regression set (Docs, HotCode, Teleport, CharacterDelete, WiringAudit, ServerLifecycle filters) 506 passed,
1 skipped. RED runs, each restored and rebuilt green: bag disposal skipped (13 tests fail), `UnregisterObserver` not
removing (11 fail), rollback, pinned refusal and waiver union disabled together (exactly those 5 tests fail), attach
failure not undoing its subscriptions (its 1 test fails).

Not built yet (from reading): the world host does not run `WorldLifecycleHooks`, `PlayerbotGroupCoordinator` still is an
`IAreaTriggerWaiver` itself, the architecture test against a bare `+=` from pack code, a pack-load check of
`ContractsVersion`, the 49 static `ConditionalWeakTable`s and the other blockers of 4.3, and any detachable bot feature.

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
  feature, section 4.2). Still planned: wiring the hooks into `WorldHost`, the call-site inversion (options and service
  interfaces into the contracts assembly), `ManagedBotRegistry`, Detach/AttachFrom on the bot features, the architecture
  test.
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
| Tier 1 | Built-in code on Release; features, DI, `IDataModule`s/schema, map-specific entry AI and built-in boss scripts; listener, tick interval, the 8 restart-only config keys; an event-handler change reaching a live creature before it respawns; a loaded instance keeps its old script until it unloads |
| Tier 2 | Everything above except the pack's own code and features; contract changes (options shapes, service interfaces) restart, because the contracts assembly lives in the default context; pinned core features |
| Tier 3b | Nothing forces a disconnect except a gateway update, a SQLite deployment, or a crash; a handoff still costs a loading screen and the 3b loss contract |
| Tier 4 | Unchanged (dev-only) |

## 9. Rollout phases and exit criteria

| Phase | Exit criteria |
|---|---|
| T1-int: merge lanes, wire `ModuleHost` | Combined build 0 warnings; filtered tests green with RED proofs for every new seam (including the instance/EventAI tests that have none yet); a fixture module that adds, replaces and removes a spell script, an instance script and an EventAI action through `.hotmodule reload`, with the unload proven collected; a post-validation failure rolls every slot back |
| T1-live | A module with spell scripts reloaded on a dev server while a real client casts; announcement and audit lines present |
| 2a | Bot features detach and re-attach in process with bots online; no Playerbots type referenced from core (architecture test) |
| 2b | Playerbots in its own project, default context, soaked with the usual bot population and no behavioural regression in the playerbot QA reports |
| 2c | CI unload test green (old context collected after swap), cold swap on a dev server with hundreds of bots and no player disconnect |
| Spike | Same-map `SMSG_NEW_WORLD` result on a real 1.12.1 client, written down |
| 3a | Real client and mock client play through the gateway; per-IP guards see the client address; `Combined` unchanged |
| 3b | Handoff of a populated dev server with fencing on: no disconnect, freeze time measured and bounded, abort tested at every stage before the epoch bump, roll-forward tested after it |

## 10. Open risks

- **Partial module commits.** Today a failure after validation leaves a module "only partly applied". More slots
  make that likelier; T1-int must replace it with a rollback.
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
- **Instance and EventAI swap tests have no RED proof yet.**
