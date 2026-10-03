# Game object types lane (`claude/vw4-gameobject-types`, wave 3)

Directive: as close to vanilla 1.12.1 (vmangos primary, mangos-classic, classic-db, wow_messages) as possible;
every deliberate deviation sits behind a config option that defaults to retail. Behaviour is re-implemented from
reading the references; no code or data was copied. Base: `claude/vw-integration` 41babaf.

## Delivered slices

### GO1a fidelity primitives

| Item | Where | Reference |
|---|---|---|
| `GameObjectInfoView`: typed per-type column helpers (`AutoCloseSeconds`, `CooldownSeconds`, `Charges`, `LinkedTrapEntry`, `IsDespawnAtAction`, `DespawnPossibility`, `CannotBeUsedUnderImmunity`, `IsUsableMounted`, `NeverDespawns`) | `src/ArcaneCore.Game/GameObjects/GameObjectInfoView.cs` | `D:\refs\vmangos\src\game\Objects\GameObjectDefines.h:536-668` |
| Door/button/goober auto-close: the template column is `seconds * 0x10000`, closed on a whole-second clock (observed delay in (R, R+1]). Before, `data2` was read as milliseconds, so a 3 s door (raw 196608) closed after 196 s; 458/618 button, 95/309 door and 244/787 goober spawns of classic-db carry such a value | `GameObjectMapSystem.ActivateDoorOrButton`, `GameObject.ResetAfterSecond` | `GameObjectDefines.h:654-668`, `GameObject.cpp:572-590`, `1370-1383` |
| `GO_FLAG_NODESPAWN` (0x20) in `GAMEOBJECT_FLAGS` for database spawns that never despawn (door/button/quest giver/goober with noDamageImmune clear, not consumable, spawntimesecs >= 0); `Despawn` of such a spawn only resets loot and state | `GameObject.NeverDespawns`, `GameObjectMapSystem.Despawn` | `GameObject.cpp:985-1022`, `:671` |
| Respawn delay rolled once per loaded spawn between `spawntimesecsmin` and `max`, then scaled by 90..110 percent when spawn flag 0x04 is set; config `GameObjects:RandomRespawn` (default true) | `GameObjectMapSystem.RollRespawnSeconds`, `RespawnDelayMs`, `GameObjectOptions` | `GameObject.cpp:698-711`, `:1002` |
| `SMSG_GAMEOBJECT_DESPAWN_ANIM` (u64 guid) before the destroy for despawn-at-action objects and any object with animprogress > 0 | `GameObjectPackets.DespawnAnim` | `GameObject.cpp:654-657`, `D:\refs\wow_messages\wow_message_parser\wowm\world\gameobject\smsg_gameobject_despawn_anim.wowm` |
| `GameObjectSpawn.SpawnTimeMaxSeconds` and `SpawnFlags` content fields (null/0 keep a fixed delay); persisted by GO3 | `GameObjectContent.cs` | |

Config: section `GameObjects` (`GameObjectOptions`), bound by `GameObjectLootFeature.ObjectOptions`.

Tests: `GameObjectFidelityPrimitivesTests` (new); the existing door test was changed to the raw seconds*0x10000 value.

### GO7a spell focus enforcement

| Item | Where | Reference |
|---|---|---|
| `SpellFocusCastCheck` (`ISpellCastCheck`, phase `Items`, after the equipment checks): a non-passive spell with `RequiresSpellFocus` needs a spawned `SPELL_FOCUS` object of that focus id whose data1 radius reaches the caster, else `RequiresSpellFocus`. 3D distance, strictly below data1 plus both bounding radii, no clamp to 1 yard. No triggered-cast exemption | `src/ArcaneCore.Game/Spells/Checks/SpellFocusCastCheck.cs` | `D:\refs\vmangos\src\game\Spells\Spell.cpp:7230-7243`, `Maps\GridNotifiers.h:586-606`, `Objects\Object.cpp:1738-1752` |
| `GameObjectMapSystem.FindSpellFocus` (lowest spawn guid wins); `HasSpellFocusNearby` now delegates to it (its old `Math.Max(1, data1)` clamp and radius-free test were not retail) | `GameObjectMapSystem.cs` | same |
| `SpellFocusFeature` (discovered `IWorldFeature`) registers the check with the per-map systems of `GameObjectLootFeature`; config `Spells:RequireSpellFocus` (default true) | `src/ArcaneCore.World/GameObjects/SpellFocusFeature.cs` | |

Behaviour to know: a map without game object content finds no focus object, so focus spells (forges, anvils, cooking fires, 695 classic-db spells)
fail with `RequiresSpellFocus`, exactly as an empty map does in retail. Set `Spells:RequireSpellFocus=false` for content-less development worlds.
The GM no-check-cast cheat exemption (Spell.cpp:5304) is not modelled (ArcaneCore has no such cheat). The 10 yard grid pre-filter of the search
(Spell.cpp:7236-7240) is not modelled; vmangos visits whole cells, so it never excludes an object the distance test accepts.
`Spell::focusObject` (kept for spell visuals) is not stored.

Tests: `SpellFocusCastCheckTests` (Game.Tests), `SpellFocusWorldTests` (World.Tests).

### GO3 spawn data import (world schema step)

