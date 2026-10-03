# Graveyards and resurrection

Lane `graveyards-resurrection` (wave 4). Everything is verified against vmangos
(`D:\refs\vmangos`, primary), mangos-classic, wow_messages and classic-db; nothing is copied.

## Delivered

### Death seams and scheduled repop (slice A)

- `DeathSeams` (`src/ArcaneCore.Game/Death/DeathSeams.cs`) is a per-world registry (weak side table, same
  pattern as `DeathHooks`) for the extension points the death area adds. It exists because
  `CombatHooks` takes one production registration (the sealed `FactionCombatHooks`) and `DeathHooks`
  can be replaced wholesale, so neither can carry a feature that a later slice adds. First seam:
  `IGraveyardRepop`. `CombatHooks.RepopAtGraveyard`'s default asks the registered seam and returns
  false when nothing is registered (the ghost stays on its body, as before the feature).
- The repop is **scheduled**, as in vmangos: `RepopPlayer` sets a per-player pending flag
  (`Player::ScheduleRepopAtGraveyard`, Player.cpp:4980-4986) and the player tick runs it once no
  movement change is pending (Player.cpp:1329-1334); the flag is cleared before the trip
  (Player.cpp:4992), so a trip that finds no graveyard is not retried. A logging-out spirit repops
  synchronously (WorldSession.cpp:694-701). The undermap and fatigue-ghost paths keep calling the hook
  immediately (they do in vmangos).
- `MapCombat.IsInstanceableMap` reads the loaded map template (dungeon, raid or battleground =
  instanceable) for maps above 1 and falls back to `CombatHooks.IsInstanceable` otherwise
  (vmangos `MapEntry::Instanceable`). Creature leash/aggro code still calls the hook directly.
- `docs/integration/instances.md` no longer claims the homebind teleport uses graveyards
  (Player.cpp:18534-18556: `TeleportToHomebind`).
- `GhostPersistenceTests.LogoutWhileDead_RelogsAsAGhostAtTheBody` waits for the ghost snapshot, not for
  any snapshot (intermittent red at wave-3 integration).

## Not delivered / limits

See the slice list in `docs/integration/graveyards-resurrection.md`.

## Not a 1.12 packet

`SMSG_DEATH_RELEASE_LOC` exists only for 2.4.3/3.3.5 (wow_messages `smsg_death_release_loc.wowm`) and
nowhere in vmangos; it is deliberately not implemented.

Ghost run speed (`Locomotion:GhostRunSpeedWorld/Battleground`) already exists in the locomotion area,
including vmangos' quirk that the factor applies only to the CORPSE state (Unit.cpp:7043-7046;
docs/areas/locomotion.md).
