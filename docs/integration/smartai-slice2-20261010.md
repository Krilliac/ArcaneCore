# SmartAI (w19, slice 2)

Slice 2 extends the slice-1 engine ([smartai-20261010.md](smartai-20261010.md)) with event conditions and three more sources: game objects
(`source_type` 1), area triggers (2) and timed action lists (9). The format is still AzerothCore's `smart_scripts` (SmartScriptMgr.h/.cpp,
SmartScript.cpp, SmartAI.cpp), re-implemented, no code copied. The sets below are the single source of truth in
`ArcaneCore.Kernel/WorldData/Creatures/SmartScriptSupport.cs`; the loader validates every row against them.

## Load rules (fail closed)

`SmartScriptCatalog` admits a row in this order, and every refused row is kept in `SmartScriptCatalog.Rejected` with its reason and logged by the
creature world feature. Nothing is dropped silently except DEBUG_ONLY (0x80) rows, which AzerothCore also skips.

1. `entryorguid` 0 is refused (SmartScriptMgr.cpp:141-210).
2. `source_type` of 10 or more is invalid. Types 3-8 are refused as not implemented (AzerothCore reports them the same way). Only 0, 1, 2 and 9 load.
3. A negative `entryorguid` (a spawn guid) is valid only for creature and game object rows. Area trigger and timed list rows keep a positive id.
4. With references, the creature or game object template (or spawn, for a negative id) and the area trigger must exist, and a `ConditionId` must name
   a row of the `conditions` table.
5. The structural checks of `SmartScriptSupport.Check`: AzerothCore must allow the event for the source (`SmartAIEventMask`,
   SmartScriptMgr.h:1844-1930), and the event, action and target must be in the sets below.
6. Actions 80, 87 and 88 may call only lists that keep usable rows. A list row can itself be refused, so the check repeats until the set is stable.

## Supported sets per source

Numbering is AzerothCore's. "Rejected" is everything not listed.

