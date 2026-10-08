# Anticheat: movement checks, scores and escalation (lane `anticheat`)

Ported from the developer's MaNGOS Zero fork (`Krilliac/server-Zero`, read-only clone): `feature/anticheat-tooling`
(AntiCheatMgr, MovementAnticheat, AntiCheatCommands), `feature/timesync` (time-skip abuse, acknowledgement timestamps,
ping latency average) and two files of `feature/anticheat-detection-framework` (the gateway `SpeedHackDetector` and the
`EdgeChecks` rate limiter). Every movement rule was cross-checked against vmangos `src/game/Anticheat/MovementAnticheat`
(`HandlePositionTests`, `HandleFlagTests`, `CheckSpeedHack`, `CheckFakeTransport`, `CheckMultiJump`) and kept only where it
has a low false-positive rate. Nothing was copied; the code is new C#.

The checks **observe and score; they never change or reject a packet**. On by default with `AntiCheat:Action = Log`:
findings are scored, logged and written to the violation log, and nothing is done to a player until an operator raises the
ceiling. vmangos ships its own anticheat off (`Anticheat.Enable = 0`), so the default is listed in the release-caveat
register of [operations](../guide/operations.md).

## What runs where

| Piece | Code | Notes |
|---|---|---|
| Independent-clock speed-hack detector | `Game/AntiCheat/SpeedHackDetector.cs` | the fork's gateway detector: a sliding window of client `MovementInfo.time` deltas against the server's receive deltas; a sustained ratio above 1 + `AntiCheat:SpeedClock:TolerancePercent`/100 is a fast client clock. Per-pair guards (same-millisecond pair, gap above `MaxGapMs`, a client step more than `MaxGapMs` ahead), hysteresis, cooldown |
| Per-player movement checks | `Game/AntiCheat/MovementAntiCheat.cs` | pure, fed a `MovementSample` per movement packet (`MovementHandlers.HandleMovement`, before the observers store the block) |
| Scores, escalation, autoban points | `Game/AntiCheat/AntiCheatScores.cs` | `AntiCheatScores` (per character, linear decay), `AntiCheatEscalation.Decide`, `AutobanLedger` |
| Manager | `World/AntiCheat/AntiCheatFeature.cs` | exemptions, the sample (allowed speed, granted flags, root, transport claim, terrain probe), escalation, GM alert, rubberband, kick, autoban, batched log |
| Terrain probe | `World/AntiCheat/MapAntiCheatTerrain.cs` | answers only where the data is in memory (see below) |
| Packet budgets | `Kernel/Net/OpcodeRateLimiter.cs`, wired in `WorldSession.DispatchAsync` | see [netguard](../ops/netguard.md) |
| Ping latency average | `WorldSession.LatencyMs` | EWMA (alpha 1/5) of the latency every `CMSG_PING` reports; the base discarded it |
| Receive time | `WorldSession.CurrentPacketReceivedMs` | stamped when the packet is queued, so a world-thread stall does not bunch the times the checks compare |
| Violation log | `Data/Characters/AntiCheat/AntiCheatDataModule.cs`, characters schema **41** | `character_anticheat_log`; coalesced and batched (one insert per flush) |
| Commands | `World/AntiCheat/AntiCheatCommands.cs` | `.anticheat ...` |
| Startup check | `World/AntiCheat/AntiCheatConfigChecks.cs` | a value that does not bind or validate is exit 78 |

## Exemptions

Never checked: accounts at or above `AntiCheat:ExemptSecurity` (default Moderator, every staff account), a GM with GM mode
on (`.gm on`), and the server's own managed playerbot sessions (`AntiCheat:ExemptManagedBots`, default true: they are
trusted server code and move by `TryManagedAction`). `AntiCheat:Enabled = false` exempts everyone.

## The checks

Weights follow the fork. A finding adds its weight (clamped 0..100) to the character's score.

