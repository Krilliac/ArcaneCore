# Map-local map stages and intra-continent parallelism: design (2026-10-24)

Status: design only. Nothing here is implemented, and no code or configuration changed with this page. It continues
[multithreading-plan-20261017](multithreading-plan-20261017.md) (W2 thread ownership, W3 parallel maps, W4 bot think per map,
W7 intra-map parallelism) and [parallel-maps-status-20261017](parallel-maps-status-20261017.md) (what the serial gate covers
today). The question it answers: how the map stages Packets, Heartbeat and Output, and whatever they pull in, leave the
world's serial gate, so the map threads really overlap, and how to use more than one thread per busy continent.

Source pin: `origin/main` at `2c266618` (2026-10-24). Every file:line below is from that commit. Live evidence comes from the
world logs on Nate's PC (read-only): the current log (2026-10-24, from 00:00 UTC) and the 2026-10-21 logs, which are the last
ones with parallel-phase lines. Log timestamps are UTC.

Proposed configuration keys appear in **bold**, not as code: none of them exist yet, and the docs key audit
(`DocKeyAuditTests`) only accepts code-formatted keys that an options class reads.

What is measured and what is reasoned: the tables in §1 are log measurements. Everything in §2-§7 is static reasoning from the
code. Each wave in §5 says what it is expected to do, and those expectations are estimates until its measurement has been
taken.

---

## 0. Summary

1. **The gate is not mostly spent in Packets, Heartbeat and Output.** In 51,122 slow-map lines for maps 0 and 1 from
   2026-10-21 (4 map threads, a biased sample: only updates over the slow threshold), the means are: packets 8.9 ms (p50 1.0),
   heartbeat 4.4, active cells 0.8, visibility 3.2, values and flush about 0. Creatures are 62 ms (p50 59.5), MapCombat 3.0 and
   EnchantUpdater 9.2 when they appear. Each map waited 32.5 ms on average for the gate. Making the three stages map-local is
   necessary, but it moves only about 10-15 % of a continent's update out of the gate. The rest is creature AI, combat and auras.
   All of those share one root cause with Output: the **spell and combat state is world-wide** (`SpellSystem._states`, one
   instance for every map, `src/ArcaneCore.Game/Spells/SpellSystem.cs:30`), and so are the feature handlers that combat
   events call.
2. **The serial phases cost more than the maps phase today.** The current log's TickOverrun lines (the slower minutes) give
   features p50 51 ms / p99 117-125 ms against maps p50 31-33 ms / p99 76-82 ms. In the top features, `ManagedPlayerbotFeature`
   takes about 50 ms and `SpellFeature` (the world-wide aura and cast update, `SpellFeature.cs:197`) takes 20-46 ms. Moving the
   spell update into each map (wave 2) and bot think into each map (wave 5, plan W4) is where most of the tick goes.
3. **Check the live configuration before baselining.** The brief says live runs `World:Parallel:MapUpdateThreads` 4. But
   `RunMapsParallel` writes a "Parallel maps phase (60 s)" line every minute whenever it runs
   (`WorldRuntime.Parallel.cs:306`, `:311-342`), and the start-up line "Parallel maps phase: N map threads" is written once
   (`:152`). Neither appears in any world log after 2026-10-21. So either the running server is serial, or that logging was
   lost. Both explain "about 1.05 cores used". Wave 0 settles this before any A/B.
