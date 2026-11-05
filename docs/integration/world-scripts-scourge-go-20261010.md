# World scripts: Scourge invasion completion and go_scripts.cpp objects — 2026-10-10

Lane `scourge-scripts`, base `d0e3606f`. References: mangos-classic `src/game/AI/ScriptDevAI/scripts/world/scourge_invasion.cpp` and
`world/go_scripts.cpp` (HEAD `8ec338a1`), cmangos `Entities/GameObject.cpp`, ClassicDB `ClassicDB_1_12_1_z2815`, and the 5875 client
`Spell.dbc` / `SpellRadius.dbc`.

mangos-classic registers 12 named scripts plus 3 spell scripts in `AddSC_scourge_invasion` (scourge_invasion.cpp:1314-1380). ArcaneCore
already ported 9 of the 12 by entry (`ScourgeInvasionFeature.InstallInvasionAis`). This slice finishes the rest and adds five
`go_scripts.cpp` object scripts, for 10 scripts in total.

## Scourge invasion (World + Game)

| Id | Script | Reference | Port |
|---|---|---|---|
| S1 | `scourge_invasion_necropolis` (creature 16401) | scourge_invasion.cpp:347-377 | Active object while alive; a 28367 hit while aura 28395 is absent casts 28395 on itself (triggered); alive below `Home.Z - 10` near-teleports home. Registered by entry in `InstallInvasionAis`. |
| S2 | `scourge_invasion_go_necropolis` (GO 181154, 181215, 181223, 181373, 181374) | :290-298 | Marked active through `Map.SetActive`. |
| S3 | communique network (relay 16386, proxy 16398, shard 16136/16172) | :542-636, :642, :740-757, :1305-1312 | Relay 28366 casts 28326, 28281 casts 28365; proxy 28373 casts 28366, 28365 casts 28367; shard casts 28346 on itself when the aura is absent and 28449 on a 28326 hit. Relay, proxy and shard are active objects; relay returns home below `Home.Z - 5`, proxy below `Home.Z - 10`. New Game spell script `CommuniqueTriggerScript` on 28345 (`spell_communique_trigger`). |
| S4 | `scourge_invasion_minion` (Shadow of Doom 16143, Flameshocker 16383) | :995-1128, :1295-1303 | Shadow of Doom casts Scourge Strike 28265 (triggered) every update when its victim is within 30 yd, not player-controlled and attackable. A 17680 hit despawns either after 3 s. A Flameshocker summoned beside an attacker by `PallidHorrorAi.SummonNearAttacker` arms a 60 s action: out of combat it casts 28091 on itself, in combat it re-arms 60 s. New Game spell script `DespawnerSelfScript` on 28091 (`spell_despawner_self`). |

Spell data was read from the real 5875 `Spell.dbc` (field offsets from `SpellDbcImporter`): 28395 is a 15 s periodic trigger of 28373;
28346 a 35 s periodic trigger of 28345; 28373 has effect 0 dummy with implicit targets 18/8 (radius index 22 = 200 yd) and effect 1
(86) target 40; 28367/28366/28365/28326/28281 are dummy effects with implicit target 38; 28345 and 28449 target 1; 28091 is a self
dummy; 17680 an aura on self; 28265 an instakill on target 6. `spell_script_target` rows: 28367->16401, 28365->16398,
28366->16386, 28326->16136/16172, 28281->16386, 28373 type 0 -> the five necropolis GOs and type 1 -> 16398.

### Documented deviations

- **go_necropolis active mark applies only while spawned.** `Map.SetActive` throws for an object that is not in the grid index
  (`GridContainer.SetActive`), so the AI marks the object once per spawn while it stands in the map; a despawned necropolis object is
  not marked.
