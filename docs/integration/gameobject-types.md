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

## Limits (not delivered, documented)

* Goober Use semantics (page before the quest gate, group quest credit, IN_USE/ACTIVATED machine, use spell, linked trap,
  gossip branch `goober.gossipID`), PlayerCanUse, mounted dismount, immunity gate, button display 295 LOS: GO2 not delivered.
  The existing goober still returns `InUse` while an auto-close goober is active (vmangos has no such gate, GameObject.cpp:1541-1611).
* Spawn flag 0x08 (dynamic respawn time, realm population) and 0x01 (active object) are carried but not modelled.
* `SMSG_GAMEOBJECT_SPAWN_ANIM` / `RESET_STATE` builders are not added (they have no caller until the object-spell slice).
* Behaviour seam (`IGameObjectBehavior`), visibility modifiers, chairs/cameras, spell-focus enforcement and summon effects,
  traps/spell casters/rituals, transports: see `slices_not_done` of the lane report.
