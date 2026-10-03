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
| characters | **11** (`LootStateDataModule.Version`; the lead renumbers at merge) | `ArcaneCore.Data.Loot.LootStateDataModule` | `CreateTableChange` × 4: `loot_state`, `loot_state_item`, `loot_state_player`, `loot_operation` |

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
  - **Chest:** gated on the data8 quest, then the lock check, then loot from data1 (`gameobject_loot_template`). When the chest is emptied and released it despawns and respawns after max(1 s, spawntimesecs). In a dungeon instance its loot is durable, see below.
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
  - Skinning loot is exposed as a spell collaborator seam. Item container opening is refused until generated/consumed loot has durable storage; a locked item still answers ItemLocked.
  - Taking items and money. Money is split among group members in range, with SMSG_LOOT_MONEY_NOTIFY. A split is deferred while an eligible recipient has a pending quest settlement, without consuming or redistributing their share.
  - On release, a round robin owner's release opens the loot to everyone.
  - A late player may open a chest someone else left unfinished.
- **Packets:**
  - SMSG_GAMEOBJECT_QUERY_RESPONSE: name, 4 zero bytes, then the full 24 data words, as vmangos sends them. gtker's 1.12 definition has a shorter data array; the client accepts the vmangos form.
  - Unknown entries answer with `entry | 0x80000000`.
  - SMSG_LOOT_RESPONSE, SMSG_LOOT_RELEASE_RESPONSE, SMSG_LOOT_REMOVED, SMSG_LOOT_MONEY_NOTIFY, SMSG_LOOT_CLEAR_MONEY, SMSG_GAMEOBJECT_CUSTOM_ANIM, SMSG_GAMEOBJECT_PAGETEXT.

## Durable chest loot in dungeon instances (handoff item 4, slice A)

Chests of a dungeon instance (`Map.InstanceId != 0`) used to be refused (`Unsupported`): their
loot lived only in memory, and a map that unloads and is recreated while its logical save
(`InstanceSave`) lives on would reroll every award. They now keep generated, remaining and
consumed contents with the logical save. Shared-copy chests (instance 0) are unchanged and still
run fully in memory.

**Delivered (slice A).**
- A chest opens in an instance only when all of these hold: the world has an `ILootStateStore`, the
  map has a live (not deleted) `InstanceSave`, the spawn is a database spawn (a runtime chest from
  `Summon` has no spawn guid to key on), and the key has no operation in flight and was not blocked
  by an unreadable outcome. Otherwise the answer stays `Unsupported` (fail closed), as before.
  `CMSG_GAMEOBJ_USE` and the spell `OpenLock` path both go through this.
- **Generation** is committed before the window is shown. Use answers Ok ("accepted"); the loot
  window appears when the commit finished. A refused or lost commit sends
  SMSG_LOOT_RELEASE_RESPONSE; the group round-robin position (`Group.LooterGuid`) only moves when the
  generation committed.
- **Takes** (`CMSG_AUTOSTORE_LOOT_ITEM`) stage the award in a detached inventory
  (`PlayerInventory.TryStageQuestRewards`: real stacking rules, new item GUIDs), freeze the character with
  the shared settlement hold, save its pre-operation snapshot, and commit the chest state and the
  inventory in one serializable transaction. Only then is the inventory published, in the settlement
  publication window, followed by `ItemCountChanged`, SMSG_ITEM_PUSH_RESULT (a loot pickup),
  SMSG_LOOT_REMOVED and the quest journal's `ItemLooted`. A refused take changes nothing and answers
  LootCantLootThatNow. A durable take answers Ok when it is accepted; the removal arrives after the commit.
- **Recreation and restart.** `LoadGrid` leaves a consumed chest despawned until its stored respawn
  time (`respawn_at`, Unix seconds; `long.MaxValue` for a negative `spawntimesecs`) and
  otherwise spawns it. Contents are rebuilt lazily when the chest is opened, from the committed
  record, never for a key with an operation in flight. The record carries the generation, the round
  robin owner (`loot_owner`), the recipients and per stack the quest/per-player flags and who may take and
  who took it, so slot indexes, counts and display ids (recomputed from the item templates) are the same
  after the rebuild. A consumed chest regenerates (generation + 1) when it is opened after its respawn.
  A stored chest whose entry differs from the object, or whose item template is missing, stays refused.
- **Cleared only by a real reset or deletion.** `EfInstanceStore.DeleteInstanceAsync` deletes the
  chest rows of the instance in its transaction, and `InstanceManager.InstanceDeleted` drops the live
  cache. The safety against a reused instance id (ids restart above the highest stored one) is the
  startup purge in `EfLootStateStore.LoadInstanceStatesAsync` (rows of a missing instance go), which runs in
  `GameObjectLootFeature.Attach` before `InstanceManager.Load`; the co-delete is hygiene. A commit that
  finishes after its instance was deleted never re-enters the cache.
