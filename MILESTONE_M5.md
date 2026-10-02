# MILESTONE M5 — Runtime core

Status: **implemented** (automated loopback + multi-engine tests, CI); **client acceptance
pending** (`docs/M5_ACCEPTANCE.md`).

M5 replaces the M2–M4 "handle everything on the socket task under a map lock" design with
the architecture every later milestone builds on (ROADMAP § Architecture decisions).

## What was built

| Area | Before (M4) | Now |
|---|---|---|
| Threading | Socket tasks mutate maps under a per-map lock | One world thread runs a fixed tick (`WorldRuntime`, 50 ms); maps own their players; network tasks only queue packets |
| Dispatch | One big `switch` in `WorldSession` | `OpcodeTable`: session handlers (character screen, async DB) and world handlers (queued, run inside `Map.Update`), with session states |
| Sending | Socket write under a per-session lock, from any thread | Header encrypted under a lock (cipher order = queue order) → channel → one writer task that batches frames; slow clients are disconnected at 8 MiB pending |
| Opcodes | 40 hand-written values | All 825 build-5875 opcodes, generated (`tools/codegen`) from vmangos and cross-checked against gtker; wire names in logs |
| Update fields | 30 hand-computed indices | All 324 fields with visibility flags, generated and cross-checked the same way |
| Object model | `PlayerObject` property bag + one-shot create builder | `WorldObject` → `Unit` → `Player`: `uint[]` values + changed-field mask; create, values and out-of-range blocks per viewer, batched per tick, zlib-compressed above 128 bytes |
| Movement | Raw bytes forwarded | `MovementInfo` parsed in full; relay carries the server receive time |
| Persistence | Nothing saved after creation | Position, zone, level and played time saved on disconnect, autosave (15 min) and shutdown, through an ordered async save queue |
| Databases | One connection string; `EnsureCreated` | Auth / characters / world split (each may share one server or database); per-component schema version with fail-closed checks, adoption of M1–M4 databases, model-derived additive upgrades; SQLite added |
| Sessions | — | One session per account (a reconnect kicks the stale one); login waits for a lingering copy of the character to leave the world |

### Bugs fixed on the way

1. **Shared-database schema creation (M1–M4).** `EnsureCreated` skips a context as soon as the
   database has *any* table, so with the default single-database config whichever daemon
   started second never got its tables (`characters` or `account` missing). Fixed by the
   per-component bootstrapper; covered by `AllComponents_ShareOneDatabase` on all engines.
2. **Rage/energy in the mana field (M3).** The player's power was always written to
   `UNIT_FIELD_POWER1`; it belongs at `POWER1 + powerType` (vmangos `Unit::SetPower`).
3. **Self create type (M3/M4).** Players were sent `UPDATETYPE_CREATE_OBJECT2`; vmangos uses
   `CREATE_OBJECT` for players (`CREATE_OBJECT2` only for objects spawned into a map).

## Verified against (charter §1.1 / §4)

