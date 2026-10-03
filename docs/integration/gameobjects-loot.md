# Integration notes: game objects and loot (`feat/gameobjects-loot`)

Fleet round 2 area, based on `codex/integrate-feature-fleet-20261003` (0d32fba). Almost all wiring
is discovered (`IDataModule`, `IWorldFeature`, `IOpcodeHandlerGroup`, `IMapUpdater`,
`IWorldTestServices`), so the area does not touch `WorldServiceCollectionExtensions.cs`,
`Program.cs`, `WorldHost.cs`, the DbContexts, `Map.cs` or `WorldTestHost.cs`.

**Provenance.** Behaviour is re-implemented from reading the vmangos sources (GameObject.cpp,
`GameObject::Use`, LootMgr.cpp, LootHandler.cpp, QueryHandler.cpp, ItemHandler.cpp
`HandleOpenItemOpcode`) and cmangos-classic (the table layouts). Packet layouts were checked
against gtker/wow_messages. No GPL code was copied. Class and method names that mirror vmangos
are cited in doc comments so reviewers can compare.

## Schema version

| Component | Version | Owner | Step |
|---|---|---|---|
| world | **7** | `ArcaneCore.Data.World.GameObjects.GameObjectLootDataModule` | `CreateTableChange` × 11 |

The 11 tables:
- `gameobject_template`
- `gameobject_spawn`
- `gameobject_questrelation`
- `gameobject_involvedrelation`
- `lock_template` (Lock.dbc rows)
- `creature_loot_template`
- `gameobject_loot_template`
- `item_loot_template`
- `skinning_loot_template`
- `reference_loot_template`
- `creature_loot_info` (loot id, skinning loot id and min/max gold per creature entry)

Loot tables use the cmangos key (entry, item). The version is the `GameObjectLootDataModule.Version`
constant. If another world module claims 7 first, bump the constant and the
`IntegratedSchemaTests` row. Nothing else depends on the value.

## Shared-file edits (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Updates/IViewerFieldFilter.cs` (new seam) | `uint Filter(WorldObject obj, int index, uint value, Player viewer)` | Some fields depend on who is looking (vmangos `BuildValuesUpdate` special cases). |
| `src/ArcaneCore.Game/Entities/WorldObject.cs` (+17) | `ViewerFieldFilter` property, `GetValueFor(index, viewer)`, `ForceFieldUpdate(index)` (wraps `MarkChanged`) | Lets the filter be attached per object and lets a changed filter answer be pushed (vmangos `ForceValuesUpdateAtIndex`). If the property is null, behaviour is unchanged. |
| `src/ArcaneCore.Game/Updates/UpdateBlockWriter.cs` (+6/−5) | The create mask and the values write use `obj.GetValueFor(index, viewer)` | UNIT_DYNFLAG_LOOTABLE only for allowed looters. GAMEOBJECT_DYN_FLAGS Activate/Sparkle only for players on the quest. |
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | Adds the `GameObjectLootDataModule` row. World current version is now 7, steps 2–7. | Schema registry check. |

All other files are new:
- `src/ArcaneCore.Kernel/WorldData/{GameObjects,Loot}/`
- `src/ArcaneCore.Data/World/GameObjects/`
- `src/ArcaneCore.Game/{GameObjects,Loot}/`
- `src/ArcaneCore.World/GameObjects/`
- `tests/ArcaneCore.{Game,World}.Tests/GameObjects/`
- `tests/ArcaneCore.Data.Tests/GameObjectLootDataTests.cs`
- this file

## Discovered registrations

- **`GameObjectLootDataModule : IDataModule` (world).**
  - Maps the 11 tables.
  - Registers `IGameObjectDataStore` → `EfGameObjectDataStore` and `ILootDataStore` → `EfLootDataStore` (scoped).
