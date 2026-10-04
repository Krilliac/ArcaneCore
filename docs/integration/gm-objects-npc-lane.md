# GM commands: game objects, NPCs and spawn state (lane `gm-objects-npc`)

Branch `ccr-build/gm-objects-npc`, based on `ccr-896c06e5-3myqi2` e6cb0d5. Input: the "Game objects" and "NPC, creature and waypoints"
lanes of `docs/integration/gm-command-matrix-raw.md`. The reference cores are evidence, not authority (the matrix header says so): the
names, levels and syntax below are ArcaneCore's. **Nothing here has been checked with a real 1.12.1 client**; the commands are tested over
the loopback world harness only. Reply texts are ArcaneCore's own wording (no `mangos_string` row is quoted).

## The one structural fact

ArcaneCore has **no spawn write path**. `ICreatureDataStore` / `IGameObjectDataStore` only load content (`EfCreatureDataStore.LoadAsync`,
the game object module has no store that writes `gameobject` rows), and `CreatureCommands.Delete` already says "Database spawns cannot be
deleted in game yet." The reference cores' `gobject add|delete|move|turn` and `npc add|delete|move|set ...` persist into the `gameobject` /
`creature` tables. Building a persisted spawn editor is a new subsystem (store write API, schema, reload of the grid caches, respawn-queue
interplay), so it is **not** in this lane. What the lane delivers instead is honest and uniform:

* editing commands act on **runtime objects** (placed by `.gobject add` / `.npc add`, never saved, gone after a restart) and refuse a database
  spawn with a message that says why;
* commands that read or change only live state (`activate`, speech, emotes, respawn, inspection) work on database spawns too.

No file named by the lane brief as off limits was touched (`CreatureRespawnQueue.cs`, `CreatureMapSystem.EventData.cs`, `Creature.cs`,
`NpcServicesFeature.cs`). `.respawn` of a creature goes through `CreatureMapSystem.ForceRespawn`, which deletes the persisted respawn row
through the existing `ICreatureRespawnPersistence` seam as any respawn does.

## Commands

Levels are retail account levels (`World:GmCommands`, see `gm-commands.md`): 3 is the default `GameMaster` mapping, 2 is reachable by a
`GameMaster` and not by a `Moderator`. Mutating commands are 3, read-only ones 2. A `Moderator` (1) can use none of them.

