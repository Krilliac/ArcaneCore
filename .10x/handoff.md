# Handoff

For the next session or agent that picks up this work. Read [status.md](status.md) first, then the root [CLAUDE.md](../CLAUDE.md).

## Where things stand (2026-10-22)

- **Audit done.** Plan: [reviews/2026-10-22-refactor-audit-plan.md](reviews/2026-10-22-refactor-audit-plan.md). Findings, with file:line
  evidence: [reviews/2026-10-22-refactor-audit-findings.md](reviews/2026-10-22-refactor-audit-findings.md). Finding numbers (`#NNN`)
  in every other file refer to that table.
- **Conventions lane (Wave 0a)** wrote:
  - `CLAUDE.md`
  - `docs/conventions.md` (linked from `docs/README.md`)
  - `.editorconfig` rules: static readonly `PascalCase`, const `PascalCase`, mutable statics unchecked, IDE0011. All at suggestion or none.
  - this `.10x/` state.

  It still needs the build-runner gate: a full Release build (it must stay green with zero `.cs` changes) and World.slnf for the docs
  tests (`DocsLinkTests`, `DocKeyAuditTests`, `ContributingGuideTests`).
- **Bug-fix lane** is running the plan's section 3 list. Fixes that need a decision (#169, #52, #53) wait for one.
- **Refactor waves 0b–5** wait for approval of [ADR 002](adrs/002-refactor-wave-strategy.md).

## Next steps

1. Get the conventions lane through the gate and review, then open its PR (not to `main` directly).
2. When ADR 002 is approved, start Wave 0b: the namespace-edge freeze test in `tests/ArcaneCore.World.Tests/Architecture/`. It
   must fail when a probe edge is injected; prove that before trusting it.
3. Run the Wave 1 items in parallel, one lane per item, with disjoint owner files ([refactor-waves](decisions/architect/refactor-waves.md)).

## Things that will bite

- Grep for old names everywhere except `.claude/worktrees/` (stale copies).
- Renames reach reflection in tests, the `MapUpdateDiagnostics` type-name switch, the `PlayerbotQaLogTap` category strings and FullName-ordered
  discovery. See [docs/conventions.md](../docs/conventions.md), "Before any rename or move".
- A `--no-build` test run against a stale `bin/` reports old results. A test filter that matches 0 tests reports no failures.
- Every markdown file here is link-checked by `DocsLinkTests`, and every backticked configuration key (section, colon, key) by `DocKeyAuditTests`. Keep
  relative links valid when moving files.
- `ContributingGuideTests` forbids `docs/guide/contributing.md` from naming `CLAUDE.md` or `.claude`. Don't link this guide from there.
