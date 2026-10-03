# Atomic creature imports (2026-10-03)

Base: `43f1e22`, separate candidate `codex/atomic-creature-import-20261003`.

The [P1 finding on PR #7](https://github.com/Krilliac/ArcaneCore/pull/7#discussion_r4171210272)
identifies replacement imports that delete existing creature content before inserting
new rows in 2,000-row batches. There was no transaction spanning those operations:
a later failed batch or cancellation could leave the old content deleted and a
partial replacement saved.

The caller audit at this base found only two invocations, both in
`tests/ArcaneCore.Data.Tests/CreatureDataTests.cs`: the initial append and replacement.
Neither establishes a transaction. The documented library entry point has no
production CLI or daemon caller that supplies protection. The separate spell
content importer uses its own transaction, which does not protect creature writes.

`CreatureDumpImporter.WriteAsync` now begins a transaction before any delete or
insert and commits only after every table's batches succeed. If an explicit EF
transaction is already active, it creates a unique savepoint before the import,
releases it on success, and rolls back only to that savepoint on failure. The
caller retains transaction ownership. Transactions that cannot create savepoints
are refused before writes. This uses the APIs documented in
[EF Core transactions and savepoints](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

Cancellation rolls back with `CancellationToken.None`; cancelling the import
does not cancel the cleanup that preserves earlier content. The importer clears
its tracked entities on every exit from the write phase and restores the previous
`AutoDetectChangesEnabled` setting. If rollback itself fails, both failures are
reported in an `AggregateException`; that context and transaction must be discarded.

The context must have an empty change tracker on entry. The old batch code saves
and clears all tracked entities, so accepting caller state could flush or detach
unrelated work. Callers must save their changes and clear tracking or use a
dedicated context before importing. The guard leaves rejected caller state intact.
An ambient `System.Transactions` scope without an explicit EF transaction is also
rejected before mutation, rather than relying on provider-dependent enlistment.

`CreatureImportAtomicTests` uses only synthetic rows and disposable databases
from the existing SQLite/MariaDB/PostgreSQL fixture. It verifies:

- Successful replacement of all five creature tables with 2,001 templates.
- An interruption or cancellation after the first completed 2,000-row batch
  restores the previous five-table content and leaves the context usable.
- A real NOT NULL violation in the later batch rolls back earlier batches and deletes.
- A successful import remains subject to the caller's rollback.
- A failed caller-owned import restores its savepoint, preserves earlier caller
  work, and allows another import and caller commit in the same transaction.
- Cancellation still restores a caller-owned savepoint and permits caller commit.
- Tracked caller state and unsupported ambient scopes are rejected before writes.

Local validation: the full Release solution build passed with warnings treated
as errors (zero warnings/errors), followed by 8,526 passing tests: crypto 8,005,
data 50 (SQLite, including all nine new atomic import cases), game 335, realm 3,
world 133. No failures or skipped tests. Existing schema bootstrap/upgrade checks
passed; auth remains v2, characters/world v6, with no migration changes.

The draft PR records exact commit/run provenance and the hosted SQLite, MariaDB,
and PostgreSQL matrix result. No schema or protocol changes, content assets,
valued imports, or developer databases are involved. A broken provider connection
can prevent rollback itself; both errors are reported and the caller must discard
that context/transaction. No valued database was used to simulate that condition.