| Command | Level | Syntax | Effect |
|---|---|---|---|
| `gobject add` | 3 | `#entry [#despawnSeconds]` | Place a runtime game object at the invoker (`GameObjectMapSystem.Summon`); with seconds it expires |
| `gobject delete` | 3 | `#guid` | Remove a runtime object; a database spawn is refused |
| `gobject move` | 3 | `#guid [#x #y #z]` | Move a runtime object (to the invoker when no coordinates); clients get destroy then create |
| `gobject turn` | 3 | `#guid [#orientation]` | Turn a runtime object (radians; the invoker's facing when omitted); rotation fields follow |
| `gobject activate` | 3 | `#guid` | Make ready, then flip the state with the in-use flag and the template auto-close (any spawned object, database spawns included) |
| `gobject near` | 2 | `[#radius]` | Objects within the radius (default 10, at most 1000), nearest first, 20 rows then a count |
| `gobject info` | 2 | `#guid` | Entry, type, origin, respawn timer, state, flags, position |
| `npc say` / `yell` / `textemote` | 3 | `$message` | The selected, living creature speaks through `CreatureMapSystem.Say` (say and text emote 25 yards, yell 300) |
| `npc whisper` | 3 | `$playername $message` | Whisper to an online player on the creature's map |
| `npc playemote` | 3 | `#emote` | One-shot SMSG_EMOTE to the creature's observers |
| `npc add` | 3 | `#entry` | The existing temporary spawn (`CreatureCommands.Add`) under the retail name |
| `npc delete` | 3 | (selection) | The existing delete (`CreatureCommands.Delete`): temporary creatures only |
| `npc info` | 2 | (selection) | Entry, level, health, origin, life state and respawn timer, flags, position, home |
| `npc near` | 2 | `[#radius]` | Creatures within the radius, nearest first (pets excluded) |
| `respawn` | 3 | `[#radius]` | Respawn dead database creatures and despawned database objects that wait on a timer, within the radius (default 100) |
| `spawninfo creature` | 2 | `[#radius]` | **Native.** Creatures within the radius (default 40) with origin and respawn state |
| `spawninfo gameobject` | 2 | `[#radius]` | **Native.** The same for game objects |
| `spawninfo summary` | 2 | (none) | **Native.** Per-map counts by state, plus the respawn times kept for unloaded grids |

A game object is named by the guid number `.gobject near` prints (the spawn guid of a database object, the runtime counter of a placed one;
the two ranges cannot collide inside a map, `GameObjectMapSystem` starts runtime counters above the largest spawn guid). Arguments follow the
vmangos grammar helpers in `Gm/Args/CommandArgs.cs`: a number that does not parse, a trailing token, or a radius outside 0 to 1000 prints the
command's syntax text.

## Reference versus ours

Levels in the reference columns are the matrix's (ArcEmu letters: `o` operator-like, `g` GM, `m`/`n`/`v`/`w` per-module flags, not numeric).

| Command | Reference (core: level) | Reference behaviour | Ours | Difference and why |
|---|---|---|---|---|
| `gobject add` | AC 3, MaNGOS Zero 2, TC 2 | Spawns and persists a `gameobject` row, optional spawntime | level 3, runtime object | not persisted: no spawn write path |
| `gobject delete` | ArcEmu o, AC 3, Zero 2, TC 2 | Deletes the object and its row | level 3, runtime only | database spawns refused |
| `gobject move` | ArcEmu g, AC 3, Zero 2, TC 2 | Moves and saves the row | level 3, runtime only | as above; moving a database spawn live would silently revert at the next grid load |
| `gobject turn` | AC 3, Zero 2, TC 2 | Rotates and saves | level 3, runtime only | as above |
| `gobject activate` | ArcEmu o, AC 2, TC 2 | `SetLootState(GO_READY)` then `UseDoorOrButton` (TC passes 10 s auto-close when the template has any) | level 3 | we use the template's own auto-close seconds; level raised because it changes what players see |
| `gobject near` | AC 1, Zero 2, TC 2 | Lists objects within a radius from the database | level 2, live objects | lists what is loaded, including despawned ones |
| `gobject info` | ArcEmu o, AC 1, TC 2 | Template and spawn details | level 2 | live state instead of template columns |
| `gobject target`, `select`, `gobject set state`, `anim`, `lootstate`, `scale`, `faction`, `spawngroup`, `despawngroup`, `load`, `export`, `portto` | various | Selection or spawn-group or schema features | not delivered | see Not delivered |
| `npc say`, `npc yell` | ArcEmu n, AC 2, Zero 1, TC 1 | `Creature::Say/Yell` with the typed text; TC also plays a question/exclamation emote on a trailing `?` or `!` | level 3 | no automatic emote; level 3 because an NPC speaking in a GM's words is impersonation-grade and a level 1 staff account must not do it |
| `npc textemote` | AC 2, Zero 1, TC 1 | Text emote | level 3 | as above |
| `npc whisper` | AC 2, Zero 1, TC 1 | Whisper to the selected player | level 3, by name | the player is named in the command (online, same map) |
| `npc playemote` | AC 2, Zero 3, TC 3 | Plays an emote id | level 3 | one-shot only; the emote id is not validated (see UNVERIFIED) |
| `npc add` | AC 3, Zero 2, TC 2 | Spawns and persists | level 3, temporary | existing `.creature add` behaviour |
| `npc delete` | AC 3, Zero 2, TC 2 | Deletes creature and row | level 3, temporary only | database spawns refused (existing message) |
| `npc info` | AC 2, Zero 3, TC 3 | Entry, spawn, template facts | level 2 | adds the respawn timer |
| `npc near` | AC 2, TC 2 | Creatures near | level 2 | live creatures |
| `respawn` | AC 2, Zero 3, TC 3 | TC: a selected creature, else everything in grid range (creatures with corpses, then objects) | level 3, radius | selection is ignored (radius only, default 100 yards); only database spawns; durable (instance) chests and temporary objects are skipped |
| `npc follow`, `tame`, `move`, `set ...`, `add item`, `delete item`, `showloot`, `flags`, `spawngroup`, `despawngroup`, `evade`, `do`, formations, `wp ...`, `waypoint ...`, `pool ...`, `ai ...` | various | see Not delivered | not delivered | |
| `spawninfo ...` | none | n/a | level 2 | ArcaneCore-native (the lane's one allowed addition): the respawn bookkeeping is otherwise only visible in a debugger |

## Not delivered, and what each needs

| Commands | Needs |
|---|---|
| Any persisted spawn edit: `gobject add\|delete\|move\|turn` on database objects, `npc add\|delete\|move`, `npc set spawntime\|wanderdistance\|movetype\|link\|data`, `npc add move`, formations | A spawn write API (insert/update/delete of `creature` and `gameobject` rows) plus reload of the per-map grid caches (`CreatureMapSystem._spawnsByGrid`, `GameObjectMapSystem._spawnsByGrid`). New subsystem. |
| `npc set level\|model\|entry\|faction\|flag\|allowmove`, `npc tame`, `npc follow` | Persisted template/instance overrides, a pet-binding path for arbitrary creatures, and a follow movement generator. Not present in `src/ArcaneCore.Game/Creatures`. Level and model changes are also edits that must persist to mean anything. |
| `npc add item`, `delete item`, `showloot`, vendor edits | A vendor/loot write path. `NpcServicesFeature.cs` is out of bounds for this lane. |
| `wp *`, `waypoint *` | `creature_movement` is read-only content; the visual waypoint markers need object spawning and a persisted editor. |
| `pool *`, `pooltools *`, `spawngroup`, `despawngroup` | ArcaneCore has no pool or spawn-group system. |
| `gobject target`, `gobject select` | The selection model carries a unit guid only (`Player.Selection`); a selected game object does not exist. `.gobject near` plus the guid argument replaces it. |
| `gobject set state`, `anim`, `lootstate`, `scale`, `faction` | Single-field overrides of a live object that the next respawn would discard. Cheap, but each needs a decision on what survives `Respawn()`, so left for a follow-up rather than guessed. |
| `ahbot *` | The auction-house bot is an economy feature, not this lane. |
| `respawn all\|creature\|gameobject entry\|guid` (AC) | Selecting by entry and guid across a map; `.respawn #radius` covers the loaded area. A creature whose home grid is unloaded is held as a dormant respawn time (`DormantRespawnCount`, shown by `.spawninfo summary`) and is not reachable by any command here. |

## Changes outside the new files (for the integrator)

* `Game/GameObjects/GameObjectMapSystem.Gm.cs` (new): `Activate`, `Relocate` (runtime objects only), `RespawnRemainingMs`, `RespawnPending`,
  `DormantRespawnCount`.
* `Game/Creatures/CreatureMapSystem.Gm.cs` (new): `RespawnRemainingMs`, `DormantRespawnCount`.
* `World/Gm/Objects/*`, `World/Gm/Npc/*` (new): `GmObjectCommands` (`gobject`), `GmNpcCommands` (`npc`), `GmSpawnCommands` (`respawn`,
  `spawninfo`), `GmDistance`.
* `World/Creatures/CreatureCommands.cs`: `Add` and `Delete` changed from `private` to `internal` so `.npc add|delete` share them. The older
  `.creature` root is unchanged.
* New roots `gobject`, `npc` and `respawn` are already in the retail order list (`RetailCommandOrder`), so abbreviations resolve as in the
  retail table: `.n` now means `npc` (it was `notify`: roots are ordered `... modify, npc ... notify ...`).
* `docs/reference/gm-commands.md` and `docs/integration/gm-command-matrix-raw.md` are **not** regenerated here. Locally
  `Docs.CommandReferenceTests.GmCommandReference_MatchesTheCommittedPage` fails until the orchestrator regenerates the page (the GameMaster
  count moves from 63 to 83).

## UNVERIFIED

* Everything above against a real 1.12.1 client: that the destroy-then-create sent by `.gobject move|turn` renders the object at its new
  place without a flicker beyond the one frame, that `SMSG_EMOTE` ids play the intended animation on a creature, and the chat rendering of
  the monster say, yell, emote and whisper packets (the packets are the ones `CreatureMapSystem.Say` already sends for creature scripts).
* `.npc playemote` does not check the id against the client's emote table (no `EmotesText.dbc` / `Emotes.dbc` loader exists here); an id the
  client does not know is sent as given.
* The destroy-and-create relocation was chosen because a game object has no movement packet; whether vmangos relocates through the same
  grid-cell move is not checked.

## Tests

`tests/ArcaneCore.World.Tests/Gm/Objects/GmObjectNpcCommandTests.cs`: every command on its success path, target-resolution failures (unknown
guid, no selection, dead creature, non-creature selection, unknown player, despawned object, database spawn on an edit),
argument failures (syntax text), and account gating (a table level check for every path, and a live refusal for a `Moderator` and a `Player`
account with no object created).
