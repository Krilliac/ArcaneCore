# SQL update files

Every schema change ArcaneCore ships is also available as plain SQL, so a database can be created or updated by hand (the
vmangos `sql/migrations` workflow) without running `arcane-db`. The files are generated from the code's EF model by
`arcane-db export-updates --out sql/updates` and checked by `SqlUpdateGoldenTests` / `SqlUpdateReplayTests`; do not edit them.

```
sql/updates/<component>/schema-snapshot.json            shape of the schema after the newest file, plus a hash manifest of every file
sql/updates/<component>/<provider>/base_vNNNN.sql       baseline: an empty database to schema version NNNN
sql/updates/<component>/<provider>/NNNN_<Module>.sql    the step that takes a database from NNNN-1 to NNNN
```

`<component>` is `auth`, `characters` or `world` (characters is per realm); `<provider>` is `sqlite`, `mariadb` or `postgresql`.

**Stop every daemon and back up the database first** ([database upgrade runbook](../../docs/ops/database-upgrade.md)); rolling
upgrades are unsupported.

## Order

1. A new database: apply the baseline of each component, then every step file above the baseline's version in ascending order.
   Auth first, then characters and world.
2. An existing database: apply, in ascending order, the step files whose number is above the version in its `<component>_schema` table.
   Each step file refuses to run unless the database is recorded at exactly the previous version, so a skipped or repeated file
   fails before it changes anything. A baseline refuses to run when the database already holds any table of the component
   (including a pre-M5 table set without a version table).
3. A database older than the baseline (versions below the first baseline) has no SQL path. Bring it to the baseline with
   `arcane-db upgrade` (which also applies every later step), or recreate it from the baseline.

## Applying a file

`<` redirection is a cmd.exe / POSIX shell feature; PowerShell does not support it, so on Windows use the client's own file
option or cmd.exe.

| Provider | POSIX shell / cmd.exe | PowerShell or any shell |
|---|---|---|
| SQLite | `sqlite3 -bail <db> < file.sql` | `sqlite3 -bail <db> ".read file.sql"` |
| PostgreSQL | `psql -v ON_ERROR_STOP=1 -d <db> -f file.sql` | same (`-f` works everywhere) |
| MariaDB | `mariadb <db> < file.sql` | `mariadb --abort-source-on-error <db> -e "source file.sql"` |

- MariaDB `source` (the `-e "source ..."` form) **continues after an error by default**; `--abort-source-on-error` is what
  makes it stop at the guard. Never drop it.
- **Never use `--force`** (mysql/mariadb) or drop `ON_ERROR_STOP` (psql): the guard at the top of every file works by making the
  client stop, and a client that carries on would run the changes anyway.
- SQLite: one transaction; a failure leaves the database unchanged (`-bail` stops at the first error and the open transaction is
  rolled back when the client exits).
- PostgreSQL: one transaction; a failure changes nothing. The files carry their own `BEGIN`/`COMMIT`, so do not add `-1`
  (`--single-transaction`): psql's documentation warns against combining it with files that manage their own transaction.
- MariaDB: no transaction (MariaDB commits DDL implicitly). The guard is a `BEGIN NOT ATOMIC ... SIGNAL SQLSTATE '45000'` block
  between `DELIMITER //` lines, so use the command-line client (or one that understands `DELIMITER`); the rest is plain statements.

Each file starts with a header (component, provider, from, to, owner module, SHA-256 of the body), then the guard, then the
changes, and writes the version row last. A baseline records version 0 ("creating") right after the version table, like the
bootstrapper, and updates it to the real version as its last statement.

### MariaDB failure recovery

If a MariaDB file fails midway (or the connection drops), earlier statements stay applied.

- A step file: the version row is unchanged.
- A baseline: the version table holds the "creating" row (version 0) and some tables exist.

Do not re-run the file (the guard refuses a half-built baseline). Run `arcane-db upgrade` (back up first), which re-evaluates each
change against the real catalog, skips what exists and finishes the step or the create. `arcane-db check` then confirms the
database matches the model. If the failure came before the version table existed (the guard itself, or `ALTER DATABASE`), nothing
needs recovering: fix the cause and run the file again.

## Limits

- Files are generated offline from the model against a snapshot, so index decisions assume empty tables: a unique index added to
  a table that already holds duplicate rows fails mid-step (on MariaDB, after earlier statements of that step committed; recover
  as above). `arcane-db plan` / `upgrade` check a live database for duplicates before changing anything; use them when in doubt.
- The MariaDB DDL is generated for MariaDB 11.4 (the version in `docker-compose.yml`); CI also runs 10.11. The replay test
  (`ARCANECORE_TEST_MARIADB`) shows whether a given server accepts it.
- Only additive changes (new tables, columns, indexes) can be exported; a changed column type or nullability is refused.

## Seed data

Apart from the row inserts described below, the files carry the schema and the version row only. Initial rows (configured realms, the Warden scan table, M3 world data) are
written by the daemons' initializers at start, into empty tables, and are not part of the schema.

A step can also insert rows of its own (`EnsureRowChange`: insert the row when its key is absent, never overwrite an edited one). Characters 0060/0061 and world 0064 (the shipped bot goal ladder) do this; the generated file carries the INSERTs and is version-guarded like any other.

Content ArcaneCore ships as rows (the Undercity siege bot scenario) is separate idempotent SQL under [`sql/content`](../content/README.md).

## For contributors

A change that adds a schema step must regenerate and commit its update files in the same PR:
`ARCANECORE_UPDATE_SQL=1 dotnet test tests/ArcaneCore.Data.Tests --filter SqlUpdateGoldenTests` (or
`arcane-db export-updates --out sql/updates`). Released files are frozen: the manifest in `schema-snapshot.json` fails the golden
tests when one changes.

**Renumbering at merge time:** version numbers are allocated by the integrator and can change when lanes merge. Delete the
lane's own new files (the `NNNN_*.sql` it added, for all three providers), restore main's `schema-snapshot.json` for the
component (`git checkout origin/main -- sql/updates/<component>/schema-snapshot.json`), then run `arcane-db export-updates`
again so the steps are re-generated with the new numbers.
