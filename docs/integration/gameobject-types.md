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

## Wave 2 (`claude/w2-gameobjects`): the types the review listed as missing

Base `2ca2f4e`. Every row is re-implemented from `D:\refs\vmangos` (0e3ff01); classic-db z2815 counts were taken from
`gameobject_template` (type 3 chests: 129 with a restock time, 104 with min/max opens, 59 with a linked trap, 6 with a level; type 18: 8 rituals;
type 22: 17 spell casters; type 23: 25 meeting stones; type 24: 8 flag stands; type 12: none).

| Item | Where | Reference |
|---|---|---|
| Chest level gate: chest.level (data9) more than 10 above the opener refuses with `SMSG_LOOT_RELEASE_RESPONSE` and a warning (vmangos: passive anticheat report) | `GameObjectMapSystem.ChestLevelRefuses`, result `LevelTooLow` | `Objects/Player.cpp:7656-7665` |
| Keys: the open-lock spell opens a key case when the key is the cast item, without skill-up or caster skill; a key that opens a chest from the bags is used up by its own spell charges (an expendable key, e.g. Dull Iron Key 3467 with spell 3366 charges -1, is destroyed; a key without a spell stays) | `GatheringSpells` (cast item to `GatheringRules.CanOpenLock`), `GameObjectLocks.CheckDirectUse(.., out key)`, `GameObjectMapSystem.UseUpKey`, `ItemSpellCharges.TakeCharge` | `Spells/Spell.cpp:7869-7923, 4991-5048`, `Spells/SpellEffects.cpp:2191-2192` |
| Chest restock: a non-consumable chest with chestRestockTime (data2) stays in the world, empty and `NotReady`, until the time ran out, then gives fresh loot. Not for dungeon chests whose loot is stored with the save | `TryStartRestock`, `UpdateRestock` | `Objects/GameObject.cpp:381-394, 629-639` |
| Multi-use veins: min/maxSuccessOpens (data4/5); ready again below min, then `100*pow(0.8*next, 4/max*uses) + mining/(lockSkill+25)` percent, used up at max; skill-up list kept between opens. Config `GameObjects:MiningAmountRate`, `GameObjects:MiningNextRate` (vmangos Rate.Mining.Amount/Next, default 1) | `VeinStaysAfterLooting`, `ReadyVeinAgain`, `OnLootReleased(.., releaser)` | `Handlers/LootHandler.cpp:435-487` |
| Goober: page text or gossip (data19) shown before the quest gate; questId is signed, -1 usable and sparkling for everyone; the goober spell (data10) is cast by the object at the user; linked trap | `ShowGooberPageOrGossip`, `GooberQuestId`, `CastGooberSpell`, `IGameObjectGossip` to `QuestNpcServices.OpenGameObjectGossip` | `GameObject.cpp:1541-1611, 1245-1250` |
| Spell caster (type 22): flags become GO_FLAG_LOCKED, party-only (data2) by the owner's raid or, without owner, the creator's group; use counted, charges (data1) used up in the update; the object casts data0 at the user | `UseSpellCaster`, `UpdateTypeBehaviour` | `GameObject.cpp:1798-1834, 555-561` |
| Linked traps: a button/chest/spell focus/goober click, and the open-lock spell on a chest or button (Spell::SendLoot runs GameObject::Use), springs the nearest linked trap within the trap spell's max range (0.5 yd without) when that nearest one is spawned (a despawned nearest trap is not replaced by a further one); the trap's own cooldown (data5) gates it as GameObject::Use gates any object with a cooldown; the trap respawns with its object; spell-created objects get their trap (`SummonLinkedTrapIfAny`) | `TriggerLinkedTrap`, `RespawnLinkedTrap`, `UseTrap`, `SummonLinkedTrapIfAny`, `OpenLock` | `GameObject.cpp:1258-1347, 1421-1428, 1487-1513, 427-437`, `Spells/SpellEffects.cpp:2048-2068`, `Maps/GridNotifiers.h:639-661` |
| Environmental traps (no owner, or no charges): arm on the first update (startDelay data7), nearest living player within radius (both bounding radii, 3D), cast by owner or object, cooldown data5 (4 s default), charges | `UpdateEnvironmentalTrap` | `GameObject.cpp:340-357, 455-560`, `Maps/GridNotifiers.h:1228-1241` |
| Area damage (type 12): no vmangos behaviour and no 1.12 rows; interaction distance 0 so no client can use it; `ActivateAreaDamage` (scripts, GM, spells) follows the template columns: activates like a door, auto-close data5, one roll damageMin..Max to living players in radius, environmental log (slime for nature, fire otherwise), no absorb or resist | `UseAreaDamage`, `ActivateAreaDamage` | `GameObjectDefines.h:364-375, 780`, `GameObject.cpp:1981-1983` |
| Flag stand (type 24): a player who may use battleground objects loses stealth and invisibility and the click goes to the battleground; elsewhere nothing | `UseFlagStand`, `IGameObjectFlagStands` | `GameObject.cpp:1843-1870` |
| Summoning rituals (type 18): AddUniqueUse (first user, anim spell for non-owners), owner/raid/channel rules, wild rituals grouped by first user, ritual spell by the owner (first user for a wild one) with the summon target for the warlock portal 36727, FinishRitual (creating-spell cooldown, non-persistent used up, Ritual of Doom sacrifice), RemoveUniqueUse (an active, unfinished non-persistent ritual is detached from its owner to keep it running, so the owner's channel end or logout leaves it), the owner's channel end and leaving the map; the channel-end and leave hooks visit an index of the map's rituals, not every object | `GameObjectMapSystem.Rituals.cs` | `GameObject.cpp:739-831, 1733-1796, 1993-2027`, `Spells/Spell.cpp:3560-3597, 4400-4406` |
| TRANS_DOOR for objects that are not bobbers (portals, rituals, Lightwell): destination, else effect radius ahead (no travel time), else random within range and arc; spell duration, owner, owner group, level, creating spell, linked trap; ritual summon target = caster selection | `GameObjectSpellEffects.Transmit` (registered before `FishingSpells`, which hands it every non-bobber) | `Spells/SpellEffects.cpp:5648-5790` |
| SUMMON_PLAYER and CMSG_SUMMON_RESPONSE (u64 summoner): two-minute offer `SMSG_SUMMON_REQUEST` (u64 summoner, u32 zone, u32 ms); accepted by a living player out of combat while it lasts; a summon target on another map gets the request directly | `GameObjectSpellEffects.Offer/Accept`, `SummonResponseHandlers`, `Player.PendingSummon` | `Spells/SpellEffects.cpp:4783-4800`, `Objects/Player.cpp:19636-19674`, `Handlers/MovementHandler.cpp:981-987` |
| Object spells: `SpellSystem.CastForGameObject` casts as the owner (a trap's owner), or as the object itself through its target as stand-in. The stand-in judges others as the object (GameObject::IsHostileTo / IsFriendlyTo: GM never hostile, an owner answers for the object, a pet or charmed unit is judged by its charmer or owner, faction 0 hostile to all and friendly to none, then a player's forced reaction or reputation standing, then the template relations; neutral is neither), through the map's reputation or faction hooks. It brings no caster side of its own (no spell power, healing power, done percentages, spell mods or crit: a game object has none) and effect values scale with the object's level (chest.level, trap.level, GAMEOBJECT_LEVEL, else 60). Hunter traps now publish their owner (Unit::AddGameObject) | `SpellSystem.GameObjectCasts.cs`, `GameObjectReactions`, `SpellBonusModule`, `GameObjectTypesFeature` | `GameObject.cpp:2076-2166, 2492-2510`, `Objects/SpellCaster.h:320`, `Objects/SpellCaster.cpp:1147-1200, 1457-1700`, `Objects/Object.cpp:3818-3857`, `Objects/Unit.cpp:4084-4101` |
| Meeting stones (type 23): `CMSG_MEETINGSTONE_JOIN/LEAVE/INFO`, the LFG queue (roles, priorities, a party of two from five solo players, completion, IN_PROGRESS reminder) and the group hooks | `Game/Lfg/LfgQueue.cs`, `MeetingStoneFeature`, `MeetingStoneHandlers`, `GroupManager.Lfg.cs` | `LFG/LFGHandler.cpp`, `LFG/LFGQueue.cpp`, `LFG/LFGMgr.cpp`, `Group/Group.cpp:418-740` |

Already present before this lane: the camera's `SMSG_TRIGGER_CINEMATIC` (GO5 above). The camera event id stays unrun (no scripts engine).

Deliberate deviations: `CMSG_MEETINGSTONE_INFO` answers JOINED for a solo player in the queue (vmangos reads an offline store nothing fills and
answers NONE); a key used from the bags is spent by its spell charges on the direct-use path as the item cast would spend it (vmangos has no
direct-use opening of chests at all).

Limits: the object stand-in names the target as the caster in spell packets and damage and heal logs (vmangos names the object); its hit,
resist and armor rolls use the stand-in's own level and skills (so no level difference) where vmangos uses the object's level, and the hit
roll still reads the stand-in's hit-chance spell mods; the damage-taken hooks see the stand-in as the attacker, an aura the cast applies
remembers the stand-in as its caster, and a spell the stand-in casts itself while the object's cast is under way (a proc) also goes without
its caster side; the charmer or owner is read from UNIT_FIELD_CHARMEDBY / SUMMONEDBY and must be in the same map; a restocking chest
refuses the open (vmangos shows an empty window and restarts the timer on release); triggered channels do not channel here, so a ritual
helper's animation spell keeps no channel and its end is not seen; a wild grouped ritual finds its first user only in the same map;
an owned trap with charges that is not a hunter trap (a linked trap of a spell object) is not scanned; battleground buff traps (radius 0,
cooldown 3) are left to the battleground area; the flag stand's battleground side is a seam until the battleground area is wired into the
world; LFG talent-based roles (`LFG.Matchmaking`) are not modelled (class roles only); `SPELL_EFFECT_ACTIVATE_OBJECT` does not exist yet.

Data needed for the real content: the spells the objects cast come from Spell.dbc through `tools/spell-import` (Ritual of Summoning 698 and
its effect 7720, Ritual of Doom 18540/18541/20625, mage portals 10059/11416-11420 and their effects 17334/17607-17611, Lightwell 724/27870/27871
and 7001/27873/27874, the key spell 3366); the objects themselves are classic-db `gameobject_template` rows the importer already reads.

Tests: Game `GameObjectChestTypeTests`, `GameObjectSpellTypeTests`, `SummoningRitualTests`, `Spells/GameObjectCastTests`, `Spells/GameObjectStandInTests`,
`Social/MeetingStoneQueueTests`; World `GameObjectTypesWorldTests`, `InactiveQueueHandlerTests` (meeting stone join and leave now registered),
playerbot scenarios `meeting-stone` and `ritual-of-summoning` (`Playerbots/Scenarios/GameObjectScenarioTests`).

## Limits (not delivered, documented)

* Goober Use semantics: page before the quest gate, use spell, linked trap and the gossip branch were delivered in wave 2 (above); group
  quest credit, PlayerCanUse and the button display 295 LOS are still not delivered.
  The existing goober still returns `InUse` while an auto-close goober is active (vmangos has no such gate, GameObject.cpp:1541-1611).
* Spawn flag 0x08 (dynamic respawn time, realm population) and 0x01 (active object) are carried but not modelled.
* Spawn flag 0x02 (disabled) is honoured: such a spawn is never loaded (GameObject.cpp:969, ObjectDefines.h:128); the vmangos force path (GM or script spawn of a disabled row) is not modelled.
* `SMSG_GAMEOBJECT_SPAWN_ANIM` / `RESET_STATE` builders are not added (they have no caller until the object-spell slice).
* Behaviour seam (`IGameObjectBehavior`: the per-type switch in `GameObjectMapSystem.Use` stays, chairs and cameras were added to it in place), per-object
  visibility modifiers, page text, ACTIVATE_OBJECT and transports are still open; TRANS_DOOR, traps, spell casters and rituals were delivered in wave 2.
