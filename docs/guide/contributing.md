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

- `src/ArcaneCore.Kernel`, `Protocol`, `Cryptography`, `Data`, `Game` (the world simulation), `World` (the world daemon:
  handlers, features, commands), `World.Contracts` (the versioned feature and bot interfaces the World and the bot pack
  share), `Playerbots` (the bot pack, its own assembly), `Gateway` (the client socket daemon in front of a worker-mode
  World) and `Realm` (the logon daemon).
- `tests/` has a project for each source project except `Protocol` (covered by the Game and World tests),
  `World.Contracts` and `Playerbots` (covered by the World tests), plus `ArcaneCore.MockClient.Tests`,
  `ArcaneCore.Data.ProcessRunner` (a helper process the Data tests start), `ArcaneCore.TestSupport` (shared fixtures, doubles and
  the repository locator; a library, not a test project), `ThirdParty` (imported DotRecast and
  wow_messages sources the tests build against), `hotmodule-fixtures` and `ci` (the CI solution filters). `tools/`
  holds the command-line tools. `docs/` is this documentation.
- Prefer new files and narrow, additive edits to shared files (spell system, player, inventory, quest NPC, the world runtime and session). Integration notes for a feature go in `docs/integration/`.

## Build and test

CI runs exactly these commands (`.github/workflows/ci.yml`; a test compares this list with the workflow):

```
dotnet restore ArcaneCore.slnx --locked-mode
dotnet build ArcaneCore.slnx -c Release --no-restore
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity normal --filter "Category!=Timing" --logger trx --results-directory TestResults
bash tools/ops/ci/test-stages.sh --stage timing --logs timing-logs ArcaneCore.slnx
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

CI splits the test step into a matrix (`test (Game)`, `test (World)`, `test (Data)`, `test (Rest)`): each job runs the same `dotnet test`
line on one solution filter of `ArcaneCore.slnx` (`tests/ci/<suite>.slnf`), and the filters together cover every test project exactly once
(a test checks this, so a new test project must be added to one of them). The mock-client self-test runs in the `Rest` job. Two more jobs, `test (Game, spell batching)` and `test (World, spell batching)`, run those suites again with `ARCANECORE_TEST_SPELL_BATCH_MS=400`, which turns spell batching on for the whole run (`SpellBatchingTestSwitch`); set the same variable locally to reproduce them.

Warnings are errors, but through `Directory.Build.props` (`TreatWarningsAsErrors`), not a command-line flag, so a warning anywhere fails the build locally and in CI. That includes broken doc comments: XML documentation is generated so the compiler resolves them (the `.xml` files stay in `obj`, out of build, publish and deploy output), so a `<see cref>` that no longer resolves (CS1574) or a `<paramref>` naming a parameter that is gone (CS1734) fails the build; when you rename or move a member, fix the comments that name it. Test projects run serially
(`-m:1`) because first-time database fixture start-up has a bounded deadline. The `.trx` files land in `TestResults/` (ignored by git); CI uploads them and `test-report.yml` posts the summary on the PR. A count of 0 tests, or a run that prints no summary line, means the check did not run.

#### Timing-sensitive tests

A test whose outcome depends on wall-clock time or on scheduling under load (a real timeout, a `Stopwatch` bound, a wait for a packet
or a background thread) carries `[Trait("Category", "Timing")]`, on the method or, when every test in it is like that, on the class.
The test step leaves those out (`Category!=Timing`), and the next step (`tools/ops/ci/test-stages.sh --stage timing`) runs them on their
own, serially, with no parallel test collections. A timing test that fails gets exactly one isolated rerun: if it passes, the step passes
but prints `FLAKY (passed on rerun): <test>` and records it in `timing-logs/flake-ledger.txt` (uploaded as the `timing-gate-*`
artifact); if it fails again, the step fails. A timing stage that runs 0 tests also fails, so a lost tag or a broken filter cannot read
green. A flaky test is still a bug to fix, not a pass: the ledger is the list to work from. Node CI (`tools/ops/ci/node-ci.ps1`) runs the
same two stages with the same script.

### Package lock files

Every project has a committed `packages.lock.json` beside its `.csproj` (`Directory.Build.props` sets `RestorePackagesWithLockFile`):
the exact version and content hash of every direct and transitive package. CI (`ci.yml`, `dotnet.yml`), node CI
(`tools/ops/ci/node-ci.ps1`), the local full gate (`tools/ops/ci/full-suite-gate.ps1`), the image build
(`apps/docker/Dockerfile`) and the deploy build (`tools/ops/deploy/build-integrated.sh`) restore with `--locked-mode`, so a restore
whose package graph differs from the lock files fails (NU1004) instead of quietly resolving something new. A test
(`PackageLockFileTests`) checks that every project has one and that every scripted restore is locked.

When you add, remove or bump a `PackageReference` (or a project reference that brings packages with it), refresh the lock files with
the repository SDK and commit every changed `packages.lock.json` in the same commit as the `.csproj` change:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore ArcaneCore.slnx --force-evaluate
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore tools/hotmodule-template/GmCommands/GmCommands.csproj --force-evaluate
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore tools/ops/diag/exception-probe/ArcaneCore.ExceptionProbe.csproj --force-evaluate
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore tools/ops/diag/alloc-probe/ArcaneCore.AllocProbe.csproj --force-evaluate
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore ArcaneCore.slnx --locked-mode
```