- **Gold.** Chests hold no gold (`mingold` is not imported), so the record has no money. Corpse group
  splits are unchanged: a split is deferred as a whole while any eligible sharer has a settlement
  pending, which now includes a recipient whose chest take is in flight; the original shares are
  paid after it ends.

**Design.**

| Layer | Piece |
|---|---|
| Kernel | `Kernel/Loot/LootStateContracts.cs` (`LootStateKey` = instance id + spawn guid, `LootStateRecord`, `LootAward`, `LootCommitRequest`, `ILootStateStore`) and `LootStateRules.cs`, the single implementation of how a stored chest may change |
| Data | `Data/Loot/LootStateDataModule.cs` (4 tables) and `EfLootStateStore` (`CommitAsync` mirrors `EfEconomyStore`: dedicated context, serializable transaction, ledger, expected-state comparison) |
| Game | `Game/Loot/LootStateCoordinator.cs` (`ILootStateCoordinator`, `LootOperation`, `LootActor`), `LootService.Durable`, `LootBag.ToRecord/FromRecord/ApplyRecord` |
| World | `World/GameObjects/LootSettlements.cs` (the runner) and `GameObjectLootFeature` (loads and purges the chests at attach, wires the instance system, is an `ICharacterSettlementBarrier`, stops the runner) |

**What the store accepts.** A commit is `Committed` only when the logical `instance` row exists
(`ScopeMissing` otherwise: a reset, or a lost queued `InstanceSaved` write), the stored chest equals
`Expected` as sets (`Conflict`; null = no state), `Updated` is exactly what `LootStateRules.Replay`
produces from `Expected` and the awards (a fresh generation + 1 only after a consumed chest;
otherwise the same generation with the awarded stacks taken, recipients only growing by characters
that take something, the round-robin owner only cleared, never set), every participant's money and
complete inventory equal its `Before` and its `After` differs by exactly the awards, and a newly granted
item GUID does not exist (`InvalidTransition` or `Conflict`). The game computes `Updated` from the
committed record, never from the live bag, so a bag rebuilt or changed while an operation was pending
cannot re-take a slot; the store would refuse it anyway. A retried operation id is `AlreadyCommitted`.

**Runner semantics** (`LootSettlements`, like `EconomySettlements`): the actor must be the current online
session, not teleporting, not already held/quarantined/in a settlement, not logging out. The worker saves
the pre-operation snapshot, quarantines the character, waits (bounded by the 5 s budget) for the instance
write queue to drain up to the operation's start (`InstanceWriteQueue.WaitForAsync(watermark)`; a stream of
later writes cannot starve it) and commits. An exception is reconciled with `IsCommittedAsync`: After or
Before. If that fails too the outcome is Unknown: the key stays blocked until restart and the actor is kicked
and stays quarantined (a fresh login reads the durable state). Shutdown finalizes without publishing.
Login and character deletion wait for an in-flight operation (`ICharacterSettlementBarrier`).

**Behaviour that differs from before, for durable chests only.** A take needs the same preconditions as an
economy operation (current session, not held), and answers LootCantLootThatNow otherwise; a take or open on a
key with an operation in flight is refused; a loot window or a take is one DB round trip later; a slow
database refuses takes (never loses them); PostgreSQL serialization failures (40001) surface as exceptions and
reconcile to Before.

**Limits (explicit).**
- **Slice B, item containers, is not delivered.** `CMSG_OPEN_ITEM` on a lootable item still answers
  LootCantLootThatNow (`LootService.OpenItem`, pinned by `LootServiceTests`). It needs the vmangos
  behaviour verified first (charter section 1.1/1.5: whether an item whose loot was generated may be traded,
  mailed, sold or destroyed, and whether the generated loot persists), which was not available in this
  session, plus a guid-precise container removal and a transfer guard in `PlayerInventory.CanTransferOut`.
- **Provenance of the semantics.** The handoff requirement (store generated, remaining and consumed contents
  atomically with the awards and tie them to the logical save) is what this implements. Whether vmangos
  itself persists the remaining contents of a partly looted instance chest (rather than only the per-instance
  respawn time of a looted one) was not re-checked against the vmangos sources here; the contents persistence
  is this port's choice, the respawn semantics follow `GameObject::Despawn`/`spawntimesecs` as the in-memory
  path already did.
- A character deleted after it was recorded keeps its id in chest rows (inert history, see
  [character-delete.md](character-delete.md)); on engines that reuse the highest character id a later
  character with that id would count as having been a recipient of those chests.