| Source | Events | Actions | Targets |
|---|---|---|---|
| 0 creature | 0, 1, 2, 4, 6, 7, 8, 21, 25, 59, 60, 61 | 1, 11, 12, 22, 23, 30, 31, 67, 69, 73, 74, 80, 87, 88 | 0-9, 11, 17, 18, 19, 21, 23, 24 |
| 1 game object | 1 UPDATE_OOC, 37 AI_INIT, 59, 60, 61, 63 JUST_CREATED, 64 GOSSIP_HELLO | 1, 11, 22, 23, 30, 31, 67, 73, 74, 80, 87, 88 | 0, 1, 7, 8, 9, 11, 17, 18, 19, 21 |
| 2 area trigger | 46 AREATRIGGER_ONTRIGGER, 61 LINK | 1, 80, 87, 88 | 7, 19, 21 |
| 9 timed action list | the stored event type is ignored (the caller's timer type replaces it) | the creature set | the creature set |

Reasons a row in a plausible set is still refused:

- **Game object.** Action 12 SUMMON_CREATURE needs a creature summoner (`CreatureMapSystem.SummonAt`), and action 69 MOVE_TO_POS needs a motion
  master, which an object does not have. Targets that need a creature (victim, threat list, summoner) are not offered.
- **Area trigger.** Action 11 CAST is refused: AzerothCore summons a trigger creature to cast, and ArcaneCore has none. Actions 22, 23, 30, 31, 67, 73
  and 74 are refused because the script lives for one trigger only (phases and timed events would have nothing to persist in). Range targets 9, 11, 17
  and 18 are refused because they search around a base object and an area trigger has none (SmartScript.cpp:4293-4297); the invoking player is the
  reference point of 19 and 21.
- **GOSSIP_HELLO (64).** Filter (param1) 0 and 1 load. Filter 2 (report use only) is refused because `CMSG_GAMEOBJ_REPORT_USE` does not exist in 1.12,
  so it could never fire. Any other value is refused as invalid.
- **Creature.** Events AzerothCore forbids for creatures (transport 41-44, instance 45, area trigger 46, quest 47-51, GO-only 70-71) are refused with
  that wording; all other events outside the set are refused as having no ArcaneCore hook yet. Slice-1 notes on 1.12 still apply.
- **Calls (80, 87, 88).** Target 0 is refused, since AzerothCore logs an error and does nothing (SmartScript.cpp:2136-2160). Action 80 needs
  `allowOverride` (param3) of 0 or 1. Action 87 needs at least one list id. Action 88 needs min not above max (SmartScriptMgr.cpp:1613-1620) and at
  least one usable list in the range.

## Documented deviations

- **ConditionId instead of conditions source type 22.** AzerothCore gates a smart event through `conditions` rows of source type 22
  (SmartScript.cpp:157-180). ArcaneCore's `conditions` table is the cmangos layout (`condition_entry`), which has no source types. So the
  `smart_scripts` row carries a `ConditionId` column (world schema 51, `SmartScriptConditionDataModule`; 0 means none) that names the condition
  directly. The check uses the relay-condition path of `CreatureMapSystem` (the same path a DB script row's `condition_id` takes): target is the
  event's invoker, source is the script's base object. A condition that is not loaded, or cannot be decided, is not satisfied (Conditions.cpp:1023-1028).
  The loader refuses a row whose `ConditionId` is not in the `conditions` table. Ported AzerothCore rows with type-22 conditions need their condition
  copied into `conditions` and its id written into this column.
- **When the condition is checked.** `ProcessEventsFor` checks it for each row of the raised event type, like AzerothCore. `LINK` rows are not checked:
  they run from the row that links them. A timed event (UPDATE family) whose condition fails is retried after 5000 ms (SmartScript.cpp:4316-4318).
- **Synthetic CREATE_TIMED_EVENT rows run unconditioned.** Action 67 stores a synthetic UPDATE row that fires TIMED_EVENT_TRIGGERED. AzerothCore looks
  its conditions up by the synthetic id, which has none; here the synthetic row has `ConditionId` 0, so the stored event never checks a condition.
  The rows that react to TIMED_EVENT_TRIGGERED are checked as any other.
- **Area-trigger "handled" semantics.** AzerothCore ends the CMSG_AREATRIGGER handler when a trigger script ran, so quest exploration, tavern and
  teleport do not follow. ArcaneCore's `IAreaTriggerListener` seam runs after the packet's volume check and cannot end the handler, so the rest of
  the handler still runs. `SmartAreaTrigger.Run` returns whether a script ran, but nothing consumes it. Dead players and game masters run no script
  (SmartTrigger is for an alive player; MiscHandler.cpp:727 skips game masters). Do not put a smart script on a trigger that also teleports unless
  both are wanted.
- **Game object opt-in has no AIName column.** AzerothCore opts a GO in through `gameobject_template.AIName 'SmartGameObjectAI'`. ArcaneCore's template
  has no such column, so the rows are the opt-in: an object with source-type-1 rows (its spawn's, else its entry's) runs them unless a C# AI is
  registered for its entry (a C# AI always wins, as for creatures). `SmartGameObjectAi` is the `IGameObjectFallbackAi` of every
  `GameObjectMapSystem` and walks a map's objects only when the catalog holds GO rows. The script is created on first use: timers start, then
  AI_INIT, then JUST_CREATED fire once (SmartScript.cpp:5473-5480). An unspawned object does not tick.
  ArcaneCore respawns the same `GameObject` instance, so its script survives; `GameObjectMapSystem.Respawn` calls the new `IGameObjectAi.OnRespawn`
  hook (default no-op), only for an object spawned by default (spawn time >= 0), and `SmartGameObjectAi` runs `OnReset` (phase 0, timers and
  run-once state back, then SMART_EVENT_RESET), as AzerothCore's `AI()->Reset()` does on that branch only (GameObject.cpp:656-665, after the
  `!m_spawnedByDefault` early return; SmartAI.cpp:1476-1481). An object with a negative spawn time brought back by a script or event is not reset.
- **GOSSIP_HELLO.** `GameObject::Use` passes false for every player use (GameObject.cpp:1490-1496), so var0 is always 0, and the handler returns
  false so the use goes on.
- **Links from timed-list rows are rejected.** A timed action list row with a nonzero `link` is refused at load. Timed list rows run one at a time
  by id order, each disabling itself and enabling the next, so a link chain has no defined place in that order here. Put the actions in consecutive
  rows with delays instead.
- **Timed list rules kept.** Delay min above max and repeat min above max are refused (SmartScriptMgr.cpp:1083-1091). A list called while another
  list is processing is refused at run time and reported in `SmartScript.Unsupported`. A running list is kept when the caller's allowOverride is 0.
  The list id is rolled once per call, and each smart creature or game object target runs it with the script's last invoker. The caller's param2
  picks the timer type: 0 out of combat, 1 in combat, above 1 always. A list survives a reset (SmartScript.cpp:131-155).
- **Unchanged from slice 1.** TALK uses broadcast text ids, SUMMON_CREATURE honours the duration only, and MOVE_TO_POS ignores the transport and
  controlled params (see the slice-1 doc).

## Schema

World schema 51 adds `smart_scripts.ConditionId` (`SmartScriptConditionDataModule`, `AddColumnChange`). A database created at 48-50 gains the
column on upgrade; a database created at 51 has it from the table definition. See `docs/reference/schema.md`.
