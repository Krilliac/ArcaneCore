# Character deletion cleanup (Grok Bot fleet round 2, lead slice)

Branch `feat/character-delete-cleanup`, based on canonical integration head
`0d32fba1070a0be08932a85551a4c1b4ca191cfc`. No schema version is used.

## Problem

`CMSG_CHAR_DELETE` removed only the `characters` row, its action bar and its items. Spellbook,
quest log/rewarded history, taxi mask, friend/ignore rows (its own and those pointing at it) and
guild membership stayed in the characters database. Live state stayed too: the spellbook cache,
online players' friend/ignore lists, the guild roster, offline group membership, retained quest
snapshots and save-queue holds/quarantine/failed snapshots. A character created later with a
reused id (possible on engines that reuse the highest row id) could inherit any of it, and a
retained failed quest/core snapshot could be written back after the deletion.

## Seams

| Layer | Seam | Discovery | Contract |
|---|---|---|---|
| Characters database | `ICharacterDataCleanup.DeleteCharacterDataAsync(CharacterDbContext, int, CancellationToken)` on the module's `IDataModule` | `CharacterDataCleanups.All` = characters modules implementing it | Runs inside one deletion transaction before the `characters` row goes. Stage tracked removals or set-based deletes; the store saves and commits. Throw `CharacterDeletionRefusedException` to refuse (rolls back, answers false); any other exception rolls back and propagates. |
| World daemon | `ICharacterDeleteHook` on an `IWorldFeature` | added to `WorldFeatures.SeamInterfaces` | `CanDeleteCharacterAsync` (refuse), `OnCharacterDeletingAsync` (drain this character's queued writes; a throw refuses), `OnCharacterDeletedAsync` (after commit and directory removal: drop live state; a throw is logged and the rest still run). Session task; world state through `WorldRuntime.InvokeAsync`. |

**Every characters module must implement `ICharacterDataCleanup`.**
`CharacterDeletionTests.EveryCharactersModule_DeclaresHowItsRowsAreDeleted` fails otherwise, so a
later module (reputation v7, spell cooldown/aura v8, instance binds v9, mail/AH v10 …) has to say
how its rows are deleted. Rows of other characters that point at the deleted one are neutralized
in the same method (as social does for `character_social.OtherId`). Mail should return or
delete letters, and auctions should be cancelled, in that module's own cleanup.

`LootStateDataModule` (durable chest loot of dungeon instances) deliberately **keeps** the rows that name the
character (`loot_state_player`, `loot_state.loot_owner`): an empty recipient list means "anyone may loot", so
deleting the only recipient would open the chest to everyone and could un-loot per-player stacks. The marks stay
as inert history, and the live loot cache is never keyed by character, so nothing in it goes stale. A pending loot
operation of the character is drained first (`GameObjectLootFeature` is an `ICharacterSettlementBarrier`,
awaited by the delete flow). Limit: where an engine reuses the highest character id, a later character with that
id would appear in those marks. If that must be removed, replace the id with a reserved tombstone id instead of deleting.

## Order (vmangos `WorldSession::HandleCharDeleteOpcode` → `Player::DeleteFromDB`)

1. Refuse: invalid GUID, character not owned by the account, character in the world.
2. Each hook's `CanDeleteCharacterAsync`. Social refuses a guild leader (vmangos
   `GetGuildByLeader` → `CHAR_DELETE_FAILED`), using the live roster after the guild preload.
3. `CharacterSaveQueue.FlushCharacterAsync`, then each hook's `OnCharacterDeletingAsync`:
   spells flush the spellbook write queue; quests wait for a settlement in flight and flush the
   quest/taxi queue (a retained failure is retried; if that fails the deletion is refused).
4. Re-check the character is not in the world, then `EfCharacterStore.DeleteAsync` in one
   transaction: every module cleanup (items, spellbook, quests/taxi, social with leader
   refusal), then action bar and `characters` row. Refusal or failure leaves every row.
