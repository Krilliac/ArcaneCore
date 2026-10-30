# Upgrading a production database

How to move a real MariaDB, MySQL, PostgreSQL or SQLite install from one ArcaneCore version to the next with
`arcane-db` (`tools/ArcaneCore.DbUpgrade`, also reachable as `arcane-account db ...`). Delivered scope, limits and
reference citations: `docs/integration/db-upgrade-tooling.md`.

## The rules

1. **Stop every daemon first.** Realm, world and every tool. Rolling upgrades are unsupported: a daemon that is
   running holds sessions on the database and an older binary refuses a newer schema.
2. **Back up first.** The tool cannot undo a schema change; a restore from backup is the only rollback.
3. **Characters is per realm.** Run the tool once per realm configuration (each realm has its own characters database).
   Auth is shared by every realm: upgrade it once, before the first realm that needs it.
4. **A database newer than the binary is refused.** An older build never touches a newer schema (exit 4). Run the newer
   build, or restore the matching backup. Retail servers only warn about extra migrations; this is stricter on purpose.

Qualified servers (what CI runs): **MariaDB 10.11 or newer, PostgreSQL 16 or newer, SQLite.** MySQL 8 is supported
but not qualified: `arcane-db status` prints a warning, take the backup seriously.

## Procedure

For client data inspection without a database connection, run `arcane-db dbc dump <File> [--id N] [--json]`.
It prints fields using the generated build-5875 WoWDBDefs layout; `File` can be a path or a file in `ClientData:DbcDirectory`.

```text
1. stop realm, world and every tool
2. back up each database          (arcane-db backup-info prints the command; SQLite: --backup-dir)
3. arcane-db status               versions of every component; exit 3 means an upgrade is pending
4. arcane-db plan                 read-only dry run: what would be created, changed or refused
5. arcane-db upgrade --confirm-backup
6. arcane-db check                the same drift check that upgrade ran at its end
7. start the new binaries
```

World schema 57 adds the six creature magic resistance columns to `creature_template`, all 0 after the upgrade. Run the content importer's `refresh` ([content import](../areas/content-import.md)) with a ClassicDB, cmangos or vmangos dump once after step 6 to fill them: the report line `creature_template (magic resistances)` counts the templates changed. A dump without the resistance columns leaves them as they are.

World schema 62 adds `holiday_flavour` (how bots tell each festival). The step creates the table empty; the next world daemon start fills an empty table with the shipped rows (the world database initializer), so there is nothing to apply by hand. A table that already has rows is left alone, and `sql/content/world/<provider>/0002_holiday_flavour.sql` is only for an operator who wants to reset or edit the shipped rows.

Configuration is the `Database` section the daemons use (`appsettings.json` next to the tool, an extra file with
`--config <file>`, or environment variables such as `Database__Characters__ConnectionString`); keep passwords in the
environment, not on the command line. Every line the tool prints is scrubbed of passwords.

```text
export Database__Auth__ConnectionString='Server=db;Database=arcane_auth;User ID=arcane;Password=...'
export Database__Characters__ConnectionString='Server=db;Database=arcane_chars_1;User ID=arcane;Password=...'
export Database__World__ConnectionString='Server=db;Database=arcane_world;User ID=arcane;Password=...'
arcane-db status
```

### Backups

`arcane-db backup-info` prints, per distinct database, the exact command and the environment variable that carries
the password (it is never put on a command line):

| Engine | Command | Password |
|---|---|---|
| MariaDB / MySQL | `mysqldump --host=H --user=U --single-transaction --quick --order-by-primary --default-character-set=utf8mb4 --result-file=F DB` | `MYSQL_PWD` |
| PostgreSQL | `pg_dump --host=H --username=U --format=custom --file=F DB` | `PGPASSWORD` |
| SQLite | `arcane-db upgrade --backup-dir <directory>` writes a verified copy (`VACUUM INTO`) | none |

