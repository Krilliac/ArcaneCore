# World clock read audit

This audit covers the clock sources of `src/ArcaneCore.Playerbots` and `src/ArcaneCore.Game` that the greps in "Method and tallies" and the tables below name:
the `Now`-style properties and methods found by the wrapper greps and listed in the wrapper tables, the injected clock delegates found by the name-filtered grep
(`Func<long>`, `Func<uint>`) and the type greps, the accumulators in the accumulator table, and the game-UTC reads, each with its family. What the greps cannot see,
wrappers, conversions and double reads included, is listed under "Not covered". It also covers the
fields assigned from a clock reading, by a scan of single-line and split-line assignments (the patterns are in "Method and tallies"), and the helpers that
receive a reading. It records what each clock is, which sites share a clock, and what it found: one mixed-clock stamp and one double read of a live clock, both
fixed, and the other double reads of a live clock, which do no harm and are listed under "Double reads of a live clock". It is a static reading of the source; nothing here was run against a server. The tests named in this doc were not run
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

Every clock source in the two projects that the scans, the delegate grep and the tables below name is one of these. The name `NowMs` is reused by several of
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

The `Now`-style properties and wrapper delegates, with their source and family. The table has 51 rows naming 65 wrappers (a row that names several counts each name).
Every row is counted, Wall ones included. The rows come from the wrapper greps in "Method and tallies". The clock delegates held as fields or constructor parameters are in the next two tables.

