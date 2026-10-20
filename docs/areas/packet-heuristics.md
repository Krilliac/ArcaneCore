# Packet heuristics: report-only analysis of what sessions send (lane `packet-heuristics`)

Heuristic checks over the packets sessions send to the server. Each finding goes to the anomaly ledger (subsystem `packet`, code =
the check name) and to a per-check counter; nothing else happens. **A packet is never dropped, changed, delayed, answered
differently, kicked or banned because of a finding, and no finding feeds the [AntiCheat](anticheat.md) score or escalation.** A
packet that dispatched before still dispatches and one that was dropped is still dropped, identically. The feature is **off by
default** (`World:PacketHeuristics:Enabled` = false); `.packetcheck on` switches it on at runtime.

It exists to answer one question on a live server: with real clients and the playerbots on, what do the sessions send that no 1.12.1
client builds, and is it a cheat, a bot bug or a server bug.

## Where it hooks

| Traffic | Hook | What it sees |
|---|---|---|
| Client (socket, gateway, packet-tool injection) | `WorldSession.DispatchAsync` (`HeuristicsClientPacket`, `WorldSession.cs`, before the dispatcher decides anything) | shape, cadence, replay on every authenticated packet |
| Client dispatcher outcomes | the drop sites in `DispatchAsync` and `ProcessWorldPackets` | `wrong-state`, `unknown-opcode`, `malformed` (reported before the disconnect or kick) |
| Client world packet about to run | `ProcessWorldPackets` on the world thread | `incapacitated-action` |
| Playerbot (managed session) | `ManagedWorldSession.TryManagedAction` | shape, cadence, replay and `incapacitated-action` when the action reaches its handler; `wrong-state`, `unknown-opcode` and `malformed` when the action is refused or its handler throws (an exception filter that always returns false, so the exception continues exactly as before) |
| Acknowledgement handlers | `SpeedAckHandlers`, `KnockbackAckHandler`, `RootAckHandler`, `FlagAckHandlers`, `TeleportHandlers` | `orphan-ack`, at the point where the handler already ignores the packet |

The glue is `Net/WorldSession.PacketHeuristics.cs`; the checks are in `AntiCheat/PacketHeuristics/`. Bots bypass `DispatchAsync`, so the
managed hook is what makes bot traffic visible at all. A chaos-engine probe a bot dispatches on purpose is never checked.

## Switching it on and off

Off: a packet costs one volatile read and no per-session state exists (a test pushes a burst of bad packets and asserts zero findings and
no state). **Exemptions**: managed bots (`ExemptBots`, default true) and staff (`ExemptStaff`, default true: Moderator and above, or a
player in GM mode) are not checked at all. With an exemption lifted their traffic is checked and counted under its own column (player,
bot, staff) of the status table. The bot sessions are almost all the traffic on this server, so the usual way to use it here is
`.packetcheck on` and `.packetcheck bots on`.

`.packetcheck status | on | off | bots on|off | staff on|off | reset` (Administrator; works from chat, the console and RA). A change lasts
until the next restart, which returns to `World:PacketHeuristics`. `status` prints whether the checks run, who is exempt and the
per-check counts. `reset` zeroes the counters and lets every session report again at once. `off` keeps the counters and the ledger.

## Findings and the throttle

Subject `account N` (player, staff) or `bot NAME` (managed bot). The subsystem is `packet`, never `bot-...`, which would auto-file bot QA
issues: triage here is manual. Detail = opcode name plus specifics (`Cast class 14 within a second > 10/s`, `trailing 3 bytes (length 11,
the message is exactly 8)`, `state CharacterSelect`). At most one ledger report per session, check and opcode per
`ReportIntervalSeconds` (default 10); every occurrence is still counted and the next report says how many were held back. The detail is
formatted only when a report is actually made, so a flood costs no allocation.

## The checks

| Code | Severity | Detects | False-positive risks |
|---|---|---|---|
| `wrong-state` | Warning | an opcode dropped because the session state does not allow it: a world opcode at the character screen, a session opcode in a disallowed state, a world packet dequeued after the session left the world or while the player has no map | packets in flight when a player logs out: none are reported for `WrongStateGraceMs` (5 s) after the session left the world, nor once the session is closing or kicked. A bot's refused action is the same check |
| `unknown-opcode` | Info (named but unhandled), Warning (outside the opcode table, or a server opcode sent by a client) | an opcode with no handler | a client feature the server does not implement yet: Info is expected noise |
| `malformed` | Error | a payload a handler rejected as truncated or invalid, a bad frame header, a `CMSG_PING` without its sequence number, a bot payload over the frame limit | none expected; a real client never sends these |
| `shape` | Warning | a payload whose length differs from the fixed length of its message (trailing or missing bytes) | a modified client or a different build with another layout; see the section below |
| `cadence` | Warning | an opcode class above its ceiling in any sliding second (`CadenceLimits`), and a `CMSG_PING` closer than `PingMinIntervalMs` to the previous one (`overspeed-ping`) | `QueryPerSecond`: a client entering a crowded city bursts queries (raise it if that is all you see); casts and uses are limited by the global cooldown, so 10/s is far above what a client produces |
| `replay` | Warning | the same opcode with the same payload at least `ReplayCount` (5) times within `ReplayWindowMs` (1000) | an opcode that legitimately repeats an identical payload and is not in the exclusion list below |
| `orphan-ack` | Warning | a speed, knockback, root, flag or teleport acknowledgement that matches nothing the server sent, or names another mover | a late acknowledgement after a second change superseded the first; a server that dropped its own pending record (a server bug, which is the point) |
| `incapacitated-action` | Info for a bot, Warning for a player | an action received while the player is dead or a ghost, stunned, on a taxi flight or being teleported; reported even when the handler refuses | a packet sent before the client learned of the state change (latency): expect a few right after a stun or a death |

