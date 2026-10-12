# Combined-World character ownership fencing

Baseline: `f67b41a15a5791d6b4850f7ea91d4067aaeba3cc`, 2026-10-11. This is a bounded M15.0 prerequisite. It does not enable multiple workers or establish leases, placement, handoff, realm economy services, or complete feature-store fencing.

The characters database currently belongs to one realm. The initial durable scope is `world`: one combined-World writer authority per characters database. A claim carries an epoch and a fresh process incarnation. A second active owner is denied. A crashed claim remains active; takeover requires the explicitly observed incarnation and epoch, and changes ownership by compare-and-swap. Release is conditional and retains the monotonically increasing epoch.

Configuration is opt-in. Captured player state carries the claim through autosave, logout, economy, and quest-reward transactions. The data guard checks ownership atomically in the same transaction as state/item changes; every save, fenced or not, holds the gate row for its transaction so a claim cannot interleave after the decision. On SQLite that is a write to the row (one writer anyway). On MariaDB/PostgreSQL it is a shared row lock (`LOCK IN SHARE MODE` / `FOR SHARE`): concurrent saves do not serialize behind the row, while a claim, takeover or release (which update it) wait for in-flight saves. An old-owner rejection is terminal for the save queue, is surfaced to the host, and must not be retried or reported as a successful drain. Retained diagnostic state is not replayable state.

## Required automated evidence

| Check | Status |
|---|---|
| Additive schema creates and idempotently seeds the inactive gate, including upgrade/restart | Pass for reported SQLite integration evidence; other providers pending |
| Concurrent acquisitions have at most one active winner | Pass for reported SQLite `ConcurrentAcquisitionHasOneWinner`; provider parity pending |
| A second active startup fails before world/feature attachment | Pass in `ExistingOwnerBlocksStartupBeforeFeaturesAttachEvenWhenFencingIsDisabled` |
| Explicit takeover/release compare all expected identity fields; stale release cannot clear a newer owner | Pass for reported SQLite ownership tests; MariaDB/PostgreSQL row-lock evidence pending |
| Current-token core save succeeds; missing/stale token leaves character and inventory unchanged | Pass for reported SQLite ownership save tests |
| Economy and quest-reward bypass paths reject old authority and roll back all related changes | Pass in `OldOwnerCannotCommitEconomyOrQuestReward` for reported SQLite evidence |
| Captured state keeps its original token through queue copies/coalescing and login/shutdown | Partial: `PlayerSnapshotCarriesTheWorldOwnershipToken` and host lifecycle are covered; queue copy/coalescing and relog evidence pending |
| Queue calls a rejecting store once, fails waiters, blocks replay, and never reports successful drain | Partial: terminal-drain ownership retention is covered; the full once/waiter/replay assertion remains pending |
| Host acquires before writes/start and releases only after all persistence drains succeed | Pass in `ClaimsBeforeFeatureAttachmentAndReleasesAfterAHealthyDrain` and `FailedTerminalDrainRetainsOwnership` |
| Fencing-disabled ordinary behavior and focused existing tests remain valid | Pass for reported release build focused suites |
| SQLite plus MariaDB/PostgreSQL provider evidence | Partial: the gate-locking, quest-fencing, schema-upgrade and quest-reward Data tests were run locally against MariaDB 10.11.10 and PostgreSQL 16.4 (Windows builds); the full Data suite on those providers is hosted-CI evidence |
| Unfenced saves run concurrently and a claim/takeover waits for them (MariaDB/PostgreSQL) | `OwnershipGateLockingTests` (server providers only; SQLite is single-writer) |
| Real two-process stale-write scenario | Pass in `CharacterOwnershipProcessTests` (SQLite file, two child processes) |
| Build-5875 lifecycle | Pending |

SQLite establishes atomicity under its file-writer lock. MariaDB/PostgreSQL tests must additionally establish concurrent row-lock/transaction behavior. A provider missing from the test environment remains unverified. No real-client or production deployment acceptance follows merely from these automated tests.

Local evidence reported for release build 8 on 2026-10-12: Game 3/3, World 83/83, Data 26/26, and Docs 98/98. This is fixture and SQLite evidence; it does not prove a real two-process run, provider parity, build-5875 lifecycle, or deployment acceptance. Reproduce with the repository's focused Game, World, Data, and Docs filters when the build runner has resources.

The first guard covers staged core character and inventory writes, including the economy/quest transactions that consume that seam. Other feature stores and realm-global ID allocators still need explicit ownership/ordering work before multiple workers are safe. It is incorrect to describe this slice as fully fenced persistence.

## Operations

**Schema v60 is forward-only.** Version 60 creates `character_ownership` and seeds the inactive `world` row. The bootstrapper never drops it, and older server builds do not read it, so a downgrade needs no schema change, but there is no automated rollback of the version number: to return a characters database to v59, restore the pre-upgrade backup (`docs/ops/database-upgrade.md`).

**A stuck ownership row.** A crashed World keeps its claim active on purpose, and the next start is denied until an operator decides. With every World process on that characters database stopped, inspect and clear it:

```sql
SELECT scope, owner_node, owner_incarnation, epoch, write_sequence, active FROM character_ownership;
UPDATE character_ownership SET active = 0, owner_node = NULL, owner_incarnation = NULL WHERE scope = 'world';
```

Keep `epoch` and `write_sequence`: the epoch is monotonic and a stale process still holding an older token must stay rejected. Use `FALSE` instead of `0` on PostgreSQL. Never clear the row while a World process might still be running.
