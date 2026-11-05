# World clock read audit (partial)

This is a partial audit. It records what was read and what was not. It does not show that no same-tick double-read bug exists.

Citations are file:line positions read in the tree this doc was committed in. They go stale as files change; re-grep rather than trusting a line
number.

## Clock semantics

`WorldClock` (WorldClock.cs) exposes `NowMs`, `ElapsedMs`, `LiveNowMs` and `UtcNow`. `Uptime` is on `WorldRuntime` (WorldRuntime.ManualClock.cs),
not on `WorldClock`. While a tick runs, `NowMs`, `UtcNow` and `Uptime` are the tick-start reading, so two reads in one tick are equal and a
duration taken between two of them in one tick is 0. Exception: on the real clock `ElapsedMs`, and so `NowMs`, `UtcNow` and `Uptime`, is a live
stopwatch read until the first tick (WorldClock.cs:65-71). `LiveNowMs` is the reading that moves inside a tick, and it must not be compared with
a stamp taken from `NowMs`.

## What was read

Not a complete pass. The set of stamp fields was found with rough single-pattern greps, and the counts depend on the pattern. Patterns
used, from the repo root, over `src/`:

- `_[A-Za-z]+ = [A-Za-z.]*(NowMs|Uptime)` (a field assigned directly from a world clock read): roughly 40 to 50 lines.
- `_[A-Za-z]+ = now;` (a field assigned from a local that holds the reading): about 200 lines. Many are not clock stamps.
- A wider grep for any `Ms = ...NowMs` assignment gave about 95 direct assignments, of which about 70 are real stamps.

The tables below are samples read by hand. They are not the full set.

Read by hand:
- The fields in the tables below.
- The four `LiveNowMs` consumers in ManagedPlayerbotFeature.cs (2659, 2927, 3078, 4646).
- A same-line grep, for files that read a world clock and also `Stopwatch`, `Environment.TickCount` or `DateTime.UtcNow`, for a subtraction of
  one source from the other. A same-line grep does not prove the claim across separate statements. None was found on one line. WorldDeathClock.cs:27
  adds a wall anchor to a game delta on the manual clock only; that is not a subtraction.

## Sampled stamp fields: world clock (`NowMs` / `Uptime`)

Compared only with later tick-start readings of the same source, so a zero gap inside the tick that set the stamp is the intended meaning
(a spacing gap, a bound). The sampled fields show no bug.

| Field | Set at | Compared at |
|---|---|---|
| `_stepEndedMs` | PlayerbotKite.cs:266 | :202 |
| `_nextVisitAt` | PlayerbotEconomy.cs:243 | `Reached(now, ...)` |
| `_fallStartMs` | PlayerbotFollower.cs:556 | :564 (fall time bound; zero at the start is intended) |
| `_wanderCellLastMs` | PlayerbotBrain.cs:3365 | :3377 |
| `_startedMs` | PlayerbotRetreat.cs:146 | :255 |
| `_followedFrom` | ScenarioClock.cs:27 | :19 (both `Uptime`) |
| `_lastUpdateMs` | SpellFeature.cs:225 | :304 (`diff` between ticks) |

## Sampled stamp fields: not the world clock

These are stamped from `pass.NowMs`. `PlayerbotSocialPass.NowMs` (PlayerbotSocialFeature.cs:137) is `_clockMs`, a private accumulator
(`_clockMs += elapsedMs`, PlayerbotSocialFeature.cs:492; passed into the pass at :548 and :664). It is not a read of `WorldClock.NowMs` or
`Uptime`, so these rows are not coverage of the world clock read question. They were read only for sign and zero-gap meaning on their own
clock, and the "tick-start reading" statement above does not apply to them.

| Field | Set at | Compared at | Clock |
|---|---|---|---|
| `_prunedMs` | PlayerbotNeedsVoice.cs:1169 | :1168 | social accumulator |
| `_sweptAtMs` | PlayerbotGuildChat.cs:189, PlayerbotMood.cs:169, PlayerbotSocialEmotes.cs:40 | same files, `pass.NowMs` | social accumulator |
| `_lastAttemptMs` | PlayerbotGuildFounding.cs:102, 144, 166 | :70 | social accumulator |
| `_now` | PlayerbotRivalry.cs:303 | :339 | social accumulator |
| `_registryAtMs`, `_registryCheckedAtMs` | PlayerbotSocialFeature.cs:950, 974, 975 | :947, :948 | social accumulator |
| `_passNowMs` | PlayerbotSocialInitiative.cs:239 | :539 (a said-at stamp) | social accumulator |
| `_lastHandoffMs` | SqliteWriteGate.cs:298 | not read | `IWriteGateClock` (SqliteWriteGate.cs:576, field at :63), implemented by `SystemWriteGateClock` (:589), a live stopwatch |