- **`GameObjectLootDumpImporter`.**
  - Reads cmangos classic-db and vmangos world dumps by column name, the same way `CreatureDumpImporter` does.
  - vmangos rows are patch-filtered: the template with the highest patch ≤ 10, and rows whose patch range contains 10.
  - `ReadLocks` reads Lock.dbc 1.12.1 (33 fields; other layouts are rejected).
  - `WriteAsync(db, replace)` is atomic, with a savepoint when the caller has a transaction.
  - It returns a `GameObjectLootImportReport`. Spawns with guids above 24 bits are skipped with a warning.
  - Like the creature importer, it has no CLI entry point yet.
- **`GameObjectLootFeature : IWorldFeature`** (config section `Loot`, bound to `LootOptions`).
  - `Attach` loads both stores synchronously. It fails closed: a store exception stops startup.
  - It builds the world's `LootService`, then posts `Install`, which attaches a `GameObjectMapSystem` (an `IMapUpdater`) to every map, now and on `MapCreated`.
  - It hooks `Combat.UnitKilled` to create corpse loot, and `PlayerLoggingOut` to `LootService.OnPlayerLeft`.
  - Collaborators are optional and resolved from DI:
    - item templates from `ItemsFeature`, read lazily
    - groups from `SocialFeature`
    - quest checks from `QuestNpcFeature` through `QuestJournalAdapter`
    - corpse timings from `CreatureWorldFeature.Options`
- **`GameObjectLootHandlers : IOpcodeHandlerGroup`.**
  - CMSG_GAMEOBJECT_QUERY runs on the session task (content is immutable).
  - These run on the world thread: CMSG_GAMEOBJ_USE, CMSG_LOOT, CMSG_AUTOSTORE_LOOT_ITEM, CMSG_LOOT_MONEY, CMSG_LOOT_RELEASE, CMSG_OPEN_ITEM.
  - Short payloads are ignored.
- **Tests.** `GameObjectTestServices : IWorldTestServices` registers async-local stores (empty unless a test sets content) and a probe feature.

## What works

- **Game objects.**
  - They load and unload with grids.
  - A grid unload keeps the pending respawn time.
  - A negative `spawntimesecs` starts the object despawned.
  - The rotation quaternion is derived from the orientation when the stored rotation is 0.
  - The create block uses update flags All|HasPosition.
- **Using objects, by type:**
  - **Door / button:** toggles state, reports in-use while active, and auto-closes after data2 ms.
  - **Chest:** gated on the data8 quest, then the lock check, then loot from data1 (`gameobject_loot_template`). When the chest is emptied and released it despawns and respawns after max(1 s, spawntimesecs).
  - **Goober:**
    - lock check, cooldown from data6, quest gate from data1
    - quest credit through `GameObjectUsed`
    - page text (data7) and custom animation broadcast (data4)
    - consumable despawn (data5), auto-close (data3)
  - **Text:** shows page text.
  - **Quest giver:** goes through the `IGameObjectQuestGiver` seam.
  - **Mailbox:** use returns OK.
  - Any other type returns NotUsable or Unsupported.
- **Locks** (Lock.dbc):
  - Item keys are checked in the bags.
  - Skill locks are checked against the lockpicking, herbalism, mining and fishing skill values (`OpenLock`). A direct use of a profession lock answers Locked.
- **Quest object flags.** Per viewer, Activate|Sparkle is set on objects that drop or start something for an incomplete quest.
- **Loot generation** (vmangos LootMgr semantics):
  - Ungrouped rows roll independently.
  - Each group yields at most one item, explicit-chance rows first, then equal-chance rows.
  - References repeat `maxcount` times, with a depth guard of 8.
  - The count is a random value in min..max.
  - Quest items are only generated for players who need them.
  - The party-loot flag (0x800) gives each recipient a copy.
  - Unknown items are skipped.
  - Rows with a condition are skipped unless `LootService.Conditions` (a `Func<Player, uint, bool>` seam) accepts them for a recipient. Nothing sets it yet.
