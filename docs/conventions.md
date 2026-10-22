# Naming and style conventions

The naming standard for ArcaneCore source. It was derived from the conventions the code already follows most often, measured
by the 2026-10-22 refactor audit ([plan](../.10x/reviews/2026-10-22-refactor-audit-plan.md) section 2,
[findings](../.10x/reviews/2026-10-22-refactor-audit-findings.md)). The counts below come from that audit's static census of
`src/` (and `tests/` for test names). They are a snapshot, not a test result.

In short, **new and changed code
follows this page**. Existing code is not mass-renamed. Where a rule is encoded in `.editorconfig`, it runs at `suggestion`
severity, because `TreatWarningsAsErrors` (`Directory.Build.props`) would turn anything stronger into a build break for every
file that does not comply yet.

Formatting basics are in `.editorconfig`: file-scoped namespaces, `using` directives outside the namespace, explicit
accessibility modifiers, LF line endings and 4-space indent. Nullable is enabled everywhere.

## The standard

| Concern | Standard | Measured | Exceptions to keep, and why |
|---|---|---|---|
| Private instance fields | `_camelCase` | 6,393 | None. The one stray, `patches` in `src/ArcaneCore.Realm/Net/LogonServer.cs`, is a fix to make when that file is next touched (#216). |
| Private static readonly fields | `PascalCase` | 447 Pascal vs 188 `s_` (#216) | `[ThreadStatic]` fields use `t_camelCase`. Other mutable (non-readonly) statics use `s_camelCase`. Constants are `PascalCase`. |
| Persistence seam | `I*Store` contract in Kernel, `Ef*Store` implementation in Data | 183 `*Store` (80 `Ef*Store`), 0 `*Repository` | A handful of stores live outside those homes (#212). Leave them until their area is otherwise touched. |
| Bound configuration | `*Options` with a `SectionName` constant | 194 vs about 10 `*Settings`/`*Config` | The `*Settings`/`*Config` types that are not bound options (mutable runtime state) keep their names (refuted #217). |
| Schema plugin | `*DataModule` (`IDataModule`) | 107 vs 10 outliers (#128) | The `*WorldModule`/`*CharactersModule` outliers are **not** renamed (see [Deferred renames](#deferred-renames)). |
| Opcode handler classes | `*Handlers` (static) | 58 | The refactor audit counted 17 further types named `*Handler`. Most are not opcode handlers (event, crash and metadata handlers). The opcode ones are `ItemQueryHandler`, `KnockbackAckHandler`, `RootAckHandler` and `TurnRateAckHandler`. New opcode handler groups use `*Handlers`. |
| World feature root | `*Feature` (`IWorldFeature`) | 162 | None. |
| GM command group | `*Commands` (`ICommandGroup`) | 92 | None. |
| Content reload hook | Leaf `*ContentReloadable`. Abstract base `*Reloadable` | about 30 / 8 (#100) | The abstract bases keep the bare `*Reloadable` suffix. |
| Creature and boss AI types | `AI` suffix (`OnyxiaAI`) | 259 `AI` vs 70 `Ai` (#35) | Infrastructure keeps `Ai`: `CreatureAiServices`, the `EventAi`/`SmartAi` folders and types, and the configuration section `Creatures:EventAi`. The casing follows the role, not chance (refuted #27, #78). Existing dungeon `*Ai` scripts are not renamed. |
| Spell scripts | `*Script` only for an `ISpellScript` | #12 | Don't introduce `ProcScript` as a suffix, because it collides with existing names. Multi-type bundles (`*Scripts.cs`) stay. |
| Orchestrators | `Manager` owns a collection of entities. `Service` is a rule engine over them | Service 52, Manager 17 (#79) | Documentation only. No mass rename. |
| Map-attached updaters | `<Subject>MapSystem` for a stateful `IMapUpdater`, `<Subject>Updater` for a thin adapter | four schemes today (#61) | Documentation only. Updater type names are matched as strings by `MapUpdateDiagnostics.SubsystemOf` (`src/ArcaneCore.Game/Maps/MapUpdateDiagnostics.cs`), so a rename changes slow-tick attribution silently. |
| Async methods | `Async` suffix on `Task`/`ValueTask` methods | 1,926 vs 28 outliers (#218) | The verb table in `src/ArcaneCore.Playerbots/Control/BotControlVerbs.cs`, plus the adapters and pipeline steps in `src/ArcaneCore.Kernel/Resilience` (`ValueTaskAdapters.ToBool`, the private `Run*` steps of `Policy.cs`). |
| Abbreviations | `Id`, `Guid`, `Npc` | 9,336 / 5,045 / 4,009 identifiers | Wire-field names quoted from a reference in comments keep the reference spelling. |
| Namespace vs folder | Namespace matches the folder path | about 80% of `src` files (#214) | See [Namespace collapse](#namespace-collapse). |
| Files | One primary public type per file, file name = type name. Partials are `Owner.Feature.cs` (`SpellSystem.Auras.cs`) | 41 single-type mismatches (#215, #11) | Multi-type script bundles (`*Scripts.cs`, `Actions`/`Events` folders) and DTO/contract files hold several types by design. |
| Tests | Class `*Tests`. Method `Subject_Behaviour`. Static helpers `<Area>TestKit`. xUnit fixtures `<Area>Fixture` | 2,512 classes, 16,864 of 18,290 methods (#180) | Hot-module fixture projects keep `Fixture.cs`. Zone-script `Batch<N>` test names stay, because docs link to them (#181). |
| Braces | Braces on every `if`/`else`/loop body | about 160 brace-less bodies (#28) | None, but enforced only as a suggestion (IDE0011). |

## Namespace collapse

The namespace follows the folder, with two documented exceptions.

1. **Feature leaf folders may collapse into the feature namespace.** For example, `Spells/Auras`, `Spells/Stances` and
   most of `Spells/Effects` are in `ArcaneCore.Game.Spells`. In `Spells/Procs`, only the five `SpellSystem` partials collapse.
   `Creatures/AI`, `Movement`, `Guards`, `Respawn`, `EventAi` and
   `SmartAi` are in `ArcaneCore.Game.Creatures`. `Maps/Parallel` and the Locomotion subfolders work the same way. Reasons:
   - `SpellSystem` is one `partial` class spread over about 89 files in several folders. Every partial must declare the same
     namespace, so those folders cannot each get their own.
   - Module, handler, EventAI and script discovery is ordered by type `FullName` (for example `DefaultMapUpdaters.cs`,
     `SpellScriptRegistry.cs`, `EventAiRegistry.cs`, `WorldFeatures.cs`, `CodeAssemblies.cs`). Moving a type to a new namespace
     changes registration and execution order.
   - Playerbots QA matches logger categories by string (`QaCategoryPrefix` and `CreatureCategory` in
     `src/ArcaneCore.Playerbots/Qa/PlayerbotQaLogTap.cs`). A namespace move there stops matching without failing anything.

   Some folders already have their own namespace and must keep it: `ArcaneCore.Game.Spells.Procs` and
   `ArcaneCore.Game.Spells.Procs.Talents` (the non-partial files in `Spells/Procs`), `ArcaneCore.Game.Spells.Effects`
   (`ExtraAttackEffects.cs`) and `ArcaneCore.Game.Combat.Threat`. Do not "fix" them to match the collapsed folders.

   A **new** subfolder should get its own namespace unless it holds partials of a type that lives elsewhere.
2. **`Console` folders use the namespace `ServerConsole`** (`src/ArcaneCore.Kernel/Ops/Console` declares
   `ArcaneCore.Kernel.Ops.ServerConsole`, and Realm and World do the same). A `.Console` namespace segment would shadow
   `System.Console` for every file under it. The same applies to test projects.

Two more namespace facts are deliberate and documented in their own files:
- The Playerbots assembly uses the root namespace `ArcaneCore.World.Playerbots` (`RootNamespace` in
  `src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj`), so code moved out of World kept its names.
- DI extension classes declare `Microsoft.Extensions.DependencyInjection`.

IDE0130 (namespace does not match folder) is **not** turned on in `.editorconfig`. It cannot express the collapse rule without
one section per collapsed folder. That is a possible later ratchet.

## What `.editorconfig` encodes

| Rule | Where | Severity |
|---|---|---|
| Private fields `_camelCase` | `private_fields_underscore` | suggestion (unchanged) |
| Private static readonly `PascalCase` | `private_static_readonly_pascal` (required modifiers `static, readonly`, so it outranks the field rule) | suggestion |
| Private constants `PascalCase` | `private_constants_pascal` | suggestion |
| Private mutable statics (`s_`, `t_`) | `private_static_fields_unchecked`. A naming rule cannot see `[ThreadStatic]`, so these are not checked | none |
| Braces | `csharp_prefer_braces` and `dotnet_diagnostic.IDE0011.severity` | suggestion |

Naming-rule order in the file does not matter. The analyzer applies the most specific symbol group (more required modifiers
wins). Naming severities are IDE-only unless `IDE1006` is raised, which this repository does not do.

**Raising a severity.** Fix the whole tree for that rule first, in its own behaviour-neutral commit. Then raise it to `warning`
in the same PR. Never raise a rule ahead of the cleanup.

A naming rule is **not** enforced by the build when only its own `severity` is raised. Microsoft documents that a naming rule's
severity is respected only inside IDEs, not during build. A naming rule is ratcheted only by also raising
`dotnet_diagnostic.IDE1006.severity` (to `warning` or `error`). Without that line a green Release build proves nothing.
`IDE1006` is one ID shared by every naming rule, so raising it puts all of them in the build at once, including
`private_fields_underscore`, which still has a stray (`patches` in `src/ArcaneCore.Realm/Net/LogonServer.cs`). Clean every naming
rule's violations before raising `IDE1006`. Only `IDE0011` (braces) is enforced by its own `dotnet_diagnostic` line, under
`EnforceCodeStyleInBuild`; for it, the full Release build does prove the tree is clean.

## Deferred renames

These inconsistencies are known and are deliberately **not** being fixed by renaming. Each rename has a hazard that silently
changes behaviour or breaks tooling, and the cost outweighs the value
([plan](../.10x/reviews/2026-10-22-refactor-audit-plan.md) section 5).

| Finding | Not renamed | Why |
|---|---|---|
| #35 | `*Ai` creature scripts to `*AI` | Script and display names reach `ScriptCatalog` (`src/ArcaneCore.Game/Creatures/AI/ScriptCatalog.cs`) and tooling that names script files (`tools/ops/llm/fidelity_review.py`). |
| #128 | `*WorldModule`/`*CharactersModule` to `*DataModule` | Module order is by `FullName`, and `docs/reference/schema.md` is a generated golden page that names the modules. |
| #61, #79, #100, #12, #166, #167 | Suffix families (updaters, Manager/Service, Reloadable, Script, Atlas `Ref`/`Reference`, tool project names) | `MapUpdateDiagnostics` string switch, FullName-ordered discovery, docs and scripts that name the types. |
| #10, #26, #49, #102, #191 | Namespace realignment (Spells, Creatures, Locomotion, Contracts, Playerbots) | `SpellSystem` partials pin the namespace, discovery order, the QA log-tap category strings. Covered by the collapse exception above. |
| #181 | `ZoneScriptBatch<N>Tests` | Docs link to the batch numbers. |

A deferred rename can be revisited one cluster at a time, with characterisation tests and the string consumers above
updated in the same commit.

## Before any rename or move

- Grep `src`, `tests`, `tools`, `docs`, `scripts` and `.github` for the old name **and** its namespace-qualified and path forms.
  Exclude `.claude/worktrees/` if it exists, because it holds stale copies of the tree.
- Check reflection in tests (`GetField("_name"`, `GetMethod("Name"`, `BindingFlags.NonPublic`), `nameof` and `GetType().Name`
  consumers, and logger categories.
- A `<see cref>` to the old name is a CS1574 build error. A member that hides an inherited one is CS0108. Both fail the build under
  warnings-as-errors.
- Paths are case-sensitive on the Linux CI runner. `CodeDocReferenceTests` rejects a `docs/` path whose case is wrong.
