# EventAI: instance data, CHANGE_MOVEMENT, death and spawned conditions (wave 4, 2026-10-08)

Branch `claude/w4-eventai-instance`, cut from `08ea5ff6`. Logs: `D:/ArcaneCore-lanes/_logs/w4-eventai-instance/`.

The wave-3 integration left four kinds of classic-db z2815 EventAI rows that could not run (docs/integration/wave3-20261007.md, "Data
still needed"). Measured on a copy of the live world database (SQLite backup API, read-only source; `registry-check-before.txt` /
`registry-check-after.txt`, the w3 content-import harness rebuilt against each tree): **48 rows over 30 entries before, 0 after**.

| Rows | What | cmangos semantics (mangos-classic `src/game/AI/EventAI/CreatureEventAI.cpp`) |
|---|---|---|
| 37 | action 34 ACTION_T_SET_INST_DATA | `:1046-1057`: `InstanceData::SetData(field, value)`; without an instance script the action fails |
| 3 | action 48 ACTION_T_CHANGE_MOVEMENT | `:1164-1206`: MoveIdle, MoveRandomAroundPoint (here), StopMoving + Clear + MoveWaypoint |
| 2 | EVENT_T_DEATH with condition 100 | CheckEvent `:327-339`: the conditions table for the killer's controlling player |
| 6 | EVENT_T_SPAWNED, condition 2 (zone 493) | SpawnedEventConditionsCheck `:1902-1927`: zone or area |

Action 35 SET_INST_DATA64 (`:1058-1077`, no z2815 row) came with the same store.

## Instance data

`Game/Instances/Scripts`:

* `InstanceData` is vmangos `InstanceData` (Maps/InstanceData.h/.cpp): Get/SetData, Get/SetData64, Initialize, Load, Save (`GetSaveData`),
  `SaveToDB`, OnCreatureCreate, OnObjectCreate, Update. It is an `IMapUpdater` of its instance map.
* `InstanceManager` creates it with the instance map of a map that has a script (`InstanceScriptRegistry`, `[InstanceScript(mapId)]`
  classes), `Initialize`, then `Load(save.Data)` when the save holds a string (vmangos Map::CreateInstanceData, Map.cpp:1987-2037). Creatures
  and game objects already in the map are reported to it; afterwards `CreatureMapSystem.AddToWorld` and the game object system's grid load
  and `AddToWorld` report every new one (vmangos `ZoneScript::OnCreatureCreate` / `OnGameObjectCreate` from AddToWorld).
* `ScriptedInstance` is ScriptDev2's base: the encounter array, the save string (states separated by spaces, written when a value is DONE),
  the load (IN_PROGRESS comes back NOT_STARTED; a value that is not a number stops the read as an istream does), the stores of objects by
  entry and `DoUseDoorOrButton` (`GameObjectMapSystem.ToggleDoorOrButton`: a ready door or button is used, an active one reset;
  sc_instance.cpp:15-43).
* The scripts of the eight dungeons the 37 rows write to, with the field numbers of the mangos-classic ScriptDev2 scripts the data was
  written for (`AI/ScriptDevAI/scripts/...`; vmangos' own creature_ai tables are a different format): Shadowfang Keep 33, Wailing Caverns 43,
  Razorfen Kraul 47, Blackfathom Deeps 48, Sunken Temple 109, Blackrock Depths 230, Zul'Gurub 309, Dire Maul 429. Ported: the state part of
  each SetData branch the rows reach (including the special rules: Kelris only DONE, the disciple's SPECIAL, the ward keeper count with its
  unsigned wrap, the Tomb's repeated-value guard, Alzzin's wall flag, the avatar's SPECIAL that stores nothing), GetData, Load (with the
  Blackrock Depths index-6 and Sunken Temple not-DONE quirks), and the doors those branches use and the doors created open when their
  encounter is done. Not ported, logged at debug level: texts, dialogues, summons, waves, the respawn of chests and shards, and every
  encounter of those scripts no EventAI row reaches.

The registry check lists every SET_INST_DATA row with the maps its creature spawns on: 30 rows on scripted maps only, 7 rows of creatures
without a spawn row (Mutanus, Lady Anacondra, the Shade of Hakkar: summoned in their dungeon), 0 elsewhere.

### Persistence: a characters schema step is needed (not taken)

vmangos keeps the save string in `instance.data` (characters database; InstanceData::SaveToDB `UPDATE instance SET data`). The
characters `instance` table here has no such column, a column needs a characters schema version, and this wave reserved none (auth 4,
characters 40, world 41; the lane took no number). So the string is kept where the column would be read from, on the in-memory
`InstanceSave.Data`, and `IInstancePersistence.InstanceDataSaved` is called (a no-op by default): an instance map that unloads and is created
again loads it; a server restart forgets it. **Needed for full fidelity: characters schema 41 (or the next free number) adding
`instance.data` (text, nullable), `EfInstanceStore` writing it from `InstanceDataSaved` and `LoadAsync` putting it on the loaded saves.**

## The other three

* CHANGE_MOVEMENT: `CreatureMapSystem.ChangeMovement`; 0 pushes idle unless idle is on top (`MotionMaster.MoveIdle`), 1 pushes a walking wander
  of the given yards around where the creature stands, 2 replaces the movement with the waypoint path (0: the spawn's own path, else the
  entry's path 0; another id: that entry path; a missing path leaves it standing). Types 3/4 and `waypoint_path` paths (flag 0x2) fail. The
  "as default" flag only records the type in cmangos and is ignored. ArcaneCore runs the AI's evade hook before the home movement, so
  Alzzin's evade row sets the path and the creature still walks home first, then resumes the new path.
* Death condition: `EventAiSearch.ConditionHolds` (as the receive-emote and out-of-combat LOS events): no killer or no conditions table, no
  fire.
* Spawned zone: `CreatureMapSystem.ZoneAndAreaOf` (`CreatureAiServices.ZoneAndAreaOf` seam, else `Map.GetZoneAndAreaId`); (0, 0) where the
  terrain does not know matches nothing.

## Found on the way: a dead creature could not cast

`CreatureMapSystem.CastSpell` refused a dead creature and `SpellSystem.CheckCast` answered CasterDead, so no "cast on death" row ever ran
(z2815: 103 death rows cast, 69 with CAST_TRIGGERED, including the two wasp rows above). cmangos refuses only a dead player
(Spell.cpp:4692-4695); vmangos lets a dead unit cast a triggered spell no aura triggered (Spell.cpp:5320). A dead creature now casts a
triggered script spell; untriggered casts and dead players are unchanged (`DeadCasterStillRequiresAllowCastWhileDead` still holds the
player rule, which is stricter than vmangos'). Separate commit `fde861a1`.

## Evidence

* RED (behaviour removed, API kept): `red-tests.log`, 22 of 47 new and changed tests failed; `red-deadcast-tests.log`, the triggered
  dead-cast case and the death-condition case failed.
* GREEN: `green2-tests.log` (119 of 119 focused), full runs in `test-Game-*.log` and the final runs listed in the lane report.
* `registry-check-before.txt` / `registry-check-after.txt` on `live-world-copy.db` (copy sha256 `279663596d0f...`, world schema 41,
  10,843 `creature_ai_scripts` rows).

## Live

No configuration key, no data path, no content-import change (the rows are already in the live world database) and no schema step. The
eight instance scripts only run once the instance maps exist in `map_template` (the instance-maps lane); until then their rows are
supported but never reached on the live server. The CHANGE_MOVEMENT, death-condition, spawned-zone and dead-cast changes act on the
continents as soon as the new binaries run.
