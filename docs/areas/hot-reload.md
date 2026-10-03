# Area: Live reload (`.reload`)

Branch `claude/vw-hot-reload`. Operator-initiated reload of configuration and content without a
restart, modelled on the vmangos `.reload` command tree. Nothing reloads by itself.

References (read-only, never copied): **vmangos** `src/game/Chat/Chat.cpp` (reload table 794-935,
root 1212), `src/game/Commands/ServerCommands.cpp` (config 1016, spell_template 1409-1417,
`reload all` 885-905), `src/game/World.cpp` (`LoadConfigSettings(reload)` 445-1100, `configNoReload`
3044-3055), `src/game/Spells/SpellMgr.cpp` (`LoadSpells` 3702-3750); **mangos-classic**
`src/game/Chat/Chat.cpp:942` (single administrator `reload` root).

## Delivered

**Pipeline** (`src/ArcaneCore.World/Reload/ReloadCoordinator.cs`, `src/ArcaneCore.Game/Reload`)
- `IContentReloadable` builds a `ContentCandidate` on a worker thread (read, build, never touching live
  state) and validates it there. Any exception, timeout (`HotReload:BuildTimeoutMs`) or validation
  problem ends the reload with live state untouched.
- The swap is posted to the world thread with `WorldRuntime.Post`, which `RunCommands` drains before any
  map updates (`WorldRuntime.cs` `RunTick`), so no map ever sees half a swap. It runs inside a
  `ReloadTransaction` (steps with paired undo actions): if a step throws, the earlier steps are undone
  and the result is `Failed ... rolled back`.
- A swap the world thread never picks up within `HotReload:CommitTimeoutMs` is cancelled for good; it
  cannot run later.
- One reload at a time (`Busy` otherwise). `ReloadResult` / `ReloadStatus` (Kernel) carry the outcome:
  `Applied`, `KeptCurrent`, `Rejected`, `Failed`, `Busy`.
- Reloadables are discovered (`IContentReloadable` in the World assembly) and registered by
  `ReloadFeature`; a new one is a new class, no registry edit.

**Switch**: `HotReload:Commands` is the single switch and defaults to **off** (`HotReloadOptions`,
shipped `appsettings.json`). Off: `ReloadFeature` builds no coordinator and registers no reloadable, and
the `.reload` root is not in the command table (`ICommandGroup.IsEnabled`), so `.reload` answers "There is
no such command." exactly like any unknown command. On: everything below. A development server enables it
with `HotReload:Commands=true` (the dev runner script sets it). Read once when the world starts.

**Commands** (`ReloadCommands`, Administrator, only when `HotReload:Commands=true`)
- `.reload config`, `.reload spell_template`, `.reload all` (the reloadables vmangos `reload all` reaches, ServerCommands.cpp:885-905:
  `areatrigger_teleport` :907-914, `game_tele` :900, `spell_template` :969-971; not the config, and not
  `item_template` or `creature_template`, which vmangos' `all_item` :996-1002 and `all_npc` :925-933 leave out
  and which stay reachable by name, Chat.cpp:830, 855), `.reload status` (ArcaneCore addition), names matched exactly or by unique
  prefix (vmangos matches command words by abbreviation, `hasStringAbbr`).
- The command returns at once and reports from the world thread when the reload ends, so the tick is
  never blocked by a database read.

**`spell_template`** (`SpellContentReloadable`): `ISpellContentStore.LoadAsync` ->
`SpellStoreFactory.Build` -> swap `SpellSystem.Store`. The setter is now backed by a volatile field
(`SpellSystem.cs`); the store is immutable, so a reader sees the old table or the new one.
- Empty `spell_template` keeps the loaded store (vmangos `SpellMgr.cpp:3724-3732` returns before the
  first `LoadSpell`; the safe variant the critique chose over `item_template`, which clears first,
  `ObjectMgr.cpp:3814-3832`).
- Duplicate spell ids are rejected with the ids listed (the store is keyed by id).

