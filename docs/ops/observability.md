# Observability: client rejections, duplicate packets, slow-tick attribution

Added on branch `claude/w33-observability`. Three log features for the failures that used to leave no trace: a refusal the
server sent a client (or a request it dropped without answering), an identical packet sent twice, and a slow world tick the
logs could not attribute. Logs are private, so lines may carry names. The refusal and silent-drop lines carry GUID low, map
id, entry and spell ids only; the duplicate-packet line also carries the **account id** and a few call-stack frames
(type and method names). None of them carries a key, a password or an SRP value, so a line pasted into a bot-QA issue is safe.

## Categories and keys

| Logger category | Level | What |
|---|---|---|
| `ArcaneCore.ClientRejections` | Information (player), Debug (bot) | One structured line per refusal sent to a client, and per handler that dropped a request without answering. |
| `ArcaneCore.DuplicatePackets` | Warning | An identical (opcode, payload) sent twice to one real player within the window. |
| `ArcaneCore.Game.Maps.WorldRuntime` (existing) | Warning, event `Perf` | "Slow map update" now ends with `top:`, `gc ...` and `host: ...`. |
| `ArcaneCore.Kernel.Ops.Watchdog.TickMonitor` (existing) | Critical / Warning | Hang and overrun lines now end with `host: ...`. |

Filter with the standard `Logging:LogLevel:ArcaneCore.ClientRejections` (turn it to `Debug` to see bot refusals too).

| Key | Default | Meaning |
|---|---|---|
| `Ops:Observability:ClientRejections:Enabled` | `true` | Decode and log refusals; off means the send path does no extra work. |
| `Ops:Observability:ClientRejections:BurstPerSession` | `20` | Per-session token bucket: lines in a burst. |
| `Ops:Observability:ClientRejections:RefillPerMinute` | `60` | Lines a session earns back per minute; refused lines are counted and reported as `suppressed` on the next line. |
| `Ops:Observability:ClientRejections:LogSilentDrops` | `true` | Handlers that return without a reply log the reason. |
| `Ops:Observability:DuplicatePackets:Enabled` | `true` | Hash every checked outbound packet. |
| `Ops:Observability:DuplicatePackets:IncludeBots` | `false` | Bots are excluded by default. |
| `Ops:Observability:DuplicatePackets:WindowMs` | `100` | Two identical packets closer than this are a duplicate (a tick is 50 ms). |
| `Ops:Observability:DuplicatePackets:RingSize` | `16` | Recent sends remembered per session. |
| `Ops:Observability:DuplicatePackets:WarnBurstPerSession` / `WarnPerMinute` | `3` / `6` | Warning bucket per session. |
| `Ops:Observability:DuplicatePackets:CaptureStacks` | `true` | Include compact call stacks (see below). |
| `Ops:Watchdog:HostLoad:Enabled` | `true` | Sample the host once per watchdog check. |
| `Ops:Watchdog:HostLoad:HistorySeconds` | `120` | Samples kept to difference against. |

All of `Ops:Observability` is restart-only. Out-of-range values are clamped.

## Client rejections

`WorldSession.Send` asks `ClientRejectionDecoder` (Game, `ClientErrors/ClientRejection.cs`) whether the opcode can be a refusal (one
table load) and, for those that can, reads the reason out of the payload. Hooking `Send` covers every call site without
editing them. Decoded: `SMSG_CAST_RESULT` / `SMSG_PET_CAST_FAILED` (failure status), `SMSG_INVENTORY_CHANGE_FAILURE`, the
error form of `SMSG_LOOT_RESPONSE`, `SMSG_BUY_FAILED`, `SMSG_SELL_ITEM`, `SMSG_QUESTGIVER_QUEST_INVALID` / `_FAILED`,
`SMSG_QUESTUPDATE_FAILED(TIMER)`, failure statuses of `SMSG_TRADE_STATUS`, `SMSG_TRAINER_BUY_FAILED`, non-OK
`SMSG_PARTY_COMMAND_RESULT`, `SMSG_AUCTION_COMMAND_RESULT`, `SMSG_SEND_MAIL_RESULT`, `SMSG_PET_TAME_FAILURE`,
`SMSG_READ_ITEM_FAILED`. A line:

```text
client rejected: CastResult BadTargets (opcode SMSG_CAST_RESULT) character 1234 map 1 target 0 entry 6478 arg 0 (suppressed 0)
```

`entry` is the spell, item or quest id; `target` the vendor, loot source or item GUID; `arg` an extra number (second item
low, trade or party operation).

**Silent drops.** A handler that returns without answering is a different problem: nothing is sent, so `Send` sees nothing.
`WorldSession.LogSilentDrop(opcode, reason, target, entry, arg)` logs `request dropped without a reply: <reason>`; handlers
compare `ClientRejectionsSent` before and after the call so a refusal that *was* sent is not logged twice. Instrumented:
`CMSG_CAST_SPELL` (unknown spell, not known, any non-OK result with no reply), `CMSG_GAMEOBJ_USE` (short payload, no map or
gameobject system, any non-OK use result with no reply), `CMSG_LOOT` / `CMSG_AUTOSTORE_LOOT_ITEM` / `CMSG_LOOT_RELEASE`
(short payload, no loot service for the map, refused open).

Cost: disabled, one null check. Enabled and not a candidate opcode, one table load. A candidate opcode costs a few integer
reads; formatting happens only after the logger level check and a token from the session bucket.

## Duplicate outbound packets

Per session a ring of `(opcode, hash, length, time)`; each checked send costs one `HashCode` pass over the payload and a scan
of the ring under a per-session lock, with no allocation. Opcodes that legitimately repeat are never checked: all `MSG_*`,
`SMSG_MONSTER_MOVE`, `SMSG_MOVE_*`, `SMSG_FORCE_*`, `SMSG_SPLINE_*`, `*_QUERY_RESPONSE`, `SMSG_UPDATE_OBJECT` (plain and
compressed), `SMSG_DESTROY_OBJECT`, `SMSG_PONG`, time and clock packets, chat and emotes, combat and spell logs, cooldowns,
sounds, world states, gossip and list windows (see `DuplicatePacketDetector.BuildAllowList`). Bots are excluded unless
`IncludeBots`.

The first send of a pair has no stack: capturing one per send would cost far more than the hash. The second send's compact
stack is captured when the duplicate is found, and the opcode is then *armed*: later sends of it keep their stack in the ring
(at most 50 captures per second process-wide), so the next report of that opcode carries **both** stacks. The first report of
an opcode says `first send: (not captured: ...)`.

A duplicate is a hint, not proof: two identical stack pushes in one tick can be legitimate. The line is rate limited per session.

## Slow-tick attribution

`HostLoadSampler` (Kernel, a watchdog monitor) stores one cumulative reading per watchdog check: process CPU
(`Environment.CpuUsage`), machine CPU (`GetSystemTimes` on Windows, `/proc/stat` on Linux; unknown elsewhere), GC collection
counts and total pause (`GC.GetTotalPauseDuration`), thread-pool thread count and pending items. A hang, overrun or slow-map
line differences a fresh reading against the newest stored one at least as old as the slow window:

```text
host: process cpu 4% / machine cpu 100% (other processes 96%) over 3.1 s on 24 cpus; gc +0/+0/+0 (gen0/1/2) pause 0 ms; threadpool 24 threads, 0 pending; verdict host-starved (other processes are using the machine)
```

