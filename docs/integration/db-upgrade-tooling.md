# Database upgrade tooling (lane `db-upgrade-tooling`)

The production upgrade path: a read-only planner, a drift checker, a typed executor and refusals, a backup gate, the
`arcane-db` command line and a start-up policy. Operator view: `docs/ops/database-upgrade.md`. Base: the verified
integration branch `claude/vw-integration` (`41babaf`). Nothing here changes a schema: no table, column or version
constant was added, so no `ICharacterDataCleanup` is involved.

## Delivered

| Piece | Where | What it does |
|---|---|---|
| Shared change decider | `src/ArcaneCore.Data/Schema/Upgrade/SchemaChangeDecider.cs` | The per-change decisions (index satisfied / under another name / conflict / blocked by duplicates, table column mismatch, add-column) and the refusal texts, extracted from `SchemaBootstrapper`; **both** the bootstrapper and the planner call it, so the dry run cannot say something the apply does not do |
| Planner | `SchemaPlanner.cs`, `SchemaPlan.cs` | Read-only classification (`Missing`, `Fresh`, `Creating`, `NoVersionRow`, `AdoptV1`, `Current`, `Behind`, `Newer`, `Unknown`) and per-change plan with the tables and indexes earlier pending steps create tracked virtually; optional DDL script via `IMigrationsSqlGenerator` |
| Drift checker | `SchemaDriftChecker.cs`, `DriftReport.cs`, `SchemaCatalogInfo.cs` | Version row, tables, columns + nullability, indexes by shape, foreign tables, MariaDB engine/charset, PostgreSQL encoding; examined counts so a vacuous pass is visible |
| Executor | `SchemaUpgrader.cs`, `SchemaUpgradeOptions.cs`, `SchemaUpgradeExceptions.cs` | Pre-flight refusals before any DDL (`SchemaDowngradeException`, `SchemaBlockedException`, `SchemaPolicyException`, `SchemaActiveSessionsException`, all `SchemaMismatchException`), then the unchanged bootstrapper (one apply path) |
| Bootstrapper options | `SchemaBootstrapper.cs` | `EnsureAsync(db, definition, SchemaUpgradeOptions, ...)`: policy (checked before the database is created and again under the lock), lock timeout, per-step progress. The old overloads are unchanged and still throw exactly `SchemaMismatchException` |
| Probes | `ServerProbe.cs` | Server product/version + qualification warning, other-session count, schema-lock-held |
| Backup | `BackupAdvisor.cs` | `mysqldump` / `pg_dump` command lines (password only as `MYSQL_PWD` / `PGPASSWORD`), verified SQLite `VACUUM INTO` copy with `RepositoryPathGuard` |
| CLI | `Cli/DbUpgradeCli.cs`, `DbUpgradeArguments.cs`, `DbUpgradeExitCodes.cs`, `PlanFormatter.cs`, `tools/ArcaneCore.DbUpgrade` | `status`, `plan [--script] [--json]`, `check`, `upgrade`, `migrate-codex [--apply]`, `backup-info`; exit codes 0-8 |
| Start-up policy | `DatabaseUpgradeOptions.cs`, `DatabaseOptions.Upgrade`, the three initializers, World/Realm/AccountTool `Program.cs` | `Database:Upgrade:Policy` and `LockTimeoutSeconds`; a refusal is one scrubbed line and exit 4 or 7; `arcane-account db ...` forwards to `arcane-db` before the auth schema is initialized |

Reused from the content-import lane (on the base): `ConnectionStringRedactor`, `RepositoryPathGuard`, `UsageException`.

## Config switches

`Database:Upgrade:Policy` = `Always` (default) | `CreateOnly` | `Never`; `Database:Upgrade:LockTimeoutSeconds` (default 60).
CLI: `--component`, `--script`, `--json`, `--no-fail-on-pending`, `--confirm-backup`, `--backup-dir`,
`--allow-active-sessions`, `--lock-timeout`, host option `--config <file>`.

## Deviations from retail (and why)

