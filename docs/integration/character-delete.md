# Character deletion cleanup (Grok Bot fleet round 2, lead slice)

Branch `feat/character-delete-cleanup`, based on canonical integration head
`0d32fba1070a0be08932a85551a4c1b4ca191cfc`. The original slice used no schema version.
The deletion-outcome recovery below (branch `claude/ac-1-delete-recovery`, base `c3dea16`) adds
characters schema **v11** (`CharacterDeletionDataModule.Version`, the one constant the integrator
renumbers; seams.md "Schema versions").

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
4. Re-check the character is not in the world, then `ICharacterDeletionStore.DeleteAsync(operation, ...)`
   (a host without one, such as the in-memory test host, uses `ICharacterStore.DeleteAsync`) in one
   transaction: every module cleanup (items, spellbook, quests/taxi, social with leader
   refusal, the ledger module's own check), then action bar, `characters` row and the
   `character_deletion` ledger row. Refusal or failure leaves every row and no ledger row.
   If the call throws, the outcome is read back with `IsCommittedAsync(operation)` in a fresh
   scope (see "Deletion outcome recovery").
5. `CharacterSaveQueue.ForgetCharacter` (holds, quarantine and the retained failed snapshot
   go), then each hook's `OnCharacterDeletedAsync`, then `CharacterDirectory.Remove`, then
   `CompleteAsync(operation)` removes the ledger row only if every hook succeeded:
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
     its ordered store delete is conditional, see "Lifetime fence") and the same for the saved
     cooldowns/auras; the hook then waits (bounded by `CharacterDeletion.DrainTimeout`) for both
     removals to be attempted;
   - quests (`QuestNpcFeature`): `QuestNpcPersistence.ForgetCharacter` (snapshot and quarantine
     dropped; refused while writes are queued).

Items have no live cache beyond the player; the module cleanup removes `item_instance` rows owned
by the character and its `character_inventory` slots.

## Shared-file edits

| File | Change |
|---|---|
| `src/ArcaneCore.World/Handlers/CharacterHandlers.cs` | delete handler calls `CharacterDeletion.TryDeleteAsync` (net −8 lines); recovery: `HandleCharEnumAsync` calls `CharacterDeletion.ReconcilePendingAsync` first, and `HandleCharCreateAsync` wraps `CreateAsync` in a try/catch that answers `CharCreateError` |
| `src/ArcaneCore.World/Features/WorldFeatures.cs` | `typeof(ICharacterDeleteHook)` added to `SeamInterfaces` |
| `src/ArcaneCore.Data/Stores/EfCharacterStore.cs` | `DeleteAsync` is transactional and runs the module cleanups; item deletion moved to `ItemCharacterDataModule`; recovery: implements `ICharacterDeletionStore`, a `Guid operationId` overload writes the ledger row, `CreateAsync` fences explicit ids |
| `src/ArcaneCore.World/WorldServiceCollectionExtensions.cs` | `CharacterDeletionReconciler` singleton (recovery) |
| `src/ArcaneCore.Kernel/Reputation/CharacterReputation.cs`, `ReputationWriteQueue`, `EfCharacterReputationStore` | `ICharacterReputationStore.DeleteDeletedCharacterAsync`, used by the queued removal (recovery) |
| `src/ArcaneCore.Game/Social/SocialContext.cs` | `ISocialPersistence.FlushAsync` with a default completed-task body; `SocialWriteQueue.FlushAsync` (recovery) |
| `EfCharacterSpellStore`, `EfCharacterSpellStateStore`, `EfInstanceStore`, `EfSocialStore` | their post-delete removals are conditional on the id having no `characters` row (recovery) |
| `src/ArcaneCore.Kernel/Social/SocialRecords.cs` | `ISocialStore.PurgeCharacterAsync` with a default no-op body |
| `src/ArcaneCore.Game/Social/SocialContext.cs` | `ISocialPersistence.PurgeCharacter` with a default no-op body |
| `src/ArcaneCore.Game/Groups/GroupManager.cs` | additive `OnCharacterDeleted(ObjectGuid)` (the only way to remove an offline member) |
| Module files (items, spells, quests, social) | each implements `ICharacterDataCleanup` |
| `CharacterSaveQueue`, `QuestNpcPersistence`, `SocialWriteQueue` | additive `ForgetCharacter` / `PurgeCharacter` |
| new files | `SocialCharacterDeleteHook`, `SpellCharacterDeleteHook` (separate features; `SocialFeature`/`SpellFeature` unchanged), `QuestNpcFeature.Deletion.cs` (partial) |
| tests: `SocialTestServices.cs`, `QuestRewardStoreTests.cs` | in-memory purge; the deleted character's quest row is now expected to be gone |