The tool never starts `mysqldump` or `pg_dump` itself, and it cannot verify that your backup exists or restores:
`--confirm-backup` is your statement that it does. Only the SQLite copy is verified by the tool (integrity check). A
`--backup-dir` inside a git work tree that is not git-ignored is refused (player data must not land in a repository),
as is an existing destination file (a backup is never overwritten). A database with no schema yet needs no backup.

### Applying the SQL by hand

`arcane-db plan --script --component characters > upgrade.sql` prints exactly the statements an upgrade would issue and
the version-row write after each step; every other line is a `--` comment, so the file runs as is (`mysql < upgrade.sql`,
`psql -f`, `sqlite3`). This keeps the retail workflow of operator-applied SQL
(`<refs-root>\vmangos-wiki\docs\Database-Setup.md:46-66`, `:88`). The script does not create the database or set its
character set. Run `arcane-db check` afterwards.

For a database the Codex line created (below) the script also carries the migration's row moves, each where the
migration runs it: the `UPDATE` that copies `npc_template_service_metadata` into `creature_template` follows the
`ALTER TABLE` statements of world step 21 and precedes the version write, so the script ends the database exactly as
`upgrade` would. A plan holding a row move that cannot be written as SQL gets no script at all (only `--` comments,
an error naming the move, exit code 4): a script that skipped the move would still record the merged version, after
which the database reads as already migrated and the rows would never move. Use `migrate-codex --apply` then.

### Update files in the repository (`sql/updates`)

Besides `plan --script` (which needs a live database), the repository carries committed SQL update files: a baseline per
component and provider at the released schema version and one file per later schema step, with a prior-version guard,
a transaction where the engine has transactional DDL and the version row written last. They let a database be created or
updated by hand with `sqlite3`, `psql` or `mysql` and are generated by `arcane-db export-updates --out sql/updates`.
How to apply them per provider, their order, MariaDB's non-atomic recovery (run `arcane-db upgrade`) and the rule that a
database older than the baseline reaches it through `arcane-db upgrade` are in [sql/updates/README.md](../../sql/updates/README.md).

## Exit codes

| Code | Meaning | Changed anything? |
|---|---|---|
| 0 | ok: current, upgraded, or drift clean | upgrade: yes |
| 1 | unexpected failure (a bug, or an input the tool did not anticipate) | maybe: read the message |
| 2 | usage or configuration: unknown command or option, missing value, no connection string | no |
| 3 | `status`, `plan`: an upgrade is pending (a normal report; `--no-fail-on-pending` makes it 0 for `set -e` scripts) | no |
| 4 | refused: database newer than the code, unknown state, blocker (duplicate rows, conflicting index, mismatching table), other sessions connected; the world daemon also exits 4 when another world process holds the characters database writer lock (`<database>.world.lock`) or the lock file cannot be created | no |
| 5 | drift: the database differs from the model (`check`, or the check at the end of `upgrade`); for `dbc`, a DBC with another layout or a world id no DBC row has | no |
| 6 | the database server could not be reached or refused the connection (message scrubbed) | no |
| 7 | the wait for another process's schema lock ran out | no |
| 8 | `upgrade` with pending steps and no confirmed backup, or a backup that could not be made | no |
| 9 | `copy`: a table differed from its source when read back through the model column types (row count or a value); the copy was rolled back and the target is as it was | no |

These differ from the content importer's codes (`arcane-content-importer`: 3 wrong source schema, 4 database error,
5 repository path, 6 verify); each tool lists its own in `--help`.

## Commands and options