4. **Chosen mechanism.** (a) A **world-thread end-of-phase queue**: cross-map and world-wide effects made during the maps
   phase are posted per map and run on the world thread after the barrier, in a fixed order. (b) A **global-state guard**:
   world-wide services check that they are written only by the main world thread, or under the gate while gating still exists.
   It reports through the existing OwnerGuard and throws in tests. (c) A **per-opcode access class** for in-world packet
   handlers: MapLocal (runs on the map thread), Gated (runs inside the serial gate; the default for anything not yet audited),
   or Deferred (parked with the rest of that session's queue and resumed on the world thread at end of phase). (d) The same
   three-way class for **event subscribers** (UnitKilled, HolderAdded and the like) through a small `MapEvent<T>` type.
   Every switch changes ordering the same way at every thread count. That keeps the 1-vs-N determinism test meaningful.
5. **Chosen intra-continent design: per-stage parallelism inside the map, not spatial regions.** The map stays the unit of
   ownership. First, data-parallel kernels on idle map workers (compute in parallel, apply serially in join order; spline
   advance, visibility decisions, update-block building). Later, vmangos-style red-black cell stripes with a safe distance
   for creature AI. Splitting a continent into regions with their own owner threads and border hand-off is rejected: it needs
   ghosting and authority transfer on every boundary, and buys nothing the stripes cannot.
6. **Wave 1** (§5.2) is the mechanism plus the two cheap stages: the end-of-phase queue, the global-state guard, the opcode
   access table, and two switches, **World:Parallel:PacketsByOpcode** and **World:Parallel:LazyHeartbeatGate**. Wave 1 does
   not move Output; Output needs wave 2's per-map spell and stealth state. Both switches default off, which keeps today's
   behaviour exactly.

---

## 1. Live evidence

| Signal | Value | Source |
|---|---|---|
| Overlap of the maps phase | x1.44-1.45 on 4 map threads; map work 38.6-39.2 s in 26.6-27.1 s wall a minute; serial gate 90.9-93.3 k entries/min, 25.7-27.5 k contended, 66.8-67.9 s waiting/min (summed over threads); barrier wait about 0.2 s/min | minute lines, 2026-10-21 16:10-16:12 UTC |
| Gate wait per map update | mean 32.5 ms, max 2,050 ms, against own work mean 68.3 ms | 51,122 slow-map lines, maps 0/1, 2026-10-21 |
| Stage means (slow lines) | packets 8.9 (p50 1.0, p95 34.1); heartbeat 4.4 (p95 6.9); active cells 0.8; visibility 3.2 (p95 7.0); values ~0; flush ~0; creatures 62.3 (p95 87.3) | same |
| Most frequent top contributors | creature AI (every line), unit heartbeats (24 k), session packets (22 k), GameObjectMapSystem (20 k), visibility (17 k), EnchantUpdater (10 k), MapCombat (5 k) | same |
| One slow update, decomposed | map 0, 760 players: own work 56 ms, gate wait 8 ms; CreatureMapSystem 31.5 (AI 2.4, motion 9.3, spline pass 4.3, 3,056 wanderers outside the gate), MapCombat 21.2, EnchantUpdater 3.4; packets 0.3, heartbeat 1.3, visibility 1 | 2026-10-21 16:12:49 UTC |
| Tick body p95 | median 192 ms over 2,431 TickOverrun lines, from 72 to 2,752 ms (TickOverrun lines are written only on overruns, so this leans slow) | current log, 2026-10-24 |
| Phase split | features p50 51.2 / p99 117-125; maps p50 31-33 / p99 76-82; commands, world tick and autosave under 1 ms | same, last lines |
| Hung frames | 3,810 ms: features 2,617 (ManagedPlayerbotFeature 2,592), maps 1,193, verdict host-quiet. 3,942 ms: maps 3,796, gc 3,717 ms, machine memory 98 %, verdict gc-pauses | TickHang 20:04 / 20:06 UTC |
| Parallel-phase lines | none after 2026-10-21 (no start-up line and no minute line) | all world logs |

What follows:
- The gate holds most of each continent's work, so two continents serialize behind each other. Taking out Packets and
  Heartbeat (about 5-10 ms of a 60-70 ms update) helps little by itself. The large shares (creature AI, combat, auras) sit
  behind the same global spell and combat state that blocks Output.
- On a bot-heavy server, the Packets stage mostly measures logout completions and the per-player loop, not bot actions. Bots
  run their handlers from the features phase through `TryManagedAction` (`ManagedWorldSession.cs:136-163`), not through the
  queue. The opcode classification matters most because bot think per map (W4) calls the **same handlers**.
- Memory sits at 88-98 % of the machine, and one hang is a GC compacting paged-out heap. More threads mean more allocation
  contexts and more live scratch. Every wave records working set and GC pauses next to tick p95.

---

## 2. Audit: shared state each stage touches today

Classes used below:
- **L**: already map-local (only this map's objects, grid and per-map systems, plus thread-safe services: `Session.Send`,
  the fault and anomaly ledgers, logging, `Post`).
- **LC**: safe to make map-local with a local change (a counter made Interlocked, a dictionary moved onto the map or the
  player, a lazy gate around a rare call).
- **D**: needs a deferral to the world thread (post to the end-of-phase queue, §3.2).
- **R**: needs a redesign (the state is world-wide by construction and many paths use it).

### 2.1 Stage Packets (`Map.Update.cs:54-62`, `UpdatePacketsAndTimers` `:136-188`)

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| P1 | Per-player loop over a reused snapshot | `Map.Update.cs:139-156` | `_updatePlayers`, `_players` (the map's own) | L |
| P2 | Queue drain and thread assert | `WorldSession.cs:382-387` | `_worldQueue` (one consumer: the player's map thread); `Invariant.Assert(World.IsWorldThread)`, which is true on map threads because of `TickOwner` (`WorldRuntime.cs:93-94`) | L for the queue. The assert says nothing about global state. Add a guard (§3.3) |
| P3 | Per-packet bookkeeping | `WorldSession.cs:388-430`; `RecordHandlerLatency` `:529-535`; `HeuristicsWorldPacket` `WorldSession.PacketHeuristics.cs:134-141`; `LogIfSlowPacket` `:469` | session fields (owned by the player's map); `LatencyRegistry` (Kernel, shared); the `_heuristics` instance (shared detector) | LC: audit `PacketHandlerLatency.Record` and `heuristics.OnWorldPacket` for concurrent callers; make them Interlocked or per-session |
| P4 | In-world handlers: 260 `OnWorld` registrations in 46 files | `OpcodeTable.cs:75-76`; largest: `EconomyHandlers.cs` (27), `GuildHandlers.cs` (20), `NpcServiceHandlers.cs` (18), `GroupHandlers.cs` (17), `ChannelHandlers.cs` (16) | see P5-P9 | per opcode: §3.4 |
| P5 | Movement (the bulk for real clients) | `MovementHandlers.cs:25-35`, `:123-140` | `TeleportService._pending` (`TeleportService.cs:55`, read by `IsBeingTeleported` `:260`); `TransportSystem` (one per world, ships cross maps; `TakeJustBoarded` in `MovementHandlers.cs:54-66`); `AntiCheatFeature` (`:64`); relay through `Map.BroadcastToObservers` (map-local) | LC: teleport stage per player on `Player` (written on the main thread or in end-of-phase work only); transport boarding marks per player; anticheat state per session |
| P6 | Chat, emotes, who | `ChatHandlers.cs:48-51` | `ChannelManager` (world-wide), whisper target lookup (`FindOnlinePlayer`, a ConcurrentDictionary read, `WorldRuntime.cs:27-28`), mute stores | Emote: LC (map broadcast). Chat, whisper, channel, who: D |
| P7 | Group, guild, petition, social, economy (AH, mail), battleground queue, LFG | `GroupHandlers.cs`, `GuildHandlers.cs`, `PetitionHandlers.cs`, `SocialHandlers.cs`, `EconomyHandlers.cs`, `BattlegroundHandlers.cs` | `GroupManager._memberOf/_invitedTo/...` (`GroupManager.cs:22-27`, plain dictionaries), guild and mail stores, auction house, BG queues | D (whole handler at end of phase) |
| P8 | Spell cast, cancel, cancel aura | `SpellHandlers.cs:19-22` | `SpellSystem._states` (`SpellSystem.cs:30`, one Dictionary for every map); `GetOrCreateState` `:1884-1892` | R (wave 2), then L |
| P9 | Loot, quest NPC, gossip, trade, item use | `GameObjectLootHandlers.cs`, `QuestNpcInteractionHandlers.cs`, `NpcServiceHandlers.cs`, `ItemHandlers.cs` | loot registries, quest feature state, kill credit through `UnitKilled` subscribers (§2.4) | R (wave 3); until then Gated |
| P10 | Players in transit: only `MSG_MOVE_WORLDPORT_ACK` is handled | `Map.Update.cs:160-169`, `WorldSession.cs:411-428` | the ack puts the player into **another map** (cross-map); `_world.IsOnline` (ConcurrentDictionary) | D |
| P11 | Logout timers: `LogoutPlayer` | `Map.Update.cs:172-187` → `WorldRuntime.cs:443-451` → `RemovePlayer` `:418-438` | `PlayerLoggingOut` (26 subscribers, `WorldRuntime.Events.cs:56`), `Scripts.Player.OnLogout`, `_saveQueue`, `_onlineByName`/`_online` (ConcurrentDictionary), `_autosaveDue` (plain Dictionary, "world thread", `WorldRuntime.Autosave.cs:13`); `Session.OnLoggedOut` asserts the world thread (`WorldSession.cs:569`) | D |
| P12 | Overdue-logout anomaly check | `WorldSession.Observability.cs:199-215` | session fields, `AnomalyLedger` (thread-safe) | L |

### 2.2 Stage Heartbeat (`Map.Update.cs:66-74`, `UpdateHeartbeats` `:191-224`)

Note on the brief: unit regeneration, combat, aura and swing timers do **not** run in this stage. They run in `MapCombat`
(regen in `MapCombat.Regen.cs`, swings in `MapCombat.Melee.cs`), in `EnchantUpdater` and in the spell update
(`SpellSystem.Update`, called from the features phase). The Heartbeat stage is the 5.2 s `Unit.Heartbeat` timer.

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| H1 | Snapshot rebuild and loop | `Map.Update.cs:196-223` | `_objects`, `_heartbeatUnits`, `HeartbeatSnapshotObjectsVisited` (all the map's own); `Player.IsQuestSettlementPending` (`Player.QuestSettlement.cs:11`, set on the world thread) | L |
| H2 | Per-player timing for diagnostics | `Map.Update.cs:212-217` | the map's `MapUpdateDiagnostics` | L |
| H3 | The timer itself | `Unit.cs:28-38` | the unit's own `_heartbeatTimer` | L |
| H4 | `Heartbeat` event subscribers | only `SpellSystem.FoodDrinkVisuals.cs:16` (`TriggerFoodDrinkHeartbeat` `:28-41`) | `GetAuras(unit)` reads `SpellSystem._states` (`SpellSystem.Auras.cs:466`); `ConditionalWeakTable` (thread-safe); `SendToSet` sends to the map's set | LC now (gate only the invocation, and only when a subscriber exists; §5.2). After wave 2 it is L |

### 2.3 Stage Output (`Map.Update.cs:237-372`, visibility `Map.Visibility.cs`)

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| O1 | Bounding radii, moved players and objects, `_movedScratch` | `Map.Update.cs:243-313` | the map's grid, `_valuesQueue`, `_movedObjects` | L |
| O2 | Visibility decisions, visible sets, `_observers` | `Map.Visibility.cs:403-433`, `:447-484` | `viewer.VisibleObjects`, `_observers` (the map's own) | L |
| O3 | Visibility rules | `Map.Visibility.cs:435-445`; installed by `StealthFeature.cs:46`, `GhostVisibilityFeature.cs:24`, `GmCheatCommands.cs:267`, `ShadowfangKeepInstance.Events.cs:42` | `StealthVisibilityRule.CanSee` (`StealthVisibilityRule.cs:40-123`) reads `StealthRegistry` (one per world; `_stealthed`/`_hiddenUnits` HashSets and `_hidden` count, `StealthState.cs:26-29`, written by `SetVisibility` `:51` from aura code); `SpellSystem.HasAuraType` (`_states`); `Groups.GetGroupMembers` (`StealthVisibilityRule.cs:37`, `GroupManager`); `Relations.IsHostile`; `map.Collision.IsWithinLineOfSight` | R for the spell reads (wave 2). LC for `StealthRegistry` (per map: every stealthed unit is in one map). The group read is safe once group writes are Deferred (frozen during the phase, §3.3) |
| O4 | Create and values blocks | `Map.Visibility.cs:123`, `:430`; `Map.Update.cs:561-644` (`UpdateBlockWriter`) | the object's fields; `Session.WantsUpdateObjects` (`ManagedWorldSession.cs:216`) through the lazily cached `BotPolicy` (`:187-200`, a per-session field filled on first use: one writer, the owner map); `BotLeanStats` (Interlocked, allow-listed) | L (check `UpdateBlockWriter` statics in wave 2's audit) |
| O5 | Flush | `Map.Update.cs:646-647` → `PendingUpdates.FlushTo` → `Session.Send` (`WorldSession.cs:312`, under `_sendLock` `:339`) | thread-safe send | L |
| O6 | Ungated broadcasts held in the outbox | `Map.Broadcast.cs:35-42`, flushed at the next gate entry `Map.Gating.cs:19-30` | per-map outbox | L. Once Output runs outside the gate, the outbox flushes at the start of Output instead (same order) |

### 2.4 What the stages and the updaters drag in

| # | What | Where | Class |
|---|---|---|---|
| X1 | `SpellSystem`: per-unit cast and aura state for every map in one Dictionary; also `_reportedUnsupported*` sets (`SpellSystem.cs:33-34`), drunk players, death-aura removals, dynamic objects; the update runs once a tick for the whole world in the features phase (`SpellSystem.cs:236-266`, `SpellFeature.cs:197`) | R (wave 2: per-map state and a per-map spell updater) |
| X2 | `SpellSystem.HolderAdded/HolderRemoved` (`SpellSystem.Rules.cs:60`, `:63`): 32 subscriptions, from map-local rules (`DiminishingRule.cs:28-29`, `CombatStatAuras.cs:49-50`, `FormStatListener.cs:44-45`, `PaladinAuraRules.cs:34-35`) to world features (`BattlegroundFeature.cs:166-167`, `OutdoorPvPFeature.cs:72-73`, `ManagedPlayerbotFeature.cs:450`, `UnstuckCommands.cs:105`) | per subscriber through `MapEvent<T>` (§3.5) |
| X3 | `MapCombat.UnitKilled` (`MapCombat.cs:149`) and `DamageTaken` (`MapCombat.DamageEvents.cs:20`): 29 subscriptions. Progression `ProgressionFeature.cs:114`, reputation `ReputationFeature.cs:190`, quests `QuestObjectiveAdapter.cs:31`, honor `HonorKillRewards.cs:63-64`, loot `GameObjectLootFeature.cs:277-278`, `SpecialLootFeature.cs:90`, battlegrounds `MatchRuntime.cs:131`, instances `InstanceManager.Resolver.cs:286`, `DeathOutcomeWatcher.cs:86`, `PlayerbotKillObserver.cs:32-33`, `PlayerbotPvpKillFeed.cs:74`, `PlayerbotSocialFeature.cs:697` | per subscriber (wave 3) |
| X4 | Follow-on events: `LevelChanged` (`ProgressionFeature.cs:53`, `SkillsFeature.cs:126`, `TalentFeature.cs:101`, `PlayerbotSocialFeature.cs:679`), `ReputationChanged` (`ReputationFeature.cs:87`), `QuestRewarded` (`CreatureQuestScripts.cs:89`, `FishingExtravaganzaFeature.cs:109`, `PlayerbotConversations.cs:165`), `ItemCountChanged` (`QuestNpcFeature.cs:367`, `PetitionManager.cs:436`, `PlayerbotChatEvents.cs:156`) | mostly per-character state (LC if it is stored per player). Social, conversation and world-event handlers: D |
| X5 | `GroupManager` aura and stats marks from aura changes: `MarkAuraChanged` writes `_pendingAuras` (`GroupManager.cs:185-190`); `_sentStats` (`:24`, `:729-744`) | LC: the pending marks go on `Player`, and the group stats pass (world thread) reads and clears them |
| X6 | Teleports and other cross-map work through `Map.RunAfterUpdate` (`Map.Membership.cs:247`), run in stage Deferred (`Map.Update.cs:395-416`): far teleports `TeleportService.cs:438`, ship follow / homebind `TransportSystem.cs:679`, `:695`, totem updater install `TotemSystem.cs:349`, creature observer capture `CreatureMapSystem.cs:207` | far teleport and transport: D. Totem install and observer capture: L (they touch this map only) |
| X7 | Map creation from inside a map update | `WorldRuntime.cs:302-353` (`GetMapInParallelPhase`: lock, then gate, then `MapCreated` with 33 subscribers) | already handled (gated, joins next tick). Becomes D when the gate goes |
| X8 | Map unload requests | `WorldRuntime.cs:377-385` (locked list, served after the phase) | already D-shaped |
| X9 | Terrain shared by instances of one map id; grid load on a cell change | `Map.Update.cs:475-490` (takes the gate when a relocation touches the grid lifecycle); terrain clean-up once a tick after the maps (`WorldRuntime.cs:573`) | already handled |
| X10 | Navmesh owner path, path searches | `NavMeshPathfinder`; the path-job pool on `claude/bot-path-off-world-thread` | out of scope here (not redesigned) |
| X11 | Static mutable fields | `StaticMutableStateAuditTests.cs:18-50` (28 allow-listed). The scan skips `static readonly` fields (`:71`), but 45 `static readonly` Dictionary/List/HashSet fields exist in Game, World and Playerbots (most are lookup tables, e.g. `SelfResurrection.cs:18`, `ExclusiveAuras.cs:27`) | extend the audit (§6.4) |
| X12 | Shared feature singletons reached from per-map updaters: `TradeMapUpdater(this)`, `FlightUpdater(this)`, `TotemMapUpdater(this)`, `SkillPersistenceUpdater(this)` and the rest; 27 `IMapUpdater` types | per type: `[MapAccess]` with a reason; a runtime "reachable from two maps" audit (§6.4) |
| X13 | Feature event handlers on world events raised from map stages: `PlayerLoggingOut` (26), `MapCreated` (33), `MapUnloading` (20) | they run where the event is raised. Logout and creation become D, so the handlers stay main-thread |
| X14 | `Random.Shared` and the `Random` a system is built with (`SpellSystem.cs` constructor, `random ?? Random.Shared`) | thread-safe in production. A seeded `Random` that tests share across maps is **not** thread-safe: per-map seeded RNG before the 1-vs-N test covers spells (wave 2) |

### 2.5 Summary by stage

| Stage | Blocking items | Earliest wave it can leave the gate |
|---|---|---|
| Packets | P3 (audit), P4-P9 per opcode, P10/P11 cross-map | wave 1 (the stage, with every unaudited opcode Gated per packet); opcodes move out wave by wave |
| Heartbeat | H4 (one subscriber, reads spell state) | wave 1 (lazy gate around the event) |
| Output | O3 (spell state, per-world stealth registry) | wave 2 |
| Updaters | X1-X5 for MapCombat, EnchantUpdater, StealthDetectionUpdater, ReactiveUpdater; X3/X6 for the rest of CreatureMapSystem | waves 2-4 |
| Deferred | X6 | wave 3 (the end-of-phase queue) |
| Grid | X9 | stays gated: rare and short |

---

## 3. Mechanism

### 3.1 Principle: global state is frozen during the parallel phase

Today, gated work of one map runs at the same time as ungated work of another map. Ungated code may therefore read only state
that **no gated code writes during the phase**, and nothing checks that. The rule this design enforces:

> During the parallel maps phase, world-wide state is read-only. A change to it is posted to the end-of-phase queue and
> runs on the world thread after the barrier. Map state changes only on the map's own thread.

The serial gate stays the safe default for unaudited code. Each audited piece of code moves from Gated to MapLocal (it
touches only map state) or to Deferred (it posts its world-wide part). When nothing writes a world-wide structure during the
phase, any map thread may read it without a lock. Group membership, guild rank, channel lists and the online registry all
become safe to read this way.

### 3.2 The world-thread end-of-phase queue

```
// WorldRuntime (Game). Any thread during the maps phase; outside it, a plain call runs at the next drain.
public void PostAfterMaps(Action work);           // map thread: appended to TickOwner.CurrentMap's list (no lock)
internal void DrainAfterMaps();                   // world thread only, after the barrier (or the serial loop)
```

- **Storage:** a `List<Action>` per `Map` (`_afterMaps`). Only the thread updating that map writes it, so it needs no lock. A
  world-level list under a lock takes posts from threads that update no map (session tasks, helpers).
- **Drain point:** in `RunTick`, after the maps loop or `RunMapsParallel` returns (`WorldRuntime.cs:507-560`) and **before**
  `UnloadRequestedMaps` (`:567`). An instance emptied by a deferred logout or teleport can then unload in the same tick, as it
  does today.
- **Order:** maps in the order of the tick's map snapshot (`_maps.Values.CopyTo`, the same array at every thread count), then
  FIFO inside each map, then the world list. Work posted during the drain is appended and runs in the same drain, with a bound
  (for example 3 rounds, then the rest goes to the next tick's commands, with a warning). Each item is isolated (try/catch,
  `FaultLedger` "world.after-maps").
- **Context:** `IsParallelPhase` is false and the thread is the main world thread, so every existing world-thread assert holds,
  and features see exactly the threading they assume today.
- **Counters:** posted, drained, rounds and drain µs, added to `ParallelMapCounters` and to the minute line
  (`WorldRuntime.Parallel.cs:332-337`).
- **Serial loop:** with a switch on, the serial loop at `MapUpdateThreads` 1 posts and drains in the same way, so N=1 and N>1
  apply deferred work in the same order. With every switch off, nothing is posted: today's behaviour.

This is vmangos' `ScheduleFarTeleport` / `SwitchPlayersInstances` and TrinityCore's "delayed work after `wait()`", applied to
every cross-map effect.

### 3.3 Ownership and global-state asserts

- **Map state:** unchanged. `CheckOwner` (`WorldRuntime.Parallel.cs:114-138`) on map mutators (`Map.EnsureWorldThread`
  `Map.cs:245-256`, `OnObjectMoved` `Map.Update.cs:473-493`, `QueueValuesUpdate` `:426-435`).
- **World-wide state:** a new `WorldRuntime.CheckGlobal(string what)`. In the parallel phase, the caller must hold the serial
  gate (wave 1 meaning: "gated code only"). Once a structure is declared frozen (its writes are all Deferred), the check
  becomes "never during the parallel phase". It uses the same anomaly code family (`parallel` / `global-state`), the same
  once-per-call-site reporting and the same `World:Parallel:OwnerGuard` mode (Off, Report, Throw), so it needs no new key.
- **Where it goes first** (writers only; reads are not guarded because of the cost): `WorldRuntime.AddPlayer` / `RemovePlayer`
  and `_autosaveDue`; `GroupManager` mutators including `MarkAuraChanged`; `StealthRegistry.SetVisibility`;
  `SpellSystem.GetOrCreateState` and its `_states.Remove` sites; `TeleportService` `_pending` writes; `ChannelManager`
  mutators. With the gate in place, these fire only for code that already runs ungated. The live config ungates whole
  `GameObjectMapSystem`, and the wanderer motion runs outside the gate, so the first Report run is itself an audit.
- **`IsWorldThread` stays as it is.** It means "may run simulation code synchronously" (`WorldRuntime.cs:85-94`). The 44
  call sites keep working. Global services that need the strict meaning use `CheckGlobal` or `IsMainWorldThread` (`:97`).
- **Tests run with Throw** (§6).

### 3.4 Per-opcode access for in-world packet handlers

```
// ArcaneCore.World/Handlers/OpcodeAccess.cs (World layer: handlers live there; Game does not know opcodes)
public enum OpcodeAccess { Gated = 0, MapLocal = 1, Deferred = 2 }

internal static class OpcodeAccessList
{
    // Reviewed like the static-field allow-list: every entry has a reason. Anything absent is Gated.
    internal static readonly FrozenDictionary<WorldOpcode, (OpcodeAccess Access, string Reason)> Entries = ...;
}
```

- `OpcodeHandler` (`OpcodeTable.cs:52`) gains `Access`, resolved from the list when `OnWorld` registers (`:75-76`). A hot
  reload builds a new table (`WithNewHandlersFrom`, `TrySwap`), so the class stays with the opcode.
- **Default Gated.** An opcode nobody audited runs inside the gate at its position in the session's queue, exactly as the whole
  stage does today. The allow-list can only make things less gated. A missing entry is safe.
- **Execution** in `ProcessWorldPacketsCore` (`WorldSession.cs:382`), with **World:Parallel:PacketsByOpcode** on:
  - MapLocal: bookkeeping and handler run on the map thread, outside the gate.
  - Gated: bookkeeping and handler run inside `player.Map.EnterGate()` (a new public wrapper around `EnterSerialAndFlush`,
    `Map.Gating.cs:19-30`; it reenters cheaply if the map already holds the gate).
  - Deferred: the session **peeks** (`ConcurrentQueue.TryPeek`) and does not dequeue. It stops for this map update, records
    the remaining budget, and posts `ResumeWorldPackets(player, budget)` to the end-of-phase queue. On the world thread, the
    resumed call handles the deferred packet and continues with the rest of the queue (every class runs inline there),
    against the same `MaxWorldPacketsPerTick` budget. **A session's packets keep their order**: nothing after a deferred
    packet runs before it, and the whole queue is still handled in the same tick.
- **Transit (P10) and logout completion (P11)** go through the same queue: the map posts "handle transit packets for
  player X" and "complete the logout of X" (both re-check that the player is still online and in the same state).
- **Bots:** `TryManagedAction` (`ManagedWorldSession.cs:136`) uses the same class once bot think runs in the map (wave 5).
  MapLocal runs inline. Gated takes the gate. Deferred queues the action and returns a "queued" result, which bot code must
  treat as pending (the existing pending-is-not-failure rule from the async-navigation contract).
- **Wave 1 entries:** Deferred for `MSG_MOVE_WORLDPORT_ACK` (cross-map by definition). MapLocal only for opcodes whose
  handler and everything it calls have been read in the wave 1 PR. Expect a handful, for example `CMSG_SET_SELECTION`,
  `CMSG_STANDSTATECHANGE` and the speed/root/knockback acks, each with its reason, or none if the bookkeeping audit (P3) is
  not finished. Movement (P5), casts (P8), loot and quests (P9) follow in waves 2-5.

### 3.5 Event subscribers with an access class

```
// ArcaneCore.Game: a multicast event that knows how its subscribers may run.
public sealed class MapEvent<T1, T2>
{
    public void Subscribe(Action<T1, T2> handler, MapAccess access = MapAccess.Unclassified);  // `+=` stays Unclassified
    internal void Raise(Map map, T1 a, T2 b);
}
```

- MapLocal subscribers run inline. Unclassified (gated) subscribers run inline inside `map.EnterGate()`, and the gate is taken
  only if such a subscriber exists. That is the same lazy-gate idea as the Heartbeat stage. Deferred subscribers get a posted
  closure **over values, not live references** (guid, map id, entry, amount). By the time it runs, the victim may have
  despawned.
- `MapCombat.UnitKilled/DamageTaken` and `SpellSystem.HolderAdded/HolderRemoved` move to `MapEvent` in waves 2-3. Each
  subscriber in X2-X4 gets a class in its PR, with a reason. The `[MapAccess]` attribute already exists
  (`MapAccessAttribute.cs:4-17`); `MapAccess.Global` maps to Deferred for events.

### 3.6 Stage classification

`MapStageGates.CodeDefault` (`MapStageGates.cs:59-63`) gains option-dependent defaults: Packets is MapLocal when
**PacketsByOpcode** is on, Heartbeat when **LazyHeartbeatGate** is on, and Output when **World:Parallel:OutputLocal**
(wave 2) is on. `Gated`/`Ungated` overrides and `GateAllStages` keep their meaning, so live experiments and the "is it the
machinery" switch still work. The held-gate optimisation (`Map.Gating.cs:33-87`) needs no change: an ungated stage simply
does not take the gate, and the held gate starts at the first gated updater.

---

## 4. Intra-continent parallelism

Nearly all load sits on two continents, so whole-map parallelism caps out at about 2x for the maps phase, and in practice
less (the two continents are unequal and the barrier waits for the larger one). Three options were weighed.

### 4.1 Option A: spatial regions with their own owner thread and border hand-off

Split map 0 or 1 into K regions (along zone lines, like vmangos `Continents.Instanciate`, or a grid of grid tiles). Each region
is owned by one thread, and objects hand off when they cross a border.

- **Cost:** every interaction that crosses a border needs proxy ("ghost") objects or messages. That covers visibility (100 yd,
  `Map.cs:38`), aggro and assist radii, AoE and chain spells, group effects in range, chase, pets, transports, and scripts
  that address an entry anywhere on the map. Hand-off needs fencing so an object is never updated twice or not at all in a
  tick. Grid and cell structures, `_observers`, the values queue and the moved lists all split per region.
- **Risk:** high and lasting. Most of the 214 k lines of Game code assume "my map has everything near me". AzerothCore turned
  continent partitioning down for this reason, and SpatialOS's main difficulty was exactly this boundary.
- **Gain:** scales with regions, but border traffic and imbalance (cities) eat into it.

### 4.2 Option B: per-region stages inside one map update (red-black stripes)

The map stays the owner. Inside selected stages, the work is cut into stripes of cells at least a safe distance wide and run in
two colours. All red stripes run concurrently, then all black ones (vmangos `MapUpdate.Continents.MTCells`,
`SafeDistance` 1,066 yd, about two grids; `GridDefines.cs:25`, `:34-37`).

- Stripes running at the same time are at least a safe distance apart. Anything a creature can touch within that distance
  belongs to its own stripe or to a neighbouring stripe, which is idle. Writes to neighbours are therefore safe.
- **Cost:** the per-map shared lists that every object update writes (`_valuesQueue`, `_movedObjects`, grid relocation,
  `_outbox`, diagnostics) get per-stripe buffers, merged at the colour barrier in stripe order. World-wide state still needs
  §3 (it is the same work as for whole maps). Map-wide scripts (entries addressed anywhere on the map) must be gated or
  deferred.
- **Determinism:** fixed stripe order (red, then black; stripes in index order; objects in join order) gives the same result
  at any helper count, but **not** the same as today's serial order. That is an order change behind a switch, like
  `CreatureSplinePass`.

### 4.3 Option C: data-parallel kernels (compute in parallel, apply serially)

For read-mostly stages: split the per-object loop into a **compute** step on helpers, which reads a frozen snapshot and writes
only per-item result slots, and an **apply** step on the map's thread in join order (`MapSequence`).

- Candidates: spline advance in the creature spline pass (per creature; it reads its own spline and the clock); visibility
  decisions per moved player (O2: candidates and rules are reads, and the add/remove lists become results; `AddVisible` and
  `RemoveVisible` apply serially); building create/values blocks per viewer (each writes only its own
  `viewer.PendingUpdates`).
- **Determinism by construction:** the apply runs in the serial order, and the compute reads nothing the pass writes. The
  result equals the serial loop at any helper count. The one condition to prove per kernel is that no item reads another
  item's output of the same pass.
- **Cost:** small per kernel. Helpers come from the map workers that have finished (or never had) a map this tick: with 4
  threads and 2 continents, 2 workers idle today. The scheduler gets a side-job queue: `MapUpdateScheduler.RunHelpers(count,
  body)`, called from inside a map item, served by idle workers and by the calling thread.
- **Guard:** helpers run with `TickOwner.IsHelper` set and no current map. Any `CheckOwner` or `CheckGlobal` from a helper
  fires (a kernel must not mutate), and helpers **never take the serial gate**. That avoids a deadlock between a map thread
  that holds the gate and waits for its helpers, and a helper that waits for the gate.

### 4.4 Choice

**C first, then B for creature AI; reject A.** C is safe, deterministic and cheap, and it uses the idle workers that whole-map
parallelism leaves behind. B is the vmangos route for the remaining per-creature AI cost, and it reuses everything §3 builds.
A is a clustering-scale change. M15 (one owner per map, across processes) is the right place for that kind of split, at map
granularity. The live sample gives the size of the prize: creatures about 60 ms per continent update (spline pass 4.3 ms,
motion 9.3 ms in one sample), so C on motion and spline helps a little, and B on AI is what can halve a continent's update.

---

## 5. Implementation waves

Rules for every wave: its switches default off and off means today's code path exactly. A switch that changes ordering changes
it the same way at every thread count. Each wave ships with its keys in `docs/reference/configuration.md`, an options property,
`check-config` validation, and a before/after measurement on the same harness, with host load, GC pauses and working set
recorded.

### 5.1 Wave 0: measurement (no behaviour change)

- Confirm what live actually runs: thread count, `Ungated`, `CreatureSplinePass` (§0 item 3). This is a read of the running
  configuration through `.server info` or the start-up line, by Nate or the deploy lane, not by this lane.
- **Gate time by owner:** time spent holding the gate, attributed to the stage, updater type or opcode that took it (a per-map
  `long[]` indexed by stage plus a small dictionary for updaters, summed into the minute line as the top 5). Today's line says
  how long maps waited, not what they waited behind.
- A 60 s baseline on the bot benchmark (`ARCANECORE_BOT_BENCH=200`) at N=1 and N=4, with the minute line, tick p95 and per-stage
  means.

### 5.2 Wave 1: mechanism, Packets and Heartbeat out of the gate

**Scope (files):**
1. `src/ArcaneCore.Game/Maps/Parallel/WorldRuntime.AfterMaps.cs` (new): `PostAfterMaps`, `DrainAfterMaps`, per-map lists,
   the world list, counters (§3.2). The drain call goes in `WorldRuntime.cs` before `UnloadRequestedMaps` (`:567`).
2. `src/ArcaneCore.Game/Maps/Parallel/WorldRuntime.Parallel.cs`: `CheckGlobal(string what)` (§3.3); the minute line gains
   after-maps counters and wave 0's gate-time-by-owner.
3. Guards (writers only): `WorldRuntime.cs` `AddPlayer`/`RemovePlayer`, `WorldRuntime.Autosave.cs` `_autosaveDue` writers,
   `GroupManager.cs` mutators (including `MarkAuraChanged` `:185`), `StealthState.cs` `SetVisibility` (`:51`), `SpellSystem.cs`
   `GetOrCreateState` (`:1884`) and the `_states.Remove` sites (`:335`, `:1273`), `TeleportService.cs` `_pending` writes.
4. `src/ArcaneCore.Game/Maps/Parallel/ParallelMapOptions.cs`: **World:Parallel:PacketsByOpcode** (bool, default false) and
   **World:Parallel:LazyHeartbeatGate** (bool, default false). `MapStageGates.CodeDefault` (`MapStageGates.cs:59`) takes the
   options into account (§3.6).
5. `src/ArcaneCore.Game/Maps/Map.Gating.cs`: public `Map.EnterGate()` (the World layer needs it for Gated handlers).
6. `src/ArcaneCore.Game/Maps/Map.Update.cs`:
   - Packets with **PacketsByOpcode**: transit handling (`:160-169`) and logout completion (`:172-187`) post to the end-of-phase
     queue instead of running inline (they re-check `IsOnline`, `player.Map`, `IsLogoutDue` when they run).
   - Heartbeat with **LazyHeartbeatGate**: the loop runs ungated. `Unit.UpdateHeartbeat` (`Unit.cs:30-38`) returns how many
     times the timer fired, and the event is not invoked there. The map collects (unit, count) for units with subscribers
     into a reused list. After the loop, if the list is not empty, it takes `EnterGate()` once and invokes the events in
     collection order. Today's only subscriber sends food/drink visuals (packets). Invoking after the loop keeps their
     order relative to one another, and nothing else in the loop emits packets or reads their effect.
7. `src/ArcaneCore.World/Handlers/OpcodeAccess.cs` (new) and `OpcodeTable.cs`: the access class on `OpcodeHandler` (§3.4).
   Wave 1 entries: `MSG_MOVE_WORLDPORT_ACK` Deferred, plus only the MapLocal opcodes audited in the PR.
8. `src/ArcaneCore.World/Net/WorldSession.cs` `ProcessWorldPacketsCore` (`:382-460`): the per-packet class (MapLocal inline,
   Gated in `EnterGate()`, Deferred peek-park-resume) when the switch is on. `ResumeWorldPackets(player, budget)` is the same
   loop, entered on the main world thread.
9. Bookkeeping audit (P3): `PacketHandlerLatency.Record`, `heuristics.OnWorldPacket` and `LogIfSlowPacket` made safe for
   concurrent sessions (Interlocked or per session), or MapLocal stays empty in wave 1.
10. Docs: `docs/reference/configuration.md` (two keys); `docs/research/parallel-maps-status-20261017.md` (what changed, how to
    turn it on).

**Acceptance tests** (Game.Tests `Maps/Parallel/`, World.Tests `MapThreads/`; all with `OwnerGuard=Throw`):
1. *Off is today:* with both switches off, the existing `ParallelMapUpdateTests` and `CreatureSplinePassTests` pass
   unchanged, and gate entries per map per tick match the old count on a fixed scenario.
2. *After-maps order:* items posted from 3 maps on 4 threads run after the barrier on the main world thread
   (`IsMainWorldThread`, `!IsParallelPhase`), maps in snapshot order and FIFO within each; the sequence is identical at N=1 and
   N=4; an item that posts another runs in the same drain; a throwing item is contained and the next one runs.
3. *Packet order:* one session queues [MapLocal A, Deferred B, MapLocal C, Gated D]. A runs on the map thread with
   `HoldsSerialGate` false; B, C and D run after the barrier on the world thread, in that order and in the same tick; the
   budget counts all four; a second session on another map is unaffected.
4. *Gated handler:* an unlisted opcode's handler observes `HoldsSerialGate` true at N=4 with the switch on.
5. *Worldport ack:* a far teleport completes with the same end state (map, position, visible sets, packets to the client) at
   N=1 and N=4 with the switch on.
6. *Logout:* a due logout completes after the maps phase in the same tick, `PlayerLoggingOut` runs on the main world thread,
   one save snapshot is queued, and an emptied instance unloads in the same tick.
7. *Heartbeat:* food/drink visuals come every 5,200 ms with identical payloads and order with the switch off and on; with it
   on, the heartbeat loop holds no gate (counter) and the subscriber runs holding it.
8. *Global guard:* an ungated test updater that calls `GroupManager.MarkAuraChanged` or `StealthRegistry.SetVisibility`
   during the parallel phase throws in Throw mode and is reported once in Report mode.
9. *Determinism:* `SameSeededTicks_GiveIdenticalStateAndPackets_At1AndNThreads` (`ParallelMapUpdateTests.cs:267`) also runs with
   both switches on, and with a scenario that logs out, teleports and eats food.
10. *Opcode list:* every entry names a registered world opcode; MapLocal and Deferred entries carry a reason; the test prints
    the count per class (so a filter that matched nothing shows up as 0).

**Measurement:** the bot benchmark at N=4, then live A/B (1 h per arm, same bot count, builds paused): overlap xN, gate
entries/contended/wait per minute, gate time by owner, tick p95 from the 1-minute TickSummary (not TickOverrun) lines, GC pause
and working set. **Expected (estimate):** Packets and Heartbeat leave the gate: about 5-10 ms of a 60-70 ms continent update by
the 2026-10-21 slow-line means, which lean slow. Overlap should rise a little (from about x1.45 to perhaps x1.5-1.6). Tick p95
should barely move. Wave 1 pays for itself through the mechanism the later waves need, not through its own gain.

### 5.3 Wave 2: per-map spell and stealth state; Output out of the gate

- `UnitSpellState` moves from `SpellSystem._states` to a per-map registry (`Map.SpellStates`, keyed by guid). States of units
  in transit (no map) go in a small main-thread-only side table. `SpellSystem` methods resolve state through the unit's map.
  The `_reportedUnsupported*` sets become concurrent (report-only). Drunk players, death-aura removals and dynamic objects
  become per map.
- A per-map `SpellMapUpdater` (an `IMapUpdater`) runs the cast, aura and cooldown update of that map's states, behind
  **World:Parallel:SpellsPerMap**. With the switch off, `SpellFeature` keeps updating the whole world in the features phase
  (`SpellFeature.cs:197`). With it on, the 20-46 ms `SpellFeature` share moves from the serial features phase into the maps.
  That is an order change (auras tick during the map's update, not after every map), the same at every thread count.
- `StealthRegistry` per map. `GroupManager` aura and stat marks go on `Player` (X5). `HolderAdded/HolderRemoved` move to
  `MapEvent` with every subscriber classified (X2). A seeded `Random` per map derived from (seed, map id, instance id) for
  tests.
- Output becomes MapLocal under **World:Parallel:OutputLocal** (O3 resolved). `UpdateBlockWriter` statics are checked by the
  extended audit.
- Then `EnchantUpdater`, `StealthDetectionUpdater` and `LiquidAuraInterruptUpdater` get `[MapAccess(MapLocal)]` with reasons,
  or a list of what still blocks them.
- **Tests:** spell suites at N=1 and N=4 with the switch on; the determinism hash with casts, auras, stealth and detection
  across two maps; a rogue stealth test on two maps at once (registry isolation); the guard on the old global `_states`
  path (it must be unreachable with the switch on).
- **Measurement:** features p50 should drop by the `SpellFeature` share; overlap and gate wait as before.

### 5.4 Wave 3: the combat event fabric and cross-map work

- `MapCombat.UnitKilled/DamageTaken` move to `MapEvent`, with every subscriber in X3/X4 classified. Per-character
  progression, reputation and quest state is stored on (or keyed by) the player and becomes MapLocal. Social, conversation,
  battleground, world-event and bot-feed handlers become Deferred, with value captures. Loot registries become per map
  (loot belongs to an object on one map).
- Stage (6) Deferred: far teleports (`TeleportService.cs:438`) and transport hand-offs (`TransportSystem.cs:679`, `:695`) post to
  the end-of-phase queue. `RunAfterUpdate` keeps its map-local users (`TotemSystem.cs:349`, `CreatureMapSystem.cs:207`).
  `GetMapInParallelPhase` (`WorldRuntime.cs:325`) posts the creation instead of gating it.
- Then `MapCombat`, `ReactiveUpdater` and `LootRollManager` get `[MapAccess(MapLocal)]`. The live sample's 21 ms MapCombat
  share leaves the gate.
- **Tests:** kill credit, XP share, reputation and quest progress for a group spread over two maps; loot rolls; far teleports
  and boat rides between continents at N=1 and N=4 with identical results.

### 5.5 Wave 4: the creature walk

Continues the status page's list: creatures with no AI work this tick, then waypoint motion between nodes, then AI with lazy
gates around script hooks, spawn pools and respawn persistence (the X1/X3 dependencies are resolved by now). The navmesh owner
path stays with the path-job lane. **Measurement:** the creature share of gate time (wave 0's counter) should fall toward the
remaining script and pool calls.

### 5.6 Wave 5: opcode allow-list growth and bot think per map (plan W4)

- Movement (P5: teleport stage and boarding marks per player) becomes MapLocal; casts after wave 2; loot and quest NPC after
  wave 3; group, guild, chat and economy stay Deferred.
- Bot think per map (**World:Playerbots:ThinkInMapPhase** from the plan): each map's bots think inside the map's update,
  after the updaters and before Output, under a per-map share of the think budget. `TryManagedAction` and
  `TryManagedMovement` (`ManagedWorldSession.cs:136-178`) honour the opcode class. A Deferred action returns "queued"
  (pending, not failure). Group, social and travel coordinators stay in the features phase or become Deferred.
- This lands after the path-job pool (`claude/bot-path-off-world-thread`) and the social pass slicing
  (`claude/playerbot-social-pass-slicing`). It reuses both and redesigns neither. The status page already says why it waits:
  splitting bots per map gains nothing until the handlers they call are out of the gate.
- **Measurement:** features p50 down by the per-map share of `ManagedPlayerbotFeature` (about 50 ms today); bot quest
  completions per hour and the bot-stall anomaly rate unchanged.

### 5.7 Wave 6: intra-continent kernels, then stripes (§4)

- 6a: `MapUpdateScheduler.RunHelpers` and the helper guard; kernels for spline advance, visibility decisions and block
  building, behind **World:Parallel:MapHelpers** (0 = off). Test: each kernel's output is byte-identical to the serial pass at
  0, 1 and 3 helpers.
- 6b: red-black stripes for creature AI behind **World:Parallel:CellStripes**, with per-stripe buffers and the stripe barrier.
  Tests include stripe-boundary fixtures (combat, AoE, chase, pets and group credit across a stripe line).

---

## 6. Risks

### 6.1 Determinism

- Every switch changes the order of events (deferred logouts and teleports run after every map; auras tick inside the map; bot
  think moves). The 1-vs-N test therefore compares N=1 and N=4 **with the same switches on**, never switch-on N=4 against
  switch-off N=1.
- **Gated work is safe but not deterministic across maps.** Two maps' gated stages run in gate-acquisition order, which
  depends on scheduling. A third session that receives packets from both (a whisper and a group update) can see them in a
  different order from run to run. Deferred work is both safe and deterministic (fixed drain order). The determinism test
  must include a cross-map sender to catch handlers that stay Gated when they should be Deferred.
- Shared seeded `Random` instances in tests are a data race at N>1 (X14). Per-map seeding comes with wave 2, before spells
  are hashed.

### 6.2 Packet order per session

- Inbound: one consumer per session per tick (the player's map thread), and Deferred parks the **rest of the queue**, not just
  the packet, so handler order is the arrival order. A player is processed by at most one map per tick. The transit list is
  skipped once `player.Map` is set (`Map.Update.cs:162`), and with the switch on the worldport ack runs only on the world
  thread.
- Outbound: `Session.Send` is ordered by `_sendLock` (`WorldSession.cs:339`). What can change is the interleaving between a
  session's own map output and cross-map sends to it; that is the §6.1 rule (cross-map sends become Deferred).
- Visible delay: a Deferred handler runs after the map's Output, so a values change it makes goes out with the next tick's
  Output (50 ms later). Direct `Send`s (replies) go out at once. Group, guild, chat and economy tolerate this; casts and
  movement do not, which is why they become MapLocal rather than Deferred.

### 6.3 Deadlocks

Lock order: serial gate, then `_maps` lock (`WorldRuntime.cs:325-353`), then session `_sendLock`. Never take the gate while
holding `_sendLock`: the managed-packet observers run under it and must not send (`ManagedWorldSession.cs:80-84`), and they must
not enter the gate either. A map thread never waits for the world thread (`InvokeAsync` from a map thread would deadlock; this
is already documented at `WorldRuntime.cs:85-91`). Helpers never take the gate (§4.3). The held gate is always released at the
end of a map's update (`Map.Update.cs:43`), so no thread holds it across the barrier. The end-of-phase queue takes no lock on
the map path. Add a test that runs N > cores with random LPT ties and a watchdog on the barrier, so a hang fails the test
instead of stalling CI.

### 6.4 Test strategy

- **OwnerGuard=Throw everywhere in tests.** The parallel tests already do this (`ParallelMapUpdateTests.cs:18`). Add a test-host
  switch (an environment variable such as `ARCANECORE_TEST_MAP_THREADS=4`) that the shared world builder reads, so the whole
  World and Playerbots suites can run a second time at N=4 with Throw and every wave's switches on. Run that as its own CI job
  (the box and the PC run filtered tests only).
- **Stress:** the 200-bot terrain benchmark and `WorldTickLoadTests` (two populated continents) at N=1/2/4/8 with Throw and
  random tie-breaking in the scheduler. A run with 0 ownership violations counts only if the minute line shows the parallel
  phase actually ran (ticks > 0, overlap > 1). A guard that never ran is not a pass.
- **StaticMutableStateAudit extensions:** (a) also list `static readonly` fields whose type is a mutable collection
  (Dictionary, List, HashSet, Queue, Stack; 45 today). Each must be a `Frozen*` or `IReadOnly*` table, or be allow-listed
  with a reason ("lookup table, written only in the type initializer"). (b) A **shared-instance audit**: build a world with
  the features attached and two maps, walk the fields of every `IMapUpdater` instance one or two levels deep, and list every
  object reachable from both maps' updaters whose type holds a non-concurrent collection. Each listed type needs a
  `[MapAccess]`-style declaration (MapLocal impossible, Global, or "frozen during the phase") and a reason. This is the
  singleton audit the static scan cannot do (X12).
- **Opcode coverage:** the opcode list test (§5.2 test 10), plus one test per MapLocal entry that runs the handler at N=4 with
  Throw against a player on each of two maps at once.

### 6.5 Debug-build overheads

Live runs Debug by choice, so every guard costs real time there: no inlining, and `Invariant` checks run. The rules: guard
writers, not readers. Each guard starts with the volatile `_parallelPhase` read and returns. The stack trace is built only for a
call site's first violation (`WorldRuntime.Parallel.cs:123-125`). The after-maps lists are reused and closures are cached where
the call site allows. Measure each wave in Debug, since that is what live runs, and note that an optimized build would scale
all of the gains (plan W0).

### 6.6 Behaviour drift from deferral

A deferred effect runs a little later in the same tick. Code that reads the effect back in the same map update (a handler that
expects the group it just joined to exist when its next packet runs) would break, but the park-the-rest-of-the-queue rule
covers the packet case. For event subscribers, the reviewer of each Deferred classification checks that nothing later in the
map update reads the effect. Where something does, the subscriber stays Gated until the reader moves as well.

---

## 7. Decisions for Nate

1. **Confirm the live thread count** (§0 item 3) before wave 1's A/B. The logs since 2026-10-21 show no parallel phase.
2. **Order of waves 2 and 5.** Wave 2 (spells per map) also cuts the serial features phase by `SpellFeature`'s 20-46 ms, which
   helps even at one map thread. Wave 5 (bot think per map) moves the largest single cost, but it needs waves 2-3 for casts and
   kills. This page recommends 1 → 2 → 3 → 5, with 4 alongside 3.
3. **Order changes behind switches** (deferred logouts and teleports, auras ticking inside the map): acceptable, as with
   `CreatureSplinePass`? Each is the same at every thread count and off by default.

---

## 8. References

- vmangos MapManager (continent threads, deferred far teleports): https://github.com/vmangos/core/blob/development/src/game/Maps/MapManager.cpp
- vmangos Map.cpp (MTCells stripes, SafeDistance, motion and visibility threads): https://github.com/vmangos/core/blob/development/src/game/Maps/Map.cpp
- TrinityCore MapUpdater (schedule, wait, delayed work): https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Maps/MapUpdater.cpp
- AzerothCore on continent partitioning: https://github.com/azerothcore/azerothcore-wotlk/issues/22571

Related ArcaneCore pages: [multithreading plan](multithreading-plan-20261017.md), [parallel maps status](parallel-maps-status-20261017.md),
[async navigation acceptance](../ASYNC_NAVIGATION_ACCEPTANCE.md), [clustering design](../CLUSTERING_DESIGN.md).
