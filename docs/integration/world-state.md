# Integration notes: world state and exploration

Behaviour: [areas/world-state.md](../areas/world-state.md). New code is in `WorldState`
folders; the shared-file edits below are small and additive.

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/Handlers/PlayerHandlers.cs` | `HandleZoneUpdate` calls `ZoneAreaFeature.HandleClientZone` when the feature exists (old body kept as fallback). | Server-derived zone (vmangos `MiscHandler.cpp:381-386`). |
| `src/ArcaneCore.World/Handlers/LoginSequence.cs` | `SendInitialPacketsAfterAddToMap` calls `ZoneAreaFeature.ForceUpdate` (old send kept as fallback). | vmangos `SendInitialPacketsAfterAddToMap` runs `UpdateZone`. The death lane's login ghost handling edits the same file; keep both. |

Seams added: `WorldStateHooks.For(world)` (options, clock, zone locator, location listeners),
`IPlayerLocationListener`, `IZoneLocator`, `IGameTime`. Rest/tavern, PvP-enforced-area and
channel logic from other lanes should use `IPlayerLocationListener` instead of polling zones.

Schema: none so far.
