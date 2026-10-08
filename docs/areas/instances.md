# Area: instances and bosses (wave 2, lane instances-bosses)

Status: first slices delivered on `claude/vw2-instances-bosses`. WoW 1.12.1 (5875). Base behaviour (saves, binds, raid resets, homebind timer, durable chests) is described in `docs/integration/instances.md`; this page records what this lane changed on top of it, what it left open, and the provenance of every rule.

References (GPL, read for verification only; nothing copied): `D:\refs\vmangos` (primary), `D:\refs\mangos-classic`, `D:\refs\wow_messages`, `D:\refs\classic-db`.

## Delivered

| Slice | Behaviour | Reference |
|---|---|---|
| IB0 lookups and events | `InstanceManager` is a partial class. `GetGoBackTrigger(map)`, `GetMapEntranceTrigger(map)` (lowest trigger id when several qualify; vmangos walks an unordered map), `IsSaveLive(map, instance)`, `GetParentMapChain(map)`, events `Loaded` (end of `Load`) and `SaveCreated`. `RelocateToEntrance` now uses `GetGoBackTrigger`. Other lanes (ghost-dungeon rules, summon, state) use these, not private copies. | vmangos `ObjectMgr.cpp:7789-7822` |
| IB1 saved-instance packets | After every far teleport (instance or continent) the player gets `SMSG_UPDATE_INSTANCE_OWNERSHIP` (u32 bool: any permanent bind) and one `SMSG_UPDATE_LAST_INSTANCE` (u32 map) per permanent bind, ordered by map id. | vmangos `Player.cpp:16002-16031`, `:2113-2120`; wow_messages `raid/smsg_update_instance_ownership.wowm`, `raid/smsg_update_last_instance.wowm` |
| IB7a bind credit and reset time | Kill handling moved to `InstanceManager.Binds.cs`. A credited player is resolved through `IKillCreditResolver` (default: the killer if it is a player; the pets and tap lanes replace it). Raid: permanent bind only for a creature with `ExtraFlags & 1`. Normal dungeon: never a permanent bind; `ResetTime = max(ResetTime, now + respawn delay + 2 h)` on a credited kill, re-persisted through `InstanceSaved`, the scheduler flag (`ResetScheduled`) untouched ("set but not added to the scheduler until the players leave"). | vmangos `Unit.cpp:1253-1263`, `Map.cpp:3526-3545` |
| IB2 (partial) entry rules | Per-account hourly new-instance limit (`InstanceEnterLimiter`) and vmangos check order: raid group, hourly limit, then player cap (GMs exempt), then pending reset (GMs refused too). | vmangos `MapManager.cpp:185-218`, `AccountMgr.cpp:441-472`, `Map.cpp:2124-2146`, `:2188` |

Options (`World:Instances`, defaults retail): `ResetExtendsOnKills=true`, `PerHourLimit=5` (0 turns it off; vmangos would refuse everything at 0).

## Deliberate behaviours and limits

- The hourly limit is checked on teleports only, not on the login re-entry, and is in memory (a restart clears it, as in vmangos). At the cap vmangos erases whichever expired entry its unordered map yields first; here the oldest. Whether retail 1.12 enforced the limit is unverified (both reference servers default it on).
- The respawn delay of the killed creature is `RespawnAtMs - CreatureMapSystem.ClockMs`, i.e. the delay the creature system drew at death. A creature without a spawn row counts as delay 0 (reset = now + 2 h).
- The saved-instance packets are sent when the player has arrived (after the worldport ack), not together with `SMSG_NEW_WORLD`, because the teleport service belongs to another lane. Real-client confirmation of the packets is still required.
- The raid bind flag is classic-db's `creature_template.ExtraFlags` bit 1 (`CreatureFlagExtraInstanceBind`); vmangos's own static flag `LOCK_TAPPERS_TO_RAID_ON_DEATH` is not set in that data.

## Not delivered (needs another lane or a decision)

- `IgnoreLevel` option and the `IAreaTriggerGate` seam in `TeleportHandlers` (shared with the death-persistence ghost branch; merge order to be agreed).
- Area-trigger item/quest/condition requirements (needs content-import-full columns and the condition evaluator).
- Encounter completion mask, instance variables, DB-driven door rules, encounter-in-progress zone lock, game-object respawn persistence, loot-open reset extension, `creature_respawn`-derived reset time at load, dungeon mount rule (spell lane), visibility distance, schedule-interval/GM reset tooling, spawn-choice persistence. Designs and reference citations for all of them are in the lane design; none is partially implemented.
- Delivered elsewhere since: the summoning rituals (Ritual of Summoning, the summon request and its answer) and the meeting stones with their LFG queue belong to the game object types ([gameobject-types.md](../integration/gameobject-types.md)); they do not touch instance binds.
- Group binds still do not survive a restart (groups are memory-only).