Standing directive: retail behaviour, with any deviation behind a switch that defaults to retail. Here the retail behaviour
(never create or upgrade on start; refuse a database that lacks migrations) is reachable but is **not the default**.

| Deviation | ArcaneCore | Retail reference | Switch |
|---|---|---|---|
| Daemons create and upgrade on every start | `SchemaBootstrapper.EnsureAsync` from the three initializers | vmangos refuses to start when a migration is missing and never applies one: `D:\refs\vmangos\src\mangosd\Master.cpp:415-470` (`StartDB` returns `CheckRequiredMigrations`), `:482-485`; `D:\refs\vmangos\src\shared\Database\Database.cpp:587-640` (`:622-629` missing returns false); `D:\refs\mangos-classic\src\shared\Database\Database.cpp:490-498` ("You have [A], You need [B], You must apply all updates") | `Database:Upgrade:Policy`: `Never` is retail, `CreateOnly` is retail-like but still creates an empty database, **default `Always` keeps today's behaviour** (see open questions) |
| A database newer than the code is refused | `SchemaBootstrapper` (unchanged) and the upgrader | vmangos only warns about extra migrations and keeps running: `Database.cpp:631-640` | none: stricter on purpose, silently losing columns or indexes an older build does not know is unsafe |
| One integer version per component, not a set of named migrations | `SchemaVersionRow` | vmangos `migrations` table of varchar ids: `D:\refs\vmangos\sql\characters.sql:784-789` | none; `status` and `plan` print the pending steps, which is the reference's "missing migrations" list |
| Updates are applied in-process | `SchemaBootstrapper` | operator-applied idempotent SQL: `D:\refs\vmangos-wiki\docs\Database-Setup.md:46-66`, `:88` | `plan --script` prints the exact SQL, so the retail workflow still works |
| A guard against running while the server is up | `--allow-active-sessions` | "apply all migrations before starting the server": `D:\refs\vmangos-wiki\docs\Frequently-Asked-Questions.md:13` | default refuses (best effort, see limits) |
| The tool gates the apply on a backup acknowledgement | `--confirm-backup`, `BackupAdvisor` | `backup_create` before overwriting, `mysqldump --quick --single-transaction --order-by-primary` with `MYSQL_PWD` exported: `D:\refs\classic-db\InstallFullDB.sh:465`, `:2227` (the reference also passes `--compress`; omitted here) | operator flag, not a behaviour switch |
| Not atomic on MariaDB | engine property (DDL commits implicitly) | vmangos' ledger table is MyISAM: `characters.sql:789` | none |
| Connection strings never reach output | `ConnectionStringRedactor` | the password token is masked: `Master.cpp:428-452` | none |

Quantification behind the ranking (session scripts over `D:\refs`): vmangos `sql/migrations` has 1,165 files, 61 of them
(5.2 percent) with DDL (world 43 of 1,141; characters 7 of 8) and all 1,165 idempotent-guarded; classic-db `Updates` has 9 of
357 files with DDL. Structural upgrades are rare, so what matters is that each one is previewable, refusable and
re-runnable. The world tables the index repair touches hold 66k, 48k and 11k rows in the classic-db dump (creature,
gameobject, creature_ai_scripts), so the operational risk is the characters database and lock time, not world content.
No characters row-volume data exists in the references: **no index-build duration is claimed.**

## Provider evidence

| Engine | What ran in this lane | What did not |
|---|---|---|
| SQLite | every test: planner, plan-equals-reality (legacy v1 and all three frozen-baseline components), drift, executor (interruption after every sampled DDL statement, concurrent applies, policy, lock held by another connection), CLI end to end, backup copy, start-up policy through the three real initializers; the `arcane-db` and `arcane-account db` executables were run once by hand | – |
| MariaDB / PostgreSQL | refused-connection behaviour (`127.0.0.1:1`, exit 6, no password in output), offline command-line text, qualification parsing, redaction | **Everything that needs a server**: `GET_LOCK` / `pg_try_advisory_lock` visibility (`IS_USED_LOCK`, `pg_locks` with `classid`/`objid` halves of the advisory key), session counting (`information_schema.processlist`, `pg_stat_activity`), engine/charset/encoding warnings, catalog nullability on `information_schema.columns`, the non-transactional MariaDB partial apply. The theories are written over `TestDatabases.AvailableProviders` and run on hosted CI only; none of these was run here. |

