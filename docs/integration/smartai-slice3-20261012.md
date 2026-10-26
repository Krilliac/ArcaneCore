# SmartAI slice 3a: creature emote, evade, aura and despawn actions (2026-10-12)

Grok lane, branch `grok/w37-smartai-slice3`. Builds on slice 2 (`smartai-slice2-20261010.md`). Numbering is AzerothCore's.

## Added actions (creature and timed action list sources)

| Action | Name | Behaviour here |
|---|---|---|
| 5 | PLAY_EMOTE | Every creature target plays param1 through `CreatureMapSystem.PlayEmote` (SMSG_EMOTE for a one-shot emote). |
| 17 | SET_EMOTE_STATE | Every unit target gets `UNIT_NPC_EMOTESTATE` = param1. |
| 24 | EVADE | The script's own creature evades (`CreatureMapSystem.EnterEvadeMode`); the target is ignored, as in AzerothCore. |
| 28 | REMOVEAURASFROMSPELL | Every unit target loses the auras of spell param1; param1 0 strips every aura (creature targets only). |
| 41 | FORCE_DESPAWN | Every creature target despawns after param1 ms (0 at once) through `CreatureMapSystem.ForcedDespawn`. |

The creature action set is now `1, 5, 11, 12, 17, 22, 23, 24, 28, 30, 31, 41, 67, 69, 73, 74, 80, 87, 88`. Game object and area trigger sets
are unchanged; a game object row with action 24 is refused with "a game object cannot evade". A timed action list run by a game object skips
action 24 (it needs a creature owner, like 12 and 69).

## Deviations

- **PLAY_EMOTE on players.** AzerothCore plays the emote on any unit target. There is no player emote seam on this path, so player targets
  are skipped. `PlayEmote` also keeps a state emote (EMOTE_STATE_*) as the emote state rather than playing it once, as the DB-script path does.
- **REMOVEAURASFROMSPELL 0 on players.** "Remove all auras" exists only for creatures here (`CreatureMapSystem.RemoveAllAuras`), so a player
  target with param1 0 keeps its auras. The `onlyOwnedAuras` param is ignored.
- **FORCE_DESPAWN respawn param.** AzerothCore's param2 (forced respawn timer) is ignored; a database spawn respawns after its own delay.

## Slice 3b (branch `grok/w37-smartai-slice3b`)

| Action | Name | Behaviour here |
|---|---|---|
| 2 | SET_FACTION | Every creature target takes faction param1; 0 restores its template faction. |
| 3 | MORPH_TO_ENTRY_OR_MODEL | Every creature target wears a model of template param1, else model param2, else its native model. |
| 20 | AUTO_ATTACK | `me` starts (param1 1) or stops (0) its melee swing. |
| 21 | ALLOW_COMBAT_MOVEMENT | `me` chases its victim (1) or stands (0); no change when already so. |
| 37 | DIE | `me` dies without a killer (`CreatureMapSystem.KillCreature`). |

Actions 20, 21 and 37 act on the script's own creature, so a game object row with them is refused, and a game-object-run timed list skips them.
The creature action set is now `1, 2, 3, 5, 11, 12, 17, 20, 21, 22, 23, 24, 28, 30, 31, 37, 41, 67, 69, 73, 74, 80, 87, 88`.

## Slice 3c (branch `grok/w37-smartai-slice3c`)

Creature events now run: `0, 1, 2, 4, 5, 6, 7, 8, 17, 21, 22, 25, 59, 60, 61`.

| Id | Name | Behaviour here |
|---|---|---|
| event 5 | KILL | `me` killed a unit (`OnKilledUnit`); param3 player victims only, param4 victim entry (0 any); cooldown param1..param2. |
| event 17 | SUMMONED_UNIT | `me` summoned a creature (`OnJustSummoned`); param1 its entry (0 any); cooldown param2..param3. |
| event 22 | RECEIVE_EMOTE | a player aimed text emote param1 at `me` (`OnReceiveEmote`); the player is the invoker; cooldown param2..param3. |
| action 4 | SOUND | each creature target plays sound param1 to the players that see it (players as targets are skipped; `onlySelf` ignored). |
| action 39 | CALL_FOR_HELP | idle same-faction creatures within param1 yards join `me`'s fight (`CreatureMapSystem.CallForHelp`); the emote param is ignored. Refused for game objects. |