### Shape: a length table, not the oracle

The wire oracle (`tests/ThirdParty/WowWorldMessages`, the vanilla decoder `tools/ArcaneCore.Sniff/SniffDecoder.cs` uses) is a test
dependency: 899 generated source files and an async stream decoder that allocates a message object per packet. The world assembly must
not reference it and the hot path must not allocate. So `PacketShapeTable` lists the **fixed-length** messages only (205 rows): exactly
the messages whose layout the oracle declares as a constant number of bytes. Variable layouts (strings, arrays, packed GUIDs, movement
blocks, optional parts) are never shape-checked. The table is pinned to the oracle by `PacketShapeTableTests`: every row equals the
oracle's literal header size minus the 4 header bytes, no fixed oracle message is missing, and the oracle's own reader decodes exactly
that many zero bytes and fails one byte short. Lookup is one array read.

### Cadence classes

| Class | Opcodes | Default ceiling |
|---|---|---|
| Cast | `CMSG_CAST_SPELL`, `CMSG_PET_CAST_SPELL` | 10/s |
| Use | `CMSG_USE_ITEM`, `CMSG_GAMEOBJ_USE`, `CMSG_LOOT` | 10/s |
| Query | every client opcode whose name contains `QUERY` | 50/s |
| Emote | `CMSG_EMOTE`, `CMSG_TEXT_EMOTE` | 5/s |
| Ping | `CMSG_PING`: not a window, a minimum gap (a 1.12.1 client pings every 30 s) | 5000 ms |

A ceiling of 0 switches that class off.

### Replay exclusions

The replay check skips the opcodes in the built-in list (`ReplayBuiltInExclusions`, default true): `MSG_MOVE_*`, `CMSG_MOVE_*` and
`CMSG_FORCE_*` (movement and its acknowledgements repeat while standing still), `CMSG_SET_ACTIVE_MOVER`, `CMSG_PING`, every `*QUERY*`
(idempotent polling), `CMSG_SET_SELECTION`, `CMSG_ATTACKSWING`, `CMSG_ATTACKSTOP`, `CMSG_CANCEL_*`, `CMSG_LOGOUT_CANCEL`,
`CMSG_STANDSTATECHANGE`, `CMSG_LOOT_RELEASE`, `CMSG_AREATRIGGER`, `CMSG_ZONEUPDATE`, `CMSG_REQUEST_PARTY_MEMBER_STATS`,
`CMSG_CHAR_ENUM`, `CMSG_BATTLEFIELD_*`, `*ACCOUNT_DATA*` and `CMSG_PLAYED_TIME`. These are what the 1.12.1 client and the playerbots
resend by design. `ReplayExclude` adds more patterns (an exact opcode name, or a name with a leading and/or trailing `*`); a value must
parse and match an opcode or the server refuses to start. Because the configuration binder appends a configured array to the default
one, the default of `ReplayExclude` is empty and the built-in list is a separate switch: set `ReplayBuiltInExclusions` to false to take
full control.

### Incapacitated actions

What each state forbids is grounded in the handlers and in what the 1.12.1 client does. A **ghost** may still release, reclaim, talk to a
spirit healer (`CMSG_GOSSIP_HELLO` opens one), chat, move, rearrange its bags and cast Reincarnation, so those are not listed; the dead
list is use item, loot, auto-store loot, trade, auto-equip, attack swing, buy and sell. **Stunned**: cast and use item (the client refuses
both). **Taxi flight**: cast, use, game object use, loot, trade, attack, gossip and quest-giver hello, buy, sell, auto-equip.
**Teleporting** (between the order and its acknowledgement): cast, use, game object use, loot, trade.

## Options (`World:PacketHeuristics`)

The keys, defaults and ranges are in the generated [configuration reference](../reference/configuration.md) and `world.conf.dist`. Every
value is validated at startup (`check-config`, exit 78 on a bad value) and again when the command rebuilds the settings; the live
settings are an immutable snapshot swapped by reference, so a check never sees half a change.

## Cost

Measured by `PacketHeuristicsPerformanceTests` on 1500 real `WorldSession` objects with an in-test clock (legitimate mixed traffic, one
packet per session per simulated 100 ms tick): the number is in the test output (`PERF` line). The steady state allocates nothing
(asserted). Per-session state is created lazily on the first checked packet and guarded by a small per-session lock; the shared counters
are `Interlocked`.

## Not in v1 (future work)

Movement-clock long-term drift (the [SpeedHackDetector](anticheat.md) covers part of it), feeding findings into the AntiCheat score or
escalation, possessed-unit checks, and the checks that were dropped as infeasible or noisy: a teleport-counter comparison beyond the
handler's own (the counter is only known to the handler), and enum-range checks (they need the variable layouts, which the shape table
deliberately does not model).
