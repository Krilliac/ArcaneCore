# Schema: forward index repair and upgrade parity

Branch `claude/ac-3-index-repair`, from `c3dea16` (handoff item 3). Allocates **Characters 11** and
**World 9**. Auth stays 2. In the integrated tree the repair is not the top step: modules take Characters 12 (deletion
outcome recovery) and 13 (durable loot state) and World 10 (quest reputation rewards) after it. That is safe because a
module's `CreateTableChange` creates the table together with its model indexes, and the repair names only tables that
exist before it. `SchemaBootstrapGuardsTests` and `IntegratedSchemaTests` no longer require the repair to be last.

## What was wrong

`SchemaBootstrapper` brought a database to the current version by asking EF's model differ
for the operations that create the whole model and keeping only the `CreateTableOperation`s. EF
emits `CreateIndexOperation` separately, so **every index of a table created by an upgrade step was
dropped** (`SchemaBootstrapper.ExecuteAsync`, `all.OfType<CreateTableOperation>()`). A fresh database
used `CreateTablesAsync`, which creates indexes, so fresh and upgraded databases differed. Missing on
an upgraded database, among others: the **unique** `guild_member(CharacterId)` (one character could be
in two guilds) and the **unique** `auction(item_guid)`, plus the owner, spawn, instance, mail and AI
script indexes. Even a database created fresh by the 2/6/6 integration candidate got no index of
characters 7-10 / world 7-8 when later code upgraded it.

Related weaknesses the handoff asked to cover: a step interrupted after its first `CREATE TABLE` failed
on every later start ("table already exists"); a fresh create interrupted before the version row left a
database that failed closed forever; two processes starting together (the realm and the world daemon
both bootstrap auth) could both create tables or both insert the version row.

## What changed

All in `src/ArcaneCore.Data/Schema/` unless noted.

**A. Upgrades create the indexes their tables map.**
- `CreateTableChange` now creates the table *and* the indexes the model declares on it.
- `EnsureIndexesChange(table)` (new) creates the model's missing indexes on an existing table. It is the
  forward repair for tables created by the old bootstrapper.
- An index is satisfied by any existing index with the same ordered columns and uniqueness, whatever its
  name. A same-named index of another shape fails closed (`SchemaMismatchException`) and is not replaced.
- Before creating a **unique** index the bootstrapper counts duplicate groups (`GROUP BY ... HAVING COUNT(*) > 1`,
  rows with a NULL key column ignored). Duplicates fail closed with a message naming the table, the index
  and the number of groups. **No row is ever deleted or merged.**
- `SchemaCatalog` (new, public) holds the catalog reads (`TableExistsAsync`, `ColumnExistsAsync`, `ReadColumnsAsync`,
  `ReadIndexesAsync` returning `CatalogIndex(Name, IsUnique, Columns)`) for SQLite, Pomelo (MariaDB/MySQL) and
  Npgsql. They are raw ADO commands that join the context's current transaction, so EF command interceptors
  do not see them.

**B. Idempotent, serialized, resumable startup.**
- Changes run one statement at a time, each after a catalog check. `CreateTableChange` on an existing table keeps
  it if its columns are exactly the model's, ignoring columns a *later* step adds. A table with missing or unexpected
  columns is not adopted (`SchemaMismatchException`): that is another component's table or damage.
- `AddColumnChange` was already idempotent. Re-running an interrupted step therefore converges, which matters
  on MariaDB where DDL is not transactional.
- The version row may be `0` (`SchemaBootstrapper.CreatingVersion`): "a fresh create is in progress". A fresh
  create writes `0`, creates every table and index, then writes the current version. A start that finds `0` resumes.
  A build that predates this sees `0` and finds no upgrade path (fails closed).
- A version table with **no row** is resumed when none, or all, of the component's other tables exist. "All" is exactly
  what the previous bootstrapper left if it died between creating the tables and writing the version. Anything else
  keeps the old fail-closed message.
- Database creation tolerates another process creating it first.
- A per-component lock is held for the whole bootstrap and the version is read only after the lock is
  taken, so a process that waited finds the work done and issues no DDL.

| Provider | Lock | Notes |
|---|---|---|
| SQLite | `BEGIN IMMEDIATE` kept for the whole bootstrap, committed at the end | Excludes every other writer of the file. The upgrade is **atomic**: a refused or crashed bootstrap leaves the database exactly as it was (version row included). |
| MariaDB / MySQL | `GET_LOCK(SHA1(CONCAT('arcanecore_schema:', DATABASE(), ':<component>')), 0)`, polled every 250 ms, `RELEASE_LOCK` in finally | Session lock on the connection kept open for the call. |
| PostgreSQL | `pg_try_advisory_lock(key)`, polled, `pg_advisory_unlock` in finally; `key` = `SchemaBootstrapper.AdvisoryLockKey(component)` (64-bit FNV-1a of `arcanecore_schema:<component>`) | Visible in `pg_locks`. Per database. |