**`item_template`** (`ItemContentReloadable`, `LiveItemTemplateStore`): `IItemTemplateSource` ->
`ItemTemplateStore` (templates plus `playercreateinfo_item`) off the world thread, then
`ItemsFeature.ReplaceTemplates`. `ItemsFeature.Templates` and the value `EnsureLoadedAsync` returns are now a
stable `LiveItemTemplateStore` that forwards to the current immutable store, so every holder of the
reference (each online `PlayerInventory.Templates`, the economy, vendors, loot) sees the reload on its
next lookup by entry, without re-wiring. The first reload on a feature nobody has used yet performs the
initial load first, so the item GUID allocator is still seeded.
- Empty `item_template` empties the content, as vmangos does (`HotReload:EmptyTables`, default `Retail`; `KeepLoaded` opts into the safe variant). vmangos clears the map first and only then notices the
  empty result (`ObjectMgr.cpp:3817`, `3822-3830`), leaving no items; the loaders for spells
  (`SpellMgr.cpp:3724-3732`) and creatures (`ObjectMgr.cpp:1192-1196`) return before touching anything,
  and that is the behaviour taken.
- Duplicate entries are rejected with the entries listed.

**`creature_template`** (`CreatureContentReloadable`, `CreatureContent.SwapDefinitions`): the creature
content is read off the world thread and its definitions (templates, model infos, addons, waypoints,
EventAI) replace the live ones inside the `CreatureContent` object that the feature, every map system and
every creature already hold, like vmangos' `LoadCreatureTemplates` overwriting the `CreatureInfo` table
live creatures point into (`ObjectMgr.cpp:1190`; command `ServerCommands.cpp:1758-1773`, `Chat.cpp:830`).
`Creature.Template` is now looked up through the content (a version counter keeps it cheap), so a running
creature sees the new template at once and takes its unit fields from it at its next respawn
(`InitializeFields` already re-runs at respawn); queries and grids loaded later use it too.
- Empty table keeps the loaded definitions (`ObjectMgr.cpp:1192-1196` returns early).
- Spawns (`creature`) are not reloaded; the reload reports spawns whose template disappeared (they keep
  their last known template). A world that started with no creature data takes the whole content,
  spawns included, through `CreatureWorldFeature.Install`; the shared `CreatureContent.Empty` is never
  mutated.
- The vmangos `<entry>` argument (one template) is not supported; the whole table reloads.

**`game_tele`, `areatrigger_teleport`** (`MapContentReloadables`, `WorldMaps.ReplaceGameTeles` /
`BuildAreaTriggerTables` / `ReplaceAreaTriggerTables`): the `.tele` locations, and the area triggers with
their teleports, are read via `IMapDataStore` off the world thread and swapped on it (vmangos
`HandleReloadGameTeleCommand` `ServerCommands.cpp:1638` -> `ObjectMgr::LoadGameTele` `ObjectMgr.cpp:10466`;
`HandleReloadAreaTriggerTeleportCommand` `ServerCommands.cpp:1032` -> `LoadAreaTriggerTeleports`
`ObjectMgr.cpp:7706`; table entries `Chat.cpp:836`, `:813`). The teleport rows go through the same
loader rules as at startup (a trigger row, a known target map, a non-zero position); rejected rows are
listed in the result. `WorldMaps.Load` now builds the trigger tables through the same function.
- vmangos clears both tables before looking at the query result (`ObjectMgr.cpp:10468`, `7708`), so an empty
  table empties them; here an empty (or, for triggers, unusable) table empties them too (`HotReload:EmptyTables`, default `Retail`; `KeepLoaded` keeps the loaded rows).
- The map registry, area table, terrain and collision data are not reloaded: they are read when the
  daemon starts (`DataDir` is restart-only in vmangos too, `World.cpp:932-935`).

