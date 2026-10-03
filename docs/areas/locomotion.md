# Locomotion: movement handshake, flags, falls, environment (wave 3, lane movement-environment)

Retail reference for every rule: vmangos (`D:\refs\vmangos`, primary), cross-checked with
mangos-classic and the gtker packet layouts (`D:\refs\wow_messages`). Nothing was copied; each rule
below cites the file and lines it was verified against. Everything is retail by default; the
`Locomotion` configuration section holds the (few) switches.

## Slice 1: loco-foundation (delivered)

### What it does

* **Pending-movement-change ledger** (`src/ArcaneCore.Game/Locomotion/PendingMovementChanges.cs`,
  vmangos `Unit.cpp:6619-6930`). Every server-ordered change of a player-controlled unit is a pair:
  the order is sent with the player's movement counter (shared with teleports, as vmangos
  `GetMovementCounterAndInc`) and recorded; the state changes only when the matching client ack
  arrives (counter + apply flag + type must match, `FindPendingMovementFlagChange` /
  `FindPendingMovementRootChange`). An ack that matches nothing is ignored and counted
  (`LocomotionState.WrongAckCount`, vmangos `OnWrongAckData`); it is never punished here.
* **Ack timeout** (`MapLocomotion`, an `IMapUpdater` with `[DefaultMapUpdater(Order = 10)]`,
  vmangos `CheckPendingMovementChanges` `:6619-6665`). The oldest unacknowledged change is enforced
  after `Locomotion:PendingAckResponseTimeMs` (4000, vmangos `Movement.PendingAckResponseTime`,
  `World.cpp:985`), five times longer while the player is being teleported. A change that a newer
  change of the same type superseded is only dropped. Enforcing applies the state, counts
  `FailedAckCount` (`OnFailedToAckChange`) and tells everyone with `SMSG_SPLINE_MOVE_*`
  (`SendMovementFlagChangeToAll`, build above 1.9.4). A player leaving the map has its latest pending
  changes applied silently (`ResolvePendingMovementChanges(false, ...)`).
  Age is accumulated from the map tick (not wall time) so the timeout is deterministic.
* **Root ack on the ledger** (`ArcaneCore.World/Locomotion/RootAckHandler.cs`, replaces the ack
  registration in `MovementHandlers`; an opcode may only be registered once). `Player.SetRooted` now
  records its order in the ledger. A matching ack stores the client block, sets `MOVEFLAG_ROOT` in
  `Unit.Movement` and relays `MSG_MOVE_ROOT/UNROOT` (packed GUID + block) to observers. Before this
  change an ack was accepted whenever it agreed with `IsRooted`, whatever its counter, and the server
  never set the flag.
* **Client-movement observer seam** (`ClientMovementObservers.cs`): classes marked
  `[MovementObserver(Order = n)]` in `ArcaneCore.Game` are discovered by reflection (fail-closed),
  others are added per world with `MovementObservers.Register`. `BeforeApply` runs after validation
  and before the block is stored and sees the previously stored block (vmangos `HandleFall` /
  `UpdateFallInformationIfNeed`, `MovementHandler.cpp:333-344`) and may correct the incoming block;
  `AfterApply` runs after the position was stored (`HandleMoverRelocation`). A throwing observer is
  logged and the others still run. `MovementHandlers.ApplyObserved` is the single hook, shared by
  movement packets and the acks. The security lane's `MovementValidator` stays before it; this lane
  never validates structure.
* **Per-unit state without editing hot classes** (`LocomotionState`, `unit.Locomotion`,
  a `ConditionalWeakTable`): pending ledger, `AuraLedger` (what the locomotion aura modules record,
  because `SpellSystem` is a world-daemon feature that map code cannot reach), fall start height and
  the ack counters. Nothing is persisted (vmangos saves none of it), so **no schema or data module
  changes**: the MariaDB/PostgreSQL provider rule is not triggered by this lane.
* **Shared-file edits**: `Unit` gained `AddMovementFlags` / `RemoveMovementFlags`, and `Relocate`
  keeps the server-owned flags (Root, WaterWalking, Hover, SafeFall) instead of clearing them
  (vmangos only rewrites the position of `m_movementInfo`); `Player.SetRooted` calls the ledger;
  `MovementHandlers` lost the root ack and gained the hook. See `docs/integration/locomotion.md`.

### Options (`Locomotion` section)

| Key | Default | Source |
|---|---|---|
| `PendingAckResponseTimeMs` | 4000 | vmangos `Movement.PendingAckResponseTime`, `World.cpp:985` |
| `RateDamageFall` | 1.0 (negative resets to 1) | vmangos `Rate.Damage.Fall`, `World.cpp:533` (`setConfigPos`) |

Only options whose rule is delivered exist; keys for undelivered rules would be stubs.

### Limits and open points

* `Player.SetRooted` keeps today's behaviour of always sending the order (its callers, logout and
  stun, run in the world); the new flag primitives send nothing and set the flag directly while the
  player is not in a map (vmangos `Unit::SetWaterWalking`, `Unit.cpp:7239-7245`).
* vmangos kicks a client that acks a root without the Root flag in its block
  (`MovementHandler.cpp:719-731`, a 1.14-client workaround). ArcaneCore applies the flag from the
  order and does not kick.
* GUID width: wow_messages lists the `SMSG_FORCE_MOVE_ROOT` family and `SMSG_SPLINE_MOVE_ROOT` with a
  full GUID for 1.12; vmangos, mangos-classic and mangoszero send a packed GUID. Packed is kept; only
  a real-client capture settles it. The handshake therefore needs a real-client check before it can
  be called retail-verified.
* The client's movement block is applied with the same `ApplyClientMovement` as any movement;
  a later heartbeat without the Root flag still overwrites the stored flag until the flag-authority
  slice (not delivered in this wave if listed under "not done" in the report) lands.
