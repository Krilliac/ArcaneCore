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

Schema: World `WorldStateDataModule.Version` (11 at this base) and Characters `ExploredZonesDataModule.Version` (14 at this base); tests reference the constants.

## game-time slice

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/Packets/CharacterPackets.cs` | `BuildTimeSpeed` gains a `DateTimeOffset` (local) overload; the old `DateTime` overload delegates; the private packer moved to `GameTimePacker`. | Pack local time. |
| `src/ArcaneCore.World/Handlers/LoginSequence.cs` | `SendInitialPacketsBeforeAddToMap` passes `WorldStateHooks.For(session.World).LocalNow()`. | vmangos `Player.cpp:19141-19145`. |

## world-state-data slice

| File | Change | Why |
|---|---|---|
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | One tuple appended to the expected module list (through the constant). | Every data module is listed there; append only. |

Renumbering: change `WorldStateDataModule.Version` only; the tests use the constant.

## weather-runtime slice

| File | Change | Why |
|---|---|---|
| `tests/ArcaneCore.World.Tests/WorldTestHost.cs` | Three lines after `AttachWorldFeatures`: weather off for the harness. | Keeps every other lane's login packet sequences unchanged; weather tests enable it. |

Another lane's tests that assert a literal login sequence on the real defaults (weather on) will see
one extra `SMSG_WEATHER` after `SMSG_INIT_WORLD_STATES`; that is retail behaviour.

## exploration-persistence slice

`tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs`: one more tuple appended to the expected module list (through the constant). No other shared file. The characters module list is also checked by the existing character-deletion guard (`ICharacterDataCleanup`).

## world-states-runtime slice

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/Packets/LoginPackets.cs` | `BuildInitWorldStates(map, zone)` delegates to `WorldStatePackets.BuildInit` (same bytes). | One builder for the list. |

## pvp-area-state slice

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Combat/MapCombat.Death.cs` | `UpdatePvpFlagTimer`: one extra term, `!WorldState.Zones.PvpAreaState.IsInEnforcedArea(player)`, in the existing freeze condition. | vmangos `Player.cpp:17199-17207`. The combat-cc-spell-rules lane may rewrite this function: keep their version and add this term. |