5. `CharacterSaveQueue.ForgetCharacter` (holds, quarantine and the retained failed snapshot
   go), then each hook's `OnCharacterDeletedAsync`, then `CharacterDirectory.Remove`:
   - social (`SocialCharacterDeleteHook`), in one world-thread step: the character leaves its
     guild through the existing `.guild uninvite` path (GE_LEFT with its name, roster saved);
     the directory forgets it, so no add-friend/invite by name can follow; every loaded
     friend/ignore list drops the GUID through the ordinary `CMSG_DEL_FRIEND`/`CMSG_DEL_IGNORE`
     paths, so its online owner gets FRIEND_REMOVED / FRIEND_IGNORE_REMOVED; a pending group
     invite goes and the character leaves its group (a two-member group disbands, a leader is
     replaced as on leave); finally `ISocialPersistence.PurgeCharacter` →
     `ISocialStore.PurgeCharacterAsync` is queued after every earlier social write, so a snapshot
     queued before the deletion cannot bring a row back;
   - spells (`SpellCharacterDeleteHook`): `SpellbookCache.DeleteCharacter` (cache entry dropped;
     its ordered store delete is a no-op by then);
   - quests (`QuestNpcFeature`): `QuestNpcPersistence.ForgetCharacter` (snapshot and quarantine
     dropped; refused while writes are queued).

Items have no live cache beyond the player; the module cleanup removes `item_instance` rows owned
by the character and its `character_inventory` slots.

## Shared-file edits

| File | Change |
|---|---|
| `src/ArcaneCore.World/Handlers/CharacterHandlers.cs` | delete handler calls `CharacterDeletion.TryDeleteAsync` (net −8 lines) |
| `src/ArcaneCore.World/Features/WorldFeatures.cs` | `typeof(ICharacterDeleteHook)` added to `SeamInterfaces` |
| `src/ArcaneCore.Data/Stores/EfCharacterStore.cs` | `DeleteAsync` is transactional and runs the module cleanups; item deletion moved to `ItemCharacterDataModule` |
| `src/ArcaneCore.Kernel/Social/SocialRecords.cs` | `ISocialStore.PurgeCharacterAsync` with a default no-op body |
| `src/ArcaneCore.Game/Social/SocialContext.cs` | `ISocialPersistence.PurgeCharacter` with a default no-op body |
| `src/ArcaneCore.Game/Groups/GroupManager.cs` | additive `OnCharacterDeleted(ObjectGuid)` (the only way to remove an offline member) |
| Module files (items, spells, quests, social) | each implements `ICharacterDataCleanup` |
| `CharacterSaveQueue`, `QuestNpcPersistence`, `SocialWriteQueue` | additive `ForgetCharacter` / `PurgeCharacter` |
| new files | `SocialCharacterDeleteHook`, `SpellCharacterDeleteHook` (separate features; `SocialFeature`/`SpellFeature` unchanged), `QuestNpcFeature.Deletion.cs` (partial) |
| tests: `SocialTestServices.cs`, `QuestRewardStoreTests.cs` | in-memory purge; the deleted character's quest row is now expected to be gone |

## Tests

- Data (every engine): `CharacterDeletionTests` — guard over all characters modules; full
  deletion across characters/action bar/items/inventory/spells/quests/taxi/social (owned and
  pointing)/guild membership with other characters untouched; guild leader refused with every
  module rolled back; wrong account / unknown id remove nothing; an injected failing cleanup rolls
  back earlier set-based deletes; late social writes are removed by the idempotent purge.
- Game: `GroupCharacterDeletionTests` — offline group leader replaced; two-member group
  disbands.
- World (loopback): `CharacterDeleteCleanupTests` — hooks discovered; end-to-end deletion of a
  character that is a friend+ignore, guild member and offline group member with a cached
  spellbook; guild leader refused and kept; another account's / unknown / zero / out-of-range GUID
  refused. `ForgetDeletedCharacterTests` covers both queues' `ForgetCharacter` (no retry of the
  deleted character's retained snapshot, no shutdown rewrite, no inherited hold/quarantine,
  refusal while quest writes are queued).

## Limits

- One session per account is assumed (the existing realm behavior); deletion and login of the
  same character are not serialized beyond the in-world checks before and after the flushes.
- Social writes are best effort (existing queue: three attempts, then logged); the purge shares
  that policy. A store without `PurgeCharacterAsync` keeps orphan rows, which login already skips.
- No soft delete / undelete (`CharacterDeletionMethod` in later cores) and no
  character-level audit log.
- Real-client deletion acceptance is deferred with the other client runs.