## Slice 3d (branch `claude/smartai-slice3`, 2026-10-16)

Picked by row count. The live world DB (`complete-r5/world.db`) has no `smart_scripts` rows: its content is cmangos/ClassicDB EventAI. So the count
comes from the reference data that ported rows will come from: AzerothCore `data/sql/base/db_world/smart_scripts.sql`, filtered to creature rows
(`source_type` 0, positive entry, not DEBUG_ONLY) whose entry exists in the live `creature_template`. That is 12,448 rows over 4,224 entries.
Before this slice 7,874 of them could run in full (event, action and target all supported). After it, 9,285 can (+1,411). Fully runnable entries
went from 2,261 to 3,087. Action 25 alone accounts for 699 rows and event 9 for 298.

Creature events now run: `0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 14, 17, 21, 22, 25, 59, 60, 61`.
The creature action set is now `1, 2, 3, 4, 5, 8, 10, 11, 12, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28, 30, 31, 37, 39, 41, 49, 67, 69, 73, 74, 80, 87, 88`.

| Id | Name | Behaviour here (AzerothCore SmartScript.cpp / SmartAI.cpp) |
|---|---|---|
| event 3 | MANA_PCT | `me` in combat with mana between param1 and param2 percent; repeat param3..param4 (ProcessEvent SMART_EVENT_MANA_PCT). |
| event 9 | RANGE | `me`'s victim within param5..param6 yards (3D, plus both bounding radii, as `WorldObject::IsInRange`); the victim is the invoker; first timer param1..param2, repeat param3..param4; out of range re-checks in 1200 ms. |
| event 11 | RESPAWN | raised after the reset on spawn and on every respawn (`SmartAI::InitializeAI`, `JustRespawned`); param1 1 needs map param2, 2 needs zone param3. |
| event 14 | FRIENDLY_HEALTH | the living, in-combat friendly creature within param2 yards missing more than param1 health is the invoker; none re-checks in 1000..3000 ms; repeat param3..param4. |
| action 8 | SET_REACT_STATE | every creature target takes react state param1 (0 passive, 1 defensive, 2 aggressive). |
| action 10 | RANDOM_EMOTE | every creature target plays one of the non-zero params 1-6, picked per target. |
| action 18/19 | SET/REMOVE_UNIT_FLAG | every unit target gains or loses param1 in UNIT_FIELD_FLAGS. |
| action 25 | FLEE_FOR_ASSIST | `me` runs to the nearest creature that can help, else flees (`CreatureMapSystem.FleeForAssistance`, cmangos `DoFleeToGetAssistance`); param1 1 also says broadcast text 1150 ("%s attempts to run away in fear!", BROADCAST_TEXT_FLEE_FOR_ASSIST) as an emote. A stunned, confused or fleeing creature does not flee, and a casting one is put off by 1200 ms (UpdateTimer). |
| action 49 | ATTACK_START | `me` attacks one target picked at random (`AttackStart`). |

Load rules added (SmartScriptMgr.cpp `IsEventValid`/`IsActionValid`): event 3 and 9 min above max for any of their pairs, event 14 radius 0 or repeat
min above max, action 8 a react state above 2, action 10 with no non-zero emote, and action 18/19 with param2 set (UNIT_FIELD_FLAGS_2 does not exist in
1.12). Actions 25 and 49 act on the script's own creature, so a game object row with them is refused, and a game-object-run timed list skips them.
Game objects, area triggers and their sets are unchanged (RESPAWN for game objects is still unhooked).

Deviations:

- **RANGE uses AzerothCore's current layout.** Older TrinityCore rows put the distances in params 1-2. The AzerothCore data uses params 5-6, and so
  does this engine.
