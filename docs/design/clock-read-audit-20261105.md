# World clock read audit (partial)

This is a partial audit. It records what was read and what was not. It does not show that no same-tick double-read bug exists.

Citations are file:line positions taken at commit aa27cdfe4 (branch claude/uc2-a16b-timing-audit-rest, based on claude/uc2-integration). They go
stale as files change; re-grep rather than trusting a line number.

## Clock semantics

`WorldClock` (WorldClock.cs) exposes `NowMs`, `ElapsedMs`, `LiveNowMs` and `UtcNow`. `Uptime` is on `WorldRuntime` (WorldRuntime.ManualClock.cs),
not on `WorldClock`. While a tick runs, `NowMs`, `UtcNow` and `Uptime` are the tick-start reading, so two reads in one tick are equal and a
duration taken between two of them in one tick is 0. Exception: on the real clock `ElapsedMs`, and so `NowMs`, `UtcNow` and `Uptime`, is a live
stopwatch read until the first tick (WorldClock.cs:65-71). `LiveNowMs` is the reading that moves inside a tick, and it must not be compared with
a stamp taken from `NowMs`.

## What was read

Not a complete pass. The set of stamp fields was found with rough single-pattern greps, and the counts depend on the pattern (a field-assignment
grep gives roughly 40 lines; a `_x = now` local-stamp grep gives well over 100; a wider grep gives about 95 direct assignments of which about
70 are real stamps). The table below is a sample of those fields that was read by hand. It is not the full set.

Read by hand:
- The fields in the table below.
- The four `LiveNowMs` consumers in ManagedPlayerbotFeature.cs (2659, 2927, 3078, 4646).
- A same-line grep, for files that read a world clock and also `Stopwatch`, `Environment.TickCount` or `DateTime.UtcNow`, for a subtraction of
  one source from the other. A same-line grep does not prove the claim across separate statements. None was found on one line. WorldDeathClock.cs:27
  adds a wall anchor to a game delta on the manual clock only; that is not a subtraction.

## Sampled stamp fields

All compared only with later tick-start readings of the same source, so a zero gap inside the tick that set the stamp is the intended meaning
(a minute sweep, a spacing gap). The sampled fields show no bug.

| Field | Set at | Compared at |
|---|---|---|
| `_prunedMs` | PlayerbotNeedsVoice.cs:1169 | :1168 |
| `_stepEndedMs` | PlayerbotKite.cs:266 | :202 |
| `_nextVisitAt` | PlayerbotEconomy.cs:243 | `Reached(now, ...)` |
| `_fallStartMs` | PlayerbotFollower.cs:556 | :564 (fall time bound; zero at the start is intended) |
| `_wanderCellLastMs` | PlayerbotBrain.cs:3365 | :3377 |
| `_startedMs` | PlayerbotRetreat.cs:146 | :255 |
| `_followedFrom` | ScenarioClock.cs:27 | :19 (both `Uptime`) |
| `_sweptAtMs` | PlayerbotGuildChat.cs:189, PlayerbotMood.cs:169, PlayerbotSocialEmotes.cs:40 | same files, `pass.NowMs` |
| `_lastAttemptMs` | PlayerbotGuildFounding.cs:102, 144, 166 | :70 |
| `_now` | PlayerbotRivalry.cs:303 | :339 |
| `_registryAtMs`, `_registryCheckedAtMs` | PlayerbotSocialFeature.cs:950, 974, 975 | :947, :948 |
| `_passNowMs` | PlayerbotSocialInitiative.cs:239 | :539 (a said-at stamp) |
| `_lastUpdateMs` | SpellFeature.cs:225 | :304 (`diff` between ticks) |

`_lastHandoffMs` (SqliteWriteGate.cs:298) is not a world-clock stamp: it uses `ISqliteGateClock`, a live stopwatch (SqliteWriteGate.cs:593).

Also reviewed earlier and not duration bugs: PlayerbotElevatorSnapshot.cs:33, BattlegroundManager.cs:1110 and 1157, WorldDeathClock.cs:27,
PlayerbotSocialFeature.cs:948, PlayerbotHelpLane.cs:255.

## LiveNowMs

Every `LastUpdateMs` write and every comparison with it in the four consumers use `LiveNowMs`; no mixing with `NowMs`. One note: only the
map-think path refreshes `frame.Now` after the thinks (ManagedPlayerbotFeature.cs:2927). The serial path passes the pre-think `frame.Now`
(:2765, :2800) to `ReportStarvedBots`, so bots that thought this tick have `LastUpdateMs` later than `now`. The underflow is guarded at
ManagedPlayerbotFeature.cs:3014 and PlayerbotThinkBudget.cs:263, so it is harmless.

## Not read (the remainder)

Stamp fields and their comparisons not read call by call:
- Direct assignments missing from the table: ScenarioClock.cs:67 `_createdAt`; PlayerbotBrain.cs:1642 `_lootStartedMs`, :2214 `_errand`;
  PlayerbotRisk.cs:223/1282/1285 (`_fightSinceMs`, `_retreatEndedMs`, `_retreatDiedMs`); TeleportService.cs 478-479, 510-511, 587-588, 703, 780,
  943-944 (`StartedMs`, `StageSinceMs`); Knockback.cs:99/122 `LastTeleportOrKnockbackMs`; SanctuaryEffect.cs:29; PlayerbotNeedsVoice.cs
  204/215/354/407/452/714/855; PlayerbotGratitude.cs:363/381.
- Stamps taken through a local (`_x = now`) and `Math.Max(1u, ...)` forms, for example PlayerbotCombatSpells.cs:124.
- The `Now` wrapper properties, which are stamp sources and were checked only for mixing with a wall source, not call by call:
  PlayerbotGroupAI.cs:252 (stamps at :1554/:1588), PlayerbotBattlegroundCoordinator.cs:180 (:441/:444/:688/:504),
  PlayerbotGroupCoordinator.cs:379 (:1890/:1939), PlayerbotPartyAI.cs:327, PlayerbotFollower.cs:146, PlayerbotBattlegroundAI.cs:172,
  PlayerbotOpportunities.cs:141, PlayerbotWorldPvpObjectives.cs:204.
- The list of files that read both a world clock and a wall source is not enumerated here, so "every file" was not checked.

Nothing mechanically enforces this audit.
