# Creature movement, spawns and respawn (wave 4 lane "creature-movement-spawns")

Fidelity work on how creatures move, how their splines reach the client and how spawns live, die and come back. Everything is
checked against the real references (vmangos primary, mangos-classic, wow_messages, classic-db) and every non-retail
behaviour sits behind `Creatures:*` configuration that defaults to retail. Nothing here copies reference code or data.

Companion docs: `docs/areas/creatures.md` (the base creature system), `docs/areas/creature-ai.md` (AI, evade, leash).

## Delivered

### 1. SMSG_MONSTER_MOVE offsets and walk/run mode (slice `monster-move-and-walk-mode`)

* Intermediate spline points are now written as `destination - point` (11/11/10-bit quarter-yard pack), not
  `middle - point`: vmangos `Movement/spline/packet_builder.cpp:77-111` (`offset = destination - real_path[i]`),
  mangos-classic `Movement/packet_builder.cpp:98-125` (`destination - pathPoint[i]`).
* An offset under 0.25 yd on every axis is nudged on z (+0.51 when z is below zero, else +0.26): "the client freezes when it
  gets a zero offset" (`packet_builder.cpp:99-106`). mangos-classic drops such points instead (`:104-107`); vmangos is followed
  because the packet's point count then stays equal to the spline's.
* Every spline launch syncs the creature's walk mode: a run spline clears `MOVEFLAG_WALK_MODE`, a walk spline sets it, and a
  *change* sends `SMSG_SPLINE_MOVE_SET_RUN_MODE` (0x30D) / `SMSG_SPLINE_MOVE_SET_WALK_MODE` (0x30E), body = packed guid, to
  the observers before the monster move (vmangos `MoveSplineInit.cpp:109-112`, `:173-176`; wow_messages
  `world/movement/smsg/smsg_spline_move_set_walk_mode.wowm`). A repeat of the same mode sends nothing.
* `Unit.Relocate` keeps `WalkMode` (it is server-decided for creatures and every spline step relocates the creature).
* Config: `Creatures:Movement:MonsterMoveOffsetBase` = `Destination` (retail, default) | `Midpoint` (legacy, rollback only).

Verification: unit tests derive the expected words by hand from the reference layout and never use the production
unpacker as the oracle (`MonsterMovePacketFidelityTests`). **Unit-verified against reference bytes only; no 1.12.1 client has
confirmed multi-point splines or the toggle packets.**

Limits: `MOVEFLAG_SPLINE_ENABLED | FORWARD` are not maintained on the creature's movement block (the create block clears
`SplineEnabled` anyway, `UpdateBlockWriter.cs`); a late observer still gets a one-tick catch-up move instead of vmangos'
live spline inside the create block (`packet_builder.cpp:152-200`).
