# GM lookup, list and info lane

The commands of the "Lookup, list and info" lane of `gm-command-matrix-raw.md` that three or more reference cores implement, where ArcaneCore has
the content they read, plus a read-only `.arcane` root for server internals. The reference cores are evidence, not authority: names, levels and
syntax below are ArcaneCore's decisions. Code: `src/ArcaneCore.World/Gm/Lookup/LookupContentCommands.cs` (extends `.lookup` through
`ICommandExtension`, adds `.guid` and `.list`) and `ArcaneInfoCommands.cs` (`.arcane`). Tests: `tests/ArcaneCore.World.Tests/Gm/Lookup/LookupContentCommandTests.cs`.

Levels are the vmangos scale the existing lookups use (`RetailLevel`: 1 moderator, 2 ticketmaster, 3 game master) on top of ArcaneCore's four
tiers. Every list is capped by `World:GmCommands:LookupMaxResults` (0 = unlimited) and prints the same "More results were omitted" line as
`.lookup item`. Matches are case-insensitive substrings, ordered by id, like `.lookup item|creature|object|tele`.

## Reference versus ours

Levels in the reference columns are the matrix's (ArcEmu letters l = lookup flag, m = moderator-ish, h = high; AzerothCore and MaNGOS Zero and TrinityCore numbers). "n/a" = the core does not have it.

| Command | ArcEmu | AzerothCore | MaNGOS Zero | TrinityCore | ArcaneCore | Data source in ArcaneCore |
|---|---|---|---|---|---|---|
| `lookup quest $name` | l | 1 | 3 | 3 | level 2 | `quest_template.Title` (`QuestStore`) |
| `lookup skill $name` | l | 1 | 3 | 3 | level 2 | SkillLine.dbc names (`SkillCatalog`, only when the skill system is active) |
| `lookup spell $name` | l | 1 | 3 | 3 | level 2 | `spell_template` name and rank (`SpellStore`) |
| `lookup area $name` | n/a | 1 | 1 | 3 | level 2 | area table (`AreaTable.All`, added for this lane) |
| `lookup map $name` | n/a | 1 | n/a | 3 | level 2 | `map_template` (`MapRegistry`) |
| `lookup taxinode $name` | n/a | 1 | 3 | 3 | level 2 | TaxiNodes.dbc names (`NpcStore.Nodes`) |
| `guid` | n/a | 2 | 2 | 2 | level 2 | the invoker's selection |
| `list creature #id [#max]` | n/a | 1 | 3 | 3 | level 3, default max 10 | creature spawns (`CreatureContent`) |
| `list object #id [#max]` | n/a | 1 | 3 | 3 | level 3, default max 10 | gameobject spawns (`GameObjectContent`) |
| `list auras` | n/a | 1 | 3 | 3 | level 3 | live auras (`SpellSystem.GetAuras`) of the selected unit, else yours |
| `arcane content`, `arcane maps`, `arcane reloads` | n/a | n/a | n/a | n/a | GameMaster tier, no retail level | see below |

Decisions:

- Lookups sit at level 2 with the existing four (`item`, `creature`, `object`, `tele`): the cores disagree (1 to 3), 2 is what vmangos gives
  every lookup and what ArcaneCore already uses. `lookup faction` and `lookup event` come from other lanes and use the same level.
- `list` is a new root at level 3, the MaNGOS Zero and TrinityCore level; AzerothCore's 1 would let a moderator enumerate spawn positions.
- `lookup` result lines use the shift-click link shape of the existing lookups (`id - |cffffffff|H<kind>:<id>|h[name]|h|r`). The quest link
  carries the quest level (`Hquest:id:level`, the vanilla client form); spell links carry name and rank. The `Hskill`, `Harea`, `Hmap` and
  `Htaxinode` link kinds follow the MaNGOS Zero result lines; whether the 1.12.1 client turns them into clickable links is UNVERIFIED (the
  text is shown either way).
- "No quests/skills/spells/area/taxinodes found!" are mangos_string 446, 444, 445, 442, 466 (MaNGOS Zero `Language.h`); "No maps found!",
  the `.list` and `.guid` wording other than "No selection." (200) and "Object GUID is: %s" (201), the aura header and the `.arcane` lines are
  ArcaneCore's.
- Data that is not loaded answers the "none found" line instead of an error: no skill DBC configured (skills `Legacy` mode) means
  `.lookup skill` finds nothing.
- `.list creature|object` take a numeric entry only and a max of at least 1; a spawn that can become several entries (`creature_spawn_entry`)
  is matched by its primary entry only. Coordinates print with two decimals.
- `.guid` prints `ObjectGuid.ToString()` plus the entry and counter (entry-carrying guids) or the low part (players, items). It reads the
  selection only, like the cores.

## ArcaneCore-native: `.arcane`

No reference core has this root. It is read-only and runs on the world thread, so the figures are consistent with the tick that served the
command. Gated by the GameMaster tier (`World:GmCommands` level map), no retail level.

| Command | What it shows |
|---|---|
| `.arcane content` | rows loaded per content table: maps, areas, tele locations, area triggers, item templates, creature templates and spawns, gameobject templates and spawns, quests, taxi nodes, spells, skill lines (or "not active") |
| `.arcane maps` | every running map instance: map id, instance id, name, players, objects, objects in transit |
| `.arcane reloads` | the creature definitions generation (`CreatureContent.DefinitionsVersion`, how many times a `.reload` swapped the definitions since start) and how each reloadable last ended (the same data as `.reload status`) |

"Reload generations" exist only for creature definitions: no other store keeps a generation counter, and none was added. The per-reloadable
last outcome comes from `ReloadCoordinator.LastResults`, which keeps the latest result only, so a count of applied reloads per table is not
available. When `HotReload:Commands` is false `.arcane reloads` says so after the creature line.

## Not provided

| Command | Reason |
|---|---|
| `lookup itemset` (AC, TC) | no item-set table is loaded (no ItemSet.dbc reader or `itemset` rows in the content model) |
| `lookup player account|email|ip`, `lookup player` | the world daemon keeps no account, email or ip index of characters; those live in the realm database and the realm daemon |
| `list item`, `list mail` | `list item` reads item instances of characters in the database and `list mail` the mail table; the world daemon has no GM-side query path for either |
| `list respawns`, `list spawnpoints` | no per-map respawn timer or spawn-point enumeration API is exposed to commands |
| `list players`, `list talents` | single-core commands (MaNGOS Zero); below the three-core cut |
| `lookup spell id` (AC, TC) | `.lookup spell` takes a name only; a numeric id is not resolved |
| `info` (WCell) | single-core |

## Test notes

`docs/reference/gm-commands.md` is generated from the command table and is regenerated after merge, so
`CommandReferenceTests.GmCommandReference_MatchesTheCommittedPage` fails locally until then. With the skill system on, a normal character cannot
speak Common without the language spell, so the tests that configure skills put the invoker in GM mode (`PlayerFlags.Gm`) before chatting.