**`config`** (`ConfigContentReloadable`, `WorldConfigKeys`): the configuration source list is rebuilt
into a throwaway configuration (the live root is never reloaded: a broken file would empty it), the
`World` section is bound to a candidate, and the live `WorldRuntimeOptions` object is updated in place
on the world thread, so every reader sees the new value on its next read.
- Live: `UpdateCompressionThreshold`, `AutosaveIntervalMs`, `CharactersPerRealm`, `Motd`,
  `ListenRange*`, `AllowTwoSideChat`, `AllowTwoSideWhoList`, `LogoutDelayMs`, `InstantLogoutSecurity`,
  `GmLevelInWhoList`, `PlayerCommands`, `Maps:GridUnload`, `Maps:GridCleanUpDelayMs`,
  `Maps:GridActivationDistance` (`GridContainer` reads the shared `MapOptions` at each use; grids
  already running keep their timer until it resets, like `MapManager::SetGridCleanUpDelay`).
- Live, social rules (`World:Social:*`, a configuration surface that did not exist before: `SocialOptions`
  was never bound from configuration; `SocialFeature` now binds it at attach and the reload keeps it
  current). Keys map one to one to vmangos: `AllowTwoSideGroup` = `AllowTwoSide.Interaction.Group`
  (World.cpp:612), `AllowTwoSideGuild` = `...Interaction.Guild` (:613), `AllowTwoSideChannel` =
  `...Interaction.Channel` (:611), `AllowTwoSideAddFriend` = `AllowTwoSide.AddFriend` (:618). All default
  to false, as vmangos. `AllowTwoSideChat` and `AllowTwoSideWhoList` stay under `World:` where they
  already lived (`...Interaction.Chat` :610, `...WhoList` :617). vmangos' other `AllowTwoSide.*` keys
  (Accounts, Trade, Auction, Mail) have no ArcaneCore option and are not added here.
- Restart-only, reported as `<key> option can't be changed at reload, using current value (<v>).`
  (vmangos `configNoReload`, World.cpp:3044-3055): `TickIntervalMs`, `Maps:DataDirectory`, `Port`,
  `BindAddress` (the last two only when `IOptions<WorldOptions>` is registered, as in the daemon).
- A key removed from the file returns to its default (vmangos `GetIntDefault`).
- An unreadable or missing source or a value of the wrong type rejects the whole reload. A negative
  interval/range is replaced by the option default and noted, as vmangos `setConfigPos`/`setConfigMin`
  do (World.cpp:2949-2977); `HotReload:NegativeNumbers = Reject` rejects the whole reload instead. A test fails if a new `WorldRuntimeOptions` / `MapOptions` / `WorldOptions` property is
  not classified in `WorldConfigKeys`.

## Reload table audit (vmangos `Chat.cpp:794-910` against ArcaneCore)

Every entry of the vmangos reload table, what ArcaneCore does with it, and whether `.reload all` reaches it
(`ServerCommands.cpp:885-1002`). `ReloadAllMembershipTests` pins the "in all" answer for every registered
reloadable, so a new reloadable that forgets to say whether `all` includes it fails the suite. Status words:
**delivered** (a reloadable exists), **planned** (this lane, a later slice), **owner lane** (the store belongs to
another wave-4 lane, which adds a reloadable when its store is swappable), **no store** (ArcaneCore reads no such
table or catalog from the database).

