# World clock read audit (tick-start snapshot)

`WorldClock.NowMs`, `ElapsedMs`, `Uptime` and `UtcNow` are fixed at the start of each tick, so two reads inside one tick are equal.
A duration taken between two such reads in one tick is 0. `WorldClock.LiveNowMs` is the only reading that moves inside a tick, and it
must never be compared with a stamp taken from `NowMs`.

## Scope

- Every field assigned from `NowMs` or `Uptime` in `src/` (20 assignments, listed below).
- Every `LiveNowMs` consumer.
- Every file that reads the world clock and also reads `Stopwatch`, `Environment.TickCount` or `DateTime.UtcNow`.

Found by grep and read by hand; no tooling enforces it.

## Result: no same-tick double-read bug found

Stamp fields (all set from the tick-start reading and compared only with later tick-start readings of the same source, so a zero gap inside
the tick that set the stamp is the intended meaning, e.g. a minute sweep or a spacing gap):

| Field | Set at | Compared at |
|---|---|---|
| `_prunedMs` | PlayerbotNeedsVoice.cs:1169 | :1168 |
| `_stepEndedMs` | PlayerbotKite.cs:266 | :202 (`now` from the same clock) |
| `_nextVisitAt` | PlayerbotEconomy.cs:243 | `Reached(now, ...)` |
| `_fallStartMs` | PlayerbotFollower.cs:556 | :564 (fall time bound; zero at the start is intended) |
| `_wanderCellLastMs` | PlayerbotBrain.cs:3365 | :3377 |
| `_startedMs` | PlayerbotRetreat.cs:146 | :255 |
| `_followedFrom` | ScenarioClock.cs:27 | :19 (both `Uptime`) |
| `_sweptAtMs` | PlayerbotGuildChat.cs:189, PlayerbotMood.cs:169, PlayerbotSocialEmotes.cs:40 | same files, `pass.NowMs` |
| `_lastAttemptMs` | PlayerbotGuildFounding.cs:102, 144, 166 | :70 |
| `_now` | PlayerbotRivalry.cs:303 | :339 |
| `_registryAtMs`, `_registryCheckedAtMs` | PlayerbotSocialFeature.cs:950, 974, 975 | :947, :948 (three `pass.NowMs` reads in one pass are the same value, gaps are between passes) |
| `_passNowMs` | PlayerbotSocialInitiative.cs:239 | :539 (a said-at stamp) |
| `_lastUpdateMs` | SpellFeature.cs:225 | :304 (`diff` between ticks) |
| `_lastHandoffMs` | SqliteWriteGate.cs:298 | uses its own stopwatch clock (`ISqliteGateClock`), not the world clock |

`LiveNowMs` consumers (ManagedPlayerbotFeature.cs:2659, 2927, 3078, 4646): every `LastUpdateMs` write and every comparison with it use `LiveNowMs`.
No mixing with `NowMs`.

Files that read both the world clock and a wall source (Map.Update, PlayerbotBrain, PlayerbotNavigation, PlayerbotOpportunities,
EconomySettlements, LootSettlements, QuestNpcFeature.Rewards, WorldRuntime.Perf and others): the wall reads time work spent
(`Stopwatch` deltas, cache TTLs, claim deadlines); none subtracts one source from the other.

Also reviewed earlier and not duration bugs: PlayerbotElevatorSnapshot.cs:33, BattlegroundManager.cs:1110 and 1157, WorldDeathClock.cs:27,
PlayerbotSocialFeature.cs:948, PlayerbotHelpLane.cs:255.

Limit: the `Now` properties that wrap `World.Uptime` or `NowMs` (PlayerbotGroupAI, PlayerbotPartyAI, PlayerbotBattlegroundAI and others) were
checked only for mixing with a wall source, not call by call.