- Temporary and runtime chests in instances stay Unsupported. Chest gold is not generated or stored.
- A lost queued `InstanceSaved` write (three attempts) makes the chests of that instance refuse (`ScopeMissing`)
  until restart: visible, but safe.
- Not run in this session: MariaDB and PostgreSQL (the Data tests run on SQLite here, there is no server on this
  box; CI runs the other engines, including their serializable-commit behaviour). Not covered by any test: a real
  world database dump with chests (the end-to-end test seeds a minimal dungeon, trigger, chest and loot rows).

**Shared-file edits** (all additive): `EfInstanceStore.DeleteInstanceAsync` (+chest rows),
`InstanceManager.InstanceDeleted` event, `InstanceWriteQueue.Enqueued/WaitForAsync`,
`InstanceFeature.WriteWatermark/WaitForWritesAsync`, `GameObjectMapSystem` (`OpenChest`, `LoadGrid`, `UnloadGrid`,
`FindBySpawn`, `OnDurableRecordCommitted`), `LootService` (`Durable`, durable open/take/release paths,
`AssignOwner` split into `PickOwner`), `LootBag`, `LootResult.Unsupported`, `PlayerInventory.NotifyLootInventory`,
test hosts (`WorldTestHost.WorldServices`, `InMemoryInstanceStore.Live/Deleted/SaveDelay`).

## Known gaps (honest scope)

- **Persistence.**
  - Game object respawn times of shared-copy maps (instance 0) are not persisted across a restart. Chests of dungeon instances persist theirs (above).
  - Item container loot is not persisted (slice B above), so CMSG_OPEN_ITEM does not generate loot or consume containers.
  - The remainder of a money split (gold mod number of sharers) is dropped.
- **Group loot.**
  - There is no master loot, need/greed or group-loot roll UI. Those methods fall back to round robin.
  - Looter changes are not broadcast to the group.
  - There is no tap list: the killer decides the recipients.
- **Conditions.** `condition_id` rows are skipped: no conditions evaluator is wired into `LootService.Conditions` yet.
- **Spell side.**
  - No cast time, skill-ups or spell-driven opening (`OpenLock` is the hook for the spells area).
  - Fishing nodes, chairs, traps, rituals, spell casters, meeting stones, flag stands and transports are not usable.
- **Chest gold.** vmangos `gameobject_template` mingold/maxgold is not imported (so durable chest state has no money).
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
  - `GameObjectTests` (23, including partial-chest grid reload and unresolved lock checks)
  - `LootGeneratorTests` (7)
  - `LootServiceTests` (18, including settlement-held money and container refusal)
- `ArcaneCore.Data.Tests/GameObjectLootDataTests` (6 tests; 10 cases with all three engines):
  - schema step
  - round-trip and replace on SQLite, MariaDB and PostgreSQL
  - rollback, savepoint and non-empty tracker
  - vmangos patch filtering
  - Lock.dbc layout rejection
  - guid overflow
- Durable chest loot:
  - `ArcaneCore.Data.Tests/LootStateRulesTests` (10): the pure rules (availability, takes, replay, legal successors, award/inventory match).
  - `ArcaneCore.Data.Tests/LootStateStoreTests` (10 theories on the provider matrix): one-transaction commit and retry, stale expected, forged successors, award/inventory mismatch, missing scope, consumed then regenerated, startup purge, instance deletion, character deletion keeps the marks.
  - `ArcaneCore.Game.Tests/GameObjects/DurableChestTests` (19, with `FakeLootCoordinator`, a coordinator that follows the runner contract and refuses any illegal transition): generation and refusal, partial take across map recreation, consumed chest and respawn, pending take with a grid reload, deferred despawn, owner release and restore, quest and per-player stacks, unknown outcome, runtime chests.
  - `ArcaneCore.World.Tests/GameObjects/InstanceChestDurabilityTests` (11, real sockets, the real instance system and runner, `InMemoryLootStateStore`): opening after the commit, partial take across unload and recreation, consumed chest, real reset, held commit, lost acknowledgement, refused generation, unreadable outcome, group gold split deferral, deleted scope, startup load and purge over stored chests.
  - `ArcaneCore.MockClient.Tests/InstanceLootPersistenceTests` (1): real SRP/world sessions, the real SQLite stores and a real world restart. The Deadmines trigger, a partial take (state and inventory committed together, checked in the database), a normal world stop, a fresh daemon over the same files, login into the bound instance, and the exact remaining contents; then the last take consumes the chest (generation still 1).
- `ArcaneCore.World.Tests/GameObjects/GameObjectWorldTests` (2): over loopback, the query (known, unknown and short payload), the chest create block, then use → loot → autostore → item in the bags → release → despawn.
