# M4 Acceptance Test — Movement + Visibility

> Defined against real WoW **1.12.1 (build 5875)** clients. Per Charter §6, M4 is "done"
> only when a character walks and **two clients see each other move**.

## Scope under test

The world daemon's movement and visibility:

- Accepts the client movement opcodes (`MSG_MOVE_*`: start/stop forward/back/strafe, jump,
  turn, heartbeat, set-facing, …) and updates the player's server-side position.
- A per-map visibility system (`WorldState` / `Map`): players on the same map within
  ~100 yards see each other.
- On entering a map, exchanges `SMSG_UPDATE_OBJECT` create-updates so each client renders
  the other.
- Relays each movement packet (prefixed with the mover's packed GUID) to the players that
  currently see the mover.
- On movement, re-evaluates visibility both ways: a player walking out of range is removed
  with an out-of-range `SMSG_UPDATE_OBJECT` block; walking back in re-sends the create-update.
- On disconnect, sends `SMSG_DESTROY_OBJECT` to the players that see the leaver.

## Pre-conditions

1. M1–M3 working; two accounts each with a character in the **same starting area** (e.g.
   two human characters in Northshire — same map, same zone, within ~100 yards).
2. One world daemon serving both clients (shared `WorldState`).

## Procedure

1. Log both characters into the world (two clients).
2. Confirm each client renders the other character standing nearby.
3. Walk one character around; observe the other client.
4. Walk the second character; observe the first client.
5. Walk one character more than ~100 yards away from the other (e.g. Northshire Abbey
   steps → the vineyard), then walk back.
6. Log one client out.

## Expected result (PASS criteria)

- Each client sees the other character appear when it enters the world.
- Moving one character makes it **move smoothly on the other client** (and vice versa).
- The first character walks normally with no rubber-banding/disconnect.
- Walking out of range makes each character disappear on the other's client; walking back
  makes it reappear at its current position and keep moving smoothly (no frozen or
  duplicate model, no disconnect).
- Logging a character out makes it disappear on the other client.

A character walking, and two clients seeing each other move = **M4 PASS**.

## Known limitation (note for testing)

Visibility is a per-map distance check (2D, 100 yards + 1-yard grey hysteresis), not yet a
grid/cell index; behaviour is identical, only the cost scales with players per map.

## Reference basis

Movement opcodes, the relay format (packed GUID + MovementInfo), `SMSG_DESTROY_OBJECT`,
and the create-for-others object update verified against vmangos `MovementHandler.cpp`,
`Opcodes_1_12_1.h`, `Object.cpp`, `Player.cpp` (`UpdateVisibilityOf`),
`UpdateData.h`, `Server/Packets/ObjectUpdate.cpp`, and gtker `MSG_MOVE_*` definitions. Notes in
`MILESTONE_M4.md`.