| Command | What it does |
|---|---|
| `status` | per component: provider, server version (with a warning when CI does not qualify it), database version, code version, pending steps, other sessions, schema lock held |
| `plan` | read-only dry run (never creates the database, takes the lock or writes); `--script`, `--json` |
| `check` | drift check: version row, tables, columns and nullability, indexes, foreign tables, MariaDB engine and charset, PostgreSQL encoding; `--json` |
| `upgrade` | plan everything, refuse before any change, require the backup acknowledgement, apply auth, characters, world in that order, then check |
| `migrate-codex` | find databases the Codex line created and report their one-shot migration to this build's numbering (read-only); `--apply` runs only that migration, behind the same backup gate (see "Databases created by the Codex line") |
| `backup-info` | print how to back each database up |
| `export-updates` | write the SQL update files and schema snapshots under `--out` (normally `sql/updates`) without contacting any database; a first export writes each component's baseline, later exports add one file per new schema step; refuses a non-additive model change or a reserved placeholder version (exit 4) |
| `dbc` | read-only client data check: every file of the DBC directory against its vmangos, ArcaneCore or WoWDBDefs layout, then the world database's spell, map, area, faction, display ... ids that no DBC row has, then the client DBCs' own references into each other (a diagnostic that never sets exit 5; `clientReferences` in `--json`) (docs/areas/client-data.md); `--json` |
| `dbc dump <file.dbc>` | read-only named record dump of one build-5875 DBC; uses generated signedness, needs `--dbc-dir` or `ClientData:DbcDirectory`, and does not open a database |
| `copy` | move a component's rows to another database, typically SQLite to PostgreSQL or MariaDB (see "Moving from SQLite to PostgreSQL or MariaDB"); refuses before writing anything, creates the target schema, copies, resets key counters, verifies and runs `check` on the target |

| Option | Applies to | Meaning |
|---|---|---|
| `--component auth\|characters\|world\|all` | all commands | which component(s); default all |
| `--script` | plan | print the SQL |
| `--json` | status, plan, check, dbc | machine-readable output |
| `--no-fail-on-pending` | status, plan | exit 0 when only an upgrade is pending |
| `--apply` | migrate-codex | migrate; without it `migrate-codex` is a read-only dry run |
| `--confirm-backup` | upgrade, migrate-codex | you hold a backup of every database that has pending steps |
| `--backup-dir <directory>` | upgrade, migrate-codex | also write a verified copy of each SQLite database there (server engines still need `--confirm-backup`) |
| `--allow-active-sessions` | upgrade, migrate-codex, copy | do not refuse when other sessions are connected to the database |
| `--lock-timeout <seconds>` | upgrade, migrate-codex, copy | wait for another process's schema lock (default 60) |
| `--dbc-dir <directory>` | dbc | the build-5875 DBC directory; default `ClientData:DbcDirectory` of the configuration |
| `--out <directory>` | export-updates | the update root to write; required |
| `--source-provider <engine>`, `--source-connection <string>` | copy | name the source instead of the configured `Database` section: `sqlite`, `mariadb`, `mysql` or `postgresql`; one component only |
| `--target-provider <engine>`, `--target-connection <string>` | copy | the target database; one component only. Without them the target comes from ARCANE_DB_TARGET_PROVIDER and ARCANE_DB_TARGET_AUTH_CONNECTION, ARCANE_DB_TARGET_CHARACTERS_CONNECTION, ARCANE_DB_TARGET_WORLD_CONNECTION (ARCANE_DB_TARGET_CONNECTION for a single component) |
| `--replace` | copy | empty the target component's tables first; without it a target that holds rows is refused |

`status`, `plan`, `check` and `migrate-codex` without `--apply` open SQLite read-only and never create a file. All
connections are unpooled so the session count only sees other processes. On a failure in one component the later ones
are not run and the message names it.

### Moving from SQLite to PostgreSQL or MariaDB

`arcane-db copy` moves a realm's databases from one engine to another; it is the supported way to leave SQLite for a server engine.
The source is never changed (SQLite is opened read-only). Run it once per realm configuration, auth once for all realms.

1. **Stop every daemon** (`authd`, `worldd`, tools). `copy` refuses a SQLite characters file that a world process owns (it takes the same
   `<database>.world.lock` sidecar for the length of the copy) and, on a server engine, other connected sessions.
2. **Back up the SQLite files** (`arcane-db backup-info`; with `--backup-dir` `upgrade` writes a verified copy). The copy does not modify them, but you
   will keep them until the new install has run for a while.
3. **Create empty databases and a login role** on the target server, one database per component you move (for example `arcane_auth`,
   `arcane_characters`, `arcane_world`), owned by a role that may create tables. Do not create any tables: `copy` builds the schema with the same
   bootstrap the daemons run, so the target ends at exactly the source's schema version.
