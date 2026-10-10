# Contributing

The rules for changing ArcaneCore. The binding statement of intent is the [charter](../../ARCANECORE_CHARTER.md); this page is the practical side of it.
The sections that name commands, types and variables are checked against the code by tests, so they cannot drift.

## The prime directives

From charter section 1, in short (read the charter for the full text):

1. **Ground truth over generation.** Verify every protocol detail against a real reference before writing it, and cite which reference confirmed it.
2. **Incremental and gated.** One milestone at a time; no scaffolding of later milestones.
3. **Verified against a real client.** A milestone is only done when it passes against an actual 1.12.1 (5875) client. Automated tests alone do not make an area "verified".
4. **Complete within scope, absent outside it.** No TODOs, stubs or placeholders in delivered scope; what is not delivered is written down as a limit.
5. **No invented API surface.** Never fabricate a method, packet field, opcode number or DBC column; read a reference or ask.

The standing direction on top of the charter: match retail 1.12.1 as closely as possible, mechanics and data, verified against the real references. A deliberate deviation
sits behind a configuration option that **defaults to retail**, and its option summary says what retail does.

## References and citations

The reference projects (vmangos first, then mangos-classic, wow_messages for packet and update-field layouts, classic-db for data, the vmangos wiki) are GPL and read-only: use them to verify, re-express
the behaviour in your own code, and never copy their code or data into this repository. Cite the file and line next to every formula, table and packet you add. The
[repo hygiene guard](#repository-hygiene) enforces the data half of that rule.

## Layout

- `src/ArcaneCore.Kernel`, `Protocol`, `Cryptography`, `Data`, `Game` (the world simulation), `World` (the world daemon: handlers, features, commands) and `Realm` (the logon daemon).
- `tests/` has one project per source project, plus `ArcaneCore.MockClient.Tests` and `hotmodule-fixtures`. `tools/` holds the command-line tools. `docs/` is this documentation.
- Prefer new files and narrow, additive edits to shared files (spell system, player, inventory, quest NPC, the world runtime and session). Integration notes for a feature go in `docs/integration/`.

## Build and test

CI runs exactly these commands (`.github/workflows/ci.yml`; a test compares this list with the workflow):

```
dotnet restore ArcaneCore.slnx
dotnet build ArcaneCore.slnx -c Release --no-restore
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity normal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

CI splits the test step into a matrix (`test (Game)`, `test (World)`, `test (Data)`, `test (Rest)`): each job runs the same `dotnet test`
line on one solution filter of `ArcaneCore.slnx` (`tests/ci/<suite>.slnf`), and the filters together cover every test project exactly once
(a test checks this, so a new test project must be added to one of them). The mock-client self-test runs in the `Rest` job.

Warnings are errors, but through `Directory.Build.props` (`TreatWarningsAsErrors`), not a command-line flag, so a warning anywhere fails the build locally and in CI. Test projects run serially
(`-m:1`) because first-time database fixture start-up has a bounded deadline. A count of 0 tests, or a run that prints no summary line, means the check did not run.

## Seams: how a feature plugs in

Features plug in without editing shared registration files. Discovery is reflection over one assembly (non-abstract classes with a parameterless constructor, ordered by type name; a duplicate
fails at startup). The seam table is [docs/integration/seams.md](../integration/seams.md). The interfaces:

| Need | Interface |
|---|---|
| Opcode handlers | `IOpcodeHandlerGroup` |
| A world service with a lifecycle | `IWorldFeature` |
| Chat types the core does not serve | `IChatMessageHandler` |
| GM and player chat commands | `ICommandGroup`, and `ICommandExtension` to add sub-commands to another feature's root |
| Database tables | `IDataModule` |
| Per-character setup | `ICharacterHooks` |
| Character deletion | `ICharacterDataCleanup` (stored rows) and `ICharacterDeleteHook` (live state) |
| Per-map simulation | `IMapUpdater` |
| Test doubles in the end-to-end host | `IWorldTestServices` |

## Schema modules

- A component's schema versions are one contiguous sequence starting at 2 (a gap or a duplicate fails at start). The integrator assigns the final number at merge time, so take the next free number in your own tree.
- Keep the version in **one named constant**. Tests refer to that constant or to `<Context>.Schema.CurrentVersion`, never to a literal.
- Every characters module implements or registers an `ICharacterDataCleanup`, so deleting a character removes its rows ([schema reference](../reference/schema.md) shows which modules do).
- Schema steps are additive (`CreateTableChange`, `AddColumnChange`); a `CreateTableChange` also creates the indexes the model declares.

## Tests that touch a database

The MariaDB and PostgreSQL provider tests run **only on the hosted CI**, which starts both servers and sets `ARCANECORE_TEST_MARIADB` and `ARCANECORE_TEST_POSTGRES`; a developer machine normally has
neither, so only SQLite runs locally. A store or schema change therefore needs provider-aware theories (`TestDatabases.AvailableProviders`) written against the real provider semantics, because
a green local run proves nothing about them:

- MariaDB DDL is not transactional and implicitly commits; PostgreSQL DDL is transactional.
- Npgsql pooling returns the same physical connection, so an advisory lock taken twice on it is re-entrant.
- Identifier quoting and case folding differ between the engines.

Say in your notes when a store was only exercised on SQLite.

## Tests must not flake

Do not depend on wall-clock timing, or on a single packet arriving within a fixed window under load. Wait on a condition with a generous deadline, assert invariants rather than exact timings, and run a
timing-sensitive test repeatedly before calling it green.

## Repository hygiene

Client data never belongs in the repository. The guard in `tests/ArcaneCore.Game.Tests/Hygiene` fails the build when a tracked file has a client-data or extraction-output extension (`.dbc`, `.mpq`, `.map`,
`.vmtree`, `.vmtile`, `.vmo`, `.mmtile`, `.mmap`, `.adt`, `.wdt`, `.wmo`, `.m2`, `.blp`), starts with a client-data file header (a DBC, an extracted terrain tile, vmap or navmesh data), sits under `data/` or `local-data/`, carries a SQL dump banner, or is a SQL file over 64 KiB outside `tests/**/Baselines/`. Content is imported from your own dumps with
`arcane-content-importer` into a database outside the work tree.

## Documentation

The pages under `docs/reference/` are generated from the code by the tests in `tests/ArcaneCore.World.Tests/Docs`. When you add an option, a command or a schema step, the matching test fails and names the
page. Regenerate, then review the diff as part of your change:

```
ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs
```

- A new options class is found through its `SectionName` constant. A section the catalog cannot reach needs a row in the docs exception table, and an option with no `///` summary needs a documentation override
  (a failing test says which). Write the summary, with the retail reference, next to the option instead.
- Hand-written pages (the [installation](installation.md) and [operations](operations.md) guides) are checked mechanically: every configuration key, repository path, tool command and flag they name must exist. A
  key you document before it exists will fail `DocKeyAuditTests` until it is added to its allow-list with a reason.
- Add an area page under `docs/areas/` for a new area (what is delivered, its limits, its references) and link it from [the docs index](../README.md).