## Tests

- Recovery (characters v11): Data `CharacterDeletionLedgerTests` (commit-then-throw, rollback and
  refusal, caller transaction, create fence, idempotent complete, cleanup refusal, generated-id
  evidence), `PostDeleteRemovalFenceTests` and `ReputationLifetimeFenceTests` (late removal keeps a
  recreated character, still deletes orphans), `IntegratedSchemaTests` (allocation by constant);
  World `SocialWriteQueueFlushTests`; MockClient `CharacterDeleteOutcomeRecoveryTests` (real
  SQLite, real world, loopback clients: lost acknowledgement answers success and finalizes; failed
  read-back then resend and then enumeration both finalize; another account cannot trigger it;
  `CompleteAsync` failure re-runs once per session; a throwing create answers `CHAR_CREATE_ERROR`
  and keeps the session).
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

## Deletion outcome recovery (characters v11)

Handoff item 1 (`claude-handoff-20261003.md`). Before this, the stored delete and the runtime
finalizers shared one `try`: when the store call threw *after* committing (a lost acknowledgement),
`CMSG_CHAR_DELETE` answered `CHAR_DELETE_FAILED` and skipped every finalizer, and a retry found no
row and skipped them again, so the spellbook cache, friend lists, guild roster, groups, instance and
economy caches, save-queue holds and the directory entry stayed live until restart. The pattern is
the economy ledger's (`economy_operation` / `IsCommittedAsync`).

**Ledger.** `ICharacterDeletionStore` (Kernel) with `PendingCharacterDeletion`; `EfCharacterStore`
implements it. `character_deletion` (`CharacterDeletionDataModule`, v11, implements
`ICharacterDataCleanup`): `operation_id` PK, `character_id` UNIQUE, `account_id` INDEX, `name`,
`committed_at`. The row is written in the deletion transaction, so it exists exactly when the
character's rows are gone, and stays until the world completes the finalizers. The module's
cleanup refuses to delete a *live* character whose id still has a pending row (an id recreated out
of band), which protects the earlier lifetime's finalization.

**World flow (`CharacterDeletion`).**
- The range, online, ownership, `CanDelete`, barrier, save-flush, `Deleting` and online re-check
  steps are unchanged. The durable delete runs with a fresh `Guid` operation.
- A throwing delete is read back with `IsCommittedAsync` in a fresh scope (the session's context and
  connection may be faulted). Committed: finalize and answer success. Not observed as committed, or
  the read-back fails: answer `CHAR_DELETE_FAILED`.
- A delete for a missing row looks the id up in `GetPendingAsync(session.AccountId)`. A pending row
  of this account is finalized from its recorded id/account/name and answers success; anything else
  fails as before. The online and ownership checks still run; the `CanDelete` hooks and settlement
  barriers do not, because the rows are already gone.
- `CharacterDeletion.ReconcilePendingAsync` runs at the top of `CMSG_CHAR_ENUM`: it finalizes every
  pending deletion of the account, so recovery does not depend on the client retrying the delete.
  It never throws, skips ids that are online or have a live row again, and retries a given
  operation at most once per session (`CharacterDeletionReconciler`), so a finalizer that keeps
  failing cannot make every character-list request pay for it. A process-wide in-flight guard keeps
  two sessions of one account from finalizing the same operation at once.
- `FinalizeAsync` order: save-queue forget, every hook's `OnCharacterDeletedAsync` (a failure is
  logged, the rest still run), directory removal, then `CompleteAsync` only if every hook
  succeeded. Otherwise the row stays and the sweep re-runs the finalizers.

**Meaning of the answers.** `CHAR_DELETE_SUCCESS` means the stored rows are durably gone. The
response packet bytes did not change. `CHAR_DELETE_FAILED` means nothing was observed removed, or the
outcome is unknown. "Not observed committed" is not "nothing happened": after a connection fault on
MariaDB/PostgreSQL a read can run before the server finishes a COMMIT it already received (the
economy reconciliation has the same limit). A late commit is found by the sweep or a retry.

**Idempotent finalizers.** Hooks can now run twice for one character. Checked per hook: social
(`AdminUninvite` of a non-member, list `Has` guards, set/queue removals) and instances and spells
(map, set and queue removals) are naturally idempotent. Quests: `QuestNpcPersistence.ForgetCharacter`
throws while writes are still queued; that failure keeps the deletion pending and the next sweep
retries it once the queue drained. Economy: `ResyncDeletedCharacter` re-reads rows, but a re-run
re-sends `SMSG_RECEIVED_MAIL` for letters returned within the last 60 seconds, a cosmetic duplicate
that is accepted and not suppressed.

