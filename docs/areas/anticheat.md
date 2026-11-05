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

A separate, report-only analysis of the packets themselves (shape, cadence, replay, wrong state, orphan acknowledgements, actions while incapacitated) is described in [packet heuristics](packet-heuristics.md); it is off by default and never feeds these scores.

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

A GM cheat is a grant of flags, not an exemption (vmangos reads `PLAYER_CHEAT_FLY` and `PLAYER_CHEAT_FIXED_Z` only in the flag
checks, MovementAnticheat.cpp:789-806, 1090): `.cheat fly` lets the levitating and swim-fly flags pass the flag test and the
swimming-out-of-water test, `.cheat waterwalk` and `.cheat featherfall` grant the water walk and slow fall flags
(`MovementCheats.GrantedFlags`). Speed, teleport, fall damage, the void kill and the time checks judge the player as always, and a
jump ends fly mode and a successful far teleport ends fly and fixed Z on arrival (a near teleport, and a far one that never sends SMSG_NEW_WORLD because it is refused, bails out or stalls while scheduled, keep them, as the client keeps flying). `UNIT_FLAG_SERVER_CONTROLLED` (`.cheat wallclimb`) switches the climbed-into-the-air test off for that
player (vmangos NO_WALL_CLIMB_CHECK_UNIT_FLAGS). A feather fall cheat also takes the fall damage away. The rejection points (an unknown spell, a far game object) are not movement
and still apply. The cheats live on the `Player`, are never saved, and end with the session.

## The checks

Weights follow the fork. A finding adds its weight (clamped 0..100) to the character's score.

