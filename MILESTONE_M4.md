# MILESTONE M4 — Movement + Visibility

Status: **implementation + automated (loopback) verification complete; awaiting
real-client acceptance** (`docs/M4_ACCEPTANCE.md`).

## What was built

- `ArcaneCore.Game` — the spatial/visibility layer: `IWorldPlayer`, `Map` (per-map player
  registry with a per-player visible set, dynamic enter/leave visibility on movement,
  movement relay to viewers, destroy on leave) and `WorldState` (process-wide map registry, registered as a singleton).
  `ObjectUpdateBuilder` gained a "create for others" path (no SELF flag).
- `ArcaneCore.Protocol` — the `MSG_MOVE_*` opcodes, `SMSG_DESTROY_OBJECT`, and
  `MovementOpcodes.IsRelayable`.
- `ArcaneCore.World` — `WorldSession` is now an `IWorldPlayer`: it registers with the map
  on login, updates its position from inbound movement packets and relays them to nearby
  players, and leaves the map on disconnect. Sends are serialized with a per-session lock
  (other sessions broadcast onto the same stream).

## Verified against (Charter §1.1 / §4)

| Detail | Reference |
|--------|-----------|
| Movement opcode values (`MSG_MOVE_*` 181–238) | vmangos `Opcodes_1_12_1.h` |
| Client movement = `MovementInfo` only (no GUID prefix) | gtker `MSG_MOVE_*_Client` (1.12) |
| Relay format = packed GUID + `MovementInfo` | vmangos `MovementHandler::HandleMovementOpcodes` |
| `SMSG_DESTROY_OBJECT` = 170 (8-byte GUID) | vmangos `Opcodes_1_12_1.h` |
| Create-update for other players (no SELF flag) | vmangos `Object::BuildCreateUpdateBlockForPlayer` |
| Visible-set enter/leave logic; out-of-range block for players leaving range | vmangos `Player::UpdateVisibilityOf<Player>` |
| Range = 100 yd (`DEFAULT_VISIBILITY_DISTANCE`), +1 yd grey once visible (`Visibility.Distance.Grey.Unit`) | vmangos `ObjectDefines.h`, `World.cpp` |
| 2D distance + both bounding radii, strict `<` | vmangos `WorldObject::IsWithinVisibilityDistanceOf`, `IsWithinDist` |
| `UPDATETYPE_OUT_OF_RANGE_OBJECTS` = 4; layout: count(u32) + packed GUIDs | vmangos `UpdateData.h`, `ObjectUpdate.cpp` `AppendBodyTo` |
| Logout uses `SMSG_DESTROY_OBJECT`, not out-of-range | vmangos `Object::DestroyForPlayer` |

### Automated tests (13 world tests total)
- Two clients sharing one `WorldState`: each sees the other's create-update on entry; one
  moving makes the other receive the relayed `MSG_MOVE_HEARTBEAT` with the mover's packed
  GUID; a client disconnecting makes the other receive `SMSG_DESTROY_OBJECT` for its GUID.
- Walking 200 yd away sends each client the other's out-of-range block and stops the relay;
  walking back re-sends both create-updates, then the relay resumes.
- Visibility-distance boundary (2D, bounding radii, grey hysteresis) and the out-of-range
  block's byte layout.

## Decisions & limitations

1. **Distance-based visibility, not a full grid.** `Map` checks every player on the map
   (O(n) per movement packet). A grid/cell index is a later optimization that slots in
   behind the same `Map` surface without changing behaviour.
2. **Dynamic visibility on movement.** Each player keeps the set of players its client has
   objects for; every movement packet re-evaluates it both ways (create on entering range,
   out-of-range block on leaving, 1-yard hysteresis), and movement is relayed only to
   players whose set contains the mover — the vmangos broadcaster-listener model.
3. **Movement is relayed, not validated.** The server updates position and forwards the
   packet; anti-cheat / speed / position validation (vmangos `HandlePositionTests`) is out
   of scope for M4.
4. **Thread-safety.** A per-session `SemaphoreSlim` makes header-encryption + write atomic,
   since broadcasts from other sessions write to the same stream. Visibility decisions and
   their sends run under one per-map lock so each client's create → movement →
   out-of-range/destroy order always matches its visible set. Cost: a client whose socket
   stops draining stalls that map's movement processing until its write completes or the
   connection drops. A per-session outbound queue removes that coupling and is a natural
   follow-up once player counts make it matter.

## Client build verified against

Pending — to be filled in by the developer after running `docs/M4_ACCEPTANCE.md` against
two real WoW **1.12.1 (build 5875)** clients.
