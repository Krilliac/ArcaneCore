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

### 2. Random wander parity and generator type numbers (slice `random-wander`)

* `RandomMovementGenerator` moved to `Movement/RandomMovementGenerator.cs` (no behaviour change to its timing: 1 s first move, 50 ms
  steps, urand(4,10) s pauses, urand(0, wander<=1 ? 2 : 8) steps; `Movement/RandomMovementGenerator.cpp:56-76`).
* Legs run only for ALWAYS_RUN (`:53`), now read through the dialect table (`Template.Behaviour`), not raw `ExtraFlags & 0x40`: the
  same bit is meaningless in the cmangos dialect, so cmangos-imported creatures walk. The raw constant `Creature.ExtraFlagAlwaysRun`
  is gone; waypoint legs use the same decoded flag.
* cmangos RUN_DURING_WANDER (0x20 in the cmangos dialect only; vmangos' 0x20 is NO_MOVEMENT_PAUSE): a per-leg draw
  `urand(0,99) < Creatures:Movement:RunDuringWanderChancePercent` (default 15; cmangos `RandomMovementGenerator.cpp:135-136`).
* `GetResetPosition` (`:131-144`): the creature's own position when within the wander distance of its spawn point, else the spawn
  point. Evade already consults the default generator (`CreatureMapSystem.Evade.cs`), so a wanderer evading from inside its disc no
  longer runs back to the spawn point.
* UpdateAsync gates (`:113-128`): stunned/rooted/confused/fleeing zero the move timer and start no leg; casting stops the creature and
  freezes the timer.
* `MovementGeneratorType` numbers follow `Movement/MotionMaster.h:36-59` (Chase 6, Home 7, Point 9, Fleeing 10, Follow 15); the
  values are internal (never persisted or sent; grep of casts found none).

Limits: no navmesh random point and no steep-slope exclusion (vmangos `MOVE_PATHFINDING | MOVE_EXCLUDE_STEEP_SLOPES`; the pathfinder
has no random-point query, so the point is uniform over the disc at the height provider's Z); no flying circle path (`:28-44`: needs
the Flying spline flag plumbed through `ICreatureMover.MovePath`); no timed random / pause-time API (nothing consumes it until waypoint
node wander exists); vmangos' `Interrupt`/`Finalize` walk-mode reset (`:83-93`) is not sent separately because every spline launch
already syncs the mode.