| Finding | Type | Weight | Rule | Never scored when |
|---|---|---|---|---|
| Speed | Speed | 5..25 by overshoot | the horizontal step exceeds the allowed speed × (1 + `SpeedTolerancePercent`) × budget + `SpeedSlackYards`. Budget = the client's own elapsed time, capped by the server's receive interval plus the latency slack (`LatencySlackMs` + 2 × ping average, at most `MaxLatencySlackMs`); for the step that spans a reported time skip, the client's elapsed time plus the skip the evidence supports (below), capped by the receive interval alone (no latency slack). Airborne steps get 25 % more. A block stamped at or behind the previous one counts as **zero client time elapsed**: its step gets only `SpeedSlackYards` (and `TeleportDistance` for a teleport), plus a supported time skip. Beside the step, a **distance budget** spans packets (below) | on a known transport (either packet), during a knockback, the step after a re-baseline |
| Teleport | Teleport | 25 | one step longer than `TeleportDistance` + speed × budget | same |
| Stamp far ahead | TimeSync | 15 | a block stamped more than `SpeedClock:MaxGapMs` ahead of the real interval since the previous block **and** whose step spends more yards than that real interval allows (allowed speed × (1 + tolerance) × the real interval + `SpeedSlackYards`). A pair that reads that way and spends nothing is an honest desync (a held stop and a start sent seconds later that arrive together) and is left to the independent clock, which discards it. A scored step gets the real interval and no latency slack. The weight (15) is this server's own choice (no reference scores it), in step with the other timing findings | same as Speed |
| Fast clock | TimeSync | 5..25 (half the severity) | `SpeedHackDetector` fires | lag (the real side only gets longer), idle gaps; a reported time skip credits at most 250 ms of jitter per ten seconds |
| Water walk / hover / slow fall | Flag | 25 / 25 / 15 | the flag without a grant, living players only | an aura that grants it is on the player, an order of that kind is pending (the client may assert the flag until it has the order), `AntiCheatFeature.Grant` |
| Levitating | Flag | 40 | a creature flag (vmangos `HandleFlagTests`) | granted |
| Flying while swimming | Flag | 40 | vmangos' swim-fly hack; flying alone is not judged (vmangos neither) | granted |
| Fake transport | Flag | 20 | `ON_TRANSPORT` starts with a GUID that is no ship of the transport system near the player (200 yd) and no TRANSPORT game object (elevator, tram) on the map near it (600 yd, measured from where its TransportAnimation.dbc path puts it now: a Deeprun Tram car is 2,480 yd from its spawn at the other platform; `TramCarVisibilityTests`); any other GUID kind | transports disabled or no game object system (Unknown); a fake claim never hides the speed checks |
| Move start while rooted | Physics | 15 | `MSG_MOVE_START_*` / jump while an acknowledged root is in force | the root is not acknowledged yet or an unroot is pending |
| Moving while rooted | Physics | 20 | forward/backward/strafe flags while rooted | same |
| Infinite jump | Jump | 30 | `MSG_MOVE_JUMP` while airborne (no `FALL_LAND` or swim since the last jump) | after any re-baseline |
| Fall damage suppressed | Fall | 25 | a drop of at least `FallSuppressYards` that ends grounded without `MSG_MOVE_FALL_LAND` | landing in water, dead, rooted |
| Timestamp back | PacketTiming | 10 | the client's movement time goes back by more than `ClientTimeRegressionMs` (a reported time skip is no exemption) | |
| Zero timestamp | PacketTiming | 5 | client time 0 after a baseline (vmangos `NULL_CLIENT_TIME`) | |
| Burst | Burst | 15 | more than `BurstPacketsPerSecond` packets within one second by the receive clock **and** the client's own clock (a signed difference: stamps standing still or going back span no client time and count) | lag bunching (sent over a longer client time) |
| Oversized time skip | TimeSync | up to 30 | `CMSG_MOVE_TIME_SKIPPED` above `MaxTimeSkipMs` | |
| Impossible time skip | PacketTiming | 10 | `CMSG_MOVE_TIME_SKIPPED` above 2³¹ − 1 ms (24.8 days; a sign-confused forge). It gets no credit | |
| Time-skip spam | PacketTiming | 12 | more than `MaxTimeSkipsPer10Seconds` skips in ten seconds | |
| Acknowledgement time back | PacketTiming | 10 | a speed/flag/root/knockback ack's client time goes back by more than `ClientTimeRegressionMs` (a reported time skip is no exemption) | |
| Swimming out of water | Physics | 20 | the swim flag on four packets in a row where terrain and models hold no liquid | no terrain tile with data **and** vmap tile in memory, inside a WMO (indoor water is in the models) |
| Climbed into the air | Vertical | 15 | a steep climb (dz > 1 and > 2 × horizontal) on the ground that ends more than 3 yd above the floor | no terrain and vmap data |
| Through a wall | Physics | 15 | a ground step over 4 yd without line of sight, twice within five seconds (one can be a corner) | no vmap tile at both ends |
| Unknown spell | Spell | 10 | `CMSG_CAST_SPELL` refused as `NotKnown` by the spell system | |
| Honeypot spell | Spell | `AntiCheat:SpellHoneypot:Weight` (60) | `CMSG_CAST_SPELL` of a spell that is `NeverCast`, `TestSpell` or `Deprecated` in `DeliberatelyInertSpells`, or listed in `AntiCheat:SpellHoneypot:ToolSpellIds`; only with `AntiCheat:SpellHoneypot:Enabled` (default **false**: retail just refuses the cast). The cast is dropped before the spell system, as an unknown spell is | GMs and every exempt session |
| Trade-window item | Item | 30 | `CMSG_USE_ITEM` of an item offered in the trade (vmangos "cheat way only"); also `CMSG_SWAP_ITEM`, `CMSG_SWAP_INV_ITEM`, `CMSG_AUTOEQUIP_ITEM`, `CMSG_AUTOSTORE_BAG_ITEM`, `CMSG_SPLIT_ITEM`, `CMSG_DESTROYITEM` and `CMSG_SELL_ITEM` whose **source** is an offered item | |
| Far interaction | Interact | 15 | `CMSG_GAMEOBJ_USE` refused as too far, and more than 20 yd + run speed × 2 × latency away | at the edge of the range |

