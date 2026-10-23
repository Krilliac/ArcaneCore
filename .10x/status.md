# Team status

Updated 2026-10-22. Base: `origin/main` 8700e2f72.

## Phase

| Track | State |
|---|---|
| Discovery: refactor audit | **Complete.** 229 raw findings, 222 after dedup, 166 confirmed, 56 refuted ([findings](reviews/2026-10-22-refactor-audit-findings.md)). The architect's plan is in [reviews/2026-10-22-refactor-audit-plan.md](reviews/2026-10-22-refactor-audit-plan.md). Everything was found by static reading; nothing was built during the audit. |
| Conventions (Wave 0a) | **In review.** `CLAUDE.md`, `docs/conventions.md` and the suggestion-level `.editorconfig` rules are written. They are waiting on the build-runner's full Release build and the docs tests (`DocsLinkTests`, `DocKeyAuditTests`). |
| Bug-fix lane (plan section 3) | **In progress.** Each fix goes reproducing test first, then fix, then the named slnf, then review. Four items need an operator or design decision first: #169 (blocking log lines), #52 (teleport stall recovery), #53 (Map add/remove exception safety) and #15 (per-source catch, deferred part). |
| Refactor waves 0b–5 (plan section 4) | **Awaiting the maintainer's approval.** The strategy is in the plan, section 4. No wave starts before approval. |

## Gates every lane uses

The local merge gate is in [CLAUDE.md](../CLAUDE.md): locked restore, Release build, the four `tests/ci/*.slnf` suites with `-m:1`, and the MockClient self-test, all run through `tools/ops/ci/run-in-slot.ps1`. Report per-suite test counts compared with base, not exit codes.

## Open decisions

| Decision | Owner | Blocking |
|---|---|---|
| Approve the wave strategy and order | maintainer | Waves 0b–5 |
| #169: which world-log lines block a deploy | operator | deploy-script fix |
| #52: correct the doc (report-only) or add recovery | maintainer | TeleportService fix |
| #53: roll back or continue on an updater fault; does it count toward the breaker | maintainer | Map add/remove fix |