| Wrapper | Source |
|---|---|
| `PlayerbotGroupAI.Now` | Uptime |
| `PlayerbotBattlegroundAI.Now` | Uptime |
| `PlayerbotFollower.Now` | Uptime plus `ClockOffsetMs` (a test seam, 0 in production) |
| `PlayerbotPartyAI.Now` | Uptime plus `ClockOffsetMs` (same seam; it also sets the follower's) |
| `PlayerbotBattlegroundCoordinator.Now` | World (`_world?.NowMs ?? 0`) |
| `PlayerbotGroupCoordinator.Now` | World (`_world?.NowMs ?? 0`) |
| `EventScenarioHost.NowMs`, `ManagedEventScenarioHost.NowMs` | World (the interface and its production implementation, `world.NowMs`) |
| `PlayerbotOpportunities.Now` | `Clock?.Invoke()` test seam, else World |
| `PlayerbotWorldPvpObjectives.Now` | `Clock?.Invoke()` test seam, else World |
| `SpellSystem.NowMs` | a delegate; the only production constructor is `SpellFeature` (`() => _world?.NowMs ?? 0`), so World. Tests pass their own |
| `PlayerbotDefenseBoard.NowMs`, `PlayerbotWorldEventBoard.NowMs` | a delegate; `ManagedPlayerbotFeature.EventClockMs` gives both the Social accumulator (`social.ClockMs`), or World when the social feature is not registered. The service is a singleton, so one process uses one of the two for good |
| `PlayerbotSocialMemory.NowMs` | a delegate; `ManagedPlayerbotFeature` passes `_setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()`, so Wall (unix ms) |
| `PlayerbotChat.NowMs` | Wall (unix ms, `TimeProvider`) |
| `PlayerbotWorldClock.UptimeMs` and `Ms(world, nowMs)` | Uptime; `Ms` places a wrapped `NowMs` on the Uptime axis relative to the present (the defer ledger's bridge between the two) |
| `PlayerbotSocialPass.NowMs` | Social accumulator |
| `PlayerbotPopulationFeature.NowUnix()` | Game UTC (`DateTimeOffset.UtcNow` Unix seconds); every use is a persisted `UpdatedUnix` or a veto/trouble window on Unix seconds, never `NowMs` |
| `CreatureRuleRunner.Clock` | Map accumulator of the runner (`_clock`, see the accumulator table). No reader of this property was found in `src` or `tests`; the runner itself uses the `_clock` field |
| `CreatureRuleRunner.FrozenNow` | the same `_clock`: `_inBatch ? _clock : _clock - _diffMs`. Two readings of one accumulator, by design (see "CreatureRuleRunner timers") |
| `RuleActionContext.NowMs` | the `nowMs` the evaluator was handed: `CreatureRuleRunner._clock` on the creature path (`_evaluator.Update/Fire(..., _clock, ...)`) |
| `EventAiContext.ClockMs` | `System?.ClockMs`: the `CreatureMapSystem._clockMs` map accumulator (0 outside a map) |
| `GameEventService.Now` | `_utcNow()`, the injected UTC delegate: Game UTC (`DateTimeOffset`) |
| `IGameTime.UtcNow` | Game UTC; implemented by the world-state time service and test fakes |
| `PlayerbotQaFeature.Now` / `UtcNow` | Wall (`TimeProvider.GetUtcNow`, `Time` test seam); passes, age-out and views all use it |
| `ISchedulerClock.NowUs` | `SimClock` (a manual counter) or the stopwatch clock in `SchedulerClock.cs`: microseconds of the chat inference scheduler only, Wall |
| `FireworksShow.Now` | `Func<DateTime>`, server local time (`DateTime.Now`), a calendar read; tests replace it. Wall family, not compared with any map clock |
| `PlayerbotNavigationAnomalies.NowOf(Player)` | World (`session.World.NowMs`; 0 for a player with no `WorldSession`). The one reader is `ObservePlan`, whose `now` local stamps `GoalWatch.FirstAt` and `LastAt` and is compared with them (`unchecked(now - watch.LastAt)`, the replan-loop span). All are `uint` World; no other family reaches `GoalWatch` |
| `ILootStateCoordinator.UnixNow` | Game UTC. Production is `LootSettlements.UnixNow` (`ArcaneCore.World`): `DateTimeOffset.UtcNow` Unix seconds, a live read that does not follow the tick or the manual clock. `LootService` hands it to `RespawnAtUnix(go, now)`; `GameObjectMapSystem` reads it twice when it rebuilds a consumed chest. See "Cross-family conversions" |
| `CreatureMapSystem.ClockMs`, `GameObjectMapSystem.ClockMs` | Map accumulator (`_clockMs`). The Game and Playerbots readers found by `git grep -nE "\.ClockMs\b"` compare it with a stamp of the same system: `Creature.RespawnAtMs` (`Creature`, `InstanceManager.Binds.SecondsUntilBack`, which uses the creature's own system), `GameObject.CreatedAtMs` (`KurinnaxxSandTrapScript`; every writer sets it from `_clockMs`), the pulses of `DireMaulDreadsteed`, `TrapSystem`, and `PendingRespawnAt` (`PlayerbotTaxi`) |
| `GameObjectMapSystem.ClockSeconds` | Map accumulator in whole seconds (`_clockMs / 1000`). Stamps `ResetAfterSecond` and `RestockAfterSecond`, which are compared with `ClockSeconds` only |
| `InstanceManager.Now` | Game UTC (`_unixNow()`, see the delegate table). Six `long now = Now;` locals (three in `InstanceManager.cs`, three in `InstanceManager.RaidSchedule.cs`) and six direct reads (one in `InstanceManager.Binds.cs`, five in `InstanceManager.Resolver.cs`, each once per expression). Reset times and the enter limiter, Unix seconds on both sides |
| `QuestNpcServices.UnixNow` | Game UTC (`_unixNow()`, see the delegate table); quest `TimerEndUnix` and vendor restock times, Unix seconds on both sides. A live read: `AddQuest` read it twice in one expression and now reads it once (see "Double reads of a live clock") |
| `MapCombat.NowSeconds` | Game UTC (`DeathHooks.For(world).Clock.UnixSeconds`) |
| `GmAuditFeature.NowUnixSeconds` | Game UTC, in `ArcaneCore.World`. The one Playerbots reader, the bot chat mute in `ManagedPlayerbotFeature`, adds a duration and compares with `MutedUntil`, Unix seconds on both sides |
| `PlayerbotWorldEventBoard.UnixSeconds` | Game UTC (`_setAsideTime` Unix seconds from `ManagedPlayerbotFeature`; the same provider the Wall rows read as Unix ms, labelled here by its use as a calendar time in seconds). One reader, `PlayerbotFestivalRules.StaggeredDay`: a calendar day, not a stamp |
| `WorldStateHooks.LocalNow()`, `IGameTime.LocalNow()` | Game UTC (`IGameTime.UtcNow`) moved to a time zone. A calendar read (weather season, the login time, a GM command), never a stamp and never compared with a millisecond clock |
| `WorldClock.UnixSeconds`, `WorldClock.UnixMilliseconds` | Game UTC: `WorldClock.UtcNow`, so tick-start like `NowMs` |
| `DeathClock.UnixSeconds`, `HonorClock.UnixSeconds`, `HonorClock.UnixMilliseconds`, `IRespawnClock.UnixSeconds` | Game UTC (see the families table; `WorldDeathClock` follows world time on the manual clock) |
| `GameTimeProvider.GetUtcNow`, `GameTimeProvider.GetTimestamp` | a `TimeProvider` over `WorldClock`: Game UTC and `ElapsedMs`. The greps found no construction of it in `src`, so no production path was traced through it |
| `ScenarioTimeProvider.GetUtcNow`, `IScenarioClock.Advanced` | scenario time: a start time plus an Uptime delta (and test-added ticks). Scenario code only |
| `LootRollManager.Clock` | Wall (`TimeProvider.System`, settable, a test replaces it). `LootRoll.StartedAt = Clock.GetTimestamp()`, aged by `Clock.GetElapsedTime(roll.StartedAt)` on the same provider; no other family reaches it |
| `BellAi._now` | Wall, a calendar read (`Func<DateTime>`, `DateTime.Now`; production passes no delegate, tests may). Read once for the hour. The toll times use `BellState.ClockMs`, a separate accumulator |
| `PlayerbotSocialFeature.PassClockMs` | Wall (`Func<double>`, `Stopwatch` in milliseconds, a test seam). `start` and the budget check in the pass read the same delegate; it is not the Social accumulator and meets no stamp of it |
| `PlayerbotQaGitHubClient._now` | Wall (`Func<DateTimeOffset>`, `DateTimeOffset.UtcNow` by default). Subtracted from HTTP retry dates only: UTC against UTC |
| `PlayerbotSocialFeature.ClockMs` | Social accumulator |
| `ManagedPlayerbotFeature.ClockMs` | the feature accumulator (see the accumulator table) |
| `ManagedPlayerbotFeature.EventClockMs()` | the board clock: Social accumulator, or World when the social feature is not registered (see the board row above) |
| `PlayerbotPopulationFeature.ClockMs` | Population accumulator |
| `PlayerbotChatEvents.LastNowMs` | the last `now` handed to `Observe`: the owner's clock, Uptime (see check 4 below) |
| `PlayerbotNavigation.RealNow()` | Wall (`RealTicks` seam, else `Stopwatch.GetTimestamp`), the planning budget |
| `PlayerbotThinkProbe.RunningMs`, `PlayerbotThinkProbe.Timestamp()` | Wall (`TimeProvider.GetTimestamp` or `Stopwatch`), think timing only |
| `TickLaneDriver.ElapsedMs()`, `DelegateThinkLaneDriver.ElapsedMs()` | Wall (`Stopwatch.GetElapsedTime(thinkStarted)`; the delegate form takes its clock from the caller of `PlayerbotThinkRotation.TickLanes`). Compared with a millisecond budget only |

### Clock delegates and Game-side wrappers

Each `Func<long>` / `Func<uint>` clock found by
`git grep -nE "Func<(long|uint|ulong|int)>\??\s+_?\w*([Cc]lock|[Nn]ow|[Tt]ime|Ms)\w*" -- src/ArcaneCore.Game src/ArcaneCore.Playerbots`,
traced to the lambda its production constructor receives. "Result" is whether any stamp from it meets a reading of another family.

| Delegate | Source in production | Family | Result |
|---|---|---|---|
| `TaxiFlightSystem._nowMs` | `NpcServicesFeature`: `() => _world?.NowMs ?? 0` | World | only `Relocate(..., _nowMs())` stamps; no comparison |
| `PlayerProgression._nowMs` | `ProgressionFeature`: `() => _world?.NowMs ?? 0` (class default `() => 0`) | World | one use, `ResetLevelPlayedTime(_nowMs())`; no comparison |
| `CreatureMapSystem._serverTime` | `CreatureWorldFeature`, both creation sites: `() => world.NowMs`; default (tests) `unchecked((uint)_clockMs)` | World in production (the truncated map accumulator only in tests that pass no delegate) | 18 call sites, all read: the 16 listed here and the 2 fixed `Relocate` sites at the end of this row. `StopSpline(_clockMs, _serverTime())` (`CreatureMapSystem.cs`) passes the map accumulator (spline time) and the server time as two separate parameters on purpose (`Creature.StopSpline(long clockMs, uint serverTimeMs)`). Two `now` locals (`CreatureMapSystem.cs`, near 990 and 1203) are used against World stamps. Eight `ResetToHome` calls: five in `CreatureMapSystem.Lifecycle.cs` (near 140, 462, 527, 601, 616) and one each in `PetRevival` (22), `RelayCommands` (140) and `SummonedUnits` (40). `moved.Time` and `Relocate` in `PetRevival` (64, 67) and `ScriptedAi` (137, 140). `GuardPosts.TryUse` in `CreatureMapSystem.Guards.cs` (71). All stamp a `Unit` movement `Time` or a home reset from World. `GuardPostTable.TryUse(areaId, team, long nowMs)` takes the wrapped `uint` widened to `long`; its stamps (`LastUseMs`, `RechargeEpoch = nowMs / RechargeMs`) are World values, and the wrap branch (`GuardPostTable.cs` near 156-162, `nowMs < state.LastUseMs`) restarts the post with full charges, so a 49.7-day wrap is a reset and not a stuck cooldown. **Mixed family found and fixed:** the under-map stand-up in `CreatureMapSystem.Anomalies.cs` (near 301 and 349) called `creature.Relocate(..., unchecked((uint)_clockMs))`, stamping the movement block `Time` from the map accumulator while every other `Relocate` and `moved.Time` site used `_serverTime()` (World). Both now pass `_serverTime()`. The one comparison on that field (`StampServerMovementTime`, `MovementCheats.cs`) is player-only, so no creature-side behaviour was found to depend on it; the fix is for consistency |
| `WorldEventRuleRunner._clockMs` | `WorldEventRuleFeature`: `() => _clockMs`, the feature's own `_clockMs += diffMs` | accumulator (`long`) | `now` and the `Fire(..., _clockMs())` stamp read the same accumulator |
| `DuelService.Clock` | `DuelFeature`: `DeathHooks.For(world).Clock.UnixSeconds`, or `TimeProvider.GetUtcNow` seconds when a provider is set | Game UTC | the same delegate is also set as `SpellSystem.UnixSecondsClock`, so aura stamps and the duel start share one clock |
| `GroupAuthority.MillisecondsClock`, `GroupManager.MillisecondsClock` | default `Kernel.Logging.Clock.Milliseconds` (`Environment.TickCount64`) | Wall | online-hint stamps and their age checks both use the delegate, both `long`; no other family reaches them |
| `GroupAuthority.UnixSecondsClock`, `GroupManager.UnixSecondsClock`, `SpellSystem.UnixSecondsClock` | `DateTimeOffset.UtcNow` seconds (`SpellSystem` is overridden by `DuelFeature`) | Game UTC | Unix seconds against Unix seconds |
| `InstanceManager._unixNow` | `InstanceFeature`: `time.GetUtcNow().ToUnixTimeSeconds()` | Game UTC | reset times against Unix seconds |
| `InventoryItemService`, `QuestNpcServices._unixNow`, `TalentService._unixNow`, `PetController.UnixNow`, `LunarFestivalScripts.NowUnix`, `ChatRestrictionService` (`unixNow`) | `DateTimeOffset.UtcNow` / `TimeProvider` Unix seconds (`NpcServicesFeature`, `ChatRestrictionFeature`, class defaults) | Game UTC | persisted or calendar times; none meets `NowMs`. `QuestNpcServices` and `ChatRestrictionService` each held a double read of this live clock (see "Double reads of a live clock") |
| `PerfLogAggregator._nowMicros` | `WorldRuntime.PerfClockMicros`: `Clock.ElapsedMs * 1000` on the manual clock, else the runtime stopwatch | Wall (microseconds) | perf windows only |
| `NavMeshQuery.DeadlineClock`, `TickStats.Clock` | `Stopwatch.GetTimestamp` | Wall | budgets and tick timing |
| `PlayerbotPathJobs._clockMs` | `ManagedPlayerbotFeature`: `() => unchecked((uint)ClockMs)`, the feature accumulator | feature accumulator (`long`, truncated) | every stamp (`SubmittedAtMs`, `LastAskMs`, `_deadlineReported`, `_lastSummaryMs`) comes from `_clockMs()`; callers hand it no stamp (`WallMs` is a separate stopwatch field) |
| `PlayerbotQaInteractionMonitor._clockMs`, `PlayerbotQaSpellMonitor._clockMs` | default `Environment.TickCount64` | Wall | repeat-suppression stamps against the same delegate |
| `PlayerbotMood._now`, `PlayerbotSocialRecorder._now`, `PlayerbotTradeOwners.Clock` | default `Environment.TickCount64`; `ManagedPlayerbotFeature.TradeDesk` sets the owners' clock to set-aside time Unix ms | Wall | stamp and check on the same delegate |
| `PlayerbotPriceBook`, `PlayerbotTradeDesk`, `PlayerbotTradeGifts`, `PlayerbotTradeLedger`, `PlayerbotCombatTelemetry` (`clockUnixMs`, `unixMs`) | `_setAsideTime.GetUtcNow()` or `TimeProvider.GetUtcNow()` Unix ms | Wall | persisted and trade-window times |
| `PlayerbotGroupCoordinator.UnixNow` | `DateTimeOffset.UtcNow` seconds | Game UTC | compared with respawn-ledger due times in Unix seconds |
| `PlayerbotDefenseBoard._clock`, `PlayerbotWorldEventBoard` clock | `ManagedPlayerbotFeature.EventClockMs` (Social accumulator, else World) | see the wrapper table | see "The two board clocks" |
| `PlayerbotOpportunities.Clock`, `PlayerbotWorldPvpObjectives.Clock` | test seams, null in production | World | see the wrapper table |
| `TimeProvider` holders outside the wrapper table: `PlayerbotChaosEngine`, `InProcessBotChatClient`, `PlayerbotChatSafety`, `PlayerbotAnomalyHistory`, `PlayerbotPolicyEngine`, `PlayerbotPolicyTelemetry`, `PlayerbotQaFirstChanceWatch`, `PlayerbotQaLogTap`, `PlayerbotQaPublisher`, `ItemMaintenanceUpdater`, `ManagedPlayerbotFeature.ResetClock` (`FleetResetConfirmation`), `PlayerbotPopulationFeature.RushClock` | `TimeProvider.System` by default | Wall (`ItemMaintenanceUpdater`: Game UTC) | most reads stay `DateTimeOffset` or `DateTime`, which the compiler will not compare with a `uint` or `long` clock. The ones turned into numbers: `PlayerbotPolicyEngine` (`GetTimestamp`, its own budget), `PlayerbotPolicyTelemetry` (Unix ms kept apart from the `nowMs` parameter: `OpenDecision` has `AtMs` and `UnixMs`, and only `AtMs` is compared with `nowMs`, which is World: `ManagedPlayerbotFeature` passes `thinkWorld.NowMs` and `world.NowMs`), `RushClock` (`wallMs`, the brake windows only), `ItemMaintenanceUpdater` (Unix seconds handed to `ItemMaintenance.Tick`) |

### Accumulators

Fields a system advances by the tick diff. Each is stamped and compared on its own field only.

| Accumulator | Type | Notes |
|---|---|---|
| `CreatureSpellListScheduler._nowMs` | `ulong` (`ItemMaintenance` is `uint`, `_autosaveClockMs` is `ulong`, the rest are `long`) | `_nowMs += diffMs`; `_readyAtMs` entries are `_nowMs + roll`, compared with `_nowMs` only |
| `ItemMaintenance._elapsedMs` | `uint` | an interval timer: `+= diffMs`, reset to 0 at the zone-limit check; never a stamp |
| Interval timers: `ManagedPlayerbotFeature._checkpointMs`, `PlayerbotPopulationFeature._sinceMs`, `PlayerbotSocialFeature._sinceLastPassMs`, `PlayerbotEventScenarioFeature._sinceMs`, `_thinkElapsed` (`PlayerbotBrain`, `PlayerbotGroupAI`, `PlayerbotPartyAI`, `PlayerbotBattlegroundAI`), `PlayerbotCombatAnomalies._sinceMs` | `uint`; `long` for Population and Social | the rule: a field that is only `+= elapsedMs`, compared with a constant interval and reset to 0 is a duration counter, not a clock. It holds no stamp and meets no other family, so it is named here and not read further |
| `WorldRuntime._autosaveClockMs` | `ulong` | sum of tick diffs; the `_autosaveDue` values and the compare in `WorldRuntime.Autosave.cs` use it alone |
| `ManagedPlayerbotFeature._clockMs` | `long` | `Interlocked.Add(ref _clockMs, elapsedMs)`; restored across a restart (`playerbots.clockMs`); feeds `PathJobs`, the quarantine retry times and the slow-think log |
| `WorldEventRuleFeature._clockMs` | `long` | the delegate source above |
| `CreatureRuleRunner._clock` | `long` | `_clock += diffMs` in `Update`; the runner's own timer clock, stamped and compared only on itself (see "CreatureRuleRunner timers") |
| `EventScenarioEngine._now` | `long` | `Advance()` adds `unchecked(host.NowMs - _lastRaw)`: a non-wrapping copy of the World clock that starts at 0; `CreateRetryMs = _now + RetryMs` and its comparison use `_now` only |

The game-UTC reads (for example `Honor/HonorKillRewards.cs` through `HonorClock.UnixMilliseconds`, `Creatures/CreatureMapSystem.RespawnPersistence.cs` through
`IRespawnClock.UnixSeconds`, `MapCombat.NowSeconds`) are in the Game UTC family. Each pairs a Unix-time stamp with a Unix-time comparison (rows persisted with
a Unix due time); none is read against `NowMs`. Where a persisted Unix time has to become a map or World time, it crosses as a remaining duration (next section).

### Cross-family conversions

A stored time outlives the process, but `_clockMs` and `NowMs` restart, so these sites read two families on purpose. Each subtracts within one family and adds the
resulting duration to the other. None compares a stamp of one family with a reading of another. Found by three greps over the two projects, comment lines dropped and every
hit read; each sees one line at a time:

1. A clock token and a Unix token on one line: `git grep -nE "(_clockMs|ClockMs|NowMs|\bnow\b|\bnowMs\b|\buntil\b).*([Uu]nix|_respawnClock|UtcNow)|([Uu]nix|_respawnClock|UtcNow).*(_clockMs|ClockMs|NowMs|\bnow\b|\bnowMs\b|\buntil\b)"`.
2. Arithmetic on a Unix name, whatever is on the other side (this one finds `EndsAtUnixMs - nowUnixMs`): `git grep -nE "(\w*[Uu]nix\w*|\w*[Ee]pochMs\w*)\s*[-+]\s*\(?\w|\w\s*[-+]\s*\w*[Uu]nix\w*"`.
3. A remaining time added to a clock: `git grep -nE "(\b(now|nowMs|Now|NowMs|_clockMs|ClockMs)\b)\s*\+\s*\(?(uint\)|long\))?\(?\w*([Rr]emaining|[Ll]eft)\w*"`.

The hits not in this table stay inside one family (a Unix value against a Unix value, for example `RestService`, `RespecCost`, `AntiCheatScores`, `PopulationZoneVetoes`,
`PlayerbotWorldBoss`), or hand a World and a Unix reading to separate parameters or record fields (`BotController`, `PlayerbotPolicyTelemetry`, `PlayerbotCombatTelemetry`).

| Site | Direction | Result |
|---|---|---|
| `GameObjectMapSystem` durable chest rebuild (`go.RespawnAtMs = _clockMs + Math.Max(1L, consumed.RespawnAtUnix - durable.UnixNow) * 1000L`) | Game UTC to Map accumulator | benign. `RespawnAtUnix - UnixNow` is a remaining duration in one family, added to the map's clock; `RespawnAtMs` is then compared with `_clockMs` only. `long.MaxValue` (never) is tested first and gives 0, so the subtraction cannot overflow on it. `UnixNow` is live and is read twice (the `>` guard and the subtraction), so the second read can be one second later and the difference can reach 0; `Math.Max(1L, ...)` absorbs that. A double read with no bad result, so nothing to fix. Precision is whole seconds |
| `GameObjectMapSystem.RespawnPersistence` save and load, `CreatureMapSystem.RespawnPersistence` save and load, `CreatureMapSystem.SpawnGroups` save | Map accumulator to Game UTC and back | benign, the same shape: save is `now + max(0, RespawnAtMs - _clockMs) / 1000`, load is `_clockMs + (unix - now) * 1000` behind `unix > now`. One `_respawnClock` read per save or load in the local-`now` forms |
| `SpellSystem.CaptureState` and `RestoreCooldowns` (`SpellSystem.Persistence.cs`, the `PersistedCooldown.EndsAtUnixMs` rows). Callers hand `nowUnixMs`, one reading per call: `SpellFeature.UnixNowMs` (`ArcaneCore.World`, save and load) and `DateTimeOffset.UtcNow` in `SummonService.PersistentPets` | World to Game UTC and back | benign, the same shape. Save is `nowUnixMs + (until - now)` with `uint now = NowMs` read once, reached only behind `IsStillRunning(until, now)`, a signed wrap-safe test, so the `uint` difference is the true time left. Load is `remaining = EndsAtUnixMs - nowUnixMs`, skipped at `<= 0`, capped at `MaxReasonableCooldownMs` (seven days, reported as an anomaly), then `until = unchecked(now + (uint)remaining)`. `until` is compared wrap-safely with World readings only |
| `SpellSystem.RestoreCooldowns` called with no Unix time: `DuelResetScript` (`nowUnixMs: 0`, the time left passed as the end) | World to World | benign. `Left(end, now)` is a duration on World, and `EndsAtUnixMs - 0` hands it back; no Unix reading is involved despite the names |
| `SpellSystem.CaptureState` and `RestoreAurasCore`, aura rows | none crosses | an aura stores a duration (`RemainingMs`) and `SavedAtUnixMs`. `offline = max(0, nowUnixMs - SavedAtUnixMs)` is Unix against Unix and shortens the duration; no World stamp is stored |
| `CreatureMapSystem.Pools` (`_respawnAt = _clockMs + delayMs`, then `_respawnClock.UnixSeconds + delayMs / 1000`) and `CreatureMapSystem.Removal` (`UnixSeconds + RemovedRespawnHorizonSeconds`; `IsRemovalRow` compares a Unix row with `UnixSeconds`) | one duration added to each family | benign. Nothing is subtracted across families |
| `WorldDeathClock.UnixSeconds`, manual clock only | Uptime to Game UTC | benign. `anchor.UnixSeconds + (Uptime - anchor.UptimeMs) / 1000`: the difference is on Uptime and is added to the anchored Unix seconds. The real clock returns `DateTimeOffset.UtcNow` |
| `PlayerbotStallWatch` set-aside ledgers, `Export` and `Restore` (`ManagedPlayerbotFeature` hands `World.NowMs` and the Unix ms) | Uptime axis to Wall Unix ms and back | benign, the same shape: `unixNowMs + Remaining(until, now)` or `unixNowMs + record.RemainingMs` on export, `now + (ExpiresUnixMs - unixNowMs)` on restore (the per-bot form goes through `PlayerbotDeferLedger.RestoreEntry`, `UntilMs = now + remainingMs`). `T(nowMs)` puts the World reading on the Uptime axis through `PlayerbotWorldClock.Ms` |

### Double reads of a live clock

A clock that moves between two reads can make one expression disagree with itself; a tick-start clock cannot. So these two scans took the live sources only (`UnixNow`, `NowUnix()`, `unixNow()`,
`_unixNow()`, `UnixSecondsClock()`, `DateTimeOffset.UtcNow`, `DateTime.UtcNow`, `GetUtcNow()`, `TickCount64`, `_now()`, `Clock.Milliseconds()`, `_respawnClock.UnixSeconds`, `Clock.UnixSeconds`,
`Clock.UnixMilliseconds`, `MillisecondsClock()`, `UnixNowMs`): two reads on one line, and two reads in one file no more than four lines apart. Whole comment lines were dropped; a trailing comment was kept. On the tree this doc was committed in, the scans
give 6 one-line hits and 5 pairs, and each one is accounted for here. A hit only means two tokens sit close together, so it can be something other than two reads feeding one computation; the table records what each one turned out to be.

The 6 one-line hits: four are a wrapper's own declaration, where the name and the clock in its body are the two tokens, and are not double reads (`QuestNpcServices.UnixNow`, `PetController.UnixNow`, `PlayerbotGroupCoordinator.UnixNow`, `PlayerbotPopulationFeature.NowUnix`).
One is `ChatRestrictionService.MuteRemaining`, in the table. One is the `AddQuest` line after its fix, `long nowUnix = UnixNow;`, whose second token is in the trailing comment; before the fix the expression below was the hit.
The 5 pairs: the `GameObjectMapSystem` chest rebuild (`GameObjectMapSystem.cs:1879/1883`), `InProcessBotChatClient.Overran` (`:199/202`), `ManagedPlayerbotFeature.CreateSocial` (`:298/299`), `PlayerbotPolicyTelemetry` (`:313/315`) and the zone value pass of `PlayerbotPopulationFeature` (`:2515/2519`), all in the table.
`PenaliseGhost` is not a scan hit; it came from reading the `RestoreCooldowns` callers.

| Site | Clock | Result |
|---|---|---|
| `QuestNpcServices.AddQuest`, shared timed quest | `UnixNow`, a fresh `_unixNow()` per read (production: `QuestNpcFeature`, `_clock.GetUtcNow()` seconds) | **Real, fixed.** `checked(UnixNow + Math.Max(0, shared - UnixNow))` read the clock twice. A second boundary between the reads put `TimerEndUnix` one second before the sharer's end, and no `Max` absorbs that. It now reads one `nowUnix` local, also used by the unshared arm. `QuestShareTests.ASharedTimedQuest_EndsWithTheSharer_WhenTheClockMovesBetweenReads` covers it (a clock that steps on every read); not run when this was written |
| `GameObjectMapSystem` durable chest rebuild | `ILootStateCoordinator.UnixNow`, live | harmless; `Math.Max(1L, ...)` absorbs it (see "Cross-family conversions"). Not changed |
| `ChatRestrictionService.MuteRemaining` | `unixNow()`, live | harmless. `until > unixNow() ? until - unixNow() : 0`: a later second read gives one second less, and a result of 0 or below means the mute has ended at that read. Every caller tests `> 0`. Not changed |
| `UnstuckCommands.PenaliseGhost` (`ArcaneCore.World`) | `DateTimeOffset.UtcNow`, twice in one call | harmless. The end and `nowUnixMs` are separate reads, so the one-hour cooldown can start short by the gap between them. Not changed |
| `PlayerbotPolicyTelemetry`, the wait loop on `Environment.TickCount64` | Wall | intended: a deadline stamp and a poll against it |
| `InProcessBotChatClient.Overran` | `_time.GetUtcNow()`, Wall, live | harmless. Two statements under `_gate`: the first tests whether the circuit is still open and returns if so, the second stamps a new `_circuitOpenUntil`. The second is only reached when the circuit was closed at the first, and it does not use the first reading. The lock does not stop the clock, but the only effect of a gap between the reads is that the circuit stays open that much longer. Not changed |
| `ManagedPlayerbotFeature.CreateSocial` | `_setAsideTime.GetUtcNow()`, Wall (Unix ms), in two lambdas | not a read here. Both lines build a delegate, one for `PlayerbotSocialMemory` and one for `PlayerbotTradeLedger`, and neither runs at this site. Each object reads its own delegate later. Not changed |
| `PlayerbotPopulationFeature`, the zone value pass (`troubleOf`, `stayedSince`) | `NowUnix()`, Game UTC, live | not two reads of one computation. `troubleOf` is a lambda that reads the clock each time it is called, for a count in the trouble window. `stayedSince` is one read, a cutoff compared with `UpdatedUnix`. Neither value is used with the other, so a second boundary between them can only move a window edge by one second. Not changed |

Two reads of a tick-start clock in one expression are equal once ticks run, so they are not listed as faults: `PlayerbotGroupCoordinator` set-aside remaining (`until - Now` twice), `PlayerbotBattlegroundCoordinator`
`PhaseSinceMs = Now, QueuedSinceMs = Now`, `PlayerbotElevatorSnapshot`, and the social lanes' `pass.NowMs`. `PlayerbotGroupAI` reads `Now` (Uptime, against `_resurrectAtMs`) and `World.NowMs` (World, into
`InFight`, against `LastCombatMs`) on one line, each against its own family. The `BattlegroundManager` lines read an accumulator and go to lane Y22.

`BattlegroundManager.NowMs` is the Battleground accumulator family above and is handed to lane Y22 (see "Handed to lane Y22"). Outside the two projects and not read: `SqliteWriteGate._clock.NowMs` (`IWriteGateClock`,
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

That scan predates the last wrapper rows. The wrapper table rows added after it (from `NowOf` down, after `FireworksShow.Now`) were read by hand, so their sites are not in the counts above.

The wrappers were found by three greps over the two projects, then read:

1. Name: a declaration with a modifier whose name holds `Now`, `Clock`, `Unix`, `UtcNow` or `Uptime`.
2. Interface and unmodified members:
   `^\s*(long|uint|ulong|int|double|DateTimeOffset|DateTime|TimeSpan)\??\s+\w*(Now|Clock|Unix|Utc|Uptime|Time|Ticks)\w*\s*(\{|\(\s*\)|=>)`.
3. Shape, whatever the name: an expression-bodied member whose body holds `NowMs`, `LiveNowMs`, `Uptime`, `ElapsedMs`, `UtcNow`, `GetUtcNow`, `GetTimestamp`, `TickCount`,
   `UnixSeconds`, `UnixMilliseconds`, `ToUnixTime`, `Clock.Milliseconds`, `_clock`, `_clockMs`, `_now()`, `_nowMs()`, `_unixNow` or `DateTime.Now`. Members in that list that
   only pass a reading to something else (`IsEntrySetAside`, `SetAside`, `Resting`) use a clock and do not wrap one, so they are not rows.

Two type greps cover what those miss: `\bTimeProvider\??\s+_?\w+` and `Func<(double|DateTime|DateTimeOffset|TimeSpan|float)\??>\??\s+_?\w+`. A block-bodied check, a `return` or `get`
whose whole expression is a clock read, found only `WorldDeathClock` and the `PlayerbotPopulationFeature.ClockMs` getter, both in the table.

Patterns used by the scan. A clock read is a match of
`\b(NowMs|LiveNowMs|Uptime|Clock\.Milliseconds|TickCount64?|UnixSeconds|UnixMilliseconds|GetUtcNow|UtcNow|Stopwatch)\b`, or one of the wrapper names in the first table as it stood then, on the
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
   each hands a World reading or a local of one. One stamp did: the `Relocate` call in the `_serverTime` row, fixed. This list covers assignments to named fields; method arguments such as `Relocate` were found through the delegate table, not this scan.
2. **Every file holding two or more families** (25 files with a World and a Wall read; 12 with both Uptime and World; the files that hold an accumulator and
   World, such as `CreatureMapSystem` with `_clockMs` and `_serverTime`, and `ManagedPlayerbotFeature` with `_clockMs`, `NowMs`, `LiveNowMs` and Wall, were read
   through the delegate and accumulator tables). The lines were read, not just counted. Mixed-clock subtraction: none, other than the `Relocate` stamp fixed in the `_serverTime` row. Where both appear in one function they go to different fields (a wall stopwatch for a budget, World for a stamp).
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

### CreatureRuleRunner timers

`CreatureRuleRunner._clock` is the sum of the `diffMs` the runner saw. `_diffMs` is the time accumulated since the last batch, and `FrozenNow` is
`_inBatch ? _clock : _clock - _diffMs`: the clock as of the last batch, so a timer stamped between batches lands on the batch boundary (cmangos decrements timers
only at a batch). `_timerUntil` is stamped from `FrozenNow` (`UpdateRepeatTimer`) and compared with `_clock` (the batch loop), `_clock - _diffMs` (the
phase-hidden shift) and `FrozenNow` (`CheckEvent`). All of these are readings of the one `_clock` accumulator, so no family is mixed, and `_clock` and `FrozenNow`
differ only by the intended accumulated `_diffMs`. The evaluator receives raw `_clock` (`_evaluator.Update`, `Fire`) for its own delayed actions, which it stamps and
compares against the same value; `_timerUntil` never reaches it. No bug found. `EventAiContext.ClockMs` is the map's accumulator, a different one, and is not
compared with `_timerUntil`.

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
- Delegate names. The delegate grep filters on a name containing `clock`, `now`, `time` or `ms`, so a clock delegate with another name is not found. Known ones
  read by hand: `PlayerbotNavigation.RealTicks` and `PlayerbotOpportunities.ChestWaitRealTicks` (`Func<long>`, Stopwatch ticks for a wall budget, default
  `Stopwatch.GetTimestamp`, so Wall) and the `unixSeconds` parameter of `PlayerbotWorldEventBoard` (Unix seconds, Game UTC). Others may exist.
- Wrappers. The wrapper table is what the greps in "Method and tallies" found. A wrapper is not seen if its name has none of the name tokens and its body is a block that does more
  than return a clock read, or if its expression body spans lines or reaches the clock through a member not in the shape grep's token list. Others of that kind may exist.
- Conversions. The three conversion greps see one line at a time and only the tokens they name. A conversion is not seen if its Unix value sits in a name with no `Unix` in it (for example a local
  `end` or a field `ExpiresAt`) and its other side is not a `remaining` or `left` name, or if the two halves are on different lines with neither line matching. Others of that kind may exist.
- Double reads. The two live-clock scans see the source names listed in "Double reads of a live clock", on one line or within four lines. Two reads further apart in one method, reads through a
  live wrapper under another name, and a wrapper called from a helper in the same expression are not seen.
- Interval timers beyond those named in the accumulator table, and any accumulator not found by the greps, are not claimed.
- Behaviour. Nothing here shows the code runs correctly, only that no stamp was found compared against a different clock family.

Nothing mechanically enforces this audit. Re-run it with the patterns in "Method and tallies" and the delegate grep, group by family as above, and read every field
that appears under two families.