| vmangos name (Chat.cpp line) | In vmangos `all` | ArcaneCore | Status |
|---|---|---|---|
| `all` (796), `all_area` (797) | itself; all_area = areatrigger_teleport, areatrigger_tavern, game_graveyard_zone (:910-912) | `.reload all` | delivered |
| `areatrigger_teleport` (813) | yes, via all_area (:910) | `areatrigger_teleport` | delivered |
| `areatrigger_tavern` (812) | yes, via all_area (:911) | none | no store |
| `game_graveyard_zone` (835) | yes, via all_area (:912) | none | owner lane (graveyards-resurrection) |
| `areatrigger_involvedrelation` (811) | yes, via all_quest (:937) | none: the quest area triggers are the configuration key `Quests:AreaTriggerQuests`, bound when the world starts | no store (restart) |
| `config` (808) | no | `config` | delivered |
| `game_tele` (836) | yes (:902) | `game_tele` | delivered |
| `spell_template` (905) | yes, via all_spell (:971) | `spell_template` | delivered |
| `item_template` (855) | no | `item_template` | delivered |
| `creature_template` (830) | no (all_npc :925-933 omits it) | `creature_template` | delivered |
| `creature_ai_events` (820) | yes (:890) | part of the creature content, rides on `creature_template` (so not in `all`) | delivered, see Limits |
| `quest_template` (884) and the four `*_questrelation` / `*_involvedrelation` tables (824, 827, 840, 842) | yes, via all_quest (:938-942) | none yet | planned |
| `npc_gossip` (867), `npc_trainer` (869), `npc_vendor` (870), `points_of_interest` (880) | yes, via all_npc (:928-931) | none yet | planned |
| `gossip_menu` (847), `gossip_menu_option` (848) | yes, via all_gossips (:987-988) | none yet | planned |
| `npc_text` (868) | no | none yet | planned |
| `creature_loot_template` (825), `gameobject_loot_template` (841), `item_loot_template` (853), `skinning_loot_template` (890), `reference_loot_template` (885), `fishing_loot_template` (834), `pickpocketing_loot_template` (874), `disenchant_loot_template` (831) | yes, all at once via all_loot (:891, :916-922) | none yet | planned |
| `mail_loot_template` (863) | yes, via all_loot | none | no store |
| `skill_fishing_base_level` (889) | yes (:887) | none yet; the base levels live in the loot content object | planned |
| `item_enchantment_template` (852), `page_text` (871), `item_required_target` (854) | yes, via all_item (:996-1002) | none | no store |
| `gameobject_template` (845) | no | none yet | planned |
| `gameobject` (838), `gameobject_requirement` (843) | no | none | owner lane (gameobject-types) |
| `command` (817), `reserved_name` (888), `mangos_string` (864) | yes (:899-901) | none | no store |
| `creature` (819), `creature_groups` (823), `creature_display_info_addon` (822), `cinematic_waypoints` (816) | no | none | owner lane (creature-movement-spawns) |
| `creature_spells` (828) | no | none | no store |
| `creature_onkill_reputation` (826), `reputation_reward_rate` (886), `reputation_spillover_template` (887) | no | none | owner lane (reputation-factions) |
| `spell_area`, `spell_chain`, `spell_elixir`, `spell_learn_spell`, `spell_proc_event`, `spell_proc_item_enchant`, `spell_script_target`, `spell_target_position`, `spell_threats`, `spell_pet_auras` (891-906) | yes, via all_spell (:972-981) | none | owner lane (spell-modifier-engine, aura-engine-completeness, threat-and-aggro) |
| `spell_mod` (898), `spell_group` (895), `spell_group_stack_rules` (896), `spell_disabled` (893) | no | none | owner lane (spell-modifier-engine, aura-engine-completeness) |
| `*_scripts` (829, 832, 844, 846, 849, 881, 883, 903), `all_scripts` (804) | not in `all` (:946-967) | none | no store (DB scripts are not interpreted) |
| `game_weather` (837) | no | none | owner lane (game-events-weather) |
| `creature_battleground`, `gameobject_battleground` (821, 839) | yes (:903) | none | owner lane (battlegrounds) |
| `locales_*` (856-862), `all_locales` (800) | yes (:897) | none | no store (only the default strings are read) |
| `conditions` (818) | no | none | owner lane (condition evaluator content) |
| `exploration_basexp` (833), `instance_buff_removal` (850), `map_template` (866), `map_loot_disabled` (865), `taxi_path_transitions` (907), `quest_greeting` (882), `trainer_greeting` (908), `pet_name_generation` (872), `variables` (909), `player_factionchange_*` (875-879), `petitions` (873), `character_pet` (815), `autobroadcast` (814), `anticheat` (807), `account_banned` (810), `ip_banned` (851) | no | none | no store, or another area's live store (bans are enforced live from the database, not from a cached list) |

## Deviations from retail

Every row with a switch defaults to retail. Rows marked *structural* are behaviours the design cannot offer a
retail variant of (no switch is possible); they are limits, not options.

