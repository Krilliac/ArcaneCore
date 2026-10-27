# CLAUDE.md: working in ArcaneCore

This is the operating guide for agents and humans changing this repository. It is a summary with pointers. Where it and a linked
document disagree, the linked document wins: [charter](ARCANECORE_CHARTER.md), [contributing](docs/guide/contributing.md),
[conventions](docs/conventions.md).

## What this is

ArcaneCore is a from-scratch World of Warcraft **1.12.1 (client build 5875)** server emulator in **C# / .NET 11**
(`global.json`, `Directory.Build.props`). It ships two daemons, logon/realm (`ArcaneCore.Realm`) and world (`ArcaneCore.World`),
plus an optional client-socket Gateway, a playerbot pack, and tools. Its databases are SQLite, MariaDB and PostgreSQL through EF Core.

### Charter prime directives (summary of [ARCANECORE_CHARTER.md](ARCANECORE_CHARTER.md) section 1)

1. **Ground truth over generation.** Verify every protocol detail (opcode, struct layout, field order, hash) against a real
   reference (charter section 4: vmangos, cmangos/mangos-classic, wow_messages, ...) and cite it in a comment.
2. **Incremental and gated.** Milestone gating was relaxed after M4 (charter section 9). Scope and order are in `docs/ROADMAP.md`.
3. **Verified against a real client.** Automated tests make an area *implemented*, not *accepted*.
4. **Complete within scope, absent outside it.** No TODOs, stubs or placeholders in delivered scope. Write down what is not delivered as a limit.
5. **No invented API surface.** Never guess a method, packet field, opcode or DBC column. Read a reference or ask.

Hard rules that follow from these:
- **Never change the wire format, opcode values or packet field order** in a refactor. Packet builders that "look the same" are
  pinned by golden byte tests first.
- Match retail 1.12.1. A deliberate deviation sits behind an option that **defaults to retail**.
- Reference projects are GPL and read-only. Re-express their behaviour; never copy their code or data.

## Project graph

```
Kernel ── Cryptography, Protocol, Data            (each references Kernel only)
Game        → Kernel, Protocol
World.Contracts → Game
World (Exe) → Kernel, Cryptography, Data, Protocol, Game, World.Contracts
Playerbots  → World, World.Contracts, Game, Kernel, Protocol, Data   (pack; copied beside World, loaded at runtime)
Realm (Exe) → Kernel, Cryptography, Data
Gateway (Exe) → Kernel, Cryptography, Data, Protocol, World          (known smell, audit #184/#148)
```

**Allowed directions:** downward only, in the layer order Kernel < {Cryptography, Protocol, Data} < Game < World.Contracts < World <
Playerbots. Never add an edge upward or sideways (for example Game → Data, Data → Game, Protocol → Game, World → Playerbots). Kernel
holds contracts (`I*Store`), so Game does not need Data. Inside Game there are namespace-level cycles (Spells ↔ Combat, Crafting, Creatures, Death, Entities, GameObjects, Honor, Instances, Items, Locomotion, Maps, Pets, Progression, Ranged, Reputation, Skills, Stats, Totems).
Don't add new ones. Shipped tools still reference `tests/ThirdParty` (Atlas, Sniff). Don't add more of those.
Full graph: [audit findings](.10x/reviews/2026-10-22-refactor-audit-findings.md), "PROJECT REFERENCE GRAPH".

## Where things live