4. **Copy.** Put the target connection strings in the environment (a flag shows in the process list):

   ```
   export ARCANE_DB_TARGET_PROVIDER=postgresql
   export ARCANE_DB_TARGET_AUTH_CONNECTION='Host=db;Port=5432;Database=arcane_auth;Username=arcane;Password=...;'
   export ARCANE_DB_TARGET_CHARACTERS_CONNECTION='Host=db;Port=5432;Database=arcane_characters;Username=arcane;Password=...;'
   export ARCANE_DB_TARGET_WORLD_CONNECTION='Host=db;Port=5432;Database=arcane_world;Username=arcane;Password=...;'
   arcane-db copy --component all
   ```

   The source is the configured `Database` section (the realm's current SQLite settings), or `--source-provider` and `--source-connection` for one
   component. For one component the target may also be `--target-provider` and `--target-connection`. Connection strings and passwords are never printed.
5. **Read the summary.** Every table prints its source and target row count and `ok` once the values agree. Exit 0 means every table of every
   selected component matched and `check` is clean on the target. Exit 9 means a table differed after the rows were read back: the copy was rolled back and the
   target is as it was; fix the cause and run again. A MariaDB `AUTO_INCREMENT` failure after the commit exits 1 and says so; run again with `--replace`.
6. **Switch the configuration** to the new engine: `Database:Auth:Provider` and `Database:Auth:ConnectionString`, and the same keys under
   `Database:Characters` and `Database:World` (or `Database:Provider` and `Database:ConnectionString` for a single database).
7. **Start the daemons** and verify: `arcane-db status` shows every component `Current`, the realm list and a login work, and a character
   loads. Keep the SQLite files, untouched, as the rollback.

What `copy` does, in order. It refuses, for every selected component, before the first write: a source that is not at this build's schema
version (run `arcane-db upgrade` on it first), a source whose tables or columns differ from the model or that holds a table no data module owns,
a source and target that are the same database, a world process owning a SQLite characters file, other sessions connected, a target that cannot be
brought to the current schema, and a target that holds rows (any table other than the version table and the rows the schema bootstrap itself
seeds) without `--replace`. Then, per component: creates or upgrades the target schema through the normal bootstrap; requires the target's tables and
columns to equal the source's; empties the component's tables (reverse foreign-key order) and fills them (parents first, from the model's foreign keys;
a cycle is refused and named) with batched multi-row `INSERT`s inside **one** target transaction, so a failure leaves the target as it was; converts each
value to the target column's type (SQLite's text timestamps to `timestamp with time zone` / `datetime`, integer flags to booleans, text decimals and
GUIDs, blobs, NULLs; an unsigned 64-bit column such as a character aura's caster GUID is read from SQLite's signed storage back to its unsigned value, so
GUIDs of `0xF130...` land as `numeric(20,0)` / `bigint unsigned` and are written back to SQLite as the same 64 bits); resets the key counters (PostgreSQL
`setval` on every identity or serial sequence, inside the transaction; MariaDB `AUTO_INCREMENT`, which is DDL and therefore runs right after the commit,
with its own failure message and exit 1 if it fails; SQLite continues from the largest rowid by itself); and, still before committing, verifies every table:
both sides are read back through the model's column types (the same property type EF Core materialises, decoded by each engine's own driver, not by the
copy's conversion code), their row counts and an order-independent SHA-256 digest of every value are compared (a `real` column at float precision), and
only if every table matches does the transaction commit. A difference rolls the whole component back (exit 9), as does any error. The source is read in one
read transaction (a consistent snapshot of that component). Finally it runs the `check` comparison on every target.

The schema version table is not copied: the target's bootstrap wrote it and the copy requires it to equal the source's. Rows the bootstrap seeds
(the world-ownership fence row, id-range and lease scopes, the playerbot ladder) are replaced by the source's rows, so the target ends equal to the
source. `--replace` makes a re-run idempotent; it touches only the selected component's tables, never other tables in the same database.