| Detail | Reference |
|---|---|
| Opcode values (825) | vmangos `Opcodes_1_12_1.h`; 650 cross-checked with gtker IR (2 spelling differences, same values) |
| Update fields: offsets, sizes, types, visibility flags | vmangos `UpdateFields_1_12_1.cpp`; all 236 gtker `vanilla_update_mask` ranges tiled with matching types |
| Visible field classes per viewer (public/dynamic, +private for self) | vmangos `Object::GetUpdateFieldFlagsForTarget` |
| GUID fields sent as pairs in creates; both halves marked on change | vmangos `Object::_SetCreateBits`, `SetUInt64Value` |
| Create block: type, packed GUID, type id, movement block, values | vmangos `BuildCreateUpdateBlockForPlayer`, `BuildMovementUpdate` (> 1.8.4) |
| Living movement block = MovementInfo + 6 speeds; unmoved objects report now + 1000 | vmangos `BuildMovementUpdate` |
| Base speeds 2.5/7/4.5/4.722222/2.5/3.141594 | vmangos `Unit.cpp baseMoveSpeed` |
| `MovementInfo` layout and flags (`ONTRANSPORT` = 0x02000000) | vmangos + cmangos-classic + mangoszero (flags), vmangos + cmangos-classic (layout) |
| Relay = packed GUID + movement with server time | vmangos/cmangos-classic `MovementInfo::Write` (stime) |
| `CMSG_MOVE_FALL_RESET` applied, not relayed | vmangos `HandleMovementOpcodes` |
| Update packet layout, out-of-range block first | vmangos `UpdateObject::AppendBodyTo` |
| Compression: > 128 bytes, `SMSG_COMPRESSED_UPDATE_OBJECT` = u32 size + zlib | vmangos `Compression.Update.Size`, `UpdateObject::AppendBodyTo`, `PacketCompressor` |
| Player creation fields (bytes layouts, 0xEE bytes, misc flags, watched faction −1, rest state) | vmangos `Player::Create`, `UnitDefines.h`, `Player.h` |
| Default bounding radius 0.389 / combat reach 1.5 | vmangos `ObjectDefines.h` |
| Login order: verify world, tutorials, spells, time speed, self create | vmangos `HandlePlayerLogin`, `SendInitialPacketsBeforeAddToMap`, `Map::Add` |
| `SMSG_CHARACTER_LOGIN_FAILED` = one result byte; CHAR_LOGIN_* codes | gtker `smsg_character_login_failed.wowm`, vmangos `ResponseCodes` |
| Client header size limits [4, 0x2800] | vmangos `WorldSocket::handle_input_header` |
| One session per account | vmangos `World::AddSession_` |
| Tick 50 ms, autosave 15 min | vmangos `WORLD_SLEEP_CONST`, `PlayerSave.Interval` |

### Reference discrepancies found

1. **Transport block of `MovementInfo`.** vmangos/cmangos: u64 GUID + position; mangoszero adds
   a u32 time and skips fall time; gtker: packed GUID + u32 time. No transports exist yet;
   the vmangos/cmangos layout is used until a capture on a boat settles it.
2. **`ON_TRANSPORT` flag.** gtker labels 0x200; all MaNGOS cores use 0x02000000.
3. **`CHAR_NAME_SUCCESS`.** gtker 0x50, vmangos 0x52 (extra CONSECUTIVE_SPACES entry). Unused so far.

## Automated tests

| Project | Tests | What |
|---|---|---|
| ArcaneCore.Game.Tests | 14 | masks, values/create blocks per viewer, batching/splitting/compression, map visibility, runtime save/autosave/invoke |
| ArcaneCore.Data.Tests | 18 | 6 schema/store scenarios × SQLite, MariaDB 10.11, PostgreSQL 16 |
| ArcaneCore.World.Tests | 19 | handshake, lifecycle, login failure, ping, unknown opcodes, bad headers, duplicate account, visibility, relay time, compression, persistence, values routing |

CI runs MariaDB and PostgreSQL as service containers so the multi-engine claim is enforced.

## Decisions & limitations

1. **One world thread for all maps.** Maps are updated sequentially. Packet handling is
   already per map (`Map.Update` drains its players' queues), so a per-map thread pool can
   come later without touching handlers.
2. **Visibility is still O(players²) per map**; the grid/cell index arrives with creatures (M10).
3. **Tick-granular latency.** In-world packets wait for the next tick (≤ 50 ms), like vmangos.
4. **Schema evolution** is additive only (new tables/columns, derived from the EF model).
   Anything else fails closed; real migrations come with the first destructive change.
5. **Bounding radius / combat reach** use vmangos' defaults until display model data is
   imported (M8).

## Client build verified against

Pending — `docs/M5_ACCEPTANCE.md` with two WoW **1.12.1 (5875)** clients.
