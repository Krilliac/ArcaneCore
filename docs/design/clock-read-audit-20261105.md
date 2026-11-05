# World clock read audit (partial)

This is a partial audit. It records what was read and what was not. It does not show that no same-tick double-read bug exists. Everything here is a
static reading of the source; nothing was run against a server or a test.

Citations are file:line positions read in the tree this doc was committed in. They go stale as files change; re-grep rather than trusting a line
number.

## Clock semantics

`WorldClock` (WorldClock.cs) exposes `NowMs`, `ElapsedMs`, `LiveNowMs` and `UtcNow`. `Uptime` is on `WorldRuntime` (WorldRuntime.ManualClock.cs),
not on `WorldClock`. While a tick runs, `NowMs`, `UtcNow` and `Uptime` are the tick-start reading, so two reads in one tick are equal and a
duration taken between two of them in one tick is 0. Exception: on the real clock `ElapsedMs`, and so `NowMs`, `UtcNow` and `Uptime`, is a live
stopwatch read until the first tick (WorldClock.cs:65-71). `LiveNowMs` is the reading that moves inside a tick, and it must not be compared with
a stamp taken from `NowMs`.

## What was read

Not a complete pass. The fields below were picked from rough greps. Every pattern in this doc is run from the repo root as
`git grep -nE '<pattern>' -- 'src/*.cs'`, and each count is the number of lines that command printed when this doc was last changed. A count is
lines matched, comments included. It is not a number of stamps; none of these results was classified.

- `_[A-Za-z]+ = [A-Za-z_.]*(NowMs|Uptime)`, a field assigned straight from a clock read: 20 lines. It misses wrapped forms such as
  `_x = Math.Max(1u, _session.World.NowMs)`.
- `_[A-Za-z]+ = now;`, a field assigned from a local: 131 lines. The local is not always a world clock reading.
- `Ms = .*NowMs`, any `Ms` name assigned from a line holding `NowMs`: 88 lines.

The tables below are samples read by hand. They are not the full set.

Read by hand:
- The fields in the tables below.
- The four `LiveNowMs` consumers in ManagedPlayerbotFeature.cs (2659, 2927, 3078, 4646). `git grep -n LiveNowMs` finds only these and the
  definition at WorldClock.cs:84.
- The lines that hold both a wall read and a world clock read:
  `git grep -nE 'Stopwatch|Environment\.TickCount|DateTime(Offset)?\.(UtcNow|Now)' -- 'src/*.cs' | grep -E '(\.NowMs|\.Uptime|LiveNowMs|\.UnixSeconds|\.UnixMilliseconds)\b'`
  prints 3 lines: WorldDeathClock.cs:33 (builds an anchor from both), BotController.cs:375 (records both in one event) and PlayerbotBrain.cs:739
  (passes them as separate arguments). None subtracts one from the other. This does not cover a world reading held in a local or a `Now` wrapper,
  and it does not cover separate statements. By the same two patterns taken per file, 30 files under `src/` hold both kinds of read; those files
  were not read across statements.

## Sampled stamp fields: world clock (`NowMs` / `Uptime`)

In each row the stamp and the comparison read the same source, and a zero gap inside the tick that set the stamp means what the code wants (a
spacing gap, a bound). Those two points are all that was checked. No problem of that kind showed up in these rows.

| Field | Set at | Compared at |
|---|---|---|
| `_stepEndedMs` | PlayerbotKite.cs:266 | :202 (`now` from `NowMs`, :135) |
| `_nextVisitAt` | PlayerbotEconomy.cs:296, :300; :243 is the test and inspection seam | :303 `Reached(now, ...)` (`now` from `NowMs`, :272) |
| `_fallStartMs` | PlayerbotFollower.cs:556 | :564 (fall time bound; zero at the start is intended) |
| `_wanderCellLastMs` | PlayerbotBrain.cs:3365 | :3377 (`now` from `NowMs`) |
| `_startedMs` | PlayerbotRetreat.cs:146 | :255 |
| `_followedFrom` | ScenarioClock.cs:27 | :19 (both `Uptime`) |
| `_lastUpdateMs` | SpellFeature.cs:225 (attach), :305 (each update) | :304 (`diff` between ticks) against `System.NowMs`, which is `world.NowMs` through the closure at :71 |

## Sampled stamp fields: the social accumulator

Every row in this table is stamped from `pass.NowMs`. `PlayerbotSocialPass.NowMs` (PlayerbotSocialFeature.cs:137) is `_clockMs`, a private
accumulator (`_clockMs += elapsedMs`, PlayerbotSocialFeature.cs:492), and the only two constructions of the pass, at :548 and :664, hand it in. It is
not a read of `WorldClock.NowMs` or `Uptime`, so these rows are not coverage of the world clock read question. They were read only for sign and
zero-gap meaning on their own clock, and the "tick-start reading" statement above does not apply to them.