The server-side queries are written from the providers' documentation. Hosted CI must prove them before anyone relies on
`ServerProbe` on a production server.

## Limits (not delivered, and why)

- **PostgreSQL single-transaction bootstrap is not implemented.** PostgreSQL DDL is transactional, so the whole bootstrap could be
  one transaction like SQLite, but it cannot be proven on this machine, and `SchemaStartupResilienceTests` and
  `SchemaBootstrapGuardsTests` encode per-provider partial-state expectations that would have to change with it. Left for a
  lane with a PostgreSQL server (slice S8 of the design; the switch was to be `Database:Upgrade:PostgresAtomic`, default off).
- **The default policy is still `Always`.** Flipping it to `CreateOnly` breaks every restart against a database that is behind
  (and every other lane bumping a schema version); that is an integrator decision after the lanes merge.
- **Column types and defaults are not compared by the drift check** (EF store types and catalog type names are not portably
  comparable): a database whose column types drifted still reports clean.
- **Active-session detection is best effort** (invisible sessions of other roles; undetectable on SQLite). Stop the daemons.
- **The backup gate is an acknowledgement**, not proof; only the SQLite copy is verified.
- No per-upgrade history ledger, no staged `--to N` upgrade, no downgrade, no automatic repair of duplicate rows (never deletes).
- No content migration: that belongs to the importer lane.
- `status` and `plan` against MariaDB/MySQL cannot enforce read-only at the connection level (SQLite is opened read-only);
  they only issue reads.
- Seeding after `EnsureAsync` is still outside the lock (`schema-index-repair.md`).
- Process-level tests of World and Realm exit codes were not written (the executables are not referenced by test projects);
  `DatabaseStartup.InitializeAsync`, which both call, is tested in-process.

## Open questions for the developer

1. Default of `Database:Upgrade:Policy`: `Always` today. Is the pre-release status enough to flip it to `CreateOnly`?
2. Keep refusing a database newer than the code with no override (stricter than vmangos)?
3. Should the tool also support a staged `--to N` target for very large characters databases?
4. Do you want a per-upgrade history ledger like vmangos' `migrations` table? It needs a table outside the three component schemas.
5. Which database role will operators use? A least-privilege role may not see other sessions.
6. If the ops lane restructures `tools/ArcaneCore.AccountTool`, which tool name should carry the `db` verb? `arcane-db` stands alone either way.
7. Should `arcane-db` refuse, not only warn, on MySQL 8 until CI runs a `mysql:8` service?

## Tests

`tests/ArcaneCore.Data.Tests/Upgrade/`: `SchemaPlannerTests`, `SchemaDriftTests`, `BackupAdvisorTests`, `SchemaUpgraderTests`,
`ServerProbeTests`, `DbUpgradeCliTests`, `StartupPolicyTests`, `DocsConsistencyTests` (the runbook must name every exit code,
command, option and `Database:Upgrade` key). Red-first was proven by mutation: with the planner's change evaluation, the drift
checker's index/nullability checks and the policy gate each disabled in turn, the corresponding tests fail.

## Provenance

New files only apart from: `SchemaBootstrapper.cs` (decisions routed through `SchemaChangeDecider`, the options overload, the
now unsealed `SchemaMismatchException` with a `Reason`), `SchemaCatalog.cs` (two helpers made `internal`), `SchemaLock.cs`
(the timeout carries `SchemaMismatchReason.LockTimeout`), `DatabaseOptions.cs` (one property), the three initializers (one
line each), `ArcaneCore.World/Program.cs`, `ArcaneCore.Realm/Program.cs`, `tools/ArcaneCore.AccountTool/Program.cs`,
`ArcaneCore.slnx` (one project line), `ArcaneCore.Data.Tests.csproj` (the hosting package for configuration in tests), `README.md`
and `schema-index-repair.md` (Limits only). No code or data from the references was copied.
