> RAW DATA, NOT A PLAN. Reference cores are evidence, not authority; the TrinityCore extraction is incomplete (no modify/server/guild/reload paths) and lane assignment is a planning aid. The curated backlog supersedes this file.

# GM command matrix: ArcaneCore vs reference cores

Generated from the per-core extraction files `*.commands.json` and `*.gaps.json` in the workflow scratchpad (`matrix/`). Every fact below comes from those files or the source locations they cite; anything not verifiable is marked UNVERIFIED.

ArcaneCore side: `docs/reference/gm-commands.md` (122 rows) cross-checked by the extractor against `ChatCommand` registrations in `src/ArcaneCore.World/**` at `origin/claude/vw5-integration` (commit `7bcbc950c6d126a7684e9c5c5071643e806126e3`, also the local HEAD when this was generated). ArcaneCore levels are the vmangos retail 0-6 scale.

## 1. Provenance

| Core | Repo | Branch | HEAD (git log -1) | HEAD date | Language | Client era | Caveats |
|---|---|---|---|---|---|---|---|
| ArcEmu | https://github.com/arcemu/arcemu | master | `71bff6d6726717c3a4fa6fc46fa0dcb1c3408c39` | 2024-03-05 | C++ | 3.3.5a (its README) | Targets 3.3.5a, so many commands are WotLK-only. Levels are permission letters, only 0 vs staff is comparable (levelNote). Name by help text, 33 commands excluded as not applicable. |
| AzerothCore | https://github.com/azerothcore/azerothcore-wotlk | master | `ca7e5015d01e8818cb20b1265185f73a691e273f` | 2026-10-04 | C++ | 3.3.5a (WotLK). UNVERIFIED: no README in the checkout, era taken from the repo name and from the extractor excluding TBC/WotLK items | Not a 1.12 core: relevance of its commands was judged by the extractor from client knowledge, not read from code. Levels come from `acore_world.command.security` SQL, 0-3. Level null = not recorded, no mismatch judged. Matching is by exact path only; `closestOurs` hints are name guesses. |
| MangosSharp | https://github.com/MangosServer/mangossharp | main | `9f4f9f420912f4a30ddcf4fc1adfc93a37dadb3c` | 2026-09-10 | C# | 1.12.1 to 1.12.3 (its README) | Small command set (75 paths). Scale: GameMaster 2, Developer 3, Admin 4. Mapping to ArcaneCore is by help text and name, some approximate. |
| MaNGOS Zero | https://github.com/mangoszero/server | master | `b7fface6b1bd3b036211dcde1bff39cc6da503e4` | 2026-09-08 | C++ | 1.12.1 to 1.12.3 (its README) | Closest reference for 1.12.1. Levels 0-4 (PLAYER..CONSOLE). Band mapping between scales is the extractor's own (see section 5). |
| TrinityCore | https://github.com/TrinityCore/TrinityCore | 3.3.5 | `25f70808e6ac68f14b632ab37ea680171d14ac63` | 2026-10-03 | C++ | 3.3.5a (its README title) | Not a 1.12 core. The extraction is INCOMPLETE: `trinitycore.commands.json` has 382 paths and contains none of modify*, server*, guild*, goname, namego, gocorpse, deleteitem, replenish/deplenish, creature add/info/kill, reload, hotcode or group-only roots, so those were not compared (extractionCaveat). Levels derived from RBAC default permissions, 0-3. |
| WCell | https://github.com/WCell/WCell | master | `4f009372d072cff74b7606031673449209ee6e62` | 2013-03-09 | C# | UNVERIFIED (README does not state a client build) | Very old HEAD (2013), no commits for over 13 years. Command names are CamelCase with aliases and no dot-paths, and levels are a role enum (Player/Staff/Admin) with no numeric value. Cross-core row alignment for WCell is by name only and is weak. |

Commit hashes were taken with `git -C /home/user/mangosserver/<dir> log -1` (dirs: arcemu, azerothcore-wotlk, mangossharp, server for mangoszero, TrinityCore, WCell).

Extraction counts (from the JSON files):

| Core | commands.json entries | matched to ArcaneCore | missing, relevant | missing, not applicable | level mismatches |
|---|---|---|---|---|---|
| ArcEmu | 367 | 52 | 281 | 33 | 2 |
| AzerothCore | 736 | 87 | 558 | 91 | 35 |
| MangosSharp | 75 | 23 | 48 | 4 | 14 |
| MaNGOS Zero | 485 | 105 | 376 | 4 | 54 |
| TrinityCore | 382 | 53 | 277 | 52 | 22 |
| WCell | 299 | 39 | 252 | 8 | 8 |

Relevance to 1.12.1 ("relevant" vs "not applicable") was classified by the extractors, in several cases by client knowledge rather than code (e.g. AzerothCore, MaNGOS Zero `debug play movie`). Those claims are marked in the JSON as client-knowledge and are not independently verified here.

## 2. Summary counts

- Distinct command rows (normalised lowercase path, matched or relevant in at least one core): 1405
- Rows ArcaneCore has: 103 (includes group-entry rows merged into their base path)
- Rows ArcaneCore lacks (gaps): 1302
  - implemented by 4 reference core(s): 17 gaps
  - implemented by 3 reference core(s): 107 gaps
  - implemented by 2 reference core(s): 204 gaps
  - implemented by 1 reference core(s): 974 gaps
- Row alignment across cores is by exact normalised path, plus the extractors' own matching for ArcaneCore. The same feature under different names in different cores (e.g. `npc add` vs `creature add`) is therefore counted as separate rows unless the extractor matched it. UNVERIFIED that no other equivalences exist.
- WCell: 277 rows, only 65 share a row with another core, confirming name-only alignment is weak for WCell.

## 3. Ranked gap list (what ArcaneCore lacks)

Ordered by number of reference cores that implement the command (descending), ties alphabetical. `Cores` lists which. Count includes only cores that classed the command as relevant to 1.12.1.

### 3a. Lane summary

| Lane | Gaps | Gaps with 3+ cores | Top commands |
|---|---|---|---|
| Character state and cheats | 221 | 35 | `aura` (4), `bank` (4), `character rename` (4), `dismount` (4) |
| Data reload and cache | 195 | 2 | `pdump load` (3), `pdump write` (3), `cache` (2), `mmap` (2) |
| Other / unclassified | 193 | 3 | `cometome` (3), `movegens` (3), `waterwalk` (3), `additemset` (2) |
| NPC, creature and waypoints | 159 | 13 | `npc say` (4), `npc yell` (4), `npc` (3), `npc add` (3) |
| Debug and developer | 125 | 22 | `debug setvalue` (4), `debug` (3), `debug anim` (3), `debug bg` (3) |
| Teleport and navigation | 73 | 7 | `distance` (4), `go creature` (3), `go graveyard` (3), `go grid` (3) |
| GM audit, tickets and bans | 70 | 7 | `gmannounce` (3), `mute` (3), `pinfo` (3), `ticket` (3) |
| Account management | 65 | 7 | `account` (4), `account delete` (4), `account create` (3), `account onlinelist` (3) |
| Lookup, list and info | 42 | 13 | `lookup quest` (4), `lookup skill` (4), `lookup spell` (4), `guid` (3) |
| World, events and battlegrounds | 36 | 0 | `event activelist` (2), `event info` (2), `worldstate` (2), `battleground` (1) |
| Quest | 30 | 3 | `quest remove` (4), `quest` (3), `quest complete` (3), `quest add` (2) |
| Game objects | 28 | 8 | `gobject delete` (4), `gobject move` (4), `gobject activate` (3), `gobject add` (3) |
| Server ops | 25 | 3 | `gm list` (4), `gm ingame` (3), `gm visible` (3), `gm fly` (2) |
| Pets | 15 | 0 | `pet` (2), `pet create` (2), `pet learn` (2), `pet unlearn` (2) |
| Guild and group | 13 | 0 | `guild rename` (2), `group` (1), `group disband` (1), `group invites` (1) |
| Instance ops | 12 | 1 | `instance savedata` (3), `instance create` (2), `instance getbossstate` (2), `instance setbossstate` (2) |

Lane assignment is by the first word of the command path using a hand-written table in the generator; it is a planning aid, not read from any source.

### 3b. Top 15 gaps

| # | Command | Cores | Count | Lane |
|---|---|---|---|---|
| 1 | `account` | AzerothCore, MaNGOS Zero, TrinityCore, WCell | 4 | Account management |
| 2 | `account delete` | AzerothCore, MaNGOS Zero, TrinityCore, WCell | 4 | Account management |
| 3 | `aura` | AzerothCore, MaNGOS Zero, TrinityCore, WCell | 4 | Character state and cheats |
| 4 | `bank` | MangosSharp, MaNGOS Zero, TrinityCore, WCell | 4 | Character state and cheats |
| 5 | `character rename` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Character state and cheats |
| 6 | `debug setvalue` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Debug and developer |
| 7 | `dismount` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Character state and cheats |
| 8 | `distance` | AzerothCore, MaNGOS Zero, TrinityCore, WCell | 4 | Teleport and navigation |
| 9 | `gm list` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Server ops |
| 10 | `gobject delete` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Game objects |
| 11 | `gobject move` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Game objects |
| 12 | `lookup quest` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Lookup, list and info |
| 13 | `lookup skill` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Lookup, list and info |
| 14 | `lookup spell` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | Lookup, list and info |
| 15 | `npc say` | ArcEmu, AzerothCore, MaNGOS Zero, TrinityCore | 4 | NPC, creature and waypoints |

### 3c. Full gap list by lane

#### Character state and cheats (221)

| Command | Count | Cores (their level) |
|---|---|---|
| `aura` | 4 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3; WCell Staff |
| `bank` | 4 | MangosSharp 2; MaNGOS Zero 3; TrinityCore 3; WCell Staff |
| `character rename` | 4 | ArcEmu m; AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `dismount` | 4 | ArcEmu h; AzerothCore 0; MaNGOS Zero 0; TrinityCore 2 |
| `appear` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `cast back` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `cast dist` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `cast self` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `cast target` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `character deleted delete` | 3 | AzerothCore 4; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `character deleted list` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `character deleted restore` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `character erase` | 3 | AzerothCore 4; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `character level` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `cheat casttime` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `cheat cooldown` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `cheat god` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `cheat power` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `cheat status` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `cheat taxi` | 3 | ArcEmu m; AzerothCore 2; TrinityCore 3 |
| `combatstop` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `damage` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `die` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `honor update` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 1 |
| `modify energy` | 3 | ArcEmu m; AzerothCore 2; MaNGOS Zero 1 |
| `modify faction` | 3 | ArcEmu m; AzerothCore 3; MaNGOS Zero 1 |
| `modify gender` | 3 | ArcEmu m; AzerothCore 2; MaNGOS Zero 2 |
| `modify rage` | 3 | ArcEmu m; AzerothCore 2; MaNGOS Zero 1 |
| `modify scale` | 3 | ArcEmu m; AzerothCore 2; MaNGOS Zero 1 |
| `modify speed` | 3 | ArcEmu m; AzerothCore 2; MaNGOS Zero 1 |
| `send items` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 1 |
| `send mail` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 2 |
| `send message` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 2 |
| `send money` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 2 |
| `summon` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `additem set` | 2 | AzerothCore 2; TrinityCore 3 |
| `cast dest` | 2 | AzerothCore 2; TrinityCore 3 |
| `character changeaccount` | 2 | AzerothCore 3; TrinityCore 3 |
| `character deleted` | 2 | AzerothCore yes (level n/r); MaNGOS Zero 2 |
| `character deleted old` | 2 | MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `cheat explore` | 2 | AzerothCore 2; TrinityCore 3 |
| `cheat waterwalk` | 2 | AzerothCore 2; TrinityCore 3 |
| `deserter bg add` | 2 | AzerothCore 3; TrinityCore 3 |
| `deserter bg remove` | 2 | AzerothCore 3; TrinityCore 3 |
| `freeze` | 2 | AzerothCore 2; TrinityCore 1 |
| `honor add kill` | 2 | AzerothCore 2; TrinityCore 1 |
| `kill` | 2 | ArcEmu r; MangosSharp 2 |
| `learn all` | 2 | AzerothCore 2; MaNGOS Zero 3 |
| `learn all crafts` | 2 | AzerothCore 2; TrinityCore 3 |
| `learn all default` | 2 | AzerothCore 2; TrinityCore 3 |
| `learn all recipes` | 2 | AzerothCore 2; TrinityCore 3 |
| `mail` | 2 | AzerothCore 2; WCell Staff |
| `modify drunk` | 2 | AzerothCore 2; MaNGOS Zero 1 |
| `modify mount` | 2 | AzerothCore 2; MaNGOS Zero 1 |
| `modify standstate` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `modify talentpoints` | 2 | ArcEmu m; AzerothCore 2 |
| `reset` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset all` | 2 | AzerothCore 4; MaNGOS Zero 3 |
| `reset honor` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset items` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset level` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset spells` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset stats` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reset talents` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `send` | 2 | AzerothCore 2; MaNGOS Zero 1 |
| `unfreeze` | 2 | AzerothCore 2; TrinityCore 1 |
| `additem to` | 1 | TrinityCore 3 |
| `ah` | 1 | MaNGOS Zero 3 |
| `ah console` | 1 | MaNGOS Zero 3 |
| `ah console hide` | 1 | MaNGOS Zero 3 |
| `ah console show` | 1 | MaNGOS Zero 3 |
| `ah repair` | 1 | MaNGOS Zero 3 |
| `auction` | 1 | MaNGOS Zero 3 |
| `auction alliance` | 1 | MaNGOS Zero 3 |
| `auction goblin` | 1 | MaNGOS Zero 3 |
| `auction horde` | 1 | MaNGOS Zero 3 |
| `auction item` | 1 | MaNGOS Zero 3 |
| `aura add` | 1 | WCell Staff |
| `aura dump` | 1 | WCell Staff |
| `aura stack` | 1 | AzerothCore 2 |
| `character additemset` | 1 | ArcEmu m |
| `character advanceallskills` | 1 | ArcEmu m |
| `character advanceskill` | 1 | ArcEmu m |
| `character check` | 1 | AzerothCore yes (level n/r) |
| `character check bag` | 1 | AzerothCore 2 |
| `character check bank` | 1 | AzerothCore 2 |
| `character check profession` | 1 | AzerothCore 2 |
| `character deleted purge` | 1 | AzerothCore 4 |
| `character forcerename` | 1 | ArcEmu m |
| `character getskillinfo` | 1 | ArcEmu m |
| `character increaseweaponskill` | 1 | ArcEmu m |
| `character learnskill` | 1 | ArcEmu m |
| `character removeskill` | 1 | ArcEmu m |
| `character repairitems` | 1 | ArcEmu n |
| `character resetreputation` | 1 | ArcEmu n |
| `character resetskills` | 1 | ArcEmu n |
| `character resetspells` | 1 | ArcEmu n |
| `character resettalents` | 1 | ArcEmu n |
| `character showitems` | 1 | ArcEmu m |
| `character showskills` | 1 | ArcEmu m |
| `cheat` | 1 | AzerothCore 2 |
| `cheat area` | 1 | ArcEmu m |
| `cheat flyingmount` | 1 | ArcEmu m |
| `cheat itemstack` | 1 | ArcEmu m |
| `cheat stack` | 1 | ArcEmu m |
| `cheat triggerpass` | 1 | ArcEmu m |
| `damage go` | 1 | TrinityCore 3 |
| `deserter` | 1 | AzerothCore yes (level n/r) |
| `deserter bg` | 1 | AzerothCore yes (level n/r) |
| `deserter bg remove all` | 1 | AzerothCore 3 |
| `deserter instance` | 1 | AzerothCore yes (level n/r) |
| `deserter instance add` | 1 | AzerothCore 3 |
| `deserter instance remove` | 1 | AzerothCore 3 |
| `deserter instance remove all` | 1 | AzerothCore 3 |
| `gear` | 1 | AzerothCore yes (level n/r) |
| `gear repair` | 1 | AzerothCore 2 |
| `gear stats` | 1 | AzerothCore 0 |
| `honor addkills` | 1 | ArcEmu m |
| `honor globaldailyupdate` | 1 | ArcEmu m |
| `honor pvpcredit` | 1 | ArcEmu m |
| `honor singledailyupdate` | 1 | ArcEmu m |
| `inv` | 1 | WCell Staff |
| `inv call` | 1 | WCell Admin |
| `inv createset` | 1 | WCell Staff |
| `inv enchant` | 1 | WCell Staff |
| `inv find` | 1 | WCell Staff |
| `inv get` | 1 | WCell Admin |
| `inv mod` | 1 | WCell Admin |
| `inv purgeall` | 1 | WCell Staff |
| `inv set` | 1 | WCell Admin |
| `inv strip` | 1 | WCell Staff |
| `item` | 1 | AzerothCore yes (level n/r) |
| `item move` | 1 | AzerothCore 2 |
| `item restore` | 1 | AzerothCore 2 |
| `item restore list` | 1 | AzerothCore 2 |
| `learn all blizzard` | 1 | TrinityCore 3 |
| `learn all debug` | 1 | TrinityCore 3 |
| `learn all gm` | 1 | AzerothCore 2 |
| `learn all lang` | 1 | AzerothCore 2 |
| `learn all languages` | 1 | TrinityCore 3 |
| `learn all my` | 1 | AzerothCore 2 |
| `learn all my class` | 1 | AzerothCore 2 |
| `learn all my quest` | 1 | AzerothCore 2 |
| `learn all my talents` | 1 | AzerothCore 2 |
| `learn all my trainer` | 1 | AzerothCore 2 |
| `learn all talents` | 1 | TrinityCore 3 |
| `learn all_crafts` | 1 | MaNGOS Zero 2 |
| `learn all_default` | 1 | MaNGOS Zero 1 |
| `learn all_gm` | 1 | MaNGOS Zero 2 |
| `learn all_lang` | 1 | MaNGOS Zero 1 |
| `learn all_myclass` | 1 | MaNGOS Zero 3 |
| `learn all_myspells` | 1 | MaNGOS Zero 3 |
| `learn all_mytalents` | 1 | MaNGOS Zero 3 |
| `learn all_recipes` | 1 | MaNGOS Zero 2 |
| `learn my quests` | 1 | TrinityCore 3 |
| `learn my trainer` | 1 | TrinityCore 3 |
| `mail list` | 1 | AzerothCore 2 |
| `mail read` | 1 | WCell Staff |
| `mail return` | 1 | AzerothCore 2 |
| `mail send` | 1 | WCell Staff |
| `modify agility` | 1 | ArcEmu m |
| `modify ap` | 1 | ArcEmu m |
| `modify arcane` | 1 | ArcEmu m |
| `modify armor` | 1 | ArcEmu m |
| `modify aspeed` | 1 | MaNGOS Zero 1 |
| `modify bit` | 1 | AzerothCore 2 |
| `modify boundingraidius` | 1 | ArcEmu m |
| `modify bwalk` | 1 | MaNGOS Zero 1 |
| `modify bytes0` | 1 | ArcEmu m |
| `modify bytes1` | 1 | ArcEmu m |
| `modify bytes2` | 1 | ArcEmu m |
| `modify combatreach` | 1 | ArcEmu m |
| `modify damage` | 1 | ArcEmu m |
| `modify displayid` | 1 | ArcEmu m |
| `modify dynamicflags` | 1 | ArcEmu m |
| `modify fire` | 1 | ArcEmu m |
| `modify flags` | 1 | ArcEmu m |
| `modify frost` | 1 | ArcEmu m |
| `modify happiness` | 1 | ArcEmu m |
| `modify holy` | 1 | ArcEmu m |
| `modify intelligence` | 1 | ArcEmu m |
| `modify level` | 1 | ArcEmu m |
| `modify morph` | 1 | MaNGOS Zero 2 |
| `modify nativedisplayid` | 1 | ArcEmu m |
| `modify nature` | 1 | ArcEmu m |
| `modify npcemotestate` | 1 | ArcEmu m |
| `modify rangeap` | 1 | ArcEmu m |
| `modify reputation` | 1 | AzerothCore 2 |
| `modify shadow` | 1 | ArcEmu m |
| `modify speed all` | 1 | AzerothCore 2 |
| `modify speed backwalk` | 1 | AzerothCore 2 |
| `modify speed swim` | 1 | AzerothCore 2 |
| `modify speed walk` | 1 | AzerothCore 2 |
| `modify spell` | 1 | AzerothCore 4 |
| `modify spirit` | 1 | ArcEmu m |
| `modify strength` | 1 | ArcEmu m |
| `modify swim` | 1 | MaNGOS Zero 1 |
| `modify tp` | 1 | MaNGOS Zero 1 |
| `morph` | 1 | AzerothCore 1 |
| `morph mount` | 1 | AzerothCore 1 |
| `morph reset` | 1 | AzerothCore 1 |
| `morph target` | 1 | AzerothCore 1 |
| `playsound` | 1 | MangosSharp 3 |
| `reset items all` | 1 | AzerothCore 3 |
| `reset items allbags` | 1 | AzerothCore 3 |
| `reset items bags` | 1 | AzerothCore 3 |
| `reset items bank` | 1 | AzerothCore 3 |
| `reset items equipped` | 1 | AzerothCore 3 |
| `reset items keyring` | 1 | AzerothCore 3 |
| `reset items vendor_buyback` | 1 | AzerothCore 3 |
| `reset mail` | 1 | MaNGOS Zero 3 |
| `send mass` | 1 | MaNGOS Zero 3 |
| `send mass items` | 1 | MaNGOS Zero 3 |
| `send mass mail` | 1 | MaNGOS Zero 3 |
| `send mass money` | 1 | MaNGOS Zero 3 |
| `skill` | 1 | WCell Staff |
| `skill learn` | 1 | WCell Staff |
| `skill tier` | 1 | WCell Staff |
| `spell` | 1 | WCell Staff |
| `spell clear` | 1 | WCell Staff |
| `spell trigger` | 1 | WCell Staff |

#### Data reload and cache (195)

