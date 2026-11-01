# World-thread audit, lane 7: bot movement packets (item 16.2)

Status: finding confirmed on current code and fixed in place. Not built or run in the lane (integration-branch policy); the tests run in the integration gate.

## Finding

`PlayerbotMotion.Send` (the bot movement step) built `new PacketWriter(64)`, wrote the movement block, and passed `writer.ToArray()` to
`TryManagedAction`, so every packet allocated a writer, its buffer and a result array. `World:Playerbots:MovementPackets` defaults to true.

## Fix

- `MotionState.Writer`: one `PacketWriter` per bot, `Reset()` before each step.
- `WorldSession.TryManagedActionSpan(opcode, ReadOnlySpan<byte>)` (ManagedWorldSession.cs): copies the span into one exact-length array
  the session keeps (`_managedScratch`) and calls the unchanged array `TryManagedAction`. Handlers still receive an exact-length `byte[]`, so no
  handler signature changed. A length change allocates a new exact array (jump, swim and transport steps), then reuse resumes.
- A fresh array is used instead when anything could keep the bytes: a packet observer, tap or capture is attached (`HasPacketObserver`), the
  call is re-entrant on the same buffer (`_managedScratchBusy`), or the call is off the world thread.

## Threading and aliasing

- World thread only: the writer lives in per-bot `MotionState`, the scratch in the session; the off-thread guard keeps the scratch untouched elsewhere.
- Single writer per map is unaffected: no new cross-thread state, no change to what the handler does.
- Retention audit of the dispatch path: `HeuristicsManagedPacket` only hashes the payload (`NoteReplay`); `CapturePacket`, session trace and taps take
  spans or copy; `MovementHandlers.HandleMovement` reads through a `PacketReader` and relays a packet it builds itself. `ManagedPacketObserver` and
  `_outboundPacketObserver` receive copies of outbound packets only. The span overload is for non-retaining handlers (movement); other callers keep the array overload.
- War Mode files (`PlayerbotBrain.Pvp/Defense/Help`, `PlayerbotDefenseLane`, `PlayerbotHelpLane`, `PlayerbotHostility`, `WarModeOptions`): none send movement packets or
  touch `Send`/`MotionState`; no change needed.

## Tests (tests/ArcaneCore.World.Tests/Playerbots/PlayerbotMovementPacketReuseTests.cs)

- Golden bytes: a reused, reset writer equals a fresh writer across plain, swimming and jumping steps, and a plain heartbeat equals a hand-laid 28-byte layout.
- Equal-length packets reuse one buffer and each is dispatched; a new length gets an exact-length buffer.
- With a packet observer attached the reused buffer is never used; off the world thread the call refuses and keeps no buffer.

The reuse tests fail before the change (`TryManagedActionSpan` and `ManagedScratch` do not exist). An `AllocationProbe` of a full step was not added: the
movement handler's own relay allocations are outside this lane, so a zero-byte assertion over the whole step would not hold. Buffer identity is asserted instead.

## Not measured

The live comparison with `MovementPackets=false` and `.debug tick` KB per tick were not taken (no server runs in the lane).

## Conflicts

Brief names #1648 (`PlayerbotMotion.cs`); the hunks here are the `Send` packet block and one `MotionState` field.
