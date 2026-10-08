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

```text
1. stop realm, world and every tool
2. back up each database          (arcane-db backup-info prints the command; SQLite: --backup-dir)
3. arcane-db status               versions of every component; exit 3 means an upgrade is pending
4. arcane-db plan                 read-only dry run: what would be created, changed or refused
5. arcane-db upgrade --confirm-backup
6. arcane-db check                the same drift check that upgrade ran at its end
7. start the new binaries
```

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
(`D:\refs\vmangos-wiki\docs\Database-Setup.md:46-66`, `:88`). The script does not create the database or set its
character set. Run `arcane-db check` afterwards.

For a database the Codex line created (below) the script also carries the migration's row moves, each where the
migration runs it: the `UPDATE` that copies `npc_template_service_metadata` into `creature_template` follows the
`ALTER TABLE` statements of world step 21 and precedes the version write, so the script ends the database exactly as
`upgrade` would. A plan holding a row move that cannot be written as SQL gets no script at all (only `--` comments,
an error naming the move, exit code 4): a script that skipped the move would still record the merged version, after
which the database reads as already migrated and the rows would never move. Use `migrate-codex --apply` then.

## Exit codes

| Code | Meaning | Changed anything? |
|---|---|---|
| 0 | ok: current, upgraded, or drift clean | upgrade: yes |
| 1 | unexpected failure (a bug, or an input the tool did not anticipate) | maybe: read the message |
| 2 | usage or configuration: unknown command or option, missing value, no connection string | no |
| 3 | `status`, `plan`: an upgrade is pending (a normal report; `--no-fail-on-pending` makes it 0 for `set -e` scripts) | no |
| 4 | refused: database newer than the code, unknown state, blocker (duplicate rows, conflicting index, mismatching table), other sessions connected | no |
| 5 | drift: the database differs from the model (`check`, or the check at the end of `upgrade`); for `dbc`, a DBC with another layout or a world id no DBC row has | no |
| 6 | the database server could not be reached or refused the connection (message scrubbed) | no |
| 7 | the wait for another process's schema lock ran out | no |
| 8 | `upgrade` with pending steps and no confirmed backup, or a backup that could not be made | no |

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
| `dbc` | read-only client data check: every file of the DBC directory against its vmangos layout, then the world database's spell, map, area, faction, display ... ids that no DBC row has (docs/areas/client-data.md); `--json` |

| Option | Applies to | Meaning |
|---|---|---|
| `--component auth\|characters\|world\|all` | all commands | which component(s); default all |
| `--script` | plan | print the SQL |
| `--json` | status, plan, check, dbc | machine-readable output |
| `--no-fail-on-pending` | status, plan | exit 0 when only an upgrade is pending |
| `--apply` | migrate-codex | migrate; without it `migrate-codex` is a read-only dry run |
| `--confirm-backup` | upgrade, migrate-codex | you hold a backup of every database that has pending steps |
| `--backup-dir <directory>` | upgrade, migrate-codex | also write a verified copy of each SQLite database there (server engines still need `--confirm-backup`) |
| `--allow-active-sessions` | upgrade, migrate-codex | do not refuse when other sessions are connected to the database |
| `--lock-timeout <seconds>` | upgrade, migrate-codex | wait for another process's schema lock (default 60) |
| `--dbc-dir <directory>` | dbc | the build-5875 DBC directory; default `ClientData:DbcDirectory` of the configuration |

`status`, `plan`, `check` and `migrate-codex` without `--apply` open SQLite read-only and never create a file. All
connections are unpooled so the session count only sees other processes. On a failure in one component the later ones
are not run and the message names it.

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
(`D:\refs\vmangos\src\mangosd\Master.cpp:415-470`, `D:\refs\vmangos\src\shared\Database\Database.cpp:587-640`,
`D:\refs\mangos-classic\src\shared\Database\Database.cpp:490-498`). To get that behaviour in production set:

| Key | Values | Default | Meaning |
|---|---|---|---|
| `Database:Upgrade:Policy` | `Always`, `CreateOnly`, `Never` | `Always` | `Always`: create and upgrade. `CreateOnly`: create an empty database, resume an interrupted create, **refuse to adopt or upgrade** an existing one. `Never`: verify only, create nothing, not even a missing database (the retail behaviour) |
| `Database:Upgrade:LockTimeoutSeconds` | 1 to 86400 | 60 | how long a start waits for another process's schema work |

A refused start prints one line (`database schema refused: ...`, passwords removed) and exits 4 (7 for a lock timeout)
instead of crashing. Even `CreateOnly` creates a missing database and a fresh schema, which retail never does; `Never`
plus `arcane-db upgrade` is the retail-like pair. Everything else on a start (seeding, for example) is unchanged.