**Create fence and error path.** `EfCharacterStore.CreateAsync` for an explicit nonzero `Id` inserts,
then checks `character_deletion` for that id in the same transaction, and throws
`CharacterIdPendingDeletionException` (rolling the insert back) while a deletion of it is pending.
Generated ids (`Id == 0`, the only thing the world ever creates) are not fenced:
`CharacterDeletionLedgerTests.GeneratedIds_AreNotReusedAfterDeletingTheHighest` shows no reuse on
SQLite, and exists to be run on MariaDB/PostgreSQL in CI. A fence on generated ids would turn any
reuse into a realm-wide create failure until the owner enumerates. `HandleCharCreateAsync` now wraps
`CreateAsync` in a try/catch that logs and answers `CHAR_CREATE_ERROR`; before, a store exception
escaped to the dispatcher and dropped the session with no `SMSG_CHAR_CREATE`. `CreateAsync` also
clears the change tracker after a failed insert, so the scoped context does not retry it.

**Lifetime fence for the redundant post-delete removals.** The queued removals run after the rows are
gone and delete by character id alone. If a character were explicitly recreated with that id before
a delayed or retried removal ran, it would wipe the new lifetime's data. This is a conditional gap
under explicit id reuse, not observed client loss. The removals are now one conditional statement
(`... AND NOT EXISTS (SELECT 1 FROM characters WHERE Id = id)`, per statement) in:
`EfCharacterSpellStore.DeleteCharacterAsync`, `EfCharacterSpellStateStore.DeleteCharacterAsync`
(cooldowns and auras separately), `EfInstanceStore.DeleteCharacterAsync`,
`EfSocialStore.PurgeCharacterAsync` and the new `ICharacterReputationStore.DeleteDeletedCharacterAsync`
(the reputation queue uses it; creation's stale-row clear keeps the unconditional
`DeleteCharacterAsync`). The in-transaction module cleanups stay unconditional. The social, spell,
reputation and instance hooks then wait, bounded by `CharacterDeletion.DrainTimeout` /
`InstanceFeature.DeleteTimeout`, for the queued removal to be attempted
(`SocialWriteQueue.FlushAsync` is new), so the ledger completes only after the removals ran. "Run"
means attempted, not succeeded: `SpellStatePersistence`, `SpellbookCache` and the write queues log
and swallow a failed store call. The ledger plus the create fence are the primary guarantee; the
conditional statements are defense in depth, because on InnoDB a concurrent create is not atomic
with a multi-statement removal.

## Limits

- One session per account is assumed (the existing realm behavior); deletion and login of the
  same character are not serialized beyond the in-world checks before and after the flushes.
- Social writes are best effort (existing queue: three attempts, then logged); the purge shares
  that policy. A store without `PurgeCharacterAsync` keeps orphan rows, which login already skips.
- No soft delete / undelete (`CharacterDeletionMethod` in later cores) and no
  character-level audit log.
- Real-client deletion acceptance is deferred with the other client runs. Whether the 1.12.1
  client re-enumerates after `CHAR_DELETE_FAILED` was not verified; recovery does not depend on it.
- Recovery (v11): the sweep runs only when the owning account requests its character list. After a
  process restart the live caches are already empty, so a pending row mainly blocks explicit-id
  recreation until that account next enumerates or deletes; it never blocks generated ids. There is
  no background sweep. A finalizer that fails persistently is retried at most once per session.
- A deletion that a database server commits only after its connection failed is "not observed
  committed" when read back; it is finalized later by the sweep or a retry, not at that request.
- Economy re-sends `SMSG_RECEIVED_MAIL` for recently returned letters on a re-run (cosmetic).
- Not exercised here: MariaDB and PostgreSQL (`ARCANECORE_TEST_MARIADB` / `ARCANECORE_TEST_POSTGRES`
  were not set, no server was running), so the conditional `ExecuteDelete` translations, the unique
  index and the commit-then-throw ledger test are proven on SQLite only until CI's cells run them.
- The ledger tests inject the lost acknowledgement with an EF `DbTransactionInterceptor`
  (a real commit, then a throw); a real network fault mid-COMMIT was not produced.
- The v11 upgrade path creates the table only; like every characters module it relies on the
  bootstrapper's table-only upgrade step (remaining-work item 3 covers index parity for upgraded
  databases, so an upgraded database may lack the `character_deletion` unique/account indexes).
- A World-level test that races a gated `SpellStatePersistence` delete against an explicit
  recreate was not added: queued writes are ordered behind the delete, so it only fails for writes
  that bypass the queue; the store-level fence tests cover that shape directly.