| Deviation | Switch |
|---|---|
| Reload builds off the world thread and swaps between ticks; vmangos reloads on the update thread and blocks it. Not observable to a client. | none (behaviour only) |
| A running aura keeps the `SpellInfo` it started with; vmangos mutates its spell table in place. Not observable to a client. | none |
| vmangos splits `reload` (`SEC_DEVELOPER`, Chat.cpp:1212) from `config`/`all` (`SEC_ADMINISTRATOR`, Chat.cpp:796,808); ArcaneCore's account scale ends at Administrator (no `SEC_DEVELOPER`) so every `.reload` is Administrator, as cmangos-classic Chat.cpp:942. | none, structural |
| `.reload status` and prefix matching of reloadable names are ArcaneCore additions. | none |
| `TickIntervalMs` is restart-only here; vmangos `MapUpdateInterval` is live (World.cpp:592-594). ArcaneCore's world thread captures its sleep once and `SpellFeature` its timer. | none, structural |
| An `Item` a player already holds keeps the `ItemTemplate` it was created with until that character logs in again; vmangos resolves `Item::GetProto` by entry on every call (`Item.cpp:567-570`), so a changed sell price applies to held items at once there. True parity needs `Item.Template` late-bound, which edits `Item.cs` and `PlayerInventory.*` that the economy, loot and quest-reward work also changes. Left to a later slice. Lookups by entry (vendor lists, new items, starting outfit) are live. | none (unobservable difference is limited to held items) |
| A live creature keeps the unit fields it copied from its template (faction, flags, health, speeds ...) until it respawns; what is read through `Creature.Template` (rank, corpse delay, AI name, speeds, loot ids ...) changes at once. vmangos behaves the same way: the reload replaces the table, `Creature::UpdateEntry` re-copies at respawn. | none |
| Retail behaviour, switchable: an empty `item_template`, `game_tele` or unusable `areatrigger_teleport` empties the table (vmangos clears first); `spell_template` and `creature_template` keep what is loaded (vmangos returns early). | `HotReload:EmptyTables` (`Retail` default; `KeepLoaded` keeps the loaded rows for the three clearing tables) |
| A negative interval/range in `.reload config` is replaced by its default and noted (vmangos `setConfigPos`/`setConfigMin`). | `HotReload:NegativeNumbers` (`Retail` default; `Reject` rejects the reload) |
| `.reload spell_template` does not reload `spell_mod` (no such table here; vmangos ServerCommands.cpp:1414). | none |
| Live reload is off by default and is a development facility: vmangos always registers its `reload` root (Chat.cpp:1212), here the root and the coordinator do not exist unless enabled. Per the standing rule that hot-reload commands are off by default. | `HotReload:Commands` (default false; `true` for a development server) |

## Limits (not delivered, by design of this slice)

- Only `config`, `spell_template`, `item_template` and `creature_template` are reloadable. Quests, NPC data, gameobject
  loot, catalogs, the map registry/areas/terrain and collision are separate slices (hr-quest-npc, ...), several
  gated behind other lanes' work on the files they touch.
- No file watcher, no database revision polling, no automatic apply, no assembly (code) reload and no
  cluster rollout. The `HotReload` section itself is read when the world starts.
- Reloaded spells are visible to new casts, lookups and logins. Spells already in flight keep their
  record (see above). Clients cache spell data locally (WDB), so a client sees changed tooltips only
  after clearing its cache; the automated tests cover the server swap only, not a real client.
- The live host configuration (`reloadOnChange`) is untouched. A truncated `appsettings.json` written
  while the host watches it is handled by the host's own file provider (not by this feature); `.reload
  config` re-reads from the source list on demand and ignores a broken file.

## Tests

`tests/ArcaneCore.World.Tests/Reload` (coordinator, config, social config, spell, item, creature, map, command end-to-end over loopback) and
`tests/ArcaneCore.Game.Tests/Reload` (transaction, creature definitions). The three safety behaviours were proven load-bearing
by disabling each guard and watching its test fail: the empty-table guard, the restart-only message, and
the unreadable-source rejection. The same was done for the social startup binding and for the live item
store (without it the online inventory test and the held-item test fail) and for the late-bound
`Creature.Template`.