| You need | Put it in | Seam / naming |
|---|---|---|
| A storage contract | `src/ArcaneCore.Kernel/<Feature>/I*Store.cs` | `I*Store`. Immutable content is `*Catalog`. |
| Its EF implementation | `src/ArcaneCore.Data/<Feature>/Ef*Store.cs` | `Ef*Store` |
| Tables / schema steps | `src/ArcaneCore.Data/<Feature>/*DataModule.cs` | `IDataModule` (see [SQL update files](#sql-update-files)) |
| Game rules / simulation | `src/ArcaneCore.Game/<Feature>/` | per-map `IMapUpdater`; partials `Owner.Feature.cs` |
| A world service with lifecycle | `src/ArcaneCore.World/<Feature>/*Feature.cs` | `IWorldFeature` |
| Opcode handlers | `src/ArcaneCore.World/<Feature>/*Handlers.cs` | `IOpcodeHandlerGroup` |
| GM / chat commands | `src/ArcaneCore.World/<Feature>/*Commands.cs` | `ICommandGroup`, `ICommandExtension` |
| Bound configuration | next to its feature | `*Options` with a `SectionName` constant |
| Bot behaviour | `src/ArcaneCore.Playerbots/` (namespace `ArcaneCore.World.Playerbots`) | — |

The full seam table is [docs/integration/seams.md](docs/integration/seams.md). Discovery is reflection over an assembly, **ordered by
type `FullName`** (see `src/ArcaneCore.World/Features/WorldFeatures.cs`, `src/ArcaneCore.Game/Maps/DefaultMapUpdaters.cs`,
`src/ArcaneCore.Kernel/Reflection/CodeAssemblies.cs`). Prefer new files and narrow, additive edits to shared god types (`SpellSystem`,
`CreatureMapSystem`, `QuestNpcServices`, `Map`, `WorldRuntime`, `WorldSession`).

## Naming (short form; full standard in [docs/conventions.md](docs/conventions.md))

- Private fields `_camelCase`. Private static readonly and constants `PascalCase`. `[ThreadStatic]` fields `t_camelCase`.
- Role suffixes: `*Store`/`Ef*Store`, `*Options`, `*DataModule`, `*Handlers`, `*Feature`, `*Commands`, `*ContentReloadable`.
- Creature/boss AI types end in `AI`. Infrastructure keeps `Ai` (`EventAi`, `SmartAi`, `CreatureAiServices`).
- `Async` suffix on Task/ValueTask methods. `Id`, `Guid`, `Npc` casing.
- Namespace = folder, except that feature leaf folders may collapse into the feature namespace, and `Console` folders use `ServerConsole`.
- Tests: `*Tests` classes, `Subject_Behaviour` methods, `<Area>TestKit` helpers.
- Don't mass-rename existing code. `.editorconfig` encodes these rules at **suggestion** only.

## Error-handling idiom

```csharp
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    _logger.LogFault("movement.observer-before", ex, "...", args);      // dotted site: throttled log line AND one fault-ledger record (FaultLedgerLogging)
}
```

`LogFault` already records the fault to the ledger once (`src/ArcaneCore.Kernel/Diagnostics/FaultLedger.cs`, example in
`src/ArcaneCore.Game/Locomotion/ClientMovementObservers.cs`), so **don't pair it with `FaultLedger.Shared.Record` for the same site**: that double-counts. Add a separate
`Record` only for a deliberately broader or narrower second key (the playerbot battleground coordinator is the example). Site names are dotted.
Cancellation is not a fault: skip `OperationCanceledException` before recording where a token is in play. Don't add empty catches. Policy and
the catch census are in [docs/ops/exception-audit.md](docs/ops/exception-audit.md).

## Pitfalls (each of these has bitten a lane)

**Renames and moves**
- **Reflection in tests** reaches private members by string, for example `GetField("_procDepth")` in
  `tests/ArcaneCore.Game.Tests/Spells/SpellAnomalyTests.cs` and `GetMethod("UnloadGrid")` in
  `tests/ArcaneCore.World.Tests/Playerbots/Groups/PlayerbotSiegeWorldTests.cs`. Grep for `BindingFlags.NonPublic`.
- **String lookups**: `MapUpdateDiagnostics.SubsystemOf` switches on updater type names
  (`src/ArcaneCore.Game/Maps/MapUpdateDiagnostics.cs`). `PlayerbotQaLogTap` matches logger category strings
  (`src/ArcaneCore.Playerbots/Qa/PlayerbotQaLogTap.cs`). A rename stops the match silently.
- **FullName-ordered discovery**: moving a module, handler, updater, EventAI or spell-script type to another namespace reorders
  registration and execution.
- **SQL / EF mappings**: table and column names come from the EF model. A renamed property can change the schema, and released
  `sql/updates` files are frozen.
- **Hot-module public surface**: hot-loaded modules (`tests/hotmodule-fixtures`, `tools/hotmodule-template`) compile against
  World/Game public types (for example `InstanceData`). Treat those as API.
- **Compiler traps under warnings-as-errors**: a stale `<see cref>` is CS1574. A new member that hides an inherited one is CS0108
  (for example, adding `WorldObject.DistanceTo` collides with `GameObject.DistanceTo`). An `[Obsolete]` forwarder is CS0618 at every caller.
- **`.claude/worktrees/`**, where present, holds stale copies of the tree. Exclude it from every grep and count.

**Behaviour preservation**
- **Seeded RNG draw order**: many tests seed the RNG. A helper that skips, adds or reorders a draw changes results. Keep
  inclusive and exclusive bounds as they are at each call site.
- **Float operation order**: keep the addition and multiplication order and the exact literals
  (`0.388999998569489f` is bit-exact). Keep squared comparisons squared, and keep `<` versus `<=` per site.

**Generated files**
- Generated docs: `docs/reference/*.md` (regenerate with `ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`).
  `docs/ops/exception-audit.md` comes from `scripts/exception-audit/audit.py`. `sql/updates` comes from the export (below). Regenerate; never hand-edit.
- Every markdown file outside `bin`/`obj`/`.git`/`.claude` is link-checked (`DocsLinkTests`), and every backticked config key in
  docs must exist (`DocKeyAuditTests`).

**Builds and platforms**
- **Stale DLLs with `--no-build`**: tests run whatever sits in `bin/<Configuration>`. A build made with a platform switch
  (output under `bin/x64/Debug`) leaves `bin/Debug` stale, and `--no-build` then tests old code. Build and test with the same `-c`
  and no `-p:Platform`.
- **Linux CI**: paths are case-sensitive (`CodeDocReferenceTests` rejects a wrong-case `docs/` path). `sql/updates/**` is forced to LF
  (`.gitattributes`). `.editorconfig` says LF everywhere. Don't hard-code `\` path separators.

## Build and test: the local merge gate

Run each step through the build-slot gate, which caps concurrent builds and waits for memory:

```
pwsh -NoProfile -File tools/ops/ci/run-in-slot.ps1 -Lane <name> -WorkDir <worktree> -Command '<command>'
```

The gate needs `ARCANECORE_LANES_ROOT` (or `-LanesRoot`). The commands mirror `.github/workflows/ci.yml`:

```
dotnet restore ArcaneCore.slnx --locked-mode
dotnet build ArcaneCore.slnx -c Release --no-restore
dotnet test tests/ci/Game.slnf  -c Release --no-build -m:1 --logger trx --results-directory TestResults
dotnet test tests/ci/World.slnf -c Release --no-build -m:1 --logger trx --results-directory TestResults
dotnet test tests/ci/Data.slnf  -c Release --no-build -m:1 --logger trx --results-directory TestResults
dotnet test tests/ci/Rest.slnf  -c Release --no-build -m:1 --logger trx --results-directory TestResults
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

- `Rest.slnf` = Realm, MockClient, Kernel, Cryptography, Gateway and Atlas tests. The four filters cover every test project once.
- `tools/ops/ci/full-suite-gate.ps1` is the one-shot alternative. It exits 2 when restore or build fails, so nothing ran.
- **Check counts, not exit codes.** Read the passed/failed/skipped totals per suite and compare them with the base branch. A
  killed run, a filter that matched 0 tests, or a build that never ran can all exit 0.
- MariaDB/PostgreSQL provider tests run only on hosted CI. Locally, only SQLite runs. Say so when a store change was only exercised on SQLite.
- A package change refreshes every affected `packages.lock.json` in the same commit ([contributing](docs/guide/contributing.md)).

## SQL update files

Every schema change ships per-provider SQL in the same PR. The files are generated, not hand-written:

```
ARCANECORE_UPDATE_SQL=1 dotnet test tests/ArcaneCore.Data.Tests --filter SqlUpdateGoldenTests
```

This writes `sql/updates/<auth|characters|world>/<sqlite|mariadb|postgresql>/NNNN_<Module>.sql` and updates `schema-snapshot.json`.
Each file is guarded: it refuses to run unless the database is at exactly the previous version, so a re-run fails before it changes
anything. Baselines (`base_vNNNN.sql`) install on a clean database. Released files are frozen. Renumber at merge as described in
[sql/updates/README.md](sql/updates/README.md). Content shipped as rows goes in `sql/content/<component>/<provider>/NNNN_<name>.sql`.
Content files must be **idempotent** (they delete their own id range, then insert) and install on a clean database after their schema step
([sql/content/README.md](sql/content/README.md)).

## Tooling and repository hygiene

- Operator scripts live in `tools/ops/<area>/` (`ci`, `deploy`, `diag`, `live`, `llm`, `quality`, `rsi`). Every host-specific location
  comes from an `ARCANECORE_*` environment variable (`tools/ops/OpsEnv.ps1`, `tools/ops/opsenv.py`) or a command-line option
  ([docs/ops/tooling.md](docs/ops/tooling.md)).
- **The repository is public.** Never commit secrets, tokens, host-specific absolute paths, user names, or live player or character
  names. Client data (`.dbc`, `.mpq`, extracted maps) and SQL dumps are rejected by the hygiene test
  (`tests/ArcaneCore.Game.Tests/Hygiene`).
- **Never commit to `main`.** Work on a `claude/<topic>` branch or worktree, and land changes through a PR with green CI.

## Current state

Team state and decisions are in `.10x/` (start at [.10x/status.md](.10x/status.md)). The 2026-10-22 refactor audit
([plan](.10x/reviews/2026-10-22-refactor-audit-plan.md), [findings](.10x/reviews/2026-10-22-refactor-audit-findings.md)) lists the
known bugs, the refactor waves (awaiting approval) and what is deliberately deferred. Read the relevant section before refactoring a hot spot.
