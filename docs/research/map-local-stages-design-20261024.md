# Map-local map stages and intra-continent parallelism: design (2026-10-24)

Status: design only, revision 2 (after three reviews; the responses are in §9). Nothing here is implemented, and no code or
configuration changed with this page. It continues [multithreading-plan-20261017](multithreading-plan-20261017.md) (W2 thread
ownership, W3 parallel maps, W4 bot think per map, W7 intra-map parallelism) and
[parallel-maps-status-20261017](parallel-maps-status-20261017.md) (what the serial gate covers today). The question it answers:
how map work leaves the world's serial gate so the map threads really overlap, in which order the pieces should come, and how
to use more than one thread per busy continent.

Source pin: `origin/main` at `2c266618` (2026-10-24). Every file:line below is from that commit. Live evidence comes from the
world logs on Nate's PC (read-only): the current log (2026-10-24, from 00:00 UTC) and the 2026-10-20/21 logs, which are the last
ones with parallel-phase lines. Log timestamps are UTC.

Proposed configuration keys appear in **bold**, not as code: none of them exist yet, and the docs key audit
(`DocKeyAuditTests`) only accepts code-formatted keys that an options class reads.

What is measured and what is reasoned: the tables in §1 are log measurements (§1 names the line type and extraction of each).
Everything in §2-§7 is static reasoning from the code. Each wave in §5 says what it is expected to do, and those expectations
are estimates until its measurement has been taken. Nothing in this page was built or run.

---

## 0. Summary

1. **Most of a continent's update is gated, and almost all of that is the creature walk.** In 51,122 slow-map lines for maps 0
   and 1 from 2026-10-21 (4 map threads; a biased sample of updates over the slow threshold), about **55 of 68 ms** of own work
   per continent update ran inside the serial gate, and the creature walk is about **50** of those 55 (§1.2). Packets,
   Heartbeat and Output together are 16.5 ms mean (8.3 ms p50), and most of the Packets mean is a p95 tail of logout
   completions that a deferral moves but does not remove. Per wave, the gate time that becomes overlappable is roughly:
   wave 4-lite and wave 4 up to ~50 ms, wave 1 ~4 ms (the heartbeat loop), wave 2 ~3-13 ms (Output, plus EnchantUpdater where
   present), wave 3 ~3 ms (MapCombat) plus the spell update moved in by wave 2.
2. **The serial features phase is larger at the median, but the maps phase has the worse tail.** Over the current log's 246
   one-minute TickSummary lines, the per-minute features p50 has a median of 56.3 ms against 43.5 ms for maps, and the
   per-minute p99 medians are 215 and 193 ms; maps exceeded features in 88 of 246 minutes. The TickOverrun slowest-tick
   samples (one per 30 s window, n=2,456) give features p50 282 / maps p50 168 ms, maps over features in 41 % of windows, and a
   maps p99 of 4,053 ms. `ManagedPlayerbotFeature` (50-98 ms in the top-features field) and `SpellFeature` (16-46 ms) are the
   features phase.
3. **Live has been serial since 2026-10-21 16:12 UTC.** The last "Parallel maps phase (60 s)" line is at 16:12:52 that day
   (4 map threads, overlap x1.45, owner violations 0 in all 1,073 minute lines of 10-20/21). The current run logs Information
   (its TickSummary lines are INFO), has continents loaded, and has no start-up line, so the maps phase runs serially. Wave 0
   restores the baseline; turning `World:Parallel:MapUpdateThreads` back to 4 is decision 1 in §7.
4. **Chosen mechanism** (unchanged in shape, corrected in detail): (a) a **world-thread end-of-phase queue** for cross-map and
   world-wide effects made on map threads, drained after the barrier in a fixed order that is the same at every thread count;
   (b) a **global-state guard** on writers, with a test-only **read guard** and a per-structure **frozen** flag, because the
   race MapLocal code adds is a read of a structure gated code is still writing; (c) a **per-handler access class** for
   in-world packets (MapLocal, Gated, Deferred), keyed by the registered handler, so a hot-swapped handler falls back to Gated;
   (d) the same class for **event subscribers** through `MapEvent<T>`.
5. **Chosen intra-continent design: per-stage parallelism inside the map, not spatial regions.** First data-parallel kernels
   on helpers (compute in parallel, apply serially), then red-black cell stripes for creature AI. Both need a scheduler change:
   today's idle workers are asleep and cannot take side jobs (§4.3).
6. **Recommended order: 0 → 4-lite → 1 → 2 → 3 → 4 → 5 → 6.** Wave 0 (measurement, guards, two latent fixes) and wave 4-lite
   (creatures with no AI work this tick, waypoint legs, lazy gates) attack the creature walk directly. Waves 1-3 build the
   ordering and safety machinery that waves 4 and 5 need; their own wall-time gain is small, and this page no longer claims
   otherwise.
7. **No wave 1 MapLocal opcode is known.** The examples in revision 1 (`CMSG_SET_SELECTION`, `CMSG_STANDSTATECHANGE`, the
   knockback ack) all reach world-wide spell state or the movement observers (§2.1 P4). Wave 1 ships the class machinery with
   an empty MapLocal list unless a handler is read end to end, its event subscribers included.

---

## 1. Live evidence

### 1.1 Signals