The wait is bounded (`SchemaBootstrapper.DefaultLockTimeout`, 60 s; the `EnsureAsync(db, definition, lockTimeout, ...)`
overload takes another). On expiry the start fails with `SchemaMismatchException` ("timed out ... waiting for the
<component> schema lock"), not with a provider command timeout. Locks are per component, so Auth, Characters
and World sharing one database do not block one another (SQLite excepted: one writer per file).

The fresh create also runs the engine's database-level operation the model differ yields (Pomelo:
`ALTER DATABASE CHARACTER SET utf8mb4`), as EF's `CreateTables` did; it is idempotent. Any other operation
type fails loudly instead of being dropped.

**C. Forward repair versions (inline steps, no new tables or columns).**

| Database | Version | Constant | Repairs |
|---|---|---|---|
| characters | **11** | `CharacterDbContext.IndexRepairVersion` | `characters`, `item_instance` (owner_guid), `character_inventory` (guid), `guild_member` (**unique** CharacterId), `instance` (MapId), `character_instance` (InstanceId), `mail` (receiver, expire, item), `auction` (**unique** item_guid, seller, expire) |
| world | **9** | `WorldDbContext.IndexRepairVersion` | `creature_spawn` (MapId), `gameobject_spawn` (MapId), `creature_ai_scripts` (CreatureId) |
| auth | 2 (unchanged) | none | Its only index, `account.Username`, is a version-1 index present in every lineage. |

The steps are inline in the contexts, not a module: a characters `IDataModule` must implement
`ICharacterDataCleanup` (guard in `CharacterDeletionTests`), and this step owns no rows. The lists are explicit
(frozen meaning), not "every table". A guard test fails if a model index exists on a table created after
version 1 that the repair does not name.

**Renumbering.** The lead assigns numbers at merge time (`seams.md`). If another branch takes
Characters 11 or World 9 first, change the one constant; tests use the constants and `Schema.CurrentVersion`
(`IntegratedSchemaTests` builds its step lists from them).

## Operator guide: duplicate rows

If a live database already holds duplicate `guild_member.CharacterId` or `auction.item_guid` rows, the
upgrade to Characters 11 refuses to start and names the table and index. On SQLite the database is untouched
(version row included). On MariaDB/PostgreSQL the version stays at the last completed step (10). Nothing is deleted.

1. Back up the database.
2. Find the duplicates, for example `SELECT CharacterId, COUNT(*) FROM guild_member GROUP BY CharacterId HAVING COUNT(*) > 1`
   (`SELECT item_guid, COUNT(*) FROM auction GROUP BY item_guid HAVING COUNT(*) > 1`).
3. Decide per group which row is true and delete the others yourself.
4. Start again; the repair resumes and creates the index.

## Delivered scope and evidence

All tests are in `tests/ArcaneCore.Data.Tests` (xunit). SQLite runs everywhere; MariaDB and PostgreSQL run
where `ARCANECORE_TEST_MARIADB` / `ARCANECORE_TEST_POSTGRES` are set (hosted CI sets them).

- `SchemaIndexParityTests`: a legacy v1 database upgraded step by step has exactly the indexes of a fresh database
  and of the EF model; a populated frozen baseline upgrades with every row preserved and model index and column parity;
  the repaired database enforces one guild per character and one auction per item; duplicates fail closed with
  rows kept; table names are distinct across components.
- `SchemaStartupResilienceTests`: an interruption after every DDL statement of every upgrade step and of a fresh create
  (and before the version write) resumes on restart; the database the previous bootstrapper left (tables, empty
  version table) resumes; two concurrent starts on a fresh database and on the populated baseline serialize (exactly one
  issues DDL); repeated starts issue no DDL; a complete database whose version row is behind converges without losing rows.
- `SchemaBootstrapGuardsTests`: lock timeout fails closed at the deadline and releases; another component's lock does not
  block; index under another name satisfies; same-name index of another shape and a foreign or extra-column table are
  refused; the production catalog reader equals an independently written test reader for every table; the model
  differ yields only the operations the bootstrapper handles, **for Pomelo (MariaDB 10.11 and MySQL 8.0) and Npgsql
  too, generated offline without a server**.
- `IntegratedSchemaTests` now builds its allocation checks from the constants and asserts index parity after every prefix step.

### Frozen baseline

`tests/ArcaneCore.Data.Tests/Baselines/` holds the DDL and rows of the 2026-10-03 integration candidate lineage
(auth 2 / characters 6 / world 6), captured at `43f1e221f582001a1a9b29c05c1eaf935741bb15` (the head that
`fleet-20261003.md` records; characters 7 and world 7 were first added by `4f81df0` and `b0571e1`, which are
descendants). Nothing was released; "released" in the handoff means this candidate lineage. See
`Baselines/README.md` for the capture method. Two variants: the candidate's fresh database (`ReleasedFresh`) and a
database upgraded by the historic bootstrapper (`HistoricUpgraded`: no index of a table created after version 1).
Every table has at least one row (the test asserts it).

