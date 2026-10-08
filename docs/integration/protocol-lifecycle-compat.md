# Protocol/lifecycle compatibility

Source `434968a555bfbf15fb4bc6156e34ae277baee572` on
`codex/protocol-lifecycle-compat-20261003` follows qualified canonical `462d8f3`.
Full source CI passed before preserving canonical merge
`a656e61ceeae287358f2fe327f59fdd0991d29de`. Final canonical head and its exact-head CI are recorded on
[draft #11](https://github.com/Krilliac/ArcaneCore/pull/11).

This tranche registers two inactive queue polls and makes world listener shutdown
retain ownership of accepted sessions and their asynchronous scopes. It adds
automated logout/restart persistence evidence. It does not claim that earlier
databases were corrupted, implement queue membership, or complete real-client
logout/relog/restart acceptance.

## Inactive queue contract

Both handlers are discovered through `IOpcodeHandlerGroup` and registered with
`OnWorld`: their bodies run on the authoritative world/map thread for the current
in-world player. Pre-authentication polls disconnect. Character-select/logging-in
states cannot run them; character-screen requests are dropped rather than replayed
into a later login. Exact session/player ownership prevents foreign-player dispatch.

| Request | Required body | Inactive result | State effect |
|---|---|---|---|
| `CMSG_BATTLEFIELD_STATUS`, decimal **723** | Zero bytes | Intentional **no response**: no occupied battleground queue exists to report. | No queue, player, journal or group mutation. |
| `CMSG_MEETINGSTONE_INFO`, decimal **662** | Zero bytes | `SMSG_MEETINGSTONE_SETQUEUE`, decimal **661**, exact five-byte body **`0000000005`**: little-endian u32 area 0, u8 NONE 5. | No join/leave action; existing ordinary group remains intact. |

The silent battleground result follows the pinned handler's occupied-queue loop:
without queue entries, there is no status packet. [vmangos BattleGroundHandler.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Handlers/BattleGroundHandler.cpp)
The meeting-stone idle result and NONE **5**, distinct from LEAVE_QUEUE **0**, are
grounded in the pinned queue/enum definitions. [LFGQueue.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/LFG/LFGQueue.cpp),
[LFGDefines.h](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/LFG/LFGDefines.h)
The source also records comparison with pinned
[LFGHandler.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/LFG/LFGHandler.cpp),
[Misc.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Server/Packets/Misc.cpp),
and [gtker/wow_messages at 70abb9d](https://github.com/gtker/wow_messages/tree/70abb9deff0bb63440d8aeb4386b820653e8a176),
including the battlefield-status and meetingstone `.wowm` definitions.

ArcaneCore deliberately requires **exactly zero body bytes** for these requests.
An in-world surplus body disconnects through its existing malformed-packet path.
This is stricter than the upstream `NullClientPacket` reader, which reads no body
fields without enforcing this exact length. It is an explicit validation policy,
not a claim that upstream rejects those bytes. The independent mock uses numeric
opcodes and a literal response, rather than a shared server response encoder.

Battleground join/port remain unregistered; replace the inactive battleground
result only when authoritative queue state is implemented. Meeting-stone
join/leave are registered by the meeting-stone queue (`MeetingStoneHandlers`,
[gameobject-types.md](gameobject-types.md)), and `CMSG_MEETINGSTONE_INFO` moved
there with the same zero-byte body rule: the row above is still its answer for a
player or party that is not queued, while a queued player or party gets its
queue status instead. An ordinary two-player party is not an LFG queue: its
identity, leader, membership and raid flag remain unchanged by a meeting-stone
info query.

## Listener shutdown ownership

`WorldServer.ExecuteAsync` retains accepted session tasks, prunes completed tasks
as further clients arrive, and supplies a linked session cancellation token. Its
`finally` stops the listener, cancels that token, and awaits every retained session
task. This cleanup also runs if the accept loop faults without normal host cancellation.

`WorldServer.StopAsync` calls the normal background-service stop, then awaits
`ExecuteTask` in `finally`, even when the host's stop budget is already canceled.
Accepted session handlers and asynchronous scope disposal therefore finish before
listener stop completes and `WorldHost` performs its final world/storage drain.
World cleanup posted by scope disposal remains owned while the world can process it.

Cancellation is cooperative. A login/database operation or scope disposal that
ignores cancellation can extend shutdown beyond the host's requested timeout.
This change prioritizes completing owned session work; it does not promise a
hard maximum shutdown duration or kill underlying I/O when a wait budget expires.

## Automated proof matrix

| Boundary | Evidence | Proven result / limit |
|---|---|---|
| Handler discovery/state policy | `InactiveQueueHandlerTests` | Both polls are in-world handlers; queue membership actions remain absent; character-screen requests do not leak into login. |
| Exact payload/ownership | World and independent mock queue tests | Surplus bodies disconnect; pre-authentication polls disconnect; a queued request cannot execute against another session's player. |
| Quiet battleground poll | Independent encrypted mock, repeated three times | No queue notification through an ordered real map NPC-status response; normal map operations and exact ping echo remain usable. Ping alone is not the map-dispatch barrier. |
| Meeting-stone idle and ordinary group | Independent literal mock plus World two-player party test | Exact opcode 661/body `0000000005`; no extra queue notification, player/journal mutation or ordinary group change. |
| Normal/canceled host shutdown | Two `WorldServerShutdownTests` cases | Stop stays incomplete while an actual login read ignores cancellation, then while async scope disposal is held; session closes, registry clears and posted cleanup precedes the world's final drain. |
| Existing ordinary logout timer | Retained Game M6 tests | Default 20-second boundary, cancel behavior and uint millisecond wrap cases remain covered. The new socket harness shortens its configured delay; it does not replace default-timer proof. |
| Logout followed by fresh session | `WorldLifecyclePersistenceTests`, owned 45-second SQLite/SRP harness | A real core Before save is held after countdown logout. A fully authenticated new session waits for that save, performs a fresh row read, and loads a replacement Player with exact saved identity/position/action bar. |
| Cold world reload | Same persistent harness | Normal `WorldHost.StopAsync` captures online state and drains saves; a new service provider/runtime reloads the same SQLite files and exact identity/position/action bar. This is automated server evidence, not client UI acceptance. |

**13 new World + 8 new mock cases** passed. Three expected baseline
failures demonstrated missing handler discovery and premature normal/canceled
listener stop. They establish regression sensitivity, not an observed history of
corrupted production data. A compile/analyzer-only `Assert.Single` correction was
retained with the qualification logs; protocol and durability assertions were preserved.

## Qualification and source accounting

| Check | Recorded result |
|---|---|
| Exact source | `434968a555bfbf15fb4bc6156e34ae277baee572`. |
| Committed-source Release build | PASS; zero warnings/errors; **22.09 seconds**. |
| Full native suite | PASS; **8,887**, zero failures/skips: crypto 8,005, SQLite data 78, Game 436, mock 139, Realm 3, World 226. |
| Native standalone | PASS; **59 checks / 142 frames / 5,525 ms**. |
| Full source provider CI | SUCCESS, [run 37106596886](https://github.com/Krilliac/ArcaneCore/actions/runs/37106596886), job **111156231006**, completed **07:33:12 UTC**. **8,999 passed**, zero failures/skips: crypto 8,005, data 190, Game 436, mock 139, Realm 3, World 226. SQLite/MariaDB 10.11/PostgreSQL 16; Release zero warnings/errors; standalone **59 checks / 142 frames / 4,600 ms**. |
| Preserving canonical merge | `a656e61ceeae287358f2fe327f59fdd0991d29de`, created after full source CI success. |
| Final canonical documentation head | Recorded on [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11). |
| Final canonical push/PR CI | Exact links/results are recorded on draft #11 and the integration outcome. |

The source was built and tested by the owning integration lane. The global source diff is
six files, 1,277 insertions/one deletion: the new 47-line handler, the 24-line
listener change and four focused test files. No data module/schema version is
added: **auth 2 / characters 6 / world 6**. Original default/lead
remote heads unchanged and nine worktrees preserved. No default merge, force
push, branch deletion, release, deployment or clustering runtime is included.

## Client acceptance boundary

The separate user-provided report remains attributed only to exact
`f8ae6e8b5f805e94f145194a82f80e876fa2dec3`: authentication, creation, Northshire
entry, 80.5 seconds connected and basic movement. Its artifacts were not inspected
by this coding task. Actual **f8** normal logout/fresh login/saved position and
world restart/relog are still pending; this new automated proof does not retroactively
complete them. Current NPC/quest UI and broader client acceptance are also pending.

**Computer Use remains deferred by the user's latest instruction.** No client,
run directory, credentials or artifacts were accessed, and no client input or
acceptance daemon action occurred in this tranche. A future user-selected
desktop-enabled chat retains its selected pin and databases; publishing this
successor does not switch that client run. Queue polls and automated persistence
proof do not establish general playability, queue membership or clustering acceptance.