| Item | Where | Reference |
|---|---|---|
| World schema **15** (`GameObjectSpawnDataModule.Version`, the integrator renumbers): `gameobject_spawn.SpawnTimeMaxSeconds` (nullable int, null = same as the minimum) and `SpawnFlags` (uint, default 0), both `AddColumnChange`; the step is rerunnable after a partial application | `src/ArcaneCore.Data/World/GameObjects/GameObjectSpawnDataModule.cs` | `D:\refs\vmangos\src\game\Objects\GameObjectDefines.h:814-832` |
| Importer reads `spawntimesecsmax` (a max below the min is raised to the min), `spawn_flags`, and the cmangos `gameobject_addon` table (`animprogress`, `state`, -1 = unset; state >= 3 is an invalid row, skipped with a warning) in any table order | `GameObjectLootDumpImporter` | `D:\refs\mangos-classic\src\game\Globals\ObjectMgr.cpp:2188-2192, 2252-2282` |
| Initial state: addon state if not -1, else the `gameobject` row own state column (vmangos), else a door/button with template `startOpen` (data0) starts active/open, else ready; animprogress addon, else row, else 100 | `ResolveSpawnData` | `D:\refs\mangos-classic\src\game\Entities\GameObject.cpp:226-250, 920-927`, `D:\refs\vmangos\src\game\Objects\GameObject.cpp:239-248` |
| `EfGameObjectDataStore` maps both columns into `GameObjectSpawn` | `EfGameObjectLootStores.cs` | |

Verified against the real classic-db z2815 dump (opt-in test, `ARCANECORE_CLASSICDB_DUMP`): 47827 spawns, 6056 with min != max, all states within 0..2; the numbers were counted independently with a python scan.
Provider coverage: the schema tests (`GameObjectSpawnDataTests`, `IntegratedSchemaTests`) are provider theories over `TestDatabases.AvailableProviders` written for MariaDB (non-transactional DDL: a partly applied step is completed by the rerun) and PostgreSQL
(quoted identifiers through `ISqlGenerationHelper`), but **only SQLite was available on this machine**; the MariaDB and PostgreSQL runs happen on hosted CI.
Not modelled: the `spawnMask` column, vmangos `visibility_mod` (cannot be verified, no vmangos world dump in the references), `gameobject_spawn_entry`/pools/game events (no lane owns them).

### GO5 chairs and cameras

| Item | Where | Reference |
|---|---|---|
| Chair use: the user must be within 3 yards (3D, no radii) of the nearest slot, then needs line of sight to the chair; they are moved to the slot at the chair orientation (same-map teleport through `GameObjectMapSystem.Teleports`, default `NearTeleportSink`: relocation plus `MSG_MOVE_TELEPORT_ACK`) and sit with `SIT_LOW_CHAIR` + chair height. A refused use is `TooFar` / the new `LineOfSight` result and silent for the client. 2766 classic-db spawns are chairs | `GameObjectMapSystem.UseChair` | `D:\refs\vmangos\src\game\Objects\GameObject.cpp:1515-1533`, `:2229-2236`, `GameObjectDefines.h:799` |
| Slot geometry: `data0` slots on the line perpendicular to the orientation, spaced by the template size, nearest slot wins, a later slot wins a tie, centre when there are no slots or none within 100 yards | `GameObjectChairs.ClosestSlot` | `GameObject.cpp:2536-2582` |
| Chair height data1 0..2, a larger value is a data error fixed to 0 | `GameObjectChairs.Height` | `D:\refs\vmangos\src\game\ObjectMgr.cpp:8108-8118` |
| Camera: `SMSG_TRIGGER_CINEMATIC` (u32 cinematic id from data1, when non-zero) | `GameObjectMapSystem.UseCamera`, `CinematicPackets` | `GameObject.cpp:1613-1634`, `Player.cpp:6049-6056`, `Server/Packets/Misc.cpp:789-797` |

The 1.12 layout of the cinematic packet is taken from vmangos only (the wow_messages definition is 3.3.5-only); it has not been checked against a real client.
Not modelled: dismounting a mounted user before sitting (vmangos does it in the Use prologue), the camera event id (data2, needs the scripts engine), the server-side cinematic state
of `Player::CinematicStart` (camera path, explore check at the end), `onlyCreatorUse` (vmangos does not read it either).
CMSG_STANDSTATECHANGE already stands a seated player up (existing handler).

Tests: `ChairCameraTests`.

## Limits (not delivered, documented)

* Goober Use semantics (page before the quest gate, group quest credit, IN_USE/ACTIVATED machine, use spell, linked trap,
  gossip branch `goober.gossipID`), PlayerCanUse, mounted dismount, immunity gate, button display 295 LOS: GO2 not delivered.
  The existing goober still returns `InUse` while an auto-close goober is active (vmangos has no such gate, GameObject.cpp:1541-1611).
* Spawn flag 0x08 (dynamic respawn time, realm population) and 0x01 (active object) are carried but not modelled.
* Spawn flag 0x02 (disabled) is honoured: such a spawn is never loaded (GameObject.cpp:969, ObjectDefines.h:128); the vmangos force path (GM or script spawn of a disabled row) is not modelled.
* `SMSG_GAMEOBJECT_SPAWN_ANIM` / `RESET_STATE` builders are not added (they have no caller until the object-spell slice).
* Behaviour seam (`IGameObjectBehavior`: the per-type switch in `GameObjectMapSystem.Use` stays, chairs and cameras were added to it in place), per-object
  visibility modifiers, page text, spell-created objects (TRANS_DOOR, SUMMON_OBJECT_*, ACTIVATE_OBJECT), traps/spell casters/rituals, transports: see the lane report.