| Finding | Type | Weight | Rule | Never scored when |
|---|---|---|---|---|
| Speed | Speed | 5..25 by overshoot | the horizontal step exceeds the allowed speed × (1 + `SpeedTolerancePercent`) × budget + `SpeedSlackYards`. Budget = the client's own elapsed time, capped by the server's receive interval plus the latency slack (`LatencySlackMs` + 2 × ping average, at most `MaxLatencySlackMs`). Airborne steps get 25 % more | on a known transport (either packet), during a knockback, the step after a re-baseline, a step whose client time went back |
| Teleport | Teleport | 25 | one step longer than `TeleportDistance` + speed × budget | same |
| Fast clock | TimeSync | 5..25 (half the severity) | `SpeedHackDetector` fires | lag (the real side only gets longer), idle gaps |
| Water walk / hover / slow fall | Flag | 25 / 25 / 15 | the flag without a grant, living players only | an aura that grants it is on the player, an order of that kind is pending (the client may assert the flag until it has the order), `AntiCheatFeature.Grant` |
| Levitating | Flag | 40 | a creature flag (vmangos `HandleFlagTests`) | granted |
| Flying while swimming | Flag | 40 | vmangos' swim-fly hack; flying alone is not judged (vmangos neither) | granted |
| Fake transport | Flag | 20 | `ON_TRANSPORT` starts with a GUID that is no ship of the transport system near the player (200 yd) and no TRANSPORT game object (elevator, tram) on the map near it (600 yd); any other GUID kind | transports disabled or no game object system (Unknown); a fake claim never hides the speed checks |
| Move start while rooted | Physics | 15 | `MSG_MOVE_START_*` / jump while an acknowledged root is in force | the root is not acknowledged yet or an unroot is pending |
| Moving while rooted | Physics | 20 | forward/backward/strafe flags while rooted | same |
| Infinite jump | Jump | 30 | `MSG_MOVE_JUMP` while airborne (no `FALL_LAND` or swim since the last jump) | after any re-baseline |
| Fall damage suppressed | Fall | 25 | a drop of at least `FallSuppressYards` that ends grounded without `MSG_MOVE_FALL_LAND` | landing in water, dead, rooted |
| Timestamp back | PacketTiming | 10 | the client's movement time goes back by more than `ClientTimeRegressionMs` | after a reported time skip |
| Zero timestamp | PacketTiming | 5 | client time 0 after a baseline (vmangos `NULL_CLIENT_TIME`) | |
| Burst | Burst | 15 | more than `BurstPacketsPerSecond` packets within one second by the receive clock **and** the client's own clock | lag bunching (sent over a longer client time) |
| Oversized time skip | TimeSync | up to 30 | `CMSG_MOVE_TIME_SKIPPED` above `MaxTimeSkipMs` | |
| Time-skip spam | PacketTiming | 12 | more than `MaxTimeSkipsPer10Seconds` skips in ten seconds | |
| Acknowledgement time back | PacketTiming | 10 | a speed/flag/root/knockback ack's client time goes back | within the grace after a reported time skip |
| Swimming out of water | Physics | 20 | the swim flag on four packets in a row where terrain and models hold no liquid | no terrain tile with data **and** vmap tile in memory, inside a WMO (indoor water is in the models) |
| Climbed into the air | Vertical | 15 | a steep climb (dz > 1 and > 2 × horizontal) on the ground that ends more than 3 yd above the floor | no terrain and vmap data |
| Through a wall | Physics | 15 | a ground step over 4 yd without line of sight, twice within five seconds (one can be a corner) | no vmap tile at both ends |
| Unknown spell | Spell | 10 | `CMSG_CAST_SPELL` refused as `NotKnown` by the spell system | |
| Trade-window item | Item | 30 | `CMSG_USE_ITEM` of an item offered in the trade (vmangos "cheat way only") | |
| Far interaction | Interact | 15 | `CMSG_GAMEOBJ_USE` refused as too far, and more than 20 yd + run speed × 2 × latency away | at the edge of the range |

The allowed speed is the highest of the player's five speeds and of every speed change still waiting for its ack. When it
drops (a speed decrease acknowledged, a mount gone), the previous speed still counts for `SpeedChangeGraceMs` plus the latency
slack, so packets sent before the ack are never judged by the new speed.

**Re-baselines** (the next packet is trusted, the clock window forgotten): the first packet; a server relocation (the
`TeleportService` events, and any time the stored position is not where the last judged packet left it: a charge, a
graveyard, the end of a taxi flight); a knockback acknowledgement (no speed check until the landing or six seconds); a time
skip; a gap longer than `BaselineGapMs`. Movement of a unit the player controls (a possessed creature or player) is not
checked.

**Terrain-dependent checks** run only where the data is in memory: the terrain tile with data for liquid and floor, the
map's vmap tile (`VMapManager`, line of sight enabled) for all three. The probe never loads a tile; no data is never a finding.

**Not ported** (high false-positive rate or no retail basis): the fork's acceleration/velocity-delta gate and direction
reversal checks, its bot heuristic (snap-to-waypoint plus metronomic timing), the GCD/cast-spam check, the `.spoof` /
`.anticheat test` simulators, the jail, the auto-resync rubberband on desync streaks, the relay-time correction, and the
gateway's protocol validator and session guard.

## Escalation

`AntiCheatEscalation.Decide`: the score reaches `ScoreGmAlert` (30) → GmAlert, `ScoreRubberband` (60) → Rubberband,
`ScoreKick` (120) → Kick, otherwise Log; capped by `AntiCheat:Action`. The score decays by `DecayPerSecond` (2) and is kept
across a relog until it has decayed (pruned every minute).

* **Log**: one rate-limited log line (at most one per 10 s, with the count of the others) and a violation log row.
* **GmAlert**: a system message to every staff member online (`Security` above Player), at most one per offender per
  `GmAlertIntervalSeconds`. The fork only logged ("planned enhancement").
* **Rubberband**: a near teleport (`TeleportService.TeleportTo`) to the last position that passed every position check, at
  most once per `RubberbandIntervalMs`.