| Command | Count | Cores (their level) |
|---|---|---|
| `pdump load` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `pdump write` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `cache` | 2 | AzerothCore 1; WCell Staff |
| `mmap` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `mmap loadedtiles` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `mmap loc` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `mmap path` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `mmap stats` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `mmap testarea` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `pdump` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `pdump copy` | 2 | AzerothCore 3; TrinityCore yes (level n/r) |
| `reload areatrigger_tavern` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload areatrigger_teleport` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload autobroadcast` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload command` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload conditions` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload config` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload creature_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload disables` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload disenchant_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload fishing_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload game_tele` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload gameobject_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload gossip_menu` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload gossip_menu_option` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload item_enchantment_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload item_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload mail_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload npc_vendor` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload page_text` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload pickpocketing_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload points_of_interest` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload quest_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload reference_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload reputation_reward_rate` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload reputation_spillover_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload reserved_name` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload skill_fishing_base_level` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload skinning_loot_template` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload spell_area` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload spell_bonus_data` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload spell_pet_auras` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload spell_target_position` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `reload spell_threats` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `autobroadcast` | 1 | AzerothCore 2 |
| `autobroadcast add` | 1 | AzerothCore 3 |
| `autobroadcast list` | 1 | AzerothCore 2 |
| `autobroadcast locale` | 1 | AzerothCore 3 |
| `autobroadcast remove` | 1 | AzerothCore 3 |
| `cache delete` | 1 | AzerothCore 3 |
| `cache info` | 1 | AzerothCore 2 |
| `cache purge` | 1 | WCell Staff |
| `cache refresh` | 1 | AzerothCore 2 |
| `config` | 1 | WCell n/a (auth server, no role check recorded) |
| `config get` | 1 | WCell n/a (auth server, no role check recorded) |
| `config list` | 1 | WCell n/a (auth server, no role check recorded) |
| `config load` | 1 | WCell n/a (auth server, no role check recorded) |
| `config save` | 1 | WCell n/a (auth server, no role check recorded) |
| `config set` | 1 | WCell n/a (auth server, no role check recorded) |
| `ipc` | 1 | WCell n/a (auth server, no role check recorded) |
| `ipc start` | 1 | WCell n/a (auth server, no role check recorded) |
| `ipc stop` | 1 | WCell n/a (auth server, no role check recorded) |
| `ipc toggle` | 1 | WCell n/a (auth server, no role check recorded) |
| `load` | 1 | WCell Admin |
| `load all` | 1 | WCell Admin |
| `load gos` | 1 | WCell Admin |
| `load items` | 1 | WCell Admin |
| `load loot` | 1 | WCell Admin |
| `load npcs` | 1 | WCell Admin |
| `load quests` | 1 | WCell Admin |
| `mmap testheight` | 1 | MaNGOS Zero 2 |
| `reload acore_string` | 1 | AzerothCore 3 |
| `reload all area` | 1 | AzerothCore 3 |
| `reload all gossips` | 1 | AzerothCore 3 |
| `reload all item` | 1 | AzerothCore 3 |
| `reload all locales` | 1 | AzerothCore 3 |
| `reload all loot` | 1 | AzerothCore 3 |
| `reload all npc` | 1 | AzerothCore 3 |
| `reload all quest` | 1 | AzerothCore 3 |
| `reload all scripts` | 1 | AzerothCore 3 |
| `reload all spell` | 1 | AzerothCore 3 |
| `reload all_area` | 1 | MaNGOS Zero 3 |
| `reload all_eventai` | 1 | MaNGOS Zero 3 |
| `reload all_gossips` | 1 | MaNGOS Zero 3 |
| `reload all_item` | 1 | MaNGOS Zero 3 |
| `reload all_locales` | 1 | MaNGOS Zero 3 |
| `reload all_loot` | 1 | MaNGOS Zero 3 |
| `reload all_npc` | 1 | MaNGOS Zero 3 |
| `reload all_quest` | 1 | MaNGOS Zero 3 |
| `reload all_scripts` | 1 | MaNGOS Zero 3 |
| `reload all_spell` | 1 | MaNGOS Zero 3 |
| `reload antidos_opcode_policies` | 1 | AzerothCore 3 |
| `reload areatrigger` | 1 | AzerothCore 3 |
| `reload areatrigger_involvedrelation` | 1 | AzerothCore 3 |
| `reload areatrigger_quest_end` | 1 | MaNGOS Zero 3 |
| `reload auctions` | 1 | AzerothCore 3 |
| `reload battleground_template` | 1 | AzerothCore 3 |
| `reload broadcast_text` | 1 | AzerothCore 3 |
| `reload chat_filter` | 1 | AzerothCore 3 |
| `reload creature_ai_scripts` | 1 | MaNGOS Zero 3 |
| `reload creature_ai_summons` | 1 | MaNGOS Zero 3 |
| `reload creature_ai_texts` | 1 | MaNGOS Zero 3 |
| `reload creature_battleground` | 1 | MaNGOS Zero 3 |
| `reload creature_linked_respawn` | 1 | AzerothCore 3 |
| `reload creature_movement_override` | 1 | AzerothCore 3 |
| `reload creature_onkill_reputation` | 1 | AzerothCore 3 |
| `reload creature_quest_end` | 1 | MaNGOS Zero 3 |
| `reload creature_quest_start` | 1 | MaNGOS Zero 3 |
| `reload creature_questender` | 1 | AzerothCore 3 |
| `reload creature_queststarter` | 1 | AzerothCore 3 |
| `reload creature_spells` | 1 | MaNGOS Zero 3 |
| `reload creature_template` | 1 | AzerothCore 3 |
| `reload creature_template_classlevelstats` | 1 | MaNGOS Zero 3 |
| `reload creature_template_locale` | 1 | AzerothCore 3 |
| `reload creature_text` | 1 | AzerothCore 3 |
| `reload creature_text_locale` | 1 | AzerothCore 3 |
| `reload db_script_string` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_creature_death` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_creature_spell` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_event` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_go_use` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_gossip` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_quest_end` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_quest_start` | 1 | MaNGOS Zero 3 |
| `reload dbscripts_on_spell` | 1 | MaNGOS Zero 3 |
| `reload dungeon_access_requirements` | 1 | AzerothCore 3 |
| `reload dungeon_access_template` | 1 | AzerothCore 3 |
| `reload event_scripts` | 1 | AzerothCore 3 |
| `reload game_event_npc_vendor` | 1 | AzerothCore 3 |
| `reload game_graveyard` | 1 | AzerothCore 3 |
| `reload game_graveyard_zone` | 1 | MaNGOS Zero 3 |
| `reload gameobject_battleground` | 1 | MaNGOS Zero 3 |
| `reload gameobject_quest_end` | 1 | MaNGOS Zero 3 |
| `reload gameobject_quest_start` | 1 | MaNGOS Zero 3 |
| `reload gameobject_questender` | 1 | AzerothCore 3 |
| `reload gameobject_queststarter` | 1 | AzerothCore 3 |
| `reload gameobject_template_locale` | 1 | AzerothCore 3 |
| `reload gm_tickets` | 1 | AzerothCore 3 |
| `reload gossip_menu_option_locale` | 1 | AzerothCore 3 |
| `reload graveyard_zone` | 1 | AzerothCore 3 |
| `reload item_required_target` | 1 | MaNGOS Zero 3 |
| `reload item_set_name_locale` | 1 | AzerothCore 3 |
| `reload item_set_names` | 1 | AzerothCore 3 |
| `reload item_template_locale` | 1 | AzerothCore 3 |
| `reload locales_command` | 1 | MaNGOS Zero 3 |
| `reload locales_creature` | 1 | MaNGOS Zero 3 |
| `reload locales_gameobject` | 1 | MaNGOS Zero 3 |
| `reload locales_gossip_menu_option` | 1 | MaNGOS Zero 3 |
| `reload locales_item` | 1 | MaNGOS Zero 3 |
| `reload locales_npc_text` | 1 | MaNGOS Zero 3 |
| `reload locales_page_text` | 1 | MaNGOS Zero 3 |
| `reload locales_points_of_interest` | 1 | MaNGOS Zero 3 |
| `reload locales_quest` | 1 | MaNGOS Zero 3 |
| `reload mail_level_reward` | 1 | AzerothCore 3 |
| `reload mail_server_template` | 1 | AzerothCore 3 |
| `reload mangos_string` | 1 | MaNGOS Zero 3 |
| `reload module_string` | 1 | AzerothCore 3 |
| `reload motd` | 1 | AzerothCore 3 |
| `reload npc_text` | 1 | MaNGOS Zero 3 |
| `reload npc_text_locale` | 1 | AzerothCore 3 |
| `reload npc_trainer` | 1 | MaNGOS Zero 3 |
| `reload npcs` | 1 | WCell Staff |
| `reload page_text_locale` | 1 | AzerothCore 3 |
| `reload player_loot_template` | 1 | AzerothCore 3 |
| `reload points_of_interest_locale` | 1 | AzerothCore 3 |
| `reload profanity_name` | 1 | AzerothCore 3 |
| `reload quest_greeting` | 1 | AzerothCore 3 |
| `reload quest_offer_reward_locale` | 1 | AzerothCore 3 |
| `reload quest_request_item_locale` | 1 | AzerothCore 3 |
| `reload quest_template_locale` | 1 | AzerothCore 3 |
| `reload rbac` | 1 | AzerothCore 3 |
| `reload script_binding` | 1 | MaNGOS Zero 3 |
| `reload skill_discovery_template` | 1 | AzerothCore 3 |
| `reload skill_extra_item_template` | 1 | AzerothCore 3 |
| `reload smart_scripts` | 1 | AzerothCore 3 |
| `reload spawn_group` | 1 | AzerothCore 3 |
| `reload spell_affect` | 1 | MaNGOS Zero 3 |
| `reload spell_chain` | 1 | MaNGOS Zero 3 |
| `reload spell_cone` | 1 | AzerothCore yes (level n/r) |
| `reload spell_elixir` | 1 | MaNGOS Zero 3 |
| `reload spell_group` | 1 | AzerothCore 3 |
| `reload spell_group_stack_rules` | 1 | AzerothCore 3 |
| `reload spell_learn_spell` | 1 | MaNGOS Zero 3 |
| `reload spell_linked_spell` | 1 | AzerothCore 3 |
| `reload spell_loot_template` | 1 | AzerothCore 3 |
| `reload spell_proc` | 1 | AzerothCore 3 |
| `reload spell_proc_event` | 1 | MaNGOS Zero 3 |
| `reload spell_proc_item_enchant` | 1 | MaNGOS Zero 3 |
| `reload spell_required` | 1 | AzerothCore 3 |
| `reload spell_script_target` | 1 | MaNGOS Zero 3 |
| `reload spell_scripts` | 1 | AzerothCore 3 |
| `reload trainer` | 1 | AzerothCore 3 |
| `reload warden_action` | 1 | AzerothCore 3 |
| `reload waypoint_data` | 1 | AzerothCore 3 |
| `reload waypoint_scripts` | 1 | AzerothCore 3 |

#### Other / unclassified (193)

| Command | Count | Cores (their level) |
|---|---|---|
| `cometome` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `movegens` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `waterwalk` | 3 | MangosSharp 2; MaNGOS Zero 2; WCell Staff |
| `additemset` | 2 | MangosSharp 2; MaNGOS Zero 3 |
| `bindsight` | 2 | AzerothCore 3; TrinityCore 3 |
| `control` | 2 | MangosSharp 4; WCell Staff |
| `demorph` | 2 | ArcEmu m; MaNGOS Zero 2 |
| `gmnameannounce` | 2 | AzerothCore 2; TrinityCore 1 |
| `itemmove` | 2 | MaNGOS Zero 2; TrinityCore 2 |
| `mailbox` | 2 | AzerothCore 1; TrinityCore 3 |
| `mount` | 2 | ArcEmu m; MangosSharp 2 |
| `mutehistory` | 2 | AzerothCore 2; TrinityCore 1 |
| `nameannounce` | 2 | AzerothCore 2; TrinityCore 1 |
| `playall` | 2 | AzerothCore 2; TrinityCore 2 |
| `possess` | 2 | AzerothCore 2; TrinityCore 3 |
| `remove` | 2 | MangosSharp 3; WCell Staff |
| `repairitems` | 2 | MaNGOS Zero 2; TrinityCore 2 |
| `say` | 2 | MangosSharp 2; WCell Staff |
| `start` | 2 | ArcEmu m; MaNGOS Zero 0 |
| `unbindsight` | 2 | AzerothCore 3; TrinityCore 3 |
| `unpossess` | 2 | AzerothCore 2; TrinityCore 3 |
| `unstuck` | 2 | AzerothCore 2; TrinityCore 0 |
| `wpgps` | 2 | AzerothCore 3; TrinityCore 1 |
| `abandon` | 1 | WCell Staff |
| `activatego` | 1 | MangosSharp 3 |
| `addon` | 1 | WCell Staff |
| `addon list` | 1 | WCell Staff |
| `addon load` | 1 | WCell Staff |
| `addrestedxp` | 1 | MangosSharp 3 |
| `addtrainerspell` | 1 | ArcEmu m |
| `addxp` | 1 | MangosSharp 3 |
| `auragroup` | 1 | MaNGOS Zero 3 |
| `authremote` | 1 | WCell Admin |
| `bags clear` | 1 | AzerothCore 2 |
| `bm` | 1 | AzerothCore 2 |
| `calcdist` | 1 | ArcEmu 0 |
| `call` | 1 | WCell Admin |
| `castspell` | 1 | MangosSharp 3 |
| `changemodel` | 1 | MangosSharp 2 |
| `changepassword` | 1 | MangosSharp 4 |
| `channel` | 1 | WCell Staff |
| `channel set ownership` | 1 | TrinityCore 3 |
| `cleararea` | 1 | WCell Staff |
| `cleardos` | 1 | WCell Staff |
| `combatlist` | 1 | MangosSharp 3 |
| `content` | 1 | WCell Admin |
| `content check` | 1 | WCell Admin |
| `content load` | 1 | WCell Admin |
| `cooldownlist` | 1 | MangosSharp 2 |
| `createaccount` | 1 | MangosSharp 4 |
| `db` | 1 | WCell n/a (auth server, no role check recorded) |
| `db drop` | 1 | WCell n/a (auth server, no role check recorded) |
| `db info` | 1 | WCell n/a (auth server, no role check recorded) |
| `devtag` | 1 | ArcEmu 1 |
| `dumpinventory` | 1 | WCell Staff |
| `dumpnetworkinfo` | 1 | WCell Admin |
| `dumptpinfo` | 1 | WCell Admin |
| `editor` | 1 | WCell Staff |
| `email` | 1 | WCell Staff |
| `exception` | 1 | WCell Admin |
| `exception list` | 1 | WCell Admin |
| `exception show` | 1 | WCell Admin |
| `fixscale` | 1 | ArcEmu m |
| `flagdeserter` | 1 | WCell Staff |
| `fly` | 1 | WCell Staff |
| `forcerename` | 1 | MangosSharp 2 |
| `freezeplayer` | 1 | MaNGOS Zero 2 |
| `get` | 1 | WCell Admin |
| `getspell` | 1 | WCell Staff |
| `givexp` | 1 | WCell Staff |
| `global` | 1 | WCell Admin |
| `gobjectadd` | 1 | MangosSharp 3 |
| `gobjectnear` | 1 | MangosSharp 3 |
| `gobjecttarget` | 1 | MangosSharp 3 |
| `gossip` | 1 | WCell Staff |
| `gotogy` | 1 | MangosSharp 2 |
| `gotrig` | 1 | ArcEmu v |
| `groupgo` | 1 | MaNGOS Zero 1 |
| `groupsummon` | 1 | AzerothCore 2 |
| `hateall` | 1 | WCell Staff |
| `highlightgos` | 1 | WCell Staff |
| `hover` | 1 | MangosSharp 2 |
| `hurt` | 1 | MangosSharp 2 |
| `inventory` | 1 | AzerothCore 1 |
| `inventory count` | 1 | AzerothCore 1 |
| `invincible` | 1 | ArcEmu j |
| `invisible` | 1 | ArcEmu i |
| `invul` | 1 | WCell Staff |
| `iowait` | 1 | WCell Admin |
| `kickplayer` | 1 | ArcEmu b |
| `killplr` | 1 | ArcEmu r |
| `knockback` | 1 | WCell Staff |
| `landwalk` | 1 | MangosSharp 2 |
| `learnskill` | 1 | MangosSharp 3 |
| `listdos` | 1 | WCell Staff |
| `listfreeze` | 1 | TrinityCore 1 |
| `listplayers` | 1 | WCell Staff |
| `loadscripts` | 1 | MaNGOS Zero 3 |
| `localizer` | 1 | WCell Staff |
| `localizer reload` | 1 | WCell Staff |
| `localizer setlocale` | 1 | WCell Staff |
| `logcomment` | 1 | ArcEmu 1 |
| `los` | 1 | MangosSharp 3 |
| `loveall` | 1 | WCell Staff |
| `makewild` | 1 | WCell Staff |
| `mod` | 1 | WCell Admin |
| `modauras` | 1 | WCell Staff |
| `modauras flags` | 1 | WCell Staff |
| `modauras level` | 1 | WCell Staff |
| `modperiod` | 1 | ArcEmu m |
| `multiplyspeed` | 1 | WCell Staff |
| `npcadd` | 1 | MangosSharp 3 |
| `npcai` | 1 | MangosSharp 3 |
| `npcaistate` | 1 | MangosSharp 3 |
| `npccome` | 1 | MangosSharp 3 |
| `npcrespawn` | 1 | MangosSharp 3 |
| `opendoor` | 1 | AzerothCore 2 |
| `packetlog` | 1 | AzerothCore 2 |
| `paralyze` | 1 | ArcEmu b |
| `password` | 1 | WCell Player |
| `pin` | 1 | WCell Staff |
| `player` | 1 | AzerothCore yes (level n/r) |
| `player learn` | 1 | AzerothCore 2 |
| `player unlearn` | 1 | AzerothCore 2 |
| `playerinfo` | 1 | ArcEmu m |
| `poi` | 1 | WCell Staff |
| `portal` | 1 | WCell Staff |
| `pushback` | 1 | WCell Staff |
| `pvpstats` | 1 | TrinityCore 0 |
| `quit` | 1 | MaNGOS Zero 4 |
| `race` | 1 | WCell Staff |
| `realm` | 1 | WCell n/a (auth server, no role check recorded) |
| `realm delete` | 1 | WCell n/a (auth server, no role check recorded) |
| `realm list` | 1 | WCell n/a (auth server, no role check recorded) |
| `removesickness` | 1 | ArcEmu m |
| `resetfactions` | 1 | MangosSharp 4 |
| `resetworld` | 1 | WCell Staff |
| `resync` | 1 | WCell n/a (auth server, no role check recorded) |
| `reviveplr` | 1 | ArcEmu r |
| `roles` | 1 | WCell n/a (auth server, no role check recorded) |
| `roles list` | 1 | WCell n/a (auth server, no role check recorded) |
| `root` | 1 | MangosSharp 2 |
| `rooted` | 1 | WCell Staff |
| `select` | 1 | MaNGOS Zero 3 |
| `select clear` | 1 | MaNGOS Zero 3 |
| `select player` | 1 | MaNGOS Zero 3 |
| `sendpacket` | 1 | WCell Staff |
| `sendpacket bgerror` | 1 | WCell Staff |
| `sendpacket spelllog` | 1 | WCell Staff |
| `sendraw` | 1 | WCell Staff |
| `servermessage` | 1 | MangosSharp 2 |
| `set` | 1 | WCell Admin |
| `setaccess` | 1 | MangosSharp 4 |
| `setcharacterspeed` | 1 | MangosSharp 2 |
| `setinstance` | 1 | MangosSharp 4 |
| `setlevel` | 1 | MangosSharp 3 |
| `setrole` | 1 | WCell Admin |
| `settings` | 1 | AzerothCore 1 |
| `settings announcer` | 1 | AzerothCore 1 |
| `showbankslotresult` | 1 | WCell Staff |
| `showcastfail` | 1 | WCell Staff |
| `showtaxi` | 1 | MangosSharp 3 |
| `skillmaster` | 1 | MangosSharp 3 |
| `spawndata` | 1 | MangosSharp 3 |
| `spawndo` | 1 | WCell Staff |
| `spawnzone` | 1 | WCell Admin |
| `spell_linked` | 1 | MaNGOS Zero 3 |
| `spelladd` | 1 | WCell Staff |
| `spellvisual` | 1 | WCell Staff |
| `splinestartswim` | 1 | MangosSharp 2 |
| `splinestopswim` | 1 | MangosSharp 2 |
| `stable` | 1 | MaNGOS Zero 3 |
| `stats` | 1 | WCell n/a (auth server, no role check recorded) |
| `string` | 1 | AzerothCore 2 |
| `stunned` | 1 | WCell Staff |
| `summonall` | 1 | WCell Staff |
| `talents` | 1 | WCell Staff |
| `talents reset` | 1 | WCell Staff |
| `taxicheat` | 1 | MaNGOS Zero 1 |
| `telespell` | 1 | WCell Staff |
| `tile` | 1 | WCell Staff |
| `tile load` | 1 | WCell Staff |
| `togglecached` | 1 | WCell n/a (auth server, no role check recorded) |
| `tostart` | 1 | MangosSharp 2 |
| `turn` | 1 | MangosSharp 3 |
| `unauragroup` | 1 | MaNGOS Zero 3 |
| `unfreezeplayer` | 1 | MaNGOS Zero 2 |
| `unparalyze` | 1 | ArcEmu b |
| `unroot` | 1 | MangosSharp 2 |
| `wannounce` | 1 | ArcEmu u |
| `world` | 1 | WCell Staff |
| `world save` | 1 | WCell Admin |
| `yell` | 1 | WCell Staff |

#### NPC, creature and waypoints (159)

| Command | Count | Cores (their level) |
|---|---|---|
| `npc say` | 4 | ArcEmu n; AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `npc yell` | 4 | ArcEmu n; AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `npc` | 3 | AzerothCore 2; MaNGOS Zero 1; WCell Staff |
| `npc add` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `npc delete` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `npc follow` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `npc info` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `npc move` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `npc playemote` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `npc tame` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `npc textemote` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `npc whisper` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `respawn` | 3 | AzerothCore 2; MaNGOS Zero 3; TrinityCore 3 |
| `ahbot items` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `ahbot rebuild` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `ahbot reload` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `ahbot status` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `npc add formation` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc add item` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc add move` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc add temp` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc delete item` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc despawngroup` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc flags` | 2 | ArcEmu n; WCell Staff |
| `npc follow stop` | 2 | AzerothCore 2; TrinityCore 2 |
| `npc near` | 2 | AzerothCore 2; TrinityCore 2 |
| `npc select` | 2 | ArcEmu n; WCell Staff |
| `npc set allowmove` | 2 | AzerothCore 3; TrinityCore 3 |
| `npc set data` | 2 | AzerothCore 3; TrinityCore 3 |
| `npc set entry` | 2 | AzerothCore 3; TrinityCore 3 |
| `npc set flag` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set level` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set link` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set model` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set movetype` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set spawntime` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc set wanderdistance` | 2 | AzerothCore 3; TrinityCore 2 |
| `npc showloot` | 2 | AzerothCore 2; TrinityCore 2 |
| `npc spawngroup` | 2 | AzerothCore 3; TrinityCore 2 |
| `pool` | 2 | AzerothCore yes (level n/r); MaNGOS Zero 2 |
| `wp` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `wp add` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `wp modify` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `wp show` | 2 | AzerothCore 3; MaNGOS Zero 2 |
| `ahbot` | 1 | MaNGOS Zero 3 |
| `ahbot items amount` | 1 | MaNGOS Zero 3 |
| `ahbot items amount blue` | 1 | MaNGOS Zero 3 |
| `ahbot items amount green` | 1 | MaNGOS Zero 3 |
| `ahbot items amount grey` | 1 | MaNGOS Zero 3 |
| `ahbot items amount orange` | 1 | MaNGOS Zero 3 |
| `ahbot items amount purple` | 1 | MaNGOS Zero 3 |
| `ahbot items amount white` | 1 | MaNGOS Zero 3 |
| `ahbot items amount yellow` | 1 | MaNGOS Zero 3 |
| `ahbot items blue` | 1 | TrinityCore 3 |
| `ahbot items gray` | 1 | TrinityCore 3 |
| `ahbot items green` | 1 | TrinityCore 3 |
| `ahbot items orange` | 1 | TrinityCore 3 |
| `ahbot items purple` | 1 | TrinityCore 3 |
| `ahbot items ratio` | 1 | MaNGOS Zero 3 |
| `ahbot items ratio alliance` | 1 | MaNGOS Zero 3 |
| `ahbot items ratio horde` | 1 | MaNGOS Zero 3 |
| `ahbot items ratio neutral` | 1 | MaNGOS Zero 3 |
| `ahbot items white` | 1 | TrinityCore 3 |
| `ahbot items yellow` | 1 | TrinityCore 3 |
| `ahbot ratio` | 1 | TrinityCore 3 |
| `ahbot ratio alliance` | 1 | TrinityCore 3 |
| `ahbot ratio horde` | 1 | TrinityCore 3 |
| `ahbot ratio neutral` | 1 | TrinityCore 3 |
| `ai` | 1 | WCell Staff |
| `ai active` | 1 | WCell Staff |
| `ai follow` | 1 | WCell Staff |
| `ai movetome` | 1 | WCell Staff |
| `npc addagent` | 1 | ArcEmu n |
| `npc additem` | 1 | MaNGOS Zero 2 |
| `npc aiinfo` | 1 | MaNGOS Zero 2 |
| `npc allowmove` | 1 | MaNGOS Zero 3 |
| `npc canfly` | 1 | ArcEmu n |
| `npc cast` | 1 | ArcEmu n |
| `npc changeentry` | 1 | MaNGOS Zero 3 |
| `npc changelevel` | 1 | MaNGOS Zero 2 |
| `npc come` | 1 | ArcEmu n |
| `npc delitem` | 1 | MaNGOS Zero 2 |
| `npc do` | 1 | AzerothCore 3 |
| `npc emote` | 1 | ArcEmu n |
| `npc equip1` | 1 | ArcEmu m |
| `npc equip2` | 1 | ArcEmu m |
| `npc equip3` | 1 | ArcEmu m |
| `npc evade` | 1 | TrinityCore 3 |
| `npc factionid` | 1 | MaNGOS Zero 2 |
| `npc flag` | 1 | MaNGOS Zero 2 |
| `npc formationclear` | 1 | ArcEmu m |
| `npc formationlink1` | 1 | ArcEmu m |
| `npc formationlink2` | 1 | ArcEmu m |
| `npc goto` | 1 | WCell Staff |
| `npc guid` | 1 | AzerothCore 2 |
| `npc listagent` | 1 | ArcEmu n |
| `npc load` | 1 | AzerothCore 3 |
| `npc loot` | 1 | ArcEmu m |
| `npc npcfollow` | 1 | ArcEmu m |
| `npc nullfollow` | 1 | ArcEmu m |
| `npc ongameobject` | 1 | ArcEmu n |
| `npc portto` | 1 | ArcEmu v |
| `npc possess` | 1 | ArcEmu n |
| `npc return` | 1 | ArcEmu n |
| `npc selectable` | 1 | WCell Staff |
| `npc set` | 1 | AzerothCore 3 |
| `npc set faction` | 1 | AzerothCore yes (level n/r) |
| `npc set faction original` | 1 | AzerothCore 3 |
| `npc set faction permanent` | 1 | AzerothCore 3 |
| `npc set faction temp` | 1 | AzerothCore 3 |
| `npc set factionid` | 1 | TrinityCore 2 |
| `npc setdeathstate` | 1 | MaNGOS Zero 2 |
| `npc setmodel` | 1 | MaNGOS Zero 2 |
| `npc setmovetype` | 1 | MaNGOS Zero 2 |
| `npc spawndist` | 1 | MaNGOS Zero 2 |
| `npc spawnlink` | 1 | ArcEmu n |
| `npc spawntime` | 1 | MaNGOS Zero 2 |
| `npc unfollow` | 1 | MaNGOS Zero 2 |
| `npc unpossess` | 1 | ArcEmu n |
| `npc vendoradditem` | 1 | ArcEmu n |
| `npc vendorremoveitem` | 1 | ArcEmu n |
| `npc watch` | 1 | MaNGOS Zero 2 |
| `pool info` | 1 | AzerothCore 2 |
| `pool list` | 1 | MaNGOS Zero 2 |
| `pool lookup` | 1 | AzerothCore 2 |
| `pool spawns` | 1 | MaNGOS Zero 2 |
| `pooltools` | 1 | AzerothCore 3 |
| `pooltools add` | 1 | AzerothCore 3 |
| `pooltools clear` | 1 | AzerothCore 3 |
| `pooltools def` | 1 | AzerothCore 3 |
| `pooltools end` | 1 | AzerothCore 3 |
| `pooltools remove` | 1 | AzerothCore 3 |
| `pooltools start` | 1 | AzerothCore 3 |
| `respawn all` | 1 | AzerothCore 2 |
| `respawn creature entry` | 1 | AzerothCore 3 |
| `respawn creature guid` | 1 | AzerothCore 3 |
| `respawn gameobject entry` | 1 | AzerothCore 3 |
| `respawn gameobject guid` | 1 | AzerothCore 3 |
| `waypoint add` | 1 | ArcEmu w |
| `waypoint addfly` | 1 | ArcEmu w |
| `waypoint change` | 1 | ArcEmu w |
| `waypoint delete` | 1 | ArcEmu w |
| `waypoint deleteall` | 1 | ArcEmu w |
| `waypoint emote` | 1 | ArcEmu w |
| `waypoint flags` | 1 | ArcEmu w |
| `waypoint generate` | 1 | ArcEmu w |
| `waypoint hide` | 1 | ArcEmu w |
| `waypoint info` | 1 | ArcEmu w |
| `waypoint movehere` | 1 | ArcEmu w |
| `waypoint movetype` | 1 | ArcEmu w |
| `waypoint save` | 1 | ArcEmu w |
| `waypoint show` | 1 | ArcEmu w |
| `waypoint skin` | 1 | ArcEmu w |
| `waypoint waittime` | 1 | ArcEmu w |
| `wp event` | 1 | AzerothCore 3 |
| `wp export` | 1 | MaNGOS Zero 3 |
| `wp load` | 1 | AzerothCore 3 |
| `wp reload` | 1 | AzerothCore 3 |
| `wp unload` | 1 | AzerothCore 3 |

#### Debug and developer (125)

| Command | Count | Cores (their level) |
|---|---|---|
| `debug setvalue` | 4 | ArcEmu d; AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug` | 3 | AzerothCore 2; MaNGOS Zero 1; WCell n/a (auth server, no role check recorded) |
| `debug anim` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 1 |
| `debug bg` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug getitemstate` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug getitemvalue` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug getvalue` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug lootrecipient` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 1 |
| `debug play cinematic` | 3 | AzerothCore 3; MaNGOS Zero 1; TrinityCore 1 |
| `debug play sound` | 3 | AzerothCore 3; MaNGOS Zero 1; TrinityCore 1 |
| `debug send buyerror` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send channelnotify` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send equiperror` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send opcode` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send qinvalidmsg` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send qpartymsg` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send sellerror` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug send spellfail` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug setaurastate` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `debug setbit` | 3 | ArcEmu d; AzerothCore 3; TrinityCore 1 |
| `debug setitemvalue` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `dev` | 3 | AzerothCore 3; TrinityCore 3; WCell n/a (auth server, no role check recorded) |
| `debug areatriggers` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug boundary` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug combat` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug dummy` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug itemexpire` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug los` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug mod32value` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug moveflags` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug objectcount` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug play` | 2 | AzerothCore 1; MaNGOS Zero 1 |
| `debug play music` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug send` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `debug send chatmessage` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug send largepacket` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug threat` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug threatinfo` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug update` | 2 | AzerothCore 3; TrinityCore 1 |
| `debug uws` | 2 | AzerothCore 3; MaNGOS Zero 3 |
| `debug addworldstate` | 1 | ArcEmu d |
| `debug aggrorange` | 1 | ArcEmu d |
| `debug aimove` | 1 | ArcEmu d |
| `debug aispelltestbegin` | 1 | ArcEmu d |
| `debug aispelltestcontinue` | 1 | ArcEmu d |
| `debug aispelltestskip` | 1 | ArcEmu d |
| `debug auralist` | 1 | ArcEmu d |
| `debug auraremove` | 1 | ArcEmu d |
| `debug buffers` | 1 | WCell Admin |
| `debug calcthreat` | 1 | ArcEmu d |
| `debug castself` | 1 | ArcEmu d |
| `debug castspell` | 1 | ArcEmu d |
| `debug castspellne` | 1 | ArcEmu d |
| `debug clearworldstates` | 1 | ArcEmu d |
| `debug cooldown` | 1 | AzerothCore 3 |
| `debug damageunit` | 1 | ArcEmu d |
| `debug deathstate` | 1 | ArcEmu d |
| `debug dist` | 1 | ArcEmu d |
| `debug dumpcoords` | 1 | ArcEmu d |
| `debug face` | 1 | ArcEmu d |
| `debug fade` | 1 | ArcEmu d |
| `debug gc` | 1 | WCell n/a (auth server, no role check recorded) |
| `debug getbytes` | 1 | ArcEmu d |
| `debug getheight` | 1 | ArcEmu d |
| `debug getpos` | 1 | ArcEmu d |
| `debug gettptime` | 1 | ArcEmu d |
| `debug guidlimits` | 1 | TrinityCore 1 |
| `debug hostile` | 1 | AzerothCore 3 |
| `debug info` | 1 | WCell Admin |
| `debug infront` | 1 | ArcEmu d |
| `debug initworldstates` | 1 | ArcEmu d |
| `debug instancespawn` | 1 | TrinityCore 1 |
| `debug itempushresult` | 1 | ArcEmu d |
| `debug landwalk` | 1 | ArcEmu d |
| `debug leap` | 1 | ArcEmu d |
| `debug loadcells` | 1 | TrinityCore 1 |
| `debug loot` | 1 | AzerothCore 2 |
| `debug mapdata` | 1 | AzerothCore 3 |
| `debug minion` | 1 | MaNGOS Zero 2 |
| `debug moditemvalue` | 1 | MaNGOS Zero 3 |
| `debug modvalue` | 1 | MaNGOS Zero 3 |
| `debug moveinfo` | 1 | ArcEmu d |
| `debug neargraveyard` | 1 | TrinityCore 1 |
| `debug objectpool` | 1 | WCell Admin |
| `debug play visual` | 1 | AzerothCore 3 |
| `debug playsound` | 1 | ArcEmu d |
| `debug playspellvisual` | 1 | ArcEmu d |
| `debug raidreset` | 1 | TrinityCore 1 |
| `debug rangecheck` | 1 | ArcEmu d |
| `debug recv` | 1 | MaNGOS Zero 3 |
| `debug reloaddefs` | 1 | WCell Admin |
| `debug removeaura` | 1 | ArcEmu d |
| `debug removeworldstate` | 1 | ArcEmu d |
| `debug root` | 1 | ArcEmu d |
| `debug send chatmmessage` | 1 | MaNGOS Zero 3 |
| `debug send poi` | 1 | MaNGOS Zero 3 |
| `debug sendfailed` | 1 | ArcEmu d |
| `debug sendmotd` | 1 | ArcEmu d |
| `debug sendpacket` | 1 | ArcEmu d |
| `debug setbytes` | 1 | ArcEmu d |
| `debug setweather` | 1 | ArcEmu d |
| `debug showemote` | 1 | ArcEmu d |
| `debug showreact` | 1 | ArcEmu d |
| `debug spawnwar` | 1 | ArcEmu d |
| `debug spellcheck` | 1 | MaNGOS Zero 4 |
| `debug spellcoefs` | 1 | MaNGOS Zero 3 |
| `debug spellmods` | 1 | MaNGOS Zero 3 |
| `debug sqlquery` | 1 | ArcEmu d |
| `debug taxistart` | 1 | ArcEmu d |
| `debug testindoor` | 1 | ArcEmu d |
| `debug testlos` | 1 | ArcEmu d |
| `debug threatlist` | 1 | ArcEmu d |
| `debug threatmod` | 1 | ArcEmu d |
| `debug transport` | 1 | TrinityCore 1 |
| `debug triggercinematic` | 1 | ArcEmu d |
| `debug unitstate` | 1 | AzerothCore 3 |
| `debug unroot` | 1 | ArcEmu d |
| `debug updateworldstate` | 1 | ArcEmu d |
| `debug visibilitydata` | 1 | AzerothCore 3 |
| `debug warden force` | 1 | TrinityCore 1 |
| `debug waterwalk` | 1 | ArcEmu d |
| `debug worldstate` | 1 | TrinityCore 1 |
| `debug zonestats` | 1 | AzerothCore 1 |
| `dev network` | 1 | WCell n/a (auth server, no role check recorded) |
| `dumpchunk` | 1 | WCell Staff |