## Provider tests: how to run locally

Several schema tests (the schema lock, interrupted and concurrent startup, index parity) only run
against MariaDB and PostgreSQL when a server is configured; otherwise they run against SQLite only
and say nothing about the other engines. This is how the schema-bootstrap work first reached `main`
with 35 failing provider tests that no local run had exercised.

- `ARCANECORE_TEST_MARIADB`: a MariaDB/MySQL connection string without a database name, for example
  `Server=127.0.0.1;Port=3306;User=root;Password=arcane;`
- `ARCANECORE_TEST_POSTGRES`: a PostgreSQL connection string without a database name, for example
  `Host=127.0.0.1;Port=5432;Username=arcane;Password=arcane;`

Each test creates and drops its own throw-away database (`arcane_t_<guid>`), so the account needs
`CREATE DATABASE` rights. A provider whose variable is unset is silently left out of the theories.

CI (`.github/workflows/ci.yml`) runs both as service containers: `mariadb:10.11` (root / `arcane`)
and `postgres:16` (user `arcane` / `arcane`). To match it locally:

```
docker run -d --name ac-mariadb -e MARIADB_ROOT_PASSWORD=arcane -p 3306:3306 mariadb:10.11
docker run -d --name ac-postgres -e POSTGRES_USER=arcane -e POSTGRES_PASSWORD=arcane -p 5432:5432 postgres:16
```

Provider semantics the tests must respect: MariaDB DDL is not transactional (implicit commit) while
PostgreSQL DDL is; `GET_LOCK` locks belong to one connection; `pg_advisory_lock` returns void and is
re-entrant within a session (use `pg_try_advisory_lock` to learn whether a lock was taken, and take
a "held elsewhere" lock on an unpooled connection); `CREATE DATABASE` is issued by the bootstrapper
before it holds a connection to the database.

Provider-gated tests must be run locally, or the CI run watched to green, before schema work is merged.
A SQLite-only pass is not evidence for the other two engines.
## Limits (not delivered, and why)

- **MariaDB and PostgreSQL were not run on this machine** (no server, no `ARCANECORE_TEST_*` variables). Their lock SQL
  (`GET_LOCK`, `pg_try_advisory_lock`), their catalog queries (`information_schema.statistics`,
  `pg_index`/`pg_attribute` with `unnest ... WITH ORDINALITY`) and the non-transactional resume of interrupted DDL are
  written for them and covered by the same provider-parametrized tests, but **proof comes only from hosted CI**.
  What *is* proved offline for them: the differ operation set and the generated DDL (see above).
- **Actual MySQL 8 server support remains unqualified**, separately from MariaDB, as the handoff says.
- **The frozen baseline is SQLite only.** No per-provider `mysqldump`/`pg_dump` capture was possible without servers, so
  populated-baseline upgrade, concurrency-on-baseline and duplicate-row tests run on SQLite. Intermediate lineages
  (characters 7/8/9, world 7) are covered through every contiguous step of the stepwise tests, not through frozen DDL.
- **Index repair is explicit, not automatic drift detection.** A database that is already at the current version is
  not scanned for missing indexes at startup, and an index an external DBA drops later is not noticed.
- **Seeding is outside the lock.** The realm and content seeding after `EnsureAsync` (`AuthDbInitializer`
  realm rows, `WorldDbInitializer` `player_create_info`) is a check-then-insert and can still race between two
  daemons; this change serializes the schema only.
- **Fresh-create atomicity** is weaker on PostgreSQL than before (it was one transaction inside `CreateTablesAsync`);
  it is now resumable through the `0` marker instead. SQLite stays atomic.
- **Older binaries** that meet version `0` fail closed; run the new build to finish the create.
- The SQLite lock is the file's write lock, so it also blocks other SQLite writers of the file for the bootstrap's
  duration (a few milliseconds when nothing changes).
- A stale `GET_LOCK`/advisory lock from a crashed process disappears with its session; a hung but live holder makes
  the next start time out and fail closed after the deadline.

## Provenance and shared-file edits

New: `SchemaCatalog.cs`, `SchemaLock.cs`, the four test files and `CandidateBaseline.cs`, `Baselines/*`.
Edited (shared): `SchemaBootstrapper.cs` (rewritten around the changes above; public `EnsureAsync(db, definition, logger, ct)`
unchanged and a lock-timeout overload added), `CharacterDbContext.cs`, `WorldDbContext.cs` (one inline step and one
constant each), `IntegratedSchemaTests.cs`, `seams.md`, `takeover-20261003.md`.