Limits, stated plainly:

- Timestamps are truncated to **microseconds** on PostgreSQL and MariaDB (their precision); the sub-microsecond ticks of a .NET `DateTime` are an intentional
  loss, and the content check compares at that precision.
- The key-counter reset covers tables with a **single-column** primary key (every generated key in the schema is one); a composite key has no counter.
- Create the target database as **UTF8** (PostgreSQL) or **utf8mb4** (MariaDB). `arcane-db check` on the target reports another encoding or character set, but
  non-BMP text (emoji) only survives a utf8mb4 / UTF8 database. PostgreSQL text cannot hold a NUL character: a row that has one is refused with the table,
  column and primary key, and nothing is written.
- Stop `authd` and `worldd` (the realm and world daemons) before copying: the copy sees a consistent snapshot of each component but cannot see writes a
  running daemon makes to the other components afterwards. A SQLite source is opened read-only; the tool does not inspect its `-wal` file, so make sure the
  daemons exited cleanly (a clean exit checkpoints it; a crash leaves it, and SQLite replays it when the copy opens the file).
- A table that references itself, or a foreign-key cycle, is refused (none exists today); a value the target type cannot hold (an integer outside the
  target column's range, text that is not a number in a numeric column) fails the copy and names the table, column and primary key of the row; the copy is
  one pass over a stopped realm, not a replication.

### Databases created by the Codex line

Until the 2026-10-07 merge the Codex line (`codex/server-continue-20261004`, last commit 0e07c29b) numbered its own
schema steps after the shared history: characters 21-25, world 21-27. The merged build gives those numbers to other
steps and moved the Codex steps to characters 29-33 and world 32-37 (docs/integration/codex-merge-20261007.md). A
database the Codex line created still records the old numbers, so it is migrated once:

* **Recognition** (`ForeignLineDetector`, read-only, only for a version row in the overlapping range): every object of
  this build's own steps up to the recorded version present means this build's line, left alone; otherwise every Codex
  step up to the recorded version present and none after it means the Codex line; anything else (a recorded Codex
  table missing, a step half there, a Codex table the version does not account for, neither line's tables) is refused
  with nothing written. Auth needs nothing: the Codex auth step 4 is this build's auth step 4.
* **Migration** (under the schema lock; on SQLite one transaction): this build's steps 21 up to the merged version are
  applied idempotently (the Codex tables are verified against the model, the steps the Codex line never had are
  created), the rows of the dropped Codex world step 26 (`npc_template_service_metadata`) are copied into the
  `creature_template` NPC columns of world step 21 (the table itself is left in place and `check` reports it as a
  foreign table), and the merged version is written last: characters 25 becomes 33, world 27 becomes 37. The ordinary
  steps after it follow. No row is deleted or rewritten.
* **When**: a daemon start with `Database:Upgrade:Policy` `Always` migrates automatically and logs a warning naming the
  Codex version, the merged version and the steps found. `plan` and `status` show the migration as the first pending step
  ("database version 25 (Codex (0e07c29b) numbering)"); `upgrade` applies it with everything else. `migrate-codex`
  reports it on its own and `migrate-codex --apply` runs only the migration (`upgrade` or the next start does the rest).

```
arcane-db migrate-codex                               # dry run: exit 3 when a Codex-line database was found, 0 when none
arcane-db migrate-codex --apply --backup-dir backups  # SQLite: verified copies first, then the migration
arcane-db upgrade --confirm-backup                    # the steps after the migration, then the drift check
```

## What can go wrong, per engine

| Engine | An upgrade that is interrupted or refused midway |
|---|---|
| SQLite | atomic: the whole bootstrap is one write transaction; nothing changed, run it again |
| MariaDB / MySQL | **not atomic**: DDL commits implicitly. Finished steps stay, the version row names the last finished step, the interrupted step may be half applied. Run `upgrade` again: every change is idempotent and converges |
| PostgreSQL | resumable like MariaDB (each statement autocommits). A single transaction around the bootstrap is **not** implemented (open item) |

Because MariaDB cannot roll back, the tool front-loads every refusal it can see: `upgrade` plans all components first
and refuses, with nothing changed, if any change would be refused. A race (another process changing the schema between
the plan and the apply) can still be refused by the bootstrapper under the lock; that fails closed.

### Duplicate rows (the one expected blocker)

Databases upgraded by the historic bootstrapper lack the unique indexes on `guild_member.CharacterId` and
`auction.item_guid`. The upgrade adds them and **refuses, deleting nothing**, if rows already share a value:

```text
error: characters: refused: cannot create unique index IX_guild_member_CharacterId on guild_member(CharacterId): 1 group(s) of rows share a value. ...
```

Find the groups, decide which row is right, delete the others, run the upgrade again:

```sql
SELECT CharacterId, COUNT(*) FROM guild_member GROUP BY CharacterId HAVING COUNT(*) > 1;
SELECT item_guid, COUNT(*) FROM auction GROUP BY item_guid HAVING COUNT(*) > 1;
```

(PostgreSQL quotes the PascalCase column: `"CharacterId"`.) See `docs/integration/schema-index-repair.md`.

### Locks and sessions

`upgrade` takes a per-component schema lock (SQLite: the file's write lock; MariaDB `GET_LOCK`; PostgreSQL advisory
lock) and waits `--lock-timeout` seconds for it; a daemon that starts at the same moment fails closed rather than
racing. An index build on a large characters table can exceed the default: raise it. Session counting is best effort:
sessions of other roles can be invisible without the `PROCESS` / `pg_read_all_stats` privilege, and SQLite sessions
cannot be seen at all. **Stop the daemons; do not rely on the count.**

## Start-up policy (`Database:Upgrade`)

By default every daemon creates and upgrades the schema when it starts (`Always`, as before). The retail servers never
do: they refuse to start on a database that lacks migrations and make the operator apply them
(`<refs-root>\vmangos\src\mangosd\Master.cpp:415-470`, `<refs-root>\vmangos\src\shared\Database\Database.cpp:587-640`,
`<refs-root>\mangos-classic\src\shared\Database\Database.cpp:490-498`). To get that behaviour in production set:

| Key | Values | Default | Meaning |
|---|---|---|---|
| `Database:Upgrade:Policy` | `Always`, `CreateOnly`, `Never` | `Always` | `Always`: create and upgrade. `CreateOnly`: create an empty database, resume an interrupted create, **refuse to adopt or upgrade** an existing one. `Never`: verify only, create nothing, not even a missing database (the retail behaviour) |
| `Database:Upgrade:LockTimeoutSeconds` | 1 to 86400 | 60 | how long a start waits for another process's schema work |
| `Database:Upgrade:ValidateOnly` | `true`, `false` | `false` | `true`: validate-only serving mode; verify that every component's schema is at the exact current version with all registered module tables present, executing zero DDL and zero seed inserts |

A refused start prints one line (`database schema refused: ...`, passwords removed) and exits 4 (7 for a lock timeout)
instead of crashing. Even `CreateOnly` creates a missing database and a fresh schema, which retail never does; `Never`
(or `Database:Upgrade:ValidateOnly`) plus `arcane-db upgrade` is the retail-like pair, and startup seeding is serialized
under the component schema lock (and skipped in validate-only mode).

The content importer (`arcane-content-importer`) does not read `Database:Upgrade`; it has its own gate. `refresh` refuses a world whose schema is behind the importer's unless `--migrate` is given. `import`, `import-dbc`, `import-map-dbc`, `proc-events` and `spell-threat` use the same gate in its `CreateOnly` form: they still create a database that holds no schema, but refuse to upgrade an existing one (exit 3, nothing written) unless `--migrate` is given. Production worlds migrate through `arcane-db upgrade`; pass `--migrate` only for a throwaway or staging world.

Characters schema v60 (`character_ownership`) is forward-only: roll back by restoring the pre-upgrade backup. How to clear a stuck ownership row is in `docs/CHARACTER_FENCING_ACCEPTANCE.md` (Operations).