- **Engine gap: 28373 implicit target 8.** ArcaneCore's `SelectScriptTargets` covers implicit targets 38 and 7 only.
  `TARGET_ENUM_UNITS_SCRIPT_AOE_AT_DEST_LOC` (8) has no selector, so the outbound necropolis -> proxy leg (28395 -> 28373 -> proxies) is
  **not claimed live**. It is pinned by `ScourgeNecropolisCommuniqueTargetTests.TargetEightOfTheRealCommuniqueSelectsNoProxyTodayEngineGap`
  (target 7 / 38 selects the nearest listed proxy; target 8 selects none). No workaround was added. The inbound legs (28367, 28366,
  28365, 28326, 28281) use target 38 and work.

## Object scripts (`WorldGameObjectScriptsFeature`, maps 0 and 1)

An auto-discovered `IWorldFeature` with the `HolidayObjectsFeature` pattern (install on `WorldTick`, forget on `MapUnloading`).

| Id | Script | Reference | Port |
|---|---|---|---|
| G1 | `go_andorhal_tower` (GO 176094-176097) | go_scripts.cpp:39-69, 515-518 | `OnUse`: a Player with quest 5097 or 5098 incomplete gets `KilledMonsterCredit` 10902-10905 by tower; returns true. Reached by Beacon Torch 12815 -> spell 17016 (effect 86, Disturb) -> `UseByUnit` -> `OnUse`. Quest state through `QuestNpcFeature.Services.StateOf(player)?.Quests`. |
| G2 | `go_dragon_head` (GO 179556, 179558, 179881, 179882) | :358-397, :540-543 | On the not-spawned -> spawned transition the nearest living 14392/14394/14720/14721 (by head) within 30 yd casts Rallying Cry 22888 (triggered). |
| G3 | `go_unadorned_spike` (GO 175787) | :421-445; cmangos GameObject.cpp:2068-2075 | On the LootState transition to Activated the nearest living Thrall 4949 within 30 yd casts 16609 (triggered). Reached by quest 4974's end dbscript command 13. |
| G4 | `go_transpolyporter_bb` (GO 142172) | :261-276, 356, 530-533; cmangos GameObject.cpp:466-473 | The environmental trap considers only a Player holding at least one Goblin Transponder (9173). One additive Game seam: `IGameObjectAi.AcceptsTrapTarget(objects, go, candidate)` (default true), consulted in the `UpdateEnvironmentalTrap` candidate loop. |
| G5 | `go_containment_coffer` (GO 122088) | :451-486, 555-558 | 2 s after first seen, each update looks for the nearest living Rift Spawn 6492 within 5 yd; when found the coffer is used by it once through `UseByUnit` (a Button: toggles and fires linked trap 103575, spell 9012), then does nothing more. |

### Documented deviations and findings

- **go_dragon_head fires only on the not-spawned -> spawned transition.** cmangos `GameObject::LoadFromDB` (GameObject.cpp:966-967)
  also calls `JustSpawned` at grid load, which depends on load order; the heads are dormant spawns (-21600 s) respawned by the
  quest-end dbscript command 9 (7491 -> guid 40134, 7496 -> 40135, 7782 -> 40151, 7784 -> 40150). ArcaneCore fires on the respawn
  transition only and does not fire for a head that is merely loaded dormant.