Also reviewed earlier and not duration bugs: PlayerbotElevatorSnapshot.cs:33, BattlegroundManager.cs:1122 and 1169 (each reads `NowMs` twice in
one expression), WorldDeathClock.cs:27, PlayerbotSocialFeature.cs:948, PlayerbotHelpLane.cs:255. `BattlegroundManager.NowMs` (:274) is its own
accumulator (`NowMs += diffMs`, :1196), not the world clock, so those two sites are not world clock coverage either.

## LiveNowMs

Every `LastUpdateMs` write and every comparison with it in the four consumers use `LiveNowMs`; no mixing with `NowMs`. One note: only the
map-think path refreshes `frame.Now` after the thinks (ManagedPlayerbotFeature.cs:2927). The serial path passes the pre-think `frame.Now`
(:2765, :2800) to `ReportStarvedBots`, so bots that thought this tick have `LastUpdateMs` later than `now`. The underflow is guarded at
ManagedPlayerbotFeature.cs:3014 and PlayerbotThinkBudget.cs:263, so `ReportStarvedBots` is harmless on this point. `Classify`
(ManagedPlayerbotFeature.cs:2567) also computes `unchecked(now - bot.LastUpdateMs)` with the pre-think `frame.Now` and has no such guard
(an underflow would wrap and choose the Priority lane). Whether it can run after a same-tick think was not determined.

## Not read (the remainder)

The lists below are examples, not the complete remainder. The real set is defined by these greps over `src/`, to be run at the time of the
review: `(Ms|At|Since|From) = .*(NowMs|Uptime|now)` for stamps, `(NowMs|Uptime|now) \+ ` for deadlines, and `Now =>` for the
`Now` wrapper properties.

Stamp fields and their comparisons not read call by call:
- Direct assignments missing from the tables: ScenarioClock.cs:67 `_createdAt`; PlayerbotBrain.cs:1642 `_lootStartedMs`, :2214 `_errand`;
  PlayerbotRisk.cs:223/1282/1285 (`_fightSinceMs`, `_retreatEndedMs`, `_retreatDiedMs`); TeleportService.cs 478-479, 510-511, 587-588, 703, 780,
  943-944 (`StartedMs`, `StageSinceMs`); Knockback.cs:99/122 `LastTeleportOrKnockbackMs`; SanctuaryEffect.cs:29; PlayerbotMotion.cs:1556
  `RenewedMs` (compared at :521); PlayerbotPortalBoard.cs:304 `StateMs` (compared at :261); PlayerbotGroupAI.cs:456 `AnswerRequestedMs`;
  DebugPathFollow.cs:150 `LastCheckMs`; MovementHandlers.cs:507 `LastNonFiniteReportServerMs`.
- PlayerbotNeedsVoice.cs 204/215/354/407/452/714/855 and PlayerbotGratitude.cs:363/381: not read, and their clock is not established
  (the social accumulator `pass.NowMs` or a world read); check each before counting it.
- `NowMs + delta` deadlines: PlayerbotBrain.cs:2405/2470/3504; PlayerbotBrain.Defense.cs:130/146; PlayerbotBrain.Help.cs:89/163;
  PlayerbotCombatSpells.cs:298/317/325/557; PlayerbotQuestGoals.cs:2087; PlayerbotTownGoals.cs:247/503; PlayerbotGroupAI.cs:1628;
  PlayerbotPartyAI.cs:2351; ManagedPlayerbotFeature.cs:2270.
- Stamps taken through a local (`_x = now`) and `Math.Max(1u, ...)` forms, for example PlayerbotCombatSpells.cs:124.
- The `Now` wrapper properties, which are stamp sources and were checked only for mixing with a wall source, not call by call:
  PlayerbotGroupAI.cs:252 (stamps at :1554/:1588), PlayerbotBattlegroundCoordinator.cs:180 (:441/:444/:688/:504),
  PlayerbotGroupCoordinator.cs:379 (:1890/:1939), PlayerbotPartyAI.cs:327, PlayerbotFollower.cs:146, PlayerbotBattlegroundAI.cs:172,
  PlayerbotOpportunities.cs:141, PlayerbotWorldPvpObjectives.cs:204.
- The list of files that read both a world clock and a wall source is not enumerated here, so "every file" was not checked.

Nothing mechanically enforces this audit.
