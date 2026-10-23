# CTO: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md), [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Pending decisions

- Approve or amend [ADR 002](../../adrs/002-refactor-wave-strategy.md), the refactor wave strategy. It is waiting for the maintainer.
- Bug items that need an owner's call:
  - #169: which deploy log lines block a deploy.
  - #52: teleport stall. Correct the doc, or add recovery.
  - #53: Map add/remove fault semantics.

## Observations

- [DISCOVERED] The codebase is disciplined. The xc-bugs summary found:
  - no `async void`
  - three empty catches, all deliberate
  - filtered catches that record to the fault ledger
  - wire readers that check bounds before allocating

  Of the 166 confirmed findings, 25 are bugs, nearly all of them low impact.
- [DISCOVERED] The largest structural debt is god classes and families of copy-pasted helpers, not layering. The project graph is acyclic.
- [DISCOVERED] 56 findings were refuted, and many "obvious" refactors were rejected because of their hazards. The deferred list in plan section 5 is deliberate: the rename and extraction churn there costs more than it returns.