**Offered items cannot change while a trade is open.** The trade settles from the live item count, so anything that changes an
offered stack after the first accept would hand the partner a different amount than they saw. The move opcodes above are refused
with `ItemLocked` (a vendor sale with `CantSellItem`). Destination-side hits are refused the same way but **not scored**:
vmangos never checks the destination, so a client that lets a drop onto an offered stack through must not be charged. These are
a swap, split or auto-store whose destination, or whose automatic target (a merge into a partial stack), is an offered item, and
`CMSG_WRAP_ITEM` / `CMSG_OPEN_ITEM` on an offered item or offered wrapping paper. This is deliberately stricter than vmangos,
which sets `Item::IsInTrade` only while a trade completes and so refuses only `Player::SplitItem` (silently, scored). Only the six
traded slots count; the enchant-target slot is never moved. One helper, `EconomyFeature.IsOffered`, answers the question for the
item handlers, the vendor, crafting reagents and use-item.

**The distance budget** (`CheckDistanceBudget`): a step's own budget cannot see a stream of packets each stamped at or behind the last (every one gets `SpeedSlackYards` and nothing is scored) nor the latency slack added again for every packet. The yards a player may still have covered stand in a bucket that the allowed speed × (1 + tolerance) refills by the time that passed (the REAL receive interval only: no client stamp buys any refill, so stamps alternating far ahead and behind refill nothing but real time). The refill stops at a ceiling of `SpeedSlackYards` plus that speed over the latency slack or, after a genuine stall, that speed over the real silence (at most four times `BaselineGapMs`, 12 s by default, so an honest stall of that length scores nothing; a longer silence is credited only that much, and an hour of held packets buys 12 s of movement, not an hour); a balance above the ceiling never grows. The bucket starts full, and every judged step spends its horizontal distance; an overdraft is a Speed finding ("over several packets", weight 5 + the overdraft, at most 25) and the bucket restarts empty. It restarts on every re-baseline, a knockback, a known transport and a step already scored. Sustained travel is therefore capped at the allowed speed whatever the stamps say (the stamps alternating +500 / -500 ms at 50 packets per second, which no single step scores, overdraw the bucket within a few packets: `AStreamOfStampsAlternatingFiveHundredAheadAndBehind_ScoresSpeed_WhateverTheStampsClaim`); a client clock that runs ahead by less than `MaxGapMs` is the independent clock's finding (a pair further ahead is discarded there, and gains no distance, because the bucket still holds).

The allowed speed is the highest of the player's five speeds and of every speed change still waiting for its ack. When it
drops (a speed decrease acknowledged, a mount gone), the previous speed still counts for `SpeedChangeGraceMs` plus the latency
slack, so packets sent before the ack are never judged by the new speed.