#### Teleport and navigation (73)

| Command | Count | Cores (their level) |
|---|---|---|
| `distance` | 4 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3; WCell Staff |
| `go creature` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `go graveyard` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `go grid` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `go taxinode` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `go zonexy` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 1 |
| `linkgrave` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `go creature id` | 2 | AzerothCore 1; TrinityCore 1 |
| `go gameobject` | 2 | AzerothCore 1; TrinityCore 1 |
| `go gameobject id` | 2 | AzerothCore 1; TrinityCore 1 |
| `go ticket` | 2 | AzerothCore 2; TrinityCore 1 |
| `go trigger` | 2 | AzerothCore 1; MaNGOS Zero 1 |
| `tele add` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `tele del` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `tele group` | 2 | MaNGOS Zero 1; TrinityCore 1 |
| `go anim` | 1 | WCell Staff |
| `go areatrigger` | 1 | TrinityCore 1 |
| `go boss` | 1 | TrinityCore 1 |
| `go call` | 1 | WCell Admin |
| `go creature name` | 1 | AzerothCore 1 |
| `go deselect` | 1 | WCell Staff |
| `go get` | 1 | WCell Admin |
| `go instance` | 1 | TrinityCore 1 |
| `go object` | 1 | MaNGOS Zero 1 |
| `go offset` | 1 | TrinityCore 1 |
| `go quest` | 1 | AzerothCore 1 |
| `go select` | 1 | WCell Staff |
| `go set` | 1 | WCell Admin |
| `go spawn` | 1 | WCell Staff |
| `go toggle` | 1 | WCell Staff |
| `go xy` | 1 | MaNGOS Zero 1 |
| `grid` | 1 | MaNGOS Zero 2 |
| `grid anchors` | 1 | MaNGOS Zero 2 |
| `grid info` | 1 | MaNGOS Zero 2 |
| `grid lwstats` | 1 | MaNGOS Zero 2 |
| `map` | 1 | WCell Admin |
| `map clear` | 1 | WCell Admin |
| `map list` | 1 | WCell Admin |
| `map spawn` | 1 | WCell Admin |
| `map updates` | 1 | WCell Admin |
| `nav` | 1 | WCell Staff |
| `nav clear` | 1 | WCell Staff |
| `nav path` | 1 | WCell Staff |
| `nav show` | 1 | WCell Staff |
| `recall add` | 1 | ArcEmu q |
| `recall del` | 1 | ArcEmu q |
| `recall list` | 1 | ArcEmu q |
| `recall port` | 1 | ArcEmu q |
| `recall portplayer` | 1 | ArcEmu m |
| `recall portus` | 1 | ArcEmu m |
| `taxi` | 1 | WCell Staff |
| `taxi activate` | 1 | WCell Staff |
| `taxi go` | 1 | WCell Staff |
| `taxi gotonext` | 1 | WCell Staff |
| `taxi info` | 1 | WCell Staff |
| `taxi list` | 1 | WCell Staff |
| `taxi show` | 1 | WCell Staff |
| `taxi stop` | 1 | WCell Staff |
| `tele name npc guid` | 1 | TrinityCore 1 |
| `tele name npc id` | 1 | TrinityCore 1 |
| `tele name npc name` | 1 | TrinityCore 1 |
| `teleport` | 1 | AzerothCore 2 |
| `teleport add` | 1 | AzerothCore 3 |
| `teleport del` | 1 | AzerothCore 3 |
| `teleport group` | 1 | AzerothCore 2 |
| `teleport name` | 1 | AzerothCore 2 |
| `teleport name npc` | 1 | AzerothCore yes (level n/r) |
| `teleport name npc guid` | 1 | AzerothCore 2 |
| `teleport name npc id` | 1 | AzerothCore 2 |
| `teleport name npc name` | 1 | AzerothCore 2 |
| `trigger` | 1 | MaNGOS Zero 2 |
| `trigger active` | 1 | MaNGOS Zero 2 |
| `trigger near` | 1 | MaNGOS Zero 2 |

#### GM audit, tickets and bans (70)

| Command | Count | Cores (their level) |
|---|---|---|
| `gmannounce` | 3 | ArcEmu u; AzerothCore 2; TrinityCore 1 |
| `mute` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `pinfo` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `ticket` | 3 | AzerothCore 2; MaNGOS Zero 2; WCell Staff |
| `ticket delete` | 3 | AzerothCore 3; MaNGOS Zero 3; WCell Staff |
| `ticket list` | 3 | AzerothCore 2; MaNGOS Zero 2; WCell Staff |
| `unmute` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `ban playeraccount` | 2 | AzerothCore 2; TrinityCore 3 |
| `gmnotify` | 2 | AzerothCore 2; TrinityCore 1 |
| `ticket close` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `ticket onlinelist` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `ticket show` | 2 | MaNGOS Zero 2; WCell Staff |
| `unban playeraccount` | 2 | AzerothCore 3; TrinityCore 3 |
| `ban all` | 1 | ArcEmu a |
| `bans` | 1 | WCell n/a (auth server, no role check recorded) |
| `bans add` | 1 | WCell n/a (auth server, no role check recorded) |
| `bans lift` | 1 | WCell n/a (auth server, no role check recorded) |
| `bans list` | 1 | WCell n/a (auth server, no role check recorded) |
| `chatfilter` | 1 | AzerothCore 2 |
| `chatfilter add` | 1 | AzerothCore 3 |
| `chatfilter list` | 1 | AzerothCore 2 |
| `chatfilter remove` | 1 | AzerothCore 3 |
| `gmticket assign` | 1 | ArcEmu c |
| `gmticket comment` | 1 | ArcEmu c |
| `gmticket deletepermanent` | 1 | ArcEmu z |
| `gmticket delid` | 1 | ArcEmu c |
| `gmticket get` | 1 | ArcEmu c |
| `gmticket getid` | 1 | ArcEmu c |
| `gmticket list` | 1 | ArcEmu c |
| `gmticket release` | 1 | ArcEmu c |
| `gmticket remove` | 1 | ArcEmu c |
| `gmticket toggle` | 1 | ArcEmu z |
| `kick account` | 1 | ArcEmu f |
| `kick ip` | 1 | ArcEmu f |
| `log` | 1 | WCell Admin |
| `log togglelevel` | 1 | WCell Admin |
| `rbac` | 1 | AzerothCore yes (level n/r) |
| `rbac account` | 1 | AzerothCore yes (level n/r) |
| `rbac account deny` | 1 | AzerothCore yes (level n/r) |
| `rbac account grant` | 1 | AzerothCore yes (level n/r) |
| `rbac account list` | 1 | AzerothCore yes (level n/r) |
| `rbac account revoke` | 1 | AzerothCore yes (level n/r) |
| `rbac list` | 1 | AzerothCore 3 |
| `ticket accept` | 1 | MaNGOS Zero 3 |
| `ticket assign` | 1 | AzerothCore 2 |
| `ticket closedlist` | 1 | AzerothCore 2 |
| `ticket comment` | 1 | AzerothCore 2 |
| `ticket complete` | 1 | AzerothCore 2 |
| `ticket current` | 1 | WCell Staff |
| `ticket escalate` | 1 | AzerothCore 2 |
| `ticket escalatedlist` | 1 | AzerothCore 2 |
| `ticket goto` | 1 | WCell Staff |
| `ticket info` | 1 | MaNGOS Zero 2 |
| `ticket meaccept` | 1 | MaNGOS Zero 2 |
| `ticket notify` | 1 | WCell Staff |
| `ticket reset` | 1 | AzerothCore 4 |
| `ticket respond` | 1 | MaNGOS Zero 2 |
| `ticket response` | 1 | AzerothCore 2 |
| `ticket response append` | 1 | AzerothCore 2 |
| `ticket response appendln` | 1 | AzerothCore 2 |
| `ticket response delete` | 1 | AzerothCore 2 |
| `ticket response show` | 1 | AzerothCore 2 |
| `ticket select` | 1 | WCell Staff |
| `ticket selectnext` | 1 | WCell Staff |
| `ticket surveyclose` | 1 | MaNGOS Zero 2 |
| `ticket togglesystem` | 1 | AzerothCore 3 |
| `ticket unassign` | 1 | AzerothCore 2 |
| `ticket unselect` | 1 | WCell Staff |
| `ticket viewid` | 1 | AzerothCore 2 |
| `ticket viewname` | 1 | AzerothCore 2 |

#### Account management (65)

| Command | Count | Cores (their level) |
|---|---|---|
| `account` | 4 | AzerothCore 0; MaNGOS Zero 0; TrinityCore 0; WCell n/a (auth server, no role check recorded) |
| `account delete` | 4 | AzerothCore 4; MaNGOS Zero 4; TrinityCore yes (level n/r); WCell n/a (auth server, no role check recorded) |
| `account create` | 3 | AzerothCore 4; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `account onlinelist` | 3 | AzerothCore 4; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `account password` | 3 | AzerothCore 0; MaNGOS Zero 0; TrinityCore 0 |
| `account set gmlevel` | 3 | AzerothCore 3; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `account set password` | 3 | AzerothCore 3; MaNGOS Zero 4; TrinityCore yes (level n/r) |
| `account info` | 2 | AzerothCore 2; WCell n/a (auth server, no role check recorded) |
| `account lock` | 2 | AzerothCore yes (level n/r); MaNGOS Zero 0 |
| `account lock country` | 2 | AzerothCore 0; TrinityCore 0 |
| `account lock ip` | 2 | AzerothCore 0; TrinityCore 0 |
| `account set` | 2 | AzerothCore 2; MaNGOS Zero 3 |
| `disable add battleground` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable add map` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable add quest` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable add spell` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable add vmap` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable remove battleground` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable remove map` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable remove quest` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable remove spell` | 2 | AzerothCore 3; TrinityCore 3 |
| `disable remove vmap` | 2 | AzerothCore 3; TrinityCore 3 |
| `account 2fa` | 1 | AzerothCore 0 |
| `account 2fa remove` | 1 | AzerothCore 0 |
| `account 2fa setup` | 1 | AzerothCore 0 |
| `account add` | 1 | WCell n/a (auth server, no role check recorded) |
| `account characters` | 1 | MaNGOS Zero 3 |
| `account email` | 1 | TrinityCore 0 |
| `account flag` | 1 | AzerothCore 2 |
| `account flag add` | 1 | AzerothCore 3 |
| `account flag list` | 1 | AzerothCore 2 |
| `account flag remove` | 1 | AzerothCore 3 |
| `account level` | 1 | ArcEmu z |
| `account list` | 1 | WCell n/a (auth server, no role check recorded) |
| `account mute` | 1 | ArcEmu a |
| `account onlinelist ip` | 1 | TrinityCore yes (level n/r) |
| `account onlinelist limit` | 1 | TrinityCore yes (level n/r) |
| `account onlinelist map` | 1 | TrinityCore yes (level n/r) |
| `account onlinelist zone` | 1 | TrinityCore yes (level n/r) |
| `account remove` | 1 | AzerothCore yes (level n/r) |
| `account remove country` | 1 | AzerothCore 3 |
| `account set 2fa` | 1 | AzerothCore 0 |
| `account set addon` | 1 | MaNGOS Zero 3 |
| `account set email` | 1 | AzerothCore 3 |
| `account set sec email` | 1 | TrinityCore 3 |
| `account set sec regmail` | 1 | TrinityCore 3 |
| `account set seclevel` | 1 | TrinityCore yes (level n/r) |
| `account setprop` | 1 | WCell n/a (auth server, no role check recorded) |
| `account setprop clientid` | 1 | WCell n/a (auth server, no role check recorded) |
| `account setprop email` | 1 | WCell n/a (auth server, no role check recorded) |
| `account setprop pass` | 1 | WCell n/a (auth server, no role check recorded) |
| `account setprop role` | 1 | WCell n/a (auth server, no role check recorded) |
| `account unmute` | 1 | ArcEmu a |
| `admin castall` | 1 | ArcEmu z |
| `admin dispelall` | 1 | ArcEmu z |
| `admin masssummon` | 1 | ArcEmu z |
| `admin playall` | 1 | ArcEmu z |
| `admin renameallinvalidchars` | 1 | ArcEmu z |
| `disable` | 1 | AzerothCore yes (level n/r) |
| `disable add` | 1 | AzerothCore yes (level n/r) |
| `disable add mmap` | 1 | TrinityCore 3 |
| `disable add outdoorpvp` | 1 | TrinityCore 3 |
| `disable remove` | 1 | AzerothCore yes (level n/r) |
| `disable remove mmap` | 1 | TrinityCore 3 |
| `disable remove outdoorpvp` | 1 | TrinityCore 3 |

#### Lookup, list and info (42)

