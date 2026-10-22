# ADR 001: Naming conventions and how they are enforced

- **Status:** Accepted
- **Date:** 2026-10-22
- **Deciders:** staff engineer, architect (refactor audit). Recorded by the conventions lane.
- **Related:** [docs/conventions.md](../../docs/conventions.md), [plan](../reviews/2026-10-22-refactor-audit-plan.md) section 2 and Wave 0a,
  findings #10, #26, #28, #35, #49, #61, #79, #100, #128, #214, #215, #216, #218 ([findings](../reviews/2026-10-22-refactor-audit-findings.md))

## Context

The 2026-10-22 audit measured the naming conventions across about 2,950 `src` files and 2,640 test files. Most conventions are strongly
dominant:
- `_camelCase` fields: 6,393
- `*Store`: 183
- `*Options`: 194
- `Async` suffix: 1,926 vs 28
- `Subject_Behaviour` test methods: 16,864

A minority of concerns are split:
- Private static readonly fields: PascalCase 447 vs `s_` 188.
- AI suffix: `AI` 259 vs `Ai` 70.
- Namespace vs folder: about 80% match.
- About 160 brace-less bodies remain.

`.editorconfig` declared `_camelCase` for all private fields, which contradicted the dominant static style. No document stated the
standard, so finder agents proposed renames in opposite directions (#13, #62 and #91 were refuted for getting the majority wrong).

Constraints:
- `Directory.Build.props` sets `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`, so any code-style rule at `warning` or
  above breaks the build for every file that does not yet comply.
- Some names are load-bearing. Reflection in tests, the `MapUpdateDiagnostics` type-name switch, the `PlayerbotQaLogTap` category
  strings, FullName-ordered discovery, generated docs and the schema golden all depend on them.

## Decision

1. Adopt the plan's section 2 table as the written standard in `docs/conventions.md`. It is the measured majority, with each
   exception named and its reason given.
2. New and changed code follows the standard. Existing code is **not** mass-renamed. The deferred renames are listed with their hazards.
3. Encode the mechanically checkable rules in `.editorconfig` at **suggestion** severity:
   - private static readonly → PascalCase (more required modifiers than the field rule, so it wins)
   - private const → PascalCase
   - private mutable static → `none`, because a naming rule cannot select `[ThreadStatic]`, and `t_`/`s_` would otherwise be asked for `_camelCase`
   - `dotnet_diagnostic.IDE0011.severity = suggestion`

   Existing rules and severities stay unchanged.
4. Raise a rule later only by the ratchet: clean the tree for that rule, then raise it to `warning` in the same PR. A naming
   rule's own severity is respected only in IDEs, not during build, so a naming rule is ratcheted only by also raising
   `dotnet_diagnostic.IDE1006.severity`. `IDE1006` is shared by every naming rule, so raising it enforces all of them at once,
   including `private_fields_underscore` (stray `patches` in `src/ArcaneCore.Realm/Net/LogonServer.cs`). Only `IDE0011` is
   enforced by its own `dotnet_diagnostic` line, so only for it does a green Release build prove the tree is clean.
5. `CLAUDE.md` at the repository root carries the short form for agents.

## Alternatives

| Option | For | Against | Verdict |
|---|---|---|---|
| A. Hard-enforce now (`warning` / IDE1006 raised) | Drift stops immediately | Hundreds of existing violations break the build at once. The fix-up commit is a huge rename that touches load-bearing names. | Rejected |
| B. Suggestion now, ratchet one rule at a time | Zero build risk and IDE guidance today. Each raise is a small, reviewable, behaviour-neutral PR. | Drift is only advised against, not blocked, until a rule is ratcheted | **Chosen** |
| C. Mass-rename to the standard, then enforce | Consistent tree at the end | String and reflection consumers break silently. Discovery order changes. The schema golden and tooling strings churn. Blame history is lost. Low value for the risk (plan section 5). | Rejected |
| D. Document only, no `.editorconfig` change | No config churn | The existing `_camelCase` rule keeps contradicting the PascalCase static majority in the IDE | Rejected |

## Consequences

- Positive:
  - One written standard, with the reasons for each exception.
  - The IDE now suggests the dominant static style instead of the minority one.
  - The build is unchanged: a full build must stay green with zero `.cs` diff.
- Negative:
  - Existing inconsistencies (`s_` statics, `Ai` scripts, brace-less bodies) remain until a ratchet lane cleans them.
  - Mutable statics are not checked at all.
- Neutral: the namespace-vs-folder rule (IDE0130) is documented but not enforced.

## Risks

- **Rule ranking.** The analyzer ranks naming rules by specificity, and file order does not matter. If a toolchain ranks
  differently, the effect is limited to which suggestion appears, because every new rule is suggestion or none. Verify in the IDE
  on a sample `s_` static readonly field.
- **Silent enforcement drift.** Naming severities are IDE-only unless IDE1006 is raised. A future lane could assume the build enforces
  naming when it does not. `docs/conventions.md` says so explicitly.
- **Docs tests.** `docs/conventions.md` and `CLAUDE.md` are link-checked (`DocsLinkTests`) and key-checked (`DocKeyAuditTests`), so a
  stale path or a backticked configuration key that does not exist fails World.slnf.

## Dependencies

- The build-runner's full Release build must be green with no `.cs` changes, and World.slnf must pass for the docs tests.
- Ratchet lanes depend on Wave 0b (namespace-edge freeze) before any namespace move. See [ADR 002](002-refactor-wave-strategy.md).