Verdicts: `gc-pauses` (pauses cover 30% or more of the window), `gc-paging` (a `gc-pauses` window where the pause covers half or more of it, this process
used half a core or less of cpu over the window, and the machine memory load, from the GC's last collection, is at or above its high-memory threshold, or 90% when the runtime reports none:
the collector was compacting paged-out heap, so free host memory; the line adds `machine memory N% (gc high-memory threshold M%)`; the fatal hang action and bot QA skip it like `gc-pauses`), `process-stalled` (the watchdog thread itself did not run for 1 s or more
beyond its check interval and beyond the GC pauses, covering 30% or more of the window: the whole process was stopped by paging on a
machine short of memory, a native debugger holding a debug event or a suspension, not the code; the line adds `watchdog stalled N ms`;
a starved machine makes the watchdog late too, so `host-starved` wins over it; the tick-hang fatal action and bot QA skip it like `gc-pauses`), `host-starved` (machine at 85% or more with other processes at
40% or more), `process-busy` (this process uses 50% or more of the machine), `memory-starved` (a window of 1 s or more where this process used half a core or less while the machine memory load was at or above the GC's high-memory threshold: its threads waited on paging; the fatal hang action defers for it, but fires anyway once the hang lasts more than 3 times `ActionHangMs`, because an idle process on a memory-loaded host is also what a deadlock looks like; bot QA still gets the hang, with `|memory-starved` on its work token), `process-blocked` (the same idle process with no memory pressure: the thread was waiting on a lock, I/O or a call, so it is not called host-quiet; it is still reported), `host-quiet` (neither: the code was slow by
itself), `unknown` (window under 100 ms or no machine reading). The printed window is the real span between the two readings
(up to one check interval longer than the slow window).

The "Slow map update" line gains `top: <three largest sub-phases>` and `gc <pause> (gen0 +n, ...)` for that map's update alone.
Sub-phases come from the existing `MapUpdateDiagnostics` seams: session packets (bot packets included), player heartbeats
(timed per player with a `Stopwatch` pair, only while the slow-map warning is on), creature AI and unit heartbeats (AI timers,
auras and regeneration run in a unit's heartbeat; the remainder of the heartbeat phase), each map updater by subsystem
(creature AI, movement, collision and pathing, combat, pets, ...), visibility and relocation, values, flush, grid lifecycle,
deferred work. The terrain clean-up is not map work either: it runs once per tick for the whole world after the maps (one map
id's terrain serves all its instances) and counts in the tick's maps phase. Playerbot *brains* are world features, not map work:
they appear in the hang line's `top features` list, not here.

## Silent catches

Catch blocks that swallowed an exception without a log now carry a one-line reason (cancellation and shutdown, fail-closed
regex timeouts in the name catalog, best-effort fallbacks). The ones that cannot log (the logging subsystem itself, static
helpers with no logger) say so.

Swallowed catches that carry no logger (static helpers, stores, the ban cache, the planner and gear-score seams, the packet
capture, the hot-code audit write, the word filter, the name catalog) now count the exception in `FaultLedger.Shared` under a
site name (`bans.effective-read`, `realm.ipban-reload`, `net.packet-capture`, ...). Control flow is unchanged.

## Diagnostics naming and the shared ledger core

A fault site is written `subsystem.code` (`writequeue.character-retry`, `instance.manager`); a site built at run time may use
`subsystem:detail` or `subsystem/detail`. An anomaly passes its subsystem separately. `DiagnosticSubsystem` (Kernel) is the one rule that
turns either into a canonical subsystem: the part before the first `.`, `:` or `/` (older dash-only sites are cut at the first `-`), with
variant spellings folded together (`instances` -> `instance`, `outdoorpvp` -> `outdoor-pvp`, `bg` -> `battleground`, `worldtick` -> `world`).
`FaultLedger.BySubsystem`, `AnomalyLedger.BySubsystem`, `.server faults summary` and the self-diagnosis report all group by it, and
`.server faults summary` ends with one line per subsystem that carries its faults and its anomalies together (`DiagnosticOverview`).
Use a canonical subsystem name for a new site or anomaly.

`FaultLedger` and `AnomalyLedger` keep their per-key counts, timing, tallies and log throttle in one shared `LedgerEntryCore`. Both, and
`Invariant`, expose an `Epoch` that changes on reset; `DiagnosticDeltaTracker` turns repeated snapshots into per-key deltas and starts every
key over on a new epoch, which is how bot QA reads faults, invariant failures and anomalies (occurrences after a `.server faults reset` are no
longer lost when a key grows past its old count before the next QA pass). Invariant failures carry first and last seen.

## Skipped navmesh tiles

`NavRegionGraph.Build` lists every `.mmtile` it could not read in `SkippedTiles`. The pathfinder writes one Warning per
tile (tile coordinates and file, at most 20 lines then a summary) and counts each in the fault ledger under
`mmaps.regiongraph`. The live mesh load only reports a tile when a path query loads it lazily, so this is the first report
of a corrupt tile no query has needed yet.