| Command | Count | Cores (their level) |
|---|---|---|
| `lookup quest` | 4 | ArcEmu l; AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `lookup skill` | 4 | ArcEmu l; AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `lookup spell` | 4 | ArcEmu l; AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `guid` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 2 |
| `list auras` | 3 | AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `list creature` | 3 | AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `list item` | 3 | AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `list object` | 3 | AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `lookup area` | 3 | AzerothCore 1; MaNGOS Zero 1; TrinityCore 3 |
| `lookup player account` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 3 |
| `lookup player email` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 3 |
| `lookup player ip` | 3 | AzerothCore 2; MaNGOS Zero 2; TrinityCore 3 |
| `lookup taxinode` | 3 | AzerothCore 1; MaNGOS Zero 3; TrinityCore 3 |
| `list` | 2 | AzerothCore 1; MaNGOS Zero 3 |
| `list auras id` | 2 | AzerothCore 1; TrinityCore 3 |
| `list auras name` | 2 | AzerothCore 1; TrinityCore 3 |
| `list respawns` | 2 | AzerothCore 2; TrinityCore 2 |
| `lookup item set` | 2 | AzerothCore 1; TrinityCore 3 |
| `lookup map` | 2 | AzerothCore 1; TrinityCore 3 |
| `lookup player` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `lookup spell id` | 2 | AzerothCore 1; TrinityCore 3 |
| `info` | 1 | WCell Player |
| `list mail` | 1 | TrinityCore 3 |
| `list players` | 1 | MaNGOS Zero 3 |
| `list spawnpoints` | 1 | TrinityCore 3 |
| `list talents` | 1 | MaNGOS Zero 3 |
| `lookup account` | 1 | MaNGOS Zero 2 |
| `lookup account email` | 1 | MaNGOS Zero 2 |
| `lookup account ip` | 1 | MaNGOS Zero 2 |
| `lookup account name` | 1 | MaNGOS Zero 2 |
| `lookup gobject` | 1 | AzerothCore 1 |
| `lookup item id` | 1 | TrinityCore 3 |
| `lookup itemset` | 1 | MaNGOS Zero 3 |
| `lookup map id` | 1 | TrinityCore 3 |
| `lookup pool` | 1 | MaNGOS Zero 2 |
| `lookup quest id` | 1 | TrinityCore 3 |
| `lookup teleport` | 1 | AzerothCore 1 |
| `spellinfo` | 1 | AzerothCore 2 |
| `spellinfo all` | 1 | AzerothCore 2 |
| `spellinfo attributes` | 1 | AzerothCore 2 |
| `spellinfo effects` | 1 | AzerothCore 2 |
| `spellinfo targets` | 1 | AzerothCore 2 |

#### World, events and battlegrounds (36)

| Command | Count | Cores (their level) |
|---|---|---|
| `event activelist` | 2 | AzerothCore 2; TrinityCore 1 |
| `event info` | 2 | AzerothCore 2; TrinityCore 1 |
| `worldstate` | 2 | AzerothCore yes (level n/r); WCell Staff |
| `battleground` | 1 | WCell Staff |
| `battleground battleground` | 1 | ArcEmu e |
| `battleground bginfo` | 1 | ArcEmu e |
| `battleground config` | 1 | WCell Staff |
| `battleground config reload` | 1 | WCell Staff |
| `battleground create` | 1 | WCell Staff |
| `battleground delete` | 1 | WCell Staff |
| `battleground enter` | 1 | WCell Staff |
| `battleground forcestart` | 1 | ArcEmu z |
| `battleground getqueue` | 1 | ArcEmu z |
| `battleground info` | 1 | WCell Staff |
| `battleground invite` | 1 | WCell Staff |
| `battleground leave` | 1 | ArcEmu e |
| `battleground list` | 1 | WCell Staff |
| `battleground pausebg` | 1 | ArcEmu e |
| `battleground playsound` | 1 | ArcEmu e |
| `battleground prepare` | 1 | WCell Staff |
| `battleground setbgscore` | 1 | ArcEmu e |
| `battleground setworldstate` | 1 | ArcEmu e |
| `battleground setworldstates` | 1 | ArcEmu e |
| `battleground startbg` | 1 | ArcEmu e |
| `bg start` | 1 | TrinityCore 2 |
| `bg stop` | 1 | TrinityCore 2 |
| `event duration` | 1 | WCell Staff |
| `event find` | 1 | WCell Staff |
| `event occurence` | 1 | WCell Staff |
| `event remove` | 1 | WCell Staff |
| `worldstate init` | 1 | WCell Staff |
| `worldstate scourgeinvasion` | 1 | AzerothCore yes (level n/r) |
| `worldstate scourgeinvasion battleswon` | 1 | AzerothCore 3 |
| `worldstate scourgeinvasion show` | 1 | AzerothCore 3 |
| `worldstate scourgeinvasion startzone` | 1 | AzerothCore 3 |
| `worldstate scourgeinvasion state` | 1 | AzerothCore 3 |

#### Quest (30)

| Command | Count | Cores (their level) |
|---|---|---|
| `quest remove` | 4 | ArcEmu 2; AzerothCore 2; MaNGOS Zero 3; WCell Staff |
| `quest` | 3 | AzerothCore 2; MaNGOS Zero 3; WCell Staff |
| `quest complete` | 3 | ArcEmu 2; AzerothCore 2; MaNGOS Zero 3 |
| `quest add` | 2 | AzerothCore 2; MaNGOS Zero 3 |
| `quest lookup` | 2 | ArcEmu 2; WCell Staff |
| `quest reward` | 2 | ArcEmu 2; AzerothCore 2 |
| `quest status` | 2 | ArcEmu 2; AzerothCore 2 |
| `quest addboth` | 1 | ArcEmu 2 |
| `quest addfinish` | 1 | ArcEmu 2 |
| `quest addstart` | 1 | ArcEmu 2 |
| `quest delboth` | 1 | ArcEmu 2 |
| `quest delfinish` | 1 | ArcEmu 2 |
| `quest delstart` | 1 | ArcEmu 2 |
| `quest fail` | 1 | ArcEmu 2 |
| `quest finisher` | 1 | ArcEmu 2 |
| `quest finishspawn` | 1 | ArcEmu 2 |
| `quest giver` | 1 | ArcEmu 2 |
| `quest givereward` | 1 | WCell Staff |
| `quest goto` | 1 | WCell Staff |
| `quest item` | 1 | ArcEmu 2 |
| `quest list` | 1 | ArcEmu 2 |
| `quest load` | 1 | ArcEmu 2 |
| `quest reset` | 1 | WCell Staff |
| `quest start` | 1 | ArcEmu 2 |
| `quest startspawn` | 1 | ArcEmu 2 |
| `questsend` | 1 | WCell Staff |
| `questsend giverquestcomplete` | 1 | WCell Staff |
| `questsend giverquestdetails` | 1 | WCell Staff |
| `questsend invalid` | 1 | WCell Staff |
| `questsend pushresult` | 1 | WCell Staff |

#### Game objects (28)

| Command | Count | Cores (their level) |
|---|---|---|
| `gobject delete` | 4 | ArcEmu o; AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `gobject move` | 4 | ArcEmu g; AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `gobject activate` | 3 | ArcEmu o; AzerothCore 2; TrinityCore 2 |
| `gobject add` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `gobject info` | 3 | ArcEmu o; AzerothCore 1; TrinityCore 2 |
| `gobject near` | 3 | AzerothCore 1; MaNGOS Zero 2; TrinityCore 2 |
| `gobject target` | 3 | AzerothCore 1; MaNGOS Zero 2; TrinityCore 2 |
| `gobject turn` | 3 | AzerothCore 3; MaNGOS Zero 2; TrinityCore 2 |
| `gobject` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `gobject add temp` | 2 | AzerothCore 2; TrinityCore 2 |
| `gobject despawngroup` | 2 | AzerothCore 3; TrinityCore 2 |
| `gobject set state` | 2 | AzerothCore 3; TrinityCore 2 |
| `gobject spawngroup` | 2 | AzerothCore 3; TrinityCore 2 |
| `gobject anim` | 1 | MaNGOS Zero 2 |
| `gobject animprogress` | 1 | ArcEmu o |
| `gobject distance` | 1 | ArcEmu o |
| `gobject enable` | 1 | ArcEmu o |
| `gobject export` | 1 | ArcEmu o |
| `gobject faction` | 1 | ArcEmu o |
| `gobject load` | 1 | AzerothCore 3 |
| `gobject lootstate` | 1 | MaNGOS Zero 2 |
| `gobject portto` | 1 | ArcEmu v |
| `gobject respawn` | 1 | AzerothCore 2 |
| `gobject rotate` | 1 | ArcEmu g |
| `gobject scale` | 1 | ArcEmu o |
| `gobject select` | 1 | ArcEmu o |
| `gobject spawn` | 1 | ArcEmu o |
| `gobject state` | 1 | MaNGOS Zero 2 |

#### Server ops (25)

| Command | Count | Cores (their level) |
|---|---|---|
| `gm list` | 4 | ArcEmu 0; AzerothCore 3; MaNGOS Zero 3; TrinityCore 3 |
| `gm ingame` | 3 | AzerothCore 0; MaNGOS Zero 0; TrinityCore 1 |
| `gm visible` | 3 | AzerothCore 2; MaNGOS Zero 1; TrinityCore 1 |
| `gm fly` | 2 | MaNGOS Zero 3; TrinityCore 3 |
| `gm off` | 2 | AzerothCore 1; TrinityCore 1 |
| `gm on` | 2 | AzerothCore 1; TrinityCore 1 |
| `server corpses` | 2 | AzerothCore 2; MaNGOS Zero 2 |
| `server exit` | 2 | AzerothCore 4; MaNGOS Zero 4 |
| `gm allowwhispers` | 1 | ArcEmu c |
| `gm blockwhispers` | 1 | ArcEmu c |
| `gm setview` | 1 | MaNGOS Zero 1 |
| `gm spectator` | 1 | AzerothCore 2 |
| `gm whisperblock` | 1 | ArcEmu g |
| `server debug` | 1 | AzerothCore 3 |
| `server log` | 1 | MaNGOS Zero 4 |
| `server log filter` | 1 | MaNGOS Zero 4 |
| `server log level` | 1 | MaNGOS Zero 4 |
| `server netstatus` | 1 | ArcEmu 0 |
| `server plimit` | 1 | MaNGOS Zero 3 |
| `server rehash` | 1 | ArcEmu z |
| `server resetallraid` | 1 | MaNGOS Zero 3 |
| `server set closed` | 1 | AzerothCore 4 |
| `server set loglevel` | 1 | AzerothCore 4 |
| `server set security` | 1 | AzerothCore 4 |
| `shutdown` | 1 | WCell n/a (auth server, no role check recorded) |

#### Pets (15)

| Command | Count | Cores (their level) |
|---|---|---|
| `pet` | 2 | AzerothCore 2; WCell Staff |
| `pet create` | 2 | AzerothCore 2; TrinityCore 2 |
| `pet learn` | 2 | AzerothCore 2; TrinityCore 2 |
| `pet unlearn` | 2 | AzerothCore 2; TrinityCore 2 |
| `pet addspell` | 1 | ArcEmu m |
| `pet createpet` | 1 | ArcEmu m |
| `pet delete` | 1 | AzerothCore 3 |
| `pet dismiss` | 1 | ArcEmu m |
| `pet level` | 1 | TrinityCore 3 |
| `pet list` | 1 | AzerothCore 1 |
| `pet removespell` | 1 | ArcEmu m |
| `pet rename` | 1 | AzerothCore yes (level n/r) |
| `pet renamepet` | 1 | ArcEmu m |
| `pet setlevel` | 1 | ArcEmu m |
| `pet spawnbot` | 1 | ArcEmu a |

#### Guild and group (13)

| Command | Count | Cores (their level) |
|---|---|---|
| `guild rename` | 2 | ArcEmu m; AzerothCore 2 |
| `group` | 1 | AzerothCore 2 |
| `group disband` | 1 | AzerothCore 2 |
| `group invites` | 1 | AzerothCore 2 |
| `group join` | 1 | AzerothCore 2 |
| `group leader` | 1 | AzerothCore 2 |
| `group list` | 1 | AzerothCore 2 |
| `group remove` | 1 | AzerothCore 2 |
| `group revive` | 1 | AzerothCore 2 |
| `guild info` | 1 | AzerothCore 2 |
| `guild list` | 1 | WCell Staff |
| `guild members` | 1 | ArcEmu m |
| `guild say` | 1 | WCell Staff |

#### Instance ops (12)

| Command | Count | Cores (their level) |
|---|---|---|
| `instance savedata` | 3 | AzerothCore 3; MaNGOS Zero 3; TrinityCore 1 |
| `instance create` | 2 | ArcEmu z; WCell Staff |
| `instance getbossstate` | 2 | AzerothCore 1; TrinityCore 3 |
| `instance setbossstate` | 2 | AzerothCore 2; TrinityCore 3 |
| `instance delete` | 1 | WCell Staff |
| `instance enter` | 1 | WCell Staff |
| `instance exit` | 1 | ArcEmu m |
| `instance info` | 1 | ArcEmu m |
| `instance list` | 1 | WCell Staff |
| `instance reset` | 1 | ArcEmu z |
| `instance resetall` | 1 | ArcEmu m |
| `instance shutdown` | 1 | ArcEmu z |

## 4. Matrix

Rows are every command that is matched or relevant in at least one core. Cells: level (their native scale: ArcEmu letters, AzerothCore/TrinityCore 0-3, MaNGOS Zero 0-4, MangosSharp 2-4, WCell role), `yes (level n/r)` = present but level not recorded, `-` = absent, `n/a` = that core has it but the extractor classed it not applicable to 1.12.1. ArcaneCore column: `L<n>` on the vmangos 0-6 scale, `ABSENT` = gap.

### Instance ops (16 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `instance` | L2 | - | 1 | - | 3 | - | Staff |
| `instance create` | ABSENT | z | - | - | - | - | Staff |
| `instance delete` | ABSENT | - | - | - | - | - | Staff |
| `instance enter` | ABSENT | - | - | - | - | - | Staff |
| `instance exit` | ABSENT | m | - | - | - | - | - |
| `instance getbossstate` | ABSENT | - | 1 | - | - | 3 | - |
| `instance info` | ABSENT | m | - | - | - | - | - |
| `instance list` | ABSENT | - | - | - | - | - | Staff |
| `instance listbinds` | L3 | z | 1 | - | 3 | 1 | - |
| `instance reset` | ABSENT | z | - | - | - | - | - |
| `instance resetall` | ABSENT | m | - | - | - | - | - |
| `instance savedata` | ABSENT | - | 3 | - | 3 | 1 | - |
| `instance setbossstate` | ABSENT | - | 2 | - | - | 3 | - |
| `instance shutdown` | ABSENT | z | - | - | - | - | - |
| `instance stats` | L4 | - | 1 | - | 3 | 1 | - |
| `instance unbind` | L3 | - | 2 | - | 3 | 1 | - |

### Pets (15 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `pet` | ABSENT | - | 2 | - | - | - | Staff |
| `pet addspell` | ABSENT | m | - | - | - | - | - |
| `pet create` | ABSENT | - | 2 | - | - | 2 | - |
| `pet createpet` | ABSENT | m | - | - | - | - | - |
| `pet delete` | ABSENT | - | 3 | - | - | - | - |
| `pet dismiss` | ABSENT | m | - | - | - | - | - |
| `pet learn` | ABSENT | - | 2 | - | - | 2 | - |
| `pet level` | ABSENT | - | - | - | - | 3 | - |
| `pet list` | ABSENT | - | 1 | - | - | - | - |
| `pet removespell` | ABSENT | m | - | - | - | - | - |
| `pet rename` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `pet renamepet` | ABSENT | m | - | - | - | - | - |
| `pet setlevel` | ABSENT | m | - | - | - | - | - |
| `pet spawnbot` | ABSENT | a | - | - | - | - | - |
| `pet unlearn` | ABSENT | - | 2 | - | - | 2 | - |

### GM audit, tickets and bans (88 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `ban` | L[1, 3, 3, 6] | - | 2 | - | 3 | - | Admin |
| `ban account` | L3 | a | 2 | 2 | 3 | 3 | - |
| `ban all` | ABSENT | a | - | - | - | - | - |
| `ban character` | L3 | b | 2 | 2 | 3 | 3 | - |
| `ban ip` | L6 | m | 2 | - | 3 | 3 | - |
| `ban playeraccount` | ABSENT | - | 2 | - | - | 3 | - |
| `baninfo` | L1 | - | 2 | - | 3 | - | - |
| `baninfo account` | L1 | - | 2 | - | 3 | 3 | - |
| `baninfo character` | L1 | - | 2 | - | 3 | 3 | - |
| `baninfo ip` | L3 | - | 2 | - | 3 | 3 | - |
| `banlist` | L1 | - | 2 | - | 3 | - | - |
| `banlist account` | L1 | - | 2 | - | 3 | 3 | - |
| `banlist character` | L1 | - | 2 | - | 3 | 3 | - |
| `banlist ip` | L3 | - | 2 | - | 3 | 3 | - |
| `bans` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `bans add` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `bans lift` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `bans list` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `chatfilter` | ABSENT | - | 2 | - | - | - | - |
| `chatfilter add` | ABSENT | - | 3 | - | - | - | - |
| `chatfilter list` | ABSENT | - | 2 | - | - | - | - |
| `chatfilter remove` | ABSENT | - | 3 | - | - | - | - |
| `gmannounce` | ABSENT | u | 2 | - | - | 1 | - |
| `gmnotify` | ABSENT | - | 2 | - | - | 1 | - |
| `gmticket assign` | ABSENT | c | - | - | - | - | - |
| `gmticket comment` | ABSENT | c | - | - | - | - | - |
| `gmticket deletepermanent` | ABSENT | z | - | - | - | - | - |
| `gmticket delid` | ABSENT | c | - | - | - | - | - |
| `gmticket get` | ABSENT | c | - | - | - | - | - |
| `gmticket getid` | ABSENT | c | - | - | - | - | - |
| `gmticket list` | ABSENT | c | - | - | - | - | - |
| `gmticket release` | ABSENT | c | - | - | - | - | - |
| `gmticket remove` | ABSENT | c | - | - | - | - | - |
| `gmticket toggle` | ABSENT | z | - | - | - | - | - |
| `kick` | L2 | f | 2 | 2 | 2 | 2 | Staff |
| `kick account` | ABSENT | f | - | - | - | - | - |
| `kick ip` | ABSENT | f | - | - | - | - | - |
| `log` | ABSENT | - | - | - | - | - | Admin |
| `log togglelevel` | ABSENT | - | - | - | - | - | Admin |
| `mute` | ABSENT | - | 2 | - | 1 | 1 | - |
| `pinfo` | ABSENT | - | 2 | - | 2 | 2 | - |
| `rbac` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `rbac account` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `rbac account deny` | ABSENT | - | yes (level n/r) | - | - | n/a | - |
| `rbac account grant` | ABSENT | - | yes (level n/r) | - | - | n/a | - |
| `rbac account list` | ABSENT | - | yes (level n/r) | - | - | n/a | - |
| `rbac account revoke` | ABSENT | - | yes (level n/r) | - | - | n/a | - |
| `rbac list` | ABSENT | - | 3 | - | - | n/a | - |
| `ticket` | ABSENT | - | 2 | - | 2 | - | Staff |
| `ticket accept` | ABSENT | - | - | - | 3 | - | - |
| `ticket assign` | ABSENT | - | 2 | - | - | - | - |
| `ticket close` | ABSENT | - | 2 | - | 2 | - | - |
| `ticket closedlist` | ABSENT | - | 2 | - | - | - | - |
| `ticket comment` | ABSENT | - | 2 | - | - | - | - |
| `ticket complete` | ABSENT | - | 2 | - | - | - | - |
| `ticket current` | ABSENT | - | - | - | - | - | Staff |
| `ticket delete` | ABSENT | - | 3 | - | 3 | - | Staff |
| `ticket escalate` | ABSENT | - | 2 | - | - | - | - |
| `ticket escalatedlist` | ABSENT | - | 2 | - | - | - | - |
| `ticket goto` | ABSENT | - | - | - | - | - | Staff |
| `ticket info` | ABSENT | - | - | - | 2 | - | - |
| `ticket list` | ABSENT | - | 2 | - | 2 | - | Staff |
| `ticket meaccept` | ABSENT | - | - | - | 2 | - | - |
| `ticket notify` | ABSENT | - | - | - | - | - | Staff |
| `ticket onlinelist` | ABSENT | - | 2 | - | 2 | - | - |
| `ticket reset` | ABSENT | - | 4 | - | - | - | - |
| `ticket respond` | ABSENT | - | - | - | 2 | - | - |
| `ticket response` | ABSENT | - | 2 | - | - | - | - |
| `ticket response append` | ABSENT | - | 2 | - | - | - | - |
| `ticket response appendln` | ABSENT | - | 2 | - | - | - | - |
| `ticket response delete` | ABSENT | - | 2 | - | - | - | - |
| `ticket response show` | ABSENT | - | 2 | - | - | - | - |
| `ticket select` | ABSENT | - | - | - | - | - | Staff |
| `ticket selectnext` | ABSENT | - | - | - | - | - | Staff |
| `ticket show` | ABSENT | - | - | - | 2 | - | Staff |
| `ticket surveyclose` | ABSENT | - | - | - | 2 | - | - |
| `ticket togglesystem` | ABSENT | - | 3 | - | - | - | - |
| `ticket unassign` | ABSENT | - | 2 | - | - | - | - |
| `ticket unselect` | ABSENT | - | - | - | - | - | Staff |
| `ticket viewid` | ABSENT | - | 2 | - | - | - | - |
| `ticket viewname` | ABSENT | - | 2 | - | - | - | - |
| `unban` | L6 | - | 3 | 4 | 3 | - | - |
| `unban account` | L6 | z | 3 | - | 3 | 3 | - |
| `unban character` | L6 | b | 3 | - | 3 | 3 | - |
| `unban ip` | L6 | m | 3 | - | 3 | 3 | - |
| `unban playeraccount` | ABSENT | - | 3 | - | - | 3 | - |
| `unmute` | ABSENT | - | 2 | - | 1 | 1 | - |
| `whispers` | L1 | - | 1 | - | 1 | 1 | - |

### Account management (65 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `account` | ABSENT | - | 0 | - | 0 | 0 | n/a (auth server, no role check recorded) |
| `account 2fa` | ABSENT | - | 0 | - | - | - | - |
| `account 2fa remove` | ABSENT | - | 0 | - | - | n/a | - |
| `account 2fa setup` | ABSENT | - | 0 | - | - | n/a | - |
| `account add` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account characters` | ABSENT | - | - | - | 3 | - | - |
| `account create` | ABSENT | - | 4 | - | 4 | yes (level n/r) | - |
| `account delete` | ABSENT | - | 4 | - | 4 | yes (level n/r) | n/a (auth server, no role check recorded) |
| `account email` | ABSENT | - | - | - | - | 0 | - |
| `account flag` | ABSENT | - | 2 | - | - | - | - |
| `account flag add` | ABSENT | - | 3 | - | - | - | - |
| `account flag list` | ABSENT | - | 2 | - | - | - | - |
| `account flag remove` | ABSENT | - | 3 | - | - | - | - |
| `account info` | ABSENT | - | 2 | - | - | - | n/a (auth server, no role check recorded) |
| `account level` | ABSENT | z | - | - | - | - | - |
| `account list` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account lock` | ABSENT | - | yes (level n/r) | - | 0 | - | - |
| `account lock country` | ABSENT | - | 0 | - | - | 0 | - |
| `account lock ip` | ABSENT | - | 0 | - | - | 0 | - |
| `account mute` | ABSENT | a | - | - | - | - | - |
| `account onlinelist` | ABSENT | - | 4 | - | 4 | yes (level n/r) | - |
| `account onlinelist ip` | ABSENT | - | - | - | - | yes (level n/r) | - |
| `account onlinelist limit` | ABSENT | - | - | - | - | yes (level n/r) | - |
| `account onlinelist map` | ABSENT | - | - | - | - | yes (level n/r) | - |
| `account onlinelist zone` | ABSENT | - | - | - | - | yes (level n/r) | - |
| `account password` | ABSENT | - | 0 | - | 0 | 0 | - |
| `account remove` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `account remove country` | ABSENT | - | 3 | - | - | - | - |
| `account set` | ABSENT | - | 2 | - | 3 | - | - |
| `account set 2fa` | ABSENT | - | 0 | - | - | n/a | - |
| `account set addon` | ABSENT | - | n/a | - | 3 | n/a | - |
| `account set email` | ABSENT | - | 3 | - | - | - | - |
| `account set gmlevel` | ABSENT | - | 3 | - | 4 | yes (level n/r) | - |
| `account set password` | ABSENT | - | 3 | - | 4 | yes (level n/r) | - |
| `account set sec email` | ABSENT | - | - | - | - | 3 | - |
| `account set sec regmail` | ABSENT | - | - | - | - | 3 | - |
| `account set seclevel` | ABSENT | - | - | - | - | yes (level n/r) | - |
| `account setprop` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account setprop clientid` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account setprop email` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account setprop pass` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account setprop role` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `account unmute` | ABSENT | a | - | - | - | - | - |
| `admin castall` | ABSENT | z | - | - | - | - | - |
| `admin dispelall` | ABSENT | z | - | - | - | - | - |
| `admin masssummon` | ABSENT | z | - | - | - | - | - |
| `admin playall` | ABSENT | z | - | - | - | - | - |
| `admin renameallinvalidchars` | ABSENT | z | - | - | - | - | - |
| `disable` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `disable add` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `disable add battleground` | ABSENT | - | 3 | - | - | 3 | - |
| `disable add map` | ABSENT | - | 3 | - | - | 3 | - |
| `disable add mmap` | ABSENT | - | - | - | - | 3 | - |
| `disable add outdoorpvp` | ABSENT | - | n/a | - | - | 3 | - |
| `disable add quest` | ABSENT | - | 3 | - | - | 3 | - |
| `disable add spell` | ABSENT | - | 3 | - | - | 3 | - |
| `disable add vmap` | ABSENT | - | 3 | - | - | 3 | - |
| `disable remove` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `disable remove battleground` | ABSENT | - | 3 | - | - | 3 | - |
| `disable remove map` | ABSENT | - | 3 | - | - | 3 | - |
| `disable remove mmap` | ABSENT | - | - | - | - | 3 | - |
| `disable remove outdoorpvp` | ABSENT | - | n/a | - | - | 3 | - |
| `disable remove quest` | ABSENT | - | 3 | - | - | 3 | - |
| `disable remove spell` | ABSENT | - | 3 | - | - | 3 | - |
| `disable remove vmap` | ABSENT | - | 3 | - | - | 3 | - |