* **Kick**: the session is disconnected and the score reset. With `AntiCheat:Autoban:Enabled`, the kick adds `KickPoints` (10) to the
  account; the points decay by `DecayPerHour` (1); at `Threshold` (25: the third kick within five hours) the account is
  banned through `IBanStore.BanAccountAsync` with author `AntiCheat`, for `FirstBanSeconds` (1 day), `SecondBanSeconds`
  (7 days) or `LaterBanSeconds` (0, permanent) by the number of earlier `AntiCheat` bans in the ban history within
  `HistoryDays` (180). The step therefore survives a restart; the kick points do not (they are memory only, so a restart
  forgives at most two kicks). The ban goes through the normal ban path, so the live enforcement kicks every session of the
  account. The fork's 30-point threshold could only be reached by a fourth kick once any decay applied.

## Violation log

`character_anticheat_log` (characters schema 41): character, account, type (the `AntiCheatViolation` number), summed
weight, score after the last finding, count, map and position, a fixed detail text, first and last unix time. Repeats of one
type by one character within `AntiCheat:Log:CoalesceMs` (5 s) fold into one row; the queue (at most `AntiCheat:Log:MaxQueuedRows`) is written
every `AntiCheat:Log:FlushIntervalSeconds` (10) in one batch of at most `AntiCheat:Log:MaxRowsPerFlush`, on the thread pool. A failed write is
logged at most once a minute and the batch dropped, so the log never grows without bound. The rows go with a deleted
character. Characters versions 41 and 42 belong to other lanes of the same wave and are held by reserved placeholders
(`CharactersReservedGap41`, `CharactersReservedGap42`) until those lanes merge; delete the placeholders then.

## Commands

| Command | Security | |
|---|---|---|
| `.anticheat status [$name]` | GameMaster | the settings in force and the character's live score (name, selection or self) |
| `.anticheat top [#count]` | GameMaster | highest live scores (10, at most 50) |
| `.anticheat report [$name]` | GameMaster | flushes the queue, then the newest ten log rows |
| `.anticheat set $field $value` | Administrator | live until the next `.reload config` or restart: `enabled`, `action`, `alert`, `rubberband`, `kick`, `decay`, `speedtolerance`, `teleport`, `terrain`, `persist`, `autoban`; refused when the result does not validate |
| `.anticheat warn [$name]` | GameMaster | an on-screen warning to the player |
| `.anticheat delete [$name]` | Administrator | forgets the live score and deletes the log rows |
| `.anticheat score [$name] [#value]` | GameMaster (setting: Administrator) | shows, or sets and applies what the score warrants now (the fork's SetScore) |
| `.anticheat rubberband [$name]` | GameMaster | moves the online player back to its last validated position |

Acting on another player follows the GM hierarchy rule (`CommandContext.CanActOn`). The fork's levels map SEC_GAMEMASTER to
GameMaster and SEC_ADMINISTRATOR to Administrator.

## Configuration

The whole `AntiCheat` section (see the [configuration reference](../reference/configuration.md)) is validated at start
(`AntiCheatConfigChecks`, exit 78) and applied as a whole by `.reload config` (`ConfigContentReloadable`): one bad value
rejects the reload and nothing changes. A ceiling of Kick without the autoban is a startup warning (a kicked cheater can
reconnect at once). The `Net:Protection:World*` packet budgets are restart-only.

## Tests

* `Game.Tests/AntiCheat/SpeedHackDetectorTests` (the fork's six gateway cases ported to xunit, plus the 32-bit wrap and the
  switch), `MovementAntiCheatTests` (every check positive and its look-alikes negative: lag bunching, knockback, speed
  decrease and pending increase, server relocation, known/unknown/fake transports, dead players, grants, terrain with and
  without data), `AntiCheatScoresTests` (decay, ceiling, autoban points and ladder, validation).
* `Kernel.Tests/Net/OpcodeRateLimiterTests` (the fork's test_gateway_rate cases, a retail cadence under the defaults).
* `World.Tests/AntiCheat/AntiCheatWorldTests` through real sockets: a legitimate client running, jumping and stopping
  scores nothing (and the same client's blink is scored), a managed playerbot is exempt (and scored with the exemption off),
  staff and GM mode are exempt, the GM alert reaches the staff online, the rubberband teleports back, the kick disconnects and
  the autoban writes a 7-day ban after an earlier one, the log coalesces into one row written in one batch, the ping feeds
  the latency average, and the commands. `AntiCheatConfigurationTests` (startup check, `.reload config`),
  `Net/PacketBudgetTests` (drop and flood through a real session).

## Open questions

* The thresholds are the fork's and vmangos' and have not been tuned against real 1.12 client traffic; the log-only default
  is there to collect that data before anything acts.
* Elevator and tram transports are judged by their game object only; ArcaneCore does not move players with them.
* `MOVEFLAG_LEVITATING` on a player is treated as a hack as vmangos does; a real client capture of priest Levitate (1.12)
  should confirm the client never sets it.