| Field | Set at | Compared at |
|---|---|---|
| `_prunedMs` | PlayerbotNeedsVoice.cs:1169 | :1168 |
| `_sweptAtMs` | PlayerbotGuildChat.cs:189, PlayerbotMood.cs:169, PlayerbotSocialEmotes.cs:40 | same files, `pass.NowMs` |
| `_lastAttemptMs` | PlayerbotGuildFounding.cs:102, 144, 166 | :70 |
| `_now` | PlayerbotRivalry.cs:303 | :339 |
| `_registryAtMs`, `_registryCheckedAtMs` | PlayerbotSocialFeature.cs:950, 974, 975 | :947, :948 |
| `_passNowMs` | PlayerbotSocialInitiative.cs:239 | :539 (a said-at stamp) |

## Other reads outside the world clock

`_lastHandoffMs` (SqliteWriteGate.cs) has nothing to do with the social accumulator or the world clock. It is stamped at :298 from `_clock.NowMs`
on `IWriteGateClock` (interface at :576, field at :63), implemented by `SystemWriteGateClock` (:589), a live stopwatch. It is read at :254 (copied
to `handoffNow`, which becomes `progress.ResetNow` at :261) and at :312 (published as `_pendingGrantSinceMs`, read at :375). Those comparisons were
not audited; it is listed only so nobody counts it as world clock coverage.

Three more sites read a clock of their own, so they are not world clock coverage:
- BattlegroundManager.cs:1122 and :1169 each read `BattlegroundManager.NowMs` twice in one expression. That property (:274) is its own accumulator
  (`NowMs += diffMs`, :1196). Which thread runs these reads, and whether they can race :1196, was not determined.
- PlayerbotSocialFeature.cs:948 reads `pass.NowMs`, the social accumulator.
- PlayerbotHelpLane.cs:255 reads `pass.NowMs` in `LookAround`, whose parameter is a `PlayerbotSocialPass` (:198), so it is the social accumulator too.

Two world clock sites were looked at and read as not being duration bugs. PlayerbotElevatorSnapshot.cs:33 subtracts stamps from `world.NowMs` for an
inspection row; the stamps themselves arrive as `now` parameters and were not traced to their source. WorldDeathClock.cs:27 adds a wall anchor to a
game delta, on the manual clock only.

## LiveNowMs

Every `LastUpdateMs` write (ManagedPlayerbotFeature.cs:3081, :4646) and every read of it (:2570, :3013, :3015, :3019, :3079) sits on `LiveNowMs`:
`now` there is `frame.Now`, which comes from :2659 through :2765, or from :2927. There is no mixing with `NowMs`.

Only the map-think path refreshes `frame.Now` after the thinks (:2927). The serial path passes the pre-think `frame.Now` (:2765, :2800) to
`ReportStarvedBots`, so bots that thought this tick have `LastUpdateMs` later than `now`. The underflow is guarded at ManagedPlayerbotFeature.cs:3014
and PlayerbotThinkBudget.cs:263, so `ReportStarvedBots` is harmless on this point.

`Classify` (:2567) computes `unchecked(now - bot.LastUpdateMs)` at :2570 with the pre-think `frame.Now` and has no such guard. An underflow would
wrap and choose the Priority lane. Within one pass a bot is not classified after its own think. `Think` has one call site (:2593, inside `Visit`).
`TickLanes` classifies each index once, before the `Visit` of that index (PlayerbotThinkRotation.cs:283-285), and its main lane does not call
`Classify`. The serial path (:2636-2637) and the map path (:2953-2954, then PlayerbotThinkScheduler.cs:178) both reach it through `TickLanes`, with
`frame.Now` read after the bot list was built (:2656-2659).

Not proved: that a map key is served at most once in one tick. `ThinkMap` sets `group.Thought` (PlayerbotThinkScheduler.cs:176) but does not test
it, and `CollectUnthought` only keeps the fallback from repeating a served map. A second pass over the same key would classify bots that had thought
in the first one, against the same `frame.Now`.

## Not read (the remainder)

Row 88 stays open. The lists below are examples, not the complete remainder, and the greps below are a starting point. They do not define the set.

Run with the command form given under "What was read", when the review happens:

- Stamps: ` = .*\b(_?now(Ms)?|[A-Za-z_]*Now(Ms)?|Uptime)\b` printed 2179 lines. It takes `now`, `nowMs`, `_now`, `Now`, `NowMs` and prefixed forms
  such as `thinkNowMs`, in any target including tuples, and needs no particular field suffix. Many of the lines are locals and non-world clocks
  (`UtcNow` matches on purpose).
- Deadlines: `\b(_?now(Ms)?|[A-Za-z_]*Now(Ms)?|Uptime)\b(\.[A-Za-z]+)?\)* \+ ` printed 360 lines.
- Wrappers: `\b[A-Za-z_]*Now[A-Za-z]* *(\(\))? *(=>|\{ *get)` printed 76 lines, many of them wall clocks.

Each stamp, deadline and wrapper cited in the lists below and in the tables above matches its pattern: 62 of 62 stamps, 17 of 17 deadlines, 14 of 14
wrappers. That was checked by looking each citation up in the grep output, and the same check flagged PlayerbotCombatSpells.cs:124 against an earlier,
narrower pattern, so it can fail.

These greps are known to miss:
- A stamp that is not an assignment, such as `Volatile.Write(ref _lastProvisionSweepMs, now)` (ManagedPlayerbotFeature.cs:4486).
- A stamp from a differently named variable or parameter, such as `_lastSeenMs = receivedMs` (MovementAntiCheat.cs:307), `_evadeWindowStartMs =
  clockMs` (Creature.Anomalies.cs:81) and `_nextDespawnAtMs = at` (GameObjectMapSystem.cs:1570). Whether these carry a world clock reading was not
  checked.
- A reading taken through a delegate such as `_clock()` or `Clock?.Invoke()`, unless the line also names `now`.
- Any statement split across lines so that the `=` and the clock read are on different lines.

Stamp fields and their comparisons not read call by call:
- Direct assignments missing from the tables: ScenarioClock.cs:67 `_createdAt`; PlayerbotBrain.cs:1642 `_lootStartedMs`, :2214 `_errand`;
  PlayerbotRisk.cs:223/1282/1285 (`_fightSinceMs`, `_retreatEndedMs`, `_retreatDiedMs`); TeleportService.cs 478-479, 510-511, 587-588, 703, 780,
  943-944 (`StartedMs`, `StageSinceMs`); Knockback.cs:99/122 `LastTeleportOrKnockbackMs`; SanctuaryEffect.cs:29; PlayerbotMotion.cs:1556
  `RenewedMs` (compared at :521); PlayerbotPortalBoard.cs:304 `StateMs` (compared at :261); PlayerbotGroupAI.cs:456 `AnswerRequestedMs`;
  DebugPathFollow.cs:150 `LastCheckMs`; MovementHandlers.cs:507 `LastNonFiniteReportServerMs`.
- `NowMs + delta` deadlines: PlayerbotBrain.cs:2405/2470/3504; PlayerbotBrain.Defense.cs:130/146; PlayerbotBrain.Help.cs:89/163;
  PlayerbotCombatSpells.cs:298/317/325/557; PlayerbotQuestGoals.cs:2087; PlayerbotTownGoals.cs:247/503; PlayerbotGroupAI.cs:1628;
  PlayerbotPartyAI.cs:2351; ManagedPlayerbotFeature.cs:2270.
- Stamps taken through a local (`_x = now`) and `Math.Max(1u, ...)` forms, for example PlayerbotCombatSpells.cs:124.
- The `Now` and `NowMs` wrapper properties, which are stamp sources and were not read call by call. On the world clock by their own body:
  PlayerbotGroupAI.cs:252 (stamps at :1554/:1588), PlayerbotBattlegroundCoordinator.cs:180 (:441/:444/:688/:504),
  PlayerbotGroupCoordinator.cs:379 (:1890/:1939), PlayerbotBattlegroundAI.cs:172, ManagedEventScenarioHost.cs:38. Also on it but with an offset added:
  PlayerbotPartyAI.cs:327, PlayerbotFollower.cs:146. With a replaceable clock in front: PlayerbotOpportunities.cs:141,
  PlayerbotWorldPvpObjectives.cs:204. Through an injected delegate, source not traced here: SpellSystem.cs:117, PlayerbotDefenseBoard.cs:280,
  PlayerbotWorldEventBoard.cs:99, PlayerbotSocialMemory.cs:81.

Outside the world clock question, not read: PlayerbotNeedsVoice.cs 204/215/354/407/452/714/855 and PlayerbotGratitude.cs:363/381. They stamp from
`pass.NowMs`, and every `pass` declared in those two files is a `PlayerbotSocialPass`, so they are on the social accumulator. Their comparisons were
not checked on that clock either.

Nothing mechanically enforces this audit.