### Data reload and cache (197 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `autobroadcast` | ABSENT | - | 2 | - | - | - | - |
| `autobroadcast add` | ABSENT | - | 3 | - | - | - | - |
| `autobroadcast list` | ABSENT | - | 2 | - | - | - | - |
| `autobroadcast locale` | ABSENT | - | 3 | - | - | - | - |
| `autobroadcast remove` | ABSENT | - | 3 | - | - | - | - |
| `cache` | ABSENT | - | 1 | - | - | - | Staff |
| `cache delete` | ABSENT | - | 3 | - | - | - | - |
| `cache info` | ABSENT | - | 2 | - | - | - | - |
| `cache purge` | ABSENT | - | - | - | - | - | Staff |
| `cache refresh` | ABSENT | - | 2 | - | - | - | - |
| `config` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `config get` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `config list` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `config load` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `config save` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `config set` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `ipc` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `ipc start` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `ipc stop` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `ipc toggle` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `load` | ABSENT | - | - | - | - | - | Admin |
| `load all` | ABSENT | - | - | - | - | - | Admin |
| `load gos` | ABSENT | - | - | - | - | - | Admin |
| `load items` | ABSENT | - | - | - | - | - | Admin |
| `load loot` | ABSENT | - | - | - | - | - | Admin |
| `load npcs` | ABSENT | - | - | - | - | - | Admin |
| `load quests` | ABSENT | - | - | - | - | - | Admin |
| `mmap` | ABSENT | - | 3 | - | 3 | - | - |
| `mmap loadedtiles` | ABSENT | - | 3 | - | 2 | - | - |
| `mmap loc` | ABSENT | - | 3 | - | 2 | - | - |
| `mmap path` | ABSENT | - | 3 | - | 2 | - | - |
| `mmap stats` | ABSENT | - | 3 | - | 2 | - | - |
| `mmap testarea` | ABSENT | - | 3 | - | 2 | - | - |
| `mmap testheight` | ABSENT | - | - | - | 2 | - | - |
| `pdump` | ABSENT | - | 3 | - | 3 | - | - |
| `pdump copy` | ABSENT | - | 3 | - | - | yes (level n/r) | - |
| `pdump load` | ABSENT | - | 3 | - | 3 | 3 | - |
| `pdump write` | ABSENT | - | 3 | - | 3 | 3 | - |
| `reload` | L6 | - | 3 | - | 3 | - | Staff |
| `reload acore_string` | ABSENT | - | 3 | - | - | - | - |
| `reload all` | L6 | - | 3 | - | 3 | - | - |
| `reload all area` | ABSENT | - | 3 | - | - | - | - |
| `reload all gossips` | ABSENT | - | 3 | - | - | - | - |
| `reload all item` | ABSENT | - | 3 | - | - | - | - |
| `reload all locales` | ABSENT | - | 3 | - | - | - | - |
| `reload all loot` | ABSENT | - | 3 | - | - | - | - |
| `reload all npc` | ABSENT | - | 3 | - | - | - | - |
| `reload all quest` | ABSENT | - | 3 | - | - | - | - |
| `reload all scripts` | ABSENT | - | 3 | - | - | - | - |
| `reload all spell` | ABSENT | - | 3 | - | - | - | - |
| `reload all_area` | ABSENT | - | - | - | 3 | - | - |
| `reload all_eventai` | ABSENT | - | - | - | 3 | - | - |
| `reload all_gossips` | ABSENT | - | - | - | 3 | - | - |
| `reload all_item` | ABSENT | - | - | - | 3 | - | - |
| `reload all_locales` | ABSENT | - | - | - | 3 | - | - |
| `reload all_loot` | ABSENT | - | - | - | 3 | - | - |
| `reload all_npc` | ABSENT | - | - | - | 3 | - | - |
| `reload all_quest` | ABSENT | - | - | - | 3 | - | - |
| `reload all_scripts` | ABSENT | - | - | - | 3 | - | - |
| `reload all_spell` | ABSENT | - | - | - | 3 | - | - |
| `reload antidos_opcode_policies` | ABSENT | - | 3 | - | - | - | - |
| `reload areatrigger` | ABSENT | - | 3 | - | - | - | - |
| `reload areatrigger_involvedrelation` | ABSENT | - | 3 | - | - | - | - |
| `reload areatrigger_quest_end` | ABSENT | - | - | - | 3 | - | - |
| `reload areatrigger_tavern` | ABSENT | - | 3 | - | 3 | - | - |
| `reload areatrigger_teleport` | ABSENT | - | 3 | - | 3 | - | - |
| `reload auctions` | ABSENT | - | 3 | - | - | - | - |
| `reload autobroadcast` | ABSENT | - | 3 | - | 3 | - | - |
| `reload battleground_template` | ABSENT | - | 3 | - | - | - | - |
| `reload broadcast_text` | ABSENT | - | 3 | - | - | - | - |
| `reload chat_filter` | ABSENT | - | 3 | - | - | - | - |
| `reload command` | ABSENT | - | 3 | - | 3 | - | - |
| `reload conditions` | ABSENT | - | 3 | - | 3 | - | - |
| `reload config` | ABSENT | - | 3 | - | 3 | - | - |
| `reload creature_ai_scripts` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_ai_summons` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_ai_texts` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_battleground` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_linked_respawn` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload creature_movement_override` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_onkill_reputation` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_quest_end` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_quest_start` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_questender` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_queststarter` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_spells` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_template` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_template_classlevelstats` | ABSENT | - | - | - | 3 | - | - |
| `reload creature_template_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_text` | ABSENT | - | 3 | - | - | - | - |
| `reload creature_text_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload db_script_string` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_creature_death` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_creature_spell` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_event` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_go_use` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_gossip` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_quest_end` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_quest_start` | ABSENT | - | - | - | 3 | - | - |
| `reload dbscripts_on_spell` | ABSENT | - | - | - | 3 | - | - |
| `reload disables` | ABSENT | - | 3 | - | 3 | - | - |
| `reload disenchant_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload dungeon_access_requirements` | ABSENT | - | 3 | - | - | - | - |
| `reload dungeon_access_template` | ABSENT | - | 3 | - | - | - | - |
| `reload event_scripts` | ABSENT | - | 3 | - | - | - | - |
| `reload fishing_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload game_event_npc_vendor` | ABSENT | - | 3 | - | - | - | - |
| `reload game_graveyard` | ABSENT | - | 3 | - | - | - | - |
| `reload game_graveyard_zone` | ABSENT | - | - | - | 3 | - | - |
| `reload game_tele` | ABSENT | - | 3 | - | 3 | - | - |
| `reload gameobject_battleground` | ABSENT | - | - | - | 3 | - | - |
| `reload gameobject_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload gameobject_quest_end` | ABSENT | - | - | - | 3 | - | - |
| `reload gameobject_quest_start` | ABSENT | - | - | - | 3 | - | - |
| `reload gameobject_questender` | ABSENT | - | 3 | - | - | - | - |
| `reload gameobject_queststarter` | ABSENT | - | 3 | - | - | - | - |
| `reload gameobject_template_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload gm_tickets` | ABSENT | - | 3 | - | - | - | - |
| `reload gossip_menu` | ABSENT | - | 3 | - | 3 | - | - |
| `reload gossip_menu_option` | ABSENT | - | 3 | - | 3 | - | - |
| `reload gossip_menu_option_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload graveyard_zone` | ABSENT | - | 3 | - | - | - | - |
| `reload item_enchantment_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload item_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload item_required_target` | ABSENT | - | - | - | 3 | - | - |
| `reload item_set_name_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload item_set_names` | ABSENT | - | 3 | - | - | - | - |
| `reload item_template_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload locales_command` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_creature` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_gameobject` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_gossip_menu_option` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_item` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_npc_text` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_page_text` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_points_of_interest` | ABSENT | - | - | - | 3 | - | - |
| `reload locales_quest` | ABSENT | - | - | - | 3 | - | - |
| `reload mail_level_reward` | ABSENT | - | 3 | - | - | - | - |
| `reload mail_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload mail_server_template` | ABSENT | - | 3 | - | - | - | - |
| `reload mangos_string` | ABSENT | - | - | - | 3 | - | - |
| `reload module_string` | ABSENT | - | 3 | - | - | - | - |
| `reload motd` | ABSENT | - | 3 | - | - | - | - |
| `reload npc_text` | ABSENT | - | - | - | 3 | - | - |
| `reload npc_text_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload npc_trainer` | ABSENT | - | - | - | 3 | - | - |
| `reload npc_vendor` | ABSENT | - | 3 | - | 3 | - | - |
| `reload npcs` | ABSENT | - | - | - | - | - | Staff |
| `reload page_text` | ABSENT | - | 3 | - | 3 | - | - |
| `reload page_text_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload pickpocketing_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload player_loot_template` | ABSENT | - | 3 | - | - | - | - |
| `reload points_of_interest` | ABSENT | - | 3 | - | 3 | - | - |
| `reload points_of_interest_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload profanity_name` | ABSENT | - | 3 | - | - | - | - |
| `reload quest_greeting` | ABSENT | - | 3 | - | - | - | - |
| `reload quest_offer_reward_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload quest_request_item_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload quest_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload quest_template_locale` | ABSENT | - | 3 | - | - | - | - |
| `reload rbac` | ABSENT | - | 3 | - | - | - | - |
| `reload reference_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload reputation_reward_rate` | ABSENT | - | 3 | - | 3 | - | - |
| `reload reputation_spillover_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload reserved_name` | ABSENT | - | 3 | - | 3 | - | - |
| `reload script_binding` | ABSENT | - | - | - | 3 | - | - |
| `reload skill_discovery_template` | ABSENT | - | 3 | - | - | - | - |
| `reload skill_extra_item_template` | ABSENT | - | 3 | - | - | - | - |
| `reload skill_fishing_base_level` | ABSENT | - | 3 | - | 3 | - | - |
| `reload skinning_loot_template` | ABSENT | - | 3 | - | 3 | - | - |
| `reload smart_scripts` | ABSENT | - | 3 | - | - | - | - |
| `reload spawn_group` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_affect` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_area` | ABSENT | - | 3 | - | 3 | - | - |
| `reload spell_bonus_data` | ABSENT | - | 3 | - | 3 | - | - |
| `reload spell_chain` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_cone` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `reload spell_elixir` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_group` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_group_stack_rules` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_learn_spell` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_linked_spell` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_loot_template` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_pet_auras` | ABSENT | - | 3 | - | 3 | - | - |
| `reload spell_proc` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_proc_event` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_proc_item_enchant` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_required` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_script_target` | ABSENT | - | - | - | 3 | - | - |
| `reload spell_scripts` | ABSENT | - | 3 | - | - | - | - |
| `reload spell_target_position` | ABSENT | - | 3 | - | 3 | - | - |
| `reload spell_threats` | ABSENT | - | 3 | - | 3 | - | - |
| `reload trainer` | ABSENT | - | 3 | - | - | - | - |
| `reload warden_action` | ABSENT | - | 3 | - | - | - | - |
| `reload waypoint_data` | ABSENT | - | 3 | - | - | - | - |
| `reload waypoint_scripts` | ABSENT | - | 3 | - | - | - | - |