| Signal | Value | Line type and extraction |
|---|---|---|
| Overlap of the maps phase | x1.44-1.45 on 4 map threads; map work 38.6-39.2 s in 26.6-27.1 s wall a minute; serial gate 90.9-93.3 k entries/min, 25.7-27.5 k contended, 66.8-67.9 s waiting/min (summed over threads); barrier wait about 0.2 s/min | "Parallel maps phase (60 s)" minute lines, 2026-10-21 16:10-16:12 UTC |
| Owner violations | 0 in all 1,073 minute lines | same line type, 2026-10-20 and 10-21 logs |
| Live configuration at the last parallel start | 4 map threads, owner guard Report, ungated stages ActiveCells, ungated updaters CreatureMapSystem and GameObjectMapSystem | "Parallel maps phase: N map threads" start-up line, 2026-10-21 08:36 UTC |
| Gate wait per map update | mean 32.5 ms, max 2,050 ms, against own work mean 68.3 ms | 51,122 "Slow map update" lines, maps 0/1, 2026-10-21 |
| Stage means (slow lines) | see §1.2 | same |
| One slow update, decomposed | map 0, 760 players: own work 56 ms, gate wait 8 ms; CreatureMapSystem 31.5 (AI 2.4, motion 9.3, spline pass 4.3, 3,056 wanderers outside the gate), MapCombat 21.2, EnchantUpdater 3.4; packets 0.3, heartbeat 1.3, visibility 1 | one slow line, 2026-10-21 16:12:49 UTC |
| Phase split, per minute | features p50 (median over minutes) 56.3 ms, maps 43.5; p99 medians 215 / 193; maps > features in 88 of 246 minutes; commands, world tick and autosave under 1 ms | the "tick phases p50/p99 ms last 1 min" field of the 246 TickSummary lines in the current log |
| Phase split, slowest tick per 30 s | features p50 282 / maps p50 168; 10-19 UTC 608 / 568; from 20 UTC 604 / 190; maps > features in 1,010 of 2,456 windows; maps p99 4,053 ms, max 17,041 ms | the "slowest tick body" field of 2,456 TickOverrun lines (review 3's extraction; written only on overruns, so it leans slow) |
| Hung frames | 264 TickHang lines today; typical: features 2,032-2,592 ms in `ManagedPlayerbotFeature`; one at 3,942 ms: maps 3,796, gc 3,717 ms, machine memory 98 %, verdict gc-pauses | TickHang lines, current log |
| Parallel-phase lines | none after 2026-10-21 16:12 UTC (no start-up line, no minute line) | all world logs |

### 1.2 What is gated in a continent update today

Means / p50 / p95 in ms over the same 51,122 slow lines (review 3 recomputed them; they match revision 1's stage means except
the creature row, where revision 1 quoted the inner "creatures X ms over" field, 62.3 / 59.5 / 87.3, instead of the updater
total).

| Stage | mean | p50 | p95 | Gated today? |
|---|---|---|---|---|
| packets | 8.9 | 1.0 | 34.1 | yes |
| heartbeat | 4.4 | 4.3 | 6.9 | yes |
| active cells | 0.8 | 0.7 | 1.0 | no (rides inside the held gate when an earlier stage holds it) |
| visibility + values + flush | 3.2 | 3.0 | 7.0 | yes |
| CreatureMapSystem | 67.7 | 65.0 | 94.2 | mostly: only the spline pass (8.4) and wanderer legs (part of motion, 10.1) run outside; `Serial()` covers the rest (`CreatureMapSystem.cs:159`, `:1093`, `:1140`) |
| GameObjectMapSystem (68 % of lines) | 5.7 | 4.9 | 13.2 | partly (`GameObjectMapSystem.cs:293`, `:314`) |
| MapCombat (76 % of lines) | 3.0 | 2.6 | 4.2 | yes |
| EnchantUpdater (21 % of lines) | 9.2 | 9.0 | 14.0 | yes |
| own work / gate wait | 68.3 / 32.5 | 65 / 31 | 95 / 60 | |

So about 55 of 68 ms is gated, and two continents serialize behind each other mostly on creature AI. What follows:
- Taking Packets, Heartbeat and Output out of the gate unlocks at most 16.5 ms mean (24 % of own work) or 8.3 ms p50 (13 %).
  This page uses the p50 basis for "expected" figures, because the packets mean is a logout tail.
- On a bot-heavy server, the Packets stage mostly measures logout completions and the per-player loop, not bot actions. Bots
  run their handlers from the features phase through `TryManagedAction` (`ManagedWorldSession.cs:136-163`), not through the
  queue. So **PacketsByOpcode changes almost nothing on live until wave 5**, when bot think runs in the map and calls the
  same handlers.
- The hangs (`ManagedPlayerbotFeature` 2.0-2.6 s, GC pauses at 98 % memory) are not touched by waves 1-4. They are the target
  of the two in-flight lanes (`claude/bot-path-off-world-thread`, `claude/playerbot-social-pass-slicing`).
- Memory sits at 88-98 % of the machine, and one hang is a GC compacting paged-out heap. More threads mean more allocation
  contexts and more live scratch. Every wave records working set and GC pauses next to tick p50/p95.

---

## 2. Audit: shared state each stage touches today

Classes used below:
- **L**: already map-local (only this map's objects, grid and per-map systems, plus thread-safe services: `Session.Send`,
  the fault and anomaly ledgers, logging).
- **LC**: safe to make map-local with a local change (a counter made Interlocked, a dictionary moved onto the map or the
  player, a lazy gate around a rare call).
- **D**: needs a deferral to the world thread (post to the end-of-phase queue, §3.2).
- **R**: needs a redesign (the state is world-wide by construction and many paths use it).

**Audit rule** (applies to every row and every future allow-list entry): a path is followed through direct calls **and**
through every event it raises (unit, player, spell, combat and world events), into each subscriber. A handler is MapLocal only
if the whole closure is. Revision 1 broke this rule twice (P4 below).

### 2.1 Stage Packets (`Map.Update.cs:54-62`, `UpdatePacketsAndTimers` `:136-188`)

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| P1 | Per-player loop over a reused snapshot | `Map.Update.cs:139-156` | `_updatePlayers`, `_players` (the map's own) | L |
| P2 | Queue drain and thread assert | `WorldSession.cs:382-387` | `_worldQueue` (one consumer: the player's map thread); `Invariant.Assert(World.IsWorldThread)`, which is true on map threads because of `TickOwner` (`WorldRuntime.cs:93-94`) | L for the queue. The assert says nothing about global state. Add a guard (§3.3) |
| P3 | Per-packet bookkeeping before the handler | `WorldSession.cs:388-458`: wrong-state, settlement and map-null checks, `HeuristicsWrongState`, `_packetGate`, `ContainWorldHandlerFault`, `Kick`; `RecordHandlerLatency` `:529-535`; `HeuristicsWorldPacket` `WorldSession.PacketHeuristics.cs:134-141`; `LogIfSlowPacket` `:469` | session fields (owned by the player's map); `LatencyRegistry` (Kernel, shared); the `_heuristics` instance (whether its `Admit`/`Report` state is per session or shared is **unverified**) | LC after audit; until then every packet, bookkeeping included, runs under its handler's class and the bookkeeping of a non-Gated packet runs under the gate (§3.4) |
| P4 | In-world handlers: 260 `OnWorld` registrations in 46 files | `OpcodeTable.cs:75-76`; largest: `EconomyHandlers.cs` (27), `GuildHandlers.cs` (20), `NpcServiceHandlers.cs` (18), `GroupHandlers.cs` (17), `ChannelHandlers.cs` (16) | see P5-P9. Examples of closures that look local and are not: `CMSG_SET_SELECTION` → `SpellSystem.RetargetAutoRepeat` (`PlayerHandlers.cs:55-61`), which reads `_states` and can `CancelAutoRepeat` (`SpellSystem.AutoRepeat.cs:135-146`); `CMSG_STANDSTATECHANGE` → `Player.SetStandState` raises `StandStateChanged` (`Player.cs:355-365`) → `FoodDrinkFeature` (`FoodDrinkFeature.cs:13-21`) → `RemoveAurasWithInterruptFlags` (writes `_states`, fires `HolderRemoved`, 32 subscribers); the knockback ack (`KnockbackAckHandler.cs:24-58`) reads `TeleportService`, calls `AntiCheatFeature` and `MovementHandlers.ApplyObserved` (P5) | per handler: §3.4. No wave 1 MapLocal candidate is known |
| P5 | Movement (the bulk for real clients) | `MovementHandlers.cs:25-35`, `:123-140` | `TeleportService._pending` (`TeleportService.cs:55`, read by `IsBeingTeleported` `:260`); `TransportSystem` (one per world; `TakeJustBoarded` `MovementHandlers.cs:54-66`); `AntiCheatFeature` (`:64`); relay through `Map.BroadcastToObservers` | LC for the teleport stage (per player), boarding marks (per player) and anticheat state (per session) |
| P5a | Movement observers: `MovementHandlers.ApplyObserved` (`MovementHandlers.cs:355-372`) runs `MovementObservers.Before/After` (`ClientMovementObservers.cs`): FallObserver, UndermapObserver, FlagAuthorityObserver, MapEnvironment, TransportMovementObserver | FallObserver → `EnvironmentalDamage.Apply` (`FallObserver.cs:201`) → `map.Combat.DealDamage` (`EnvironmentalDamage.cs:136-141`) → `DamageTaken`/`UnitKilled` (29 subscribers, X3); the mitigation path reads aura state (X1). UndermapObserver → `KillPlayer`, `RepopPlayer`, `RepopAtGraveyard` (`UndermapObserver.cs:146-160`); a graveyard repop from an instance crosses maps | R (needs X1/X3, so waves 2-3); the graveyard repop is D |
| P6 | Chat, emotes, who | `ChatHandlers.cs:48-51` | `ChannelManager` (world-wide), whisper target lookup (`FindOnlinePlayer`, a ConcurrentDictionary read, `WorldRuntime.cs:27-28`), mute stores | Emote: LC once its closure is read. Chat, whisper, channel, who: D |
| P7 | Group, guild, petition, social, economy (AH, mail), battleground queue, LFG | `GroupHandlers.cs`, `GuildHandlers.cs`, `PetitionHandlers.cs`, `SocialHandlers.cs`, `EconomyHandlers.cs`, `BattlegroundHandlers.cs` | `GroupManager._memberOf/_invitedTo/...` (`GroupManager.cs:22-27`, plain dictionaries), guild and mail stores, auction house, BG queues | D (whole handler at end of phase) |
| P8 | Spell cast, cancel, cancel aura | `SpellHandlers.cs:19-22` | `SpellSystem._states` (`SpellSystem.cs:30`, one Dictionary for every map); `GetOrCreateState` `:1884-1892` | R (wave 2), then L once wave 3 classifies the damage events |
| P9 | Loot, quest NPC, gossip, trade, item use | `GameObjectLootHandlers.cs`, `QuestNpcInteractionHandlers.cs`, `NpcServiceHandlers.cs`, `ItemHandlers.cs` | loot registries, quest feature state, kill credit through `UnitKilled` subscribers (§2.4) | R (wave 3); until then Gated |
| P10 | Players in transit: only `MSG_MOVE_WORLDPORT_ACK` is handled | `Map.Update.cs:160-169`, `WorldSession.cs:411-428` → `TeleportService.HandleWorldportAck` (`TeleportService.cs:524-549`) | the ack writes `TeleportService._pending` and calls `_world.Post(CompleteFar)` (`:546`), which already runs in the **next tick's** Commands phase; the cross-map step is that posted command, not the ack. Also `_world.IsOnline` (ConcurrentDictionary) | D (for `_pending`, a world-wide plain dictionary) |
| P11 | Logout timers: `LogoutPlayer` | `Map.Update.cs:172-187` → `WorldRuntime.cs:443-451` → `RemovePlayer` `:418-438` | `PlayerLoggingOut`: **32 subscribers** (`WorldRuntime.Events.cs:56`; grep of `PlayerLoggingOut +=` in `src`), of which revision 1 missed the Game-layer ones: `MapCombat` (`MapCombat.cs:57`), `TransportSystem` (`:129`), `PetTeleportFollow` (`:34`), `InstanceManager` (`:162`), `ResurrectionService` (`:171`), and `FeatureSubscriptions` (named by review 1; not found under `src/ArcaneCore.Game` by this revision's grep, so its layer is unconfirmed); `Scripts.Player.OnLogout`, `_saveQueue`, `_onlineByName`/`_online` (ConcurrentDictionary), `_autosaveDue` (plain Dictionary, "world thread", `WorldRuntime.Autosave.cs:13`); `Session.OnLoggedOut` asserts the world thread (`WorldSession.cs:569`) | D. Contains a **cross-map write that exists today**, see P11a |
| P11a | Every map's `MapCombat` subscribes to the world-wide `PlayerLoggingOut` (`MapCombat.cs:57`). Its handler (`:91-96`) runs `OnPlayerLeaving` (`:596-609`) on *its* map when the player's corpse lies there or the player is in transit from it: `DetachRelations` (attackers' attack state, threat holders' threat lists) and `RemoveCorpse` (that map's corpse set and grid) | a logout on map A runs map B's `OnPlayerLeaving` on A's thread, inside A's gated Packets stage, while B may be inside its ungated `CreatureMapSystem` or `GameObjectMapSystem` (both ungated on live at N=4) | **an existing race** at N>1 with those updaters ungated; latent while live is serial. D (the first user of the end-of-phase queue, §5.3); the wave 1 logout test covers it |
| P12 | Overdue-logout anomaly check | `WorldSession.Observability.cs:199-215` | session fields, `AnomalyLedger` (thread-safe) | L |

### 2.2 Stage Heartbeat (`Map.Update.cs:66-74`, `UpdateHeartbeats` `:191-224`)

Unit regeneration, combat, aura and swing timers do **not** run in this stage. They run in `MapCombat` (regen in
`MapCombat.Regen.cs`, swings in `MapCombat.Melee.cs`), in `EnchantUpdater` and in the spell update (`SpellSystem.Update`, called
from the features phase). The Heartbeat stage is the 5.2 s `Unit.Heartbeat` timer (`Unit.cs:30-38`). The enum comment at
`MapStageGates.cs:12` still says otherwise; wave 1 fixes it.

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| H1 | Snapshot rebuild and loop | `Map.Update.cs:196-223` | `_objects`, `_heartbeatUnits`, `HeartbeatSnapshotObjectsVisited` (all the map's own); `Player.IsQuestSettlementPending` (`Player.QuestSettlement.cs:11`, set on the world thread) | L |
| H2 | Per-player timing for diagnostics | `Map.Update.cs:212-217` | the map's `MapUpdateDiagnostics` | L |
| H3 | The timer itself | `Unit.cs:28-38` | the unit's own `_heartbeatTimer` | L |
| H4 | `Heartbeat` event subscribers | only `SpellSystem.FoodDrinkVisuals.cs:16` (`TriggerFoodDrinkHeartbeat` `:28-41`) | `GetAuras(unit)` reads `SpellSystem._states` (`SpellSystem.Auras.cs:466`); `ConditionalWeakTable` (thread-safe); `SendToSet` sends to the map's set (whether it goes through the per-map outbox is unverified) | LC now (gate only the invocation, and only when a subscriber exists; §5.3). After waves 2-3 it is L |

### 2.3 Stage Output (`Map.Update.cs:237-372`, visibility `Map.Visibility.cs`)

| # | What | Where | Shared state | Class |
|---|---|---|---|---|
| O1 | Bounding radii, moved players and objects, `_movedScratch` | `Map.Update.cs:243-313` | the map's grid, `_valuesQueue`, `_movedObjects` | L |
| O2 | Visibility decisions, visible sets, `_observers` | `Map.Visibility.cs:403-433`, `:447-484` | `viewer.VisibleObjects`, `_observers` (the map's own) | L |
| O3 | Visibility rules | `Map.Visibility.cs:435-445`; installed by `StealthFeature.cs:46`, `GhostVisibilityFeature.cs:24`, `GmCheatCommands.cs:267`, `ShadowfangKeepInstance.Events.cs:42` | `StealthVisibilityRule.CanSee` (`StealthVisibilityRule.cs:40-123`) reads `StealthRegistry` (one per world; `_stealthed`/`_hiddenUnits` HashSets and `_hidden` count, `StealthState.cs:26-29`, written by `SetVisibility` `:51` from aura code); `SpellSystem.HasAuraType` (`_states`); `Groups.GetGroupMembers` (`StealthVisibilityRule.cs:37`, `GroupManager`); `Relations.IsHostile`; `map.Collision.IsWithinLineOfSight`. Read-only-ness of the full rule and of the `_detecting` writer is **unverified** | R for the spell reads (wave 2). LC for `StealthRegistry` (per map). The group read is safe only once **every** group writer is Deferred (frozen, §3.1), which is wave 5, not wave 1 |
| O4 | Create and values blocks | `Map.Visibility.cs:123`, `:430`; `Map.Update.cs:561-644` (`UpdateBlockWriter`) | the object's fields; `Session.WantsUpdateObjects` (`ManagedWorldSession.cs:216`) through the lazily cached `BotPolicy` (`:187-200`, one writer: the owner map); `BotLeanStats` (Interlocked, allow-listed) | L (check `UpdateBlockWriter` statics in wave 2's audit) |
| O5 | Flush | `Map.Update.cs:646-647` → `PendingUpdates.FlushTo` → `Session.Send` (`WorldSession.cs:312`, under `_sendLock` `:339`) | thread-safe send | L |
| O6 | Ungated broadcasts held in the outbox | `Map.Broadcast.cs:35-42` (one `payload.ToArray()` per broadcast), flushed at the next gate entry (`Map.Gating.cs:19-30`) and before Output (`Map.Update.cs:126-130`) | per-map outbox | L. Cost: one allocation per held broadcast. Once movement is MapLocal (wave 5) this is the busiest relay path, so outbox buffers are pooled from wave 1 and the allocation is budgeted (§3.7). Ordering: every outbox is flushed at the end of the map update and before the drain (§3.7) |

### 2.4 What the stages and the updaters drag in

| # | What | Where | Class |
|---|---|---|---|
| X1 | `SpellSystem`: per-unit cast and aura state for every map in one Dictionary; also `_reportedUnsupported*` sets (`SpellSystem.cs:33-34`), drunk players, death-aura removals, dynamic objects; the update runs once a tick for the whole world in the features phase (`SpellSystem.cs:236-266`, `SpellFeature.cs:197`, `:284`), walking `_states.Values` in Dictionary order (`:247-255`) | R (wave 2: a per-map store chosen at start-up, §5.4) |
| X2 | `SpellSystem.HolderAdded/HolderRemoved` (`SpellSystem.Rules.cs:60`, `:63`): 32 subscriptions, from map-local rules (`DiminishingRule.cs:28-29`, `CombatStatAuras.cs:49-50`, `FormStatListener.cs:44-45`, `PaladinAuraRules.cs:34-35`) to world features (`BattlegroundFeature.cs:166-167`, `OutdoorPvPFeature.cs:72-73`, `ManagedPlayerbotFeature.cs:450`, `UnstuckCommands.cs:105`) | per subscriber through `MapEvent<T>` (§3.5) |
| X3 | `MapCombat.UnitKilled` (`MapCombat.cs:149`) and `DamageTaken` (`MapCombat.DamageEvents.cs:20`): 29 subscriptions (progression, reputation, quests, honor, loot, battlegrounds, instances, death outcome, bot feeds). Periodic spell damage reaches them through `MapCombatDamageSink.cs:33` → `MapCombat.DealDamage` → `UnitKilled` (`MapCombat.Melee.cs:1050`) | per subscriber (wave 3) |
| X4 | Follow-on events: `LevelChanged`, `ReputationChanged`, `QuestRewarded`, `ItemCountChanged` (sites as in revision 1: `ProgressionFeature.cs:53`, `ReputationFeature.cs:87`, `CreatureQuestScripts.cs:89`, `QuestNpcFeature.cs:367` and others) | mostly per-character state (LC if stored per player). Social, conversation and world-event handlers: D |
| X5 | `GroupManager` aura and stats marks from aura changes: `MarkAuraChanged` writes `_pendingAuras` (`GroupManager.cs:185-190`); `_sentStats` (`:24`, `:729-744`); the group stats pass walks `_pendingAuras` in set order | LC (wave 2, behind the store choice of §5.4 so the walk order is today's when off) |
| X6 | Teleports and other cross-map work through `Map.RunAfterUpdate` (`Map.Membership.cs:247`, a plain `List.Add`), run in stage Deferred (`Map.Update.cs:395-416`): far teleports `TeleportService.cs:438`, ship follow / homebind `TransportSystem.cs:679`, `:695`, totem updater install `TotemSystem.cs:349`, creature observer capture `CreatureMapSystem.cs:207` | far teleport and transport: D. Totem install and observer capture: L |
| X7 | Map creation from inside a map update | `WorldRuntime.cs:302-353` (`GetMap(uint,uint)` is synchronous and returns a `Map`; `GetMapInParallelPhase`: lock, then gate, then `MapCreated` with **37** subscribers) | gated today. Once the gate goes, an in-phase miss is a guard failure and every in-phase caller must be Deferred (§5.5) |
| X8 | Map unload requests | `WorldRuntime.cs:377-385` (locked list, served after the phase) | already D-shaped |
| X9 | Terrain shared by instances of one map id; grid load on a cell change | `Map.Update.cs:475-490` (takes the gate when a relocation touches the grid lifecycle); terrain clean-up once a tick after the maps (`WorldRuntime.cs:573`) | already handled |
| X10 | Navmesh owner path, path searches | `NavMeshPathfinder`; the path-job pool on `claude/bot-path-off-world-thread` | out of scope |
| X11 | Static mutable fields | `StaticMutableStateAuditTests.cs:18-48` (28 allow-listed). The scan skips `static readonly` fields (`:71`); about 45 `static readonly` Dictionary/List/HashSet fields exist in Game, World and Playerbots (grep count, **unverified** by reflection) | extend the audit (§6.4) |
| X12 | Shared feature singletons reached from per-map updaters (`TradeMapUpdater(this)`, `FlightUpdater(this)`, `TotemMapUpdater(this)`, `SkillPersistenceUpdater(this)` and the rest; 27 `IMapUpdater` types) | per type: `[MapAccess]` with a reason; a runtime "reachable from two maps" audit (§6.4) |
| X13 | Feature event handlers on world events raised from map stages: `PlayerLoggingOut` (32), `MapCreated` (37), `MapUnloading` (25) | they run where the event is raised. Logout becomes D in wave 1 and creation in wave 3, so the handlers run on the main world thread. P11a is the cross-map case |
| X14 | `Random.Shared` and the `Random` a system is built with (`SpellSystem.cs` constructor, `random ?? Random.Shared`) | thread-safe in production. A seeded `Random` shared across maps in tests is a data race at N>1: per-map seeded RNG in wave 2 |
| X15 | `_world.Post` from map threads (for example `CompleteFar`, P10) enqueues to `_commands` (`WorldRuntime.cs:213-216`) in thread-arrival order. `MapAccess.MapLocal` lists "posting world commands" as allowed (`MapAccessAttribute.cs`) | **nondeterministic at N>1 today**; ordering-sensitive posts move to `PostAfterMaps` (§3.2, §6.1) |

### 2.5 Summary by stage

| Stage | Blocking items | Earliest wave it can leave the gate |
|---|---|---|
| Creature walk (quiet creatures, waypoint legs) | X1/X3 reads behind lazy `Serial()` | wave 4-lite |
| Heartbeat | H4 (one subscriber, reads spell state) | wave 1 (lazy gate around the event) |
| Packets | P3 (audit), P4-P9 per handler, P10/P11 | the stage in wave 1 with every unaudited handler Gated; handlers move out in waves 2-5 |
| Output | O3 (spell state, per-world stealth registry) | wave 2 |
| Updaters | X1-X5 for MapCombat, EnchantUpdater, StealthDetectionUpdater, ReactiveUpdater; X3/X6 for the rest of CreatureMapSystem | waves 2-4 |
| Deferred | X6 | wave 3 |
| Grid | X9 | stays gated: rare and short |

---

## 3. Mechanism

### 3.1 Principle: a world-wide structure is read on a map thread only once it is frozen

Today, gated work of one map runs at the same time as ungated work of another map. Gating a writer does not make an ungated
**reader** safe: a MapLocal reader of `SpellSystem._states` (`SpellSystem.cs:30`, written by `GetOrCreateState` `:1884-1892`),
`TeleportService._pending` (`TeleportService.cs:55`) or `GroupManager._memberOf` (`GroupManager.cs:22`) races with a gated
writer on another map on a non-thread-safe Dictionary. A writer-only guard never sees this. The rule:

> During the parallel maps phase, map state changes only on the map's own thread. A world-wide structure is **frozen** when
> every writer of it during the phase is Deferred (its writes run on the world thread after the barrier). MapLocal code may
> read a world-wide structure only if that structure is frozen. Until then, code that reads it is Gated.

Consequences:
- Group membership, guild rank, channel lists and `_autosaveDue` are **not** safe to read in waves 1-4: gated handlers and
  gated event subscribers still write them during the phase. They become frozen only when the last writer is Deferred (wave 5
  for the group, guild and channel handlers; logout and worldport writers already in wave 1). The online registry is a
  ConcurrentDictionary and is safe to read now.
- Each world-wide structure gets an explicit state in code: a `GlobalStructure` record (name, frozen flag, owning type), used by
  the guards in §3.3 and checked by the classification tests: a MapLocal handler or subscriber whose declared reach list (§3.4)
  names a non-frozen structure fails the test.

### 3.2 The world-thread end-of-phase queue

```
// WorldRuntime (Game).
public void PostAfterMaps(Action work);   // map thread during the maps phase (parallel or serial loop): appended to the map's list
internal void DrainAfterMaps(Map[] order, int count);   // main world thread, after the maps, before UnloadRequestedMaps
```

- **Storage:** a `List<Action>` per `Map` (`_afterMaps`), written only by the thread updating that map (no lock). There is no
  world-level list in the drain.
- **Where a post goes:** on a thread with `TickOwner.CurrentMap` set, to that map's list. The serial loop sets it too: wave 1
  adds `TickOwner.Enter(this, map)` around `map.Update` in the serial loop (`WorldRuntime.cs:511-523`), which today sets it only
  in `RunMapsParallel` (`WorldRuntime.Parallel.cs:222`). That loop also runs at N>1 when only one map exists (`mapCount > 1`,
  `WorldRuntime.cs:505`), so this matters at every thread count. On the main world thread outside the maps phase (features,
  commands), the work runs **inline**: that is today's synchronous behaviour, and it avoids a post from the features phase
  running after the next tick's maps. From any other thread (network, session tasks, helpers), it is a guard failure
  (`CheckGlobal("after-maps from foreign thread")`) and the work is routed to `Post` (next tick's commands).
- **Drain point:** in `RunTick`, inside the `try` after the maps loop or `RunMapsParallel` returns (`WorldRuntime.cs:503-561`),
  so the pooled map snapshot is still valid (it is returned with `clearArray: true` in the `finally`, `:562-565`), and before
  `UnloadRequestedMaps` (`:567`). An instance emptied by a deferred logout or teleport unloads in the same tick, as today.
- **Order:** maps in the order of the tick's map snapshot (`_maps.Values.CopyTo`, the same array at every thread count), FIFO
  inside each map. Work posted during the drain goes to the list of the map it names (or of the current item's map) and runs
  in the same drain, with a bound (3 rounds, then the rest goes to the next tick's commands, with a warning). Each item is
  isolated (try/catch, `FaultLedger` "world.after-maps").
- **`RunAfterUpdate` during the drain:** a far teleport issued by a resumed (Deferred) handler calls `RunAfterUpdate`, whose
  list that map already processed in its stage (6). During the drain, `RunAfterUpdate` therefore appends to the after-maps
  queue instead, so the teleport runs in the same tick, as it would today.
- **Context:** `IsParallelPhase` is false and the thread is the main world thread, so every existing world-thread assert holds.
- **Counters:** posted, drained, rounds, drain µs and foreign-thread posts, added to `ParallelMapCounters` and the minute line
  (`WorldRuntime.Parallel.cs:332-337`).
- **Off:** with every switch off nothing is posted (the converted call sites keep their old path), so the drain is an empty
  loop over the snapshot.

This is vmangos' `ScheduleFarTeleport` / `SwitchPlayersInstances` and TrinityCore's "delayed work after `wait()`", applied to
every cross-map effect.

### 3.3 Ownership and global-state guards

- **Map state:** unchanged. `CheckOwner` (`WorldRuntime.Parallel.cs:114-138`) on map mutators (`Map.EnsureWorldThread`
  `Map.cs:245-256`, `OnObjectMoved` `Map.Update.cs:473-493`, `QueueValuesUpdate` `:426-435`).
- **Reporting cost, corrected.** `CheckOwner` builds `new StackTrace(2, false).ToString()` on **every** violation
  (`WorldRuntime.Parallel.cs:123`) and only then checks the once-per-site set (`:125`), because the stack string is the dedupe
  key. Revision 1 said the opposite. Fix (wave 0, for `CheckOwner` and the new guards alike): the key is
  `(what, [CallerFilePath], [CallerLineNumber])`, captured by optional caller-info parameters at zero cost; the stack is built
  only when that key is new. A repeated violation then costs one `ConcurrentDictionary` lookup on a struct key, no allocation.
  A test counts allocations (`GC.GetAllocatedBytesForCurrentThread`) over 10,000 repeats of one violation in Report mode: 0
  after the first.
- **World-wide writers:** `WorldRuntime.CheckGlobal(GlobalStructure s, [caller info])`. During the parallel phase: if `s` is
  frozen, any call fires; otherwise the caller must hold the serial gate. Same anomaly family (`parallel` / `global-state`),
  same `World:Parallel:OwnerGuard` mode (Off, Report, Throw), no new key. Each guard starts with the volatile `_parallelPhase`
  read and returns, so outside the parallel phase (and at N=1) its cost is one read.
- **World-wide readers (test mode):** `CheckGlobalRead(GlobalStructure s, [caller info])`, compiled in but active only when
  OwnerGuard is Throw (tests). It fires when a thread that does not hold the gate reads a non-frozen structure during the
  phase. Production never pays for it beyond the mode check. This is what catches the read/write race of §3.1.
- **Where the guards go** (wave 0, Report on live, Throw in tests): writers: `WorldRuntime.AddPlayer` / `RemovePlayer` and
  `_autosaveDue`; `GroupManager` mutators including `MarkAuraChanged` (`:185`); `StealthRegistry.SetVisibility` (`:51`);
  `SpellSystem.GetOrCreateState` (`:1884`) and the `_states.Remove` sites (`:335`, `:1273`); `TeleportService` `_pending`
  writes; `ChannelManager` mutators. Readers: `SpellSystem.GetState`/`GetAuras`, `TeleportService.IsBeingTeleported`,
  `GroupManager` lookups, `StealthRegistry` queries.
- **They are not behind a switch, and this is the one stated exception to "off is today"** (§5 rules): with the cheap dedupe,
  a non-violating guard is one volatile read, and a repeated violation is one lookup. Wave 0's first live Report run is itself
  an audit of the code that already runs ungated (GameObjectMapSystem, the wanderer legs): it either reports nothing, or it
  finds races that exist today.
- **`IsWorldThread` stays as it is.** It means "may run simulation code synchronously" (`WorldRuntime.cs:85-94`). The 44 call
  sites (grep count) keep working. Global services that need the strict meaning use `CheckGlobal` or `IsMainWorldThread` (`:97`).

### 3.4 Per-handler access for in-world packets

```
// ArcaneCore.World/Handlers/OpcodeAccess.cs (World layer: handlers live there; Game does not know opcodes)
public enum OpcodeAccess { Gated = 0, MapLocal = 1, Deferred = 2 }

internal static class OpcodeAccessList
{
    // Reviewed like the static-field allow-list. Anything absent, or registered with a different handler, is Gated.
    internal static readonly FrozenDictionary<WorldOpcode, Entry> Entries = ...;
    internal sealed record Entry(OpcodeAccess Access, string Handler, string Reason, string[] Reach);
    // Handler: the audited handler's "Namespace.Type.Method"; Reach: every method and event subscriber its closure calls,
    // and every world-wide structure it reads (§3.1).
}
```

- **The class belongs to the registered handler, not the opcode.** When `OnWorld` registers (`OpcodeTable.cs:75-76`), the
  table compares the delegate's method (`Delegate.Method`, declaring type and name) and assembly with the entry. It takes the
  entry's class only if both match and the assembly is the built-in World assembly. A handler installed by `ModuleHost.TrySwap`
  (`ModuleHost.cs:561`) or `HotCodeRefresh` (`HotCodeRefresh.cs:336`, `:380`) comes from another assembly and is Gated until an
  entry names it. `WithNewHandlersFrom` re-resolves each class the same way.
- **Default Gated.** An unaudited handler runs inside the gate at its position in the session's queue, as the whole stage does
  today.
- **Where the class is decided:** at the head of the per-packet loop in `ProcessWorldPacketsCore` (`WorldSession.cs:382`), on
  the **peeked** packet, before the wrong-state, settlement and map-null checks, `HeuristicsWrongState`, `_packetGate`,
  `ContainWorldHandlerFault` and `Kick` (`:388-458`). All of the packet's bookkeeping runs under its class. Until the P3 audit
  is done, a MapLocal or Deferred packet's bookkeeping still runs under the gate (on the map thread for MapLocal, in the drain
  for Deferred).
- **Execution** with **World:Parallel:PacketsByOpcode** on:
  - MapLocal: runs on the map thread outside the gate (unless the gate is already held, see Gated).
  - Gated: the first Gated packet of the stage calls `HoldGate()`, and the gate stays held to the end of the Packets stage.
    Later MapLocal packets of any session on that map run inside it (safe, only overlap is lost). This keeps the gate-entry
    count at today's level instead of one enter/exit per packet, which is the churn `HoldGate` was added to remove
    (`Map.Gating.cs:33-37`). In practice the Packets stage stays gated for any map with one Gated packet in the tick.
  - Deferred: the session **peeks** (`ConcurrentQueue.TryPeek`) and does not dequeue. It stops for this map update, records
    the remaining budget, and posts `ResumeWorldPackets(player, budget)` to the end-of-phase queue. On the world thread, the
    resumed call handles the deferred packet and continues with the rest of the queue (every class runs inline there), against
    the same `MaxWorldPacketsPerTick` budget. Nothing after a deferred packet runs before it, and the whole queue is still
    handled in the same tick.
  - **Resume preconditions:** the session may have closed in between. `DiscardQueuedPackets` (`WorldSession.cs:858-864`) can
    empty the queue, and the player may have left. The resume re-checks the session state, `IsOnline` and `player.Map`, and
    returns on any change; an empty queue is not an error.
  - **Fault containment:** `ResumeWorldPackets` runs each packet through the same containment as `Map.ProcessPackets`
    (`Map.Update.cs:545-560`: disconnect-on-fault through `ContainWorldHandlerFault`), shared as one helper, not the drain's
    generic catch.
- **Transit (P10) and logout completion (P11)** go through the same queue: the map posts "handle transit packets for player X"
  and "complete the logout of X" (both re-check that the player is still online and in the same state).
- **Bots (wave 5):** `TryManagedAction` (`ManagedWorldSession.cs:136-163`) calls the handler synchronously and has no queue.
  Rules once bot think runs in the map:
  - Gated takes the held gate; MapLocal runs inline; Deferred appends the action to a per-session **bot action queue**, drained
    by the same `ResumeWorldPackets` path, and returns "queued" (pending, not failure, as in the async-navigation contract).
  - **Once a session has a queued action in this phase, every later action of that session in the phase queues too**, whatever
    its class, so actions run in issue order (the W3 invariant "per-session packet order unchanged").
  - A re-issued action equal to a pending one (same opcode and payload bytes) is not queued again; it returns "queued".
- **Wave 1 entries:** Deferred for `MSG_MOVE_WORLDPORT_ACK` (for `_pending`, P10). MapLocal: **none known**. An entry may be
  added only with its handler read end to end, event subscribers included, and its `Reach` list written down.
- **`GateAllStages`** (the "is it the machinery" switch) also forces every packet to Gated and disables Deferred, so it
  reproduces today's order exactly, with PacketsByOpcode on or off.

### 3.5 Event subscribers with an access class

```
// ArcaneCore.Game: a multicast event that knows how its subscribers may run.
public sealed class MapEvent<T1, T2>
{
    public void Subscribe(Action<T1, T2> handler, MapAccess access = MapAccess.Unclassified);  // `+=` stays Unclassified
    internal void Raise(Map map, T1 a, T2 b);
}
```

- `Raise` takes a snapshot of the subscriber list first. If any subscriber is Unclassified (gated), it takes the gate **before
  the first subscriber** and holds it for the whole raise; a raise with only MapLocal and Deferred subscribers runs ungated.
  There is no lazy gate entry in the middle of the iteration, so other maps' gated code cannot run between two subscribers of
  one raise.
- MapLocal subscribers run inline. Deferred subscribers get a posted closure **over values, not live references** (guid, map
  id, entry, amount). By the time it runs, the victim may have despawned.
- **Order within one event:** subscribers run in subscription order, except that Deferred ones run after the barrier. That
  reorders a Deferred subscriber after later Gated or MapLocal ones. The classification rule: a subscriber may be Deferred only
  if no later subscriber of the same event, and nothing later in the map update, reads its effect (§6.6). The classification
  test lists, per event, the subscriber order and class.
- `MapCombat.UnitKilled/DamageTaken` and `SpellSystem.HolderAdded/HolderRemoved` move to `MapEvent` in waves 2-3. The
  `[MapAccess]` attribute already exists (`MapAccessAttribute.cs:4-17`); for events, `MapAccess.Global` means Deferred.

### 3.6 Stage classification and the held gate

`Gate(stage)` only ever takes and holds the gate; it never releases it (`Map.Gating.cs:39-47`). Release happens only before an
ungated **updater** (`:60`) or at the end of the update (`Map.Update.cs:43`). Consequences, today and for this design:
- An `Ungated` override for Heartbeat or Output is a no-op today whenever an earlier stage is gated: the stage runs inside the
  held gate. This is a latent configuration defect (the override does not do what it says). Wave 0 fixes it.
- Revision 1's LazyHeartbeatGate and OutputLocal would have done nothing, for the same reason.

The fix: `Gate(stage)` releases the held gate for an ungated stage, as `GateUpdater` does, except ActiveCells, which stays a
cheap stage that rides inside a held gate. Gate entries then follow the classification:

| Configuration | Gate entries per map update (players present) |
|---|---|
| today, defaults | 1 (Packets to the first ungated updater) + 1 per later run of gated updaters + Output |
| LazyHeartbeatGate on | +1 (released at Heartbeat, taken again at the first gated updater) + 1 when a heartbeat subscriber fires |
| OutputLocal on | +0 or -1 (released before Output instead of held through it) |
| PacketsByOpcode on, any Gated packet | same as today; with no Gated packet, the first gated stage or updater takes it |

`MapStageGates.CodeDefault` (`MapStageGates.cs:59-63`) gains option-dependent defaults: Packets is MapLocal when
**PacketsByOpcode** is on, Heartbeat when **LazyHeartbeatGate** is on, Output when **World:Parallel:OutputLocal** (wave 2) is
on. Each switch works on its own; tests check each one alone with gate-entry counters (§5.3 test 7). `Gated`/`Ungated`
overrides keep their meaning, now actually honoured. `GateAllStages` gates every stage and every packet (§3.4) and reproduces
today's order.

### 3.7 Outbox and send order

- Pooling: outbox entries rent their buffer from `ArrayPool<byte>.Shared` and return it after the send, from wave 1 on.
- Order: every outbox is flushed at the end of the map update (in `Update`'s `finally`, before `ReleaseHeldGate`) and therefore
  before the drain. Otherwise a session could receive a resumed handler's direct send before an earlier held broadcast.
- Budget: wave 5 (movement MapLocal) records outbox entries per tick and pooled bytes in the minute line, and is accepted only
  if allocated bytes per tick do not rise by more than the measured per-broadcast copy.

---

## 4. Intra-continent parallelism

Nearly all load sits on two continents, so whole-map parallelism caps out at 2x for the maps phase, and in practice less (the
two continents are unequal and the barrier waits for the larger one).

### 4.1 Option A: spatial regions with their own owner thread and border hand-off

Split map 0 or 1 into K regions with their own owner threads; objects hand off at borders.
- **Cost:** every interaction that crosses a border needs ghost objects or messages (visibility 100 yd, `Map.cs:38`; aggro and
  assist radii; AoE and chain spells; group effects; chase; pets; transports; scripts that address an entry anywhere on the
  map). Hand-off needs fencing. Grid, cells, `_observers`, the values queue and the moved lists all split per region.
- **Risk:** high and lasting: most of the 214 k lines of Game code assume "my map has everything near me".
- **Rejected.**

### 4.2 Option B: per-region stages inside one map update (red-black stripes)

The map stays the owner. Inside selected stages, the work is cut into stripes of cells at least a safe distance wide and run in
two colours: all red stripes concurrently, then all black ones (the vmangos `MapUpdate.Continents.MTCells` approach; its
`SafeDistance` value is **unverified**, check vmangos `mangosd.conf.dist` and `Map.cpp`; our grid constants are
`GridDefines.cs:25`, `:34-37`).

- Stripes running at the same time are at least a safe distance apart, so writes to neighbours are safe.
- **Every per-map structure becomes multi-writer under stripes** and needs a per-stripe buffer merged at the colour barrier in
  stripe order, or a gate: `_valuesQueue`, `_movedObjects`, grid relocation, `_outbox`, diagnostics, `_afterMaps` (§3.2),
  `_afterUpdate` (`Map.Membership.cs:247`, a plain `Add`), wave 2's `Map.SpellStates`, the per-map `StealthRegistry`, the per-map
  seeded `Random` (one stream per stripe derived from (seed, map, stripe)), wave 3's per-map loot registries, `MapEvent`
  posting, `MapSequence` counters and the map's allocation accounting. §3's rule "only the thread updating that map writes it"
  becomes "only the thread updating that stripe writes its buffer".
- World-wide state still needs §3. Map-wide scripts (entries addressed anywhere on the map) must be gated or deferred.
- **Determinism:** fixed stripe order gives the same result at any helper count, but **not** today's serial order. That is an
  order change behind a switch, like `CreatureSplinePass`.

### 4.3 Option C: data-parallel kernels (compute in parallel, apply serially)

For read-mostly stages: a **compute** step on helpers reads a frozen snapshot and writes only per-item result slots; an
**apply** step on the map's thread runs in join order (`MapSequence`). Candidates: spline advance; visibility decisions per
moved player (O2); create/values block building per viewer. Determinism by construction, provided no item reads another
item's output of the same pass.

**Scheduler change (needed by B and C).** Today finished workers signal `_done` and sleep on `_go`
(`MapUpdateScheduler.cs:109-127`), and with 2 maps on 4 threads only one worker is woken (`:63-68`). The "2 idle workers" of
revision 1 are asleep and cannot take side jobs. Redesign:
- A side-job queue per phase. `RunHelpers(count, body)` pushes `count` jobs and wakes parked workers.
- **Caller-runs:** the calling map thread drains its own jobs too, so `RunHelpers` completes with zero helpers (at N=1, or when
  every worker is busy). Its result never depends on helper count.
- A worker that has no map item waits on "side job available or phase over", not on `_done`/`_go`. The barrier counts
  outstanding map items **plus** outstanding side jobs, and the phase ends when both are zero.
- `RunHelpers` asserts that the caller does not hold the serial gate. Helpers run with `TickOwner.IsHelper` set and no current
  map: any `CheckOwner`, `CheckGlobal` or `PostAfterMaps` from a helper fires, and helpers never take the gate. That rules out
  the deadlock of a map thread holding the gate and waiting for a helper that waits for the gate.
- Helper allocations: each job measures `GC.GetAllocatedBytesForCurrentThread` around its body and adds the delta to the
  calling map's bytes, so MapsBytes stays complete (`WorldRuntime.Parallel.cs:223`, `:243`).

### 4.4 Choice

**C first, then B for creature AI; reject A.** C is safe and deterministic and uses idle threads once the scheduler can lend
them. B is what can split the ~50 ms creature walk of one continent, and it reuses everything §3 builds. But neither comes
before wave 4-lite: quiet creatures and waypoint legs need no new scheduler at all.

---

## 5. Implementation waves

Rules for every wave:
- **Off is today's behaviour**, with exactly two stated exceptions: (a) the always-on guards of §3.3, whose non-violating cost
  is one volatile read and whose repeated-violation cost is one lookup (tested); (b) nothing else. Structural moves that would
  change iteration order (waves 2-3) are chosen once at start-up from their switch, so with the switch off the old structure
  and its order are used (§5.4).
- A switch that changes ordering changes it the same way at every thread count, so the 1-vs-N test compares like with like.
- Each wave ships with its keys in `docs/reference/configuration.md`, an options property, `check-config` validation, and a
  before/after measurement on the same harness: tick p50/p95 from TickSummary at N=4, overlap, gate entries/contended/wait per
  minute, gate time by owner, GC pauses and working set.
- Expected gains below use the §1.2 p50 basis and are estimates.

### 5.1 Wave 0: measurement, guards, two latent fixes

No intended behaviour change, except the two fixes, which only touch configurations or paths that are wrong today.

- **Baseline:** live back to `MapUpdateThreads` 4 (decision 1), then a 60 s bot-benchmark baseline (`ARCANECORE_BOT_BENCH=200`)
  at N=1 and N=4 with the minute line, tick p50/p95 and per-stage means.
- **Gate time by owner:** time spent holding the gate, attributed to the stage, updater type or opcode that took it (a per-map
  `long[]` per stage plus a small dictionary for updaters and handlers), top 5 in the minute line.
- **Cheap guard dedupe** for `CheckOwner` (§3.3) and the new `CheckGlobal` / `CheckGlobalRead` with the `GlobalStructure`
  records, at the sites listed in §3.3. Report on live, Throw in tests.
- **Fix: `Gate(stage)` releases the held gate for an ungated stage** (§3.6), so `Ungated` overrides do what they say.
- **Fix the `MapStageGates.cs:12` enum comment** (Heartbeat is the 5.2 s event only).
- **Precondition for wave 1:** owner violations 0 and global-state reports 0 (or each one explained) over a 1 h live Report run
  at N=4. Today's figure: 0 owner violations in 1,073 minute lines.
- **Tests:** allocation count on repeated violations (0 after the first); each guard fires once in Report and throws in Throw
  for an ungated test updater (writer: `GroupManager.MarkAuraChanged`, `StealthRegistry.SetVisibility`; reader:
  `SpellSystem.GetAuras` against a gated writer on another map); an `Ungated=Heartbeat` override at N=4 shows the heartbeat
  loop without the gate (counter).
- **Check (review 3):** after wave 0, CreatureMapSystem should hold at least 70 % of held-gate time on maps 0/1. If Packets,
  Heartbeat and Output together exceed 25 %, the §1.2 fractions are wrong and waves 1-2 move ahead of 4-lite.

### 5.2 Wave 4-lite: creatures with no AI work, waypoint legs

The status page's next step (`parallel-maps-status-20261017.md:62-64`), moved forward because it addresses most of the gated
time.

- **Switch:** **World:Parallel:CreatureQuietPass** (bool, default false).
- **Scope:** in `CreatureMapSystem`, split the walk into a quiet pass and the rest. A creature is quiet this tick when it has no
  AI tick due, no script timer due, no spline end, no combat or threat list, and no pending spawn-pool or respawn work; the
  quiet pass advances its timers and waypoint legs between nodes outside the gate. Anything that turns out not to be quiet
  mid-pass (a script hook, a pool call, a kill, an aggro check that reaches X1/X3) takes a lazy `Serial()`, the pattern
  `CreatureMapSystem.cs:1093-1157` already uses for wanderers.
- **Acceptance tests:** (1) off is today (existing CreatureMapSystem and `CreatureSplinePassTests` unchanged, gate entries
  equal on a fixed scenario); (2) quiet classification per condition, one fixture each; (3) a creature that stops being quiet
  mid-pass observes `HoldsSerialGate` true in the hook (counter > 0); (4)
  `SameSeededTicks_GiveIdenticalStateAndPackets_At1AndNThreads` (`ParallelMapUpdateTests.cs:267`) with the switch on; (5) the
  200-bot benchmark at N=4 with Throw: no `CheckGlobalRead` or `CheckOwner` fires, and the minute line shows the phase ran
  (ticks > 0, overlap > 1).
- **Expected:** the CreatureMapSystem share of gate time falls by the quiet fraction, which wave 0 measures (unknown today; the
  share of creatures with no AI work in a tick is not logged). Upper bound about 50 ms per continent update.

### 5.3 Wave 1: the queue, per-handler classes, Heartbeat out of the gate

**Scope (files):**
1. `src/ArcaneCore.Game/Maps/Parallel/WorldRuntime.AfterMaps.cs` (new): `PostAfterMaps`, `DrainAfterMaps`, per-map lists,
   routing rules, counters (§3.2). `WorldRuntime.cs`: `TickOwner.Enter` in the serial loop (`:511-523`); the drain inside the
   `try` (`:503-561`).
2. `src/ArcaneCore.Game/Maps/Parallel/WorldRuntime.Parallel.cs`: after-maps counters in the minute line.
3. `src/ArcaneCore.Game/Maps/Parallel/ParallelMapOptions.cs`: **World:Parallel:PacketsByOpcode** and
   **World:Parallel:LazyHeartbeatGate** (bool, default false). `MapStageGates.CodeDefault` (`MapStageGates.cs:59`) takes them
   into account (§3.6).
4. `src/ArcaneCore.Game/Maps/Map.Gating.cs`: public `Map.HoldGateForStage()` for the World layer's Gated handlers.
5. `src/ArcaneCore.Game/Maps/Map.Broadcast.cs`: pooled outbox buffers; outbox flush at the end of `Update` (§3.7).
6. `src/ArcaneCore.Game/Maps/Map.Update.cs`:
   - with **PacketsByOpcode**: transit handling (`:160-169`) and logout completion (`:172-187`) post to the queue (re-checks on
     run: `IsOnline`, `player.Map`, `IsLogoutDue`);
   - with **LazyHeartbeatGate**: the loop runs ungated; `Unit.UpdateHeartbeat` (`Unit.cs:30-38`) returns how many times the
     timer fired; the map collects (unit, count) for units with subscribers into a reused list; after the loop, if the list is
     not empty, it takes the gate once and invokes the events in collection order.
7. `src/ArcaneCore.Game/Combat/MapCombat.cs`: the `PlayerLoggingOut` handler (`:91-96`) posts `OnPlayerLeaving` for a map that is
   not the player's map (`PostAfterMaps`) during the phase; inline otherwise (P11a). This fixes the existing race at N>1 and is
   not behind a switch (the fix only changes the parallel phase, where today's behaviour is a race).
8. `src/ArcaneCore.World/Handlers/OpcodeAccess.cs` (new) and `OpcodeTable.cs`: the per-handler class (§3.4). Entries:
   `MSG_MOVE_WORLDPORT_ACK` Deferred; MapLocal none unless read end to end in the PR.
9. `src/ArcaneCore.World/Net/WorldSession.cs` `ProcessWorldPacketsCore` (`:382-460`): class at the head of the loop on the
   peeked packet; MapLocal inline, Gated via the held gate, Deferred peek-park-resume; `ResumeWorldPackets` with the shared
   fault containment and the resume preconditions.
10. Bookkeeping audit (P3) for `PacketHandlerLatency.Record`, `heuristics.OnWorldPacket` and `LogIfSlowPacket`, or MapLocal
    stays empty.
11. Docs: `docs/reference/configuration.md` (two keys); `docs/research/parallel-maps-status-20261017.md`.

**Acceptance tests** (Game.Tests `Maps/Parallel/`, World.Tests `MapThreads/`; all with `OwnerGuard=Throw`):
1. *Off is today:* both switches off: `ParallelMapUpdateTests` and `CreatureSplinePassTests` pass unchanged; gate entries per
   map per tick match the old count on a fixed scenario; guard cost counted (no allocations from guards over the scenario).
2. *After-maps order:* items posted from 3 maps on 4 threads run after the barrier on the main world thread
   (`IsMainWorldThread`, `!IsParallelPhase`), maps in snapshot order, FIFO within each; the sequence is identical at N=1
   (serial loop), at N=4 with one map (serial loop at N>1) and at N=4 with three maps; a post from a non-map thread during the
   phase is routed to `Post` and counted, identically at every N; an item that posts another runs in the same drain; a
   throwing item is contained and the next one runs; a `RunAfterUpdate` issued during the drain runs in the same tick.
3. *Packet order:* one session queues [MapLocal A, Deferred B, MapLocal C, Gated D] (test-only entries). A runs on the map
   thread with `HoldsSerialGate` false; B, C and D run after the barrier on the world thread, in that order, in the same tick;
   the budget counts all four; a second session on another map is unaffected. Variant: the session closes between park and
   resume; the resume returns without error.
4. *Gated handler:* an unlisted opcode's handler observes `HoldsSerialGate` true at N=4 with the switch on; two Gated packets
   in one stage cost one gate entry (counter).
5. *Hot swap:* a handler for an opcode with a MapLocal test entry, replaced through `TrySwap` with a handler from another
   assembly, is Gated.
6. *Worldport ack:* a far teleport completes with the same end state (map, position, visible sets, packets to the client) at
   N=1 and N=4 with the switch on.
7. *Logout:* a due logout completes after the maps phase in the same tick, `PlayerLoggingOut` runs on the main world thread,
   one save snapshot is queued, an emptied instance unloads in the same tick, and **no packet to that client follows
   `SMSG_LOGOUT_COMPLETE`**. Variant: the player's corpse lies on another map that is in an ungated updater at the same time;
   `RemoveCorpse` for that map runs in the drain, and no `CheckOwner` fires.
8. *Each switch alone:* with only LazyHeartbeatGate on, the heartbeat loop holds no gate (counter) and the subscriber runs
   holding it; with only PacketsByOpcode on and no Gated packet, Packets holds no gate; gate entries match the §3.6 table.
9. *Heartbeat payloads:* food/drink visuals come every 5,200 ms with identical payloads and order with the switch off and on.
10. *Read guard:* a test MapLocal reader of `SpellSystem._states` at N=4 against a gated writer on another map throws in Throw.
11. *Determinism:* `SameSeededTicks_GiveIdenticalStateAndPackets_At1AndNThreads` also with both switches on, with a scenario
    that logs out, teleports, eats food and has a cross-map sender (a whisper and a group update to a third session).
12. *Opcode list:* every entry names a registered world opcode and handler; MapLocal and Deferred entries carry a reason and a
    non-empty `Reach` list whose methods exist (reflection); no MapLocal `Reach` names a non-frozen structure; the test prints
    the count per class (so a filter that matched nothing shows up as 0).

**Expected:** overlap gain about the heartbeat loop only (~4 ms p50 per continent update, at N>1 only). Logout and worldport
move to the serial drain; tick p50 unchanged. Gate entries per minute must not rise beyond the §3.6 table (+1 per map update
with LazyHeartbeatGate). The purpose of wave 1 is the mechanism and the P11a fix.

### 5.4 Wave 2: per-map spell and stealth state; Output out of the gate

- **Store chosen at start-up:** `ISpellStateStore` with two implementations: `GlobalSpellStateStore` (today's `_states`
  Dictionary and its iteration order) and `PerMapSpellStateStore` (`Map.SpellStates`, keyed by guid; units in transit in a small
  main-thread-only side table). **World:Parallel:SpellsPerMap** (read once at world build, not hot-toggled) picks one. The
  same pattern for `StealthRegistry` (world or per map), the `GroupManager` aura and stats marks (X5: the global set, or marks
  on `Player`), and wave 3's loot registries. With the switch off, every walk is today's walk in today's order; the old
  implementation is deleted when the switch defaults on. The alternative, one structure with a global sequence number per state
  to keep today's order, is rejected: it costs a sort per tick. If Nate prefers to declare these as unconditional order changes
  instead, that is decision 4.
- A per-map `SpellMapUpdater` (an `IMapUpdater`) runs the cast, aura and cooldown update of that map's states when the switch
  is on; `SpellFeature` stops updating them (`SpellFeature.cs:284`). That is an order change (auras tick during the map's
  update), the same at every thread count.
- **It is not a gain at one map thread.** At N=1 the serial loop runs the same work on the same thread
  (`WorldRuntime.cs:511-523`): a move between phases, zero-sum. And it cannot be MapLocal yet: periodic damage goes
  `SpellSystem` → `MapCombatDamageSink.cs:33` → `MapCombat.DealDamage` → `UnitKilled` (`MapCombat.Melee.cs:1050`), which is
  classified only in wave 3. In wave 2 the spell updater stays gated on every tick that deals damage.
- `HolderAdded/HolderRemoved` move to `MapEvent` with every subscriber classified (X2). A seeded `Random` per map derived from
  (seed, map id, instance id).
- Output becomes MapLocal under **World:Parallel:OutputLocal** (O3 resolved). `UpdateBlockWriter` statics are checked by the
  extended audit. Then `EnchantUpdater`, `StealthDetectionUpdater` and `LiquidAuraInterruptUpdater` get `[MapAccess(MapLocal)]`
  with reasons, or a list of what still blocks them.
- **Tests:** spell suites at N=1 and N=4 with the switch on; with it off, the aura tick order and the RNG draw sequence equal
  today's on a fixed scenario (hash); the determinism hash with casts, auras, stealth and detection across two maps; a rogue
  stealth test on two maps at once; the read guard on the global store (unreachable with the switch on); a counter of gate
  entries made from `SpellMapUpdater`, which must be > 0 with damage-dealing auras present (it is not MapLocal yet).
- **Expected:** at N=4, Output (3.0 ms p50) and EnchantUpdater (9.0 ms p50 where present) leave the gate. Tick p50 at N=1
  unchanged within noise. Metric: tick p50/p95 at N=4, not features p50.

### 5.5 Wave 3: the combat event fabric and cross-map work

- `MapCombat.UnitKilled/DamageTaken` move to `MapEvent`, with every subscriber in X3/X4 classified. Per-character
  progression, reputation and quest state is stored on (or keyed by) the player and becomes MapLocal. Social, conversation,
  battleground, world-event and bot-feed handlers become Deferred, with value captures. Loot registries become per map (store
  chosen at start-up, §5.4).
- Stage (6) Deferred: far teleports (`TeleportService.cs:438`) and transport hand-offs (`TransportSystem.cs:679`, `:695`) post to
  the end-of-phase queue. `RunAfterUpdate` keeps its map-local users (`TotemSystem.cs:349`, `CreatureMapSystem.cs:207`).
- **Map creation:** `GetMap(uint,uint)` (`WorldRuntime.cs:302-318`) is synchronous, so a posted creation cannot satisfy an
  in-phase caller. Once the gate goes, an in-phase miss is a guard failure (`CheckGlobal("map create")`), and every in-phase
  caller (far teleport `TeleportService.cs:438`, instance resolution) must already be Deferred. Until then creation stays gated.
- Movement observers (P5a): with `DamageTaken`/`UnitKilled` classified, FallObserver's damage path becomes MapLocal;
  UndermapObserver's graveyard repop becomes Deferred.
- Then `MapCombat`, `ReactiveUpdater`, `LootRollManager` and `SpellMapUpdater` get `[MapAccess(MapLocal)]`.
- **Tests:** kill credit, XP share, reputation and quest progress for a group spread over two maps; loot rolls; far teleports
  and boat rides between continents at N=1 and N=4 with identical results; event-order listing per event (§3.5).
- **Expected:** MapCombat (2.6 ms p50) leaves the gate, and the spell update moved in by wave 2 (`SpellFeature` 16-46 ms for the
  whole world) becomes overlappable across maps. With two continents, at most about half of it overlaps.

### 5.6 Wave 4: the rest of the creature walk

After 4-lite: AI with lazy gates around script hooks, spawn pools and respawn persistence (the X1/X3 dependencies are resolved
by waves 2-3).
- **Switch:** **World:Parallel:CreatureAiLocal** (bool, default false).
- **Acceptance tests:** as 4-lite (off is today; determinism at N=1/N=4 with the switch on; benchmark with Throw and no
  guard fires), plus creature-script fixtures that call world-wide services from AI (each must take the lazy gate or post).
- **Expected:** the CreatureMapSystem share of gate time (wave 0's counter) falls toward the remaining script and pool calls;
  overlap toward the 2x two-continent ceiling.

### 5.7 Wave 5: allow-list growth and bot think per map (plan W4)

- Movement (P5 per-player marks; P5a after wave 3) becomes MapLocal; casts after waves 2-3; loot and quest NPC after wave 3;
  group, guild, chat and economy stay Deferred. When the last group, guild and channel writers are Deferred, those structures
  are marked frozen (§3.1).
- Bot think per map (**World:Playerbots:ThinkInMapPhase** from the plan): each map's bots think inside the map's update, after
  the updaters and before Output, under a per-map share of the think budget. `TryManagedAction` and `TryManagedMovement`
  (`ManagedWorldSession.cs:136-178`) follow §3.4's bot rules (queue after the first Deferred action, dedupe).
- Lands after the path-job pool and the social pass slicing. It reuses both and redesigns neither.
- **Tests:** a bot [Deferred, MapLocal] sequence runs in issue order; a re-issued pending action is not queued twice; outbox
  order against resumed packets for one session.
- **Expected:** the per-map share of `ManagedPlayerbotFeature` (50-98 ms in the top-features field) moves into the maps; with two
  continents at most about half of it overlaps. Bot quest completions per hour and the bot-stall anomaly rate unchanged.
  Outbox allocation per tick within the §3.7 budget.

### 5.8 Wave 6: intra-continent kernels, then stripes (§4)

- 6a: the scheduler change of §4.3 (side-job queue, caller-runs, barrier over items plus side jobs, no `RunHelpers` under the
  gate, helper allocation accounting) and kernels for spline advance, visibility decisions and block building, behind
  **World:Parallel:MapHelpers** (0 = off). Tests: each kernel's output byte-identical to the serial pass at 0, 1 and 3 helpers;
  `RunHelpers` completes at N=1 and with every worker busy (caller-runs); a helper is actually used at 2 maps on 4 threads
  (counter > 0); `RunHelpers` under the gate throws; helper bytes appear in MapsBytes.
- 6b: red-black stripes for creature AI behind **World:Parallel:CellStripes**, with per-stripe buffers for every structure in
  §4.2 and the stripe barrier. Tests: stripe-boundary fixtures (combat, AoE, chase, pets and group credit across a stripe
  line); per-stripe registries (spell states, stealth, loot, after-maps) merged in stripe order, identical at any helper count.

---

## 6. Risks

### 6.1 Determinism

- Every switch changes the order of events. The 1-vs-N test compares N=1 and N=4 **with the same switches on**.
- **The serial loop must set `TickOwner`** (§3.2), or N=1 and N=4 route posts differently.
- **Gated work is safe but not deterministic across maps.** Two maps' gated stages run in gate-acquisition order. A third
  session that receives packets from both can see them in a different order from run to run. Deferred work is both safe and
  deterministic. The determinism test includes a cross-map sender.
- **`_world.Post` from map threads is nondeterministic today at N>1** (X15): two far-teleport acks on different maps enqueue
  `CompleteFar` in thread-arrival order, so they can complete in a different order from run to run, with every switch off. The
  `MapAccess.MapLocal` documentation is changed to say that ordering-sensitive world work goes through `PostAfterMaps`, and
  the worldport ack's Deferred class (wave 1) moves its `Post` into the drain, where the order is fixed. A counter of `Post`
  calls from map threads during the phase goes in the minute line.
- Shared seeded `Random` instances in tests are a data race at N>1 (X14): per-map seeding in wave 2.

### 6.2 Packet order per session

- Inbound: one consumer per session per tick, and Deferred parks the rest of the queue. Bots follow the same rule through the
  per-session bot action queue (§3.4). A player is processed by at most one map per tick.
- Outbound: `Session.Send` is ordered by `_sendLock` (`WorldSession.cs:339`); outboxes are flushed before the drain (§3.7).
- **Order changes to record and accept** (decision 3):
  - a deferred logout leaves the player in the map through Heartbeat, the updaters and Output of that tick (today it leaves in
    stage 1b, `Map.Update.cs:172-187`); the client gets that tick's output, then `SMSG_LOGOUT_COMPLETE`, then nothing;
  - a Deferred handler runs after the map's Output, so a values change it makes goes out with the next tick's Output (one tick,
    ~100 ms on live today); direct replies go out at once;
  - a far teleport issued by a resumed handler runs in the same tick through the drain's `RunAfterUpdate` routing (§3.2), not
    one tick later;
  - a Deferred event subscriber runs after later subscribers of the same event (§3.5);
  - with SpellsPerMap, auras tick inside the map's update.

### 6.3 Deadlocks

Lock order: serial gate, then `_maps` lock (`WorldRuntime.cs:325-353`), then session `_sendLock`. Never take the gate while
holding `_sendLock`: the managed-packet observers run under it and must not send (`ManagedWorldSession.cs:80-84`) or enter the
gate. A map thread never waits for the world thread (`WorldRuntime.cs:85-91`). Helpers never take the gate, and `RunHelpers`
is never called under it (§4.3). The held gate is always released at the end of a map's update (`Map.Update.cs:43`). The
end-of-phase queue takes no lock on the map path. Add a test that runs N > cores with random LPT ties and a watchdog on the
barrier, so a hang fails the test instead of stalling CI.

### 6.4 Test strategy

- **OwnerGuard=Throw everywhere in tests** (`ParallelMapUpdateTests.cs:18`), which also activates the read guard. A test-host
  switch (an environment variable such as `ARCANECORE_TEST_MAP_THREADS=4`) read by the shared world builder runs the World and
  Playerbots suites a second time at N=4 with Throw and every wave's switches on, as its own CI job.
- **Stress:** the 200-bot benchmark and `WorldTickLoadTests` at N=1/2/4/8 with Throw and random tie-breaking. A run with 0
  violations counts only if the minute line shows the parallel phase ran (ticks > 0, overlap > 1).
- **StaticMutableStateAudit extensions:** (a) list `static readonly` fields whose type is a mutable collection (about 45 by
  grep; the reflection scan gives the real number), each `Frozen*`/`IReadOnly*` or allow-listed with a reason; (b) a
  shared-instance audit: walk the fields of every `IMapUpdater` of a two-map world one or two levels deep and list every object
  reachable from both maps' updaters that holds a non-concurrent collection; each needs a declaration and a reason (X12).
- **Opcode coverage:** §5.3 test 12, plus one test per MapLocal entry running the handler at N=4 with Throw against players on
  two maps at once.

### 6.5 Debug-build overheads

Live runs Debug by choice, so every guard costs real time there. Rules: writer guards always, reader guards only in Throw mode.
Each guard starts with the volatile `_parallelPhase` read and returns. The stack trace is built only when the
`(what, file, line)` key is new (§3.3; today's `CheckOwner` builds it every time, fixed in wave 0). The after-maps lists are
reused, outbox buffers pooled, and closures cached where the call site allows. Measure each wave in Debug. An optimized build
would scale all of the gains, but its multiplier on this server has never been measured (decision 0).

### 6.6 Behaviour drift from deferral

A deferred effect runs a little later in the same tick. Code that reads the effect back in the same map update would break.
The park-the-rest-of-the-queue rule covers packets and bot actions. For event subscribers, the reviewer of each Deferred
classification checks that no later subscriber and nothing later in the map update reads the effect. Where something does, the
subscriber stays Gated until the reader moves as well.

---

## 7. Decisions for Nate

0. **Optimized build A/B.** Release/Optimize (plan W0) multiplies every gain here, and the multiplier on this server is unknown.
   One A/B hour per arm would size it before any wave is measured.
1. **Turn `MapUpdateThreads` back to 4 on live** (it has been serial since 2026-10-21 16:12 UTC) for wave 0's baseline. It
   restores overlap x1.45. Known risk: the P11a race (a logout with the corpse on another map runs that map's `OnPlayerLeaving`
   on the wrong thread while its CreatureMapSystem or GameObjectMapSystem may run ungated). It was live on 10-20/21 with 0
   owner violations reported (the guard does not cover those writes), and is fixed in wave 1. Options: re-enable now and
   accept it, or re-enable after the wave 1 fix (the P11a fix can also ship alone, ahead of wave 1, if the queue is split out).
2. **Wave order 0 → 4-lite → 1 → 2 → 3 → 4 → 5 → 6.** Revision 1 recommended 1 → 2 → 3 → 5, on the claim that wave 2 helps at
   one map thread; that claim was wrong (§5.4). 4-lite targets ~50 of the 55 gated ms; waves 1-3 target ~10-20 and mainly buy
   ordering and safety.
3. **Order changes behind switches** (§6.2 list): acceptable, as with `CreatureSplinePass`? Each is the same at every thread
   count and off by default.
4. **Waves 2-3 structural moves:** keep both stores until the switch defaults on (this page's choice: off stays today's order,
   at the cost of two implementations for a while), or move unconditionally and accept the order change with the switch off.

---

## 8. References

- vmangos MapManager (continent threads, deferred far teleports): https://github.com/vmangos/core/blob/development/src/game/Maps/MapManager.cpp
- vmangos Map.cpp (MTCells stripes, motion and visibility threads): https://github.com/vmangos/core/blob/development/src/game/Maps/Map.cpp
- TrinityCore MapUpdater (schedule, wait, delayed work): https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Maps/MapUpdater.cpp
- AzerothCore on continent partitioning: https://github.com/azerothcore/azerothcore-wotlk/issues/22571

Related ArcaneCore pages: [multithreading plan](multithreading-plan-20261017.md), [parallel maps status](parallel-maps-status-20261017.md),
[async navigation acceptance](../ASYNC_NAVIGATION_ACCEPTANCE.md), [clustering design](../CLUSTERING_DESIGN.md).

---

## 9. Review responses

Three reviews of revision 1 (2026-10-24), all static reads of `0f4bd8c3b`. "Fixed" names the section that changed. Where a
review figure was re-checked for this revision, it says so.

### Review 1

| Item | Response |
|---|---|
| B1 wave 1 MapLocal examples wrong | Fixed. Examples removed; their closures are listed in P4 as counter-examples; §0.7 and §3.4 state that no candidate is known; the opcode-list test requires a `Reach` list (§5.3 test 12) |
| B2 movement observer chain missing | Fixed. P5a (R, waves 2-3; graveyard repop D); wave 5 movement depends on wave 3 (§5.5, §5.7) |
| B3 `CheckOwner` cost misstated; copied into `CheckGlobal` | Fixed. Re-checked: the stack is built at `:123` before the `TryAdd` at `:125`. Caller-info dedupe key before the stack, for both (§3.3); allocation test (§5.1); guards counted in the off-path test (§5.3 test 1) |
| B4 waves 2-3 not neutral when off | Fixed. Stores chosen at start-up, so off keeps today's structures and order (§5.4); the unconditional alternative is decision 4 |
| B5 class keyed by opcode only | Fixed. Keyed by the registered handler's method and assembly; hot-swapped handlers are Gated (§3.4, test 5) |
| B6 `PlayerLoggingOut` under-audited; cross-map write | Fixed. Re-counted: 32 subscribers; 5 Game-layer ones confirmed by grep, `FeatureSubscriptions` listed with its layer unconfirmed. P11a records the existing race; wave 1 fixes it (§5.3 item 7, test 7); decision 1 states the risk of re-enabling N=4 first |
| N1 P10 inaccurate | Fixed (P10) |
| N2 `Post` order nondeterministic | Recorded (X15, §6.1), with a counter and a documentation change |
| N3 gate churn for Gated packets | Fixed. Held gate per stage (§3.4, test 4) |
| N4 outbox allocation | Addressed. Pooled buffers from wave 1, budget in wave 5 (§3.7) |
| N5 drain after the pooled array is returned | Fixed. Drain inside the `try` (§3.2) |
| N6 `GateAllStages` with Deferred | Fixed. It forces every packet Gated and disables deferral (§3.4, §3.6) |
| N7 owner violations figure | Added. Re-checked: 0 in 1,073 minute lines (§1.1); zero is a wave 1 precondition (§5.1) |
| N8 `MapCreated` count | Fixed. Re-counted 37; `MapUnloading` re-counted 25 (was 20) |
| N9 session closed between park and resume | Fixed (§3.4 resume preconditions, test 3 variant) |
| U1 live log figures; Information level | Re-checked: the current log writes INFO lines, so the missing minute line means a serial phase (§0.3) |
| U2 static field counts | Still grep counts; the reflection scan in §6.4 replaces them |
| U3 visibility rules read-only | Still unverified; marked in O3 and covered by the read guard in tests |
| U4 heuristics state | Still unverified; P3 keeps all bookkeeping gated until audited |
| U5 plan W5-W7 | Not re-read for this revision; nothing here depends on them beyond W4 and W7 |

### Review 2

| Item | Response |
|---|---|
| B1 guard blind to the read race | Fixed. Frozen rule (§3.1), `CheckGlobalRead` in Throw mode (§3.3), frozen flag checked by the classification test (§5.3 test 12); the "safe to read in wave 1" claim is withdrawn |
| B2 two wave-1 opcodes not local | Fixed (as review 1 B1); the audit rule now follows event subscribers (§2) |
| B3 held gate never released by `Gate(stage)` | Fixed. Re-checked at `Map.Gating.cs:39-47`. Release for ungated stages (§3.6, wave 0), gate-entry table, per-switch counter test (§5.3 test 8). The same defect makes today's `Ungated` stage overrides ineffective; wave 0 fixes that |
| B4 queue not deterministic at N=1 | Fixed. Re-checked: `TickOwner.Enter` only in `RunMapsParallel`. Serial loop sets it; non-map posts go to `Post`; test 2 covers N=1, one map at N=4 and three maps |
| B5 guard cost | Fixed (as review 1 B3) |
| B6 bot action order | Fixed. Per-session bot action queue after the first Deferred action, dedupe, [Deferred, MapLocal] test (§3.4, §5.7) |
| B7 wave 6 vs one thread per map; helpers asleep | Fixed. Full multi-writer list (§4.2); scheduler redesign with caller-runs and a barrier over items plus side jobs; no `RunHelpers` under the gate (§4.3, §5.8) |
| N1 P10 rationale | Fixed |
| N2 drain placement | Fixed |
| N3 outbox allocation and order | Fixed (§3.7) |
| N4 bookkeeping before the class | Fixed. Class decided on the peeked packet before the bookkeeping, which runs under the class, gated until P3 is audited (§3.4) |
| N5 fault handling of resumed packets | Fixed. Shared containment helper (§3.4) |
| N6 order changes missing | Fixed. §6.2 lists them; the far teleport case now runs in the same tick; test 7 asserts nothing after `SMSG_LOGOUT_COMPLETE` |
| N7 `MapEvent` order and re-entry | Fixed. Gate taken before the first subscriber when any is gated; order rule and listing test (§3.5) |
| N8 hot reload | Fixed (as review 1 B5) |
| N9 helper allocations | Fixed (§4.3) |
| N10 more explanations for missing lines | Answered: Information is on and continents exist, so serial (§0.3); the first-call baseline only delays the first line by a minute |
| N11 posts from the features phase | Fixed. Inline on the main world thread outside the phase (§3.2) |
| N12 missing tests | Added: §5.3 tests 2, 3, 8, 10; §5.7; §5.8 |
| U1 vmangos SafeDistance | Removed the figure; marked unverified (§4.2) |
| U2 last updater before Output gated | Not checked; the §3.6 release rule makes the answer irrelevant for OutputLocal |
| U3 counts | Re-checked by grep: `PlayerLoggingOut` 32, `MapCreated` 37, `MapUnloading` 25. `OnWorld` 260 and `IsWorldThread` 44 not re-checked |
| U4 live figures | See review 3 below |

### Review 3

| Item | Response |
|---|---|
| Root cause: no gated-fraction computation | Fixed. §1.2 table and per-wave unlock figures (§0.1) |
| B: wave 2 "helps at one map thread" | Fixed. Withdrawn; zero-sum at N=1, gated while it deals damage until wave 3; metric is tick p50/p95 at N=4 (§5.4) |
| B: phase-split numbers not reproducible | Answered and fixed. The figures came from the "tick phases p50/p99 ms last 1 min" field of TickSummary lines, which a `features p50` grep does not match. Re-extracted over all 246 lines (§1.1) and shown next to the review's TickOverrun extraction. "Serial phases cost more" is now stated as "larger at the median, with a comparable or worse maps tail" |
| B: Deferred counted as leaving the gate | Fixed. Wave 1 expected gain is the heartbeat loop only; logout and worldport move to the drain (§5.3) |
| Serial loop `TickOwner` | Fixed (§3.2) |
| Gate churn | Fixed (§3.4) |
| Wave 3 map creation | Fixed (§5.5) |
| Inconsistent basis | Fixed. p50 basis throughout; mean shown next to it (§1.2) |
| Creature figure | Fixed (§1.2 note) |
| Stale enum comment | Added to wave 0 (§5.1) |
| Live serial as fact | Stated (§0.3), with the Information-level check |
| Simpler option first | Adopted. Wave 0 plus N=4 restored (decision 1), wave 4-lite second (§5.2, decision 2), hangs attributed to the in-flight lanes (§1.2), decision 0 for the optimized build |
| Verifiable checks | Adopted: the 70 % / 25 % check after wave 0 (§5.1); gate entries must not rise (§5.3); the `SpellMapUpdater` gate counter (§5.4) |
| U: where live's `World:Parallel:*` values are set | Still unknown; decision 1 asks Nate |
| U: Release multiplier | Decision 0 |
| U: `SendToSet` through the outbox | Marked unverified in H4; it does not affect wave 1 safety |
