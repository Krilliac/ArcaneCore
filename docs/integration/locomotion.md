# Locomotion lane: shared-file edits (for the integrator)

Lane `movement-environment` (wave 3). Area doc: [../areas/locomotion.md](../areas/locomotion.md).
No schema, store or data-module change anywhere in the lane: nothing it keeps is persisted, so there is no
Characters/World version to renumber and no provider theory.

| File | Edit | Why |
|---|---|---|
| `src/ArcaneCore.World/Handlers/MovementHandlers.cs` | Removed the `CMSG_FORCE_MOVE_(UN)ROOT_ACK` registrations and `HandleRootAck` (moved to `World/Locomotion/RootAckHandler.cs`); `HandleMovement` now stores the block through `ApplyObserved` (observers before/after `ApplyClientMovement`); `EnsureFinite` became `internal`. | One hook for the locomotion observers; an opcode may be registered once. The security lane edits the same method (`MovementValidator` call): keep its early return before `ApplyObserved`. |
| `src/ArcaneCore.Game/Entities/Unit.cs` | `AddMovementFlags` / `RemoveMovementFlags`; `Relocate` keeps Root/WaterWalking/Hover/SafeFall and ends a fall in progress. | The server decides these flags (vmangos `Set*Real`). |
| `src/ArcaneCore.Game/Entities/Player.cs` | `SetRooted` records its order in the pending ledger (`MovementControl.Order`) instead of building the packet itself (same bytes). | Ack validation and timeout. |
| `src/ArcaneCore.Protocol/MovementInfo.cs` | Added `CorrectData()` (additive). | vmangos MovementInfo::CorrectData. |
| `src/ArcaneCore.Game/Combat/MapCombat.Death.cs` | `SendGhostMovement` calls `MovementControl.Order` (same packet, now recorded in the pending ledger). | The ghost's water walk follows the client's ack. |
