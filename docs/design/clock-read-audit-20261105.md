# World clock read audit

This audit covers the clock sources of `src/ArcaneCore.Playerbots` and `src/ArcaneCore.Game`: every `Now`-style property, every injected clock delegate
(`Func<long>`, `Func<uint>`), every accumulator a system advances itself, and the game-UTC reads, each in a table below with its family. It also covers the
fields assigned from a clock reading, by a scan of single-line and split-line assignments (the patterns are in "Method and tallies"), and the helpers that
receive a reading. It records what each clock is, which sites share a clock, and the one
double read it found and fixed. It is a static reading of the source; nothing here was run against a server. The tests named in this doc were not run
when it was written.

Citations name a file and a symbol, with a line only where the line was read in the tree this doc was committed in. Lines go stale as files change;
re-grep the symbol rather than trusting a number.

## What "double read" means here

Two distinct failure shapes were looked for:

1. A stamp taken from one clock and compared with, or subtracted from, a reading of another clock (a mixed-clock comparison). It gives a wrong
   duration at best and a wrap or a huge gap at worst.
2. Two reads of one clock inside a single tick where the code wants the time the work took. On the world clock both reads are the tick-start value, so the
   difference is 0. Only `LiveNowMs`, a wall clock or an accumulator that moves inside a tick can give a non-zero difference.

## Clock semantics

`WorldClock` (WorldClock.cs) exposes `NowMs`, `ElapsedMs`, `LiveNowMs` and `UtcNow`. `Uptime` is on `WorldRuntime` (WorldRuntime.ManualClock.cs), not on
`WorldClock`. While a tick runs, `NowMs`, `UtcNow` and `Uptime` are the tick-start reading, so two reads in one tick are equal and a duration taken
between two of them in one tick is 0. Exception: on the real clock `ElapsedMs`, and so `NowMs`, `UtcNow` and `Uptime`, is a live stopwatch read until
the first tick (`WorldClock.ElapsedMs`). `LiveNowMs` is the reading that moves inside a tick, and it must not be compared with a stamp taken from `NowMs`.

`NowMs` is a wrapped `uint` (vmangos `getMSTime`). `Uptime` is the same time as a non-wrapping 64-bit value (`TimeSpan.FromMilliseconds(ElapsedMs)`). The
two agree modulo 2^32, so a `Uptime`-based stamp and a `NowMs`-based one are never compared with each other (see "Result").

## Clock families

Every clock source in the two projects found by the scans and the delegate grep in "Method and tallies" is one of these. The name `NowMs` is reused by several of
them, so a name is never proof of the family; the source is. The wrapper, delegate and accumulator tables below give each named source its family.

| Family | Source | Type | Where |
|---|---|---|---|
| World | `WorldRuntime.NowMs` / `Clock.NowMs` (tick start) | `uint`, wraps | most of the playerbot brain, Game systems that hold a `WorldRuntime` |
| Uptime | `WorldRuntime.Uptime.TotalMilliseconds` (tick start) | `long` | the four `Now` wrappers below, PlayerbotRecovery, PlayerbotBrain loot stamp, the defer ledger, ScenarioClock |
| Live | `WorldClock.LiveNowMs` | `uint`, wraps | ManagedPlayerbotFeature think-gap and starvation code only |
| Map accumulator | a system's own `_clockMs += diffMs` | `long`; a few are `uint` or `ulong` (see the accumulator table) | CreatureMapSystem, GameObjectMapSystem, GridContainer, FishingService, FireworksShow, HolidayObjectScripts, RegtharDeathgateAI, and the accumulators in the accumulator table |
| Social accumulator | `PlayerbotSocialFeature._clockMs += elapsedMs` (`PlayerbotSocialPass.NowMs`, `ClockMs`) | `long` | every playerbot social lane; also the event board and defense board clock |
| Population accumulator | `PlayerbotPopulationFeature._clockMs += elapsedMs` | `long` | population feature only |
| Battleground accumulator | `BattlegroundManager.NowMs += diffMs` | `uint` | BattlegroundManager and BattlegroundQueue |
| Receive stamp | `Clock.Milliseconds()` (a `Func<long>`, `Environment.TickCount64`) truncated to `uint` at the gateway or at `ManagedWorldSession` | `uint` after truncation | `MovementSample.ReceivedMs`, `MovementAntiCheat`, `SpeedHackDetector`, `LocomotionState` reject window (stamped in `MovementCheats.RejectAndHeartbeat` from the local `Clock.Milliseconds()`; `GatewaySessionLinkHost` translates the gateway stamp to worker time, so both writers of the window share one clock) |
| Game UTC | `Clock.UnixSeconds` / `Clock.UnixMilliseconds` of `DeathClock` (`WorldDeathClock` follows world time on the manual test clock, else `DateTimeOffset.UtcNow`), `HonorClock`, `IRespawnClock`; the `UnixSecondsClock` / `unixNow` delegates in the delegate table | `long` Unix seconds or ms | duel, honor kills, respawn persistence rows (`CreatureMapSystem.RespawnPersistence`), death timers, instance resets, talents, chat restrictions |
| Wall | `Environment.TickCount(64)`, `Stopwatch`, `DateTimeOffset.UtcNow`, `TimeProvider.GetUtcNow` | `long` | rate limits, persistence rows, budgets, chat limiter, logs |