- **Coffer `dbscripts_on_go_template_use` 122088 (command 40, despawn self) now runs.** It did not when this lane landed: ArcaneCore had no
  importer, table or runner for `dbscripts_on_go_template_use` or `dbscripts_on_go_use`. World schema 70 (`DbGameObjectScriptDataModule`)
  adds both tables, the content importer reads them from the classic-db dump, and `GameObjectMapSystem` starts them as mangos-classic
  `GameObject::Use` does (GameObject.cpp:1490 template script for every type; :1506, :1529, :1703 the guid script of a door, button and
  goober), with the user as source and the object as target, on the map's DB script executor. The coffer therefore toggles, fires its trap
  and then runs command 40 on use by a creature, as for a player.
  Both scripts start on every use that reaches GameObject::Use, with no uniqueness check: GameObject::Use passes no execution parameter, so
  ScriptsStart defaults to SCRIPT_EXEC_PARAM_NONE (Map.h:233-240, Map.cpp:2181).
  A click does not reach it on an object in use: HandleGameObjectUseOpcode returns for GO_FLAG_IN_USE (SpellHandler.cpp:295-296), which a door
  or button holds until it resets (for good if it has no auto-close time) and a goober until GameObject::Update clears it after its auto-close
  time (GameObject.cpp:549-552, 1385-1388, 1650). The goober's flag and its data6 cooldown come before its quest gate (GameObject.cpp:1485,
  1650-1651, 1691-1693), so a click by a player without the quest runs the template script once and also holds the goober until its auto-close
  time; `UseGoober` follows that order. vmangos breaks out at that gate before the flag, but it has no template script. As in the reference, a
  consumable goober despawns after such a click. `UseCore` refuses the same way, before any script, hook or aura break. So a second run while
  a delayed step (Keystone 28 s, Shadowforge Lock 20 s, Sacred Fire 5 s) is pending needs the object to be usable again first, or a use through
  the open-lock spell or `UseByUnit`, which skip the opcode and have no such refusal. A click on a generic object starts nothing either
  (SpellHandler.cpp:289-293).
  A goober or trap on cooldown refuses the use before the template script starts (GameObject.cpp:1482; GetCooldown covers TRAP and GOOBER,
  GameObject.h:458-466). ArcaneCore also holds back the template script for a goober that `UseGoober` or `UseGooberByUnit` would refuse as not
  ready on the spell and script paths, which GameObject::Use does not do. A door or button
  whose instance script took the use skips the guid script (GameObject.cpp:1505, :1528; asked for a player user on the script-use path too). Known gap: a linked or scripted trap trigger
  calls `UseTrap` directly, where the reference goes through `GameObject::Use` and so starts the template script too; the only type 6
  row is 179804 (Drakonid Bones), whose event BlackwingLairInstance already raises in C#, so it is left alone to avoid a double summon.
  The Brazier of the Herald (guid 2890009) is ported in C# by ScholomanceInstance, which takes that use, so its row does not run too.

## Tests

- `ScourgeInvasionNetworkTests` (World, 8): S1-S4 AIs and the network SpellHit branches.
- `WorldGameObjectScriptTests` (World, 9): G1-G5.
- `ScourgeInvasionSpellScriptTests` (Game, 4): `CommuniqueTriggerScript` and `DespawnerSelfScript`.
- `GameObjectTrapTargetFilterTests` (Game, 3): the `AcceptsTrapTarget` seam.
- `ScourgeNecropolisCommuniqueTargetTests` (Game): implicit target 38/7 selection and the target-8 gap.

## Not done (scope-out)

- All 10 `world/areatrigger_scripts.cpp` scripts (at_childrens_week_spot, at_ravenholdt, at_scent_larkorwi, at_murkdeep,
  at_ancient_leaf, at_huldar_miran, at_twilight_grove, at_hive_tower, at_wondervolt, at_stormwind_recruiter): next lane; the
  smartai-2 lane owns area-trigger sources.
- `go_bubbly_fissure` and `go_ectoplasmic_distiller_trap`: no z2815 `gameobject_template` row carries either ScriptName (177524,
  180057, 181054, 181057 have ScriptName ''), so binding them would be a guess.
- `go_bells`, `go_darkmoon_faire_music`, `go_elemental_rift` are already ported (HolidayObjectScripts.cs, ElementalInvasion.cs).
- The shard's 28449 -> SPELL_CHOOSE_CAMP_TYPE path (:765-772): ArcaneCore picks the camp type at construction.
- Relay/proxy SpellHitTarget(28351) self-despawn (:566-571, :614-619) and NecropolisHealthAI's 5 s proxy respawn / owned-circle logic
  (:385-420), left together for a later fidelity slice.
- NecropolisHealth's fall-under-map return (:531), Shadow of Doom `SetDetectionRange(2)`, GoCircle's 28344 cast, and zone-wide
  Flameshocker target selection.
- No schema change.
