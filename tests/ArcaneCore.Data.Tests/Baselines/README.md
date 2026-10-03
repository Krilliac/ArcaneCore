# Frozen baseline: 2026-10-03 integration candidate (auth 2 / characters 6 / world 6)

Source commit: `43f1e221f582001a1a9b29c05c1eaf935741bb15` (the head recorded in
`docs/integration/fleet-20261003.md`; nothing was released, this candidate lineage is what the handoff calls
"released"). `4f81df0` (characters 7) and `b0571e1` (world 7) are descendants of it.

| File | Content |
|---|---|
| `candidate-2-6-6.sqlite.ddl.sql` | `sqlite_master` DDL (tables, then indexes) of a database created fresh by that commit's `SchemaBootstrapper.EnsureAsync` for the auth, characters and world contexts in one SQLite file. Line endings normalised to LF. |
| `candidate-2-6-6.sqlite.data.sql` | One INSERT per non-version table: deterministic literals (INTEGER = column position, TEXT = `table.column`, REAL = position + 0.5, BLOB = `0A0B<position>`). Generated once from the DDL, then checked in; never regenerated from the current model. |

The version rows (auth 2, characters 6, world 6) are inserted by `CandidateBaseline.cs`.

## Capture method

`git archive 43f1e22 src/ArcaneCore.Data src/ArcaneCore.Kernel Directory.Build.props global.json` into a scratch
directory outside the repository, plus a throw-away console project referencing `ArcaneCore.Data` that ran
`SchemaBootstrapper.EnsureAsync` for `AuthDbContext`, `CharacterDbContext` and `WorldDbContext` on an empty SQLite
file (EF Core 9.0.0, Microsoft.EntityFrameworkCore.Sqlite 9.0.0, SDK 10.0.401), printed
`auth=2 characters=6 world=6`, then dumped `sqlite_master` and generated the INSERTs from `pragma_table_info`. 53 tables
including the three version tables.

## Variants

- `ReleasedFresh`: the DDL as captured (the candidate's fresh path created the indexes of every table at that version).
- `HistoricUpgraded`: the same, with the `CREATE INDEX` statements of every table except `account` and `characters`
  removed. That is what the historic bootstrapper produced when it upgraded a version-1 database step by step.

Only SQLite was captured: no MariaDB or PostgreSQL server was available (see `docs/integration/schema-index-repair.md`, Limits).