## Wrappers

The `Now`-style properties and wrapper delegates, with their source. The table has 15 rows naming 17 wrappers (two rows name two each: the boards, and
`PlayerbotWorldClock`). Every row is counted, Wall ones included. The clock delegates held as fields or constructor parameters are in the next two tables.

| Wrapper | Source |
|---|---|
| `PlayerbotGroupAI.Now` | Uptime |
| `PlayerbotBattlegroundAI.Now` | Uptime |
| `PlayerbotFollower.Now` | Uptime plus `ClockOffsetMs` (a test seam, 0 in production) |
| `PlayerbotPartyAI.Now` | Uptime plus `ClockOffsetMs` (same seam; it also sets the follower's) |
| `PlayerbotBattlegroundCoordinator.Now` | World (`_world?.NowMs ?? 0`) |
| `PlayerbotGroupCoordinator.Now` | World (`_world?.NowMs ?? 0`) |
| `ManagedEventScenarioHost.NowMs` | World |
| `PlayerbotOpportunities.Now` | `Clock?.Invoke()` test seam, else World |
| `PlayerbotWorldPvpObjectives.Now` | `Clock?.Invoke()` test seam, else World |
| `SpellSystem.NowMs` | a delegate; the only production constructor is `SpellFeature` (`() => _world?.NowMs ?? 0`), so World. Tests pass their own |
| `PlayerbotDefenseBoard.NowMs`, `PlayerbotWorldEventBoard.NowMs` | a delegate; `ManagedPlayerbotFeature.EventClockMs` gives both the Social accumulator (`social.ClockMs`), or World when the social feature is not registered. The service is a singleton, so one process uses one of the two for good |
| `PlayerbotSocialMemory.NowMs` | a delegate; `ManagedPlayerbotFeature` passes `_setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()`, so Wall (unix ms) |
| `PlayerbotChat.NowMs` | Wall (unix ms, `TimeProvider`) |
| `PlayerbotWorldClock.UptimeMs` and `Ms(world, nowMs)` | Uptime; `Ms` places a wrapped `NowMs` on the Uptime axis relative to the present (the defer ledger's bridge between the two) |
| `PlayerbotSocialPass.NowMs` | Social accumulator |

### Clock delegates and Game-side wrappers

Each `Func<long>` / `Func<uint>` clock found by
`git grep -nE "Func<(long|uint|ulong|int)>\??\s+_?\w*([Cc]lock|[Nn]ow|[Tt]ime|Ms)\w*" -- src/ArcaneCore.Game src/ArcaneCore.Playerbots`,
traced to the lambda its production constructor receives. "Result" is whether any stamp from it meets a reading of another family.

| Delegate | Source in production | Family | Result |
|---|---|---|---|
| `TaxiFlightSystem._nowMs` | `NpcServicesFeature`: `() => _world?.NowMs ?? 0` | World | only `Relocate(..., _nowMs())` stamps; no comparison |
| `PlayerProgression._nowMs` | `ProgressionFeature`: `() => _world?.NowMs ?? 0` (class default `() => 0`) | World | one use, `ResetLevelPlayedTime(_nowMs())`; no comparison |
| `CreatureMapSystem._serverTime` | `CreatureWorldFeature`, both creation sites: `() => world.NowMs`; default (tests) `unchecked((uint)_clockMs)` | World | `StopSpline(_clockMs, _serverTime())` passes the map accumulator (spline time) and the server time as two separate parameters on purpose (`Creature.StopSpline(long clockMs, uint serverTimeMs)`); the other two reads are `now` locals used against World stamps |
| `WorldEventRuleRunner._clockMs` | `WorldEventRuleFeature`: `() => _clockMs`, the feature's own `_clockMs += diffMs` | accumulator (`long`) | `now` and the `Fire(..., _clockMs())` stamp read the same accumulator |
| `DuelService.Clock` | `DuelFeature`: `DeathHooks.For(world).Clock.UnixSeconds`, or `TimeProvider.GetUtcNow` seconds when a provider is set | Game UTC | the same delegate is also set as `SpellSystem.UnixSecondsClock`, so aura stamps and the duel start share one clock |
| `GroupAuthority.MillisecondsClock`, `GroupManager.MillisecondsClock` | default `Kernel.Logging.Clock.Milliseconds` (`Environment.TickCount64`) | Wall | online-hint stamps and their age checks both use the delegate, both `long`; no other family reaches them |
| `GroupAuthority.UnixSecondsClock`, `GroupManager.UnixSecondsClock`, `SpellSystem.UnixSecondsClock` | `DateTimeOffset.UtcNow` seconds (`SpellSystem` is overridden by `DuelFeature`) | Game UTC | Unix seconds against Unix seconds |
| `InstanceManager._unixNow` | `InstanceFeature`: `time.GetUtcNow().ToUnixTimeSeconds()` | Game UTC | reset times against Unix seconds |
| `InventoryItemService`, `QuestNpcServices._unixNow`, `TalentService._unixNow`, `PetController.UnixNow`, `LunarFestivalScripts.NowUnix`, `ChatRestrictionService` (`unixNow`) | `DateTimeOffset.UtcNow` / `TimeProvider` Unix seconds (`NpcServicesFeature`, `ChatRestrictionFeature`, class defaults) | Game UTC | persisted or calendar times; none meets `NowMs` |
| `PerfLogAggregator._nowMicros` | `WorldRuntime.PerfClockMicros`: `Clock.ElapsedMs * 1000` on the manual clock, else the runtime stopwatch | Wall (microseconds) | perf windows only |
| `NavMeshQuery.DeadlineClock`, `TickStats.Clock` | `Stopwatch.GetTimestamp` | Wall | budgets and tick timing |
| `PlayerbotPathJobs._clockMs` | `ManagedPlayerbotFeature`: `() => unchecked((uint)ClockMs)`, the feature accumulator | feature accumulator (`long`, truncated) | every stamp (`SubmittedAtMs`, `LastAskMs`, `_deadlineReported`, `_lastSummaryMs`) comes from `_clockMs()`; callers hand it no stamp (`WallMs` is a separate stopwatch field) |
| `PlayerbotQaInteractionMonitor._clockMs`, `PlayerbotQaSpellMonitor._clockMs` | default `Environment.TickCount64` | Wall | repeat-suppression stamps against the same delegate |
| `PlayerbotMood._now`, `PlayerbotSocialRecorder._now`, `PlayerbotTradeOwners.Clock` | default `Environment.TickCount64`; `ManagedPlayerbotFeature.TradeDesk` sets the owners' clock to set-aside time Unix ms | Wall | stamp and check on the same delegate |
| `PlayerbotPriceBook`, `PlayerbotTradeDesk`, `PlayerbotTradeGifts`, `PlayerbotTradeLedger`, `PlayerbotCombatTelemetry` (`clockUnixMs`, `unixMs`) | `_setAsideTime.GetUtcNow()` or `TimeProvider.GetUtcNow()` Unix ms | Wall | persisted and trade-window times |
| `PlayerbotGroupCoordinator.UnixNow` | `DateTimeOffset.UtcNow` seconds | Game UTC | compared with respawn-ledger due times in Unix seconds |
| `PlayerbotDefenseBoard._clock`, `PlayerbotWorldEventBoard` clock | `ManagedPlayerbotFeature.EventClockMs` (Social accumulator, else World) | see the wrapper table | see "The two board clocks" |
| `PlayerbotOpportunities.Clock`, `PlayerbotWorldPvpObjectives.Clock` | test seams, null in production | World | see the wrapper table |

### Accumulators

Fields a system advances by the tick diff. Each is stamped and compared on its own field only.

| Accumulator | Type | Notes |
|---|---|---|
| `CreatureSpellListScheduler._nowMs` | `ulong` (the other accumulators are `long`) | `_nowMs += diffMs`; `_readyAtMs` entries are `_nowMs + roll`, compared with `_nowMs` only |
| `ItemMaintenance._elapsedMs` | `uint` | an interval timer: `+= diffMs`, reset to 0 at the zone-limit check; never a stamp |
| `WorldRuntime._autosaveClockMs` | `ulong` | sum of tick diffs; the `_autosaveDue` values and the compare in `WorldRuntime.Autosave.cs` use it alone |
| `ManagedPlayerbotFeature._clockMs` | `long` | `Interlocked.Add(ref _clockMs, elapsedMs)`; restored across a restart (`playerbots.clockMs`); feeds `PathJobs`, the quarantine retry times and the slow-think log |
| `WorldEventRuleFeature._clockMs` | `long` | the delegate source above |

The game-UTC reads (for example `Honor/HonorKillRewards.cs` through `HonorClock.UnixMilliseconds`, `Creatures/CreatureMapSystem.RespawnPersistence.cs` through
`IRespawnClock.UnixSeconds`, `MapCombat.NowSeconds`) are in the Game UTC family. Each pairs a Unix-time stamp with a Unix-time comparison (rows persisted with
a Unix due time); none is read against `NowMs`.

Outside the question and not read as world coverage: `BattlegroundManager.NowMs` (accumulator), `SqliteWriteGate._clock.NowMs` (`IWriteGateClock`,
a stopwatch), `DebugDrawFeature.Now` and `EconomyFeature`-style `Now` properties (world and wall respectively, in `ArcaneCore.World`).

## Method and tallies

A one-off scan over the two projects (a throwaway script, not kept in the repo; the patterns are below so it can be rebuilt) took every single-line assignment whose right side holds a clock read, and classified the
target by the family of the right side. When the right side is a local named `now`-like, it followed the local's declaration back up to 120 lines. It
found 1160 such sites:

| Class of right side | Sites |
|---|---|
| World (`NowMs`, `World.NowMs`, `Now` of a World wrapper) | 338 |
| Local declared from World | 60 |
| Local, family not resolved by the scan (a name such as `swimNow`) | 33 |
| Parameter (`now`, `nowMs` handed in by the caller) | 276 |
| Wall | 250 |
| Map or social accumulator | 157 |
| Local declared from accumulator or wall | 22 |
| Uptime | 16 |
| Live | 8 (4 direct, 4 through a local) |

Patterns used. A clock read is a match of
`\b(NowMs|LiveNowMs|Uptime|Clock\.Milliseconds|TickCount64?|UnixSeconds|UnixMilliseconds|GetUtcNow|UtcNow|Stopwatch)\b`, or one of the wrapper names in the first table, on the
right side of `=` or `=>` on one line; the target is the left identifier. The split-line scan took every line ending in one of `= => ( , ? : + -` whose next
line holds a clock token (`NowMs|LiveNowMs|Uptime|TickCount64|Clock.Milliseconds|UnixSeconds|UnixMilliseconds`): 32 sites in 15 files. They are `RuleTrace` (a
record parameter, not a read), `TeleportService` (`StartedMs` and `StageSinceMs = _world.NowMs`, World), `ManagedPlayerbotFeature` near lines 663 and 3979
(`Suspensions.Export(..., World.NowMs, unixMs)`: a World stamp and a Wall Unix-ms argument, separate parameters), `PlayerbotAreaTriggers` near 270 and 296
(`TeleportRefusal(..., session.World.NowMs)`, a World stamp), `PlayerbotBrain` near 1517 and `PlayerbotBrain.Contention` near 414, `PlayerbotRecovery` near 1240 and
1358, `PlayerbotMotion` near 716 (World `NowMs` handed to path planning, a signal or a send), `PlayerbotCombatSpells` near 545 (`_itemBlockedUntil - World.NowMs`,
World deadline), `PlayerbotElevatorSnapshot` near 32 (`world.NowMs - trip.StageMs`, handed to Y22), `PlayerbotCommands` near 183 (`memory.NowMs`, the Wall unix-ms
clock of `PlayerbotSocialMemory`, handed to a display helper), and the social lanes (`pass.NowMs`, Social accumulator: `PlayerbotConversations`,
`PlayerbotGratitude`, `PlayerbotGuildChat`, `PlayerbotGuildFounding`). No stamp meets another family. A scan for a line ending in `=` alone finds nothing in either
project. The delegates are covered by the grep named above the delegate table.

The scan cannot see a parameter's source or a delegate invocation. So the following checks were done by reading:

1. **Fields assigned from more than one family.** The scan reports a field name assigned in one file from two families. There were 15; ten were the
   local name `now` in different methods, and the rest (`StageMs`, `NextPlanMs`, `StateMs`, `LastDriveMs`, `StepSinceMs`, `_dropNextMs`, `AnchorMs`,
   `FollowedMs`, `RenewedMs`, `SeenMs`) are a constructor or method parameter in one place and `session.World.NowMs` in another. Every caller was read:
   each hands a World reading or a local of one. None mixes families.
2. **Every file holding two or more families** (25 files with a World and a Wall read; 12 with both Uptime and World; the files that hold an accumulator and
   World, such as `CreatureMapSystem` with `_clockMs` and `_serverTime`, and `ManagedPlayerbotFeature` with `_clockMs`, `NowMs`, `LiveNowMs` and Wall, were read
   through the delegate and accumulator tables). The lines were read, not just counted. Mixed-clock subtraction: none. Where both appear in one function they go to different fields (a wall stopwatch for a budget, World for a stamp).
3. **The four Uptime wrappers.** Every helper that receives their `Now` was checked for a second caller on another family. `RivalryOptOut.Handle` and
   `InviteOptOut.Handle` (shared across bots, `_answered` stamps) have one caller, `PlayerbotPartyAI`. `LfgLookupBudget` has one. The episode, reply,
   follow-drop, unreachable and watch helpers are instances owned by one AI. `PlayerbotBattlegroundBuffs.Update/GiveUp` have one caller.
   `PlayerbotGroupAI.NoteStuck` is static and called twice, both with `Now`. `PlayerbotPartyAI` converts to `uint` in two places (near lines 2062 and 2189, `unchecked((uint)Now)`) for its own
   unreachable memory; both are the same Uptime clock, so `Mark` and `Has` see consistent values.
4. **`PlayerbotChatEvents`.** Its events are stamped on the owner's `Observe` clock (PartyAI, Uptime). Callers on the social accumulator use `NoteNow` and
   `LatestNow`, which read `LastNowMs`, so no event is stamped or aged on the wrong clock. The `_talkedAbout` stamps in PlayerbotConversations are `ev.AtMs`.
5. **The two board clocks.** Brains hand the boards no stamp of their own. `PlayerbotWorldEventGoals` reads `board.NowMs`; the brain's defense code asks
   `found.IsOpen(board.NowMs)` and keeps its own World stamps (`_defenseSince`, `_defenseReportAtMs`) for itself.
6. **`PlayerbotRecovery`.** `_progressMs`, `_closingMs` and the three keep-body stamps are all Uptime, set and compared through `now` read from `Uptime` at
   the top of each method. The few `session.World.NowMs` reads in the file are path-plan arguments and `Relocate` stamps, not mixed into those fields.
7. **The wall reads in the 25 files** (`Environment.TickCount64` in `PlayerbotShip`'s dock cache, `PlayerbotMood._now`, `PlayerbotSocialFeature` lane
   fault gap, `PlayerbotNavigation` wall budget, `ScenarioContext._wall`, `PlayerbotChaosEngine` movement payloads). Each pairs a wall stamp with a wall
   comparison in the same method or type.

## Result per field family

World clock stamps. In each case the stamp and the comparison read the same source, and a zero gap inside the tick that set the stamp means what the code
wants (a spacing gap, a bound, a retry time). No mixed-clock comparison was found in any of these:

| Field | File | Source of stamp and comparison |
|---|---|---|
| `_stepEndedMs` | PlayerbotKite.cs | `session.World.NowMs`; compared with the `now` of the same think |
| `_nextVisitAt` | PlayerbotEconomy.cs | `now` from `NowMs`; the test and inspection seam is a separate setter; compared by `Reached(now, ...)` |
| `_fallStartMs` | PlayerbotFollower.cs | `_session.World.NowMs`; the fall time bound (zero at the start is intended) |
| `_wanderCellLastMs` | PlayerbotBrain.cs | `_session.World.NowMs` |
| `_startedMs` | PlayerbotRetreat.cs | `session.World.NowMs` |
| `_followedFrom` (Uptime, not World) | ScenarioClock.cs | `world.Uptime` both sides |
| `_lootStartedMs` (Uptime, not World) | PlayerbotBrain.cs | Uptime stamp, compared against Uptime in the same class |
| `_lastUpdateMs` | SpellFeature.cs | `world.NowMs` at attach and each update; compared as `diff` between ticks against `System.NowMs`, which is `world.NowMs` through the delegate given to `SpellSystem` |

Other stamps read call by call and classified as World, with the comparison on the same source: `_errand`, `_fightSinceMs`, `_retreatEndedMs`, `_retreatDiedMs`, TeleportService `StartedMs` and `StageSinceMs` (`_world.NowMs - pending.StartedMs`; a teleport
request and its timeout check are on different ticks and the subtraction is non-negative), Knockback `LastTeleportOrKnockbackMs`, SanctuaryEffect, PlayerbotMotion
`RenewedMs`/`AnchorMs`/`FollowedMs` (World `now` from `TryAdvance` and `Pump`; `ManagedPlayerbotFeature` passes the tick-start `gameNow`, see below),
PlayerbotPortalBoard `StateMs`, PlayerbotGroupAI `AnswerRequestedMs`, DebugPathFollow `LastCheckMs`, MovementHandlers `LastNonFiniteReportServerMs`, and the
deadlines `NowMs + delta` in PlayerbotBrain, PlayerbotBrain.Defense, PlayerbotBrain.Help, PlayerbotCombatSpells, PlayerbotQuestGoals, PlayerbotTownGoals,
PlayerbotGroupAI, PlayerbotPartyAI and ManagedPlayerbotFeature (each a wrapped `unchecked` add compared with a signed `unchecked((int)(deadline - now))`).

Two sites that are reads, not stamps: `PlayerbotGroupAI` `Now - _lootOpenedMs` (the `Now` read at `_lootOpenedMs = Now`, set when the loot window opened, is the stamp; the
later `Now - _lootOpenedMs < 5_000` is a read, both Uptime) and `PlayerbotBattlegroundCoordinator` `InPhaseMs = unchecked(Now - t.PhaseSinceMs)` (a read in
the facts builder; the stamps are `PhaseSinceMs = Now`, `QueuedSinceMs = Now`, `LeaveSentMs = Now`, all World).

### LiveNowMs

`git grep -n LiveNowMs -- 'src/*.cs'` finds the four consumers in `ManagedPlayerbotFeature.cs` and the definition in `WorldClock.cs`:

| Read | Use |
|---|---|
| tick entry (`uint now = world.Clock.LiveNowMs;` beside `uint gameNow = world.NowMs;`) | `frame.Now`, the starvation and think-gap clock; `gameNow` is the tick-start game time every movement and think stamp carries. The comment there states the two are never subtracted |
| after the map-think thinks (`frame.Now = world.Clock.LiveNowMs`) | refreshes `frame.Now` so starvation reports compare with a time after the thinks |
| `ThinkCore` (`uint thinkNowMs = world.Clock.LiveNowMs;`) | read once per think, used for `sinceLast = thinkNowMs - active.LastUpdateMs` and stored into `LastUpdateMs` |
| `ActiveBot.LastUpdateMs` field initialiser | the seed of the same field |

`thinkNowMs` is therefore Live, the same family as `LastUpdateMs` and `frame.Now`. Every `LastUpdateMs` write and read sits on `LiveNowMs`; none uses `NowMs`.

Only the map-think path refreshes `frame.Now` after the thinks. The serial path passes the pre-think `frame.Now` to `ReportStarvedBots`, so bots that thought this
tick have `LastUpdateMs` later than `now`. The underflow is guarded in `ReportStarvedBots` and `PlayerbotThinkStarvation.ShouldReport` (`silent > int.MaxValue`
returns false), so it is harmless there.

`Classify` computes `unchecked(now - bot.LastUpdateMs)` with the pre-think `frame.Now` and has no such guard. An underflow would wrap and choose the Priority lane.
Within one pass a bot is not classified after its own think: `Think` has one call site (inside `Visit`), `TickLanes` classifies each index once, before the `Visit` of
that index, and its main lane does not call `Classify`. The serial path and the map path (then `PlayerbotThinkScheduler`) both reach it through `TickLanes`, with
`frame.Now` read after the bot list was built.

Not proved: that a map key is served at most once in one tick. `ThinkMap` sets `group.Thought` but does not test it, and `CollectUnthought` only keeps the
fallback from repeating a served map. A second pass over the same key would classify bots that had thought in the first one, against the same `frame.Now`.
This one is handed to lane Y22, with the other points below.

### The social accumulator

Every `pass.NowMs` stamp in the social lanes (PlayerbotNeedsVoice, PlayerbotGuildChat, PlayerbotMood, PlayerbotSocialEmotes, PlayerbotGuildFounding, PlayerbotRivalry,
PlayerbotSocialFeature registry stamps, PlayerbotSocialInitiative, PlayerbotGratitude, PlayerbotHelpLane `LookAround`, PlayerbotEventLane) is on `_clockMs`, which
advances once per pass (`_clockMs += elapsedMs`), and the two pass constructions hand it in. It is not `WorldClock.NowMs` or `Uptime`, and the "tick-start reading"
statement above does not apply to it: two stamps in one pass are equal because the accumulator moves once per pass. Each field was read for sign and for the meaning
of a zero gap; each compares with `pass.NowMs` of a later pass. `PlayerbotNeedsVoice` `StateOf` seeds `TradeMs`/`LfgMs`/`QuestHelpMs` as `nowMs - random` on that same
clock, and `SeenMs` is stamped from `pass.NowMs` at all four sites.

### The named misses

- `ManagedPlayerbotFeature` `Volatile.Write(ref _lastProvisionSweepMs, now)` (`TakeProvisionSweepSlot` and `SweepStaleProvisionsAsync`). Wall (`Environment.TickCount64`),
  stamp and comparison both wall, nothing to do with the world clock. It held a narrow read-then-act gap: `SweepStaleProvisionsAsync` read `_lastProvisionSweepMs`
  and checked the interval before `_operations.WaitAsync(0)`. A create still holding the lock makes `WaitAsync(0)` fail and the sweep returns, so the sweep is only
  affected if a whole create takes the slot (`TakeProvisionSweepSlot`) and releases the lock between those two statements. The sweep would then write and run a
  second time inside one interval. This is cheap hardening, not an observed double run: the interval is now checked again under the lock. The window is two adjacent statements, so no
  deterministic test reproduces it; the existing `ManagedPlayerbotProvisionSweepTests` still pin the once-per-interval behaviour, which the recheck does not change.
- `MovementAntiCheat._lastSeenMs = receivedMs` (and `now`, `_lastReceived`). Receive stamp family: `session.CurrentPacketReceivedMs`, set from the gateway stamp
  (`GatewayClientConnection`, `Clock.Milliseconds`) or `ManagedWorldSession`'s receive clock. Every stamp and every comparison in `MovementAntiCheat` and
  `SpeedHackDetector` is a packet stamp against a packet stamp; no `NowMs`, `Uptime` or accumulator reaches it. `SupportedSkip` reads `_lastSeenMs` from the previous
  packet and takes the new packet's `receivedMs` as an argument; it is not a double read.
- `Creature._evadeWindowStartMs = clockMs` (Creature.Anomalies). `clockMs` is the `CreatureMapSystem._clockMs` map accumulator at all three call sites
  (`RecordEvadeForAnomaly`, `RecordLeashResetForAnomaly`, `ShouldSampleUnderMap` in CreatureMapSystem.Anomalies.cs). The creature stays on one map, so its stamps and
  comparisons share the accumulator; `-1` and `0` are the unset sentinels.
- `GameObjectMapSystem._nextDespawnAtMs = at`. `at = _clockMs + despawnAfterSeconds * 1000L` on the map's own accumulator (`_clockMs += diffMs`); the compare in
  `Update` reads the same `_clockMs`. The `:125-127` citation in the task does not hold a clock read on this tree (it is the spawn grid list code); the clock fields are
  the `_clockMs`, `_despawnAt` and `_nextDespawnAtMs` declarations near the top of the file.

### Injected-delegate wrappers

Traced where the source is in these two projects (table above): `SpellSystem` (World in production), the two boards (Social accumulator, World fallback), SocialMemory
(Wall), the `Clock` seams of PlayerbotOpportunities and PlayerbotWorldPvpObjectives (test only). `PlayerbotElevatorSnapshot` subtracts stamps from `world.NowMs`; the
stamps arrive as `now` parameters from the elevator and are all World at the call sites read. The `BattlegroundManager` accumulator and the two sites that read it twice
in one expression, and the proof that each injected clock in the battleground and defense lanes is served once per tick, go to lane Y22 below, with the
`ThinkMap` question.

## Handed to lane Y22

The points this pass read but did not prove: a map key served at most once per tick (`ThinkMap`); the `BattlegroundManager` double read against `NowMs += diffMs` (which
thread runs it); `PlayerbotElevatorSnapshot` stamps; `PlayerbotBattlegroundAI.Now`; `PlayerbotDefenseBoard.NowMs`; `SpellSystem.NowMs` as an injected delegate. None of
these is a mixed-clock comparison on a reading of the code; the open question is only about thread and call frequency.

## Not covered

- The projects outside `ArcaneCore.Playerbots` and `ArcaneCore.Game` (World, Data, Gateway, Kernel) except the playerbot reads of them named above and the construction
  sites of the delegates in the delegate table (`NpcServicesFeature`, `ProgressionFeature`, `CreatureWorldFeature`, `DuelFeature`, `WorldEventRuleFeature`,
  `InstanceFeature` and the like).
- Assignments split over lines are covered only by the split-line scan, for the tokens and line endings it names; a clock reached through a longer chain of
  continuation lines would not be seen. Delegates are covered only by the grep above the delegate table: a delegate typed other than `Func<long|uint|ulong|int>`
  (for example `Func<long, ...>`, or an interface such as `IWriteGateClock`) is not.
- Behaviour. Nothing here shows the code runs correctly, only that no stamp was found compared against a different clock family.

Nothing mechanically enforces this audit. Re-run it with the patterns in "Method and tallies" and the delegate grep, group by family as above, and read every field
that appears under two families.