The solution restore also covers the hot-module fixtures (`tests/hotmodule-fixtures`, reached through the World test project's
references) and the projects under `tests/ThirdParty` (reached through the Game, World and MockClient test projects'
references); each tool with a lock file that is outside the solution and referenced by nothing in it needs its own line, and `PackageLockFileTests` checks
that the recipe lists exactly those. A change in a library such as `ArcaneCore.Data` rewrites the lock file of every project that
references it, so expect several files to change. The last line repeats CI's check. A new project gets its lock file from its
first restore; commit it with the project. Dependabot's NuGet pull requests update the lock files themselves.
`tools/hotcode-spike` opts out of the repository props (its own `Directory.Build.props`) and has no lock file.
Package versions live in `Directory.Packages.props` (central package management): a `PackageReference` carries no `Version`, and a bump is one line there. `tools/hotcode-spike`, `tests/hotmodule-fixtures`, `tests/ThirdParty` and the `tools/ops/diag` probes set `ManagePackageVersionsCentrally` to false in their own `.csproj`; the probes keep their versions inline.

### Other checks on a pull request

Besides the build and tests, these run on their own and report on the PR:

| Workflow | What it reports |
|---|---|
| `codeql.yml` | CodeQL code scanning (C#, Python, the workflows; `security-extended` queries). New alerts are annotated on the changed lines; the alert list is in the Security tab. Also weekly on `main`. |
| `devskim.yml` | Microsoft DevSkim security lint over the whole tree (weak crypto, hard-coded secrets, unsafe APIs), uploaded to code scanning. Reviewed false positives are filtered out before the upload, each scoped to its rule and files and explained in the workflow; a new finding still shows. Also weekly on `main`. |
| `codacy.yml` | Codacy's security tools over the whole tree, uploaded to code scanning; uses the `CODACY_PROJECT_TOKEN` secret when it is set, else the tools' default configurations. Also weekly on `main`. |
| `dotnet.yml` | A Release build of the solution on the CI SDK, without the tests (those run in CI's matrix). |
| `test-report.yml` | A **Test results** check and PR comment built from CI's `.trx` files: totals, each failure with its message, and the change against the base commit. |
| `dependency-review.yml` | New or changed package references and their known advisories; a new dependency with a high or critical advisory fails the check, with a summary comment. |
| `workflow-security.yml` | zizmor over `.github/` (template injection, token permissions, unpinned actions), when a PR touches workflows. |
| `scorecard.yml` | OpenSSF Scorecard of the repository's supply-chain posture, on `main` and weekly. |
| `claude-code-review.yml` | A Claude code review of each non-draft PR from this repository, as inline comments on the changed lines (skipped for a fork PR, which gets no secrets). |
| `claude.yml` | `@claude` in an issue, PR comment or review asks Claude to answer or make the change; only someone with write access can summon it. |

Third-party actions are pinned to a commit SHA with the version in a comment; `.github/dependabot.yml` bumps them, and the NuGet
packages, weekly in grouped PRs (EF Core and Microsoft.Extensions majors are held back: see `Directory.Build.props`).

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
- **Every schema step ships its SQL update files in the same PR.** After adding or renumbering a step run `ARCANECORE_UPDATE_SQL=1 dotnet test tests/ArcaneCore.Data.Tests --filter SqlUpdateGoldenTests` (or `arcane-db export-updates --out sql/updates`) and commit the new `sql/updates/<component>/<provider>/NNNN_*.sql` files and the changed `schema-snapshot.json` ([sql/updates/README.md](../../sql/updates/README.md)). Only additive changes can be exported; released files are never edited.

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