**Re-baselines** (the next packet is trusted, the clock window forgotten): the first packet; a server relocation (the
`TeleportService` events, and any time the stored position is not where the last judged packet left it: a charge, a
graveyard, the end of a taxi flight); a knockback acknowledgement (no speed check until the landing or six seconds). A silence, however long (holding packets, AFK), and a time skip are **not**
re-baselines: the step after them is judged against the time that passed, so holding packets and then jumping buys only what the
silence covers (the client's own elapsed time, capped by the receive interval plus the latency slack, and never more than `BaselineGapMs` of movement in one step: an hour of held packets buys no more than three seconds of run speed).

**Exempt then checked again**: the blocks of an exempt player (a GM leaving GM mode) are stored without being judged, so the first judged block afterwards is a new baseline (`AntiCheatFeature.ForgetBaselineWhileExempt`); the step would otherwise be measured from the last position judged before the exemption.

**Other paths that store a client block**: `CMSG_MOVE_NOT_ACTIVE_MOVER` is judged, because a block stored unjudged would be
laundered by the next packet's re-baseline (the stored position no longer where the last judged packet left it). The
sender's reject window and the moved player's taxi flight refuse the block for every unit, then the checks run (vmangos
`HandleMoveNotActiveMoverOpcode`, MovementHandler.cpp:922-924, :937, :948). The solicited acknowledgements (speed, flag, root, knockback, turn rate) are judged: the block of an honoured ack goes through the same checks as a movement packet before it is stored (`AntiCheatFeature.JudgeAcknowledgedMovement`; vmangos runs the cheat tests on them, MovementHandler.cpp:489-494). The flag the acknowledged order itself switches on is granted for that block (the order is no longer pending). Only the unroot ack is exempt from the rooted-while-moving check (vmangos MovementAnticheat.cpp:777-780); a root, speed, flag or turn-rate ack under a root that carries a moving block scores Physics. An ack also feeds the movement clock, burst and client-time checks, beside its own timestamp tracking (`NotifyAcknowledgement`), so an ack stamped back more than `ClientTimeRegressionMs` scores one finding, not two: the block it carries is not scored again for the same stamp (it still is when no ack finding preceded it). **Not delivered: the position of a knockback ack is not judged.** `OnAcknowledgement(knockback: true)` re-baselines the position checks before the block is judged (`NotifyKnockback` trusts the next block), so a server knockback lets that one ack carry any position unscored (its flags are still checked); vmangos does not distance-check a spell knockback either (speed check skipped for the opcode, teleport check off while launched), and the knockback window itself lasts until the landing or six seconds. The acks are also not held to the reject window (`ShouldRejectMovement`) that movement packets obey, and the session trace shadow does not see ack blocks.

**A unit the player controls** (a possessed creature or player, moved by `MSG_MOVE_*` or given up by
`CMSG_MOVE_NOT_ACTIVE_MOVER`) has its own checks on the controller's state: the unit's position, speeds and auras (a creature
may also keep the hover, water walk, slow fall, levitate and fly flags the server already holds for it; a possessed player is
judged on its own flags, root, death state and granted flags like any player), a fresh baseline for
each new unit. The baseline is the position the server holds for the unit, never the client's first block: a first block any
distance beyond `TeleportDistance` from the unit, and a block after the server moved the unit, is a teleport finding.
The terrain checks run for a controlled unit on its own map, a time skip the client reports naming the possessed mover (`CMSG_MOVE_TIME_SKIPPED` carries the mover's GUID) is judged against that unit's checks (`OnControlledTimeSkipped`; a skip naming the controller's own unit reaches only the controller's checks, and one naming a unit no longer controlled is ignored), and there is no knockback exemption: a server knockback or any move of the unit is a relocation through the stored position (`SeedFromServerPosition`). Creatures are never knocked back, so only a possessed player matters; the order goes to that player's own session, the controller's ack naming another mover is refused, and when the victim acknowledges, the moved stored position re-seeds the unit's checks, so the controller's next block is measured from there with only `SpeedSlackYards` of slack (a short step is fine, a long one is scored as speed). A controller that is exempt while it moves the unit (GM mode) leaves the unit's baseline forgotten, as for its own unit, so the first block after the exemption is a fresh baseline. Findings score the controller, whose client sent the block, with the detail "controlled unit N: ..."; the
controller's exemption applies, and a controlled finding never rubberbands (the rubberband target is the controller's own).
vmangos runs the controller's cheat tests on a possessed player and none on a creature (MovementHandler.cpp:318-325); here
both are scored and neither is refused, so a creature's block is still stored as in vmangos. A creature's root is not judged
(only a player acknowledges a root order). A creature aboard a ship is judged against the same transport claim as a player.

**Time skips** (`CMSG_MOVE_TIME_SKIPPED`) buy no baseline and blind no clock check. The skip the evidence supports is the lag,
capped by `MaxTimeSkipMs` and by the arrival silence before the skip (since the previous in-world packet the world handler
saw, or the last packet the checks saw, whichever is later; no latency slack is added), and all the supported skips of one
ten seconds together by `BaselineGapMs`. The step that spans a skip is judged against the client's own elapsed time plus that
supported skip, capped by the receive interval alone (no latency slack), so a skip never buys more than real elapsed time and a forged skip at heartbeat cadence gains nothing over the no-skip budget, and a blink right after
a skip is still a teleport (and never the rubberband target). The independent clock keeps its window and is credited at most
250 ms per ten seconds across all skips (jitter: an honest stall already shows in the receive side of the pair it spans), so a
skip cadence under the spam threshold cannot neutralise a fast clock. The silence can read longer than it was: `CMSG_PING`, a
cancelled trade and dropped packets are not stamped. The movement and acknowledgement timestamp checks keep running with no
grace: a stalled client's clock only moves forward. A zero or impossible skip adds no budget or credit, but still counts toward spam. Not yet scored, pending a
real-client capture of a stall (see [Open questions](#open-questions)): a client clock that does not reflect the skip, and a skip
whose clock already includes the silence is not helped by a skip.

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
* **Staff lines for kicks and autobans** go to staff online directly (not through World:Announce), at most once per account and kind (kick, autoban, autoban failure) per `StaffNoticeCooldownSeconds` (default 30, 0 off); repeats are counted and reported as one `N more suppressed in the last 30 s` line.

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
reconnect at once). `AntiCheat:SpellHoneypot` (a deviation, off by default) adds the strong signal for casts no client can send; its
`ToolSpellIds` list names GM-tool spell ids, empty by default. The `Net:Protection:World*` packet budgets are restart-only.

## Tests

* `Game.Tests/AntiCheat/SpeedHackDetectorTests` (the fork's six gateway cases ported to xunit, plus the 32-bit wrap and the
  switch), `MovementAntiCheatTests` (every check positive and its look-alikes negative: lag bunching, knockback, speed
  decrease and pending increase, server relocation, known/unknown/fake transports, dead players, grants, terrain with and
  without data; time skips: a blink after a 0 or 1 ms skip, ten cheap skips each followed by a blink, an honest freeze with and
  without the clock jump, a fast clock with a cheap skip every 4 s, a 1.5x clock with 250 ms skips at the spam cadence, a 1 ms skip at 3x run speed, a forged skip with no silence, `uint.MaxValue`, the
  receive-clock wrap, an acknowledgement going back after a skip), `AntiCheatScoresTests` (decay, ceiling, autoban points and ladder, validation).
* `Game.Tests/AntiCheat/CastSpellHoneypotTests` (flag off: no signal; flag on: a non-GM gets the signal, a GM does not; the trap
  categories and the tool id list; validation).
* `Kernel.Tests/Net/OpcodeRateLimiterTests` (the fork's test_gateway_rate cases, a retail cadence under the defaults).
* `World.Tests/AntiCheat/AntiCheatWorldTests` through real sockets: a legitimate client running, jumping and stopping
  scores nothing (and the same client's blink is scored), a managed playerbot is exempt (and scored with the exemption off),
  staff and GM mode are exempt, the GM alert reaches the staff online, the rubberband teleports back, the kick disconnects and
  the autoban writes a 7-day ban after an earlier one, the log coalesces into one row written in one batch, the ping feeds
  the latency average, and the commands. `AntiCheatConfigurationTests` (startup check, `.reload config`),
  `Net/PacketBudgetTests` (drop and flood through a real session). `MovementBypassTests`: a `CMSG_MOVE_NOT_ACTIVE_MOVER`
  naming the player itself is scored every time `CMSG_SET_ACTIVE_MOVER` re-arms it and is ignored on a taxi and inside the
  reject window; a possessed creature's and a possessed player's blink is scored against the controller, not the victim.

## Open questions

* World tests possess units through `CharmService.ModPossess(..., spell: null)`. That argument is the casting spell (`SpellInfo?`), not the binding: the charm service is bound to the spell system by `CharmService.Install`, which `PetsFeature` calls through `SummonService` whenever `SpellFeature` exists, and `ModPossess` returns without effect when it was never installed. The mover change those tests assert proves it is bound in the host. Only the charm spell's own aura data (the spell bar a real spell grants) is not exercised by them.
* The thresholds are the fork's and vmangos' and have not been tuned against real 1.12 client traffic; the log-only default
  is there to collect that data before anything acts.
* Elevator and tram transports are judged by their game object and its animated position only; ArcaneCore does not move players with them (the client's world position aboard is kept).
* What a 1.12.1 client does during a stall has no capture yet: whether `CMSG_MOVE_TIME_SKIPPED` carries the wall-clock length
  of the stall, whether the client clock jumps by it, and whether the client advances movement meanwhile. Until one exists,
  the step after a skip is bounded by the client's elapsed time plus the silence-supported skip, and the skip-specific evidence
  (no matching silence, a clock that did not jump) is not scored.
* `MOVEFLAG_LEVITATING` on a player is treated as a hack as vmangos does; a real client capture of priest Levitate (1.12)
  should confirm the client never sets it.