### NPC, creature and waypoints (164 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `ahbot` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items` | ABSENT | - | - | - | 3 | 3 | - |
| `ahbot items amount` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount blue` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount green` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount grey` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount orange` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount purple` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount white` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items amount yellow` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items blue` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items gray` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items green` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items orange` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items purple` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items ratio` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items ratio alliance` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items ratio horde` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items ratio neutral` | ABSENT | - | - | - | 3 | - | - |
| `ahbot items white` | ABSENT | - | - | - | - | 3 | - |
| `ahbot items yellow` | ABSENT | - | - | - | - | 3 | - |
| `ahbot ratio` | ABSENT | - | - | - | - | 3 | - |
| `ahbot ratio alliance` | ABSENT | - | - | - | - | 3 | - |
| `ahbot ratio horde` | ABSENT | - | - | - | - | 3 | - |
| `ahbot ratio neutral` | ABSENT | - | - | - | - | 3 | - |
| `ahbot rebuild` | ABSENT | - | - | - | 3 | 3 | - |
| `ahbot reload` | ABSENT | - | - | - | 3 | 3 | - |
| `ahbot status` | ABSENT | - | - | - | 3 | 3 | - |
| `ai` | ABSENT | - | - | - | - | - | Staff |
| `ai active` | ABSENT | - | - | - | - | - | Staff |
| `ai follow` | ABSENT | - | - | - | - | - | Staff |
| `ai movetome` | ABSENT | - | - | - | - | - | Staff |
| `creature add` | L3 | n | - | - | - | - | Staff |
| `creature delete` | L3 | n | - | - | - | - | - |
| `creature info` | L3 | n | - | - | - | - | - |
| `creature kill` | L3 | - | - | - | - | - | Staff |
| `creature respawn` | L3 | n | - | - | - | - | Staff |
| `npc` | ABSENT | - | 2 | - | 1 | - | Staff |
| `npc add` | ABSENT | - | 3 | - | 2 | 2 | - |
| `npc add formation` | ABSENT | - | 3 | - | - | 2 | - |
| `npc add item` | ABSENT | - | 3 | - | - | 2 | - |
| `npc add move` | ABSENT | - | 3 | - | - | 2 | - |
| `npc add temp` | ABSENT | - | 3 | - | - | 2 | - |
| `npc addagent` | ABSENT | n | - | - | - | - | - |
| `npc additem` | ABSENT | - | - | - | 2 | - | - |
| `npc aiinfo` | ABSENT | - | - | - | 2 | - | - |
| `npc allowmove` | ABSENT | - | - | - | 3 | - | - |
| `npc canfly` | ABSENT | n | - | - | - | - | - |
| `npc cast` | ABSENT | n | - | - | - | - | - |
| `npc changeentry` | ABSENT | - | - | - | 3 | - | - |
| `npc changelevel` | ABSENT | - | - | - | 2 | - | - |
| `npc come` | ABSENT | n | - | - | - | - | - |
| `npc delete` | ABSENT | - | 3 | - | 2 | 2 | - |
| `npc delete item` | ABSENT | - | 3 | - | - | 2 | - |
| `npc delitem` | ABSENT | - | - | - | 2 | - | - |
| `npc despawngroup` | ABSENT | - | 3 | - | - | 2 | - |
| `npc do` | ABSENT | - | 3 | - | - | - | - |
| `npc emote` | ABSENT | n | - | - | - | - | - |
| `npc equip1` | ABSENT | m | - | - | - | - | - |
| `npc equip2` | ABSENT | m | - | - | - | - | - |
| `npc equip3` | ABSENT | m | - | - | - | - | - |
| `npc evade` | ABSENT | - | - | - | - | 3 | - |
| `npc factionid` | ABSENT | - | - | - | 2 | - | - |
| `npc flag` | ABSENT | - | - | - | 2 | - | - |
| `npc flags` | ABSENT | n | - | - | - | - | Staff |
| `npc follow` | ABSENT | - | 2 | - | 2 | 2 | - |
| `npc follow stop` | ABSENT | - | 2 | - | - | 2 | - |
| `npc formationclear` | ABSENT | m | - | - | - | - | - |
| `npc formationlink1` | ABSENT | m | - | - | - | - | - |
| `npc formationlink2` | ABSENT | m | - | - | - | - | - |
| `npc goto` | ABSENT | - | - | - | - | - | Staff |
| `npc guid` | ABSENT | - | 2 | - | - | - | - |
| `npc info` | ABSENT | - | 2 | - | 3 | 3 | - |
| `npc listagent` | ABSENT | n | - | - | - | - | - |
| `npc load` | ABSENT | - | 3 | - | - | - | - |
| `npc loot` | ABSENT | m | - | - | - | - | - |
| `npc move` | ABSENT | - | 2 | - | 2 | 2 | - |
| `npc near` | ABSENT | - | 2 | - | - | 2 | - |
| `npc npcfollow` | ABSENT | m | - | - | - | - | - |
| `npc nullfollow` | ABSENT | m | - | - | - | - | - |
| `npc ongameobject` | ABSENT | n | - | - | - | - | - |
| `npc playemote` | ABSENT | - | 2 | - | 3 | 3 | - |
| `npc portto` | ABSENT | v | - | - | - | - | - |
| `npc possess` | ABSENT | n | - | - | - | - | - |
| `npc return` | ABSENT | n | - | - | - | - | - |
| `npc say` | ABSENT | n | 2 | - | 1 | 1 | - |
| `npc select` | ABSENT | n | - | - | - | - | Staff |
| `npc selectable` | ABSENT | - | - | - | - | - | Staff |
| `npc set` | ABSENT | - | 3 | - | - | - | - |
| `npc set allowmove` | ABSENT | - | 3 | - | - | 3 | - |
| `npc set data` | ABSENT | - | 3 | - | - | 3 | - |
| `npc set entry` | ABSENT | - | 3 | - | - | 3 | - |
| `npc set faction` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `npc set faction original` | ABSENT | - | 3 | - | - | - | - |
| `npc set faction permanent` | ABSENT | - | 3 | - | - | - | - |
| `npc set faction temp` | ABSENT | - | 3 | - | - | - | - |
| `npc set factionid` | ABSENT | - | - | - | - | 2 | - |
| `npc set flag` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set level` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set link` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set model` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set movetype` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set spawntime` | ABSENT | - | 3 | - | - | 2 | - |
| `npc set wanderdistance` | ABSENT | - | 3 | - | - | 2 | - |
| `npc setdeathstate` | ABSENT | - | - | - | 2 | - | - |
| `npc setmodel` | ABSENT | - | - | - | 2 | - | - |
| `npc setmovetype` | ABSENT | - | - | - | 2 | - | - |
| `npc showloot` | ABSENT | - | 2 | - | - | 2 | - |
| `npc spawndist` | ABSENT | - | - | - | 2 | - | - |
| `npc spawngroup` | ABSENT | - | 3 | - | - | 2 | - |
| `npc spawnlink` | ABSENT | n | - | - | - | - | - |
| `npc spawntime` | ABSENT | - | - | - | 2 | - | - |
| `npc tame` | ABSENT | - | 2 | - | 2 | 2 | - |
| `npc textemote` | ABSENT | - | 2 | - | 1 | 1 | - |
| `npc unfollow` | ABSENT | - | - | - | 2 | - | - |
| `npc unpossess` | ABSENT | n | - | - | - | - | - |
| `npc vendoradditem` | ABSENT | n | - | - | - | - | - |
| `npc vendorremoveitem` | ABSENT | n | - | - | - | - | - |
| `npc watch` | ABSENT | - | - | - | 2 | - | - |
| `npc whisper` | ABSENT | - | 2 | - | 1 | 1 | - |
| `npc yell` | ABSENT | n | 2 | - | 1 | 1 | - |
| `pool` | ABSENT | - | yes (level n/r) | - | 2 | - | - |
| `pool info` | ABSENT | - | 2 | - | - | - | - |
| `pool list` | ABSENT | - | - | - | 2 | - | - |
| `pool lookup` | ABSENT | - | 2 | - | - | - | - |
| `pool spawns` | ABSENT | - | - | - | 2 | - | - |
| `pooltools` | ABSENT | - | 3 | - | - | - | - |
| `pooltools add` | ABSENT | - | 3 | - | - | - | - |
| `pooltools clear` | ABSENT | - | 3 | - | - | - | - |
| `pooltools def` | ABSENT | - | 3 | - | - | - | - |
| `pooltools end` | ABSENT | - | 3 | - | - | - | - |
| `pooltools remove` | ABSENT | - | 3 | - | - | - | - |
| `pooltools start` | ABSENT | - | 3 | - | - | - | - |
| `respawn` | ABSENT | - | 2 | - | 3 | 3 | - |
| `respawn all` | ABSENT | - | 2 | - | - | - | - |
| `respawn creature entry` | ABSENT | - | 3 | - | - | - | - |
| `respawn creature guid` | ABSENT | - | 3 | - | - | - | - |
| `respawn gameobject entry` | ABSENT | - | 3 | - | - | - | - |
| `respawn gameobject guid` | ABSENT | - | 3 | - | - | - | - |
| `waypoint add` | ABSENT | w | - | - | - | - | - |
| `waypoint addfly` | ABSENT | w | - | - | - | - | - |
| `waypoint change` | ABSENT | w | - | - | - | - | - |
| `waypoint delete` | ABSENT | w | - | - | - | - | - |
| `waypoint deleteall` | ABSENT | w | - | - | - | - | - |
| `waypoint emote` | ABSENT | w | - | - | - | - | - |
| `waypoint flags` | ABSENT | w | - | - | - | - | - |
| `waypoint generate` | ABSENT | w | - | - | - | - | - |
| `waypoint hide` | ABSENT | w | - | - | - | - | - |
| `waypoint info` | ABSENT | w | - | - | - | - | - |
| `waypoint movehere` | ABSENT | w | - | - | - | - | - |
| `waypoint movetype` | ABSENT | w | - | - | - | - | - |
| `waypoint save` | ABSENT | w | - | - | - | - | - |
| `waypoint show` | ABSENT | w | - | - | - | - | - |
| `waypoint skin` | ABSENT | w | - | - | - | - | - |
| `waypoint waittime` | ABSENT | w | - | - | - | - | - |
| `wp` | ABSENT | - | 3 | - | 2 | - | - |
| `wp add` | ABSENT | - | 3 | - | 2 | - | - |
| `wp event` | ABSENT | - | 3 | - | - | - | - |
| `wp export` | ABSENT | - | - | - | 3 | - | - |
| `wp load` | ABSENT | - | 3 | - | - | - | - |
| `wp modify` | ABSENT | - | 3 | - | 2 | - | - |
| `wp reload` | ABSENT | - | 3 | - | - | - | - |
| `wp show` | ABSENT | - | 3 | - | 2 | - | - |
| `wp unload` | ABSENT | - | 3 | - | - | - | - |

### Game objects (28 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `gobject` | ABSENT | - | 2 | - | 2 | - | - |
| `gobject activate` | ABSENT | o | 2 | - | - | 2 | - |
| `gobject add` | ABSENT | - | 3 | - | 2 | 2 | - |
| `gobject add temp` | ABSENT | - | 2 | - | - | 2 | - |
| `gobject anim` | ABSENT | - | - | - | 2 | - | - |
| `gobject animprogress` | ABSENT | o | - | - | - | - | - |
| `gobject delete` | ABSENT | o | 3 | - | 2 | 2 | - |
| `gobject despawngroup` | ABSENT | - | 3 | - | - | 2 | - |
| `gobject distance` | ABSENT | o | - | - | - | - | - |
| `gobject enable` | ABSENT | o | - | - | - | - | - |
| `gobject export` | ABSENT | o | - | - | - | - | - |
| `gobject faction` | ABSENT | o | - | - | - | - | - |
| `gobject info` | ABSENT | o | 1 | - | - | 2 | - |
| `gobject load` | ABSENT | - | 3 | - | - | - | - |
| `gobject lootstate` | ABSENT | - | - | - | 2 | - | - |
| `gobject move` | ABSENT | g | 3 | - | 2 | 2 | - |
| `gobject near` | ABSENT | - | 1 | - | 2 | 2 | - |
| `gobject portto` | ABSENT | v | - | - | - | - | - |
| `gobject respawn` | ABSENT | - | 2 | - | - | - | - |
| `gobject rotate` | ABSENT | g | - | - | - | - | - |
| `gobject scale` | ABSENT | o | - | - | - | - | - |
| `gobject select` | ABSENT | o | - | - | - | - | - |
| `gobject set state` | ABSENT | - | 3 | - | - | 2 | - |
| `gobject spawn` | ABSENT | o | - | - | - | - | - |
| `gobject spawngroup` | ABSENT | - | 3 | - | - | 2 | - |
| `gobject state` | ABSENT | - | - | - | 2 | - | - |
| `gobject target` | ABSENT | - | 1 | - | 2 | 2 | - |
| `gobject turn` | ABSENT | - | 3 | - | 2 | 2 | - |

### Character state and cheats (247 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `additem` | L3 | m | 2 | 2 | 3 | 3 | Staff |
| `additem set` | ABSENT | - | 2 | - | - | 3 | - |
| `additem to` | ABSENT | - | - | - | - | 3 | - |
| `ah` | ABSENT | - | - | - | 3 | - | - |
| `ah console` | ABSENT | - | - | - | 3 | - | - |
| `ah console hide` | ABSENT | - | - | - | 3 | - | - |
| `ah console show` | ABSENT | - | - | - | 3 | - | - |
| `ah repair` | ABSENT | - | - | - | 3 | - | - |
| `appear` | ABSENT | - | 1 | - | 1 | 1 | - |
| `auction` | ABSENT | - | - | - | 3 | - | - |
| `auction alliance` | ABSENT | - | - | - | 3 | - | - |
| `auction goblin` | ABSENT | - | - | - | 3 | - | - |
| `auction horde` | ABSENT | - | - | - | 3 | - | - |
| `auction item` | ABSENT | - | - | - | 3 | - | - |
| `aura` | ABSENT | - | 2 | - | 3 | 3 | Staff |
| `aura add` | ABSENT | - | - | - | - | - | Staff |
| `aura dump` | ABSENT | - | - | - | - | - | Staff |
| `aura stack` | ABSENT | - | 2 | - | - | - | - |
| `bank` | ABSENT | - | - | 2 | 3 | 3 | Staff |
| `cast` | L5 | - | 2 | 3 | 3 | 3 | - |
| `cast back` | ABSENT | - | 2 | - | 3 | 3 | - |
| `cast dest` | ABSENT | - | 2 | - | - | 3 | - |
| `cast dist` | ABSENT | - | 2 | - | 3 | 3 | - |
| `cast self` | ABSENT | - | 2 | - | 3 | 3 | - |
| `cast target` | ABSENT | - | 2 | - | 3 | 3 | - |
| `character` | L2 | - | 2 | - | 2 | - | - |
| `character additemset` | ABSENT | m | - | - | - | - | - |
| `character advanceallskills` | ABSENT | m | - | - | - | - | - |
| `character advanceskill` | ABSENT | m | - | - | - | - | - |
| `character changeaccount` | ABSENT | - | 3 | - | - | 3 | - |
| `character check` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `character check bag` | ABSENT | - | 2 | - | - | - | - |
| `character check bank` | ABSENT | - | 2 | - | - | - | - |
| `character check profession` | ABSENT | - | 2 | - | - | - | - |
| `character deleted` | ABSENT | - | yes (level n/r) | - | 2 | - | - |
| `character deleted delete` | ABSENT | - | 4 | - | 4 | yes (level n/r) | - |
| `character deleted list` | ABSENT | - | 3 | - | 3 | 3 | - |
| `character deleted old` | ABSENT | - | - | - | 4 | yes (level n/r) | - |
| `character deleted purge` | ABSENT | - | 4 | - | - | - | - |
| `character deleted restore` | ABSENT | - | 3 | - | 3 | 3 | - |
| `character erase` | ABSENT | - | 4 | - | 4 | yes (level n/r) | - |
| `character forcerename` | ABSENT | m | - | - | - | - | - |
| `character getskillinfo` | ABSENT | m | - | - | - | - | - |
| `character increaseweaponskill` | ABSENT | m | - | - | - | - | - |
| `character learnskill` | ABSENT | m | - | - | - | - | - |
| `character level` | ABSENT | - | 2 | - | 3 | 3 | - |
| `character removeskill` | ABSENT | m | - | - | - | - | - |
| `character rename` | ABSENT | m | 2 | - | 2 | 2 | - |
| `character repairitems` | ABSENT | n | - | - | - | - | - |
| `character reputation` | L2 | m | 2 | - | 2 | 2 | - |
| `character resetreputation` | ABSENT | n | - | - | - | - | - |
| `character resetskills` | ABSENT | n | - | - | - | - | - |
| `character resetspells` | ABSENT | n | - | - | - | - | - |
| `character resettalents` | ABSENT | n | - | - | - | - | - |
| `character showitems` | ABSENT | m | - | - | - | - | - |
| `character showskills` | ABSENT | m | - | - | - | - | - |
| `cheat` | ABSENT | - | 2 | - | - | - | - |
| `cheat area` | ABSENT | m | - | - | - | - | - |
| `cheat casttime` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat cooldown` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat explore` | ABSENT | - | 2 | - | - | 3 | - |
| `cheat flyingmount` | ABSENT | m | - | - | - | - | - |
| `cheat god` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat itemstack` | ABSENT | m | - | - | - | - | - |
| `cheat power` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat stack` | ABSENT | m | - | - | - | - | - |
| `cheat status` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat taxi` | ABSENT | m | 2 | - | - | 3 | - |
| `cheat triggerpass` | ABSENT | m | - | - | - | - | - |
| `cheat waterwalk` | ABSENT | - | 2 | - | - | 3 | - |
| `combatstop` | ABSENT | - | 2 | - | 2 | 2 | - |
| `cooldown` | L3 | m | 2 | 3 | 3 | 3 | Staff |
| `damage` | ABSENT | - | 2 | - | 3 | 3 | - |
| `damage go` | ABSENT | - | - | - | - | 3 | - |
| `deserter` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `deserter bg` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `deserter bg add` | ABSENT | - | 3 | - | - | 3 | - |
| `deserter bg remove` | ABSENT | - | 3 | - | - | 3 | - |
| `deserter bg remove all` | ABSENT | - | 3 | - | - | - | - |
| `deserter instance` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `deserter instance add` | ABSENT | - | 3 | - | - | n/a | - |
| `deserter instance remove` | ABSENT | - | 3 | - | - | n/a | - |
| `deserter instance remove all` | ABSENT | - | 3 | - | - | - | - |
| `die` | ABSENT | - | 2 | - | 3 | 3 | - |
| `dismount` | ABSENT | h | 0 | - | 0 | 2 | - |
| `explorecheat` | L1 | m | - | - | 3 | - | Staff |
| `freeze` | ABSENT | - | 2 | - | - | 1 | - |
| `gear` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `gear repair` | ABSENT | - | 2 | - | - | - | - |
| `gear stats` | ABSENT | - | 0 | - | - | - | - |
| `gps` | L1 | 0 | 1 | 2 | 1 | 3 | Player |
| `hidearea` | L1 | - | 3 | - | 3 | 3 | - |
| `honor` | L3 | - | 2 | - | 2 | - | - |
| `honor add` | L4 | m | 2 | - | 2 | 1 | - |
| `honor add kill` | ABSENT | - | 2 | - | - | 1 | - |
| `honor addkill` | L4 | - | - | - | 2 | - | - |
| `honor addkills` | ABSENT | m | - | - | - | - | - |
| `honor globaldailyupdate` | ABSENT | m | - | - | - | - | - |
| `honor pvpcredit` | ABSENT | m | - | - | - | - | - |
| `honor show` | L2 | - | - | - | 2 | - | - |
| `honor singledailyupdate` | ABSENT | m | - | - | - | - | - |
| `honor update` | ABSENT | - | 2 | - | 2 | 1 | - |
| `inv` | ABSENT | - | - | - | - | - | Staff |
| `inv call` | ABSENT | - | - | - | - | - | Admin |
| `inv createset` | ABSENT | - | - | - | - | - | Staff |
| `inv enchant` | ABSENT | - | - | - | - | - | Staff |
| `inv find` | ABSENT | - | - | - | - | - | Staff |
| `inv get` | ABSENT | - | - | - | - | - | Admin |
| `inv mod` | ABSENT | - | - | - | - | - | Admin |
| `inv purgeall` | ABSENT | - | - | - | - | - | Staff |
| `inv set` | ABSENT | - | - | - | - | - | Admin |
| `inv strip` | ABSENT | - | - | - | - | - | Staff |
| `item` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `item move` | ABSENT | - | 2 | - | - | - | - |
| `item restore` | ABSENT | - | 2 | - | - | - | - |
| `item restore list` | ABSENT | - | 2 | - | - | - | - |
| `kill` | ABSENT | r | - | 2 | - | - | - |
| `learn` | L5 | m | 2 | 3 | 1 | 3 | Staff |
| `learn all` | ABSENT | - | 2 | - | 3 | - | - |
| `learn all blizzard` | ABSENT | - | - | - | - | 3 | - |
| `learn all crafts` | ABSENT | - | 2 | - | - | 3 | - |
| `learn all debug` | ABSENT | - | - | - | - | 3 | - |
| `learn all default` | ABSENT | - | 2 | - | - | 3 | - |
| `learn all gm` | ABSENT | - | 2 | - | - | - | - |
| `learn all lang` | ABSENT | - | 2 | - | - | - | - |
| `learn all languages` | ABSENT | - | - | - | - | 3 | - |
| `learn all my` | ABSENT | - | 2 | - | - | - | - |
| `learn all my class` | ABSENT | - | 2 | - | - | - | - |
| `learn all my quest` | ABSENT | - | 2 | - | - | - | - |
| `learn all my talents` | ABSENT | - | 2 | - | - | - | - |
| `learn all my trainer` | ABSENT | - | 2 | - | - | - | - |
| `learn all recipes` | ABSENT | - | 2 | - | - | 3 | - |
| `learn all talents` | ABSENT | - | - | - | - | 3 | - |
| `learn all_crafts` | ABSENT | - | - | - | 2 | - | - |
| `learn all_default` | ABSENT | - | - | - | 1 | - | - |
| `learn all_gm` | ABSENT | - | - | - | 2 | - | - |
| `learn all_lang` | ABSENT | - | - | - | 1 | - | - |
| `learn all_myclass` | ABSENT | - | - | - | 3 | - | - |
| `learn all_myspells` | ABSENT | - | - | - | 3 | - | - |
| `learn all_mytalents` | ABSENT | - | - | - | 3 | - | - |
| `learn all_recipes` | ABSENT | - | - | - | 2 | - | - |
| `learn my quests` | ABSENT | - | - | - | - | 3 | - |
| `learn my trainer` | ABSENT | - | - | - | - | 3 | - |
| `levelup` | L3 | m | 2 | - | 3 | 3 | Staff |
| `mail` | ABSENT | - | 2 | - | - | - | Staff |
| `mail list` | ABSENT | - | 2 | - | - | - | - |
| `mail read` | ABSENT | - | - | - | - | - | Staff |
| `mail return` | ABSENT | - | 2 | - | - | - | - |
| `mail send` | ABSENT | - | - | - | - | - | Staff |
| `maxskill` | L3 | - | 2 | - | 3 | 3 | - |
| `modify` | L2 | - | 2 | - | 1 | - | - |
| `modify agility` | ABSENT | m | - | - | - | - | - |
| `modify ap` | ABSENT | m | - | - | - | - | - |
| `modify arcane` | ABSENT | m | - | - | - | - | - |
| `modify armor` | ABSENT | m | - | - | - | - | - |
| `modify aspeed` | ABSENT | - | - | - | 1 | - | - |
| `modify bit` | ABSENT | - | 2 | - | - | - | - |
| `modify boundingraidius` | ABSENT | m | - | - | - | - | - |
| `modify bwalk` | ABSENT | - | - | - | 1 | - | - |
| `modify bytes0` | ABSENT | m | - | - | - | - | - |
| `modify bytes1` | ABSENT | m | - | - | - | - | - |
| `modify bytes2` | ABSENT | m | - | - | - | - | - |
| `modify combatreach` | ABSENT | m | - | - | - | - | - |
| `modify damage` | ABSENT | m | - | - | - | - | - |
| `modify displayid` | ABSENT | m | - | - | - | - | - |
| `modify drunk` | ABSENT | - | 2 | - | 1 | - | - |
| `modify dynamicflags` | ABSENT | m | - | - | - | - | - |
| `modify energy` | ABSENT | m | 2 | - | 1 | - | - |
| `modify faction` | ABSENT | m | 3 | - | 1 | - | - |
| `modify fire` | ABSENT | m | - | - | - | - | - |
| `modify flags` | ABSENT | m | - | - | - | - | - |
| `modify frost` | ABSENT | m | - | - | - | - | - |
| `modify gender` | ABSENT | m | 2 | - | 2 | - | - |
| `modify happiness` | ABSENT | m | - | - | - | - | - |
| `modify holy` | ABSENT | m | - | - | - | - | - |
| `modify honor` | L4 | - | 2 | - | 1 | - | - |
| `modify hp` | L3 | m | 2 | - | 1 | - | Staff |
| `modify intelligence` | ABSENT | m | - | - | - | - | - |
| `modify level` | ABSENT | m | - | - | - | - | - |
| `modify mana` | L3 | m | 2 | - | 1 | - | - |
| `modify money` | L4 | m | 2 | 2 | 1 | - | - |
| `modify morph` | ABSENT | - | - | - | 2 | - | - |
| `modify mount` | ABSENT | - | 2 | - | 1 | - | - |
| `modify nativedisplayid` | ABSENT | m | - | - | - | - | - |
| `modify nature` | ABSENT | m | - | - | - | - | - |
| `modify npcemotestate` | ABSENT | m | - | - | - | - | - |
| `modify rage` | ABSENT | m | 2 | - | 1 | - | - |
| `modify rangeap` | ABSENT | m | - | - | - | - | - |
| `modify rep` | L4 | m | - | 2 | 2 | - | - |
| `modify reputation` | ABSENT | - | 2 | - | - | - | - |
| `modify scale` | ABSENT | m | 2 | - | 1 | - | - |
| `modify shadow` | ABSENT | m | - | - | - | - | - |
| `modify speed` | ABSENT | m | 2 | - | 1 | - | - |
| `modify speed all` | ABSENT | - | 2 | - | - | - | - |
| `modify speed backwalk` | ABSENT | - | 2 | - | - | - | - |
| `modify speed swim` | ABSENT | - | 2 | - | - | - | - |
| `modify speed walk` | ABSENT | - | 2 | - | - | - | - |
| `modify spell` | ABSENT | - | 4 | - | - | - | - |
| `modify spirit` | ABSENT | m | - | - | - | - | - |
| `modify standstate` | ABSENT | - | 2 | - | 2 | - | - |
| `modify strength` | ABSENT | m | - | - | - | - | - |
| `modify swim` | ABSENT | - | - | - | 1 | - | - |
| `modify talentpoints` | ABSENT | m | 2 | - | - | - | - |
| `modify tp` | ABSENT | - | - | - | 1 | - | - |
| `morph` | ABSENT | - | 1 | - | - | - | - |
| `morph mount` | ABSENT | - | 1 | - | - | - | - |
| `morph reset` | ABSENT | - | 1 | - | - | - | - |
| `morph target` | ABSENT | - | 1 | - | - | - | - |
| `playsound` | ABSENT | - | - | 3 | - | - | - |
| `reset` | ABSENT | - | 3 | - | 3 | - | - |
| `reset all` | ABSENT | - | 4 | - | 3 | - | - |
| `reset honor` | ABSENT | - | 3 | - | 3 | - | - |
| `reset items` | ABSENT | - | 3 | - | 3 | - | - |
| `reset items all` | ABSENT | - | 3 | - | - | - | - |
| `reset items allbags` | ABSENT | - | 3 | - | - | - | - |
| `reset items bags` | ABSENT | - | 3 | - | - | - | - |
| `reset items bank` | ABSENT | - | 3 | - | - | - | - |
| `reset items equipped` | ABSENT | - | 3 | - | - | - | - |
| `reset items keyring` | ABSENT | - | 3 | - | - | - | - |
| `reset items vendor_buyback` | ABSENT | - | 3 | - | - | - | - |
| `reset level` | ABSENT | - | 3 | - | 3 | - | - |
| `reset mail` | ABSENT | - | - | - | 3 | - | - |
| `reset spells` | ABSENT | - | 3 | - | 3 | - | - |
| `reset stats` | ABSENT | - | 3 | - | 3 | - | - |
| `reset talents` | ABSENT | - | 3 | - | 3 | - | - |
| `revive` | L3 | r | 2 | 2 | 3 | 3 | Staff |
| `send` | ABSENT | - | 2 | - | 1 | - | - |
| `send items` | ABSENT | - | 2 | - | 3 | 1 | - |
| `send mail` | ABSENT | - | 2 | - | 1 | 2 | - |
| `send mass` | ABSENT | - | - | - | 3 | - | - |
| `send mass items` | ABSENT | - | - | - | 3 | - | - |
| `send mass mail` | ABSENT | - | - | - | 3 | - | - |
| `send mass money` | ABSENT | - | - | - | 3 | - | - |
| `send message` | ABSENT | - | 3 | - | 3 | 2 | - |
| `send money` | ABSENT | - | 2 | - | 3 | 2 | - |
| `setskill` | L3 | - | 2 | - | 3 | 3 | Staff |
| `showarea` | L1 | - | 2 | - | 3 | 3 | - |
| `skill` | ABSENT | - | - | - | - | - | Staff |
| `skill learn` | ABSENT | - | - | - | - | - | Staff |
| `skill tier` | ABSENT | - | - | - | - | - | Staff |
| `spell` | ABSENT | - | - | - | - | - | Staff |
| `spell clear` | ABSENT | - | - | - | - | - | Staff |
| `spell trigger` | ABSENT | - | - | - | - | - | Staff |
| `summon` | ABSENT | - | 2 | - | 1 | 1 | - |
| `unaura` | L3 | m | 2 | - | 3 | 3 | Staff |
| `unfreeze` | ABSENT | - | 2 | - | - | 1 | - |
| `unlearn` | L3 | m | 2 | 3 | 3 | 3 | Staff |

### Quest (30 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `quest` | ABSENT | - | 2 | - | 3 | - | Staff |
| `quest add` | ABSENT | - | 2 | - | 3 | - | - |
| `quest addboth` | ABSENT | 2 | - | - | - | - | - |
| `quest addfinish` | ABSENT | 2 | - | - | - | - | - |
| `quest addstart` | ABSENT | 2 | - | - | - | - | - |
| `quest complete` | ABSENT | 2 | 2 | - | 3 | - | - |
| `quest delboth` | ABSENT | 2 | - | - | - | - | - |
| `quest delfinish` | ABSENT | 2 | - | - | - | - | - |
| `quest delstart` | ABSENT | 2 | - | - | - | - | - |
| `quest fail` | ABSENT | 2 | - | - | - | - | - |
| `quest finisher` | ABSENT | 2 | - | - | - | - | - |
| `quest finishspawn` | ABSENT | 2 | - | - | - | - | - |
| `quest giver` | ABSENT | 2 | - | - | - | - | - |
| `quest givereward` | ABSENT | - | - | - | - | - | Staff |
| `quest goto` | ABSENT | - | - | - | - | - | Staff |
| `quest item` | ABSENT | 2 | - | - | - | - | - |
| `quest list` | ABSENT | 2 | - | - | - | - | - |
| `quest load` | ABSENT | 2 | - | - | - | - | - |
| `quest lookup` | ABSENT | 2 | - | - | - | - | Staff |
| `quest remove` | ABSENT | 2 | 2 | - | 3 | - | Staff |
| `quest reset` | ABSENT | - | - | - | - | - | Staff |
| `quest reward` | ABSENT | 2 | 2 | - | - | - | - |
| `quest start` | ABSENT | 2 | - | - | - | - | - |
| `quest startspawn` | ABSENT | 2 | - | - | - | - | - |
| `quest status` | ABSENT | 2 | 2 | - | - | - | - |
| `questsend` | ABSENT | - | - | - | - | - | Staff |
| `questsend giverquestcomplete` | ABSENT | - | - | - | - | - | Staff |
| `questsend giverquestdetails` | ABSENT | - | - | - | - | - | Staff |
| `questsend invalid` | ABSENT | - | - | - | - | - | Staff |
| `questsend pushresult` | ABSENT | - | - | - | - | - | Staff |

### Teleport and navigation (81 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `distance` | ABSENT | - | 3 | - | 3 | 3 | Staff |
| `go` | L2 | - | 1 | - | 1 | - | Staff |
| `go anim` | ABSENT | - | - | - | - | - | Staff |
| `go areatrigger` | ABSENT | - | - | - | - | 1 | - |
| `go boss` | ABSENT | - | - | - | - | 1 | - |
| `go call` | ABSENT | - | - | - | - | - | Admin |
| `go creature` | ABSENT | - | 1 | - | 1 | 1 | - |
| `go creature id` | ABSENT | - | 1 | - | - | 1 | - |
| `go creature name` | ABSENT | - | 1 | - | - | - | - |
| `go deselect` | ABSENT | - | - | - | - | - | Staff |
| `go gameobject` | ABSENT | - | 1 | - | - | 1 | - |
| `go gameobject id` | ABSENT | - | 1 | - | - | 1 | - |
| `go get` | ABSENT | - | - | - | - | - | Admin |
| `go graveyard` | ABSENT | - | 1 | - | 1 | 1 | - |
| `go grid` | ABSENT | - | 1 | - | 1 | 1 | - |
| `go instance` | ABSENT | - | - | - | - | 1 | - |
| `go object` | ABSENT | - | - | - | 1 | - | - |
| `go offset` | ABSENT | - | - | - | - | 1 | - |
| `go quest` | ABSENT | - | 1 | - | - | - | - |
| `go select` | ABSENT | - | - | - | - | - | Staff |
| `go set` | ABSENT | - | - | - | - | - | Admin |
| `go spawn` | ABSENT | - | - | - | - | - | Staff |
| `go taxinode` | ABSENT | - | 1 | - | 1 | 1 | - |
| `go ticket` | ABSENT | - | 2 | - | - | 1 | - |
| `go toggle` | ABSENT | - | - | - | - | - | Staff |
| `go trigger` | ABSENT | - | 1 | - | 1 | - | - |
| `go xy` | ABSENT | - | - | - | 1 | - | - |
| `go xyz` | L2 | v | 1 | 2 | 1 | 1 | - |
| `go zonexy` | ABSENT | - | 1 | - | 1 | 1 | - |
| `goname` | L2 | v | - | 2 | - | - | Staff |
| `grid` | ABSENT | - | - | - | 2 | - | - |
| `grid anchors` | ABSENT | - | - | - | 2 | - | - |
| `grid info` | ABSENT | - | - | - | 2 | - | - |
| `grid lwstats` | ABSENT | - | - | - | 2 | - | - |
| `linkgrave` | ABSENT | - | 3 | - | 3 | 3 | - |
| `map` | ABSENT | - | - | - | - | - | Admin |
| `map clear` | ABSENT | - | - | - | - | - | Admin |
| `map list` | ABSENT | - | - | - | - | - | Admin |
| `map spawn` | ABSENT | - | - | - | - | - | Admin |
| `map updates` | ABSENT | - | - | - | - | - | Admin |
| `namego` | L2 | v | - | 2 | - | - | Staff |
| `nav` | ABSENT | - | - | - | - | - | Staff |
| `nav clear` | ABSENT | - | - | - | - | - | Staff |
| `nav path` | ABSENT | - | - | - | - | - | Staff |
| `nav show` | ABSENT | - | - | - | - | - | Staff |
| `neargrave` | L3 | - | 2 | - | 3 | 3 | - |
| `recall` | L1 | - | 2 | - | 1 | 1 | - |
| `recall add` | ABSENT | q | - | - | - | - | - |
| `recall del` | ABSENT | q | - | - | - | - | - |
| `recall list` | ABSENT | q | - | - | - | - | - |
| `recall port` | ABSENT | q | - | - | - | - | - |
| `recall portplayer` | ABSENT | m | - | - | - | - | - |
| `recall portus` | ABSENT | m | - | - | - | - | - |
| `taxi` | ABSENT | - | - | - | - | - | Staff |
| `taxi activate` | ABSENT | - | - | - | - | - | Staff |
| `taxi go` | ABSENT | - | - | - | - | - | Staff |
| `taxi gotonext` | ABSENT | - | - | - | - | - | Staff |
| `taxi info` | ABSENT | - | - | - | - | - | Staff |
| `taxi list` | ABSENT | - | - | - | - | - | Staff |
| `taxi show` | ABSENT | - | - | - | - | - | Staff |
| `taxi stop` | ABSENT | - | - | - | - | - | Staff |
| `tele` | L2 | - | - | 2 | 1 | 1 | Staff |
| `tele add` | ABSENT | - | - | - | 3 | 3 | - |
| `tele del` | ABSENT | - | - | - | 3 | 3 | - |
| `tele group` | ABSENT | - | - | - | 1 | 1 | - |
| `tele name` | L2 | - | - | - | 1 | 1 | - |
| `tele name npc guid` | ABSENT | - | - | - | - | 1 | - |
| `tele name npc id` | ABSENT | - | - | - | - | 1 | - |
| `tele name npc name` | ABSENT | - | - | - | - | 1 | - |
| `teleport` | ABSENT | - | 2 | - | - | - | - |
| `teleport add` | ABSENT | - | 3 | - | - | - | - |
| `teleport del` | ABSENT | - | 3 | - | - | - | - |
| `teleport group` | ABSENT | - | 2 | - | - | - | - |
| `teleport name` | ABSENT | - | 2 | - | - | - | - |
| `teleport name npc` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `teleport name npc guid` | ABSENT | - | 2 | - | - | - | - |
| `teleport name npc id` | ABSENT | - | 2 | - | - | - | - |
| `teleport name npc name` | ABSENT | - | 2 | - | - | - | - |
| `trigger` | ABSENT | - | - | - | 2 | - | - |
| `trigger active` | ABSENT | - | - | - | 2 | - | - |
| `trigger near` | ABSENT | - | - | - | 2 | - | - |

