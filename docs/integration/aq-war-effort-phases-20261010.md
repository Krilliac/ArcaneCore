# AQ war effort: gong and gate opening phases — 2026-10-10

## What this slice adds

It builds on the wave-16 war-effort state (`docs/integration/war-effort-20261009.md`): resource counters, type-40 conditions, events 120-127, and the Colossus flags. The reference is vmangos `silithus.cpp` (`scarab_gongAI`, `QuestRewarded_scarab_gong`) and `world_event_wareffort.*`.

- **Phase 1 → 2** is unchanged (the last counter turn-in moves the saved phase to Transporting in the reward transaction). The five-day deadline now uses `WarEffortCatalog.TransportSeconds`.
- **The five-day countdown** is the saved `phase_ends_at_unix`. A new test shows that a restart before the deadline keeps event 121 and condition 2113, and that the deadline then moves the phase to Gong (event 122).
- **Bang a Gong!, gated to the first ringer.** New characters schema **46** adds `world_war_effort_gong`, holding vmangos's `VAR_WE_GONG_BANG_TIMES` plus the first ring's time and character. Every committed 8743 reward from the Silithus gong during the Gong or TenHourWar phase increments the count in the same serializable transaction. Only the first (count 0, phase Gong) saves the ring time and the champion and starts the war. Later rings only count. Rings before the Gong phase or after the war are ignored. A rollback leaves everything unchanged.
- **Gate-opening steps.** The scarab_gongAI timeline is +1 s for roots (176147), +6 s for runes (176148), +14 s for the barrier (176146), and +24 s for the war start (`VAR_WE_GONG_TIME`). Here it is a pure function of the saved ring time (`WarEffortSnapshot.GateAt`), so a restart mid-sequence resumes at the right step. `WarEffortFeature` caches the three Kalimdor gate objects and holds each at its implied state every tick. The ten-hour deadline counts from the war start (ring + 24 s + 10 h).
- **Permanent open.** In Done, all three pieces are held open forever, and an outside close is undone.
- **Champion broadcast.** Broadcast text 11427 is sent once to every online player, from the imported `broadcast_text` with `$N` set to the champion. It is sent only within 60 s of the saved ring.

## Limits (honest)

- The broadcast goes out as a system message, not as the reference's broadcast-text chat type. A restart within 60 s of the ring can repeat it. The gate sounds (7114-7116) and the Anachronos scene abort (+8 s) are not played.
- The reference's `STAGE_RESET` (GM gate reset) is not implemented, since the open state is permanent by design.
- The phase-2 Cenarion Hold attack waves and the Saurfang/Crossroads transport scenes (vmangos `npc_aqwar_*`, events 54-65) are not part of this slice. The ten-hour war's waves of Qiraji come from the imported event-123 spawns. No scripted wave timer has been added.
- Not exercised against an original client or the imported world: the gate GO entries are matched by entry on map 1 only.

## Tests

- `ArcaneCore.World.Tests/WorldState/WarEffortGateTests` (7): the timeline steps, closed before the ring, permanent open, restart mid-opening, restart after the ten-hour deadline, the five-day countdown across a restart, and the champion announcement window.
- `ArcaneCore.Data.Tests/WarEffortRewardTests` (+3, 1 updated): only the first ringer opens the gate while later rings only count, out-of-phase rings are ignored, and a rolled-back reward leaves the gate closed.
- `IntegratedSchemaTests` is updated for characters 46.