- **FRIENDLY_HEALTH searches creatures only.** AzerothCore's `MostHPMissingInRange` visits every unit that is not hostile. This uses
  `CreatureAI.SelectLowestHpFriendly` (cmangos `DoSelectLowestHpFriendly`), so a hurt friendly player is not picked.
- **FLEE_FOR_ASSIST ignores the run-speed check.** AzerothCore also skips a creature whose run speed is below 0.1. A creature with no victim does not
  flee, as cmangos `DoFleeToGetAssistance` returns then.
- **RESPAWN does not raise REACHED_HOME.** AzerothCore's `JustRespawned` calls `JustReachedHome`, which also raises REACHED_HOME. Here a spawn runs the
  reset, then RESPAWN only, as before this slice.
- **RANDOM_EMOTE and players.** As PLAY_EMOTE, player targets are skipped.

## Slice 3e (branch `claude/smartai-pr-a`, 2026-10-26)

References: AzerothCore azerothcore-wotlk at `4ad75f467ad0cdb1991869e9a8b5ff8555ad0232`, files under `src/server/game/AI/SmartScripts/`.

Creature events now run: `0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 14, 17, 20, 21, 22, 25, 59, 60, 61`. Actions 34 and 50 run for creature, timed
action list and game object sources.

| Id | Name | Behaviour here |
|---|---|---|
| event 20 | REWARD_QUEST | `me` rewarded quest param1 (0 any) to a player (`OnQuestRewarded`, AC `SmartAI::sQuestReward`, SmartAI.cpp:1221-1224); the player is the invoker; cooldown param2..param3 (SmartScript.cpp:4728-4735). A cooldown with min above max is refused (SmartScriptMgr.cpp:1247-1252). |
| action 34 | SET_INST_DATA | `InstanceData.SetData(param1, param2)` on the base object's map, else the invoker's; no instance script does nothing (SmartScript.cpp:1236-1269). Type (param3) 1, SetBossState, is refused at load: `InstanceData` has no boss-state call. Types above 1 are refused as AC does (SmartScriptMgr.cpp:1916-1931). |
| action 50 | SUMMON_GO | Object param1 per target at the target's position plus the target offsets, or at the base object when param3 (targetsummon) is set; and at a POSITION target's point. Param2 is the despawn time in seconds (SmartScript.cpp:1651-1681; `Object.cpp:2494`, `SetRespawnTime`), through `GameObjectMapSystem.Summon`. |

Event 20 is not hooked for game objects: ArcaneCore has no game object AI quest-reward hook (a game object's reward reaches the instance script only).

**Event 40 is not delivered.** It is ESCORT_REACHED (SmartScriptMgr.h:139), not WAYPOINT_REACHED (108, :197). AzerothCore raises it only while a
SmartAI escort started by SMART_ACTION_WP_START (53) is running, with the path id that action loaded (SmartAI.cpp:262-298, 662-720); waypoint motion
raises 108 instead (:711-712). ArcaneCore runs neither action 53 nor an escort for a SmartAI creature, so there is no point or path id to match.
Rows with event 40 stay refused, now with the reason "ESCORT_REACHED needs a SMART_ACTION_WP_START escort".

Limits: SUMMON_GO's summonType (param4) is ignored, as in the AzerothCore action. A summoned object with an unknown entry is not placed
(`GameObjectMapSystem.Summon` returns null); the catalog does not check object entries. AzerothCore logs a SET_INST_DATA without an instance script;
this script has no logger, so it does nothing silently.

## Still open (slice 3f and later)

By the same count, the largest single blockers left are event 38 DATA_SET (205 rows that need nothing else), 40 ESCORT_REACHED (160, needs action
53 and an escort), 62 GOSSIP_SELECT (132), 106 and 105 (AzerothCore-only ids, 98 and 84) and 52 TEXT_OVER (76). Also open: MOVEMENTINFORM (34, needs
the AC motion-type numbering mapped), the remaining rejected events, actions and targets in the slice-2 tables, AIName for game objects, and ending
the area-trigger handler when a script ran.