### Lookup, list and info (51 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `commands` | L0 | 0 | 0 | - | 0 | 0 | - |
| `guid` | ABSENT | - | 2 | - | 2 | 2 | - |
| `help` | L0 | 0 | 0 | 2 | 0 | 0 | - |
| `info` | ABSENT | - | - | - | - | - | Player |
| `list` | ABSENT | - | 1 | - | 3 | - | - |
| `list auras` | ABSENT | - | 1 | - | 3 | 3 | - |
| `list auras id` | ABSENT | - | 1 | - | - | 3 | - |
| `list auras name` | ABSENT | - | 1 | - | - | 3 | - |
| `list creature` | ABSENT | - | 1 | - | 3 | 3 | - |
| `list item` | ABSENT | - | 1 | - | 3 | 3 | - |
| `list mail` | ABSENT | - | - | - | - | 3 | - |
| `list object` | ABSENT | - | 1 | - | 3 | 3 | - |
| `list players` | ABSENT | - | - | - | 3 | - | - |
| `list respawns` | ABSENT | - | 2 | - | - | 2 | - |
| `list spawnpoints` | ABSENT | - | - | - | - | 3 | - |
| `list talents` | ABSENT | - | - | - | 3 | - | - |
| `lookup` | L1 | - | 1 | - | 1 | - | - |
| `lookup account` | ABSENT | - | - | - | 2 | - | - |
| `lookup account email` | ABSENT | - | - | - | 2 | - | - |
| `lookup account ip` | ABSENT | - | - | - | 2 | - | - |
| `lookup account name` | ABSENT | - | - | - | 2 | - | - |
| `lookup area` | ABSENT | - | 1 | - | 1 | 3 | - |
| `lookup creature` | L2 | l | 1 | - | 3 | 3 | - |
| `lookup event` | L2 | - | 1 | - | 2 | 3 | - |
| `lookup faction` | L2 | l | 1 | - | 3 | 3 | - |
| `lookup gobject` | ABSENT | - | 1 | - | - | - | - |
| `lookup item` | L2 | l | 1 | - | 3 | 3 | - |
| `lookup item id` | ABSENT | - | - | - | - | 3 | - |
| `lookup item set` | ABSENT | - | 1 | - | - | 3 | - |
| `lookup itemset` | ABSENT | - | - | - | 3 | - | - |
| `lookup map` | ABSENT | - | 1 | - | - | 3 | - |
| `lookup map id` | ABSENT | - | - | - | - | 3 | - |
| `lookup object` | L2 | l | 1 | - | 3 | 3 | - |
| `lookup player` | ABSENT | - | 2 | - | 2 | - | - |
| `lookup player account` | ABSENT | - | 2 | - | 2 | 3 | - |
| `lookup player email` | ABSENT | - | 2 | - | 2 | 3 | - |
| `lookup player ip` | ABSENT | - | 2 | - | 2 | 3 | - |
| `lookup pool` | ABSENT | - | - | - | 2 | - | - |
| `lookup quest` | ABSENT | l | 1 | - | 3 | 3 | - |
| `lookup quest id` | ABSENT | - | - | - | - | 3 | - |
| `lookup skill` | ABSENT | l | 1 | - | 3 | 3 | - |
| `lookup spell` | ABSENT | l | 1 | - | 3 | 3 | - |
| `lookup spell id` | ABSENT | - | 1 | - | - | 3 | - |
| `lookup taxinode` | ABSENT | - | 1 | - | 3 | 3 | - |
| `lookup tele` | L2 | - | - | - | 1 | 3 | - |
| `lookup teleport` | ABSENT | - | 1 | - | - | - | - |
| `spellinfo` | ABSENT | - | 2 | - | - | - | - |
| `spellinfo all` | ABSENT | - | 2 | - | - | - | - |
| `spellinfo attributes` | ABSENT | - | 2 | - | - | - | - |
| `spellinfo effects` | ABSENT | - | 2 | - | - | - | - |
| `spellinfo targets` | ABSENT | - | 2 | - | - | - | - |

### World, events and battlegrounds (43 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `announce` | L4 | u | 2 | - | 1 | 1 | Staff |
| `battleground` | ABSENT | - | - | - | - | - | Staff |
| `battleground battleground` | ABSENT | e | - | - | - | - | - |
| `battleground bginfo` | ABSENT | e | - | - | - | - | - |
| `battleground config` | ABSENT | - | - | - | - | - | Staff |
| `battleground config reload` | ABSENT | - | - | - | - | - | Staff |
| `battleground create` | ABSENT | - | - | - | - | - | Staff |
| `battleground delete` | ABSENT | - | - | - | - | - | Staff |
| `battleground enter` | ABSENT | - | - | - | - | - | Staff |
| `battleground forcestart` | ABSENT | z | - | - | - | - | - |
| `battleground getqueue` | ABSENT | z | - | - | - | - | - |
| `battleground info` | ABSENT | - | - | - | - | - | Staff |
| `battleground invite` | ABSENT | - | - | - | - | - | Staff |
| `battleground leave` | ABSENT | e | - | - | - | - | - |
| `battleground list` | ABSENT | - | - | - | - | - | Staff |
| `battleground pausebg` | ABSENT | e | - | - | - | - | - |
| `battleground playsound` | ABSENT | e | - | - | - | - | - |
| `battleground prepare` | ABSENT | - | - | - | - | - | Staff |
| `battleground setbgscore` | ABSENT | e | - | - | - | - | - |
| `battleground setworldstate` | ABSENT | e | - | - | - | - | - |
| `battleground setworldstates` | ABSENT | e | - | - | - | - | - |
| `battleground startbg` | ABSENT | e | - | - | - | - | - |
| `bg start` | ABSENT | - | - | - | - | 2 | - |
| `bg stop` | ABSENT | - | - | - | - | 2 | - |
| `event` | L3 | - | 2 | - | 2 | - | Staff |
| `event activelist` | ABSENT | - | 2 | - | - | 1 | - |
| `event duration` | ABSENT | - | - | - | - | - | Staff |
| `event find` | ABSENT | - | - | - | - | - | Staff |
| `event info` | ABSENT | - | 2 | - | - | 1 | - |
| `event list` | L3 | - | - | - | 2 | - | Staff |
| `event occurence` | ABSENT | - | - | - | - | - | Staff |
| `event remove` | ABSENT | - | - | - | - | - | Staff |
| `event start` | L4 | - | 2 | - | 2 | 1 | Staff |
| `event stop` | L4 | - | 2 | - | 2 | 1 | Staff |
| `notify` | L4 | - | 2 | 2 | 1 | 1 | Staff |
| `wchange` | L6 | - | 3 | 3 | 3 | 3 | - |
| `worldstate` | ABSENT | - | yes (level n/r) | - | - | - | Staff |
| `worldstate init` | ABSENT | - | - | - | - | - | Staff |
| `worldstate scourgeinvasion` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `worldstate scourgeinvasion battleswon` | ABSENT | - | 3 | - | - | - | - |
| `worldstate scourgeinvasion show` | ABSENT | - | 3 | - | - | - | - |
| `worldstate scourgeinvasion startzone` | ABSENT | - | 3 | - | - | - | - |
| `worldstate scourgeinvasion state` | ABSENT | - | 3 | - | - | - | - |

### Server ops (42 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `gm` | L2 | t | 1 | 2 | 0 | - | Staff |
| `gm allowwhispers` | ABSENT | c | - | - | - | - | - |
| `gm blockwhispers` | ABSENT | c | - | - | - | - | - |
| `gm chat` | L1 | - | 2 | - | 1 | 1 | - |
| `gm fly` | ABSENT | - | n/a | - | 3 | 3 | - |
| `gm ingame` | ABSENT | - | 0 | - | 0 | 1 | - |
| `gm list` | ABSENT | 0 | 3 | - | 3 | 3 | - |
| `gm off` | ABSENT | - | 1 | - | - | 1 | - |
| `gm on` | ABSENT | - | 1 | - | - | 1 | - |
| `gm setview` | ABSENT | - | - | - | 1 | - | - |
| `gm spectator` | ABSENT | - | 2 | - | - | - | - |
| `gm visible` | ABSENT | - | 2 | - | 1 | 1 | - |
| `gm whisperblock` | ABSENT | g | - | - | - | - | - |
| `save` | L0 | s | 0 | 3 | 0 | 0 | Staff |
| `saveall` | L6 | s | 2 | - | 1 | 1 | - |
| `server` | L0 | - | 3 | - | 0 | - | - |
| `server corpses` | ABSENT | - | 2 | - | 2 | - | - |
| `server debug` | ABSENT | - | 3 | - | - | - | - |
| `server exit` | ABSENT | - | 4 | - | 4 | - | - |
| `server idlerestart` | L6 | - | 4 | - | 3 | - | - |
| `server idlerestart cancel` | L6 | - | 3 | - | 3 | - | - |
| `server idleshutdown` | L6 | - | 4 | - | 3 | - | - |
| `server idleshutdown cancel` | L6 | - | 3 | - | 3 | - | - |
| `server info` | L0 | 0 | 0 | - | 0 | - | Staff |
| `server log` | ABSENT | - | - | - | 4 | - | - |
| `server log filter` | ABSENT | - | - | - | 4 | - | - |
| `server log level` | ABSENT | - | - | - | 4 | - | - |
| `server motd` | L0 | - | 0 | - | 0 | - | - |
| `server netstatus` | ABSENT | 0 | - | - | - | - | - |
| `server plimit` | ABSENT | - | - | - | 3 | - | - |
| `server rehash` | ABSENT | z | - | - | - | - | - |
| `server resetallraid` | ABSENT | - | - | - | 3 | - | - |
| `server restart` | L6 | z | 3 | - | 3 | - | - |
| `server restart cancel` | L6 | - | 3 | - | 3 | - | - |
| `server set` | L6 | - | yes (level n/r) | - | 3 | - | - |
| `server set closed` | ABSENT | - | 4 | - | - | - | - |
| `server set loglevel` | ABSENT | - | 4 | - | - | - | - |
| `server set motd` | L6 | m | 3 | - | 3 | - | - |
| `server set security` | ABSENT | - | 4 | - | - | - | - |
| `server shutdown` | L6 | z | 3 | - | 3 | - | Admin |
| `server shutdown cancel` | L6 | z | 3 | - | 3 | - | - |
| `shutdown` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |

### Guild and group (19 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `group` | ABSENT | - | 2 | - | - | - | - |
| `group disband` | ABSENT | - | 2 | - | - | - | - |
| `group invites` | ABSENT | - | 2 | - | - | - | - |
| `group join` | ABSENT | - | 2 | - | - | - | - |
| `group leader` | ABSENT | - | 2 | - | - | - | - |
| `group list` | ABSENT | - | 2 | - | - | - | - |
| `group remove` | ABSENT | - | 2 | - | - | - | - |
| `group revive` | ABSENT | - | 2 | - | - | - | - |
| `guild` | L3 | - | 2 | - | 2 | - | Staff |
| `guild create` | L3 | m | 2 | 3 | 2 | - | Staff |
| `guild delete` | L4 | m | 2 | - | 2 | - | Staff |
| `guild info` | ABSENT | - | 2 | - | - | - | - |
| `guild invite` | L3 | m | 2 | - | 2 | - | Staff |
| `guild list` | ABSENT | - | - | - | - | - | Staff |
| `guild members` | ABSENT | m | - | - | - | - | - |
| `guild rank` | L3 | - | 2 | - | 2 | - | Staff |
| `guild rename` | ABSENT | m | 2 | - | - | - | - |
| `guild say` | ABSENT | - | - | - | - | - | Staff |
| `guild uninvite` | L3 | m | 2 | - | 2 | - | Staff |

