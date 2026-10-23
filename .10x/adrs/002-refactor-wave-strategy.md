# ADR 002: Refactor wave strategy

- **Status:** Proposed (waiting for the maintainer's approval)
- **Date:** 2026-10-22
- **Deciders:** architect (proposer), maintainer (approver)
- **Related:** [plan](../reviews/2026-10-22-refactor-audit-plan.md) sections 4 and 5,
  [findings](../reviews/2026-10-22-refactor-audit-findings.md), [refactor waves](../decisions/architect/refactor-waves.md),
  [ADR 001](001-naming-conventions-and-enforcement.md)

## Context

The audit confirmed 166 findings. 25 are bugs and the rest are duplication, architecture and naming. The codebase is a
reference-faithful port with these properties:
- vmangos and cmangos `file:line` citations everywhere
- a frozen wire format (charter section 1)
- seeded-RNG tests that pin draw order
- bit-exact float constants

Large god types are built from partial classes (SpellSystem with 89 partials, CreatureMapSystem with 43, Map, WorldRuntime,
InstanceManager), and reflection, string switches and FullName-ordered discovery reach into them.

The audit's skeptic pass refuted 56 findings, many of them because the proposed change was not behaviour-preserving. The main
risk in this repository is a refactor that compiles and passes tests while it silently changes behaviour. Typical ways that happens:
- a different RNG draw
- `<` changed to `<=`
- a lost null guard
- a registration-order change
- a logger category that no longer matches

## Decision

Run the refactor as ordered waves, smallest risk first, under four rules.

1. **Partials first.** God types are split by moving members between `partial` files of the same type (Wave 3: Map,
   WorldRuntime, InstanceManager, SpellSystem stage 1). These moves do not change state ownership, reset ordering, reflection
   targets or type names. Each move is one commit, so blame-ignore-revs can skip it. **No collaborator classes are extracted.**
2. **Characterisation tests before extracting a helper.** Every Wave 2 helper starts with tests that pin current behaviour at
   each site it will replace:
   - seeded RNG output
   - golden packet bytes (the 42-byte periodic log in 1g)
   - boundary values for inclusive and exclusive range checks
   - per-queue write-queue tests

   A site is substituted only with the variant that matches its current draw, sign, overflow, comparison and null-guard behaviour.
3. **Guards before moves.** Wave 0b's IL-based namespace-edge freeze test lands before any wave touches Spells, Combat,
   Creatures, Pets, Items or Instances, and it must be shown to fail on an injected probe edge.
4. **Deferrals are decisions, not backlog.** The plan's section 5 items are out of scope until they are revisited one cluster at a time with tests:
   - collaborator extraction from the god classes (#17, #85, #57, #211)
   - SpellSystem stage 2 and the Game project split (#6, #9, #29)
   - namespace realignment and suffix renames (#10, #26, #49, #102, #191, #35, #128 and others)
   - the QuestNpcServices split (#18)
   - the Gateway → World split (#184, #148)
   - the pack-copy target (#187)
   - the ThirdParty relocation (#185)
   - the Playerbots god classes (#138, #139)
   - LogonSession extraction (#142)
   - PacketWriter merges (#144, #194)
   - CLI and tool consolidation (#146, #160, #162, #164, #126)
   - the deploy-script restructure (#159, #163)
   - small-primitive merges (#108, #106, #115, #90, #88)
   - fault-boundary helpers (#43, #55)

Each item goes implementer → build-runner (full Release build plus the named `tests/ci/*.slnf`, compared by count) → reviewer, with
at most two review cycles before escalation. Bug fixes (plan section 3) are separate and proceed now.

## Alternatives

| Option | For | Against | Verdict |
|---|---|---|---|
| A. Waves: partials first, characterisation tests first, deferrals explicit | Each PR is small and behaviour-neutral, so a regression is attributable. Parallel lanes on disjoint files. | Slow. The god classes stay large for now. | **Proposed** |
| B. Extract collaborators from the god classes now | The biggest structural win | Splits shared private state. Breaks the reflection targets (`_respawnAt`, `UnloadGrid`, `_procDepth`), the `MapUpdateDiagnostics` string switch and override precedence. High regression risk with no oracle. | Rejected for now |
| C. Big-bang project split (Spells/Creatures/Instances into separate assemblies) | Removes the namespace cycles structurally | Hundreds of files, hot-module public surface, FullName-ordered discovery, InternalsVisibleTo churn | Rejected; Wave 0b freezes the edges instead |
| D. Fix bugs only, no refactor | Zero refactor risk | The copy-paste families keep diverging (rand_dither ×17, distance ×15+, write queues ×7), and fixes have to be made N times | Rejected |

## Consequences

- Positive:
  - Duplication shrinks family by family, with a test oracle for each.
  - File-level navigation of the god types improves.
  - A frozen namespace graph stops new cycles.
- Negative:
  - The god types remain one class each.
  - Some deferred inconsistencies stay visible (namespaces, suffixes).
  - Characterisation tests add code before any is removed.
- Process: Wave 3 commits must be pure moves. Mixing a move with a logic change defeats blame-ignore-revs and review.

## Risks

- **A check that stops checking.** A filter that matches 0 tests, a `--no-build` run against stale output, or a killed run can all read as
  green. Mitigation: per-suite counts compared with base, and the full build in every gate.
- **Hidden string consumers.** A rename or move can miss a reflection, logger-category or tooling string. Mitigation: the rename
  checklist in `docs/conventions.md`, and greps that exclude `.claude/worktrees/`.
- **Generated artefacts.** `docs/ops/exception-audit.md` and `docs/reference/*.md` go stale after moves. Mitigation: regenerate in the same PR.
- **Parallel-lane conflicts.** Mitigation: disjoint owner files per item, and serial order where items share a file.

## Dependencies

- Maintainer approval of this ADR (status → Accepted).
- ADR 001 in place (conventions written, `.editorconfig` at suggestion level).
- Wave 0b's freeze test, shown to fail on a probe edge, before any Spells/Combat/Creatures/Pets/Items/Instances wave.
- Item 2k depends on bug #168. Item 1g depends on its golden-bytes test.
