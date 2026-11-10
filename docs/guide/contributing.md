# Contributing

The rules for changing ArcaneCore. This page is the binding statement of the project's working rules (it took over from the build charter, see [Retired rules](#retired-rules)), and it is also the practical side:
build and test commands, seams, schema modules and hygiene. The sections that name commands, types and variables are checked against the code by tests, so they cannot drift.

## The prime directives

These are non-negotiable and override anything else on this page.

1. **Ground truth over generation.** Verify every protocol detail (an opcode value, a struct layout, a field order, a hash construction) against a real reference before writing it, and cite in a comment *which* reference confirmed it.
   If it cannot be verified, stop and ask; never guess. When two references disagree, prefer the one a real 1.12.1 client agrees with, and note the discrepancy in a comment (the recorded cases are in
   [protocol decisions](../areas/protocol-decisions.md)).
2. **Verified against a real client.** Automated tests make an area *implemented*, not *accepted*. An area is client-accepted only when it has been run against an actual 1.12.1 (build 5875) client
   ([real-client acceptance](client-acceptance.md)). Do not describe an area as accepted, verified or done on the strength of a build or a test you wrote.
3. **Complete within scope, absent outside it.** Inside the change being made there are no TODOs, stubs, placeholders or "implement later" comments: finish it. What the change does not deliver is written down as a limit
   (an area page's limits section, or the roadmap), not left as dead code.
4. **No invented API surface.** Never fabricate a method signature, packet field, opcode number or DBC column. An unknown detail is a blocker to resolve by reading a reference or asking, never by plausible-looking invention.

The standing direction on top of these: match retail 1.12.1 as closely as possible, mechanics and data, verified against the real references. A deliberate deviation
sits behind a configuration option that **defaults to retail**, and its option summary says what retail does. Server-side observation (scoring, telemetry, diagnostics) needs no gate. Never change the wire format, opcode values or
packet field order in a refactor: pin the packet builders with golden byte tests first.

## Technical baseline

- **Runtime and language:** C# / .NET 11 (`global.json`, `Directory.Build.props`). Nullable is enabled and warnings are errors; match the `.editorconfig` at the repository root.
- **Target protocol:** WoW 1.12.1, client build 5875, and nothing newer. Opcodes, update fields and packet shapes are build-specific; values from later expansions are wrong and silently desync or disconnect the client.
  Pull exact indices and DBC layouts from references and never carry values over from a later core.
- **Databases:** MariaDB is the primary engine; MySQL, PostgreSQL 16 and SQLite are also supported through the provider abstraction (EF Core).
- **Processes:** the logon/realm daemon and the world daemon are separate processes (TCP 3724 and 8085 by default), with an optional client-socket gateway in front of world workers ([gateway](../areas/gateway.md)).
  Components talk through the seams in [integration/seams.md](../integration/seams.md), never by reaching across layers.

## References and citations

The reference projects are **GPL and read-only**. ArcaneCore is GPL-3.0 ([LICENSE](../../LICENSE)) but stays its own implementation by choice: read a reference for behaviour and exact values, re-express the behaviour in your own code,
and never copy its code or data into this repository (not even a table). Cite the file and line next to every formula, table and packet you add. The [repo hygiene guard](#repository-hygiene) enforces the data half of that rule.
Local clones of the references live outside this repository.

| Reference | Use |
|---|---|
| vmangos/core | Primary vanilla behaviour and protocol reference; excellent auth and world socket reference |
| cmangos/mangos-classic | Closest maintained vanilla reference for 1.12.x; cross-check |
| mangoszero/server | Additional vanilla cross-check |
| cmangos/classic-db | World content source for the importer (never committed) |
| gtker/wow_messages, gtker/wow_srp | Machine-readable packet, opcode and update-field definitions and SRP6 known-answer vectors; permissive licence, so the source for generated tables |
| wowdev.wiki | Neutral protocol, opcode and format documentation |
| EmberEmu/Ember | Clean modern C++ vanilla auth daemon |
| WCell | C#/.NET architecture idioms; the auto-create-on-login mechanism |
| MangosSharp, mangosvb | .NET lineage patterns (clustering restoration, ported fixes); design input only |
| WoWWiki archive (patch 1.12.1) and Wowhead Classic | Patch notes, historical mechanics and dated observations; see the source-version policy below |

The full catalogue with client versions, licences and roles (including AscEmu, TrinityCore, ArcEmu, Worldforge and Noggit, which inform design or tooling only) is the
[roadmap reference set](../ROADMAP.md#reference-set-extended-2026-10-02-and-2026-10-04-at-the-developers-request). **WotLK-era references never supply 1.12.1 wire values.**

**Source-version policy for wiki and Wowhead facts.** Identify the patch or expansion, the page id and the date, and name a supporting vanilla reference. Evolving wiki pages and Wowhead comments may include TBC, WotLK, Classic Era,
Hardcore, Season of Discovery or PTR behaviour, and none of that establishes original build-5875 values. Cross-check mechanics against the 5875 client and data or the vanilla references before implementing; page comments alone never
establish protocol fields or authoritative values.

## Retired rules

The project's earlier build charter (`ARCANECORE_CHARTER.md`) was removed on 2026-11-04. Its live rules are the sections above. The rest was retired as obsolete: the strictly gated milestone plan (M1 to M6 and the
`MILESTONE_Mx.md` completion notes, long since relaxed by the owner and replaced by the [roadmap](../ROADMAP.md)), the instruction to ignore earlier reconstructions, the list of what the developer supplied, and the amendment log.
Git history keeps the old text: `git log --follow -- ARCANECORE_CHARTER.md`. The same applies to the retired milestone and acceptance pages (`MILESTONE_*.md`, `docs/M*_ACCEPTANCE.md`); what they recorded that still holds is in
[protocol decisions](../areas/protocol-decisions.md), [real-client acceptance](client-acceptance.md), the [mock client](../areas/mock-client.md) and [quests-npc](../areas/quests-npc.md).

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
green. The main stage of `test-stages.sh` (used by node CI only; the hosted `ci.yml` main steps and `full-suite-gate.ps1` call `dotnet test` directly) does the same for a target with at most `GATE_MAIN_RERUN_MAX` (default 5) plain test failures: each failed test is rerun once alone, a pass is `FLAKY`, a second failure, a timeout, an abort, a kill (any dotnet exit code but 1), a build/setup error or a mass failure fails the gate (`GATE_MAIN_RERUN=off` disables it). A flaky test is still a bug to fix, not a pass: the ledger is the list to work from. Node CI (`tools/ops/ci/node-ci.ps1`) runs the
same two stages with the same script.

### Package lock files

Every project has two committed lock files beside its `.csproj`: `packages.net10.lock.json` and
`packages.net11.lock.json`. `Directory.Build.props` enables locked packages, and `Directory.Build.targets` selects
the file matching the effective `TargetFramework` through `NuGetLockFilePath`.
Each file records exact versions and content hashes of direct and transitive packages. CI (`ci.yml`, `dotnet.yml`), node CI
(`tools/ops/ci/node-ci.ps1`), the local full gate (`tools/ops/ci/full-suite-gate.ps1`), the image build
(`apps/docker/Dockerfile`) and the deploy build (`tools/ops/deploy/build-integrated.sh`) restore with `--locked-mode`, so a restore
whose package graph differs from the lock files fails (NU1004) instead of quietly resolving something new. A test
(`PackageLockFileTests`) checks that every project has one and that every scripted restore is locked.

When you add, remove or bump a `PackageReference` (or a project reference that brings packages with it), refresh the lock files with
the repository SDK and commit every changed `packages.net10.lock.json` in the same commit as the `.csproj` change.
For .NET 11 dependency changes, use the stable .NET 11 SDK and refresh the corresponding
`packages.net11.lock.json` files separately. Do not overwrite one framework's locks with the other:

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
first restore; commit the per-framework snapshots with the project. A new checkout migrated from
unversioned .NET 10 lock files can run `bash scripts/migrate-nuget-lock-files.sh` first;
`bash scripts/build-net10.sh` invokes this migration automatically. Dependabot's NuGet pull requests
should update the affected framework lock files.
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