- **Corpse loot.**
  - The killer (or their group in range) are the recipients.
  - Creatures drop gold from `creature_loot_info`.
  - Round robin advances `Group.LooterGuid`.
  - UNIT_DYNFLAG_LOOTABLE is shown only to allowed looters.
  - When a corpse is looted out it becomes skinnable if it has skinning loot. Otherwise its decay is shortened by `LootedCorpseDecayRate`.
- **Other loot sources and the loot window.**
  - Skinning loot. Lootable items (ITEM_FLAG_LOOTABLE 0x4). A locked item answers ItemLocked.
  - Taking items and money. Money is split among group members in range, with SMSG_LOOT_MONEY_NOTIFY.
  - On release, a round robin owner's release opens the loot to everyone.
  - A late player may open a chest someone else left unfinished.
- **Packets:**
  - SMSG_GAMEOBJECT_QUERY_RESPONSE: name, 4 zero bytes, then the full 24 data words, as vmangos sends them. gtker's 1.12 definition has a shorter data array; the client accepts the vmangos form.
  - Unknown entries answer with `entry | 0x80000000`.
  - SMSG_LOOT_RESPONSE, SMSG_LOOT_RELEASE_RESPONSE, SMSG_LOOT_REMOVED, SMSG_LOOT_MONEY_NOTIFY, SMSG_LOOT_CLEAR_MONEY, SMSG_GAMEOBJECT_CUSTOM_ANIM, SMSG_GAMEOBJECT_PAGETEXT.

## Known gaps (honest scope)

- **Persistence.**
  - Game object respawn times are not persisted across a restart.
  - Item container loot is not persisted.
  - The remainder of a money split (gold mod number of sharers) is dropped.
- **Group loot.**
  - There is no master loot, need/greed or group-loot roll UI. Those methods fall back to round robin.
  - Looter changes are not broadcast to the group.
  - There is no tap list: the killer decides the recipients.
- **Conditions.** `condition_id` rows are skipped: no conditions evaluator is wired into `LootService.Conditions` yet.
- **Spell side.**
  - No cast time, skill-ups or spell-driven opening (`OpenLock` is the hook for the spells area).
  - Fishing nodes, chairs, traps, rituals, spell casters, meeting stones, flag stands and transports are not usable.
- **Chest gold.** vmangos `gameobject_template` mingold/maxgold is not imported.
- **Quest givers.** The quest-giver object type only calls the seam; the quest area must implement `IGameObjectQuestGiver`.
- **Polling.**
  - Quest flags are re-checked every 1 s by polling, not on quest events.
  - `PlayerInventory.ItemCountChanged` is not used; loot reports `ItemLooted` to the quest journal itself.
- **Tooling.** There is no GM `.gobject` command group yet, and no CLI importer wiring.

## Suggested next slice

1. GM `.gobject add|delete|respawn` commands, and persisted respawn times (a characters/world-state table).
2. A conditions evaluator shared with gossip and quests, then enable conditioned loot rows.
3. Group loot rolls (SMSG_LOOT_START_ROLL / ROLL / ROLL_WON, need/greed/pass) and master loot. Add a tap list on creatures.
4. Spell-driven opening (lockpicking, herbalism, mining, with cast time and skill-ups), fishing bobbers and traps, once the spells area exposes effect hooks.

## Tests

- `ArcaneCore.Game.Tests/GameObjects`:
  - `GameObjectTests` (21)
  - `LootGeneratorTests` (7)
  - `LootServiceTests` (17)
- `ArcaneCore.Data.Tests/GameObjectLootDataTests` (6 tests; 10 cases with all three engines):
  - schema step
  - round-trip and replace on SQLite, MariaDB and PostgreSQL
  - rollback, savepoint and non-empty tracker
  - vmangos patch filtering
  - Lock.dbc layout rejection
  - guid overflow
- `ArcaneCore.World.Tests/GameObjects/GameObjectWorldTests` (2): over loopback, the query (known, unknown and short payload), the chest create block, then use → loot → autostore → item in the bags → release → despawn.
