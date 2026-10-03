# Integration notes: instances and dungeons (`feat/instances`)

Status: **draft, in progress** (fleet round 2). Base: `codex/integrate-feature-fleet-20261003`
at `0d32fba1070a0be08932a85551a4c1b4ca191cfc`.

Goal: distinct `Map` objects per dungeon/raid instance, keyed by `(mapId, instanceId)`, with
1.12 (vmangos) bind/save/reset semantics, group ownership, reset opcodes, raid info and
entrance requirements. This file is filled in as the slices land; the final version lists
scope, shared-file edits, schema, tests and known gaps.

Planned slices:

1. Per-instance maps in `WorldRuntime` (create on demand, unload when empty after a delay,
   `MapUnloading` lifecycle event), `Map.InstanceId`.
2. `Game/Instances`: instance saves, player/group binds, entry resolution
   (permanent > group > solo > new), CMSG_RESET_INSTANCES rules, raid global reset
   schedule with warnings, normal dungeon reset while empty, homebind timer for players
   who lose instance validity, max-player cap.
3. Teleport integration through a resolver seam (existing worldport-ack flow kept).
4. Persistence: characters schema module (reserved v9), provider-matrix tests.
5. World feature: handlers, login raid info, group hooks, per-instance creature spawns.
