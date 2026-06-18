# MILESTONE M4 — Movement + Visibility

Status: **implementation + automated (loopback) verification complete; awaiting
real-client acceptance** (`docs/M4_ACCEPTANCE.md`).

## What was built

- `ArcaneCore.Game` — the spatial/visibility layer: `IWorldPlayer`, `Map` (per-map player
  registry + distance-based visibility, create-update exchange, movement relay, destroy on
  leave) and `WorldState` (process-wide map registry, registered as a singleton).
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

### Automated tests (12 world tests total)
- Two clients sharing one `WorldState`: each sees the other's create-update on entry; one
  moving makes the other receive the relayed `MSG_MOVE_HEARTBEAT` with the mover's packed
  GUID; a client disconnecting makes the other receive `SMSG_DESTROY_OBJECT` for its GUID.

## Decisions & limitations

1. **Distance-based visibility, not a full grid.** `Map` uses a per-map ~100-yard distance
   check (the charter's grid/cell visibility in its simplest correct form). A real
   grid/cell index is a later optimization that slots in behind the same `Map` surface.
2. **Visibility is exchanged on entry; relay is to in-range players.** A player walking out
   of range stops being relayed to, but is not yet dynamically destroyed for the other
   client (no continuous enter/leave culling on movement). Sufficient for the M4 acceptance
   (two clients near each other); dynamic culling is a follow-up.
3. **Movement is relayed, not validated.** The server updates position and forwards the
   packet; anti-cheat / speed / position validation (vmangos `HandlePositionTests`) is out
   of scope for M4.
4. **Thread-safety.** A per-session `SemaphoreSlim` makes header-encryption + write atomic,
   since broadcasts from other sessions write to the same stream.

## Client build verified against

Pending — to be filled in by the developer after running `docs/M4_ACCEPTANCE.md` against
two real WoW **1.12.1 (build 5875)** clients.
