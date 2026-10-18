# Zone scripts, batch 12 (2026-10-17)

Ported from mangos-classic ScriptDev2 (`src/game/AI/ScriptDevAI/scripts`), checked first against what ArcaneCore already had.

| Script | Source | Where |
| --- | --- | --- |
| `npc_felhound_tracker` (8668, quest 3602 Azsharite) | kalimdor/azshara.cpp:194-316 | `Creatures/Scripts/FelhoundTrackerAi.cs`, registered as a map entry script by `KalimdorWorldMapData.OnCreatureCreate` |
| `mob_colonel_kurzen` (813) | eastern_kingdoms/stranglethorn_vale.cpp:110-160 | `Creatures/Scripts/ColonelKurzenAI.cs`, `CreatureAiServices` |
| `go_marshal_haggards_chest` (GO 1562) | eastern_kingdoms/elwynn_forest.cpp:36-56 | `ArcaneCore.World/GameObjects/ZoneGameObjectScriptsBatch12.cs` |
| `go_flagongut_fossil` (GO 9630) | eastern_kingdoms/wetlands.cpp:231-253 | same file |

## Engine seam

`IGameObjectAi.OnLootedOut(objects, go, looter)` is a new default hook. `GameObjectMapSystem.OnLootReleased` calls it when a looted-out
object goes `JustDeactivated` (not for a mineral vein that stays, partly looted chests or durable dungeon chests). It stands in for the
source's `OnLootStateChange(GO_JUST_DEACTIVATED)`. Some loot-service call sites release with no player, so `looter` can be null.

## Behaviour

- **Felhound Tracker.** When its owner is on quest 3602 and roars at it (text emote 74), it takes the first of the crystal entries
  152620, 152621, 152622 and 152631 that has any spawned object within 40 yd. It picks the nearest of those that is still
  `GO_FLAG_INTERACT_COND` and runs to 3 yd (plus its bounding radius) short of it. When it gets there it stops and waits 20 s, then goes back
  to `PetAI`. If no crystal qualifies, it growls (sound 1249).
- **Colonel Kurzen.** In combat he throws a Smoke Bomb (8817, on himself) every 6-12 s. 2-6 s after each bomb, he Garrotes (8818) the
  victim if he still has Stealth (8822). **Deviation:** the source also sets combat-only stealth; ArcaneCore has no such flag, so
  stealth comes only from the spell's aura.
- **Marshal Haggard's Chest.** When the chest is looted out and no living Forlorn Spirit (2044) is within 20 yd of the chest, a spirit
  appears at (-9553.51, -1430.54, 62.38). It has a five-minute out-of-combat despawn and attacks the looter.
- **Flagongut's Fossil.** When it is looted out, Prospector Whelgar (1077), if he is within 10 yd, plays the talk emote and says broadcast
  text 1235.

## Correction to batch 11

The batch 11 note said `go_lightning` (azshara.cpp) was not ported. That was wrong: `LightningAi` in
`ZoneGameObjectScriptsBatch5.cs` already covers it, and the Arcanite Buoy event respawns that object.

## Tests

- `ZoneScriptBatch12Tests` (Game): felhound registration, quest/emote gating, crystal choice, the growl, and the 20 s wait; Kurzen's timers and stealth gate.
- `ZoneGameObjectScriptBatch12Tests` (World): the spirit's summon, attack and single-spirit guard; Whelgar's range; feature registration.
- `ChestGoldAndConditionTests.Chest_TheScriptHearsTheLootedOutRelease_NotAPartlyLootedOne`: the hook seam.