### Debug and developer (125 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `debug` | ABSENT | - | 2 | - | 1 | - | n/a (auth server, no role check recorded) |
| `debug addworldstate` | ABSENT | d | - | - | - | - | - |
| `debug aggrorange` | ABSENT | d | - | - | - | - | - |
| `debug aimove` | ABSENT | d | - | - | - | - | - |
| `debug aispelltestbegin` | ABSENT | d | - | - | - | - | - |
| `debug aispelltestcontinue` | ABSENT | d | - | - | - | - | - |
| `debug aispelltestskip` | ABSENT | d | - | - | - | - | - |
| `debug anim` | ABSENT | - | 3 | - | 2 | 1 | - |
| `debug areatriggers` | ABSENT | - | 3 | - | - | 1 | - |
| `debug auralist` | ABSENT | d | - | - | - | - | - |
| `debug auraremove` | ABSENT | d | - | - | - | - | - |
| `debug bg` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug boundary` | ABSENT | - | 3 | - | - | 1 | - |
| `debug buffers` | ABSENT | - | - | - | - | - | Admin |
| `debug calcthreat` | ABSENT | d | - | - | - | - | - |
| `debug castself` | ABSENT | d | - | - | - | - | - |
| `debug castspell` | ABSENT | d | - | - | - | - | - |
| `debug castspellne` | ABSENT | d | - | - | - | - | - |
| `debug clearworldstates` | ABSENT | d | - | - | - | - | - |
| `debug combat` | ABSENT | - | 3 | - | - | 1 | - |
| `debug cooldown` | ABSENT | - | 3 | - | - | - | - |
| `debug damageunit` | ABSENT | d | - | - | - | - | - |
| `debug deathstate` | ABSENT | d | - | - | - | - | - |
| `debug dist` | ABSENT | d | - | - | - | - | - |
| `debug dummy` | ABSENT | - | 3 | - | - | 1 | - |
| `debug dumpcoords` | ABSENT | d | - | - | - | - | - |
| `debug face` | ABSENT | d | - | - | - | - | - |
| `debug fade` | ABSENT | d | - | - | - | - | - |
| `debug gc` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `debug getbytes` | ABSENT | d | - | - | - | - | - |
| `debug getheight` | ABSENT | d | - | - | - | - | - |
| `debug getitemstate` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug getitemvalue` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug getpos` | ABSENT | d | - | - | - | - | - |
| `debug gettptime` | ABSENT | d | - | - | - | - | - |
| `debug getvalue` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug guidlimits` | ABSENT | - | - | - | - | 1 | - |
| `debug hostile` | ABSENT | - | 3 | - | - | - | - |
| `debug info` | ABSENT | - | - | - | - | - | Admin |
| `debug infront` | ABSENT | d | - | - | - | - | - |
| `debug initworldstates` | ABSENT | d | - | - | - | - | - |
| `debug instancespawn` | ABSENT | - | - | - | - | 1 | - |
| `debug itemexpire` | ABSENT | - | 3 | - | - | 1 | - |
| `debug itempushresult` | ABSENT | d | - | - | - | - | - |
| `debug landwalk` | ABSENT | d | - | - | - | - | - |
| `debug leap` | ABSENT | d | - | - | - | - | - |
| `debug loadcells` | ABSENT | - | - | - | - | 1 | - |
| `debug loot` | ABSENT | - | 2 | - | - | - | - |
| `debug lootrecipient` | ABSENT | - | 3 | - | 2 | 1 | - |
| `debug los` | ABSENT | - | 3 | - | - | 1 | - |
| `debug mapdata` | ABSENT | - | 3 | - | - | - | - |
| `debug minion` | ABSENT | - | - | - | 2 | - | - |
| `debug mod32value` | ABSENT | - | 3 | - | - | 1 | - |
| `debug moditemvalue` | ABSENT | - | - | - | 3 | - | - |
| `debug modvalue` | ABSENT | - | - | - | 3 | - | - |
| `debug moveflags` | ABSENT | - | 3 | - | - | 1 | - |
| `debug moveinfo` | ABSENT | d | - | - | - | - | - |
| `debug neargraveyard` | ABSENT | - | - | - | - | 1 | - |
| `debug objectcount` | ABSENT | - | 3 | - | - | 1 | - |
| `debug objectpool` | ABSENT | - | - | - | - | - | Admin |
| `debug play` | ABSENT | - | 1 | - | 1 | - | - |
| `debug play cinematic` | ABSENT | - | 3 | - | 1 | 1 | - |
| `debug play music` | ABSENT | - | 3 | - | - | 1 | - |
| `debug play sound` | ABSENT | - | 3 | - | 1 | 1 | - |
| `debug play visual` | ABSENT | - | 3 | - | - | - | - |
| `debug playsound` | ABSENT | d | - | - | - | - | - |
| `debug playspellvisual` | ABSENT | d | - | - | - | - | - |
| `debug raidreset` | ABSENT | - | - | - | - | 1 | - |
| `debug rangecheck` | ABSENT | d | - | - | - | - | - |
| `debug recv` | ABSENT | - | - | - | 3 | - | - |
| `debug reloaddefs` | ABSENT | - | - | - | - | - | Admin |
| `debug removeaura` | ABSENT | d | - | - | - | - | - |
| `debug removeworldstate` | ABSENT | d | - | - | - | - | - |
| `debug root` | ABSENT | d | - | - | - | - | - |
| `debug send` | ABSENT | - | 3 | - | 3 | - | - |
| `debug send buyerror` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send channelnotify` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send chatmessage` | ABSENT | - | 3 | - | - | 1 | - |
| `debug send chatmmessage` | ABSENT | - | - | - | 3 | - | - |
| `debug send equiperror` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send largepacket` | ABSENT | - | 3 | - | - | 1 | - |
| `debug send opcode` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send poi` | ABSENT | - | - | - | 3 | - | - |
| `debug send qinvalidmsg` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send qpartymsg` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send sellerror` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug send spellfail` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug sendfailed` | ABSENT | d | - | - | - | - | - |
| `debug sendmotd` | ABSENT | d | - | - | - | - | - |
| `debug sendpacket` | ABSENT | d | - | - | - | - | - |
| `debug setaurastate` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug setbit` | ABSENT | d | 3 | - | - | 1 | - |
| `debug setbytes` | ABSENT | d | - | - | - | - | - |
| `debug setitemvalue` | ABSENT | - | 3 | - | 3 | 1 | - |
| `debug setvalue` | ABSENT | d | 3 | - | 3 | 1 | - |
| `debug setweather` | ABSENT | d | - | - | - | - | - |
| `debug showemote` | ABSENT | d | - | - | - | - | - |
| `debug showreact` | ABSENT | d | - | - | - | - | - |
| `debug spawnwar` | ABSENT | d | - | - | - | - | - |
| `debug spellcheck` | ABSENT | - | - | - | 4 | - | - |
| `debug spellcoefs` | ABSENT | - | - | - | 3 | - | - |
| `debug spellmods` | ABSENT | - | - | - | 3 | - | - |
| `debug sqlquery` | ABSENT | d | - | - | - | - | - |
| `debug taxistart` | ABSENT | d | - | - | - | - | - |
| `debug testindoor` | ABSENT | d | - | - | - | - | - |
| `debug testlos` | ABSENT | d | - | - | - | - | - |
| `debug threat` | ABSENT | - | 3 | - | - | 1 | - |
| `debug threatinfo` | ABSENT | - | 3 | - | - | 1 | - |
| `debug threatlist` | ABSENT | d | - | - | - | - | - |
| `debug threatmod` | ABSENT | d | - | - | - | - | - |
| `debug transport` | ABSENT | - | - | - | - | 1 | - |
| `debug triggercinematic` | ABSENT | d | - | - | - | - | - |
| `debug unitstate` | ABSENT | - | 3 | - | - | - | - |
| `debug unroot` | ABSENT | d | - | - | - | - | - |
| `debug update` | ABSENT | - | 3 | - | - | 1 | - |
| `debug updateworldstate` | ABSENT | d | - | - | - | - | - |
| `debug uws` | ABSENT | - | 3 | - | 3 | - | - |
| `debug visibilitydata` | ABSENT | - | 3 | - | - | - | - |
| `debug warden force` | ABSENT | - | - | - | - | 1 | - |
| `debug waterwalk` | ABSENT | d | - | - | - | - | - |
| `debug worldstate` | ABSENT | - | - | - | - | 1 | - |
| `debug zonestats` | ABSENT | - | 1 | - | - | - | - |
| `dev` | ABSENT | - | 3 | - | - | 3 | n/a (auth server, no role check recorded) |
| `dev network` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `dumpchunk` | ABSENT | - | - | - | - | - | Staff |

### Other / unclassified (194 rows)

| Command | ArcaneCore | ArcEmu | AzerothCore | MangosSharp | MaNGOS Zero | TrinityCore | WCell |
|---|---|---|---|---|---|---|---|
| `abandon` | ABSENT | - | - | - | - | - | Staff |
| `activatego` | ABSENT | - | - | 3 | - | - | - |
| `additemset` | ABSENT | - | - | 2 | 3 | - | - |
| `addon` | ABSENT | - | - | - | - | - | Staff |
| `addon list` | ABSENT | - | - | - | - | - | Staff |
| `addon load` | ABSENT | - | - | - | - | - | Staff |
| `addrestedxp` | ABSENT | - | - | 3 | - | - | - |
| `addtrainerspell` | ABSENT | m | - | - | - | - | - |
| `addxp` | ABSENT | - | - | 3 | - | - | - |
| `auragroup` | ABSENT | - | - | - | 3 | - | - |
| `authremote` | ABSENT | - | - | - | - | - | Admin |
| `bags clear` | ABSENT | - | 2 | - | - | - | - |
| `bindsight` | ABSENT | - | 3 | - | - | 3 | - |
| `bm` | ABSENT | - | 2 | - | - | - | - |
| `calcdist` | ABSENT | 0 | - | - | - | - | - |
| `call` | ABSENT | - | - | - | - | - | Admin |
| `castspell` | ABSENT | - | - | 3 | - | - | - |
| `changemodel` | ABSENT | - | - | 2 | - | - | - |
| `changepassword` | ABSENT | - | - | 4 | - | - | - |
| `channel` | ABSENT | - | - | - | - | - | Staff |
| `channel set ownership` | ABSENT | - | - | - | - | 3 | - |
| `cleararea` | ABSENT | - | - | - | - | - | Staff |
| `cleardos` | ABSENT | - | - | - | - | - | Staff |
| `combatlist` | ABSENT | - | - | 3 | - | - | - |
| `cometome` | ABSENT | - | 3 | - | 3 | 3 | - |
| `content` | ABSENT | - | - | - | - | - | Admin |
| `content check` | ABSENT | - | - | - | - | - | Admin |
| `content load` | ABSENT | - | - | - | - | - | Admin |
| `control` | ABSENT | - | - | 4 | - | - | Staff |
| `cooldownlist` | ABSENT | - | - | 2 | - | - | - |
| `createaccount` | ABSENT | - | - | 4 | - | - | - |
| `db` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `db drop` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `db info` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `deleteitem` | L3 | m | - | - | - | - | Staff |
| `demorph` | ABSENT | m | - | - | 2 | - | - |
| `devtag` | ABSENT | 1 | - | - | - | - | - |
| `dumpinventory` | ABSENT | - | - | - | - | - | Staff |
| `dumpnetworkinfo` | ABSENT | - | - | - | - | - | Admin |
| `dumptpinfo` | ABSENT | - | - | - | - | - | Admin |
| `editor` | ABSENT | - | - | - | - | - | Staff |
| `email` | ABSENT | - | - | - | - | - | Staff |
| `exception` | ABSENT | - | - | - | - | - | Admin |
| `exception list` | ABSENT | - | - | - | - | - | Admin |
| `exception show` | ABSENT | - | - | - | - | - | Admin |
| `fixscale` | ABSENT | m | - | - | - | - | - |
| `flagdeserter` | ABSENT | - | - | - | - | - | Staff |
| `fly` | ABSENT | - | - | - | - | - | Staff |
| `forcerename` | ABSENT | - | - | 2 | - | - | - |
| `freezeplayer` | ABSENT | - | - | - | 2 | - | - |
| `get` | ABSENT | - | - | - | - | - | Admin |
| `getspell` | ABSENT | - | - | - | - | - | Staff |
| `givexp` | ABSENT | - | - | - | - | - | Staff |
| `global` | ABSENT | - | - | - | - | - | Admin |
| `gmnameannounce` | ABSENT | - | 2 | - | - | 1 | - |
| `gobjectadd` | ABSENT | - | - | 3 | - | - | - |
| `gobjectnear` | ABSENT | - | - | 3 | - | - | - |
| `gobjecttarget` | ABSENT | - | - | 3 | - | - | - |
| `gossip` | ABSENT | - | - | - | - | - | Staff |
| `gotogy` | ABSENT | - | - | 2 | - | - | - |
| `gotrig` | ABSENT | v | - | - | - | - | - |
| `groupgo` | ABSENT | - | - | - | 1 | - | - |
| `groupsummon` | ABSENT | - | 2 | - | - | - | - |
| `hateall` | ABSENT | - | - | - | - | - | Staff |
| `highlightgos` | ABSENT | - | - | - | - | - | Staff |
| `hover` | ABSENT | - | - | 2 | - | - | - |
| `hurt` | ABSENT | - | - | 2 | - | - | - |
| `inventory` | ABSENT | - | 1 | - | - | - | - |
| `inventory count` | ABSENT | - | 1 | - | - | - | - |
| `invincible` | ABSENT | j | - | - | - | - | - |
| `invisible` | ABSENT | i | - | - | - | - | - |
| `invul` | ABSENT | - | - | - | - | - | Staff |
| `iowait` | ABSENT | - | - | - | - | - | Admin |
| `itemmove` | ABSENT | - | - | - | 2 | 2 | - |
| `kickplayer` | ABSENT | b | - | - | - | - | - |
| `killplr` | ABSENT | r | - | - | - | - | - |
| `knockback` | ABSENT | - | - | - | - | - | Staff |
| `landwalk` | ABSENT | - | - | 2 | - | - | - |
| `learnskill` | ABSENT | - | - | 3 | - | - | - |
| `listdos` | ABSENT | - | - | - | - | - | Staff |
| `listfreeze` | ABSENT | - | - | - | - | 1 | - |
| `listplayers` | ABSENT | - | - | - | - | - | Staff |
| `loadscripts` | ABSENT | - | - | - | 3 | - | - |
| `localizer` | ABSENT | - | - | - | - | - | Staff |
| `localizer reload` | ABSENT | - | - | - | - | - | Staff |
| `localizer setlocale` | ABSENT | - | - | - | - | - | Staff |
| `logcomment` | ABSENT | 1 | - | - | - | - | - |
| `los` | ABSENT | - | - | 3 | - | - | - |
| `loveall` | ABSENT | - | - | - | - | - | Staff |
| `mailbox` | ABSENT | - | 1 | - | - | 3 | - |
| `makewild` | ABSENT | - | - | - | - | - | Staff |
| `mod` | ABSENT | - | - | - | - | - | Admin |
| `modauras` | ABSENT | - | - | - | - | - | Staff |
| `modauras flags` | ABSENT | - | - | - | - | - | Staff |
| `modauras level` | ABSENT | - | - | - | - | - | Staff |
| `modperiod` | ABSENT | m | - | - | - | - | - |
| `mount` | ABSENT | m | - | 2 | - | - | - |
| `movegens` | ABSENT | - | 3 | - | 3 | 3 | - |
| `multiplyspeed` | ABSENT | - | - | - | - | - | Staff |
| `mutehistory` | ABSENT | - | 2 | - | - | 1 | - |
| `nameannounce` | ABSENT | - | 2 | - | - | 1 | - |
| `npcadd` | ABSENT | - | - | 3 | - | - | - |
| `npcai` | ABSENT | - | - | 3 | - | - | - |
| `npcaistate` | ABSENT | - | - | 3 | - | - | - |
| `npccome` | ABSENT | - | - | 3 | - | - | - |
| `npcrespawn` | ABSENT | - | - | 3 | - | - | - |
| `opendoor` | ABSENT | - | 2 | - | - | - | - |
| `packetlog` | ABSENT | - | 2 | - | - | - | - |
| `paralyze` | ABSENT | b | - | - | - | - | - |
| `password` | ABSENT | - | - | - | - | - | Player |
| `pin` | ABSENT | - | - | - | - | - | Staff |
| `playall` | ABSENT | - | 2 | - | - | 2 | - |
| `player` | ABSENT | - | yes (level n/r) | - | - | - | - |
| `player learn` | ABSENT | - | 2 | - | - | - | - |
| `player unlearn` | ABSENT | - | 2 | - | - | - | - |
| `playerinfo` | ABSENT | m | - | - | - | - | - |
| `poi` | ABSENT | - | - | - | - | - | Staff |
| `portal` | ABSENT | - | - | - | - | - | Staff |
| `possess` | ABSENT | - | 2 | - | - | 3 | - |
| `pushback` | ABSENT | - | - | - | - | - | Staff |
| `pvpstats` | ABSENT | - | - | - | - | 0 | - |
| `quit` | ABSENT | - | - | - | 4 | - | - |
| `race` | ABSENT | - | - | - | - | - | Staff |
| `realm` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `realm delete` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `realm list` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `remove` | ABSENT | - | - | 3 | - | - | Staff |
| `removesickness` | ABSENT | m | - | - | - | - | - |
| `repairitems` | ABSENT | - | - | - | 2 | 2 | - |
| `resetfactions` | ABSENT | - | - | 4 | - | - | - |
| `resetworld` | ABSENT | - | - | - | - | - | Staff |
| `resync` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `reviveplr` | ABSENT | r | - | - | - | - | - |
| `roles` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `roles list` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `root` | ABSENT | - | - | 2 | - | - | - |
| `rooted` | ABSENT | - | - | - | - | - | Staff |
| `say` | ABSENT | - | - | 2 | - | - | Staff |
| `select` | ABSENT | - | - | - | 3 | - | - |
| `select clear` | ABSENT | - | - | - | 3 | - | - |
| `select player` | ABSENT | - | - | - | 3 | - | - |
| `sendpacket` | ABSENT | - | - | - | - | - | Staff |
| `sendpacket bgerror` | ABSENT | - | - | - | - | - | Staff |
| `sendpacket spelllog` | ABSENT | - | - | - | - | - | Staff |
| `sendraw` | ABSENT | - | - | - | - | - | Staff |
| `servermessage` | ABSENT | - | - | 2 | - | - | - |
| `set` | ABSENT | - | - | - | - | - | Admin |
| `setaccess` | ABSENT | - | - | 4 | - | - | - |
| `setcharacterspeed` | ABSENT | - | - | 2 | - | - | - |
| `setinstance` | ABSENT | - | - | 4 | - | - | - |
| `setlevel` | ABSENT | - | - | 3 | - | - | - |
| `setrole` | ABSENT | - | - | - | - | - | Admin |
| `settings` | ABSENT | - | 1 | - | - | - | - |
| `settings announcer` | ABSENT | - | 1 | - | - | - | - |
| `showbankslotresult` | ABSENT | - | - | - | - | - | Staff |
| `showcastfail` | ABSENT | - | - | - | - | - | Staff |
| `showtaxi` | ABSENT | - | - | 3 | - | - | - |
| `skillmaster` | ABSENT | - | - | 3 | - | - | - |
| `spawndata` | ABSENT | - | - | 3 | - | - | - |
| `spawndo` | ABSENT | - | - | - | - | - | Staff |
| `spawnzone` | ABSENT | - | - | - | - | - | Admin |
| `spell_linked` | ABSENT | - | - | - | 3 | - | - |
| `spelladd` | ABSENT | - | - | - | - | - | Staff |
| `spellvisual` | ABSENT | - | - | - | - | - | Staff |
| `splinestartswim` | ABSENT | - | - | 2 | - | - | - |
| `splinestopswim` | ABSENT | - | - | 2 | - | - | - |
| `stable` | ABSENT | - | - | - | 3 | - | - |
| `start` | ABSENT | m | - | - | 0 | - | - |
| `stats` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `string` | ABSENT | - | 2 | - | - | - | - |
| `stunned` | ABSENT | - | - | - | - | - | Staff |
| `summonall` | ABSENT | - | - | - | - | - | Staff |
| `talents` | ABSENT | - | - | - | - | - | Staff |
| `talents reset` | ABSENT | - | - | - | - | - | Staff |
| `taxicheat` | ABSENT | - | - | - | 1 | - | - |
| `telespell` | ABSENT | - | - | - | - | - | Staff |
| `tile` | ABSENT | - | - | - | - | - | Staff |
| `tile load` | ABSENT | - | - | - | - | - | Staff |
| `togglecached` | ABSENT | - | - | - | - | - | n/a (auth server, no role check recorded) |
| `tostart` | ABSENT | - | - | 2 | - | - | - |
| `turn` | ABSENT | - | - | 3 | - | - | - |
| `unauragroup` | ABSENT | - | - | - | 3 | - | - |
| `unbindsight` | ABSENT | - | 3 | - | - | 3 | - |
| `unfreezeplayer` | ABSENT | - | - | - | 2 | - | - |
| `unparalyze` | ABSENT | b | - | - | - | - | - |
| `unpossess` | ABSENT | - | 2 | - | - | 3 | - |
| `unroot` | ABSENT | - | - | 2 | - | - | - |
| `unstuck` | ABSENT | - | 2 | - | - | 0 | - |
| `wannounce` | ABSENT | u | - | - | - | - | - |
| `waterwalk` | ABSENT | - | - | 2 | 2 | - | Staff |
| `world` | ABSENT | - | - | - | - | - | Staff |
| `world save` | ABSENT | - | - | - | - | - | Admin |
| `wpgps` | ABSENT | - | 3 | - | - | 1 | - |
| `yell` | ABSENT | - | - | - | - | - | Staff |

## 5. Level mismatches

Each core's extractor flagged commands where ArcaneCore's level looks inconsistent with that core's. The scales differ, so these are indicators, not defects. The band mappings are the extractors' own, not read from either source (UNVERIFIED as policy).

### ArcEmu (2)

ArcEmu levels are permission letters; only player (0) vs staff is comparable.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `gps` | 0 | 1 | src/world/Chat/Chat.cpp:741 |
| `server save` | s | 0 | src/world/Chat/Chat.cpp:578 |

### AzerothCore (35)

Their 0-3 vs our 0-6; expected band for their level: 1->1, 2->2-3, 3->4-6.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `ban` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:83 |
| `ban ip` | 2 (SEC_GAMEMASTER) | 6 | src/server/scripts/Commands/cs_ban.cpp:78 |
| `baninfo` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:84 |
| `baninfo account` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:68 |
| `baninfo character` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:69 |
| `banlist` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:85 |
| `banlist account` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:61 |
| `banlist character` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_ban.cpp:62 |
| `cast` | 2 (SEC_GAMEMASTER) | 5 | src/server/scripts/Commands/cs_cast.cpp:43 |
| `event start` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_event.cpp:40 |
| `event stop` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_event.cpp:41 |
| `gm` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_gm.cpp:53 |
| `gm chat` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_gm.cpp:42 |
| `go` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_go.cpp:60 |
| `go xyz` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_go.cpp:53 |
| `guild delete` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_guild.cpp:36 |
| `honor add` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_honor.cpp:37 |
| `instance` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_instance.cpp:51 |
| `instance listbinds` | 1 (SEC_MODERATOR) | 3 | src/server/scripts/Commands/cs_instance.cpp:41 |
| `instance stats` | 1 (SEC_MODERATOR) | 4 | src/server/scripts/Commands/cs_instance.cpp:43 |
| `learn` | 2 (SEC_GAMEMASTER) | 5 | src/server/scripts/Commands/cs_learn.cpp:59 |
| `lookup creature` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_lookup.cpp:50 |
| `lookup event` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_lookup.cpp:51 |
| `lookup faction` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_lookup.cpp:52 |
| `lookup item` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_lookup.cpp:53 |
| `lookup object` | 1 (SEC_MODERATOR) | 2 | src/server/scripts/Commands/cs_lookup.cpp:56 |
| `announce` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_message.cpp:44 |
| `notify` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_message.cpp:46 |
| `recall` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_misc.cpp:121 |
| `saveall` | 2 (SEC_GAMEMASTER) | 6 | src/server/scripts/Commands/cs_misc.cpp:123 |
| `showarea` | 2 (SEC_GAMEMASTER) | 1 | src/server/scripts/Commands/cs_misc.cpp:128 |
| `hidearea` | 3 (SEC_ADMINISTRATOR) | 1 | src/server/scripts/Commands/cs_misc.cpp:129 |
| `modify money` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_modify.cpp:54 |
| `modify honor` | 2 (SEC_GAMEMASTER) | 4 | src/server/scripts/Commands/cs_modify.cpp:61 |
| `server` | 3 (SEC_ADMINISTRATOR) | 0 | src/server/scripts/Commands/cs_server.cpp:101 |

### MangosSharp (14)

Their GameMaster 2, Developer 3, Admin 4 vs our 0-6; numeric comparison approximate.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `help` | 2 (GameMaster) | 0 | src/server/Mangos.World/Handlers/WS_Commands.cs:142 |
| `cast` | 3 (Developer) | 5 | src/server/Mangos.World/Handlers/WS_Commands.cs:299 |
| `save` | 3 (Developer) | 0 | src/server/Mangos.World/Handlers/WS_Commands.cs:337 |
| `additem` | 2 (GameMaster) | 3 | src/server/Mangos.World/Handlers/WS_Commands.cs:757 |
| `addmoney` | 2 (GameMaster) | 4 | src/server/Mangos.World/Handlers/WS_Commands.cs:858 |
| `learnSpell` | 3 (Developer) | 5 | src/server/Mangos.World/Handlers/WS_Commands.cs:918 |
| `setreputation` | 2 (GameMaster) | 4 | src/server/Mangos.World/Handlers/WS_Commands.cs:1003 |
| `revive` | 2 (GameMaster) | 3 | src/server/Mangos.World/Handlers/WS_Commands.cs:1195 |
| `gps` | 2 (GameMaster) | 1 | src/server/Mangos.World/Handlers/WS_Commands.cs:1371 |
| `unban` | 4 (Admin) | 6 | src/server/Mangos.World/Handlers/WS_Commands.cs:1618 |
| `banaccount` | 2 (GameMaster) | 3 | src/server/Mangos.World/Handlers/WS_Commands.cs:1584 |
| `bancharacter` | 2 (GameMaster) | 3 | src/server/Mangos.World/Handlers/WS_Commands.cs:1565 |
| `notifymessage` | 2 (GameMaster) | 4 | src/server/Mangos.World/Handlers/WS_Commands.cs:426 |
| `setweather` | 3 (Developer) | 6 | src/server/Mangos.World/Handlers/WS_Commands.cs:1698 |

### MaNGOS Zero (54)

Their 0-4 vs our 0-6; band 1->1, 2->2-3, 3->4-6, 4->6.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `event start` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:292 |
| `event stop` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:293 |
| `gm` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:306 |
| `gm (group entry)` | 0 (SEC_PLAYER) | 2 | src/game/WorldHandlers/Chat.cpp:787 |
| `honor add` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:359 |
| `honor addkill` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:360 |
| `go` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:321 |
| `go (group entry)` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:789 |
| `go xyz` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:320 |
| `guild delete` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:350 |
| `instance (group entry)` | 3 (SEC_ADMINISTRATOR) | 2 | src/game/WorldHandlers/Chat.cpp:793 |
| `instance listbinds` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:368 |
| `instance unbind` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:369 |
| `learn (group entry)` | 1 (SEC_MODERATOR) | 5 | src/game/WorldHandlers/Chat.cpp:794 |
| `lookup item` | 3 (SEC_ADMINISTRATOR) | 2 | src/game/WorldHandlers/Chat.cpp:424 |
| `lookup creature` | 3 (SEC_ADMINISTRATOR) | 2 | src/game/WorldHandlers/Chat.cpp:421 |
| `lookup object` | 3 (SEC_ADMINISTRATOR) | 2 | src/game/WorldHandlers/Chat.cpp:426 |
| `lookup tele` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:433 |
| `lookup faction` | 3 (SEC_ADMINISTRATOR) | 2 | src/game/WorldHandlers/Chat.cpp:423 |
| `modify (group entry)` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:797 |
| `modify money` | 1 (SEC_MODERATOR) | 4 | src/game/WorldHandlers/Chat.cpp:455 |
| `modify hp` | 1 (SEC_MODERATOR) | 3 | src/game/WorldHandlers/Chat.cpp:451 |
| `modify mana` | 1 (SEC_MODERATOR) | 3 | src/game/WorldHandlers/Chat.cpp:452 |
| `modify honor` | 1 (SEC_MODERATOR) | 4 | src/game/WorldHandlers/Chat.cpp:464 |
| `modify rep` | 2 (SEC_GAMEMASTER) | 4 | src/game/WorldHandlers/Chat.cpp:465 |
| `tele` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:733 |
| `tele (group entry)` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:806 |
| `tele name` | 1 (SEC_MODERATOR) | 2 | src/game/WorldHandlers/Chat.cpp:731 |
| `unaura` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:811 |
| `announce` | 1 (SEC_MODERATOR) | 4 | src/game/WorldHandlers/Chat.cpp:812 |
| `notify` | 1 (SEC_MODERATOR) | 4 | src/game/WorldHandlers/Chat.cpp:813 |
| `revive` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:822 |
| `cooldown` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:828 |
| `unlearn` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:829 |
| `saveall` | 1 (SEC_MODERATOR) | 6 | src/game/WorldHandlers/Chat.cpp:833 |
| `ban (group entry)` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:835 |
| `ban account` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:191 |
| `ban character` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:192 |
| `baninfo (group entry)` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:837 |
| `baninfo account` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:199 |
| `baninfo character` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:200 |
| `baninfo ip` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:201 |
| `banlist (group entry)` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:838 |
| `banlist account` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:207 |
| `banlist character` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:208 |
| `banlist ip` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:209 |
| `neargrave` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:842 |
| `explorecheat` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:843 |
| `levelup` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:844 |
| `showarea` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:845 |
| `hidearea` | 3 (SEC_ADMINISTRATOR) | 1 | src/game/WorldHandlers/Chat.cpp:846 |
| `additem` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:847 |
| `maxskill` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:852 |
| `setskill` | 3 (SEC_ADMINISTRATOR) | 3 | src/game/WorldHandlers/Chat.cpp:853 |

### TrinityCore (22)

Their 0-3 vs our 0-6; band TC0->0, TC1->1-2, TC2->2-3, TC3->3-6 (extractor mapping). Our level below the band = permissiveness gap, above = restrictiveness gap.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `baninfo account` | 3 | 1 | src/server/scripts/Commands/cs_ban.cpp:66 |
| `baninfo character` | 3 | 1 | src/server/scripts/Commands/cs_ban.cpp:67 |
| `banlist account` | 3 | 1 | src/server/scripts/Commands/cs_ban.cpp:60 |
| `banlist character` | 3 | 1 | src/server/scripts/Commands/cs_ban.cpp:61 |
| `event start` | 1 | 4 | src/server/scripts/Commands/cs_event.cpp:45 |
| `event stop` | 1 | 4 | src/server/scripts/Commands/cs_event.cpp:46 |
| `honor add` | 1 | 4 | src/server/scripts/Commands/cs_honor.cpp:44 |
| `instance listbinds` | 1 | 3 | src/server/scripts/Commands/cs_instance.cpp:50 |
| `instance unbind` | 1 | 3 | src/server/scripts/Commands/cs_instance.cpp:51 |
| `instance stats` | 1 | 4 | src/server/scripts/Commands/cs_instance.cpp:52 |
| `lookup creature` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:63 |
| `lookup event` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:64 |
| `lookup faction` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:65 |
| `lookup item` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:66 |
| `lookup object` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:69 |
| `lookup tele` | 3 | 2 | src/server/scripts/Commands/cs_lookup.cpp:77 |
| `announce` | 1 | 4 | src/server/scripts/Commands/cs_message.cpp:53 |
| `notify` | 1 | 4 | src/server/scripts/Commands/cs_message.cpp:55 |
| `gps` | 3 | 1 | src/server/scripts/Commands/cs_misc.cpp:94 |
| `hidearea` | 3 | 1 | src/server/scripts/Commands/cs_misc.cpp:97 |
| `saveall` | 1 | 6 | src/server/scripts/Commands/cs_misc.cpp:115 |
| `showarea` | 3 | 1 | src/server/scripts/Commands/cs_misc.cpp:118 |

### WCell (8)

WCell role enum (Player/Staff/Admin) vs our 0-6; flagged where Player vs >0, Admin vs <=2, Staff vs >=4.

| Command | Their level | Our level | Source (theirs) |
|---|---|---|---|
| `Ban` | Admin | [1, 3, 3, 6] | wcell:Services/WCell.RealmServer/Commands/AdminCommands.cs:140 |
| `Broadcast` | Staff | 4 | wcell:Services/WCell.RealmServer/Commands/AdminCommands.cs:58 |
| `Guild Disband` | Staff | 4 | wcell:Services/WCell.RealmServer/Commands/GuildCommands.cs:279 |
| `Where` | Player | 1 | wcell:Services/WCell.RealmServer/Commands/PlayerCommands.cs:214 |
| `Notify` | Staff | 4 | wcell:Services/WCell.RealmServer/Commands/PlayerCommands.cs:348 |
| `Spell Add` | Staff | 5 | wcell:Services/WCell.RealmServer/Commands/SpellCommands.cs:179 |
| `Event Start` | Staff | 4 | wcell:Services/WCell.RealmServer/Commands/WorldEventCommands.cs:18 |
| `Event End` | Staff | 4 | wcell:Services/WCell.RealmServer/Commands/WorldEventCommands.cs:60 |

## 6. Other notes

- AzerothCore `ourOnly` (35) and MaNGOS Zero `ourOnly` (27) list ArcaneCore commands the core lacks; TrinityCore lists 69 `ourCommandsNotInTheirJson` that are largely extraction gaps, not real absences.
- Rows with ArcaneCore present are determined by any core's `matched` list. If one core matched a command and another listed a differently named equivalent as missing, the second shows in the matrix as present for that core under its own name.
