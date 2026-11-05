# World clock read audit

This audit covers every clock read under `src/ArcaneCore.Playerbots` and `src/ArcaneCore.Game`: every field and property assigned from a clock, every
`Now`-style wrapper, and every call that hands a clock reading to a helper. It records what each clock is, which sites share a clock, and the one
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

Every reading in the two projects comes from one of these. The name `NowMs` is reused by several of them, so a name is never proof of the family;
the source is.

| Family | Source | Type | Where |
|---|---|---|---|
| World | `WorldRuntime.NowMs` / `Clock.NowMs` (tick start) | `uint`, wraps | most of the playerbot brain, Game systems that hold a `WorldRuntime` |
| Uptime | `WorldRuntime.Uptime.TotalMilliseconds` (tick start) | `long` | the four `Now` wrappers below, PlayerbotRecovery, PlayerbotBrain loot stamp, the defer ledger, ScenarioClock |
| Live | `WorldClock.LiveNowMs` | `uint`, wraps | ManagedPlayerbotFeature think-gap and starvation code only |
| Map accumulator | a system's own `_clockMs += diffMs` | `long` | CreatureMapSystem, GameObjectMapSystem, GridContainer, FishingService, FireworksShow, HolidayObjectScripts, RegtharDeathgateAI |
| Social accumulator | `PlayerbotSocialFeature._clockMs += elapsedMs` (`PlayerbotSocialPass.NowMs`, `ClockMs`) | `long` | every playerbot social lane; also the event board and defense board clock |
| Population accumulator | `PlayerbotPopulationFeature._clockMs += elapsedMs` | `long` | population feature only |
| Battleground accumulator | `BattlegroundManager.NowMs += diffMs` | `uint` | BattlegroundManager and BattlegroundQueue |
| Receive stamp | `Clock.Milliseconds()` at the gateway or at `ManagedWorldSession` | `uint` | `MovementSample.ReceivedMs`, `MovementAntiCheat`, `SpeedHackDetector`, `LocomotionState` reject window |
| Wall | `Environment.TickCount(64)`, `Stopwatch`, `DateTimeOffset.UtcNow`, `TimeProvider.GetUtcNow` | `long` | rate limits, persistence rows, budgets, chat limiter, logs |

## Wrappers

Every property or delegate in the two projects (and the playerbot reads in `ArcaneCore.World` that call them) that returns a clock reading, with its
source. 14 world-related wrappers plus the social pass, listed by name:

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

Outside the question and not read as world coverage: `BattlegroundManager.NowMs` (accumulator), `SqliteWriteGate._clock.NowMs` (`IWriteGateClock`,
a stopwatch), `DebugDrawFeature.Now` and `EconomyFeature`-style `Now` properties (world and wall respectively, in `ArcaneCore.World`).

## Method and tallies

A one-off scan over the two projects (not kept in the repo) took every single-line assignment whose right side holds a clock read, and classified the
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

The scan cannot see a parameter's source, a statement split over lines, or a delegate. So the following checks were done by reading:

1. **Fields assigned from more than one family.** The scan reports a field name assigned in one file from two families. There were 15; ten were the
   local name `now` in different methods, and the rest (`StageMs`, `NextPlanMs`, `StateMs`, `LastDriveMs`, `StepSinceMs`, `_dropNextMs`, `AnchorMs`,
   `FollowedMs`, `RenewedMs`, `SeenMs`) are a constructor or method parameter in one place and `session.World.NowMs` in another. Every caller was read:
   each hands a World reading or a local of one. None mixes families.
2. **Every file holding two or more families** (25 files with a World and a Wall read; 12 with both Uptime and World). The lines were read, not just
   counted. Mixed-clock subtraction: none. Where both appear in one function they go to different fields (a wall stopwatch for a budget, World for a stamp).
3. **The four Uptime wrappers.** Every helper that receives their `Now` was checked for a second caller on another family. `RivalryOptOut.Handle` and
   `InviteOptOut.Handle` (shared across bots, `_answered` stamps) have one caller, `PlayerbotPartyAI`. `LfgLookupBudget` has one. The episode, reply,
   follow-drop, unreachable and watch helpers are instances owned by one AI. `PlayerbotBattlegroundBuffs.Update/GiveUp` have one caller.
   `PlayerbotGroupAI.NoteStuck` is static and called twice, both with `Now`. `PlayerbotPartyAI` converts to `uint` once (`unchecked((uint)Now)`) for its own
   unreachable memory, and both its `Mark` and `Has` take that value.
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
| `_followedFrom` | ScenarioClock.cs | `world.Uptime` both sides |
| `_lastUpdateMs` | SpellFeature.cs | `world.NowMs` at attach and each update; compared as `diff` between ticks against `System.NowMs`, which is `world.NowMs` through the delegate given to `SpellSystem` |

Other stamps read call by call and classified as World, with the comparison on the same source: `_lootStartedMs` (Uptime, set and compared in PlayerbotBrain),
`_errand`, `_fightSinceMs`, `_retreatEndedMs`, `_retreatDiedMs`, TeleportService `StartedMs` and `StageSinceMs` (`_world.NowMs - pending.StartedMs`; a teleport
request and its timeout check are on different ticks and the subtraction is non-negative), Knockback `LastTeleportOrKnockbackMs`, SanctuaryEffect, PlayerbotMotion
`RenewedMs`/`AnchorMs`/`FollowedMs` (World `now` from `TryAdvance` and `Pump`; `ManagedPlayerbotFeature` passes the tick-start `gameNow`, see below),
PlayerbotPortalBoard `StateMs`, PlayerbotGroupAI `AnswerRequestedMs`, DebugPathFollow `LastCheckMs`, MovementHandlers `LastNonFiniteReportServerMs`, and the
deadlines `NowMs + delta` in PlayerbotBrain, PlayerbotBrain.Defense, PlayerbotBrain.Help, PlayerbotCombatSpells, PlayerbotQuestGoals, PlayerbotTownGoals,
PlayerbotGroupAI, PlayerbotPartyAI and ManagedPlayerbotFeature (each a wrapped `unchecked` add compared with a signed `unchecked((int)(deadline - now))`).

Stamps read as reads, not stamps: `PlayerbotGroupAI` `Now - _lootOpenedMs` (the `Now` read at `_lootOpenedMs = Now`, set when the loot window opened, is the stamp; the
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
  stamp and comparison both wall, nothing to do with the world clock. It did hold a real read-then-act gap: `SweepStaleProvisionsAsync` read `_lastProvisionSweepMs`
  and checked the interval before it took `_operations`, and a create holding the lock in between could have taken the slot through `TakeProvisionSweepSlot`. The
  sweep then wrote and ran a second time inside one interval. Fixed: the interval is checked again under the lock. The window is two adjacent statements, so no
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

- The projects outside `ArcaneCore.Playerbots` and `ArcaneCore.Game` (World, Data, Gateway, Kernel) except the playerbot reads of them named above.
- Multi-line assignments, where the `=` and the clock read are on different lines; and a delegate invocation (`_clock()`) is covered only for the wrappers named above.
- Behaviour. Nothing here shows the code runs correctly, only that no stamp was found compared against a different clock family.

Nothing mechanically enforces this audit. Re-run it by listing every assignment from a clock read, grouping by family as above, and reading every field that
appears under two families.
